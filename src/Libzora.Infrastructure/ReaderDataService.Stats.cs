using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Reading stats and the reading dashboard.
/// </summary>
public sealed partial class ReaderDataService
{
    public async Task<ReaderReadingStats?> GetReadingStatsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt
            FROM ReaderReadingStats
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderReadingStats
        {
            BookId = Guid.Parse(reader.GetString(0)),
            BookFileId = Guid.Parse(reader.GetString(1)),
            CumulativeSeconds = reader.GetInt64(2),
            ProgressPercent = reader.GetDouble(3),
            CompletedChapters = reader.GetInt32(4),
            TotalChapters = reader.GetInt32(5),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    public async Task SaveReadingStatsAsync(ReaderReadingStats stats, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ReaderReadingStats (
                    BookFileId, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $cumulativeSeconds, $progressPercent, $completedChapters, $totalChapters, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, CumulativeSeconds=MAX(CumulativeSeconds, $cumulativeSeconds), ProgressPercent=$progressPercent,
                    CompletedChapters=$completedChapters, TotalChapters=$totalChapters, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", stats.BookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", stats.BookId.ToString());
            command.Parameters.AddWithValue("$cumulativeSeconds", stats.CumulativeSeconds);
            command.Parameters.AddWithValue("$progressPercent", stats.ProgressPercent);
            command.Parameters.AddWithValue("$completedChapters", stats.CompletedChapters);
            command.Parameters.AddWithValue("$totalChapters", stats.TotalChapters);
            command.Parameters.AddWithValue("$updatedAt", stats.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await ReadingTimeSyncTracker.RecordCurrentTotalAsync(connection, transaction, stats.BookFileId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.ReadingStats);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<ReadingDashboard> GetReadingDashboardAsync(
        int recentLimit = 12,
        CancellationToken cancellationToken = default,
        int dayCount = 14,
        DateOnly? throughDate = null)
    {
        dayCount = Math.Clamp(dayCount, 1, 366);
        var lastDay = throughDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var librarySchema = connection.CreateCommand();
        librarySchema.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Books');";
        var hasLibrary = Convert.ToInt32(await librarySchema.ExecuteScalarAsync(cancellationToken)) != 0;
        using var history = connection.CreateCommand();
        history.CommandText = hasLibrary ? """
            SELECT s.BookId, s.BookFileId, s.ProgressPercent, s.CumulativeSeconds, s.UpdatedAt,
                   COALESCE(b.Title, h.Title, ''), b.Id IS NOT NULL
            FROM ReaderReadingStats s
            LEFT JOIN ReaderReadingHistory h ON h.BookFileId = s.BookFileId
            LEFT JOIN Books b ON b.Id = s.BookId;
            """ : """
            SELECT s.BookId, s.BookFileId, s.ProgressPercent, s.CumulativeSeconds, s.UpdatedAt,
                   COALESCE(h.Title, ''), 0
            FROM ReaderReadingStats s
            LEFT JOIN ReaderReadingHistory h ON h.BookFileId = s.BookFileId;
            """;
        var fileHistory = new List<ReadingDashboardBook>();
        await using (var reader = await history.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                fileHistory.Add(new ReadingDashboardBook(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetDouble(2),
                    reader.GetInt64(3), DateTimeOffset.Parse(reader.GetString(4)))
                {
                    Title = reader.GetString(5), IsInLibrary = reader.GetInt32(6) != 0
                });
        }
        // A book can have several formats. Sum their time, but use the most
        // recently read format for its progress and count the book only once.
        var books = fileHistory.GroupBy(item => item.BookId)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.BookFileId).First() with
            {
                CumulativeSeconds = group.Sum(item => item.CumulativeSeconds),
                Title = group.Select(item => item.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? string.Empty
            })
            .OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.BookId).ToArray();
        var started = books.Length;
        var finished = books.Count(item => item.ProgressPercent >= 99.5);
        var seconds = books.Sum(item => item.CumulativeSeconds);
        var average = books.Length == 0 ? 0 : books.Average(item => item.ProgressPercent);

        var counts = connection.CreateCommand();
        counts.CommandText = hasLibrary ? """
            SELECT
                (SELECT COUNT(*) FROM ReaderBookmarks r
                    JOIN BookFiles f ON f.Id = r.BookFileId AND f.BookId = r.BookId
                    JOIN Books b ON b.Id = f.BookId),
                (SELECT COUNT(*) FROM ReaderAnnotations r
                    JOIN BookFiles f ON f.Id = r.BookFileId AND f.BookId = r.BookId
                    JOIN Books b ON b.Id = f.BookId);
            """ : "SELECT (SELECT COUNT(*) FROM ReaderBookmarks), (SELECT COUNT(*) FROM ReaderAnnotations);";
        int bookmarks;
        int annotations;
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            bookmarks = reader.GetInt32(0);
            annotations = reader.GetInt32(1);
        }

        var recentBooks = books.Take(Math.Clamp(recentLimit, 1, 100)).ToArray();

        var firstDay = lastDay.AddDays(-(dayCount - 1));
        var dailyCommand = connection.CreateCommand();
        dailyCommand.CommandText = """
            SELECT ReadingDate, COALESCE(SUM(Seconds), 0)
            FROM S3SyncReadingDayCounters
            WHERE ReadingDate >= $cutoff AND ReadingDate <= $lastDay
            GROUP BY ReadingDate
            ORDER BY ReadingDate;
            """;
        dailyCommand.Parameters.AddWithValue("$cutoff", firstDay.ToString("yyyy-MM-dd"));
        dailyCommand.Parameters.AddWithValue("$lastDay", lastDay.ToString("yyyy-MM-dd"));
        var dailyValues = new Dictionary<DateOnly, long>();
        await using (var reader = await dailyCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (DateOnly.TryParse(reader.GetString(0), out var day))
                    dailyValues[day] = reader.GetInt64(1);
            }
        }
        var dailyReading = Enumerable.Range(0, dayCount)
            .Select(offset => firstDay.AddDays(offset))
            .Select(day => new ReadingDashboardDay(day, dailyValues.GetValueOrDefault(day)))
            .ToArray();

        return new ReadingDashboard(started, finished, seconds, average, bookmarks, annotations, recentBooks, dailyReading)
        {
            Books = books,
            MostReadBooks = books.OrderByDescending(item => item.CumulativeSeconds)
                .ThenByDescending(item => item.UpdatedAt).Take(8).ToArray()
        };
    }

    public Task AddReadingTimeAsync(
        Guid bookId,
        Guid bookFileId,
        long activeSeconds,
        double progressPercent,
        int completedChapters,
        int totalChapters,
        CancellationToken cancellationToken = default) =>
        AddReadingTimeCoreAsync(bookId, bookFileId, activeSeconds, progressPercent, completedChapters, totalChapters,
            DateTimeOffset.UtcNow, null, cancellationToken);

    internal Task AddRecoveredReadingTimeAsync(ReadingTimeCheckpoint checkpoint, CancellationToken token) =>
        AddReadingTimeCoreAsync(checkpoint.BookId, checkpoint.BookFileId, checkpoint.Seconds, checkpoint.ProgressPercent,
            checkpoint.CompletedChapters, checkpoint.TotalChapters, checkpoint.RecordedAt, checkpoint, token);

    private async Task AddReadingTimeCoreAsync(Guid bookId, Guid bookFileId, long activeSeconds, double progressPercent,
        int completedChapters, int totalChapters, DateTimeOffset recordedAt, ReadingTimeCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        // A position-only save must refresh the dashboard even when the
        // activity clock has not accumulated another whole second. Recovery
        // checkpoints still represent positive, idempotently applied time.
        if (activeSeconds < 0 || checkpoint is not null && activeSeconds == 0) return;
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            if (checkpoint is not null) await MakeRecoveryCommitDurableAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (checkpoint is not null)
            {
                if (await ReaderRecoveryEpoch.ReadAsync(connection, transaction, cancellationToken) != checkpoint.Context) return;
                using var applied = connection.CreateCommand();
                applied.Transaction = transaction;
                applied.CommandText = "INSERT OR IGNORE INTO ReaderReadingTimeRecoveryApplied (Id) VALUES ($id);";
                applied.Parameters.AddWithValue("$id", checkpoint.Id.ToString("N"));
                if (await applied.ExecuteNonQueryAsync(cancellationToken) == 0) return;
            }
            var applyMetadata = true;
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT UpdatedAt FROM ReaderReadingStats WHERE BookFileId=$id;";
                current.Parameters.AddWithValue("$id", bookFileId.ToString());
                if (await current.ExecuteScalarAsync(cancellationToken) is string version)
                    applyMetadata = DateTimeOffset.Parse(version) <= recordedAt;
            }
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ReaderReadingStats (
                    BookFileId, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $seconds, $progressPercent, $completedChapters, $totalChapters, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    CumulativeSeconds = CumulativeSeconds + $seconds,
                    ProgressPercent = CASE WHEN $applyMetadata=1 THEN $progressPercent ELSE ProgressPercent END,
                    CompletedChapters = CASE WHEN $applyMetadata=1 THEN $completedChapters ELSE CompletedChapters END,
                    TotalChapters = CASE WHEN $applyMetadata=1 THEN $totalChapters ELSE TotalChapters END,
                    UpdatedAt = CASE WHEN $applyMetadata=1 THEN $updatedAt ELSE UpdatedAt END;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$seconds", activeSeconds);
            command.Parameters.AddWithValue("$progressPercent", progressPercent);
            command.Parameters.AddWithValue("$completedChapters", completedChapters);
            command.Parameters.AddWithValue("$totalChapters", totalChapters);
            command.Parameters.AddWithValue("$updatedAt", recordedAt.ToString("O"));
            command.Parameters.AddWithValue("$applyMetadata", applyMetadata ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (activeSeconds > 0)
            {
                using var session = connection.CreateCommand();
                session.Transaction = transaction;
                session.CommandText = """
                    INSERT INTO ReaderReadingSessions (
                        BookId, BookFileId, ActiveSeconds, ProgressPercent, RecordedAt)
                    VALUES ($bookId, $bookFileId, $seconds, $progressPercent, $recordedAt);
                    """;
                session.Parameters.AddWithValue("$bookId", bookId.ToString());
                session.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
                session.Parameters.AddWithValue("$seconds", activeSeconds);
                session.Parameters.AddWithValue("$progressPercent", progressPercent);
                session.Parameters.AddWithValue("$recordedAt", recordedAt.ToString("O"));
                await session.ExecuteNonQueryAsync(cancellationToken);
                await ReadingTimeSyncTracker.RecordReadingDayAsync(
                    connection, transaction, bookFileId, recordedAt, activeSeconds, cancellationToken);
                await ReadingTimeSyncTracker.RecordCurrentTotalAsync(connection, transaction, bookFileId, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.ReadingStats);
        }
        finally
        {
            _databaseGate.Release();
        }
    }
}
