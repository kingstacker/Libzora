using System.IO.Compression;
using System.Reflection;
using Avalonia.Controls;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Libzora.TestFixtures;
using Xunit;

namespace Libzora.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class SyncedResumeMarkerUiTests(SettingsUiSession session)
{
    [Fact]
    public Task EpubOpenAfterRemoteSync_ShowsResumeMarkerOnFirstPage_PreservesOnReflow_AndClearsOnPageTurn() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var epub = CreateMultiPageEpub(scope.Paths.ReaderCache);
        var library = scope.Field<IBookLibraryService>("_library");
        await library.ImportAsync([epub]);
        var book = Assert.Single(await library.SearchAsync());
        var file = Assert.Single(book.Files);
        using var card = new BookCardViewModel(book, scope.Paths.Data);

        var data = scope.Field<ReaderDataService>("_readerData");
        var contentPosition = new ReaderContentPosition { TextOffset = 1800, PageIndex = 1 };
        await data.SaveProgressAsync(new ReaderProgressRow(
            book.Id,
            file.Id,
            "chapter.xhtml",
            null,
            0,
            0,
            25,
            1,
            DateTimeOffset.UtcNow)
        {
            ContentPosition = contentPosition
        });
        await UpsertMarkerViaSyncStoreAsync(scope.Paths, book.Id, file.Id, "chapter.xhtml", 0, 0, contentPosition);

        scope.Set("_readerTtsEnvironmentChecked", true);
        await scope.Call<Task>("OpenEpubReaderAsync", card, file, library.GetAbsoluteFilePath(file), true).WaitAsync(TimeSpan.FromSeconds(30));
        await ReaderTests.Render();

        var host = scope.Field<NativeReaderHost>("_readerActiveHost");
        var activeMarker = scope.Field<ReaderSyncedPositionMarker?>("_readerActiveSyncedMarker");
        Assert.NotNull(activeMarker);
        var hostMarker = NativeField<ReaderResumeMarker?>(host, "_resumeMarker");
        Assert.NotNull(hostMarker);
        Assert.Equal(contentPosition.TextOffset, hostMarker.ContentPosition!.TextOffset);

        // Reflow (font scale change) without turning pages keeps the marker on the page.
        var layout = scope.Field<ReaderLayoutSettings>("_readerBaseLayout") with { FontScale = 1.35 };
        scope.Set("_readerBaseLayout", layout);
        scope.Set("_readerLayout", layout);
        await scope.Call<Task>("ApplyReaderLayoutToHostsAsync", CancellationToken.None);
        await ReaderTests.Render();
        Assert.NotNull(scope.Field<ReaderSyncedPositionMarker?>("_readerActiveSyncedMarker"));
        Assert.NotNull(NativeField<ReaderResumeMarker?>(host, "_resumeMarker"));
        var layoutObj = NativeField<ChapterLayout>(host, "_layout");
        var restoredPage = layoutObj.Pages[host.CurrentPage];
        var resolved = typeof(NativeReaderHost)
            .GetMethod("ResumeMarkerFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(host, [restoredPage]);
        Assert.NotNull(resolved);

        // Turning to another page clears both the visual and persisted markers.
        var turnDirection = host.CanTurn(1) ? 1 : -1;
        await scope.Call<Task>("TurnReaderPageAsync", turnDirection, false);
        await ReaderTests.Render();

        Assert.Null(scope.Field<ReaderSyncedPositionMarker?>("_readerActiveSyncedMarker"));
        Assert.Null(NativeField<ReaderResumeMarker?>(host, "_resumeMarker"));
        Assert.Null(await data.GetSyncedPositionMarkerAsync(file.Id));

        // Turning back to the original page must not restore the marker.
        await scope.Call<Task>("TurnReaderPageAsync", -turnDirection, false);
        await ReaderTests.Render();
        Assert.Null(NativeField<ReaderResumeMarker?>(host, "_resumeMarker"));
    });

    [Fact]
    public Task EpubOpenWithoutRestoreProgress_DoesNotShowOrConsumeSyncedMarker() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var epub = CreateMultiPageEpub(scope.Paths.ReaderCache);
        var library = scope.Field<IBookLibraryService>("_library");
        await library.ImportAsync([epub]);
        var book = Assert.Single(await library.SearchAsync());
        var file = Assert.Single(book.Files);
        using var card = new BookCardViewModel(book, scope.Paths.Data);

        var data = scope.Field<ReaderDataService>("_readerData");
        var contentPosition = new ReaderContentPosition { TextOffset = 1800, PageIndex = 1 };
        await UpsertMarkerViaSyncStoreAsync(scope.Paths, book.Id, file.Id, "chapter.xhtml", 0, 0, contentPosition);

        scope.Set("_readerTtsEnvironmentChecked", true);
        await scope.Call<Task>("OpenEpubReaderAsync", card, file, library.GetAbsoluteFilePath(file), false).WaitAsync(TimeSpan.FromSeconds(30));
        await ReaderTests.Render();

        var host = scope.Field<NativeReaderHost>("_readerActiveHost");
        Assert.Null(scope.Field<ReaderSyncedPositionMarker?>("_readerActiveSyncedMarker"));
        Assert.Null(NativeField<ReaderResumeMarker?>(host, "_resumeMarker"));
        Assert.NotNull(await data.GetSyncedPositionMarkerAsync(file.Id));
    });

    [Fact]
    public Task PdfOpenAfterRemoteSync_ShowsResumeMarkerOnRestoredPage_AndClearsOnPageTurn() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var pdfPath = PdfFixture.Write(scope.Paths.ReaderCache, outline: true);
        var library = scope.Field<IBookLibraryService>("_library");
        await library.ImportAsync([pdfPath]);
        var book = Assert.Single(await library.SearchAsync());
        var file = Assert.Single(book.Files);
        using var card = new BookCardViewModel(book, scope.Paths.Data);

        var data = scope.Field<ReaderDataService>("_readerData");
        await data.SaveProgressAsync(new ReaderProgressRow(
            book.Id,
            file.Id,
            "pdf:page:2",
            "pdf-view:1;0;0.42;0;0;0;0;0",
            1,
            0,
            50,
            0,
            DateTimeOffset.UtcNow));
        await UpsertMarkerViaSyncStoreAsync(
            scope.Paths,
            book.Id,
            file.Id,
            "pdf:page:2",
            1,
            0,
            null,
            fragment: "pdf-view:1;0;0.42;0;0;0;0;0");

        await scope.Call<Task>("OpenPdfReaderAsync", card, file, library.GetAbsoluteFilePath(file));
        await ReaderTests.Render();

        var pdf = scope.Field<NativePdfReaderHost>("_readerActiveHost");
        Assert.Equal(2, pdf.PageNumber);
        Assert.NotNull(scope.Field<ReaderSyncedPositionMarker?>("_readerActiveSyncedMarker"));
        Assert.NotNull(PdfField<ReaderResumeMarker?>(pdf, "_resumeMarker"));

        // Turning to page 3 clears the marker from both the host and SQLite.
        await scope.Call<Task>("NavigatePdfPageAsync", 3, CancellationToken.None, true);
        await ReaderTests.Render();

        Assert.Equal(3, pdf.PageNumber);
        Assert.Null(scope.Field<ReaderSyncedPositionMarker?>("_readerActiveSyncedMarker"));
        Assert.Null(PdfField<ReaderResumeMarker?>(pdf, "_resumeMarker"));
        Assert.Null(await data.GetSyncedPositionMarkerAsync(file.Id));
    });

    private static async Task UpsertMarkerViaSyncStoreAsync(
        AppPaths paths,
        Guid bookId,
        Guid fileId,
        string chapterPath,
        int chapterIndex,
        int scrollPosition,
        ReaderContentPosition? contentPosition,
        string? fragment = null)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReaderSyncedPositionMarkers (
                BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                ProgressPercent, FlowMode, ContentPositionJson, SourceDeviceId, SyncedAt)
            VALUES (
                $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                25, 1, $contentPosition, 'remote-device-1', $syncedAt)
            ON CONFLICT(BookFileId) DO UPDATE SET
                BookId = excluded.BookId, ChapterPath = excluded.ChapterPath,
                Fragment = excluded.Fragment, ChapterIndex = excluded.ChapterIndex,
                ScrollPosition = excluded.ScrollPosition, ContentPositionJson = excluded.ContentPositionJson,
                SourceDeviceId = excluded.SourceDeviceId, SyncedAt = excluded.SyncedAt;
            """;
        command.Parameters.AddWithValue("$bookFileId", fileId.ToString());
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$chapterPath", chapterPath);
        command.Parameters.AddWithValue("$fragment", (object?)fragment ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterIndex", chapterIndex);
        command.Parameters.AddWithValue("$scrollPosition", scrollPosition);
        command.Parameters.AddWithValue("$contentPosition", contentPosition is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(contentPosition));
        command.Parameters.AddWithValue("$syncedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateMultiPageEpub(string directory)
    {
        Directory.CreateDirectory(directory);
        var epub = Path.Combine(directory, $"sync-marker-{Guid.NewGuid():N}.epub");
        var body = string.Concat(Enumerable.Range(1, 140).Select(i =>
            $"<p>第 {i} 段测试正文：多端同步阅读位置标识需要在跨设备恢复后准确落在对应文字上，并在用户切页时立刻消失。</p>"));
        var xhtml = $"<html xmlns='http://www.w3.org/1999/xhtml'><body>{body}</body></html>";
        using var archive = ZipFile.Open(epub, ZipArchiveMode.Create);
        WriteEntry("mimetype", "application/epub+zip");
        WriteEntry("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
        WriteEntry("content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0' unique-identifier='book'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='book'>sync-marker</dc:identifier><dc:title>Sync Marker Test</dc:title></metadata><manifest><item id='chapter' href='chapter.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='chapter'/></spine></package>");
        WriteEntry("chapter.xhtml", xhtml);
        return epub;

        void WriteEntry(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    private static T NativeField<T>(NativeReaderHost host, string name) =>
        (T)typeof(NativeReaderHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;

    private static T PdfField<T>(NativePdfReaderHost host, string name) =>
        (T)typeof(NativePdfReaderHost).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);
}
