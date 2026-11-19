using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Connection, deletion-tracking setup and recorded times.
/// </summary>
public sealed partial class S3SyncService
{
    private string DatabaseConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.Database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        DefaultTimeout = 30
    }.ToString();

    private async Task<SqliteConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(DatabaseConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            PRAGMA cache_size = -65536;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Installs the local deletion journal used to preserve the time at which
    /// a row was actually removed. The journal is intentionally local and is
    /// never included in a sync snapshot.
    /// </summary>
    public async Task InitializeDeletionTrackingAsync(
        CancellationToken cancellationToken = default,
        string? deviceId = null)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        if (deviceId is not null)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ReadingTimeSyncTracker.ConfigureDeviceAsync(connection, transaction, NormalizeDeviceId(deviceId), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task EnsureDeletionTrackingSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await BookFileSyncVersion.EnsureAsync(connection, cancellationToken);
        await ReaderRecoveryEpoch.EnsureAsync(connection, null, cancellationToken);
        await EnsureLocalCleanupSchemaAsync(connection, cancellationToken);
        await ReaderReadingHistory.EnsureAsync(connection, cancellationToken);
        await ReadingMaterialBookSchema.EnsureAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        using (var control = CreateCommand(connection, transaction, """
            CREATE TABLE IF NOT EXISTS S3SyncDeletionControl (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                SchemaVersion INTEGER NOT NULL DEFAULT 0,
                Suppressed INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO S3SyncDeletionControl (Id) VALUES (1);
            """))
            await control.ExecuteNonQueryAsync(cancellationToken);
        await ReadingTimeSyncTracker.EnsureSchemaAsync(connection, transaction, cancellationToken);
        using (var version = CreateCommand(connection, transaction,
                   "SELECT SchemaVersion FROM S3SyncDeletionControl WHERE Id = 1;"))
        {
            if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken)) >= 3)
            {
                await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_Books;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_BookFiles;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_BookCollections;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_BookCollectionItems;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderAnnotations;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderBookReflections;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderProgress;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderBookmarks;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderLayoutSettings;
            DROP TRIGGER IF EXISTS S3SyncDeletionLog_ReaderReadingStats;
            CREATE TABLE IF NOT EXISTS S3SyncDeletionLog (
                EntityType TEXT NOT NULL,
                EntityKey TEXT NOT NULL,
                DeletedAt TEXT NOT NULL,
                PRIMARY KEY (EntityType, EntityKey)
            );
            CREATE INDEX IF NOT EXISTS IX_S3SyncDeletionLog_DeletedAt
                ON S3SyncDeletionLog(DeletedAt);

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_Books
            AFTER DELETE ON Books
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('book', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_BookFiles
            AFTER DELETE ON BookFiles
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('file', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_BookCollections
            AFTER DELETE ON BookCollections
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('collection', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_BookCollectionItems
            AFTER DELETE ON BookCollectionItems
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('collection-item', OLD.CollectionId || '|' || OLD.BookId,
                        strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderAnnotations
            AFTER DELETE ON ReaderAnnotations
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('annotation', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderBookReflections
            AFTER DELETE ON ReaderBookReflections
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('reflection', OLD.BookId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderProgress
            AFTER DELETE ON ReaderProgress
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('progress', OLD.BookFileId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderBookmarks
            AFTER DELETE ON ReaderBookmarks
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('bookmark', OLD.Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderLayoutSettings
            AFTER DELETE ON ReaderLayoutSettings
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('layout', OLD.BookFileId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;

            CREATE TRIGGER IF NOT EXISTS S3SyncDeletionLog_ReaderReadingStats
            AFTER DELETE ON ReaderReadingStats
            WHEN (SELECT Suppressed FROM S3SyncDeletionControl WHERE Id = 1) = 0
            BEGIN
                INSERT OR REPLACE INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
                VALUES ('stats', OLD.BookFileId, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            END;
            UPDATE S3SyncDeletionControl SET SchemaVersion = 3 WHERE Id = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, DateTimeOffset>> ReadRecordedDeletionTimesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   "SELECT EntityType, EntityKey, DeletedAt FROM S3SyncDeletionLog;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var entityType = NormalizeTombstoneEntityType(reader.GetString(0));
                var entityKey = NormalizeTombstoneKey(entityType, reader.GetString(1));
                var deletedAt = ParseTimestamp(reader.GetString(2));
                if (entityType.Length == 0
                    || entityKey.Length == 0
                    || deletedAt == DateTimeOffset.MinValue)
                    continue;

                var key = VersionKey(entityType, entityKey);
                if (!result.TryGetValue(key, out var existing) || deletedAt > existing)
                    result[key] = deletedAt;
            }
        }

        // There is no device-retirement/acknowledgement protocol yet. Retain
        // deletions while old snapshots can still be read from the bucket.
        return result;
    }

    private static async Task SuppressDeletionTrackingAsync(
        SqliteConnection connection, SqliteTransaction transaction, bool suppressed, CancellationToken cancellationToken)
    {
        // This flag only exists inside the writer transaction. Other writers
        // see zero after commit, or the old zero if the transaction rolls back.
        using var command = CreateCommand(connection, transaction,
            "UPDATE S3SyncDeletionControl SET Suppressed = $value WHERE Id = 1;");
        AddParameter(command, "$value", suppressed ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ClearRecordedDeletionTimesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        using (var exists = CreateCommand(
                   connection,
                   null,
                   "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'S3SyncDeletionLog');"))
        {
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                return;
        }
        using var command = CreateCommand(connection, null, "DELETE FROM S3SyncDeletionLog;");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ClearRecordedDeletionTimesThroughAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        using var command = CreateCommand(connection, null, """
            DELETE FROM S3SyncDeletionLog
            WHERE julianday(DeletedAt) <= julianday($cutoff);
            """);
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
