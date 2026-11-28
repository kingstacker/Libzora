using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Libzora.Mobile.Views;
using SkiaSharp;
using Xunit;

namespace Libzora.Mobile.Tests;

public sealed partial class ReaderPageTransitionTests
{
    [Fact]
    public Task ScrollFooterUsesActualPixelsAndSavedProgressMatchesThem() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 40, 28]);
        var host = fixture.Host;
        host.SeekToPixelScroll(0);
        var before = fixture.Reader.GetNativeState();
        Assert.Equal(0, before.Progress, 6);
        host.ScrollByPixel(60);
        await Render();
        // Explicit emission avoids waiting for the ordinary 30 Hz throttle.
        host.SeekToPixelScroll(host.GetScrollState().Position);
        var moved = fixture.Reader.GetNativeState();
        Assert.True(moved.Progress > before.Progress && moved.Progress < 100);
        var height = host.GetScrollState().ClientHeight;
        host.SeekToPixelScroll(height + 20);
        Assert.Equal(1, host.CurrentPage);
        Assert.StartsWith("2 /", fixture.Reader.GetNativeState().Position);
        await fixture.Reader.PauseAsync();
        var saved = await fixture.Modules.Reader.Data.GetProgressAsync(fixture.FileId);
        Assert.NotNull(saved);
        Assert.Equal((int)host.GetScrollState().Position, saved.ScrollPosition);
        Assert.Equal(fixture.Reader.GetNativeState().Progress, saved.ProgressPercent, 6);
    }, CancellationToken.None);

    [Fact]
    public Task CarryBeyondAPartialPreviewSurvivesFullLayoutCompletion() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 300, 28]);
        var host = fixture.Host;
        var preview = GetField<Libzora.Layout.ChapterLayout>(host, "_scrollNextChapterLayout");
        Assert.False(preview.IsComplete);
        var distance = preview.Pages.Count * host.GetScrollState().ClientHeight * 1.2;
        var carriedOverflow = 0d;
        host.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess && args.Request == new Uri(fixture.Chapters[1]))
                carriedOverflow = GetField<double>(host, "_partialScrollOverflow");
        };
        host.SeekToPixelScroll(fixture.NextStart - 10);
        host.ScrollByPixel(20);
        host.ScrollByPixel(distance);
        await WaitUntil(() => GetField<int>(fixture.Reader, "_chapterIndex") == 1 && !GetField<bool>(fixture.Reader, "_turning"));
        Assert.True(carriedOverflow > 0, "Motion outside a partial preview must remain pending, not disappear at clamp.");
        await host.EnsureCompleteLayoutAsync();
        await Render();
        Assert.Equal(10 + distance, host.GetScrollState().Position, 3);
    }, CancellationToken.None);

    [Fact]
    public Task ReversingDuringForwardHandoffKeepsTheOriginalChapterAndExactPosition() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 300, 28]);
        var host = fixture.Host;
        var originalLayout = GetField<object>(host, "_layout");
        var seam = fixture.NextStart;
        host.SeekToPixelScroll(seam - 10);
        host.ScrollByPixel(20);
        host.ScrollByPixel(-150);
        await WaitUntil(() => !GetField<bool>(fixture.Reader, "_turning"));
        Assert.Equal(0, GetField<int>(fixture.Reader, "_chapterIndex"));
        Assert.Equal(fixture.Chapters[0], host.LoadedChapterPath);
        Assert.Same(originalLayout, GetField<object>(host, "_layout"));
        Assert.Equal(seam - 140, host.GetScrollState().Position, 4);
        Assert.True(host.IsReadingPositionInLoadedChapter);
    }, CancellationToken.None);

    [Fact]
    public Task ReverseCarryCanCrossTwoShortChaptersAfterTheFingerStops() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        await fixture.Navigate(2);
        fixture.Host.SeekToPixelScroll(0);
        await WaitUntil(() => GetField<object?>(fixture.Host, "_scrollPreviousChapterPreview") is not null);
        var previousOrigin = (double)typeof(NativeReaderHost)
            .GetProperty("ContinuousPreviousChapterStartY", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Host)!;
        fixture.Host.ScrollByPixel(previousOrigin - 200);
        await WaitUntil(() => GetField<int>(fixture.Reader, "_chapterIndex") == 0 && !GetField<bool>(fixture.Reader, "_turning"));
        Assert.Equal(fixture.NextStart - 200, fixture.Host.GetScrollState().Position, 3);
        Assert.True(fixture.Host.IsReadingPositionInLoadedChapter);
    }, CancellationToken.None);

    [Fact]
    public Task NegativeRunwayCoordinatesDoNotSelectTextFromTheWrongChapter() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        SetField(fixture.Host, "_scrollOffset", -120d);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var map = typeof(NativeReaderHost).GetMethod("MapPointToPage", flags)!;
        Assert.Null(map.Invoke(fixture.Host, [new Point(40, 60)]));
        var hit = Assert.IsType<ValueTuple<int, SKPoint>>(map.Invoke(fixture.Host, [new Point(40, 160)]));
        Assert.Equal(0, hit.Item1);
        Assert.Equal(40f, hit.Item2.Y);
        var origin = (Point)typeof(NativeReaderHost).GetMethod("PageOrigin", flags)!.Invoke(fixture.Host, [0])!;
        Assert.Equal(new Point(0, 120), origin);
        fixture.Host.PrepareContinuousScrollHandoff(handoffDown: true, carryDownOffset: 10);
        Assert.Null(map.Invoke(fixture.Host, [new Point(40, 160)]));
        fixture.Host.PrepareContinuousScrollHandoff();
    }, CancellationToken.None);

    [Fact]
    public Task ScrollSliceBoundaryUsesThePaintedFootnotePage() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        var host = fixture.Host;
        await host.EnsureCompleteLayoutAsync();
        var layout = GetField<ChapterLayout>(host, "_layout");
        Assert.True(layout.Pages.Count > 1);
        var height = (float)host.GetScrollState().ClientHeight;
        var marker = new PlacedHotZone
        {
            Kind = HotZoneKind.FootnoteMarker,
            Rect = new SKRect(30, height - 8, 55, height + 7),
            Href = "#note",
        };
        layout.Pages[0].HotZones.Add(marker);
        SetField(host, "_scrollOffset", (double)height - 80);
        var map = typeof(NativeReaderHost).GetMethod("MapPointToPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mapped = Assert.IsType<ValueTuple<int, SKPoint>>(map.Invoke(host, [new Point(42, 83)]));
        Assert.Equal(0, mapped.Item1);
        Assert.Equal(height + 3, mapped.Item2.Y, 3);
        Assert.Same(marker, layout.GetHotZoneAt(mapped.Item1, mapped.Item2));
    }, CancellationToken.None);

    [Fact]
    public Task SameSliceTocJumpUsesTheFragmentTop() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        var host = fixture.Host;
        await host.EnsureCompleteLayoutAsync();
        var layout = GetField<ChapterLayout>(host, "_layout");
        var height = host.GetScrollState().ClientHeight;
        var tops = new Dictionary<string, float> { ["middle"] = (float)(height / 2) };
        SetField(host, "_layout", new ChapterLayout
        {
            Pages = layout.Pages,
            BodyTextLength = layout.BodyTextLength,
            FragmentPages = new Dictionary<string, int> { ["middle"] = 0 },
            FragmentTops = tops,
            Options = layout.Options,
        });
        host.SeekToPixelScroll(0);
        host.ScrollToFragment("middle");
        Assert.Equal(height / 2 - layout.Options.InsetVertical, host.GetScrollState().Position, 3);
    }, CancellationToken.None);

    [Fact]
    public Task NavigationInFlightCannotSaveATargetChapterWithOldCoordinates() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        fixture.Host.SeekToPixelScroll(60);
        await fixture.Reader.PauseAsync();
        var original = await fixture.Modules.Reader.Data.GetProgressAsync(fixture.FileId);
        SetField(fixture.Reader, "_chapterIndex", 1);
        fixture.Host.PrepareContinuousScrollHandoff(handoffDown: true, carryDownOffset: 10);
        Assert.False(await (Task<bool>)typeof(ReaderPage).GetMethod("SaveProgressSoonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Reader, [true])!);
        Assert.Equal(original, await fixture.Modules.Reader.Data.GetProgressAsync(fixture.FileId));
        fixture.Host.PrepareContinuousScrollHandoff();
        SetField(fixture.Reader, "_chapterIndex", 0);
    }, CancellationToken.None);

    [Fact]
    public Task ExpandedFootnoteLayoutsAreNeverStoredAsNormalChapterPreviews() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        await fixture.Navigate(0, fragment: "note");
        Assert.True(fixture.Host.HasLoadedFootnoteFragment);
        var snapshot = typeof(NativeReaderHost).GetMethod("CaptureContinuousChapter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Host, null);
        Assert.Null(snapshot);
    }, CancellationToken.None);

    [Fact]
    public Task ReplacedPreviousChapterRequestCannotPublishItsStaleNeighbor() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 28, 28]);
        fixture.Host.SetScrollPreviousChapterPath(fixture.Chapters[1]);
        fixture.Host.SetScrollPreviousChapterPath(fixture.Chapters[2]);
        await WaitUntil(() => GetField<object?>(fixture.Host, "_scrollPreviousChapterPreview") is { } preview
            && (string)preview.GetType().GetProperty("Path")!.GetValue(preview)! == fixture.Chapters[2]);
        Assert.Equal(fixture.Chapters[2], (string)GetField<object>(fixture.Host, "_scrollPreviousChapterPreview")
            .GetType().GetProperty("Path")!.GetValue(GetField<object>(fixture.Host, "_scrollPreviousChapterPreview"))!);
    }, CancellationToken.None);

    [Fact]
    public Task OldCompletionFlagCannotStrandANewPartialChapter() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 300, 28]);
        await fixture.Navigate(1, partial: true);
        Assert.False(fixture.Host.IsLayoutComplete);
        SetField(fixture.Host, "_layoutCompletionScheduled", true);
        SetField(fixture.Host, "_layoutCompletionNavigationVersion", -1L);
        fixture.Host.CompleteLayoutInBackground();
        await (Task)typeof(NativeReaderHost).GetProperty("RelayoutTask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(fixture.Host)!;
        Assert.True(fixture.Host.IsLayoutComplete);
    }, CancellationToken.None);

    [Fact]
    public Task ScrollSeekAndFooterUseTheSameContinuousPercentage() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 40, 28]);
        await WaitUntil(() => GetField<bool>(fixture.Reader, "_bookPageCountsAreExact"));
        await fixture.Reader.NativeSeekAsync(20);
        Assert.Equal(20, fixture.Reader.GetNativeState().Progress, 4);
        await fixture.Reader.NativeSeekAsync(0);
        Assert.Equal(0, fixture.Reader.GetNativeState().Progress, 4);
        await fixture.Reader.NativeSeekAsync(100);
        Assert.Equal(100, fixture.Reader.GetNativeState().Progress, 4);
    }, CancellationToken.None);

    [Fact]
    public Task BrowsingAuxiliaryNotesCannotChangeBodyProgressOrAddPhantomPages() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 40, 28]);
        var document = GetField<EpubReaderDocument>(fixture.Reader, "_epubDocument") with { AuxiliaryChapters = [fixture.Chapters[1]] };
        SetField(fixture.Reader, "_epubDocument", document);
        SetField(fixture.Reader, "_auxiliaryChapterPaths", new HashSet<string>([fixture.Chapters[1]], StringComparer.Ordinal));
        fixture.Host.SeekToPixelScroll(60);
        await fixture.Reader.PauseAsync();
        var original = fixture.Reader.GetNativeState();
        SetField(fixture.Reader, "_activeFootnote", new NativeReaderFootnote(1, true, "注释",
            new Uri(fixture.Chapters[1]).AbsoluteUri + "#note", "注释正文", false, true, NativeReaderFootnoteAnchor.Empty, null));
        await fixture.Reader.NativeJumpFootnoteAsync();
        Assert.True(fixture.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(original.Progress, fixture.Reader.GetNativeState().Progress, 6);
        Assert.Equal(0, GetField<int[]>(fixture.Reader, "_bookPageCounts")[1]);
        fixture.Host.SeekToPixelScroll(20);
        Assert.True(fixture.Reader.GetNativeState().FootnoteReturnVisible);
        await fixture.Reader.PauseAsync();
        var saved = await fixture.Modules.Reader.Data.GetProgressAsync(fixture.FileId);
        Assert.NotNull(saved);
        Assert.Equal(0, saved.ChapterIndex);
        Assert.Equal(original.Progress, saved.ProgressPercent, 6);
    }, CancellationToken.None);

    [Fact]
    public Task TurningInsideTheComposedPrefixDoesNotWaitForTheWholeChapter() => session.Session.Dispatch(async () =>
    {
        await using var fixture = await ScrollFixture.Create([28, 300, 28], flow: 1);
        await fixture.Navigate(1, partial: true);
        Assert.False(fixture.Host.IsLayoutComplete);
        Assert.True(fixture.Host.CanTurn(1));
        var prefix = GetField<object>(fixture.Host, "_layout");
        await fixture.Reader.TurnAsync(1);
        Assert.Equal(1, fixture.Host.CurrentPage);
        Assert.Same(prefix, GetField<object>(fixture.Host, "_layout"));
        Assert.False(fixture.Host.IsLayoutComplete);
    }, CancellationToken.None);

    private sealed class ScrollFixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required string[] Chapters { get; init; }
        public required ApplicationModules Modules { get; init; }
        public required ReaderPage Reader { get; init; }
        public required NativeReaderHost Host { get; init; }
        public required Window Window { get; init; }
        public required Guid FileId { get; init; }
        private EventHandler<ReaderWebMessageReceivedEventArgs>? _handler;
        private ReaderLayoutSettings _layout = new() { FlowMode = 0, VerticalWriting = false };
        public double NextStart => (double)typeof(NativeReaderHost)
            .GetProperty("ContinuousNextChapterStartY", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;

        public static async Task<ScrollFixture> Create(int[] paragraphs, int flow = 0)
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-scroll-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            var modules = MobileComposition.Create(services);
            await modules.Reader.Data.InitializeAsync();
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            shell.Preferences.Update(p => p with { ReaderFlowMode = flow, ReaderVerticalWriting = false });
            var book = new Book { Id = Guid.NewGuid(), Title = "连续滚动回归" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('9', 64) };
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            SetField(reader, "_started", true);
            SetField(reader, "_ready", true);
            SetField(reader, "_bookFile", file);
            var chapters = new string[paragraphs.Length];
            for (var index = 0; index < chapters.Length; index++)
            {
                chapters[index] = Path.Combine(root, $"chapter-{index}.xhtml");
                var text = string.Concat(Enumerable.Range(1, paragraphs[index]).Select(n =>
                    $"<p>第{index + 1}章第{n}段。正文随手指移动，跨章节与反向手势必须保留全部距离，进度必须连续更新。</p>"));
                await File.WriteAllTextAsync(chapters[index], $"<html><body><h1>第{index + 1}章</h1>{text}<p id='note'>注释目标</p></body></html>");
            }
            SetField(reader, "_epubDocument", new EpubReaderDocument(root, chapters, [], ["第一章", "第二章", "第三章"]));
            var host = new NativeReaderHost { Focusable = false };
            SetField(reader, "_readerHost", host);
            reader.FindControl<ContentControl>("ReaderHostSlot")!.Content = host;
            var window = new Window { Width = 390, Height = 844, Content = reader };
            var fixture = new ScrollFixture { Root = root, Chapters = chapters, Modules = modules,
                Reader = reader, Host = host, Window = window, FileId = file.Id };
            fixture._layout = fixture._layout with { FlowMode = flow };
            SetField(reader, "_layout", fixture._layout);
            fixture._handler = typeof(ReaderPage).GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += fixture._handler;
            window.Show();
            await Render();
            await fixture.Navigate(0);
            return fixture;
        }

        public async Task Navigate(int chapter, bool partial = false, string? fragment = null)
        {
            SetField(Reader, "_chapterIndex", chapter);
            Host.SetScrollNextChapterPath(chapter + 1 < Chapters.Length ? Chapters[chapter + 1] : null);
            Host.SetScrollPreviousChapterPath(chapter > 0 ? Chapters[chapter - 1] : null);
            var target = new Uri(Chapters[chapter] + (fragment is null ? "" : "#" + fragment));
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(object? sender, ReaderNavigationCompletedEventArgs args)
            {
                if (args.Request == target) ready.TrySetResult(args.IsSuccess);
            }
            Host.NavigationCompleted += Completed;
            try
            {
                Host.Navigate(target, _layout, false, null, partial);
                Assert.True(await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)));
                await Render();
            }
            finally { Host.NavigationCompleted -= Completed; }
        }

        public async ValueTask DisposeAsync()
        {
            Host.WebMessageReceived -= _handler;
            Host.Stop();
            await Reader.PauseAsync();
            Window.Close();
            await Render();
            using var pending = await MobileDataCoordinator.BeginRestoreAsync(CancellationToken.None);
            Modules.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
