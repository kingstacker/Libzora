using System.Reflection;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile;
using Libzora.Mobile.Views;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class SyncedResumeMarkerMobileTests(MobileUiSession session)
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task InteractiveDragCancelRestoresResumeMarker_AndCommitClearsIt() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-marker-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        await modules.Reader.Data.InitializeAsync();
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "移动端同步标识" };
        var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", RelativePath = "book.epub" };
        book.Files = [file];
        var reader = new ReaderPage(book, modules, shell);
        using var host = new NativeReaderHost();

        try
        {
            var marker = new ReaderSyncedPositionMarker(
                book.Id,
                file.Id,
                "chapter.xhtml",
                null,
                0,
                0,
                25,
                1,
                "desktop-device",
                DateTimeOffset.UtcNow)
            {
                ContentPosition = new ReaderContentPosition { TextOffset = 120, PageIndex = 1 }
            };
            await InsertMarkerAsync(paths, marker);

            Set(reader, "_readerHost", host);
            Set(reader, "_bookFile", file);
            Set(reader, "_ready", true);
            Set(reader, "_activeSyncedMarker", marker);
            host.SetResumeMarker(new ReaderResumeMarker(0, 0, null, marker.ContentPosition));

            // Simulate interactive drag start hiding the marker on the incoming page, then cancelling.
            host.SetResumeMarker(null);
            Set(reader, "_interactiveDragDirection", 1);
            typeof(ReaderPage).GetMethod("RevertPendingDragScroll", PrivateInstance)!.Invoke(reader, null);
            Assert.NotNull(HostField<ReaderResumeMarker?>(host, "_resumeMarker"));
            Assert.NotNull(await modules.Reader.Data.GetSyncedPositionMarkerAsync(file.Id));

            // Now simulate committing the drag -> marker clears from host and SQLite.
            typeof(ReaderPage).GetMethod("CommitPendingDragScroll", PrivateInstance)!.Invoke(reader, null);
            Assert.Null(Field<ReaderSyncedPositionMarker?>(reader, "_activeSyncedMarker"));
            Assert.Null(HostField<ReaderResumeMarker?>(host, "_resumeMarker"));
            await WaitUntilAsync(async () => await modules.Reader.Data.GetSyncedPositionMarkerAsync(file.Id) is null);
        }
        finally
        {
            ((CancellationTokenSource)Field<CancellationTokenSource>(reader, "_lifetime")).Cancel();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    [Fact]
    public Task ContinuousScrollClearsResumeMarkerAfterThreshold() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-scroll-marker-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        await modules.Reader.Data.InitializeAsync();
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "滚屏同步标识" };
        var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", RelativePath = "book.epub" };
        book.Files = [file];
        var reader = new ReaderPage(book, modules, shell);
        using var host = new NativeReaderHost();

        try
        {
            var marker = new ReaderSyncedPositionMarker(
                book.Id, file.Id, "chapter.xhtml", null, 0, 100, 15, 0, "desktop-device", DateTimeOffset.UtcNow)
            {
                ContentPosition = new ReaderContentPosition { TextOffset = 80, PageIndex = 0 }
            };
            await InsertMarkerAsync(paths, marker);

            Set(reader, "_readerHost", host);
            Set(reader, "_bookFile", file);
            Set(reader, "_ready", true);
            Set(reader, "_layout", new ReaderLayoutSettings(FlowMode: 0));
            Set(reader, "_activeSyncedMarker", marker);
            Set(reader, "_syncedMarkerScrollOrigin", 100d);
            host.SetResumeMarker(new ReaderResumeMarker(0, 100, null, marker.ContentPosition));

            var applyScroll = typeof(ReaderPage).GetMethod("ApplyScrollUpdate", PrivateInstance)!;
            // Small jitter does not clear the marker.
            applyScroll.Invoke(reader, [120d, 0d]);
            Assert.NotNull(HostField<ReaderResumeMarker?>(host, "_resumeMarker"));

            // Scrolling far past the origin clears it.
            applyScroll.Invoke(reader, [900d, 0d]);
            Assert.Null(Field<ReaderSyncedPositionMarker?>(reader, "_activeSyncedMarker"));
            Assert.Null(HostField<ReaderResumeMarker?>(host, "_resumeMarker"));
            await WaitUntilAsync(async () => await modules.Reader.Data.GetSyncedPositionMarkerAsync(file.Id) is null);
        }
        finally
        {
            ((CancellationTokenSource)Field<CancellationTokenSource>(reader, "_lifetime")).Cancel();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    private static async Task InsertMarkerAsync(AppPaths paths, ReaderSyncedPositionMarker marker)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReaderSyncedPositionMarkers (
                BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                ProgressPercent, FlowMode, ContentPositionJson, SourceDeviceId, SyncedAt)
            VALUES (
                $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                $progressPercent, $flowMode, $contentPosition, $sourceDeviceId, $syncedAt);
            """;
        command.Parameters.AddWithValue("$bookFileId", marker.BookFileId.ToString());
        command.Parameters.AddWithValue("$bookId", marker.BookId.ToString());
        command.Parameters.AddWithValue("$chapterPath", marker.ChapterPath);
        command.Parameters.AddWithValue("$fragment", (object?)marker.Fragment ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterIndex", marker.ChapterIndex);
        command.Parameters.AddWithValue("$scrollPosition", marker.ScrollPosition);
        command.Parameters.AddWithValue("$progressPercent", marker.ProgressPercent);
        command.Parameters.AddWithValue("$flowMode", marker.FlowMode);
        command.Parameters.AddWithValue("$contentPosition", System.Text.Json.JsonSerializer.Serialize(marker.ContentPosition));
        command.Parameters.AddWithValue("$sourceDeviceId", marker.SourceDeviceId);
        command.Parameters.AddWithValue("$syncedAt", marker.SyncedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.True(await condition());
    }

    private static T Field<T>(ReaderPage reader, string name) =>
        (T)typeof(ReaderPage).GetField(name, PrivateInstance)!.GetValue(reader)!;

    private static T HostField<T>(NativeReaderHost host, string name) =>
        (T)typeof(NativeReaderHost).GetField(name, PrivateInstance)!.GetValue(host)!;

    private static void Set(ReaderPage reader, string name, object? value) =>
        typeof(ReaderPage).GetField(name, PrivateInstance)!.SetValue(reader, value);

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
