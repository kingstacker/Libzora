using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ReaderNavigationSingleClickShowsDetailsAndCoverReadAction() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        Assert.Null(scope.Window.FindControl<Button>("DetailCoverReadButton"));
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        // The details pane waits out the platform double-tap window so a
        // double-click can open the reader without flashing it first.
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        AssertNoLibraryCoverReadAction(scope);
        await Until(() => scope.Get<Control>("LibraryDetailPane").IsVisible);
        await Render();
        var coverRead = Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);
        var readPoint = coverRead.TranslatePoint(new Point(coverRead.Bounds.Width / 2, coverRead.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(readPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(readPoint, MouseButton.Left, RawInputModifiers.None);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);
        await Until(() => scope.Field<object?>("_readerDocument") is not null);
        await scope.Call<Task>("ReturnFromReaderAsync");
        Assert.True(scope.Get<Control>("LibraryRoot").IsVisible);
        Assert.False(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        Assert.Single(scope.Window.ViewModel.Books, card => card.IsSelected);
        AssertNoLibraryCoverReadAction(scope);
        await scope.Call<Task>("RefreshLibraryAsync");
        await Render();
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        AssertNoLibraryCoverReadAction(scope);
        await scope.Call<Task>("OpenApplicationSettingsAsync");
        scope.Call("ShowLibraryPage");
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ReaderNavigationClosingDetailsHidesCoverReadActionUntilAnotherClick(bool useCloseButton) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var detail = scope.Get<Control>("LibraryDetailPane");
        ClickBook();
        await Until(() => detail.IsVisible);
        await Render();
        Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsVisible);

        if (useCloseButton)
            scope.Get<Button>("LibraryDetailCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else
            scope.Call("ClearSelectedBook");
        AssertNoLibraryCoverReadAction(scope);
        await Until(() => !detail.IsVisible);
        await scope.Call<Task>("RefreshLibraryAsync");
        await Render();
        Assert.False(detail.IsVisible);
        AssertNoLibraryCoverReadAction(scope);

        ClickBook();
        AssertNoLibraryCoverReadAction(scope);
        await Until(() => detail.IsVisible);
        await Render();
        Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);

        void ClickBook()
        {
            var item = grid.ContainerFromIndex(0)!;
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
            scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        }
    });

    [Fact]
    public Task ReaderNavigationClosingDetailsCancelsAnEarlierPendingClick() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var detail = scope.Get<Control>("LibraryDetailPane");
        grid.SelectedItem = scope.Window.ViewModel.Books[0];
        await Until(() => detail.IsVisible);
        await Render();

        var item = grid.ContainerFromIndex(1)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Assert.NotNull(scope.Field<object?>("_pendingBookDetailsCard"));
        scope.Get<Button>("LibraryDetailCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(scope.Field<object?>("_pendingBookDetailsCard"));
        scope.Call("CompletePendingBookDetails");
        await Until(() => !detail.IsVisible);
        AssertNoLibraryCoverReadAction(scope);
    });

    [Fact]
    public Task ReaderNavigationNewClickDuringDetailsClosingStillOpensDetails() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var detail = scope.Get<Control>("LibraryDetailPane");
        grid.SelectedItem = scope.Window.ViewModel.Books[0];
        await Until(() => detail.IsVisible);
        await Render();
        scope.Get<Button>("LibraryDetailCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var card = scope.Window.ViewModel.Books[1];
        var item = grid.ContainerFromIndex(1)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        AssertNoLibraryCoverReadAction(scope);
        await Until(() => card.IsReadActionVisible);
        await Render();
        Assert.True(detail.IsVisible);
        var readButton = Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);
        Assert.Same(card, readButton.DataContext);
        Assert.Equal(card.Title, scope.Get<TextBox>("DetailTitleText").Text);
    });

    [Fact]
    public Task ReaderNavigationContextSelectionDoesNotShowCoverReadAction() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var item = scope.Get<ListBox>("BookGrid").ContainerFromIndex(0)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
        await Render();
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        AssertNoLibraryCoverReadAction(scope);
    });

    private static void AssertNoLibraryCoverReadAction(TestWindow scope) => Assert.DoesNotContain(
        scope.Get<ListBox>("BookGrid").GetVisualDescendants().OfType<Button>(),
        button => button.Classes.Contains("bookCoverRead") && button.IsVisible);

    [Fact]
    public Task ReaderNavigationDoubleClickOpensWithoutShowingDetails() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        AssertNoLibraryCoverReadAction(scope);
        await scope.Call<Task>("ReturnFromReaderAsync");
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        AssertNoLibraryCoverReadAction(scope);
    });

    [Fact]
    public Task ReaderNavigationRubberBandDragDoesNotFlashCoverReadAction() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var start = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        var end = item.TranslatePoint(new Point(item.Bounds.Width / 2, 90), scope.Window)!.Value;
        scope.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        await Render();

        // The marquee currently covers exactly the first card. It must not be
        // treated as a sole click, otherwise the reading overlay flashes on.
        var books = scope.Window.ViewModel.Books;
        Assert.True(books[0].IsSelected);
        Assert.False(books[0].IsSoleSelected);
        var coverRead = grid.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("bookCoverRead"))
            .ToArray();
        Assert.DoesNotContain(coverRead, button => button.IsEffectivelyVisible);

        scope.Window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        await Render();
        // A single enclosed book remains selected, but a marquee is not a
        // details click and must not surface its reading action or scrim.
        Assert.True(books[0].IsSoleSelected);
        Assert.False(scope.Get<Control>("LibraryDetailPane").IsVisible);
        AssertNoLibraryCoverReadAction(scope);
    });

    [Fact]
    public Task ReaderNavigationReopensInZenModeAfterReturningFromZen() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var books = scope.Window.ViewModel.Books;
        await scope.Call<Task>("OpenBookAsync", books[0], null, true);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);
        scope.Call("ToggleReaderZenMode");
        Assert.True(scope.Field<bool>("_readerZenMode"));
        await scope.Call<Task>("ReturnFromReaderAsync");
        await Until(() => scope.Get<Control>("LibraryRoot").IsVisible);
        Assert.False(scope.Field<bool>("_readerZenMode"));
        Assert.True(scope.Field<bool>("_openReaderInZenMode"));

        await scope.Call<Task>("OpenBookAsync", books[1], null, true);
        await Until(() => scope.Field<bool>("_readerZenMode"));
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.False(scope.Field<bool>("_openReaderInZenMode"));
    });

    [Fact]
    public Task ReaderNavigationDoesNotReopenInZenAfterExitingZenFirst() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var books = scope.Window.ViewModel.Books;
        await scope.Call<Task>("OpenBookAsync", books[0], null, true);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);
        scope.Call("ToggleReaderZenMode");
        Assert.True(scope.Field<bool>("_readerZenMode"));
        scope.Call("ExitReaderZenMode");
        Assert.False(scope.Field<bool>("_readerZenMode"));
        await scope.Call<Task>("ReturnFromReaderAsync");
        await Until(() => scope.Get<Control>("LibraryRoot").IsVisible);

        await scope.Call<Task>("OpenBookAsync", books[1], null, true);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.False(scope.Field<bool>("_readerZenMode"));
    });

    [Fact]
    public Task ReaderNavigationClickDoesNotShrinkHoverZoomedCover() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var border = item.GetVisualDescendants().OfType<Border>()
            .First(control => control.Classes.Contains("bookCover"));
        var image = border.GetVisualDescendants().OfType<Image>().First();
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseMove(point);
        await Render();
        await UntilSettled(() => ScaleOf(image) > 1.05, "hover should zoom the cover");

        // Pressing a card captures the pointer. The hover zoom must stay put
        // instead of collapsing into a shrink animation on click.
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        await Render();
        Assert.InRange(ScaleOf(image), 1.05, 1.07);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await Render();
        Assert.InRange(ScaleOf(image), 1.05, 1.07);

        // Leaving the card still releases the zoom once the click is over.
        scope.Window.MouseMove(new Point(30, scope.Window.Bounds.Height - 30));
        await Render();
        await UntilSettled(() => ScaleOf(image) < 1.01, "cover should zoom back after leaving the card");

        static double ScaleOf(Image image) => image.RenderTransform?.Value.M11 ?? 1d;
    });

    [Fact]
    public Task ReaderNavigationReadOverlayDoesNotScaleOnPress() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseMove(point);
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await Until(() => scope.Get<Control>("LibraryDetailPane").IsVisible);
        await Render();
        var readButton = Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);
        var readPoint = readButton.TranslatePoint(
            new Point(readButton.Bounds.Width / 2, readButton.Bounds.Height / 2),
            scope.Window)!.Value;
        scope.Window.MouseMove(readPoint);
        await Render();
        var hoverBounds = readButton.Bounds;
        Assert.InRange(readButton.RenderTransform?.Value.M11 ?? 1d, 0.999, 1.001);

        // Fluent's Button theme shrinks the control on press. The reading
        // scrim must stay flush with the cover instead of shrinking.
        scope.Window.MouseDown(readPoint, MouseButton.Left, RawInputModifiers.None);
        await Render();
        Assert.InRange(readButton.RenderTransform?.Value.M11 ?? 1d, 0.999, 1.001);
        Assert.Equal(hoverBounds, readButton.Bounds);
    });

    [Fact]
    public Task ReaderNavigationOpeningFromCoverShowsHeroTransition() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var grid = scope.Get<ListBox>("BookGrid");
        var item = grid.ContainerFromIndex(0)!;
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 30), scope.Window)!.Value;
        scope.Window.MouseMove(point);
        scope.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await Until(() => scope.Get<Control>("LibraryDetailPane").IsVisible);
        await Render();
        var readButton = Assert.Single(grid.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);
        var readPoint = readButton.TranslatePoint(
            new Point(readButton.Bounds.Width / 2, readButton.Bounds.Height / 2),
            scope.Window)!.Value;
        scope.Window.MouseDown(readPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(readPoint, MouseButton.Left, RawInputModifiers.None);

        // The frozen shelf remains behind the lifted cover even if loading
        // the document hides LibraryRoot before its first page is ready.
        var sourceCover = item.GetVisualDescendants().OfType<Border>()
            .First(control => control.Classes.Contains("bookCover"));
        var sourceSize = sourceCover.Bounds.Size;
        var layer = scope.Get<Grid>("ReaderOpenTransitionLayer");
        var liftedCover = scope.Get<Border>("ReaderOpenTransitionCover");
        Assert.True(layer.IsVisible);
        Assert.Equal(0, sourceCover.Opacity);
        Assert.NotNull(scope.Get<Image>("ReaderOpenTransitionShelf").Source);
        Assert.NotNull(scope.Get<Image>("ReaderOpenTransitionImage").Source);
        Assert.NotNull(liftedCover.RenderTransform);
        Assert.Equal(sourceSize.Width, liftedCover.Width);
        Assert.Equal(sourceSize.Height, liftedCover.Height);
        Assert.Equal(0, scope.Get<TextBlock>("ReaderOpenTransitionLoadingText").Opacity);
        await Until(() => !layer.IsVisible);
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.Equal(1, sourceCover.Opacity);
        Assert.Null(scope.Get<Image>("ReaderOpenTransitionImage").Source);
        Assert.Null(scope.Get<Image>("ReaderOpenTransitionPageImage").Source);
        Assert.Null(scope.Get<Image>("ReaderOpenTransitionShelf").Source);
        Assert.Null(scope.Field<object?>("_readerOpenTransition"));
    });

    [Fact]
    public Task ReaderOpeningTransitionResetRestoresCoverAndAllowsAnotherOpen() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        var sourceCover = scope.Get<ListBox>("BookGrid").ContainerFromIndex(0)!
            .GetVisualDescendants().OfType<Border>()
            .First(control => control.Classes.Contains("bookCover"));
        sourceCover.Opacity = 0.8;

        scope.Call("StartReaderOpenTransition", card, null);
        scope.Window.UpdateLayout();
        var layer = scope.Get<Grid>("ReaderOpenTransitionLayer");
        var liftedCover = scope.Get<Border>("ReaderOpenTransitionCover");
        var sourceOrigin = sourceCover.TranslatePoint(default, layer)!.Value;
        Assert.True(layer.IsVisible);
        Assert.True(layer.Bounds.Width > 100 && layer.Bounds.Height > 100);
        Assert.Equal(sourceOrigin.X, Canvas.GetLeft(liftedCover), 8);
        Assert.Equal(sourceOrigin.Y, Canvas.GetTop(liftedCover), 8);
        Assert.Equal(0, sourceCover.Opacity);
        // Failed opens and an early return must release the frozen shelf and
        // restore the source, without waiting for the entrance to finish.
        await scope.Call<Task>("CompleteReaderOpenTransitionAsync");
        Assert.False(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        Assert.Equal(0.8, sourceCover.Opacity);
        Assert.Null(scope.Get<Image>("ReaderOpenTransitionShelf").Source);
        Assert.Null(scope.Get<Image>("ReaderOpenTransitionImage").Source);

        scope.Call("StartReaderOpenTransition", card, null);
        Assert.True(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        scope.Call("ResetReaderOpenTransition");
        Assert.Equal(0.8, sourceCover.Opacity);
        Assert.Null(scope.Field<object?>("_readerOpenTransition"));
    });

    [Fact]
    public Task ReaderOpeningRemovesTheShelfBeforeTheBodyCrossfade() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Call("StartReaderOpenTransition", scope.Window.ViewModel.Books[0], null);
        var shelf = scope.Get<Image>("ReaderOpenTransitionShelf");
        Assert.True(shelf.IsVisible);
        await Until(() => scope.Get<Border>("ReaderOpenTransitionBackdrop").Opacity == 1);
        Assert.True(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        Assert.False(shelf.IsVisible);

        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        var reveal = scope.Call<Task>("CompleteReaderOpenTransitionAsync");
        Assert.False(shelf.IsVisible);
        await reveal;
        Assert.False(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        Assert.Null(shelf.Source);
        scope.Get<Control>("ReaderRoot").IsVisible = false;
        scope.Get<Control>("LibraryRoot").IsVisible = true;
    });

    [Fact]
    public Task ReaderOpeningOwnsItsCoverWhenTheShelfBitmapIsReleased() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        var sourceBitmap = card.CoverImage;
        scope.Call("StartReaderOpenTransition", card, null);
        var liftedImage = scope.Get<Image>("ReaderOpenTransitionImage");
        var ownedBitmap = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(liftedImage.Source);
        Assert.NotSame(sourceBitmap, ownedBitmap);

        card.UnloadCover();
        await Render();
        Assert.Null(card.CoverImage);
        Assert.Same(ownedBitmap, liftedImage.Source);
        Assert.True(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        scope.Call("ResetReaderOpenTransition");
        Assert.Null(liftedImage.Source);
    });

    [Fact]
    public Task ReaderOpeningWithoutCoverUsesFallback3DCover() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        card.UnloadCover();
        var sourceCover = scope.Get<ListBox>("BookGrid").ContainerFromIndex(0)!
            .GetVisualDescendants().OfType<Border>()
            .First(control => control.Classes.Contains("bookCover"));
        sourceCover.GetVisualDescendants().OfType<Image>().Last().Source = null;

        scope.Call("StartReaderOpenTransition", card, null);
        Assert.True(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        Assert.True(scope.Get<Border>("ReaderOpenTransitionFallbackCover").IsVisible);
        Assert.Equal(0, sourceCover.Opacity);
        Assert.NotNull(scope.Field<object?>("_readerOpenTransition"));
        await scope.Call<Task>("CompleteReaderOpenTransitionAsync");
        Assert.False(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        Assert.Equal(1, sourceCover.Opacity);
    });

    [Fact]
    public Task ReaderClosingGlidesProportionalCoverBackOntoShelf() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        var grid = scope.Get<ListBox>("BookGrid");
        var sourceCover = grid.ContainerFromIndex(0)!
            .GetVisualDescendants().OfType<Border>()
            .First(control => control.Classes.Contains("bookCover"));

        await scope.Call<Task>("OpenBookAsync", card, null, true);
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.False(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);

        var closing = scope.Call<Task>("CloseReaderAsync");
        await Until(() => scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible
            && scope.Get<Control>("LibraryRoot").IsVisible);
        Assert.NotNull(scope.Get<Image>("ReaderOpenTransitionPageImage").Source);
        Assert.Equal(sourceCover.Bounds.Width, scope.Get<Border>("ReaderOpenTransitionCover").Width, 8);
        Assert.Equal(sourceCover.Bounds.Height, scope.Get<Border>("ReaderOpenTransitionCover").Height, 8);
        Assert.Equal(0, sourceCover.Opacity);

        await closing;
        Assert.False(scope.Get<Grid>("ReaderOpenTransitionLayer").IsVisible);
        Assert.False(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>("LibraryRoot").IsVisible);
        Assert.Equal(1, sourceCover.Opacity);
        Assert.Null(scope.Get<Image>("ReaderOpenTransitionPageImage").Source);
        Assert.Null(scope.Field<object?>("_readerOpenTransition"));
    });

    [Fact]
    public Task SelectingBottomRowBookScrollsItIntoViewWhenDetailPaneReflowsGrid() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Window.Width = 860;
        scope.Window.Height = 440;
        await Render();

        var grid = scope.Get<ListBox>("BookGrid");
        var scrollViewer = Assert.Single(grid.GetVisualDescendants().OfType<ScrollViewer>());
        const int targetIndex = 1;
        var container = grid.ContainerFromIndex(targetIndex)!;
        var clickPoint = container.TranslatePoint(new Point(container.Bounds.Width / 2, 24), scope.Window)!.Value;
        scope.Window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);

        await Until(() => scope.Get<Control>("LibraryDetailPane").IsVisible);
        await Render();
        await Render();

        var updatedContainer = grid.ContainerFromIndex(targetIndex)!;
        var topLeft = updatedContainer.TranslatePoint(default, scrollViewer)!.Value;
        var bottomLeft = updatedContainer.TranslatePoint(new Point(0, updatedContainer.Bounds.Height), scrollViewer)!.Value;
        Assert.True(topLeft.Y >= 0, $"Selected card top {topLeft.Y} scrolled above viewport.");
        Assert.True(bottomLeft.Y <= scrollViewer.Viewport.Height + 1,
            $"Selected card bottom {bottomLeft.Y} extends past viewport {scrollViewer.Viewport.Height}.");
    });

    [Theory]
    [InlineData("ReadingMaterialsPage", "ReaderNotesNavigationButton", "返回笔记")]
    [InlineData("ReadingDashboardPage", "ReadingDashboardButton", "返回阅读数据")]
    public Task ReaderNavigationReturnsToOriginAfterToolbarAutoHide(string page, string navigation, string label) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("ShowStage3Page", scope.Get<Grid>(page), scope.Get<Button>(navigation));
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Set("_readerTocExpanded", true);
        scope.Call("ApplyReaderPanelLayout");
        Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(scope.Get<Button>("ReaderBackButton")));
        await Render();
        var back = scope.Get<Button>("ReaderBackButton");
        Assert.True(back.Bounds.Width >= back.DesiredSize.Width - 1);
        Assert.InRange(back.Bounds.Height, 34, 35);
        scope.Call("SetReaderToolbarsVisible", false);
        Assert.True(scope.Get<Button>("ReaderBackButton").IsEffectivelyVisible);
        scope.Get<Button>("ReaderBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>(page).IsEffectivelyVisible);
        Assert.True(scope.Window.IsVisible);
    });

    [Fact]
    public Task ReaderNavigationUsesOneBackArrowAndOneTocToggle() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Set("_readerTocExpanded", true);
        scope.Set("_readerTocMinimal", false);
        scope.Call("ApplyReaderPanelLayout");
        await Render();
        var expandedBack = scope.Get<Button>("ReaderBackButton");
        var collapsedBack = scope.Get<Button>("ReaderCollapsedBackButton");
        var toc = scope.Get<Button>("ReaderTocToggleButton");
        Assert.True(expandedBack.IsEffectivelyVisible);
        Assert.False(collapsedBack.IsEffectivelyVisible);
        Assert.True(toc.IsEffectivelyVisible);
        Assert.Null(scope.Window.FindControl<Button>("ReaderTocCloseButton"));
        Assert.True(expandedBack.TranslatePoint(default, scope.Window)!.Value.X <
                    scope.Get<TextBlock>("ReaderBookInfoText").TranslatePoint(default, scope.Window)!.Value.X);
        toc.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.False(expandedBack.IsEffectivelyVisible);
        Assert.True(collapsedBack.IsEffectivelyVisible);
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible);
        collapsedBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>("LibraryRoot").IsVisible);
    });

    [Fact]
    public Task ReaderNavigationSettingsClosesReaderBeforeShowingSettings() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await scope.Call<Task>("OpenApplicationSettingsAsync");
        Assert.False(scope.Get<Control>("ReaderRoot").IsVisible);
        Assert.True(scope.Get<Control>("SettingsPage").IsEffectivelyVisible);
        Assert.True(scope.Window.IsVisible);
    });
}
