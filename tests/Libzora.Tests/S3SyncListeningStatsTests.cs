using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task ListeningCountersConvergeWithoutDoubleCountingAndSurviveLegacySnapshots()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 120, 100, 10, 10, 120);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal(120, (await b.Reader.GetReadingStatsAsync(book.FileId))!.ListeningSeconds);
        await b.Reader.AddReadingTimeAsync(book.BookId, book.FileId, 60, 10, 1, 10, 60);
        for (var i = 0; i < 3; i++) { await b.SyncAsync(); await a.SyncAsync(); }
        foreach (var device in new[] { a, b })
        {
            var stats = (await device.Reader.GetReadingStatsAsync(book.FileId))!;
            Assert.Equal(180, stats.CumulativeSeconds);
            Assert.Equal(180, stats.ListeningSeconds);
            Assert.True(stats.HasFinished);
            var dashboard = await device.Reader.GetReadingDashboardAsync();
            Assert.Equal(180, dashboard.ListeningSeconds);
            Assert.Equal(180, dashboard.DailyReading.Sum(day => day.ListeningSeconds));
            Assert.Equal(1, dashboard.BooksFinished);
        }
        var old = bucket.Snapshot(a.SnapshotKey);
        foreach (var row in old.ReadingStats)
        {
            row.ListeningSecondsByDevice.Clear();
            row.ListeningSecondsByDateByDevice.Clear();
            row.HasFinished = false;
        }
        bucket.Objects[a.SnapshotKey] = MemoryBucket.Encode(old);
        await b.SyncAsync();
        Assert.Equal(180, (await b.Reader.GetReadingStatsAsync(book.FileId))!.ListeningSeconds);
        Assert.Equal(1, (await b.Reader.GetReadingDashboardAsync()).BooksFinished);
    }

    [Fact]
    public async Task ListeningAndCompletionSurviveReimportAndDoNotResurrectAfterReset()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var original = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2));
        await a.Reader.AddReadingTimeAsync(original.BookId, original.FileId, 120, 100, 10, 10, 120);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        await a.Library.DeleteAsync(original.BookId);
        await a.Library.PurgeTrashItemAsync(Assert.Single(await a.Library.GetTrashItemsAsync()).Id);
        var reimport = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow);
        await a.Reader.AddReadingTimeAsync(reimport.BookId, reimport.FileId, 60, 10, 1, 10, 60);
        await a.SyncAsync(); await b.SyncAsync(); await a.SyncAsync();
        Assert.Equal(180, (await a.Reader.GetReadingDashboardAsync()).ListeningSeconds);
        Assert.Equal(1, (await a.Reader.GetReadingDashboardAsync()).BooksFinished);
        await a.Reader.ResetReadingDataAsync();
        await a.SyncAsync(); await b.SyncAsync(); await a.SyncAsync();
        foreach (var device in new[] { a, b })
        {
            var dashboard = await device.Reader.GetReadingDashboardAsync();
            Assert.Equal(0, dashboard.TotalSeconds);
            Assert.Equal(0, dashboard.ListeningSeconds);
            Assert.Equal(0, dashboard.BooksFinished);
            Assert.Empty(dashboard.Books);
        }
    }

    [Fact]
    public async Task DuplicateFilesPreserveListeningAndCompletion()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        var canonical = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-1));
        var duplicate = await AddMatchingReadingBookAsync(a, DateTimeOffset.UtcNow.AddMinutes(-2), uppercaseHash: true);
        await a.Reader.AddReadingTimeAsync(canonical.BookId, canonical.FileId, 60, 10, 1, 10, 60);
        await a.Reader.AddReadingTimeAsync(duplicate.BookId, duplicate.FileId, 120, 100, 10, 10, 120);
        await a.SyncAsync();
        await using var b = await Device.CreateAsync(bucket);
        await b.SyncAsync();
        var dashboard = await b.Reader.GetReadingDashboardAsync();
        Assert.Equal(180, dashboard.TotalSeconds);
        Assert.Equal(180, dashboard.ListeningSeconds);
        Assert.Equal(180, dashboard.DailyReading.Sum(day => day.ListeningSeconds));
        Assert.Equal(1, dashboard.BooksFinished);
    }
}
