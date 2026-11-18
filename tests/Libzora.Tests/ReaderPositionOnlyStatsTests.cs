using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Tests;

public sealed class ReaderPositionOnlyStatsTests
{
    [Fact]
    public async Task PositionOnlySaveUpdatesProgressWithoutAddingTimeOrSessions()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var book = Guid.NewGuid();
            var file = Guid.NewGuid();
            await reader.AddReadingTimeAsync(book, file, 5, 25, 1, 4);

            await reader.AddReadingTimeAsync(book, file, 0, 100, 4, 4);

            var stats = (await reader.GetReadingStatsAsync(file))!;
            Assert.Equal(5, stats.CumulativeSeconds);
            Assert.Equal(100, stats.ProgressPercent);
            Assert.Equal(4, stats.CompletedChapters);
            Assert.Equal(4, stats.TotalChapters);
            Assert.Equal(1, await ScalarAsync(paths, "SELECT COUNT(*) FROM ReaderReadingSessions;"));
            Assert.Equal(5, await ScalarAsync(paths, "SELECT COALESCE(SUM(Seconds), 0) FROM S3SyncReadingDayCounters;"));
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task FirstPositionOnlySaveCreatesMetadataButNoReadingSessionOrDay()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var file = Guid.NewGuid();

            await reader.AddReadingTimeAsync(Guid.NewGuid(), file, 0, 80, 3, 5);

            var stats = (await reader.GetReadingStatsAsync(file))!;
            Assert.Equal(0, stats.CumulativeSeconds);
            Assert.Equal(80, stats.ProgressPercent);
            Assert.Equal(3, stats.CompletedChapters);
            Assert.Equal(5, stats.TotalChapters);
            Assert.Equal(0, await ScalarAsync(paths, "SELECT COUNT(*) FROM ReaderReadingSessions;"));
            Assert.Equal(0, await ScalarAsync(paths, "SELECT COUNT(*) FROM S3SyncReadingDayCounters;"));
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task NegativeTimeRemainsANoOp()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var reader = new ReaderDataService(new AppPaths(root));
            await reader.InitializeAsync();
            var file = Guid.NewGuid();
            await reader.AddReadingTimeAsync(Guid.NewGuid(), file, -1, 100, 4, 4);
            Assert.Null(await reader.GetReadingStatsAsync(file));
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task OldTimeRecoveryDoesNotRegressANewerZeroSecondPositionOrCountTwice()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var file = Guid.NewGuid();
            var book = Guid.NewGuid();
            var recovery = new ReaderReadingTimeRecoveryService(paths, reader);
            await recovery.SaveAsync(book, file, 30, 25, 1, 4, new CancellationToken(true));
            var queuePath = Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName);
            var queued = await File.ReadAllTextAsync(queuePath);
            await reader.AddReadingTimeAsync(book, file, 0, 90, 3, 4);

            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            await File.WriteAllTextAsync(queuePath, queued);
            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();

            var stats = (await reader.GetReadingStatsAsync(file))!;
            Assert.Equal(30, stats.CumulativeSeconds);
            Assert.Equal(90, stats.ProgressPercent);
            Assert.Equal(3, stats.CompletedChapters);
            Assert.Equal(1, await ScalarAsync(paths, "SELECT COUNT(*) FROM ReaderReadingSessions;"));
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    private static async Task<long> ScalarAsync(AppPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
