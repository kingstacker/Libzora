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
/// Validation, state and baseline persistence.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private static bool AiSyncSettingsEqual(S3SyncAiSettings? left, S3SyncAiSettings? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
              && string.Equals(left.BaseUrl, right.BaseUrl, StringComparison.Ordinal)
              && string.Equals(left.Model, right.Model, StringComparison.Ordinal);

    private static bool EmailSyncSettingsEqual(
        S3SyncKindleEmailSettings? left,
        S3SyncKindleEmailSettings? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.KindleEmailAddress, right.KindleEmailAddress, StringComparison.OrdinalIgnoreCase)
              && string.Equals(left.SenderEmailAddress, right.SenderEmailAddress, StringComparison.OrdinalIgnoreCase)
              && string.Equals(left.SmtpHost, right.SmtpHost, StringComparison.OrdinalIgnoreCase)
              && left.SmtpPort == right.SmtpPort
              && string.Equals(left.SmtpUsername, right.SmtpUsername, StringComparison.Ordinal)
              && left.EnableSsl == right.EnableSsl;

    private S3SyncSettings NormalizeTransferSettings(S3SyncSettings settings)
    {
        // Check before migration: an explicit removed provider must never use
        // credentials left over from a previously configured S3 connection.
        if (settings.Provider == SyncProvider.AccountServer)
            throw new InvalidOperationException(MemberEntitlements.AccountCloudRequirement);
        var normalized = S3SyncSettings.Normalize(settings);
        if (_providers.TryGet(normalized, out var factory))
        {
            if (factory is ISyncProviderValidator validator)
                normalized = validator.Normalize(normalized);
            if (factory is ISyncProviderAccountBinding binding)
                normalized = binding.BindAccount(normalized);
        }
        return normalized;
    }

    private static string? ValidateWithProvider(S3SyncSettings settings, ISyncProviderFactory factory) =>
        settings.Validate() ?? (factory is ISyncProviderValidator validator ? validator.Validate(settings) : null);

    private void ThrowIfInvalid(S3SyncSettings settings)
    {
        var validation = settings.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        if (!_providers.TryGet(settings, out var factory))
            throw new NotSupportedException(UiText.Get("当前平台不支持此同步方式。"));
        if (factory is ISyncProviderValidator validator && validator.Validate(settings) is { } providerError)
            throw new InvalidOperationException(providerError);
    }

    private static string NormalizeDeviceId(string deviceId) =>
        Guid.TryParse(deviceId, out var parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");

    private static string NormalizeKnownDeviceId(string? deviceId) =>
        Guid.TryParse(deviceId, out var parsed)
            ? parsed.ToString("N")
            : (deviceId ?? string.Empty).Trim();

    private bool SameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
        left.Provider == right.Provider
        && string.Equals(left.EffectiveProviderId, right.EffectiveProviderId, StringComparison.OrdinalIgnoreCase)
        && left.Prefix == right.Prefix
        && left.AccountRegion == right.AccountRegion
        && _providers.TryGet(left, out var factory)
        && factory.IsSameRemoteDirectory(left, right);

    private string BuildStorageIdentity(S3SyncSettings settings) =>
        (_dataRegion is { } region ? "region:" + AccountRegions.Slug(region) + "|" : "")
        + _providers.Get(settings).BuildStorageIdentity(settings);

    private SyncProviderCapabilities ProviderCapabilities(S3SyncSettings settings) =>
        _providers.Get(settings).Descriptor.Capabilities;

    private int EffectiveConcurrency(S3SyncSettings settings)
    {
        var requested = Math.Max(1, settings.ConcurrentRequests);
        return ProviderCapabilities(settings).MaxConcurrentRequests is { } limit && limit > 0
            ? Math.Min(requested, limit)
            : requested;
    }

    private string StatePath => Path.Combine(_paths.Data, "s3-sync-state.json");
    private string BaselinePath => Path.Combine(_paths.Data, "s3-sync-baseline.json.gz");
    private string? _baselineHash;

    private sealed class BadgeIndex(S3SyncState state)
    {
        public S3SyncState State { get; } = state;
        public Dictionary<Guid, S3SyncBook> Books { get; } = state.LastUploadedSnapshot!.Books.ToDictionary(book => book.Id);
        public ILookup<Guid, S3SyncBookFile> Files { get; } = state.LastUploadedSnapshot!.Files.ToLookup(file => file.BookId);
        public HashSet<Guid> RemoteBooks { get; } = (state.RemoteBookIds ?? [])
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        public HashSet<string> RemoteHashes { get; } = (state.RemoteOnlyFileHashes ?? [])
            .Where(IsSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private BadgeIndex? _badgeIndex;

    private (string Device, string Storage, long StateLength, long StateModified,
        long BaselineLength, long BaselineModified, S3SyncState State)? _badgeState;

    private async Task<S3SyncState> LoadBadgeStateAsync(string deviceId, string storageIdentity, CancellationToken token)
    {
        var stateInfo = new FileInfo(StatePath);
        var baselineInfo = new FileInfo(BaselinePath);
        var stateLength = stateInfo.Exists ? stateInfo.Length : -1;
        var stateModified = stateInfo.Exists ? stateInfo.LastWriteTimeUtc.Ticks : 0;
        var baselineLength = baselineInfo.Exists ? baselineInfo.Length : -1;
        var baselineModified = baselineInfo.Exists ? baselineInfo.LastWriteTimeUtc.Ticks : 0;
        if (_badgeState is { } cache && cache.Device == deviceId && cache.Storage == storageIdentity
            && cache.StateLength == stateLength && cache.StateModified == stateModified
            && cache.BaselineLength == baselineLength && cache.BaselineModified == baselineModified)
            return cache.State;
        var state = await LoadStateAsync(deviceId, storageIdentity, token);
        _badgeState = (deviceId, storageIdentity,
            stateLength, stateModified, baselineLength, baselineModified, state);
        return state;
    }

    private async Task<S3SyncState> LoadStateAsync(
        string deviceId,
        string storageIdentity,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
            return new S3SyncState { DeviceId = deviceId, StorageIdentity = storageIdentity };

        try
        {
            await using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                // Allow an in-flight SaveStateAsync replacement on Windows.
                FileShare.Read | FileShare.Delete,
                81920,
                useAsync: true);
            var state = await JsonSerializer.DeserializeAsync<S3SyncState>(stream, JsonOptions, cancellationToken)
                ?? new S3SyncState();
            if (!string.Equals(state.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.StorageIdentity, storageIdentity, StringComparison.Ordinal))
            {
                return new S3SyncState { DeviceId = deviceId, StorageIdentity = storageIdentity };
            }

            state.Tombstones ??= [];
            state.RemoteBookIds ??= [];
            state.RemoteOnlyFileHashes ??= [];
            state.LocalFilePaths ??= [];
            state.LocalCoverPaths ??= [];
            state.LocalDeletionTimes ??= [];
            _baselineHash = null;
            // The baseline is stored separately so the frequently rewritten
            // state file stays small. Older state files still carry the
            // snapshot inline; keep that as a fallback.
            if (state.LastUploadedSnapshot is null)
                state.LastUploadedSnapshot = await LoadBaselineAsync(state.BaselineHash, cancellationToken);
            if (state.LastUploadedSnapshot is null)
                state.SnapshotWatermark = null;
            if (state.LastUploadedSnapshot is { } previous)
            {
                previous.LocalFilePaths.Clear();
                foreach (var (id, path) in state.LocalFilePaths)
                    previous.LocalFilePaths[id] = path;
                previous.LocalCoverPaths.Clear();
                foreach (var (id, path) in state.LocalCoverPaths)
                    previous.LocalCoverPaths[id] = path;
                previous.LocalDeletionTimes = new Dictionary<string, DateTimeOffset>(
                    state.LocalDeletionTimes, StringComparer.OrdinalIgnoreCase);
            }
            return state;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new S3SyncState { DeviceId = deviceId, StorageIdentity = storageIdentity };
        }
    }

    private async Task<S3SyncSnapshot?> LoadBaselineAsync(string? expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(BaselinePath)) return null;
        try
        {
            await using var file = new FileStream(
                BaselinePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                81920,
                useAsync: true);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            await gzip.CopyToAsync(buffer, cancellationToken);
            var json = buffer.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(json));
            // The baseline and state are replaced separately. A crash between
            // those writes must not pair a new baseline with an old watermark.
            if (!string.Equals(hash, expectedHash, StringComparison.Ordinal)) return null;
            var snapshot = JsonSerializer.Deserialize<S3SyncSnapshot>(json, JsonOptions);
            if (snapshot is not null)
            {
                AdoptLegacyRatingFields(snapshot);
                _baselineHash = hash;
            }
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private async Task SaveStateAsync(S3SyncState state, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var snapshot = state.LastUploadedSnapshot;
        state.BaselineHash = await SaveBaselineAsync(snapshot, cancellationToken);

        // Keep the frequently rewritten state file small; the full baseline
        // lives in its own gzipped file and is restored by LoadStateAsync.
        state.LastUploadedSnapshot = null;
        try
        {
            var temporaryPath = StatePath + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }
            await MoveFileWithRetryAsync(temporaryPath, StatePath, cancellationToken);
        }
        finally
        {
            state.LastUploadedSnapshot = snapshot;
        }
        _badgeState = null;
        _badgeIndex = null;
    }

    private async Task<string?> SaveBaselineAsync(
        S3SyncSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            TryDeleteBaseline();
            _baselineHash = null;
            return null;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(json));
        if (string.Equals(_baselineHash, hash, StringComparison.Ordinal) && File.Exists(BaselinePath))
            return hash;

        var temporaryPath = BaselinePath + ".tmp";
        await using (var file = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzip.WriteAsync(json, cancellationToken);
        }
        await MoveFileWithRetryAsync(temporaryPath, BaselinePath, cancellationToken);
        _baselineHash = hash;
        return hash;
    }

    private void TryDeleteBaseline()
    {
        try
        {
            if (File.Exists(BaselinePath)) File.Delete(BaselinePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
