using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    internal static async Task EnsureVersionSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, """
            CREATE TABLE IF NOT EXISTS S3SyncClock (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                DeviceId TEXT NOT NULL,
                PhysicalTicks INTEGER NOT NULL DEFAULT 0,
                Counter INTEGER NOT NULL DEFAULT 0,
                ServerOffsetTicks INTEGER NOT NULL DEFAULT 0
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Assigns a stable hybrid-clock version to every portable row that does
    /// not already have one. Rows reused unchanged from the previous baseline
    /// keep their version; rows re-read from SQLite after an edit receive a new
    /// version. Rows that were just written by a remote merge also receive a
    /// new version, which is the merge event's position in the causal order.
    /// </summary>
    private static async Task AssignSnapshotVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        S3SyncSnapshot snapshot,
        S3SyncSnapshot? baseline,
        S3SyncClock clock,
        CancellationToken cancellationToken)
    {
        var baselineRows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (baseline is not null)
        {
            foreach (var entity in SyncEntities)
            foreach (var row in entity.Rows(baseline))
                baselineRows[VersionKey(entity.Type, row.Key)] = row.Value;
        }

        // Assign in row-timestamp order. The clock's physical component is a
        // batch-wide floor, so processing a row with a later timestamp first
        // would re-stamp unrelated older rows at that later instant. Sorting
        // keeps each unchanged edit's version close to its own time while the
        // clock still guarantees ordering after everything already observed.
        var rowsToVersion = SyncEntities
            .SelectMany(entity => entity.Rows(snapshot)
                .Select(row => (Entity: entity, Row: row)))
            .OrderBy(item => item.Row.Version)
            .ThenBy(item => item.Entity.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Row.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var (entity, row) in rowsToVersion)
        {
            if (row.Value is not ISyncVersionedRow versioned) continue;
            if (string.IsNullOrWhiteSpace(versioned.SyncVersion))
            {
                var key = VersionKey(entity.Type, row.Key);
                if (baselineRows.TryGetValue(key, out var old)
                    && old is ISyncVersionedRow oldVersioned
                    && !string.IsNullOrWhiteSpace(oldVersioned.SyncVersion)
                    && SnapshotRowContentEqual(row.Value, old))
                {
                    versioned.SyncVersion = oldVersioned.SyncVersion;
                }
                else
                {
                    versioned.SyncVersion = clock.Next(
                        row.Version == default ? snapshot.CreatedAt : row.Version);
                }
            }
            clock.ObserveVersion(versioned.SyncVersion);
        }

        var baselineLayouts = (baseline?.Layouts ?? [])
            .GroupBy(layout => layout.BookFileId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(LayoutSyncVersion).First());
        foreach (var layout in snapshot.Layouts)
        {
            baselineLayouts.TryGetValue(layout.BookFileId, out var oldLayout);
            if (string.IsNullOrWhiteSpace(layout.LayoutPartVersion))
            {
                layout.LayoutPartVersion = oldLayout is not null
                    && LayoutPartEqual(layout, oldLayout)
                    ? oldLayout.LayoutPartVersion
                    : clock.Next(LayoutPartTime(layout));
            }
            clock.ObserveVersion(layout.LayoutPartVersion);

            if (layout.ProfileUpdatedAt is not null)
            {
                if (string.IsNullOrWhiteSpace(layout.ProfilePartVersion))
                {
                    layout.ProfilePartVersion = oldLayout is not null
                        && ProfilePartEqual(layout, oldLayout)
                        ? oldLayout.ProfilePartVersion
                        : clock.Next(ProfilePartTime(layout));
                }
                clock.ObserveVersion(layout.ProfilePartVersion);
            }
            else
            {
                layout.ProfilePartVersion = null;
            }
        }

        var baselineTombstones = new Dictionary<string, S3SyncTombstone>(StringComparer.OrdinalIgnoreCase);
        if (baseline is not null)
        {
            foreach (var tombstone in baseline.Tombstones)
            {
                var type = NormalizeTombstoneEntityType(tombstone.EntityType);
                var key = NormalizeTombstoneKey(type, tombstone.Key);
                if (type.Length == 0 || key.Length == 0) continue;
                baselineTombstones[TombstoneVersionKey(type, key, tombstone.ReadingDataResetId)] = tombstone;
            }
        }

        foreach (var tombstone in snapshot.Tombstones)
        {
            var type = NormalizeTombstoneEntityType(tombstone.EntityType);
            var key = NormalizeTombstoneKey(type, tombstone.Key);
            if (type.Length == 0 || key.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(tombstone.SyncVersion))
            {
                var versionKey = TombstoneVersionKey(type, key, tombstone.ReadingDataResetId);
                if (baselineTombstones.TryGetValue(versionKey, out var old)
                    && !string.IsNullOrWhiteSpace(old.SyncVersion)
                    && TombstoneContentEqual(tombstone, old))
                {
                    tombstone.SyncVersion = old.SyncVersion;
                }
                else
                {
                    tombstone.SyncVersion = clock.Next(
                        tombstone.DeletedAt == default ? snapshot.CreatedAt : tombstone.DeletedAt);
                }
            }
            clock.ObserveVersion(tombstone.SyncVersion);
        }

        await clock.SaveAsync(connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Local deletion tombstones are created after the first capture, so they
    /// need a version before they are merged with remote tombstones.
    /// </summary>
    private async Task AssignTombstoneVersionsAsync(
        S3SyncSnapshot snapshot,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
        var clock = await S3SyncClock.LoadAsync(connection, transaction, deviceId, cancellationToken);
        await AssignSnapshotVersionsAsync(connection, transaction, snapshot, null, clock, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Advances this device's clock past every version it received, before
    /// generating the converged snapshot. A later local edit then sorts after
    /// the changes it has actually observed.
    /// </summary>
    private async Task ObserveRemoteVersionsAsync(
        IReadOnlyList<S3SyncSnapshot> snapshots,
        string deviceId,
        TimeSpan? serverOffset,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
        var clock = await S3SyncClock.LoadAsync(connection, transaction, deviceId, cancellationToken);
        if (serverOffset is { } offset) clock.ObserveServerOffset(offset);
        foreach (var snapshot in snapshots)
        {
            foreach (var entity in SyncEntities)
            foreach (var row in entity.Rows(snapshot))
            {
                if (row.Value is ISyncVersionedRow versioned)
                    clock.ObserveVersion(versioned.SyncVersion);
                if (row.Value is S3SyncLayout layout)
                {
                    clock.ObserveVersion(layout.LayoutPartVersion);
                    clock.ObserveVersion(layout.ProfilePartVersion);
                }
            }
            foreach (var tombstone in snapshot.Tombstones)
                clock.ObserveVersion(tombstone.SyncVersion);
        }
        await clock.SaveAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool SnapshotRowContentEqual(object left, object right)
    {
        if (left.GetType() != right.GetType()) return false;
        var leftVersioned = left as ISyncVersionedRow;
        var rightVersioned = right as ISyncVersionedRow;
        var leftVersion = leftVersioned?.SyncVersion;
        var rightVersion = rightVersioned?.SyncVersion;
        var leftLayout = left as S3SyncLayout;
        var rightLayout = right as S3SyncLayout;
        var leftLayoutVersion = leftLayout?.LayoutPartVersion;
        var rightLayoutVersion = rightLayout?.LayoutPartVersion;
        var leftProfileVersion = leftLayout?.ProfilePartVersion;
        var rightProfileVersion = rightLayout?.ProfilePartVersion;
        // Cover metadata is derived from the local file during capture. Rows
        // re-read from SQLite have not computed it yet, so it must not make an
        // otherwise unchanged row look like a new edit (which would re-version
        // it above a concurrent deletion). A real cover change bumps the book's
        // UpdatedAt and therefore still produces a new version.
        var leftBook = left as S3SyncBook;
        var rightBook = right as S3SyncBook;
        var leftCoverHash = leftBook?.CoverHash;
        var rightCoverHash = rightBook?.CoverHash;
        var leftCoverName = leftBook?.CoverFileName;
        var rightCoverName = rightBook?.CoverFileName;
        try
        {
            if (leftVersioned is not null) leftVersioned.SyncVersion = null;
            if (rightVersioned is not null) rightVersioned.SyncVersion = null;
            if (leftLayout is not null)
            {
                leftLayout.LayoutPartVersion = null;
                leftLayout.ProfilePartVersion = null;
            }
            if (rightLayout is not null)
            {
                rightLayout.LayoutPartVersion = null;
                rightLayout.ProfilePartVersion = null;
            }
            if (leftBook is not null)
            {
                leftBook.CoverHash = null;
                leftBook.CoverFileName = null;
            }
            if (rightBook is not null)
            {
                rightBook.CoverHash = null;
                rightBook.CoverFileName = null;
            }
            return SnapshotRowsEqual(left, right);
        }
        finally
        {
            if (leftVersioned is not null) leftVersioned.SyncVersion = leftVersion;
            if (rightVersioned is not null) rightVersioned.SyncVersion = rightVersion;
            if (leftLayout is not null)
            {
                leftLayout.LayoutPartVersion = leftLayoutVersion;
                leftLayout.ProfilePartVersion = leftProfileVersion;
            }
            if (rightLayout is not null)
            {
                rightLayout.LayoutPartVersion = rightLayoutVersion;
                rightLayout.ProfilePartVersion = rightProfileVersion;
            }
            if (leftBook is not null)
            {
                leftBook.CoverHash = leftCoverHash;
                leftBook.CoverFileName = leftCoverName;
            }
            if (rightBook is not null)
            {
                rightBook.CoverHash = rightCoverHash;
                rightBook.CoverFileName = rightCoverName;
            }
        }
    }

    private static bool LayoutPartEqual(S3SyncLayout left, S3SyncLayout right) =>
        left.FontScale == right.FontScale
        && left.LineHeight == right.LineHeight
        && left.MaxWidth == right.MaxWidth
        && left.BodyPadding == right.BodyPadding
        && left.FontFamily == right.FontFamily
        && left.FlowMode == right.FlowMode
        && left.VerticalWriting == right.VerticalWriting
        && left.TwoPageMode == right.TwoPageMode
        && left.ParagraphIndent == right.ParagraphIndent
        && left.LayoutUpdatedAt == right.LayoutUpdatedAt;

    private static bool ProfilePartEqual(S3SyncLayout left, S3SyncLayout right) =>
        left.ProfileUpdatedAt is not null
        && right.ProfileUpdatedAt is not null
        && left.UseBookOverride == right.UseBookOverride
        && left.AppearanceTheme == right.AppearanceTheme
        && left.AppearancePaperEnabled == right.AppearancePaperEnabled
        && left.AppearancePaperStrength == right.AppearancePaperStrength
        && left.AppearanceFibersEnabled == right.AppearanceFibersEnabled
        && left.ProfileUpdatedAt == right.ProfileUpdatedAt;

    private static DateTimeOffset LayoutPartTime(S3SyncLayout layout) =>
        layout.LayoutUpdatedAt ?? layout.UpdatedAt;

    private static DateTimeOffset ProfilePartTime(S3SyncLayout layout) =>
        layout.ProfileUpdatedAt ?? layout.UpdatedAt;

    private static bool TombstoneContentEqual(S3SyncTombstone left, S3SyncTombstone right) =>
        string.Equals(
            NormalizeTombstoneEntityType(left.EntityType),
            NormalizeTombstoneEntityType(right.EntityType),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            NormalizeTombstoneKey(left.EntityType, left.Key),
            NormalizeTombstoneKey(right.EntityType, right.Key),
            StringComparison.OrdinalIgnoreCase)
        && left.DeletedAt == right.DeletedAt
        && left.ReadingDataResetId == right.ReadingDataResetId;

    private static string TombstoneVersionKey(string entityType, string key, Guid? resetId) =>
        VersionKey(entityType, key) + ":" + (resetId?.ToString("N") ?? string.Empty);

    /// <summary>
    /// Ordering for applying a whole row. Equal versions normally mean the
    /// rows are the same revision; when a legacy or manually edited snapshot
    /// advanced the row timestamp without changing the version, the later
    /// timestamp still wins so older snapshots keep their historical
    /// semantics.
    /// </summary>
    private static bool IsRowNewer(
        string? candidateVersion, DateTimeOffset candidateTime,
        string? currentVersion, DateTimeOffset currentTime)
    {
        var comparison = S3SyncVersion.Compare(
            candidateVersion, candidateTime, currentVersion, currentTime);
        return comparison > 0 || (comparison == 0 && candidateTime > currentTime);
    }

    /// <summary>
    /// Picks the winning copy of a single whole-row entity. Unlike a plain
    /// wall-clock sort this uses each row's hybrid-clock version, so a device
    /// that observed another device's change always wins even when its own
    /// clock is behind. Exact ties are broken by serialized row content so
    /// every device converges on the same row.
    /// </summary>
    private static T? LatestVersioned<T>(
        IEnumerable<T> rows,
        Func<T, DateTimeOffset> time,
        Func<T, string?> version) where T : class
    {
        T? best = null;
        foreach (var row in rows)
        {
            if (best is null)
            {
                best = row;
                continue;
            }
            if (IsRowNewer(version(row), time(row), version(best), time(best))
                || (S3SyncVersion.Compare(version(row), time(row), version(best), time(best)) == 0
                    && CompareSerializedRows(row, best) > 0))
                best = row;
        }
        return best;
    }

    private static int CompareSerializedRows<T>(T left, T right)
    {
        var leftBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(left, JsonOptions);
        var rightBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(right, JsonOptions);
        return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
    }
}
