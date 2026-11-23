using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Libzora.Mobile.Views;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed partial class ReaderPersistenceRegressionTests(MobileUiSession session)
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void BookmarkFileWinsOverTheDefaultFormatPreference()
    {
        var book = new Book { Id = Guid.NewGuid(), Title = "多格式定位" };
        var pdf = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "pdf" };
        var epub = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = ".EPUB" };
        var mobi = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "mobi" };
        book.Files = [pdf, epub, mobi];

        Assert.Same(epub, ChooseFile(book, null));
        Assert.Same(pdf, ChooseFile(book, pdf.Id));
        Assert.Same(mobi, ChooseFile(book, mobi.Id));
    }

    [Fact]
    public void MissingForeignAndUnsupportedBookmarkFilesNeverFallBackToAnotherFormat()
    {
        var book = new Book { Id = Guid.NewGuid() };
        var epub = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub" };
        var foreign = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Format = "pdf" };
        var unsupported = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "docx" };
        book.Files = [epub, foreign, unsupported];

        Assert.Null(ChooseFile(book, Guid.NewGuid()));
        Assert.Null(ChooseFile(book, foreign.Id));
        Assert.Null(ChooseFile(book, unsupported.Id));
    }

    [Fact]
    public Task ForeignBookLocationFailsBeforeOpeningOrWritingProgress() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var location = new ReaderProgressRow(Guid.NewGuid(), state.File.Id, "body.xhtml", null, 0, 200, 40, 1, DateTimeOffset.UtcNow);
        var reader = new ReaderPage(state.Book, state.Modules, state.Shell, location);

        await (Task)typeof(ReaderPage).GetMethod("OpenAsync", PrivateInstance)!.Invoke(reader, null)!;

        Assert.NotNull(reader.GetNativeState().Error);
        Assert.False(reader.GetNativeState().Ready);
        Assert.Null(Field("_bookFile").GetValue(reader));
        Assert.Null(await state.Modules.Reader.Data.GetProgressAsync(state.File.Id));
    }, CancellationToken.None);

    [Fact]
    public Task PlainNumberedFootnoteCardOffersAJumpAndKeepsItsNoteText() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var reader = state.Reader;
        NativeReaderFootnote? published = null;
        reader.NativeFootnoteRequested += footnote => published = footnote;
        using var document = JsonDocument.Parse(
            """
            {"href":"file:///tmp/body.xhtml#__libzora-plain-footnote-0-1-42",
             "footnoteText":"第一条注释说明。","marker":"（1）","x":10,"y":20}
            """);

        await (Task)typeof(ReaderPage)
            .GetMethod("ShowFootnoteAsync", PrivateInstance)!
            .Invoke(reader, [document.RootElement.Clone()])!;

        Assert.NotNull(published);
        Assert.True(published!.CanJump);
        Assert.Equal("第一条注释说明。", published.Text);
    }, CancellationToken.None);

    [Fact]
    public Task PlainFootnoteFragmentWithoutDefinitionOffsetCannotJump() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var reader = state.Reader;
        NativeReaderFootnote? published = null;
        reader.NativeFootnoteRequested += footnote => published = footnote;
        using var document = JsonDocument.Parse(
            """
            {"href":"file:///tmp/body.xhtml#__libzora-plain-footnote-0-1",
             "footnoteText":"旧格式注释。","marker":"（1）","x":10,"y":20}
            """);

        await (Task)typeof(ReaderPage)
            .GetMethod("ShowFootnoteAsync", PrivateInstance)!
            .Invoke(reader, [document.RootElement.Clone()])!;

        Assert.NotNull(published);
        Assert.False(published!.CanJump);
    }, CancellationToken.None);

    [Fact]
    public Task CrossChapterPlainFootnoteJumpNavigatesToEndnoteChapterAndReturnsToBody() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var reader = state.Reader;
        var notesPath = Path.Combine(state.Root, "notes.xhtml");
        File.WriteAllText(
            notesPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body>
                <p>注　释</p>
                <p>［1］跨章节尾注内容。</p>
              </body>
            </html>
            """);
        var bodyPath = Path.Combine(state.Root, "body.xhtml");
        File.WriteAllText(
            bodyPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body>
                <p>正文内容<sup>［1］</sup>。</p>
              </body>
            </html>
            """);

        Set(reader, "_epubPosition", 120d);
        Set(reader, "_overallProgress", .25d);
        Set(reader, "_activeFootnote", new NativeReaderFootnote(
            1,
            true,
            "［1］",
            new Uri(notesPath).AbsoluteUri + "#__libzora-plain-footnote-0-1-4",
            "跨章节尾注内容。",
            false,
            true,
            NativeReaderFootnoteAnchor.Empty,
            null));

        await reader.NativeJumpFootnoteAsync();

        Assert.True(reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(1, (int)Field("_chapterIndex").GetValue(reader)!);

        await reader.NativeReturnFromFootnoteAsync();

        Assert.False(reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(0, (int)Field("_chapterIndex").GetValue(reader)!);
    }, CancellationToken.None);

    [Fact]
    public Task NoteJumpFlushesTheThrottledBodyPointAndPauseKeepsItsProgressWhileSavingTime() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var reader = state.Reader;
        Set(reader, "_epubPosition", 345d);
        Set(reader, "_overallProgress", .43d);
        Set(reader, "_lastProgressSave", DateTimeOffset.UtcNow);
        Set(reader, "_activeFootnote", state.Note);

        await reader.NativeJumpFootnoteAsync();

        var body = (await state.Modules.Reader.Data.GetProgressAsync(state.File.Id))!;
        Assert.Equal(345, body.ScrollPosition);
        Assert.Equal(43, body.ProgressPercent);
        Assert.Equal(body, Field("_footnoteBodyProgress").GetValue(reader));
        Assert.True((bool)Field("_footnoteProgressSuppressed").GetValue(reader)!);
        Assert.True(reader.GetNativeState().FootnoteReturnVisible);
        PauseClock(reader);
        Set(reader, "_chapterIndex", 1);
        Set(reader, "_epubPosition", 999d);
        Set(reader, "_overallProgress", .95d);
        Set(reader, "_unsavedSeconds", 42L);

        await reader.PauseAsync();

        Assert.Equal(body, await state.Modules.Reader.Data.GetProgressAsync(state.File.Id));
        var stats = (await state.Modules.Reader.Data.GetReadingStatsAsync(state.File.Id))!;
        Assert.Equal(42, stats.CumulativeSeconds);
        Assert.Equal(43, stats.ProgressPercent);
        Assert.Equal(0, stats.CompletedChapters);
        Assert.Equal(0L, Field("_unsavedSeconds").GetValue(reader));

        await reader.NativeReturnFromFootnoteAsync();
        Assert.False(reader.GetNativeState().FootnoteReturnVisible);
        Assert.False((bool)Field("_footnoteProgressSuppressed").GetValue(reader)!);
        Assert.Equal(0, (int)Field("_chapterIndex").GetValue(reader)!);
    }, CancellationToken.None);

    [Fact]
    public Task BacklinkInFootnoteReturnsToBodyEvenWhenHrefIsRelativeAndUnmarked() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var reader = state.Reader;
        Set(reader, "_epubPosition", 345d);
        Set(reader, "_overallProgress", .43d);
        Set(reader, "_activeFootnote", state.Note);
        Set(reader, "_activeFootnoteBacklink", null);

        await reader.NativeJumpFootnoteAsync();
        Assert.True(reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(1, (int)Field("_chapterIndex").GetValue(reader)!);

        var handleLink = typeof(ReaderPage).GetMethod(
            "HandleLinkAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)handleLink.Invoke(reader, ["body.xhtml#ref-1", null])!;

        Assert.False(reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(0, (int)Field("_chapterIndex").GetValue(reader)!);
    }, CancellationToken.None);

    [Fact]
    public Task NoteJumpDoesNotStartWhenTheBodyPointCannotBeSaved() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        await ExecuteAsync(state.Paths, "CREATE TRIGGER FailBodyProgress BEFORE INSERT ON ReaderProgress BEGIN SELECT RAISE(FAIL, 'blocked'); END;");
        Set(state.Reader, "_activeFootnote", state.Note);
        Set(state.Reader, "_epubPosition", 345d);

        await state.Reader.NativeJumpFootnoteAsync();

        Assert.False((bool)Field("_footnoteProgressSuppressed").GetValue(state.Reader)!);
        Assert.False(state.Reader.GetNativeState().FootnoteReturnVisible);
        Assert.Equal(state.Note, Field("_activeFootnote").GetValue(state.Reader));
        Assert.Null(await state.Modules.Reader.Data.GetProgressAsync(state.File.Id));
    }, CancellationToken.None);

    [Fact]
    public Task FailedNoteTimeCommitRetainsSecondsForRetryWithoutChangingTheBodyPoint() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var body = new ReaderProgressRow(state.Book.Id, state.File.Id, "body.xhtml", null, 0, 345, 43, 1, DateTimeOffset.UtcNow);
        await state.Modules.Reader.Data.SaveProgressAsync(body);
        Set(state.Reader, "_footnoteBodyProgress", body);
        Set(state.Reader, "_footnoteProgressSuppressed", true);
        Set(state.Reader, "_chapterIndex", 1);
        Set(state.Reader, "_overallProgress", .95d);
        PauseClock(state.Reader);
        Set(state.Reader, "_unsavedSeconds", 77L);
        await ExecuteAsync(state.Paths, "CREATE TRIGGER FailNoteTime BEFORE INSERT ON ReaderReadingStats BEGIN SELECT RAISE(FAIL, 'blocked'); END;");

        await state.Reader.PauseAsync();

        Assert.Equal(77L, Field("_unsavedSeconds").GetValue(state.Reader));
        Assert.Null(await state.Modules.Reader.Data.GetReadingStatsAsync(state.File.Id));
        await ExecuteAsync(state.Paths, "DROP TRIGGER FailNoteTime;");
        await state.Reader.PauseAsync();
        Assert.Equal(body, await state.Modules.Reader.Data.GetProgressAsync(state.File.Id));
        var stats = (await state.Modules.Reader.Data.GetReadingStatsAsync(state.File.Id))!;
        Assert.Equal(77, stats.CumulativeSeconds);
        Assert.Equal(43, stats.ProgressPercent);
        Assert.Equal(0L, Field("_unsavedSeconds").GetValue(state.Reader));
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SpeechLayoutWaitCanBeCancelledByStopOrReaderSession(bool cancelSession) => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        using var external = new CancellationTokenSource();
        var layout = new ChapterLayout
        {
            Pages = [], BodyTextLength = 1, FragmentPages = new Dictionary<string, int>(),
            Options = new TypesetLayoutOptions(), IsComplete = false
        };
        typeof(NativeReaderHost).GetField("_layout", PrivateInstance)!.SetValue(state.Host, layout);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(NativeReaderHost).GetProperty("RelayoutTask", PrivateInstance)!.SetValue(state.Host, blocked.Task);
        var request = (CancellationTokenSource)typeof(ReaderPage).GetMethod("BeginSpeechPreparation", PrivateInstance)!
            .Invoke(state.Reader, [external.Token])!;
        var waiting = (Task)typeof(ReaderPage).GetMethod("EnsureLayoutCompleteAsync", PrivateInstance)!
            .Invoke(state.Reader, [request.Token])!;
        try
        {
            Assert.False(waiting.IsCompleted);
            if (cancelSession) external.Cancel();
            else await state.Reader.NativeStopSpeechAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(blocked.Task.IsCompleted);
        }
        finally
        {
            await state.Reader.NativeStopSpeechAsync();
            state.Host.Dispose();
            blocked.TrySetResult();
            await Task.Yield();
        }
    }, CancellationToken.None);

    [Fact]
    public Task ObsoleteSpeechPreparationCannotClearANewerRequest() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var begin = typeof(ReaderPage).GetMethod("BeginSpeechPreparation", PrivateInstance)!;
        var complete = typeof(ReaderPage).GetMethod("CompleteSpeechPreparation", PrivateInstance)!;
        var first = (CancellationTokenSource)begin.Invoke(state.Reader, [CancellationToken.None])!;
        var firstToken = first.Token;
        var second = (CancellationTokenSource)begin.Invoke(state.Reader, [CancellationToken.None])!;

        complete.Invoke(state.Reader, [first, false]);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Same(second, Field("_speechCancellation").GetValue(state.Reader));
        Assert.True((bool)Field("_speechPreparing").GetValue(state.Reader)!);
        complete.Invoke(state.Reader, [second, false]);
        Assert.Null(Field("_speechCancellation").GetValue(state.Reader));
        Assert.False((bool)Field("_speechPreparing").GetValue(state.Reader)!);
    }, CancellationToken.None);

    private static BookFile? ChooseFile(Book book, Guid? id) => (BookFile?)typeof(ReaderPage)
        .GetMethod("ChooseReadableFile", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [book, id]);
    private static FieldInfo Field(string name) => typeof(ReaderPage).GetField(name, PrivateInstance)!;
    private static void Set(ReaderPage reader, string name, object? value) => Field(name).SetValue(reader, value);
    private static void PauseClock(ReaderPage reader)
    {
        var clock = Field("_activityClock").GetValue(reader)!;
        clock.GetType().GetMethod("Pause")!.Invoke(clock, [DateTimeOffset.UtcNow]);
    }
    private static async Task ExecuteAsync(AppPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ReaderState : IDisposable
    {
        public required string Root { get; init; }
        public required AppPaths Paths { get; init; }
        public required ApplicationModules Modules { get; init; }
        public required MobileShell Shell { get; init; }
        public required Book Book { get; init; }
        public required BookFile File { get; init; }
        public required ReaderPage Reader { get; init; }
        public required NativeReaderHost Host { get; init; }
        public required NativeReaderFootnote Note { get; init; }

        public static async Task<ReaderState> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-persistence-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            var modules = MobileComposition.Create(services);
            await modules.Reader.Data.InitializeAsync();
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            var book = new Book { Id = Guid.NewGuid(), Title = "阅读保存回归" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", RelativePath = "book.epub" };
            book.Files = [file];
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            var host = new NativeReaderHost();
            var body = Path.Combine(root, "body.xhtml");
            var notes = Path.Combine(root, "notes.xhtml");
            Set(reader, "_readerHost", host);
            Set(reader, "_bookFile", file);
            Set(reader, "_epubDocument", new EpubReaderDocument(root, [body, notes], [], ["正文", "注释"]));
            Set(reader, "_ready", true);
            // Tests inject exact pending seconds rather than depending on
            // filesystem speed or a wall-clock boundary during setup.
            PauseClock(reader);
            return new ReaderState
            {
                Root = root, Paths = paths, Modules = modules, Shell = shell,
                Book = book, File = file, Reader = reader, Host = host,
                Note = new NativeReaderFootnote(1, true, "注释", new Uri(body).AbsoluteUri + "#note",
                    "原注", false, true, NativeReaderFootnoteAnchor.Empty, null)
            };
        }

        public void Dispose()
        {
            ((CancellationTokenSource)Field("_lifetime").GetValue(Reader)!).Cancel();
            Host.Dispose();
            Modules.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
