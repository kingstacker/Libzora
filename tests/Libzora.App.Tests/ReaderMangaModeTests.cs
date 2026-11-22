using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using SkiaSharp;
using Xunit;

namespace Libzora.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderMangaModeTests(SettingsUiSession session)
{
    [Fact]
    public Task AutoDetectsComicEpubPersistsPerBookAndPreservesProseLayout() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var root = Path.Combine(scope.Paths.Data, "manga-detect-book");
        Directory.CreateDirectory(root);
        var img1 = CreateSolidPng(Path.Combine(root, "p1.png"), 400, 600, SKColors.Crimson);
        var img2 = CreateSolidPng(Path.Combine(root, "p2.png"), 400, 600, SKColors.RoyalBlue);
        var ch1 = Path.Combine(root, "page1.xhtml");
        var ch2 = Path.Combine(root, "page2.xhtml");
        await File.WriteAllTextAsync(Path.Combine(root, "content.opf"),
            "<package><metadata><meta property=\"rendition:layout\">pre-paginated</meta></metadata><spine page-progression-direction=\"rtl\"/></package>");
        await File.WriteAllTextAsync(ch1, "<html><body><p><img src='p1.png'/></p></body></html>");
        await File.WriteAllTextAsync(ch2, "<html><body><p><img src='p2.png'/></p></body></html>");

        var mangaDoc = new EpubReaderDocument(root, [ch1, ch2], [], []);
        var mangaFile = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Format = "epub", RelativePath = "manga.epub" };
        var mangaBook = new Book { Id = mangaFile.BookId, Title = "漫画测试", Files = [mangaFile] };
        using var mangaCard = new BookCardViewModel(mangaBook, scope.Paths.Data);

        scope.Set("_readerDocument", mangaDoc);
        scope.Set("_readerBookCard", mangaCard);
        scope.Set("_readerBookFile", mangaFile);
        await scope.Call<Task>("InitializeReaderInteractionAsync", mangaDoc, CancellationToken.None);
        await Render();

        Assert.True(scope.Property<bool>("IsReaderMangaMode"));
        Assert.True(scope.Property<bool>("IsReaderRightToLeftPaging"));
        Assert.Equal(0, scope.Field<ReaderLayoutSettings>("_readerLayout").BodyPadding);
        Assert.True(scope.Field<ReaderLayoutSettings>("_readerLayout").MangaMode);
        Assert.Equal(new Thickness(0), scope.Get<Control>("ReaderWebViewHost").Margin);
        Assert.False(scope.Get<Control>("ReaderWebViewBottomCover").IsVisible);

        scope.Call("ReaderLayoutSettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.True(scope.Get<Control>("ReaderMangaSection").IsVisible);
        Assert.True(scope.Get<ToggleSwitch>("ReaderMangaModeCheck").IsChecked);
        Assert.True(scope.Get<Control>("ReaderMangaOptionsPanel").IsVisible);
        Assert.False(scope.Get<Control>("ReaderTextLayoutPanel").IsVisible);
        Assert.True(scope.Get<RadioButton>("ReaderMangaRtlOption").IsChecked);

        // Toggle pure black background on and verify pure black #000000 canvas.
        scope.Get<ToggleSwitch>("ReaderMangaBlackBgCheck").IsChecked = true;
        await Render();
        Assert.True(scope.Property<bool>("IsReaderMangaBlackCanvas"));
        Assert.Equal(Colors.Black, ((ISolidColorBrush)scope.Window.Resources["ReaderPageBrush"]!).Color);

        // Toggle manga mode off: prose typography controls return with default BodyPadding intact.
        scope.Get<ToggleSwitch>("ReaderMangaModeCheck").IsChecked = false;
        await Render();
        Assert.False(scope.Property<bool>("IsReaderMangaMode"));
        Assert.True(scope.Get<Control>("ReaderTextLayoutPanel").IsVisible);
        Assert.False(scope.Get<Control>("ReaderMangaOptionsPanel").IsVisible);
        Assert.Equal(ReaderLayoutDefaults.DefaultBodyPadding, scope.Field<ReaderLayoutSettings>("_readerLayout").BodyPadding);
        Assert.Equal(ReaderTheme.Classic, scope.Property<ReaderAppearanceSettings>("CurrentReaderAppearance").Theme);

        // Persisted per-book decision (disabled) stays remembered, while a novel has no manga entry.
        var stored = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.True(stored.TryGetMangaSettings(mangaFile.Id, out var savedManga));
        Assert.False(savedManga.Enabled);

        scope.Get<Popup>("ReaderLayoutSettingsPopup").IsOpen = false;
        _ = img1;
        _ = img2;
    });

    [Fact]
    public Task RtlTwoPageSpreadPaintsEarlierPageOnRightAndHandlesZoomPanAndFlowMenu() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var root = Path.Combine(scope.Paths.Data, "manga-spread-book");
        Directory.CreateDirectory(root);
        CreateSolidPng(Path.Combine(root, "p1.png"), 400, 600, SKColors.Red);
        CreateSolidPng(Path.Combine(root, "p2.png"), 400, 600, SKColors.Blue);
        var chapter = Path.Combine(root, "chapter.xhtml");
        await File.WriteAllTextAsync(chapter,
            "<html><body><p><img src='p1.png'/></p><p><img src='p2.png'/></p></body></html>");

        var doc = new EpubReaderDocument(root, [chapter], [], []);
        var file = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Format = "epub", RelativePath = "spread.epub" };
        var book = new Book { Id = file.BookId, Title = "双页漫画", Files = [file] };
        using var card = new BookCardViewModel(book, scope.Paths.Data);

        scope.Set("_readerDocument", doc);
        scope.Set("_readerBookCard", card);
        scope.Set("_readerBookFile", file);
        await scope.Call<Task>("InitializeReaderInteractionAsync", doc, CancellationToken.None);
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await (Task)scope.Call("EnsureReaderHostsAsync")!;
        await Render();

        var host = Assert.IsType<NativeReaderHost>(scope.Field<IReaderHost?>("_readerActiveHost"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
        host.Navigate(new Uri(chapter), scope.Field<ReaderLayoutSettings>("_readerLayout"), false);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Render();

        // Enable Manga Mode + Two-Page RTL Spread.
        await scope.Call<Task>("ApplyReaderMangaSettingsChangeAsync", new MangaBookSettings
        {
            Enabled = true,
            Direction = MangaReadingDirection.RightToLeft,
            TwoPageSpread = true
        }, true);
        await Render();

        Assert.Equal(2, host.PageCount);
        Assert.True(scope.Get<MenuItem>("ReaderTwoPageModeItem").IsChecked);
        Assert.Equal("−", scope.Get<Button>("ReaderZoomOutButton").Content);
        Assert.Equal("+", scope.Get<Button>("ReaderZoomInButton").Content);
        Assert.Equal("100%", scope.Get<TextBlock>("ReaderZoomText").Text);

        // In RTL two-page spread, page 0 (Red) is on the right half and page 1 (Blue) is on the left half.
        using (var snap = SKBitmap.Decode(await host.CaptureVisiblePageAsync(CancellationToken.None)))
        {
            Assert.NotNull(snap);
            var leftPixel = snap!.GetPixel(snap.Width / 4, snap.Height / 2);
            var rightPixel = snap.GetPixel(snap.Width * 3 / 4, snap.Height / 2);
            Assert.True(leftPixel.Blue > leftPixel.Red, $"Expected Blue on left half, got {leftPixel}");
            Assert.True(rightPixel.Red > rightPixel.Blue, $"Expected Red on right half, got {rightPixel}");
        }

        // Double-click zooms in-place to 200% instead of opening modal lightbox; Escape resets to 100%.
        var center = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(center, MouseButton.Left);
        scope.Window.MouseUp(center, MouseButton.Left);
        scope.Window.MouseDown(center, MouseButton.Left);
        scope.Window.MouseUp(center, MouseButton.Left);
        await Render();

        Assert.False(scope.Get<Control>("ReaderImageZoomOverlay").IsVisible);
        Assert.Equal(2.0, host.MangaZoom, 2);
        Assert.Equal("200%", scope.Get<TextBlock>("ReaderZoomText").Text);

        // Toolbar + zooms to 225% and Escape resets back to 100%.
        scope.Get<Button>("ReaderZoomInButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.Equal(2.25, host.MangaZoom, 2);
        Assert.Equal("225%", scope.Get<TextBlock>("ReaderZoomText").Text);

        scope.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Render();
        Assert.Equal(1.0, host.MangaZoom, 2);
        Assert.Equal("100%", scope.Get<TextBlock>("ReaderZoomText").Text);

        // Switch via toolbar flow menu to Webtoon vertical scroll.
        scope.Call("ReaderFlowModeItem_Click", scope.Get<MenuItem>("ReaderScrollModeItem"), new RoutedEventArgs());
        await Render();
        Assert.True(scope.Property<bool>("IsReaderMangaVerticalScroll"));
        Assert.Equal(0, scope.Field<ReaderLayoutSettings>("_readerLayout").FlowMode);
        Assert.False(host.IsPaginated);

        scope.Call("ReleaseReaderHosts");
    });

    [Fact]
    public Task SinglePagePerChapterMangaPairsCompanionSpreadAndStepsByTwo() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var root = Path.Combine(scope.Paths.Data, "manga-single-page-chapters");
        Directory.CreateDirectory(root);
        var colors = new[] { SKColors.Red, SKColors.Blue, SKColors.Green, SKColors.Gold };
        var chapters = new string[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            CreateSolidPng(Path.Combine(root, $"p{i + 1}.png"), 400, 600, colors[i]);
            var chapterPath = Path.Combine(root, $"page{i + 1}.xhtml");
            await File.WriteAllTextAsync(chapterPath, $"<html><body><p><img src='p{i + 1}.png'/></p></body></html>");
            chapters[i] = chapterPath;
        }

        var doc = new EpubReaderDocument(root, chapters, [], []);
        var file = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Format = "epub", RelativePath = "single-page-manga.epub" };
        var book = new Book { Id = file.BookId, Title = "分章漫画双页", Files = [file] };
        using var card = new BookCardViewModel(book, scope.Paths.Data);

        scope.Set("_readerDocument", doc);
        scope.Set("_readerBookCard", card);
        scope.Set("_readerBookFile", file);
        scope.Set("_readerChapterIndex", 0);
        await scope.Call<Task>("InitializeReaderInteractionAsync", doc, CancellationToken.None);
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await (Task)scope.Call("EnsureReaderHostsAsync")!;
        await Render();

        var activeHost = Assert.IsType<NativeReaderHost>(scope.Field<IReaderHost?>("_readerActiveHost"));
        await scope.Call<Task>("NavigateReaderHostAndWaitAsync", activeHost, new Uri(chapters[0]), CancellationToken.None, false);
        await Render();

        await scope.Call<Task>("ApplyReaderMangaSettingsChangeAsync", new MangaBookSettings
        {
            Enabled = true,
            Direction = MangaReadingDirection.RightToLeft,
            TwoPageSpread = true
        }, true);
        await Render();

        Assert.True(activeHost.IsShowingCompanionSpread);
        using (var snap = SKBitmap.Decode(await activeHost.CaptureVisiblePageAsync(CancellationToken.None)))
        {
            Assert.NotNull(snap);
            var leftPixel = snap!.GetPixel(snap.Width / 4, snap.Height / 2);
            var rightPixel = snap.GetPixel(snap.Width * 3 / 4, snap.Height / 2);
            Assert.True(leftPixel.Blue > 200 && leftPixel.Red < 50, $"Expected Blue companion page on left half, got {leftPixel}");
            Assert.True(rightPixel.Red > 200 && rightPixel.Blue < 50, $"Expected Red primary page on right half, got {rightPixel}");
        }

        await scope.Call<Task>("TurnReaderPageCoreAsync", 1, false);
        await Render();
        Assert.Equal(2, scope.Field<int>("_readerChapterIndex"));
        var secondHost = Assert.IsType<NativeReaderHost>(scope.Property<IReaderHost?>("CurrentReaderHost"));
        Assert.True(secondHost.IsShowingCompanionSpread);
        using (var snap = SKBitmap.Decode(await secondHost.CaptureVisiblePageAsync(CancellationToken.None)))
        {
            Assert.NotNull(snap);
            var leftPixel = snap!.GetPixel(snap.Width / 4, snap.Height / 2);
            var rightPixel = snap.GetPixel(snap.Width * 3 / 4, snap.Height / 2);
            Assert.True(leftPixel.Red > 200 && leftPixel.Green > 150 && leftPixel.Blue < 50, $"Expected Gold page 4 on left half, got {leftPixel}");
            Assert.True(rightPixel.Green > 100 && rightPixel.Red < 50 && rightPixel.Blue < 50, $"Expected Green page 3 on right half, got {rightPixel}");
        }

        await scope.Call<Task>("TurnReaderPageCoreAsync", -1, false);
        await Render();
        Assert.Equal(0, scope.Field<int>("_readerChapterIndex"));

        // Clicking the left third in RTL manga turns forward immediately without
        // waiting for the double-click timer, and rapid clicks keep turning
        // instead of locking into 200% zoom.
        var preloadSlot = scope.Get<ContentControl>("ReaderPreloadHostSlot");
        Assert.True(preloadSlot.IsVisible);
        Assert.Equal(0, preloadSlot.Opacity);
        Assert.False(preloadSlot.IsHitTestVisible);

        var leftPoint = secondHost.TranslatePoint(new Point(40, secondHost.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(leftPoint, MouseButton.Left);
        scope.Window.MouseUp(leftPoint, MouseButton.Left);
        await Until(() => scope.Field<int>("_readerChapterIndex") == 2);
        var turnedHost = Assert.IsType<NativeReaderHost>(scope.Property<IReaderHost?>("CurrentReaderHost"));
        Assert.Equal(1.0, turnedHost.MangaZoom, 2);

        // Even a double-click in the outer third stays at 100% zoom and keeps turning.
        var rightPoint = turnedHost.TranslatePoint(new Point(turnedHost.Bounds.Width - 40, turnedHost.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(rightPoint, MouseButton.Left);
        scope.Window.MouseUp(rightPoint, MouseButton.Left);
        scope.Window.MouseDown(rightPoint, MouseButton.Left);
        scope.Window.MouseUp(rightPoint, MouseButton.Left);
        await Until(() => scope.Field<int>("_readerChapterIndex") == 0);
        var backHost = Assert.IsType<NativeReaderHost>(scope.Property<IReaderHost?>("CurrentReaderHost"));
        Assert.Equal(1.0, backHost.MangaZoom, 2);

        scope.Call("ReleaseReaderHosts");
    });

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static string CreateSolidPng(string path, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(action, CancellationToken.None);

    private static async Task Render()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
