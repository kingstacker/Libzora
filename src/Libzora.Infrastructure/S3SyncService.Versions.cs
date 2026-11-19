using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Timestamp/version lifting and snapshot row comparison.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private static void PreserveSettingsTimestampsForUnchangedValues(
        S3SyncSettingsSnapshot? current,
        S3SyncSettingsSnapshot? previous)
    {
        if (current is null || previous is null) return;
        if (AppSyncSettingsEqual(current.App, previous.App))
            current.AppUpdatedAt = previous.AppUpdatedAt;
        if (AiSyncSettingsEqual(current.Ai, previous.Ai))
            current.AiUpdatedAt = previous.AiUpdatedAt;
        if (EmailSyncSettingsEqual(current.KindleEmail, previous.KindleEmail))
            current.KindleEmailUpdatedAt = previous.KindleEmailUpdatedAt;

        current.UpdatedAt = new[]
        {
            current.AppUpdatedAt ?? DateTimeOffset.MinValue,
            current.AiUpdatedAt ?? DateTimeOffset.MinValue,
            current.KindleEmailUpdatedAt ?? DateTimeOffset.MinValue
        }.Max();
    }

    private static void LiftLocalDeletionVersionsOverObservedRemoteRows(
        S3SyncSnapshot local,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        IReadOnlyCollection<S3SyncTombstone> localDeletions,
        DateTimeOffset existingWatermark)
    {
        if (localDeletions.Count == 0 || local.Tombstones.Count == 0) return;
        var localDeletionKeys = localDeletions
            .Select(tombstone => VersionKey(tombstone.EntityType, tombstone.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, version) in GetSnapshotOrderingVersions(local))
            remoteVersions[key] = version;
        foreach (var snapshot in remoteSnapshots)
        {
            foreach (var (key, version) in GetSnapshotOrderingVersions(snapshot))
                if (!remoteVersions.TryGetValue(key, out var existing) || version > existing)
                    remoteVersions[key] = version;
        }

        foreach (var tombstone in local.Tombstones)
        {
            var key = VersionKey(tombstone.EntityType, tombstone.Key);
            if (localDeletionKeys.Contains(key)
                && remoteVersions.TryGetValue(key, out var remoteVersion)
                && remoteVersion >= tombstone.DeletedAt)
            {
                // Wall clocks on two devices are not a reliable ordering
                // source. The delete was recorded locally after the last local
                // baseline, so make it newer than the stale remote row it is
                // intended to suppress.
                tombstone.DeletedAt = Max(remoteVersion, existingWatermark).AddTicks(1);
                tombstone.SyncVersion = null;
            }
        }
    }

    /// <summary>
    /// Ordering versions used to compare live rows with deletion records. A
    /// row's hybrid-clock version, not its semantic timestamp: captures stamp
    /// rows with the clock's physical floor, so a stale row can carry an old
    /// UpdatedAt while sharing the deletion instant's tick. Comparing only the
    /// timestamp lets a local deletion look newer than a row it must suppress,
    /// or older than a row that re-imported the deleted identity.
    /// </summary>
    private static Dictionary<string, DateTimeOffset> GetSnapshotOrderingVersions(S3SyncSnapshot snapshot)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in SyncEntities.Where(entity => entity.TrackDeletion))
        {
            foreach (var row in entity.Rows(snapshot))
            {
                var ticks = row.Value is ISyncVersionedRow versioned
                    ? S3SyncVersion.PhysicalTicks(versioned.SyncVersion, row.Version)
                    : row.Version.UtcTicks;
                var key = VersionKey(entity.Type, row.Key);
                if (!result.TryGetValue(key, out var existing) || ticks > existing.UtcTicks)
                    result[key] = new DateTimeOffset(
                        Math.Clamp(ticks, 0, DateTimeOffset.MaxValue.UtcTicks), TimeSpan.Zero);
            }
        }
        return result;
    }

    private static void LiftRemoteDeletionVersionsOverUnchangedLocalRows(
        S3SyncSnapshot local,
        S3SyncSnapshot? previousLocal,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots)
    {
        if (previousLocal is null || remoteSnapshots.Count == 0) return;
        var unchangedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in SyncEntities.Where(entity => entity.TrackDeletion))
        {
            var previous = entity.Rows(previousLocal).ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var row in entity.Rows(local))
                if (previous.TryGetValue(row.Key, out var old) && SnapshotRowsEqual(row.Value, old.Value))
                    unchangedKeys.Add(VersionKey(entity.Type, row.Key));
        }
        if (unchangedKeys.Count == 0) return;

        var remoteVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, version) in GetSnapshotOrderingVersions(local))
            remoteVersions[key] = version;
        foreach (var snapshot in remoteSnapshots)
        {
            foreach (var (key, version) in GetSnapshotOrderingVersions(snapshot))
                if (!remoteVersions.TryGetValue(key, out var existing) || version > existing)
                    remoteVersions[key] = version;
        }

        var tombstones = local.Tombstones.ToDictionary(
            tombstone => VersionKey(tombstone.EntityType, tombstone.Key),
            StringComparer.OrdinalIgnoreCase);
        var previouslyObservedDeletions = BuildTombstoneIndex(previousLocal.Tombstones ?? []);
        foreach (var remoteTombstone in remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones ?? []))
        {
            var key = VersionKey(remoteTombstone.EntityType, remoteTombstone.Key);
            if (!unchangedKeys.Contains(key)) continue;
            if (previouslyObservedDeletions.TryGetValue(key, out var observedDeletion)
                && !S3SyncVersion.IsNewer(
                    remoteTombstone.SyncVersion, remoteTombstone.DeletedAt,
                    observedDeletion.SyncVersion, observedDeletion.DeletedAt))
                continue;
            var observedVersion = remoteVersions.GetValueOrDefault(key, DateTimeOffset.MinValue);
            var deletionVersion = Max(remoteTombstone.DeletedAt, observedVersion).AddTicks(1);
            if (tombstones.TryGetValue(key, out var localTombstone))
            {
                if (deletionVersion > localTombstone.DeletedAt)
                {
                    localTombstone.DeletedAt = deletionVersion;
                    localTombstone.SyncVersion = null;
                }
            }
            else
            {
                localTombstone = new S3SyncTombstone
                {
                    EntityType = remoteTombstone.EntityType,
                    Key = remoteTombstone.Key,
                    DeletedAt = deletionVersion,
                    ReadingDataResetId = remoteTombstone.ReadingDataResetId
                };
                local.Tombstones.Add(localTombstone);
                tombstones[key] = localTombstone;
            }
        }
    }

    /// <summary>
    /// Structural row equality without materializing two UTF-16 JSON strings.
    /// Called for every row in large annotation/bookmark sets.
    /// </summary>
    private static bool SnapshotRowsEqual<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, JsonOptions)
            .AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, JsonOptions));

    private async Task<HashSet<string>> ResolveLocalRestorationsAsync(
        S3SyncSnapshot local,
        S3SyncSnapshot? previousLocal,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        CancellationToken cancellationToken)
    {
        if (local.Tombstones.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousBooks = (previousLocal?.Books ?? [])
            .ToDictionary(item => item.Id);
        var previousFiles = (previousLocal?.Files ?? [])
            .ToDictionary(item => item.Id);
        var deletionVersions = MergeTombstones(
                local.Tombstones,
                remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones ?? []))
            .GroupBy(tombstone => VersionKey(tombstone.EntityType, tombstone.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.DeletedAt), StringComparer.OrdinalIgnoreCase);
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updatedBookVersions = new Dictionary<Guid, DateTimeOffset>();
        var updatedFileVersions = new Dictionary<Guid, DateTimeOffset>();

        var localBooksById = local.Books.ToDictionary(item => item.Id);
        var localFilesByBook = local.Files.ToLookup(item => item.BookId);
        foreach (var book in local.Books)
        {
            var bookKey = VersionKey("book", book.Id);
            var changedSinceBaseline = !previousBooks.TryGetValue(book.Id, out var previous)
                || !SnapshotRowsEqual(book, previous);
            if (!changedSinceBaseline || !deletionVersions.TryGetValue(bookKey, out var bookDeletedAt))
                continue;

            resolved.Add(bookKey);
            var restoreVersion = bookDeletedAt;
            foreach (var file in localFilesByBook[book.Id])
            {
                var fileKey = VersionKey("file", file.Id);
                if (deletionVersions.TryGetValue(fileKey, out var fileDeletedAt))
                {
                    resolved.Add(fileKey);
                    if (fileDeletedAt > restoreVersion) restoreVersion = fileDeletedAt;
                }
            }
            restoreVersion = GetVersionAfter(book.UpdatedAt, restoreVersion);
            book.UpdatedAt = restoreVersion;
            book.SyncVersion = null;
            updatedBookVersions[book.Id] = restoreVersion;
            foreach (var file in localFilesByBook[book.Id])
                updatedFileVersions[file.Id] = GetVersionAfter(file.ModifiedAt, restoreVersion);
        }

        foreach (var file in local.Files)
        {
            var fileKey = VersionKey("file", file.Id);
            var changedSinceBaseline = !previousFiles.TryGetValue(file.Id, out var previous)
                || !SnapshotRowsEqual(file, previous);
            if (!changedSinceBaseline || !deletionVersions.TryGetValue(fileKey, out var deletedAt))
                continue;

            resolved.Add(fileKey);
            updatedFileVersions[file.Id] = GetVersionAfter(
                updatedFileVersions.GetValueOrDefault(file.Id, file.ModifiedAt), deletedAt);
            if (updatedBookVersions.ContainsKey(file.BookId)) continue;
            var book = localBooksById.GetValueOrDefault(file.BookId);
            if (book is null) continue;
            var restoreVersion = GetVersionAfter(book.UpdatedAt, deletedAt);
            book.UpdatedAt = restoreVersion;
            book.SyncVersion = null;
            updatedBookVersions[book.Id] = restoreVersion;
        }

        if (resolved.Count == 0) return resolved;
        foreach (var file in local.Files)
            if (updatedFileVersions.TryGetValue(file.Id, out var version))
            {
                file.ModifiedAt = version;
                file.SyncVersion = null;
            }

        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (bookId, updatedAt) in updatedBookVersions)
        {
            using var read = CreateCommand(connection, transaction, "SELECT UpdatedAt FROM Books WHERE Id = $id;");
            AddParameter(read, "$id", bookId.ToString());
            if (await read.ExecuteScalarAsync(cancellationToken) is not string current
                || ParseTimestamp(current) >= updatedAt)
                continue;
            using var update = CreateCommand(connection, transaction, """
                UPDATE Books SET UpdatedAt = $updatedAt
                WHERE Id = $bookId;
                """);
            AddParameter(update, "$updatedAt", updatedAt.ToString("O"));
            AddParameter(update, "$bookId", bookId.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var (fileId, updatedAt) in updatedFileVersions)
        {
            using var read = CreateCommand(connection, transaction, "SELECT SyncUpdatedAt FROM BookFiles WHERE Id = $id;");
            AddParameter(read, "$id", fileId.ToString());
            if (await read.ExecuteScalarAsync(cancellationToken) is not string current
                || ParseTimestamp(current) >= updatedAt)
                continue;
            using var update = CreateCommand(connection, transaction, "UPDATE BookFiles SET SyncUpdatedAt = $updatedAt WHERE Id = $id;");
            AddParameter(update, "$id", fileId.ToString());
            AddParameter(update, "$updatedAt", updatedAt.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var hasDeletionLog = CreateCommand(connection, transaction,
                   "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'S3SyncDeletionLog');"))
        {
            if (Convert.ToInt32(await hasDeletionLog.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                foreach (var versionKey in resolved)
                {
                    var separator = versionKey.IndexOf(':');
                    if (separator <= 0) continue;
                    var entityType = versionKey[..separator];
                    var entityId = versionKey[(separator + 1)..];
                    if (entityType is not ("book" or "file")) continue;
                    using var delete = CreateCommand(connection, transaction, """
                        DELETE FROM S3SyncDeletionLog
                        WHERE EntityType = $entityType
                          AND lower(replace(EntityKey, '-', '')) = $entityId;
                        """);
                    AddParameter(delete, "$entityType", entityType);
                    AddParameter(delete, "$entityId", entityId.ToLowerInvariant());
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return resolved;
    }

    private static DateTimeOffset GetVersionAfter(DateTimeOffset current, DateTimeOffset deletion) =>
        current > deletion ? current : deletion.AddTicks(1);

    private async Task ResetLocalBaselineCoreAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var markerPath = GetPendingBaselineResetPath();
        var markerTemporaryPath = markerPath + ".tmp";
        await File.WriteAllTextAsync(
            markerTemporaryPath,
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken);
        SettingsFile.Publish(markerTemporaryPath, markerPath);

        await ClearRecordedDeletionTimesAsync(cancellationToken);
        await SaveStateAsync(new S3SyncState
        {
            DeviceId = NormalizeDeviceId(deviceId),
            StorageIdentity = string.Empty,
            LastUploadedSnapshot = null,
            Tombstones = [],
            RemoteBookIds = [],
            RemoteOnlyFileHashes = []
        }, cancellationToken);
        try { File.Delete(markerPath); }
        catch (FileNotFoundException) { }
    }

    private string GetPendingBaselineResetPath() =>
        Path.Combine(_paths.Data, AppBackupService.PendingSyncBaselineResetFileName);

    private static bool AppSyncSettingsEqual(S3SyncAppSettings? left, S3SyncAppSettings? right)
    {
        if (left is null || right is null) return left is null && right is null;
        var leftValues = JsonSerializer.Serialize(new
        {
            left.UiLanguage,
            left.PreferredOpenFormat,
            left.AutoBackupEnabled,
            left.AutoGenerateEpubAndAzw3OnImport,
            left.CollectionsMutuallyExclusive,
            left.AutoBackupRetention,
            left.AiEnabled,
            left.AutoUpdateCheckEnabled,
            left.DevelopmentUpdateCheckEnabled,
            left.CompareKindleLibraryEnabled,
            left.GridGalleryDisplay,
            left.ShowSyncStatusIcon,
            left.ShowLibraryPresenceIcon,
            left.ShowUnearnedReadingMedals,
            left.ReadingMaterialsCollapsedByDefault,
            left.PinyinContextMenuEnabled,
            left.PinyinLocalOnly,
            left.PinyinEngineId,
            left.DefaultReaderLayout
        });
        var rightValues = JsonSerializer.Serialize(new
        {
            right.UiLanguage,
            right.PreferredOpenFormat,
            right.AutoBackupEnabled,
            right.AutoGenerateEpubAndAzw3OnImport,
            right.CollectionsMutuallyExclusive,
            right.AutoBackupRetention,
            right.AiEnabled,
            right.AutoUpdateCheckEnabled,
            right.DevelopmentUpdateCheckEnabled,
            right.CompareKindleLibraryEnabled,
            right.GridGalleryDisplay,
            right.ShowSyncStatusIcon,
            right.ShowLibraryPresenceIcon,
            right.ShowUnearnedReadingMedals,
            right.ReadingMaterialsCollapsedByDefault,
            right.PinyinContextMenuEnabled,
            right.PinyinLocalOnly,
            right.PinyinEngineId,
            right.DefaultReaderLayout
        });
        return string.Equals(leftValues, rightValues, StringComparison.Ordinal);
    }
}
