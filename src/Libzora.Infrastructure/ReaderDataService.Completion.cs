using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

public sealed partial class ReaderDataService
{
    internal static async Task EnsureCompletionColumnAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = "PRAGMA table_info(ReaderReadingStats);";
        await using (var rows = await inspect.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
                if (rows.GetString(1) == "HasFinished") return;
        using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            ALTER TABLE ReaderReadingStats ADD COLUMN HasFinished INTEGER NOT NULL DEFAULT 0;
            UPDATE ReaderReadingStats SET HasFinished=1
            WHERE ProgressPercent >= 99.5 OR EXISTS (
                SELECT 1 FROM ReaderReadingSessions r
                WHERE r.BookFileId=ReaderReadingStats.BookFileId AND r.ProgressPercent >= 99.5);
            """;
        await migrate.ExecuteNonQueryAsync(token);
        using var library = connection.CreateCommand();
        library.Transaction = transaction;
        library.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='Books');";
        var hasLibrary = Convert.ToInt32(await library.ExecuteScalarAsync(token)) != 0;
        library.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='S3SyncLocalMetadata');";
        var mayImportManualStatuses = Convert.ToInt32(await library.ExecuteScalarAsync(token)) == 0
            || await ReaderReadingDataReset.ReadAsync(connection, transaction, token) is null;
        if (hasLibrary && mayImportManualStatuses)
        {
            // Import explicit legacy statuses once. Reopening after a reading
            // reset must not derive completions from unchanged book metadata.
            library.CommandText = """
                UPDATE ReaderReadingStats SET HasFinished=1
                WHERE BookId IN (SELECT Id FROM Books WHERE ReadingStatus=2);
                INSERT OR IGNORE INTO ReaderReadingStats
                    (BookFileId,BookId,CumulativeSeconds,ListeningSeconds,HasFinished,ProgressPercent,CompletedChapters,TotalChapters,UpdatedAt)
                SELECT MIN(f.Id),b.Id,0,0,1,0,0,0,b.UpdatedAt
                FROM Books b JOIN BookFiles f ON f.BookId=b.Id
                WHERE b.ReadingStatus=2 AND NOT EXISTS (SELECT 1 FROM ReaderReadingStats s WHERE s.BookId=b.Id)
                GROUP BY b.Id;
                """;
            await library.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    /// <summary>Records a completion without changing the reading position or inventing reading time.</summary>
    public async Task MarkBookFinishedAsync(Book book, CancellationToken token = default)
    {
        var file = book.Files.FirstOrDefault();
        if (file is null) return;
        var stats = await GetReadingStatsAsync(file.Id, token);
        var progress = await GetProgressAsync(file.Id, token);
        await AddReadingTimeCoreAsync(book.Id, file.Id, 0, 0,
            stats?.ProgressPercent ?? progress?.ProgressPercent ?? 0,
            stats?.CompletedChapters ?? progress?.ChapterIndex ?? 0, stats?.TotalChapters ?? 0,
            DateTimeOffset.UtcNow, null, token, hasFinished: true);
    }
}
