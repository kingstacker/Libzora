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
/// Provider store, settings, status and on-demand download.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    /// <summary>
    /// Creates the storage adapter for one sync run. Provider factories are
    /// the only place that knows about a concrete backend; the engine sees only
    /// <see cref="ISyncObjectStore"/>.
    /// </summary>
    private ISyncObjectStore CreateStore(S3SyncSettings settings)
    {
        if (_accounts is not null && _dataRegion is { } expectedRegion && settings.AccountRegion != expectedRegion)
            throw new InvalidOperationException("同步区域与当前书库不一致，请检查区域设置。");
        var hasFactory = _providers.TryGet(settings, out var factory);
        var rawStore = _storeFactoryOverride?.Invoke(settings)
            ?? (hasFactory ? factory.Create(settings) : _providers.Get(settings).Create(settings));
        var capabilities = hasFactory ? factory.Descriptor.Capabilities : new SyncProviderCapabilities();
        ISyncObjectStore store = new GuardedSyncObjectStore(rawStore, capabilities);
        return _accounts is { } accounts && _dataRegion is { } region
            ? new RegionalSyncObjectStore(store, accounts, region)
            : store;
    }

    public IReadOnlyList<SyncProviderDescriptor> AvailableProviders => _providers.Descriptors;

    /// <summary>
    /// Evaluates the current connection health in a provider-agnostic way so
    /// desktop and mobile UIs can show consistent status for any sync provider.
    /// </summary>
    public async Task<SyncConnectionStatus> EvaluateConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _settingsStore.LoadAsync(cancellationToken, initializeIfMissing: false);
        var settings = stored.Settings;
        var providerId = settings.EffectiveProviderId;
        if (!_providers.TryGet(settings, out var factory))
        {
            return new SyncConnectionStatus(
                SyncConnectionHealth.UnsupportedOnPlatform,
                providerId,
                settings.ProviderName,
                UiText.Get("当前平台不支持此同步方式。"));
        }

        var displayName = factory.Descriptor.DisplayName;
        if (!settings.Enabled)
        {
            return new SyncConnectionStatus(SyncConnectionHealth.Disabled, factory.Descriptor.Id, displayName);
        }

        var validation = ValidateWithProvider(settings, factory);
        if (validation is not null)
        {
            return new SyncConnectionStatus(SyncConnectionHealth.NeedsConfiguration, factory.Descriptor.Id, displayName, validation);
        }

        if (factory is ISyncProviderAccountBinding binding)
        {
            try
            {
                var bound = binding.BindAccount(settings);
                if (bound != settings)
                {
                    return new SyncConnectionStatus(SyncConnectionHealth.AccountChanged, factory.Descriptor.Id, displayName,
                        UiText.Get("系统同步账户已更改，请重新保存设置。"));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return new SyncConnectionStatus(SyncConnectionHealth.AccountChanged, factory.Descriptor.Id, displayName, ex.Message);
            }
        }

        return new SyncConnectionStatus(SyncConnectionHealth.Ready, factory.Descriptor.Id, displayName);
    }

    public async Task<S3SyncStoredSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _settingsStore.LoadAsync(cancellationToken);
        if (_dataRegion is { } region && stored.Settings.AccountRegion != region)
            stored = stored with { Settings = stored.Settings with { AccountRegion = region, Enabled = false } };
        // A backup may carry an Apple-only or custom provider onto another OS.
        // Keep the choice visible without rerouting it to leftover credentials.
        if (!_providers.TryGet(stored.Settings, out var factory))
            stored = stored with { Settings = stored.Settings with { Enabled = false } };
        else if (stored.Settings.Enabled && factory is ISyncProviderAccountBinding binding)
        {
            // A signed-out/switched system account must not automatically
            // receive this library. Only an explicit settings save can rebind.
            try
            {
                var bound = binding.BindAccount(stored.Settings);
                if (bound != stored.Settings)
                    stored = stored with { Settings = stored.Settings with { Enabled = false } };
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                stored = stored with { Settings = stored.Settings with { Enabled = false } };
            }
        }
        return stored;
    }

    // A later upload can fail after settings have already been committed.
    public event EventHandler? RemoteSettingsApplied;

    public async Task SaveSettingsAsync(
        string deviceId,
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        if (_providers.TryGet(normalized, out var factory))
        {
            if (factory is ISyncProviderValidator validator)
                normalized = validator.Normalize(normalized);
            if (normalized.Enabled && factory is ISyncProviderAccountBinding binding)
                normalized = binding.BindAccount(normalized, allowAccountChange: true);
        }
        else if (normalized.Enabled)
        {
            throw new NotSupportedException(UiText.Get("当前平台不支持此同步方式。"));
        }
        if (_dataRegion is { } dataRegion)
        {
            await _accounts!.EnsureSyncRegionAsync(dataRegion, cancellationToken);
            normalized = normalized with { AccountRegion = dataRegion };
        }
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var current = await _settingsStore.LoadAsync(cancellationToken, initializeIfMissing: false);
            if (current.Settings.EncryptionKey != normalized.EncryptionKey
                && SameRemoteDirectory(current.Settings, normalized))
            {
                var state = await LoadStateAsync(NormalizeDeviceId(deviceId), BuildStorageIdentity(current.Settings), cancellationToken);
                if (state.LastUploadedSnapshot is not null)
                    throw new InvalidOperationException(UiText.Get("当前同步目录已使用原加密配置。更换密钥或启停加密时，请改用新的同步子目录，并在其他设备上配置相同的前缀和密钥。"));
            }
            await _settingsStore.SaveAsync(deviceId, normalized, cancellationToken);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Clears the local change-detection baseline after a portable backup has
    /// replaced the database and library. The next sync treats the imported
    /// package as the current local state instead of turning package omissions
    /// into deletion tombstones.
    /// </summary>
    public async Task ResetLocalBaselineAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        deviceId = NormalizeDeviceId(deviceId);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
            await ResetLocalBaselineCoreAsync(deviceId, cancellationToken);
        }
        finally
        {
            ResetEncryptionSession();
            _syncGate.Release();
        }
    }

    public async Task TestConnectionAsync(
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTransferSettings(settings);
        ThrowIfInvalid(normalized);

        await _features.EnsureFeatureAsync(MemberEntitlements.SyncFeature(normalized.Provider), cancellationToken);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            ResetEncryptionSession();
            using var client = CreateStore(normalized);
            // Validate every device snapshot before a probe: a supported older
            // device must not hide another device's incompatible protocol.
            // Serialize with sync so session key erasure cannot race decryption.
            var keys = await ListSnapshotKeysAsync(client, normalized, cancellationToken);
            foreach (var key in keys)
            {
                var payload = await DownloadObjectBytesAsync(client, key, cancellationToken);
                var snapshot = await DecodeSnapshotAsync(payload, normalized.EncryptionKey, cancellationToken);
                ValidateSnapshotCompatibility(snapshot);
            }
            await client.TestWriteAsync(normalized.Prefix, cancellationToken);
        }
        finally
        {
            ResetEncryptionSession();
            _syncGate.Release();
        }
    }

    /// Local-only summary for the sync details dialog. It reads the persisted
    /// baseline state and never contacts the remote object store.
    /// </summary>
    public async Task<S3SyncStatusSummary> GetStatusAsync(
        string deviceId,
        S3SyncSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = S3SyncSettings.Normalize(settings);
        if (!_providers.TryGet(normalized.Provider, out _))
            return new S3SyncStatusSummary(null, 0, 0, 0, 0, 0, 0, null);
        deviceId = NormalizeDeviceId(deviceId);
        var storageIdentity = BuildStorageIdentity(normalized);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(deviceId, storageIdentity, cancellationToken);
            var snapshot = state.LastUploadedSnapshot;
            return new S3SyncStatusSummary(
                state.LastSyncAt,
                state.RemoteBookIds?.Count ?? 0,
                state.RemoteOnlyFileHashes?.Count ?? 0,
                snapshot?.Books.Count ?? 0,
                snapshot?.Files.Count ?? 0,
                snapshot?.Annotations.Count ?? 0,
                snapshot?.Tombstones.Count ?? 0,
                snapshot?.CreatedAt);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Downloads one book file on demand. A normal sync only writes the file's
    /// metadata to SQLite; opening the book is the explicit user action that
    /// fetches the content-addressed object.
    /// </summary>
    public async Task<string> EnsureBookFileDownloadedAsync(
        string deviceId,
        S3SyncSettings settings,
        BookFile file,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var normalized = NormalizeTransferSettings(settings);
        ThrowIfInvalid(normalized);
        await _features.EnsureFeatureAsync(MemberEntitlements.SyncFeature(normalized.Provider), cancellationToken);
        if (!IsSha256(file.Sha256))
            throw new InvalidDataException("同步文件缺少有效的 SHA-256 校验值。");

        var targetPath = ResolveDataPath(file.RelativePath);
        if (targetPath is null)
            throw new InvalidDataException("同步文件目标路径无效。");

        deviceId = NormalizeDeviceId(deviceId);
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            ResetEncryptionSession();
            _encryptionSession = normalized.EncryptionKey.Length == 0
                ? null
                : CreateEncryptionSession(normalized.EncryptionKey);
            _paths.EnsureDirectories();

            var storageIdentity = BuildStorageIdentity(normalized);
            var state = await LoadStateAsync(deviceId, storageIdentity, cancellationToken);
            using var client = CreateStore(normalized);
            await InitializeBlobNamingForDownloadAsync(client, normalized, deviceId, storageIdentity, state, cancellationToken);
            progress?.Report(UiText.Get("正在下载《{0}》…", Path.GetFileName(file.RelativePath)));
            var needsDownload = true;
            if (File.Exists(targetPath))
            {
                try
                {
                    needsDownload = !string.Equals(
                        await GetCachedFileHashAsync(targetPath, cancellationToken),
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (IOException)
                {
                    needsDownload = true;
                }
            }
            var staged = needsDownload
                ? await DownloadBlobToStagingAsync(
                    client,
                    normalized,
                    file.Sha256,
                    targetPath,
                    cancellationToken)
                : null;
            try
            {
                // Network I/O must not hold the library writer lease. Recheck
                // the live row under the same lease used by DeleteFileAsync/
                // DeleteAsync before publishing, so a format deleted meanwhile
                // is never resurrected at its old path.
                using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
                if (!await BookFileMatchesAsync(file, targetPath, cancellationToken))
                    throw new SyncedBookFileChangedException(
                        "该书籍格式已在下载期间被删除或更改，已取消写入。",
                        file.RelativePath);

                if (staged is not null)
                {
                    PublishStagedSyncBlob(staged.Path, targetPath);
                    progress?.Report(UiText.Get("已下载 {0}", Path.GetFileName(targetPath)));
                }

                state.DeviceId = deviceId;
                state.StorageIdentity = storageIdentity;
                state.RemoteOnlyFileHashes = (state.RemoteOnlyFileHashes ?? [])
                    .Where(hash => !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await SaveStateAsync(state, cancellationToken);
                return targetPath;
            }
            finally
            {
                if (staged is not null)
                    TryDeleteSyncPartialFile(staged.Path);
            }
        }
        finally
        {
            ResetEncryptionSession();
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Returns the four-state badge value for the supplied local library view.
    /// The comparison uses only the local metadata baseline and file existence;
    /// it never contacts the remote object store.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, BookSyncStatus>> GetBookSyncStatusesAsync(
        string deviceId,
        S3SyncSettings settings,
        IReadOnlyCollection<Book> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        var statuses = books.ToDictionary(book => book.Id, _ => BookSyncStatus.NotSynced);
        var normalized = S3SyncSettings.Normalize(settings);
        if (!normalized.IsConfigured || !_providers.TryGet(normalized.Provider, out _)) return statuses;

        // The local state file is replaced during SyncAsync. Serialize this
        // read with the same gate, otherwise a badge refresh can observe the
        // transient/empty state and incorrectly mark every book NotSynced.
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadBadgeStateAsync(
                NormalizeDeviceId(deviceId),
                BuildStorageIdentity(normalized),
                cancellationToken);
            var baseline = state.LastUploadedSnapshot;
            if (baseline is null) return statuses;

            if (_badgeIndex is null || !ReferenceEquals(_badgeIndex.State, state))
                _badgeIndex = new BadgeIndex(state);
            var remoteBookIds = _badgeIndex.RemoteBooks;
            var remoteOnlyFileHashes = _badgeIndex.RemoteHashes;
            var booksById = _badgeIndex.Books;
            var filesByBook = _badgeIndex.Files;
            foreach (var book in books)
            {
                var baselineBook = booksById.GetValueOrDefault(book.Id);
                var baselineFiles = filesByBook[book.Id];
                if (baselineBook is null
                    || book.UpdatedAt != baselineBook.UpdatedAt
                    || !BookFilesMatchBaseline(book.Files, baselineFiles))
                {
                    statuses[book.Id] = BookSyncStatus.NotSynced;
                    continue;
                }

                var hasRemoteOnlyFile = book.Files.Any(file => remoteOnlyFileHashes.Contains(file.Sha256));
                var hasLocalFile = book.Files.Any(file =>
                {
                    if (remoteOnlyFileHashes.Contains(file.Sha256)) return false;
                    var path = ResolveDataPath(file.RelativePath);
                    return path is not null && File.Exists(path);
                });
                if (!remoteBookIds.Contains(book.Id) && !hasRemoteOnlyFile && hasLocalFile)
                {
                    statuses[book.Id] = BookSyncStatus.Synced;
                    continue;
                }

                statuses[book.Id] = hasLocalFile
                    ? BookSyncStatus.Downloaded
                    : BookSyncStatus.NotDownloaded;
            }

            return statuses;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static bool BookFilesMatchBaseline(
        IEnumerable<BookFile> currentFiles,
        IEnumerable<S3SyncBookFile> baselineFiles)
    {
        var current = currentFiles.ToArray();
        var baseline = baselineFiles.ToArray();
        return current.Length == baseline.Length
            && current.All(file => baseline.Any(remote =>
                remote.Id == file.Id
                && string.Equals(remote.Format, file.Format, StringComparison.OrdinalIgnoreCase)
                && remote.Size == file.Size
                && string.Equals(remote.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)));
    }
}
