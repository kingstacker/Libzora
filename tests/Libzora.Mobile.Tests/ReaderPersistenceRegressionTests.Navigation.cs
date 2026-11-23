using System.Reflection;
using Libzora.Infrastructure;
using Libzora.Core;
using Libzora.Layout;
using Libzora.Mobile.Views;
using Xunit;

namespace Libzora.Mobile.Tests;

public sealed partial class ReaderPersistenceRegressionTests
{
    [Fact]
    public Task LegacyChapterIndexRestoresAfterDuplicateCoverRemoval() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var cover = Path.Combine(state.Root, "cover.xhtml");
        var duplicate = Path.Combine(state.Root, "duplicate.xhtml");
        var body = Path.Combine(state.Root, "body.xhtml");
        Set(state.Reader, "_epubDocument", new EpubReaderDocument(state.Root, [cover, body], [], [])
        {
            OriginalChapters = [cover, duplicate, body],
            ChapterAliases = new Dictionary<string, string> { [duplicate] = cover }
        });
        var saved = new ReaderProgressRow(state.File.BookId, state.File.Id,
            "/old/device/body.xhtml", "text", 2, 150, 25, 0, DateTimeOffset.UtcNow)
        {
            ContentPosition = new ReaderContentPosition { TextOffset = 42 }
        };
        var restored = (ReaderProgressRow)typeof(ReaderPage).GetMethod("ResolvePosition", PrivateInstance)!
            .Invoke(state.Reader, [saved])!;
        Assert.Equal(1, restored.ChapterIndex);
        Assert.Equal(150, restored.ScrollPosition);
        Assert.Equal(42, restored.ContentPosition!.TextOffset);
    }, CancellationToken.None);

    private static Task JumpToFigure(ReaderState state) => (Task)typeof(ReaderPage)
        .GetMethod("JumpToInChapterFragmentWithReturnAsync", PrivateInstance)!
        .Invoke(state.Reader, [state.Host, "figure"])!;

    [Fact]
    public Task FigureJumpDoesNotStartWhenBodyProgressCannotBeSaved() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        await ExecuteAsync(state.Paths, "CREATE TRIGGER FailFigureProgress BEFORE INSERT ON ReaderProgress BEGIN SELECT RAISE(FAIL, 'blocked'); END;");
        Set(state.Reader, "_epubPosition", 345d);
        await JumpToFigure(state);
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.False((bool)Field("_footnoteProgressSuppressed").GetValue(state.Reader)!);
        Assert.Null(Field("_footnoteBodyProgress").GetValue(state.Reader));
        Assert.Equal(345d, Field("_epubPosition").GetValue(state.Reader));
        Assert.Null(await state.Modules.Reader.Data.GetProgressAsync(state.File.Id));
    }, CancellationToken.None);

    [Fact]
    public Task NestedFigureJumpPreservesTheOriginalBodyBaseline() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        Set(state.Reader, "_epubPosition", 345d);
        await JumpToFigure(state);
        var body = await state.Modules.Reader.Data.GetProgressAsync(state.File.Id);
        Set(state.Reader, "_epubPosition", 999d);
        await JumpToFigure(state);
        await state.Reader.PauseAsync();
        Assert.True(state.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(body, Field("_footnoteBodyProgress").GetValue(state.Reader));
        Assert.Equal(body, await state.Modules.Reader.Data.GetProgressAsync(state.File.Id));
    }, CancellationToken.None);

    [Fact]
    public Task ImmediateMovementAwayFromFigureResumesSavingCurrentProgress() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        Set(state.Reader, "_epubPosition", 345d);
        await JumpToFigure(state);
        typeof(ReaderPage).GetMethod("ApplyScrollUpdate", PrivateInstance)!.Invoke(state.Reader, [0d, 999d]);
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.False((bool)Field("_footnoteProgressSuppressed").GetValue(state.Reader)!);
        await state.Reader.PauseAsync();
        Assert.Equal(999, (await state.Modules.Reader.Data.GetProgressAsync(state.File.Id))!.ScrollPosition);
    }, CancellationToken.None);

    [Fact]
    public Task ProgrammaticPositioningAndCanceledDragKeepTheReturnPoint() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        await JumpToFigure(state);
        Set(state.Reader, "_footnoteNavigationInFlight", true);
        typeof(ReaderPage).GetMethod("ApplyScrollUpdate", PrivateInstance)!.Invoke(state.Reader, [0d, 500d]);
        Assert.True(state.Reader.GetNativeState().FootnoteReturnVisible);
        Set(state.Reader, "_footnoteNavigationInFlight", false);
        typeof(ReaderPage).GetMethod("OnReaderWebMessage", PrivateInstance)!.Invoke(state.Reader,
            [state.Host, new ReaderWebMessageReceivedEventArgs("""{"type":"scroll","top":0,"left":700,"layoutAdjusted":true}""")]);
        Assert.True(state.Reader.GetNativeState().FootnoteReturnVisible);
        Set(state.Reader, "_pendingDragScroll", (0d, 900d));
        typeof(ReaderPage).GetMethod("RevertPendingDragScroll", PrivateInstance)!.Invoke(state.Reader, null);
        Assert.True(state.Reader.GetNativeState().FootnoteReturnVisible);
        Set(state.Reader, "_pendingDragScroll", (0d, 900d));
        typeof(ReaderPage).GetMethod("CommitPendingDragScroll", PrivateInstance)!.Invoke(state.Reader, null);
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
    }, CancellationToken.None);

    [Fact]
    public Task SharedFrontendFigureLinkDoesNotEnterAnUnreachableReturnMode() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        Set(state.Reader, "_usesNativeFrontend", false);
        var body = Path.Combine(state.Root, "body.xhtml");
        await File.WriteAllTextAsync(body, "<html><body><p>Body</p><div style='page-break-before:always'></div><table id='figure'><tr><td>Figure</td></tr></table></body></html>");
        await LoadChapter(state.Host, body);
        Assert.True(state.Host.TryGetFragmentMediaTarget("figure", out var page, out var visible, out _, out _));
        Assert.True(page > 0);
        Assert.False(visible);
        state.Host.WebMessageReceived += typeof(ReaderPage).GetMethod("OnReaderWebMessage", PrivateInstance)!
            .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(state.Reader);
        await (Task)typeof(ReaderPage).GetMethod("HandleLinkAsync", PrivateInstance)!
            .Invoke(state.Reader, ["#figure", null])!;
        await state.Reader.PauseAsync();
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.False((bool)Field("_footnoteProgressSuppressed").GetValue(state.Reader)!);
        Assert.True((await state.Modules.Reader.Data.GetProgressAsync(state.File.Id))!.ScrollPosition > 0);
    }, CancellationToken.None);

    [Fact]
    public Task FigureJumpWaitingForSaveCannotMoveAfterNewChapterNavigation() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var databaseGate = (SemaphoreSlim)typeof(ReaderDataService).GetField("_databaseGate", PrivateInstance)!
            .GetValue(state.Modules.Reader.Data)!;
        await databaseGate.WaitAsync();
        Set(state.Reader, "_epubPosition", 345d);
        var pending = JumpToFigure(state);
        Assert.False(pending.IsCompleted);
        var nextPath = Path.Combine(state.Root, "notes.xhtml");
        try
        {
            await File.WriteAllTextAsync(nextPath, "<html><body><p>New chapter</p></body></html>");
            Assert.True(await (Task<bool>)typeof(ReaderPage).GetMethod("NavigateToChapterAsync", PrivateInstance)!
                .Invoke(state.Reader, [state.Host, 1, new Uri(nextPath), null, true])!);
        }
        finally { databaseGate.Release(); }
        await pending;
        Assert.Equal(1, Field("_chapterIndex").GetValue(state.Reader));
        Assert.Equal(nextPath, state.Host.Source!.LocalPath);
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.Null(Field("_footnoteBodyProgress").GetValue(state.Reader));
    }, CancellationToken.None);

    [Fact]
    public Task OldLinkWaitingForLayoutCannotNavigateBackAfterChapterChange() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        typeof(NativeReaderHost).GetField("_layout", PrivateInstance)!.SetValue(state.Host, new ChapterLayout
        {
            Pages = [], BodyTextLength = 100, FragmentPages = new Dictionary<string, int>(),
            Options = new TypesetLayoutOptions(), IsComplete = false
        });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(NativeReaderHost).GetProperty("RelayoutTask", PrivateInstance)!.SetValue(state.Host, gate.Task);
        var oldLink = (Task)typeof(ReaderPage).GetMethod("HandleLinkAsync", PrivateInstance)!
            .Invoke(state.Reader, ["#figure", null])!;
        Assert.False(oldLink.IsCompleted);
        var nextPath = Path.Combine(state.Root, "notes.xhtml");
        await File.WriteAllTextAsync(nextPath, "<html><body><p>New chapter</p></body></html>");
        try
        {
            Assert.True(await (Task<bool>)typeof(ReaderPage).GetMethod("NavigateToChapterAsync", PrivateInstance)!
                .Invoke(state.Reader, [state.Host, 1, new Uri(nextPath), null, true])!);
        }
        finally { gate.TrySetResult(); }
        await oldLink;
        Assert.Equal(1, Field("_chapterIndex").GetValue(state.Reader));
        Assert.Equal(nextPath, state.Host.Source!.LocalPath);
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
    }, CancellationToken.None);

    [Fact]
    public Task SupersededNavigationFailureDoesNotRollBackTheNewChapterIndex() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var target = new Uri(Path.Combine(state.Root, "missing.xhtml"));
        var pending = (Task<bool>)typeof(ReaderPage).GetMethod("NavigateToChapterAsync", PrivateInstance)!
            .Invoke(state.Reader, [state.Host, 1, target, null, true])!;
        typeof(ReaderPage).GetMethod("BeginReaderNavigation", PrivateInstance)!.Invoke(state.Reader, null);
        // A newer request has claimed chapter 1. A stale failure would
        // incorrectly restore chapter 0 from the superseded request.
        // Complete it directly without waiting for the background file load.
        var completed = (EventHandler<ReaderNavigationCompletedEventArgs>)typeof(NativeReaderHost)
            .GetField("NavigationCompleted", PrivateInstance)!.GetValue(state.Host)!;
        completed(state.Host, new ReaderNavigationCompletedEventArgs(target, false));
        Assert.False(await pending);
        Assert.Equal(1, Field("_chapterIndex").GetValue(state.Reader));
    }, CancellationToken.None);

    [Fact]
    public void ChapterLinksPreferExactCaseAndRejectAmbiguousFallback()
    {
        var root = Path.GetTempPath();
        var upper = Path.Combine(root, "A.xhtml");
        var lower = Path.Combine(root, "a.xhtml");
        var document = new EpubReaderDocument(root, [upper, lower], [], []);
        var resolve = typeof(ReaderPage).GetMethod("FindEpubChapter", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(0, resolve.Invoke(null, [document, upper]));
        Assert.Equal(1, resolve.Invoke(null, [document, lower]));
        Assert.Equal(-1, resolve.Invoke(null, [document, Path.Combine(root, "a.XHTML")]));
        var legacy = new EpubReaderDocument(root, [upper], [], []);
        Assert.Equal(0, resolve.Invoke(null, [legacy, lower]));
    }

    private static async Task LoadChapter(NativeReaderHost host, string path)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ReaderNavigationCompletedEventArgs> handler = (_, args) => completion.TrySetResult(args.IsSuccess);
        host.NavigationCompleted += handler;
        try
        {
            host.Navigate(new Uri(path));
            Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally { host.NavigationCompleted -= handler; }
    }
}
