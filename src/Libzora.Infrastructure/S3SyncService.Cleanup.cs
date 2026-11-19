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
/// Remote storage cleanup and retention.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private async Task<string?> CleanupRemoteStorageAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        string localDeviceId,
        S3SyncSnapshot localSnapshot,
        S3SyncState state,
        IReadOnlyList<S3SyncSnapshot> downloadedRemoteSnapshots,
        CancellationToken cancellationToken)
    {
        // Unknown data may reference blobs or encode deletion semantics we do
        // not understand. Leave retention/compaction to a reader that does.
        if (HasForwardCompatibleData(localSnapshot)
            || downloadedRemoteSnapshots.Any(HasForwardCompatibleData)) return null;
        // A provider may return a partial listing without failing. Never turn
        // that into snapshot retirement or object deletion.
        if (!ProviderCapabilities(settings).ReliableListing) return null;
        IReadOnlyList<SyncObjectMetadata> deviceObjects;
        try
        {
            deviceObjects = await client.ListObjectsAsync(
                $"{settings.Prefix}/devices/",
                cancellationToken);
        }
        catch (SyncListingUnavailableException)
        {
            return null;
        }

        var snapshotsByDevice = downloadedRemoteSnapshots
            .Where(snapshot => !string.Equals(
                NormalizeKnownDeviceId(snapshot.DeviceId),
                NormalizeDeviceId(localDeviceId),
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(snapshot => NormalizeKnownDeviceId(snapshot.DeviceId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);
        var localId = NormalizeDeviceId(localDeviceId);
        var deletedSnapshots = new HashSet<string>(StringComparer.Ordinal);
        var cleanupErrors = new List<string>();
        var retiredBefore = DateTimeOffset.UtcNow - RetiredDeviceSnapshotRetention;

        foreach (var remoteObject in deviceObjects
                     .Where(item => item.Key.EndsWith("/snapshot.bin", StringComparison.OrdinalIgnoreCase)
                         && item.LastModified is { } modified
                         && modified <= retiredBefore)
                     .OrderBy(item => item.LastModified))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSnapshotDeviceId(settings, remoteObject.Key, out var snapshotDeviceId)
                || string.Equals(snapshotDeviceId, localId, StringComparison.OrdinalIgnoreCase)
                || !snapshotsByDevice.TryGetValue(snapshotDeviceId, out var candidate))
                continue;

            var targets = snapshotsByDevice
                .Where(pair => !string.Equals(pair.Key, snapshotDeviceId, StringComparison.OrdinalIgnoreCase)
                    && !deletedSnapshots.Contains(SnapshotKey(settings, pair.Key)))
                .Select(pair => pair.Value)
                .Append(localSnapshot)
                .ToArray();
            if (!SnapshotIsSubsumed(candidate, targets)) continue;

            try
            {
                await client.DeleteAsync(remoteObject.Key, cancellationToken);
                deletedSnapshots.Add(remoteObject.Key);
                snapshotsByDevice.Remove(snapshotDeviceId);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupErrors.Add(exception.Message);
            }
        }

        var retainedSnapshots = snapshotsByDevice.Values.Append(localSnapshot).ToArray();
        var compactedWatermark = GetTombstoneCompactionWatermark(retainedSnapshots);
        var existingWatermark = retainedSnapshots
            .Select(snapshot => snapshot.TombstonesPrunedBefore ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (compactedWatermark > existingWatermark)
        {
            localSnapshot.Tombstones = (localSnapshot.Tombstones ?? [])
                .Where(tombstone => tombstone.DeletedAt > compactedWatermark)
                .ToList();
            localSnapshot.TombstonesPrunedBefore = compactedWatermark;
            state.LastUploadedSnapshot = localSnapshot;
            state.Tombstones = localSnapshot.Tombstones;
            await SaveStateAsync(state, cancellationToken);
            using (await AppDataProcessLock.AcquireAsync(_paths, cancellationToken))
                await ClearRecordedDeletionTimesThroughAsync(compactedWatermark, cancellationToken);
            await UploadSnapshotAsync(client, settings, localSnapshot, cancellationToken);
        }

        retainedSnapshots = snapshotsByDevice.Values.Append(localSnapshot).ToArray();
        var referencedBlobKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in retainedSnapshots)
        {
            foreach (var hash in SyncEntities.SelectMany(entity => entity.Rows(snapshot))
                         .Select(row => row.BlobHash).Where(IsSha256))
                referencedBlobKeys.UnionWith(BlobKeys(settings, hash!));
        }

        IReadOnlyList<SyncObjectMetadata> blobObjects;
        try
        {
            var listed = new List<SyncObjectMetadata>();
            foreach (var prefix in BlobPrefixes(settings))
                listed.AddRange(await client.ListObjectsAsync(prefix, cancellationToken));
            blobObjects = listed;
        }
        catch (SyncListingUnavailableException)
        {
            return cleanupErrors.Count == 0
                ? null
                : UiText.Get("已清理部分远端快照，但对象清理失败：{0}", string.Join("; ", cleanupErrors.Distinct()));
        }

        var orphanBefore = DateTimeOffset.UtcNow - UnreferencedBlobRetention;
        foreach (var blob in blobObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referencedBlobKeys.Contains(blob.Key)
                || blob.LastModified is not { } modified
                || modified > orphanBefore)
                continue;
            try
            {
                await client.DeleteAsync(blob.Key, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupErrors.Add(exception.Message);
            }
        }

        if (client is S3SyncObjectStore s3Store)
        {
            try
            {
                await s3Store.CleanupIncompleteMultipartUploadsAsync(
                    TimeSpan.FromDays(180),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupErrors.Add(exception.Message);
            }
        }

        return cleanupErrors.Count == 0
            ? null
            : UiText.Get("远端清理未完成：{0}", string.Join("; ", cleanupErrors.Distinct()));
    }

    private static bool TryReadSnapshotDeviceId(
        S3SyncSettings settings,
        string key,
        out string deviceId)
    {
        deviceId = string.Empty;
        var marker = $"{settings.Prefix}/devices/";
        const string suffix = "/snapshot.bin";
        if (!key.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
            || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = key[marker.Length..^suffix.Length];
        if (!Guid.TryParse(candidate, out var parsed)) return false;
        deviceId = parsed.ToString("N");
        return true;
    }

    private static bool SnapshotIsSubsumed(
        S3SyncSnapshot candidate,
        IReadOnlyList<S3SyncSnapshot> targets)
    {
        if (targets.Count == 0) return false;
        if (HasForwardCompatibleData(candidate)) return false;
        var targetLiveRows = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var tombstoneVersions = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            foreach (var (key, version) in GetSnapshotEntityVersions(target))
                if (!targetLiveRows.TryGetValue(key, out var existing) || version > existing)
                    targetLiveRows[key] = version;
            foreach (var tombstone in target.Tombstones ?? [])
            {
                var key = VersionKey(tombstone.EntityType, tombstone.Key);
                if (!tombstoneVersions.TryGetValue(key, out var existing) || tombstone.DeletedAt > existing)
                    tombstoneVersions[key] = tombstone.DeletedAt;
            }
        }

        foreach (var entity in SyncEntities)
        {
            var byKey = targets.SelectMany(entity.Rows)
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var source in entity.Rows(candidate))
            {
                if (byKey.TryGetValue(source.Key, out var alternatives)
                    && alternatives.Any(target => target.Version >= source.Version
                        && SnapshotRowsEqual(target.Value, source.Value))) continue;
                if (!source.DeletionKeys.Any(key => tombstoneVersions.TryGetValue(key, out var deletedAt)
                    && deletedAt >= source.Version)) return false;
            }
        }

        foreach (var tombstone in candidate.Tombstones ?? [])
        {
            var key = VersionKey(tombstone.EntityType, tombstone.Key);
            if (tombstoneVersions.TryGetValue(key, out var deletedAt) && deletedAt >= tombstone.DeletedAt) continue;
            if (targetLiveRows.TryGetValue(key, out var liveVersion) && liveVersion > tombstone.DeletedAt) continue;
            return false;
        }
        return true;
    }

    private static DateTimeOffset GetTombstoneCompactionWatermark(
        IReadOnlyList<S3SyncSnapshot> snapshots)
    {
        var existingWatermark = snapshots
            .Select(snapshot => snapshot.TombstonesPrunedBefore ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        var retentionCutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(365);
        var versions = snapshots
            .SelectMany(snapshot => (snapshot.Tombstones ?? []).Select(tombstone => (
                Key: VersionKey(tombstone.EntityType, tombstone.Key),
                DeviceId: NormalizeKnownDeviceId(snapshot.DeviceId),
                tombstone.DeletedAt)))
            .Where(item => item.DeletedAt > existingWatermark)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versions.Length == 0) return existingWatermark;

        var commonKeys = versions
            .Where(group => group.Select(item => item.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == snapshots.Count
                && snapshots.All(snapshot => !ContainsEntityAtOrBefore(
                    snapshot,
                    group.Key,
                    group.Max(item => item.DeletedAt))))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldestUnreplicated = versions
            .Where(group => !commonKeys.Contains(group.Key))
            .Select(group => group.Min(item => item.DeletedAt))
            .Where(date => date <= retentionCutoff)
            .DefaultIfEmpty(retentionCutoff.AddTicks(1))
            .Min();
        var upperBound = oldestUnreplicated <= retentionCutoff
            ? oldestUnreplicated.AddTicks(-1)
            : retentionCutoff;
        var safeCommonVersions = versions
            .Where(group => commonKeys.Contains(group.Key))
            .Select(group => group.Max(item => item.DeletedAt))
            .Where(date => date <= upperBound)
            .ToArray();
        if (safeCommonVersions.Length == 0) return existingWatermark;
        return Max(existingWatermark, safeCommonVersions.Max());
    }
}
