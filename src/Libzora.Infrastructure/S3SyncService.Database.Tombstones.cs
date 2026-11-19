using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Deletion detection, safety checks and merge result types.
/// </summary>
public sealed partial class S3SyncService
{
    private static List<S3SyncTombstone> DetectDeletedEntities(
        S3SyncSnapshot? previous,
        S3SyncSnapshot current)
        => DetectDeletedEntitiesCore(previous, current, null);

    private static Dictionary<string, DateTimeOffset> GetSnapshotEntityVersions(S3SyncSnapshot snapshot)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in SyncEntities.Where(entity => entity.TrackDeletion))
            foreach (var row in entity.Rows(snapshot))
                result[VersionKey(entity.Type, row.Key)] = row.Version;
        return result;
    }

    private static List<S3SyncTombstone> GetRecordedTombstones(
        IReadOnlyDictionary<string, DateTimeOffset> recorded,
        S3SyncSnapshot current,
        S3SyncSnapshot? previous = null)
    {
        var live = GetSnapshotEntityVersions(current);
        var previousVersions = previous is null ? null : GetSnapshotEntityVersions(previous);
        var result = new List<S3SyncTombstone>();
        foreach (var (key, deletedAt) in recorded)
        {
            var separator = key.IndexOf(':');
            if (separator <= 0) continue;
            var entityType = key[..separator];
            var sameGeneration = !IsResetReadingEntity(entityType)
                || previous?.ReadingDataReset == current.ReadingDataReset;
            if (live.ContainsKey(key)
                || (sameGeneration && previousVersions?.TryGetValue(key, out var version) == true && version > deletedAt))
                continue;
            result.Add(new S3SyncTombstone
            {
                EntityType = entityType, Key = key[(separator + 1)..], DeletedAt = deletedAt,
                ReadingDataResetId = IsResetReadingEntity(entityType) ? current.ReadingDataReset?.Id : null
            });
        }
        return result;
    }

    private static List<S3SyncTombstone> DetectDeletedEntitiesWithRecordedTimes(
        S3SyncSnapshot? previous,
        S3SyncSnapshot current,
        IReadOnlyDictionary<string, DateTimeOffset> recordedDeletionTimes)
        => DetectDeletedEntitiesCore(previous, current, recordedDeletionTimes);

    private static List<S3SyncTombstone> DetectDeletedEntitiesCore(
        S3SyncSnapshot? previous,
        S3SyncSnapshot current,
        IReadOnlyDictionary<string, DateTimeOffset>? recordedDeletionTimes)
    {
        if (previous is null) return [];
        var result = new List<S3SyncTombstone>();
        var sameReadingGeneration = previous.ReadingDataReset == current.ReadingDataReset;

        foreach (var entity in SyncEntities.Where(entity => entity.TrackDeletion))
        {
            if (!sameReadingGeneration && IsResetReadingEntity(entity.Type)) continue;
            AddMissing(entity.Type, entity.Rows(previous).Select(row => (row.Key, row.Version)),
                entity.Rows(current).Select(row => row.Key), result,
                previous.CreatedAt, current.CreatedAt, recordedDeletionTimes);
        }
        foreach (var item in result.Where(item => IsResetReadingEntity(item.EntityType)))
            item.ReadingDataResetId = current.ReadingDataReset?.Id;
        return result;
    }

    private static void AddMissing(
        string entityType,
        IEnumerable<(string Key, DateTimeOffset DeletedAt)> previous,
        IEnumerable<string> current,
        ICollection<S3SyncTombstone> output,
        DateTimeOffset fallbackDeletedAt,
        DateTimeOffset observationTime,
        IReadOnlyDictionary<string, DateTimeOffset>? recordedDeletionTimes)
    {
        var currentKeys = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous.Where(item => !currentKeys.Contains(item.Key)))
        {
            var recordedDeletionTime = DateTimeOffset.MinValue;
            var hasRecordedDeletionTime = recordedDeletionTimes is not null
                && recordedDeletionTimes.TryGetValue(
                    VersionKey(entityType, item.Key),
                    out recordedDeletionTime)
                && recordedDeletionTime != DateTimeOffset.MinValue;
            var deletedAt = hasRecordedDeletionTime
                ? recordedDeletionTime
                : item.DeletedAt == DateTimeOffset.MinValue
                    ? fallbackDeletedAt
                    : item.DeletedAt;

            if (deletedAt == DateTimeOffset.MinValue)
                deletedAt = observationTime;
            output.Add(new S3SyncTombstone
            {
                EntityType = NormalizeTombstoneEntityType(entityType),
                Key = item.Key,
                DeletedAt = deletedAt
            });
        }
    }

    private static void EnsureDeletionVolumeIsSafe(
        S3SyncSnapshot? previous,
        IReadOnlyCollection<S3SyncTombstone> detectedDeletions,
        string? confirmedDeletionFingerprint = null)
    {
        if (previous is null) return;
        var knownKeys = GetSnapshotEntityVersions(previous).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingKeys = detectedDeletions.Select(item => VersionKey(item.EntityType, item.Key))
            .Where(knownKeys.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", pendingKeys))));
        if (string.Equals(confirmedDeletionFingerprint, fingerprint, StringComparison.Ordinal)) return;
        var descriptions = new List<string>();
        var counts = new[]
        {
            (Name: "书籍", Type: "book", Total: previous.Books.Count),
            (Name: "文件", Type: "file", Total: previous.Files.Count),
            (Name: "收藏夹", Type: "collection", Total: previous.Collections.Count),
            (Name: "收藏夹条目", Type: "collection-item", Total: previous.CollectionItems.Count),
            (Name: "批注", Type: "annotation", Total: previous.Annotations.Count),
            (Name: "读后思考", Type: "reflection", Total: previous.BookReflections.Count),
            (Name: "阅读进度", Type: "progress", Total: previous.Progress.Count),
            (Name: "书签", Type: "bookmark", Total: previous.Bookmarks.Count),
            (Name: "阅读布局", Type: "layout", Total: previous.Layouts.Count),
            (Name: "阅读统计", Type: "stats", Total: previous.ReadingStats.Count)
        };

        foreach (var count in counts)
        {
            if (count.Total < LargeDeletionMinimumEntities) continue;
            var deleted = detectedDeletions
                .Where(item => string.Equals(
                    NormalizeTombstoneEntityType(item.EntityType),
                    count.Type,
                    StringComparison.OrdinalIgnoreCase)
                    && knownKeys.Contains(VersionKey(item.EntityType, item.Key)))
                .Select(item => NormalizeTombstoneKey(count.Type, item.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (deleted <= count.Total * LargeDeletionRatio) continue;

            descriptions.Add(UiText.Get("{0}：删除 {1}/{2} 条", UiText.Get(count.Name), deleted, count.Total));
        }
        if (descriptions.Count > 0)
            throw new S3SyncDeletionConfirmationRequiredException(
                UiText.Get("本次同步将传播大量删除（{0}）。请确认这些删除由你主动执行。", string.Join("; ", descriptions)), fingerprint);
    }

    private static List<S3SyncTombstone> MergeTombstones(
        IEnumerable<S3SyncTombstone> first,
        IEnumerable<S3SyncTombstone> second,
        DateTimeOffset? now = null)
    {
        var merged = new Dictionary<string, S3SyncTombstone>(StringComparer.OrdinalIgnoreCase);
        foreach (var tombstone in first.Concat(second))
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityType)
                || string.IsNullOrWhiteSpace(tombstone.Key))
                continue;
            var entityType = NormalizeTombstoneEntityType(tombstone.EntityType);
            var normalizedKey = NormalizeTombstoneKey(entityType, tombstone.Key);
            if (entityType.Length == 0 || normalizedKey.Length == 0)
                continue;
            var key = VersionKey(entityType, normalizedKey);
            var readingResetId = IsResetReadingEntity(entityType) ? tombstone.ReadingDataResetId : null;
            if (readingResetId is { } resetId) key += ":" + resetId.ToString("N");
            if (!merged.TryGetValue(key, out var existing)
                || S3SyncVersion.IsNewer(
                    tombstone.SyncVersion, tombstone.DeletedAt,
                    existing.SyncVersion, existing.DeletedAt))
                merged[key] = new S3SyncTombstone
                {
                    EntityType = entityType,
                    Key = normalizedKey,
                    DeletedAt = tombstone.DeletedAt,
                    ReadingDataResetId = readingResetId,
                    SyncVersion = tombstone.SyncVersion
                };
        }
        return merged.Values.OrderBy(item => item.DeletedAt).ToList();
    }

    private sealed class LocalDatabaseIdentity
    {
        public Dictionary<Guid, LocalBookIdentity> BooksById { get; } = [];
        public Dictionary<Guid, LocalFileIdentity> FilesById { get; } = [];
        public Dictionary<string, LocalFileIdentity> FilesByHash { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, LocalCollectionIdentity> CollectionsById { get; } = [];
    }

    private sealed record LocalBookIdentity(
        Guid Id,
        string Title,
        string Authors,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? CoverPath);

    private sealed record LocalFileIdentity(
        Guid Id,
        Guid BookId,
        string RelativePath,
        long Size,
        string Sha256);

    private sealed record LocalCollectionIdentity(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record LocalDuplicateBook(
        Guid Id,
        string Title,
        string Authors,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? CoverPath,
        long FileCount);

    private sealed record LocalDuplicateFile(Guid Id, string RelativePath, string Sha256);

    private sealed record PreparedSyncFile(
        S3SyncBookFile Source,
        Guid LocalBookId,
        Guid LocalFileId,
        string RelativePath);

    private sealed class SynchronizedFileMove(
        string sourcePath,
        string targetPath,
        string temporaryPath)
    {
        public string SourcePath { get; } = sourcePath;
        public string TargetPath { get; } = targetPath;
        public string TemporaryPath { get; } = temporaryPath;
        public bool TargetMoved { get; set; }
    }

    private sealed record DatabaseMergeResult(
        int BooksAdded,
        int FilesDownloaded,
        int AnnotationsApplied,
        bool Changed,
        string? Warning)
    {
        public bool IsPartial { get; init; }
        public IReadOnlySet<Guid> RemoteBookIds { get; init; } = new HashSet<Guid>();
        public IReadOnlySet<string> RemoteOnlyFileHashes { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The pre-merge snapshot captured inside the write transaction. The
        // caller derives the converged upload view from this instead of
        // re-reading every table after the commit.
        public S3SyncSnapshot? LocalBaseline { get; init; }
        // Duplicate consolidation re-points annotations/files without touching
        // UpdatedAt, so the converged capture must scan annotations in full.
        public bool ConsolidatedDuplicates { get; init; }
    }

    internal static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private sealed class SqliteCommandCache : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly Dictionary<string, SqliteCommand> _commands =
            new(StringComparer.Ordinal);

        public SqliteCommandCache(SqliteConnection connection, SqliteTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public SqliteCommand Get(string commandText)
        {
            if (_commands.TryGetValue(commandText, out var command))
                return command;
            command = CreateCommand(_connection, _transaction, commandText);
            _commands.Add(commandText, command);
            return command;
        }

        public void Dispose()
        {
            foreach (var command in _commands.Values)
                command.Dispose();
            _commands.Clear();
        }
    }

    private static Guid ParseGuid(string value, string field) =>
        Guid.TryParse(value, out var result)
            ? result
            : throw new InvalidDataException(UiText.Get("本地数据库字段 {0} 不是有效的 GUID。", field));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : DateTimeOffset.MinValue;

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
