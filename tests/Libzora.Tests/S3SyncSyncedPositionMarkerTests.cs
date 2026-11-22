using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task RemoteProgressFromAnotherDevice_CreatesSyncedPositionMarker_AndClearRemovesIt()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var bookA = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2));
        var bookB = await AddMatchingReadingBookAsync(b, DateTimeOffset.UtcNow.AddMinutes(-3));

        var contentPos = new ReaderContentPosition
        {
            TextOffset = 420,
            PageIndex = 2,
            RunY = 18,
            SliceFraction = 0.25
        };
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(
            bookA.BookId,
            bookA.FileId,
            "chapter.xhtml",
            null,
            1,
            2592,
            35.5,
            1,
            DateTimeOffset.UtcNow)
        {
            ContentPosition = contentPos
        });

        await a.SyncAsync();
        await b.SyncAsync();

        // Device B received a newer progress from Device A -> marker should exist on Device B.
        var markerB = await b.Reader.GetSyncedPositionMarkerAsync(bookB.FileId);
        Assert.NotNull(markerB);
        Assert.Equal(bookB.BookId, markerB.BookId);
        Assert.Equal(bookB.FileId, markerB.BookFileId);
        Assert.Equal(1, markerB.ChapterIndex);
        Assert.Equal(contentPos, markerB.ContentPosition);
        Assert.Equal(a.Id, markerB.SourceDeviceId);

        // Device A's own progress must never create a marker on Device A.
        Assert.Null(await a.Reader.GetSyncedPositionMarkerAsync(bookA.FileId));

        // Consuming the marker removes it, and a subsequent sync with no new change does not recreate it.
        await b.Reader.ClearSyncedPositionMarkerAsync(bookB.FileId);
        Assert.Null(await b.Reader.GetSyncedPositionMarkerAsync(bookB.FileId));

        await b.SyncAsync();
        Assert.Null(await b.Reader.GetSyncedPositionMarkerAsync(bookB.FileId));
    }

    [Fact]
    public async Task RemoteProgressAtSamePosition_DoesNotCreateSyncedPositionMarker()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var bookA = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-3));
        var bookB = await AddMatchingReadingBookAsync(b, DateTimeOffset.UtcNow.AddMinutes(-3));

        var sharedPos = new ReaderContentPosition { TextOffset = 200, PageIndex = 1 };
        await b.Reader.SaveProgressAsync(new ReaderProgressRow(
            bookB.BookId, bookB.FileId, "chapter.xhtml", null, 0, 1296, 20, 1, DateTimeOffset.UtcNow.AddMinutes(-2))
        {
            ContentPosition = sharedPos
        });
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(
            bookA.BookId, bookA.FileId, "chapter.xhtml", null, 0, 1296, 20, 1, DateTimeOffset.UtcNow)
        {
            ContentPosition = sharedPos
        });

        await a.SyncAsync();
        await b.SyncAsync();

        var progressB = await b.Reader.GetProgressAsync(bookB.FileId);
        Assert.NotNull(progressB);
        Assert.Null(await b.Reader.GetSyncedPositionMarkerAsync(bookB.FileId));
    }

    [Fact]
    public async Task ReadingDataReset_AndProgressTombstone_ClearSyncedPositionMarker()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(
            book.BookId, book.FileId, "chapter.xhtml", null, 2, 1800, 45, 1, DateTimeOffset.UtcNow)
        {
            ContentPosition = new ReaderContentPosition { TextOffset = 300, PageIndex = 1 }
        });

        await a.SyncAsync();
        await b.SyncAsync();
        Assert.NotNull(await b.Reader.GetSyncedPositionMarkerAsync(book.FileId));

        await b.Reader.ResetReadingDataAsync();
        Assert.Null(await b.Reader.GetSyncedPositionMarkerAsync(book.FileId));
    }

    [Fact]
    public async Task SameDeviceSnapshotReapplied_DoesNotCreateSyncedPositionMarker()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(
            book.BookId, book.FileId, "chapter.xhtml", null, 1, 900, 30, 1, DateTimeOffset.UtcNow)
        {
            ContentPosition = new ReaderContentPosition { TextOffset = 150, PageIndex = 1 }
        });
        await a.SyncAsync();

        // Simulate local progress row removal without changing DeviceId, then re-syncing own snapshot.
        await a.SqlAsync("DELETE FROM ReaderProgress;");
        await a.Reader.ClearSyncedPositionMarkerAsync(book.FileId);
        await a.Service.ResetLocalBaselineAsync(a.Id);
        await a.SyncAsync();

        Assert.NotNull(await a.Reader.GetProgressAsync(book.FileId));
        Assert.Null(await a.Reader.GetSyncedPositionMarkerAsync(book.FileId));
    }
}
