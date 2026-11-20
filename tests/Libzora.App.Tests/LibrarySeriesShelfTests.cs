using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Libzora.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task SeriesStackingCollapsesShelfAndPromotesVolumes() => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = "zh-CN",
            OnboardingCompleted = true,
            NetworkEnabled = false,
            AutoUpdateCheckEnabled = false,
            AutoConnectDevice = false,
            GridGalleryDisplay = false,
            StackBookSeries = true
        });

        await InsertBookAsync(scope.Paths.Database, "第二部", 2);
        await InsertBookAsync(scope.Paths.Database, "第一部", 1);
        await InsertBookAsync(scope.Paths.Database, "第三部", 3);
        await scope.Window.ViewModel.RefreshViewAsync();
        await Render();

        var viewModel = scope.Window.ViewModel;
        Assert.Equal(3, viewModel.Books.Count);
        var stack = Assert.Single(viewModel.ShelfBooks);
        Assert.True(stack.IsSeriesStack);
        Assert.Equal(3, stack.SeriesCount);
        Assert.Equal("测试系列", stack.DisplayTitle);
        Assert.Equal("第一部", stack.Title);
        Assert.Equal(["第一部", "第二部", "第三部"], stack.SeriesMembers.Select(member => member.Title));
        Assert.Same(viewModel.ShelfBooks, scope.Get<ListBox>("BookGrid").ItemsSource);

        var second = stack.SeriesMembers.Single(member => member.Title == "第二部");
        viewModel.PromoteSeriesVolume(stack, second);
        await Render();
        var promoted = Assert.Single(viewModel.ShelfBooks);
        Assert.Same(second, promoted);
        Assert.Equal(["第二部", "第一部", "第三部"], promoted.SeriesMembers.Select(member => member.Title));

        scope.Get<ToggleSwitch>("StackBookSeriesCheck").IsChecked = false;
        await Render();
        Assert.Equal(3, viewModel.ShelfBooks.Count);

        scope.Get<ToggleSwitch>("StackBookSeriesCheck").IsChecked = true;
        await Render();
        Assert.Single(viewModel.ShelfBooks);

        var deviceCards = new[]
        {
            new KindleBookCardViewModel(new KindleBook { RelativePath = "docs/sea-1.epub", Title = "星海 第1卷", Authors = "甲" }),
            new KindleBookCardViewModel(new KindleBook { RelativePath = "docs/sea-2.epub", Title = "星海 第2卷", Authors = "甲" }),
            new KindleBookCardViewModel(new KindleBook { RelativePath = "docs/sea-3.epub", Title = "星海 第3卷", Authors = "甲" })
        };
        foreach (var card in deviceCards)
            scope.Window.DeviceBooks.Add(card);
        InvokeApplyDeviceBookFilter(scope.Window);
        await Render();

        var deviceStack = Assert.Single(scope.Window.VisibleDeviceShelfBooks);
        Assert.True(deviceStack.IsSeriesStack);
        Assert.Equal(3, deviceStack.SeriesCount);
        Assert.Same(scope.Get<ItemsControl>("DeviceBookGridItems").ItemsSource,
            scope.Window.VisibleDeviceShelfBooks);

        var deviceSecond = deviceStack.SeriesMembers.Single(member => member.Title == "星海 第2卷");
        var promotion = (Task)scope.Call("PromoteDeviceSeriesVolume", deviceStack, deviceSecond)!;
        await promotion;
        await Render();
        var promotedDevice = Assert.Single(scope.Window.VisibleDeviceShelfBooks);
        Assert.Same(deviceSecond, promotedDevice);
        Assert.Equal("星海 第2卷", promotedDevice.Title);
    });

    [Fact]
    public Task SeriesBadgeKeepsContrastOnHoverAndSendMenusOfferVolumeAndAllChoices() => Run(async () =>
    {
        var sentPaths = new List<string>();
        await using var scope = await TestWindow.Create(
            new AppSettings
            {
                UiLanguage = "zh-CN",
                OnboardingCompleted = true,
                NetworkEnabled = false,
                AutoUpdateCheckEnabled = false,
                AutoConnectDevice = false,
                GridGalleryDisplay = false,
                StackBookSeries = true,
                SendToKindleWebEnabled = true
            });
        var kindle = new ReaderService
        {
            Devices = [Reader(scope, "SERIES-SEND", ReaderDeviceProfiles.Kobo)],
            SendHandler = file =>
            {
                sentPaths.Add(Path.GetFileName(file.RelativePath));
                return Task.CompletedTask;
            }
        };
        await UseReaderServiceAsync(scope, kindle);
        await scope.Call<Task>("RefreshDevicesAsync", false, CancellationToken.None);

        var file1 = Path.Combine(scope.Paths.Library, "series-vol-1.epub");
        var file2 = Path.Combine(scope.Paths.Library, "series-vol-2.epub");
        var file3 = Path.Combine(scope.Paths.Library, "series-vol-3.epub");
        await File.WriteAllTextAsync(file1, "vol1");
        await File.WriteAllTextAsync(file2, "vol2");
        await File.WriteAllTextAsync(file3, "vol3");

        await InsertBookWithFileAsync(scope.Paths.Database, "第一部", 1, "library/series-vol-1.epub");
        await InsertBookWithFileAsync(scope.Paths.Database, "第二部", 2, "library/series-vol-2.epub");
        await InsertBookWithFileAsync(scope.Paths.Database, "第三部", 3, "library/series-vol-3.epub");
        await scope.Window.ViewModel.RefreshViewAsync();
        await Render();

        var stack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        var badge = scope.Get<ListBox>("BookGrid")
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("seriesBadge") && button.IsVisible);

        var coverBorder = badge.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("bookCover"));
        var frontImage = coverBorder.GetVisualDescendants().OfType<Image>().Single(image => image.Classes.Contains("seriesFront"));
        stack.IsHovered = true;
        await Until(() => frontImage.RenderTransform?.Value.M11 >= 1.059);
        Assert.InRange(frontImage.RenderTransform!.Value.M11, 1.059, 1.061);
        stack.IsHovered = false;
        await Until(() => frontImage.RenderTransform?.Value.M11 <= 1.001);

        var presenter = Assert.IsType<ContentPresenter>(badge.GetVisualDescendants().First(visual => visual is ContentPresenter));
        var badgeCenter = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseMove(badgeCenter);
        await Until(() => badge.Classes.Contains(":pointerover"));
        await Render();
        var hoverBg = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
        var hoverFg = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground);
        Assert.Equal(byte.MaxValue, hoverBg.Color.A);
        Assert.NotEqual(hoverBg.Color, hoverFg.Color);
        scope.Window.MouseMove(new Point(10, 10));
        await Until(() => !badge.Classes.Contains(":pointerover"));
        await Render();

        var menu = (ContextMenu)scope.Call("BuildBookContextMenu", stack)!;
        var items = menu.Items.OfType<MenuItem>().ToArray();
        var sendDeviceMenu = Assert.Single(items, item => Equals(item.Header, "发送到设备"));
        var sendEmailMenu = Assert.Single(items, item => Equals(item.Header, "发送到 Kindle 邮箱"));
        var sendWebMenu = Assert.Single(items, item => Equals(item.Header, "发送到 Kindle Web"));

        // Inside the expanded series strip, right-clicking even the front card treats it as a single volume:
        // no "展开系列" entry and single-book Send menus without "全部发送".
        var volumeMenu = (ContextMenu)scope.Call("BuildSingleBookContextMenu", stack)!;
        var volumeItems = volumeMenu.Items.OfType<MenuItem>().ToArray();
        Assert.DoesNotContain(volumeItems, item => (item.Header?.ToString() ?? string.Empty).StartsWith("展开系列", StringComparison.Ordinal));
        foreach (var sendHeader in new[] { "发送到设备", "发送到 Kindle 邮箱", "发送到 Kindle Web" })
        {
            var volumeSendMenu = Assert.Single(volumeItems, item => Equals(item.Header, sendHeader));
            Assert.DoesNotContain(volumeSendMenu.Items.OfType<MenuItem>(),
                sub => (sub.Header?.ToString() ?? string.Empty).StartsWith("全部发送", StringComparison.Ordinal));
        }

        foreach (var parentMenu in new[] { sendDeviceMenu, sendEmailMenu, sendWebMenu })
        {
            var subItems = parentMenu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal("全部发送（3 本）", subItems[0].Header);
            Assert.Equal(["全部发送（3 本）", "1 · 第一部", "2 · 第二部", "3 · 第三部"],
                subItems.Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
        }

        // Sending a specific volume targets that exact volume even though it is not _selectedCard.
        var sendSecondVolume = sendDeviceMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "2 · 第二部"));
        sendSecondVolume.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Contains("第二部", scope.Get<TextBlock>("ConfirmationMessageText").Text);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => sentPaths.Count == 1);
        Assert.Equal("series-vol-2.epub", Assert.Single(sentPaths));

        // Wait until the single-volume transfer fully unwinds before triggering Send All.
        await Until(() => !scope.Field<bool>("_isTransferring") && scope.Field<int>("_transferGate") == 0);

        sentPaths.Clear();
        var sendAllItem = sendDeviceMenu.Items.OfType<MenuItem>().First();
        sendAllItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Contains("3 本书", scope.Get<TextBlock>("ConfirmationTitleText").Text);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => sentPaths.Count == 3);
        Assert.Equal(["series-vol-1.epub", "series-vol-2.epub", "series-vol-3.epub"], sentPaths);

        // Clicking the series badge expands the horizontal cover strip overlay.
        badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        var overlay = scope.Get<Grid>("SeriesShelfOverlay");
        var strip = scope.Get<StackPanel>("SeriesShelfItemsHost");
        Assert.True(overlay.IsVisible);
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, strip.Orientation);
        Assert.Equal("测试系列", scope.Get<TextBlock>("SeriesShelfTitleText").Text);
        Assert.Equal(3, strip.Children.Count);
        scope.Get<Button>("SeriesShelfCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilSettled(() => !overlay.IsVisible, "series shelf overlay should finish closing transition");
        Assert.False(overlay.IsVisible);
    });

    [Fact]
    public Task SeriesCollectionAssignmentTargetsWholeStackUnlessExpandedVolumeClicked() => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = "zh-CN",
            OnboardingCompleted = true,
            NetworkEnabled = false,
            AutoUpdateCheckEnabled = false,
            AutoConnectDevice = false,
            StackBookSeries = true,
            CollectionsMutuallyExclusive = false
        });

        await InsertBookAsync(scope.Paths.Database, "第一部", 1);
        await InsertBookAsync(scope.Paths.Database, "第二部", 2);
        await InsertBookAsync(scope.Paths.Database, "第三部", 3);
        var library = scope.Field<IBookLibraryService>("_library");
        var collection = await library.CreateCollectionAsync("推理漫画");
        await scope.Call<Task>("RefreshLibraryAsync");
        await Render();

        // 1) Assigning a collection from the shelf stack card adds all 3 volumes.
        var stack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        var stackMenu = (ContextMenu)scope.Call("BuildBookContextMenu", stack)!;
        var stackCollectionRoot = Assert.Single(stackMenu.Items.OfType<MenuItem>(), m => Equals(m.Header, "收藏夹"));
        var stackMangaItem = Assert.Single(stackCollectionRoot.Items.OfType<MenuItem>(), m => Equals(m.Header, "推理漫画"));
        stackMangaItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => scope.Window.ViewModel.Books.All(b => b.Book.CollectionIds.Contains(collection.Id)));
        Assert.All(scope.Window.ViewModel.Books, card => Assert.Contains(collection.Id, card.Book.CollectionIds));

        // 2) Toggling again on the checked stack removes all 3 volumes.
        stack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        stackMenu = (ContextMenu)scope.Call("BuildBookContextMenu", stack)!;
        stackCollectionRoot = Assert.Single(stackMenu.Items.OfType<MenuItem>(), m => Equals(m.Header, "收藏夹"));
        stackMangaItem = Assert.Single(stackCollectionRoot.Items.OfType<MenuItem>(), m => Equals(m.Header, "推理漫画"));
        Assert.True(stackMangaItem.IsChecked);
        stackMangaItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => scope.Window.ViewModel.Books.All(b => !b.Book.CollectionIds.Contains(collection.Id)));

        // 3) Assigning a collection from an expanded individual volume (even the front stack card) adds only that volume.
        stack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        var frontId = stack.Book.Id;
        var singleMenu = (ContextMenu)scope.Call("BuildSingleBookContextMenu", stack)!;
        var singleCollectionRoot = Assert.Single(singleMenu.Items.OfType<MenuItem>(), m => Equals(m.Header, "收藏夹"));
        var singleMangaItem = Assert.Single(singleCollectionRoot.Items.OfType<MenuItem>(), m => Equals(m.Header, "推理漫画"));
        singleMangaItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => scope.Window.ViewModel.Books.Single(b => b.Book.Id == frontId).Book.CollectionIds.Contains(collection.Id));

        Assert.Single(scope.Window.ViewModel.Books, b => b.Book.CollectionIds.Contains(collection.Id));
        Assert.All(
            scope.Window.ViewModel.Books.Where(b => b.Book.Id != frontId),
            other => Assert.DoesNotContain(collection.Id, other.Book.CollectionIds));
    });

    [Fact]
    public Task SeriesShelfOverlayCentersReadClickAndRetainsCoversAcrossReopen() => Run(async () =>
    {
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = "zh-CN",
            OnboardingCompleted = true,
            NetworkEnabled = false,
            AutoUpdateCheckEnabled = false,
            AutoConnectDevice = false,
            StackBookSeries = true
        });

        var coversDir = scope.Paths.Covers;
        Directory.CreateDirectory(coversDir);
        var tinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X2S0AAAAASUVORK5CYII=");
        for (var i = 1; i <= 5; i++)
        {
            var coverRel = Path.Combine("covers", $"vol-{i}.png");
            await File.WriteAllBytesAsync(Path.Combine(scope.Paths.Data, coverRel), tinyPng);
            await InsertBookWithCoverAndFileAsync(
                scope.Paths.Database,
                $"卷 {i}",
                i,
                $"series-vol-{i}.epub",
                coverRel);
        }

        await scope.Window.ViewModel.RefreshViewAsync();
        await Render();

        var grid = scope.Get<ListBox>("BookGrid");
        await Until(() => grid.ContainerFromIndex(0) is not null);
        var badge = grid.ContainerFromIndex(0)!.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("seriesBadge"));

        // Opening the series overlay places it strictly inside Column 1 (bookshelf area, not sidebar)
        // and keeps all 5 volume covers bound so volumes beyond the initial 3 preload still render.
        badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();

        var overlay = scope.Get<Grid>("SeriesShelfOverlay");
        var panel = scope.Get<Border>("SeriesShelfPanel");
        var strip = scope.Get<StackPanel>("SeriesShelfItemsHost");
        var detailPane = scope.Get<Border>("LibraryDetailPane");
        Assert.True(overlay.IsVisible);
        Assert.Equal(1, Grid.GetColumn(overlay));
        Assert.Equal(1, Grid.GetColumnSpan(overlay));
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, panel.VerticalAlignment);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, panel.HorizontalAlignment);
        var panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), scope.Window)!.Value.Y;
        var gridBottom = grid.TranslatePoint(new Point(0, grid.Bounds.Height), scope.Window)!.Value.Y;
        Assert.InRange(panelBottom, gridBottom - 1, gridBottom + 1);
        Assert.Equal(5, strip.Children.Count);
        Assert.Equal(["1 · 卷 1", "2 · 卷 2", "3 · 卷 3", "4 · 卷 4", "5 · 卷 5"],
            strip.Children.Select(tile => ToolTip.GetTip(tile)?.ToString() ?? string.Empty).ToArray());

        var stack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        await Task.WhenAll(stack.SeriesMembers.Select(member => member.CoverLoadingTask));
        await Render();

        foreach (var tile in strip.Children)
        {
            var image = tile.GetVisualDescendants().OfType<Image>().Single();
            Assert.NotNull(image.Source);
            var readBtn = tile.GetVisualDescendants().OfType<Button>()
                .Single(btn => btn.Classes.Contains("coverReadAction"));
            Assert.False(readBtn.IsVisible);
        }

        var scrollViewer = scope.Get<ScrollViewer>("SeriesShelfScrollViewer");
        Assert.Equal(default, strip.Margin);
        Assert.True(scrollViewer.Extent.Width <= scrollViewer.Viewport.Width + 1);
        Assert.Equal(0, scrollViewer.Offset.X);

        var firstBadge = strip.Children[0].GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("seriesCurrentBadge"));
        Assert.True(firstBadge.IsVisible);
        Assert.All(strip.Children.Skip(1), tile =>
            Assert.False(tile.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("seriesCurrentBadge")).IsVisible));

        // First-click the 5th tile: keeps overlay open, shows Read and moves the current marker
        // without opening the right detail pane.
        var fifthTile = Assert.IsAssignableFrom<Grid>(strip.Children[4]);
        var fifthPoint = fifthTile.TranslatePoint(new Point(fifthTile.Bounds.Width / 2, 40), scope.Window)!.Value;
        scope.Window.MouseDown(fifthPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(fifthPoint, MouseButton.Left, RawInputModifiers.None);
        await Render();

        var fifthReadButton = fifthTile.GetVisualDescendants().OfType<Button>()
            .Single(btn => btn.Classes.Contains("coverReadAction"));
        Assert.True(overlay.IsVisible);
        Assert.False(detailPane.IsVisible);
        Assert.Equal(0, overlay.Margin.Right);
        Assert.True(fifthReadButton.IsVisible);

        // Close overlay and simulate cover unloading (e.g. virtualization / reader close),
        // then reopen and confirm:
        // 1) volume order stays 1..5 even though vol 5 is now the front book on the shelf
        // 2) vol 5 (currently active) is horizontally centered in the ScrollViewer viewport
        // 3) all volume covers reload and bind without falling back to placeholders.
        scope.Get<Button>("SeriesShelfCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(overlay.IsVisible);
        Assert.False(overlay.IsHitTestVisible);
        await UntilSettled(() => !overlay.IsVisible, "series shelf overlay should finish closing transition");
        Assert.Equal(0, overlay.Opacity);
        Assert.False(overlay.IsVisible);

        var currentStack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        Assert.Equal("卷 5", currentStack.Title);
        foreach (var member in currentStack.SeriesMembers)
            member.UnloadCover();

        badge = grid.ContainerFromIndex(0)!.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("seriesBadge"));
        badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();

        Assert.True(overlay.IsVisible);
        Assert.Equal(["1 · 卷 1", "2 · 卷 2", "3 · 卷 3", "4 · 卷 4", "5 · 卷 5"],
            strip.Children.Select(tile => ToolTip.GetTip(tile)?.ToString() ?? string.Empty).ToArray());
        Assert.Equal(0, scrollViewer.Offset.X);
        var fifthBadge = strip.Children[4].GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("seriesCurrentBadge"));
        Assert.True(fifthBadge.IsVisible);

        await Task.WhenAll(currentStack.SeriesMembers.Select(member => member.CoverLoadingTask));
        await Render();
        foreach (var tile in strip.Children)
        {
            var image = tile.GetVisualDescendants().OfType<Image>().Single();
            Assert.NotNull(image.Source);
        }

        // Expanding an 8-volume overflowing series scrolls the middle active volume into viewport center
        // without leaving artificial whitespace when less than full.
        scope.Get<Button>("SeriesShelfCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilSettled(() => !overlay.IsVisible, "series shelf overlay should finish closing transition");
        for (var i = 6; i <= 8; i++)
        {
            var coverRel = Path.Combine("covers", $"vol-{i}.png");
            await File.WriteAllBytesAsync(Path.Combine(scope.Paths.Data, coverRel), tinyPng);
            await InsertBookWithCoverAndFileAsync(scope.Paths.Database, $"卷 {i}", i, $"series-vol-{i}.epub", coverRel);
        }
        await scope.Window.ViewModel.RefreshViewAsync();
        await Render();
        var expandedStack = Assert.Single(scope.Window.ViewModel.ShelfBooks);
        var vol5Member = expandedStack.SeriesMembers.Single(m => m.Title == "卷 5");
        scope.Window.ViewModel.PromoteSeriesVolume(expandedStack, vol5Member);
        await Render();
        badge = grid.ContainerFromIndex(0)!.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("seriesBadge"));
        badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.True(scrollViewer.Extent.Width > scrollViewer.Viewport.Width);
        var overflowVol5 = Assert.IsAssignableFrom<Grid>(strip.Children[4]);
        var overflowCenter = overflowVol5.TranslatePoint(new Point(overflowVol5.Bounds.Width / 2, 40), scrollViewer)!.Value.X;
        Assert.InRange(overflowCenter, scrollViewer.Viewport.Width / 2 - 2, scrollViewer.Viewport.Width / 2 + 2);

        // Holding left mouse button on the strip and dragging scrolls horizontally without triggering a tile click.
        var offsetBeforeDrag = scrollViewer.Offset.X;
        var dragStart = scrollViewer.TranslatePoint(new Point(scrollViewer.Bounds.Width / 2, 80), scope.Window)!.Value;
        scope.Window.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseMove(new Point(dragStart.X + 120, dragStart.Y), RawInputModifiers.LeftMouseButton);
        scope.Window.MouseUp(new Point(dragStart.X + 120, dragStart.Y), MouseButton.Left, RawInputModifiers.None);
        await Render();
        Assert.True(overlay.IsVisible);
        Assert.True(scrollViewer.Offset.X < offsetBeforeDrag - 80);
        Assert.All(strip.Children, tile =>
            Assert.False(tile.GetVisualDescendants().OfType<Button>()
                .Single(btn => btn.Classes.Contains("coverReadAction")).IsVisible));

        // Select a tile again and click its visible Read button -> closes overlay.
        scrollViewer.Offset = new Vector(0, 0);
        await Render();
        var secondTile = Assert.IsAssignableFrom<Grid>(strip.Children[1]);
        var secondPoint = secondTile.TranslatePoint(new Point(secondTile.Bounds.Width / 2, 40), scope.Window)!.Value;
        scope.Window.MouseDown(secondPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(secondPoint, MouseButton.Left, RawInputModifiers.None);
        await Render();
        var secondReadButton = secondTile.GetVisualDescendants().OfType<Button>()
            .Single(btn => btn.Classes.Contains("coverReadAction"));
        Assert.True(secondReadButton.IsVisible);
        var readPoint = secondReadButton.TranslatePoint(
            new Point(secondReadButton.Bounds.Width / 2, secondReadButton.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(readPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(readPoint, MouseButton.Left, RawInputModifiers.None);
        await Render();
        Assert.False(overlay.IsVisible);
    });

    private static async Task InsertBookWithCoverAndFileAsync(
        string databasePath,
        string title,
        double seriesIndex,
        string relativePath,
        string coverPath)
    {
        var bookId = Guid.NewGuid().ToString();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Books(Id,Title,Authors,Series,SeriesIndex,CoverPath,CreatedAt,UpdatedAt) " +
            "VALUES($id,$title,'测试作者','测试系列',$index,$cover,$time,$time);" +
            "INSERT INTO BookFiles(Id,BookId,Format,RelativePath,Size,Sha256) " +
            "VALUES($fileId,$id,'epub',$path,128,$sha);";
        command.Parameters.AddWithValue("$id", bookId);
        command.Parameters.AddWithValue("$fileId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$index", seriesIndex);
        command.Parameters.AddWithValue("$cover", coverPath);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$sha", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertBookWithFileAsync(string databasePath, string title, double seriesIndex, string relativePath)
    {
        var bookId = Guid.NewGuid().ToString();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Books(Id,Title,Authors,Series,SeriesIndex,CreatedAt,UpdatedAt) " +
            "VALUES($id,$title,'测试作者','测试系列',$index,$time,$time);" +
            "INSERT INTO BookFiles(Id,BookId,Format,RelativePath,Size,Sha256) " +
            "VALUES($fileId,$id,'epub',$path,128,$sha);";
        command.Parameters.AddWithValue("$id", bookId);
        command.Parameters.AddWithValue("$fileId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$index", seriesIndex);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$sha", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertBookAsync(string databasePath, string title, double seriesIndex)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Books(Id,Title,Authors,Series,SeriesIndex,CreatedAt,UpdatedAt) " +
            "VALUES($id,$title,'测试作者','测试系列',$index,$time,$time);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$index", seriesIndex);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static void InvokeApplyDeviceBookFilter(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "ApplyDeviceBookFilter",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(bool)],
            modifiers: null)!;
        method.Invoke(window, [true]);
    }
}
