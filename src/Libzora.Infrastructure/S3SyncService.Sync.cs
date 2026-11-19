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
/// Synchronisation entry point.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    public async Task<S3SyncResult> SyncAsync(
        string deviceId,
        S3SyncSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        S3SyncOptions? options = null)
    {
        var normalized = NormalizeTransferSettings(settings);
        ThrowIfInvalid(normalized);
        await _features.EnsureFeatureAsync(MemberEntitlements.SyncFeature(normalized.Provider), cancellationToken);
        deviceId = NormalizeDeviceId(deviceId);

        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            ResetEncryptionSession();
            _encryptionSession = normalized.EncryptionKey.Length == 0
                ? null
                : CreateEncryptionSession(normalized.EncryptionKey);
            _paths.EnsureDirectories();
            await InitializeDeletionTrackingAsync(cancellationToken, deviceId);
            if (File.Exists(GetPendingBaselineResetPath()))
            {
                using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
                await ResetLocalBaselineCoreAsync(deviceId, cancellationToken);
            }
            var storageIdentity = BuildStorageIdentity(normalized);
            var state = await LoadStateAsync(deviceId, storageIdentity, cancellationToken);
            var local = await CaptureSnapshotWithBaselineAsync(
                deviceId,
                state.Tombstones,
                cancellationToken,
                state.LastUploadedSnapshot,
                state.SnapshotWatermark);
            local.TombstonesPrunedBefore = state.LastUploadedSnapshot?.TombstonesPrunedBefore;
            PreserveSettingsTimestampsForUnchangedValues(
                local.Settings,
                state.LastUploadedSnapshot?.Settings);
            var recordedDeletionTimes = local.LocalDeletionTimes;
            var detectedDeletions = DetectDeletedEntitiesWithRecordedTimes(
                state.LastUploadedSnapshot,
                local,
                recordedDeletionTimes);
            EnsureDeletionVolumeIsSafe(state.LastUploadedSnapshot, detectedDeletions, options?.ConfirmedDeletionFingerprint);
            local.Tombstones = MergeTombstones(
                local.Tombstones,
                detectedDeletions.Concat(GetRecordedTombstones(recordedDeletionTimes, local, state.LastUploadedSnapshot)));
            await AssignTombstoneVersionsAsync(local, deviceId, cancellationToken);

            progress?.Report(UiText.Get("正在连接 {0}…", normalized.ProviderName));
            using var client = CreateStore(normalized);
            var snapshotObjects = await ListSnapshotObjectsAsync(client, normalized, cancellationToken);
            var snapshotKeys = snapshotObjects.Select(item => item.Key).ToList();

            // Validate remote encryption before uploading any local bytes.
            progress?.Report(UiText.Get("正在读取其他设备的同步快照…"));
            var remoteSnapshots = await DownloadRemoteSnapshotsAsync(
                client,
                normalized,
                snapshotObjects,
                storageIdentity,
                deviceId,
                state.LastUploadedSnapshot is not null,
                progress,
                cancellationToken);
            var serverOffset = (client as ISyncServerClockSource)?.ServerClockOffset;
            await ObserveRemoteVersionsAsync(remoteSnapshots, deviceId, serverOffset, cancellationToken);
            InitializeBlobNamingSession(normalized, state.LastUploadedSnapshot, remoteSnapshots, createIfMissing: true);

            var tombstoneWatermark = Max(
                local.TombstonesPrunedBefore ?? DateTimeOffset.MinValue,
                remoteSnapshots.Select(snapshot => snapshot.TombstonesPrunedBefore ?? DateTimeOffset.MinValue)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max());
            if (tombstoneWatermark > DateTimeOffset.MinValue)
            {
                local.TombstonesPrunedBefore = tombstoneWatermark;
                local.Tombstones = local.Tombstones
                    .Where(tombstone => tombstone.DeletedAt > tombstoneWatermark)
                    .ToList();
                local.LocalDeletionTimes = local.LocalDeletionTimes
                    .Where(entry => entry.Value > tombstoneWatermark)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                recordedDeletionTimes = local.LocalDeletionTimes;
                state.Tombstones = local.Tombstones;
                detectedDeletions = detectedDeletions
                    .Where(tombstone => tombstone.DeletedAt > tombstoneWatermark)
                    .ToList();
                foreach (var remote in remoteSnapshots)
                {
                    remote.TombstonesPrunedBefore = Max(
                        remote.TombstonesPrunedBefore ?? DateTimeOffset.MinValue,
                        tombstoneWatermark);
                    remote.Tombstones = (remote.Tombstones ?? [])
                        .Where(tombstone => tombstone.DeletedAt > tombstoneWatermark)
                        .ToList();
                }
            }

            local.Tombstones = MergeTombstones(
                local.Tombstones,
                remoteSnapshots.SelectMany(snapshot => snapshot.Tombstones ?? []));
            var restoredEntityKeys = await ResolveLocalRestorationsAsync(
                local,
                state.LastUploadedSnapshot,
                remoteSnapshots,
                cancellationToken);
            if (restoredEntityKeys.Count > 0)
            {
                // Retain dominated tombstones as acknowledgements. Dropping
                // them makes another device's old deletion look unseen later.
                local.LocalDeletionTimes = local.LocalDeletionTimes
                    .Where(entry => !restoredEntityKeys.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                recordedDeletionTimes = local.LocalDeletionTimes;
                detectedDeletions = detectedDeletions
                    .Where(tombstone => !restoredEntityKeys.Contains(VersionKey(tombstone.EntityType, tombstone.Key)))
                    .ToList();
                state.Tombstones = local.Tombstones;
            }
            LiftRemoteDeletionVersionsOverUnchangedLocalRows(
                local,
                state.LastUploadedSnapshot,
                remoteSnapshots);
            LiftLocalDeletionVersionsOverObservedRemoteRows(
                local,
                remoteSnapshots,
                detectedDeletions,
                tombstoneWatermark);
            await AssignTombstoneVersionsAsync(local, deviceId, cancellationToken);

            progress?.Report(UiText.Get("正在合并书籍和阅读数据…"));
            var databaseResult = await ApplyRemoteSnapshotsAsync(
                client,
                normalized,
                local,
                state.LastUploadedSnapshot,
                remoteSnapshots,
                progress,
                cancellationToken);

            // The database transaction commits before the remaining network
            // work. Persist the remote provenance immediately so a later
            // object/snapshot upload failure cannot make an incoming book look
            // like a locally-created, already-synced book on the next retry.
            var remoteOnlyFileHashes = (state.RemoteOnlyFileHashes ?? [])
                .Where(IsSha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            remoteOnlyFileHashes.UnionWith(databaseResult.RemoteOnlyFileHashes);
            state.RemoteBookIds = (state.RemoteBookIds ?? [])
                .Concat(databaseResult.RemoteBookIds.Select(id => id.ToString("N")))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (databaseResult.RemoteBookIds.Count > 0 || databaseResult.RemoteOnlyFileHashes.Count > 0)
                await SaveStateAsync(state, cancellationToken);

            progress?.Report(UiText.Get("正在合并同步设置…"));
            var settingsChanged = await ApplyRemoteSettingsAsync(
                local.Settings,
                remoteSnapshots,
                cancellationToken);

            // Capture again after the merge. This makes this device's snapshot
            // a complete converged view, so a third device can catch up from it
            // without having to contact every previous device forever.
            var remoteAnnotationIds = remoteSnapshots
                .SelectMany(snapshot => snapshot.Annotations)
                .Select(annotation => annotation.Id)
                .Distinct()
                .ToArray();
            // Reuse the sync-start capture's row versions when there was no
            // merge to establish a newer baseline. A plain full capture would
            // re-stamp every unchanged row with the sync time, letting a
            // device that merely syncs later overwrite a genuinely newer edit
            // from another device.
            var convergedBase = databaseResult.LocalBaseline ?? local;
            var finalSnapshot = await CaptureConvergedSnapshotAsync(
                deviceId, local.Tombstones, convergedBase, remoteAnnotationIds,
                databaseResult.ConsolidatedDuplicates, cancellationToken);
            finalSnapshot.TombstonesPrunedBefore = local.TombstonesPrunedBefore;
            var finalDeletions = finalSnapshot.LocalDeletionTimes
                .Where(entry => finalSnapshot.TombstonesPrunedBefore is not { } watermark
                    || entry.Value > watermark)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var recordedTombstones = GetRecordedTombstones(finalDeletions, finalSnapshot, state.LastUploadedSnapshot);
            EnsureDeletionVolumeIsSafe(state.LastUploadedSnapshot,
                MergeTombstones(FilterReadingTombstones(detectedDeletions, finalSnapshot.ReadingDataReset), recordedTombstones),
                options?.ConfirmedDeletionFingerprint);
            finalSnapshot.Tombstones = MergeTombstones(finalSnapshot.Tombstones, recordedTombstones);
            finalSnapshot.BlobNamingSalts = _blobNamingSession?.Salts is { Count: > 0 } salts ? salts.ToArray() : null;
            PreserveForwardCompatibleData(finalSnapshot, state.LastUploadedSnapshot, remoteSnapshots);

            // Publish exactly this captured view after all its objects exist.
            // Writes after capture remain outside the saved local baseline.
            progress?.Report(UiText.Get("正在上传本地书籍文件…"));
            var uploadWarning = await UploadLocalObjectsAsync(
                client, normalized, finalSnapshot, remoteOnlyFileHashes, progress, cancellationToken);

            var contentHash = SnapshotContentHash(finalSnapshot);
            var ownSnapshotPresent = snapshotKeys.Any(key => IsSnapshotKeyForDevice(key, normalized, deviceId));
            if (ownSnapshotPresent && state.LastSnapshotContentHash == contentHash
                && state.LastSnapshotUploadAt is { } published && DateTimeOffset.UtcNow - published < SnapshotRepublishInterval)
            {
                // Same content as the published copy: keep its timestamp so other devices can reuse their cache.
                progress?.Report(UiText.Get("同步快照没有变化，跳过上传。"));
            }
            else
            {
                progress?.Report(UiText.Get("正在保存同步快照…"));
                await UploadSnapshotAsync(client, normalized, finalSnapshot, cancellationToken);
                state.LastSnapshotContentHash = contentHash;
                state.LastSnapshotUploadAt = DateTimeOffset.UtcNow;
            }

            state.DeviceId = deviceId;
            state.StorageIdentity = storageIdentity;
            state.LastSyncAt = DateTimeOffset.UtcNow;
            state.LastUploadedSnapshot = finalSnapshot;
            state.Tombstones = finalSnapshot.Tombstones;
            // Persist the local maps needed by the next incremental capture.
            // They are state-file only; S3SyncSnapshot keeps them [JsonIgnore]
            // so relative paths never reach the cloud.
            state.SnapshotWatermark = finalSnapshot.CreatedAt;
            state.LocalFilePaths = new Dictionary<Guid, string>(finalSnapshot.LocalFilePaths);
            state.LocalCoverPaths = new Dictionary<Guid, string>(finalSnapshot.LocalCoverPaths);
            state.LocalDeletionTimes = new Dictionary<string, DateTimeOffset>(
                finalSnapshot.LocalDeletionTimes, StringComparer.OrdinalIgnoreCase);
            var finalBookIds = finalSnapshot.Books.Select(book => book.Id).ToHashSet();
            state.RemoteBookIds = state.RemoteBookIds
                .Where(id => Guid.TryParse(id, out var parsed) && finalBookIds.Contains(parsed))
                .ToList();
            state.RemoteOnlyFileHashes = remoteOnlyFileHashes
                .Where(hash => finalSnapshot.Files.Any(file =>
                        string.Equals(file.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                    && !finalSnapshot.Files.Any(file =>
                        string.Equals(file.Sha256, hash, StringComparison.OrdinalIgnoreCase)
                        && finalSnapshot.LocalFilePaths.TryGetValue(file.Id, out var relativePath)
                        && ResolveDataPath(relativePath) is { } path
                        && File.Exists(path)))
                .ToList();
            await SaveStateAsync(state, cancellationToken);

            string? cleanupWarning = null;
            try
            {
                cleanupWarning = await CleanupRemoteStorageAsync(
                    client,
                    normalized,
                    deviceId,
                    finalSnapshot,
                    state,
                    remoteSnapshots,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The converged snapshot and local sync state are already
                // durable. A cancelled cleanup can resume on the next sync.
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or AmazonS3Exception or TimeoutException)
            {
                cleanupWarning = UiText.Get("远端清理未完成：{0}", UiText.Localize(exception.Message));
            }

            // Cleanup only mutates the persisted state in its rare snapshot
            // compaction path, which saves on its own. Re-saving the whole
            // snapshot here doubled the state write for every normal sync.
            var warning = string.Join(
                " ",
                new[] { uploadWarning, databaseResult.Warning, cleanupWarning }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
            return new S3SyncResult(
                remoteSnapshots
                    .Select(snapshot => NormalizeKnownDeviceId(snapshot.DeviceId))
                    .Where(remoteDeviceId => !string.Equals(remoteDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                databaseResult.BooksAdded,
                databaseResult.FilesDownloaded,
                databaseResult.AnnotationsApplied,
                settingsChanged ? 1 : 0,
                databaseResult.Changed || settingsChanged,
                warning.Length == 0 ? null : warning)
            {
                IsPartial = uploadWarning is not null
                    || cleanupWarning is not null
                    || databaseResult.IsPartial
            };
        }
        finally
        {
            ResetEncryptionSession();
            _syncGate.Release();
        }
    }
}
