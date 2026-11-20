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
/// An on-demand download completed after its BookFiles row was deleted or
/// changed. Callers must not treat the staged bytes as a valid local file.
/// </summary>
public sealed class SyncedBookFileChangedException(string message, string relativePath) : IOException(message)
{
    public string RelativePath { get; } = relativePath;
}

/// <summary>
/// S3/WebDAV synchronisation for Libzora's logical data.
///
/// The live SQLite database is never placed in the bucket. Each device owns a
/// compressed snapshot and book bytes are stored as content-addressed objects
/// (keyed names when encrypted). Snapshots are merged by stable IDs/content hashes,
/// and timestamped tombstones carry deletions between devices. This follows
/// the important part of SiYuan/dejavu's design while keeping Libzora's
/// existing SQLite schema and reader services intact.
/// </summary>

public sealed partial class S3SyncService : ISyncService
{
    // Keyed object names and padded blobs require reader 6. Future additive
    // changes can still be read when MinimumReaderVersion explicitly permits it.
    private const int SnapshotVersion = 6;
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private const int EncryptionSaltBytes = 16;
    private const int EncryptionNonceBytes = 12;
    private const int EncryptionTagBytes = 16;
    private const int EncryptionKeyBytes = 32;
    private const int EncryptionIterations = 120_000;
    private const int BlobEncryptionChunkBytes = 1024 * 1024;
    private const double LargeDeletionRatio = 0.50;
    private const int LargeDeletionMinimumEntities = 10;
    private static readonly TimeSpan RetiredDeviceSnapshotRetention = TimeSpan.FromDays(365);
    private static readonly TimeSpan UnreferencedBlobRetention = TimeSpan.FromDays(30);
    private static readonly byte[] EncryptionMagic = Encoding.ASCII.GetBytes("LIBZORA-SYNC1");
    private static readonly byte[] StreamingEncryptionMagic = Encoding.ASCII.GetBytes("LIBZORA-SYNC2");
    private static readonly byte[] PaddedEncryptionMagic = Encoding.ASCII.GetBytes("LIBZORA-SYNC3");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly AppPaths _paths;
    private readonly S3SyncSettingsStore _settingsStore;
    private readonly SyncProviderRegistry _providers;
    private readonly Func<S3SyncSettings, ISyncObjectStore>? _storeFactoryOverride;
    private readonly IDeviceFeatureAccess _features;
    private readonly AccountService? _accounts;
    private readonly AccountRegion? _dataRegion;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedFileHash> _fileHashCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _derivedEncryptionKeys =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _legacyBlobGate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _legacyBlobKeys;
    private EncryptionSession? _encryptionSession;

    private sealed record CachedFileHash(long Length, DateTime LastWriteTimeUtc, string Sha256);

    private sealed class EncryptionSession
    {
        public EncryptionSession(string passphrase, byte[] salt, byte[] key)
        {
            Passphrase = passphrase;
            Salt = salt;
            Key = key;
        }

        public string Passphrase { get; }
        public byte[] Salt { get; }
        public byte[] Key { get; }
    }

    public S3SyncService(AppPaths paths, ISecretProtector protector)
        : this(paths, protector, SyncProviderRegistry.CreateDefault(), features: null)
    {
    }

    public S3SyncService(AppPaths paths, ISecretProtector protector, SyncProviderRegistry providers,
        IDeviceFeatureAccess? features = null)
        : this(paths, protector, providers, features, storeFactoryOverride: null)
    {
    }

    public S3SyncService(AppPaths paths, ISecretProtector protector, AccountService accounts, AccountRegion dataRegion,
        IDeviceFeatureAccess? features = null)
        : this(paths, protector, SyncProviderRegistry.CreateDefault(), features ?? new DeviceMembershipService(accounts))
    {
        _accounts = accounts;
        _dataRegion = dataRegion;
    }

    /// <summary>
    /// Composition overload for platform heads that register additional sync
    /// providers. The caller should start from
    /// <see cref="SyncProviderRegistry.CreateDefault"/> and add factories;
    /// the built-in S3/WebDAV behavior is unchanged.
    /// </summary>
    public S3SyncService(AppPaths paths, ISecretProtector protector, AccountService accounts, AccountRegion dataRegion,
        IDeviceFeatureAccess? features, SyncProviderRegistry providers)
        : this(paths, protector, providers, features ?? new DeviceMembershipService(accounts))
    {
        _accounts = accounts;
        _dataRegion = dataRegion;
    }

    internal S3SyncService(AppPaths paths, ISecretProtector protector, Func<S3SyncSettings, IAmazonS3> clientFactory,
        IDeviceFeatureAccess? features = null)
        : this(paths, protector, SyncProviderRegistry.CreateDefault(), features,
            settings => new S3SyncObjectStore(settings, clientFactory(settings)))
    {
    }

    internal S3SyncService(AppPaths paths, ISecretProtector protector, Func<S3SyncSettings, ISyncObjectStore> clientFactory,
        IDeviceFeatureAccess? features = null)
        : this(paths, protector, SyncProviderRegistry.CreateDefault(), features, clientFactory)
    {
    }

    private S3SyncService(AppPaths paths, ISecretProtector protector, SyncProviderRegistry providers,
        IDeviceFeatureAccess? features, Func<S3SyncSettings, ISyncObjectStore>? storeFactoryOverride = null)
    {
        _paths = paths;
        _providers = providers;
        _storeFactoryOverride = storeFactoryOverride;
        _features = features ?? DeniedDeviceFeatureAccess.Instance;
        _settingsStore = new S3SyncSettingsStore(paths, protector);
        _appSettingsStore = new AppSettingsStore(paths);
        _aiSettingsStore = new AiSettingsStore(paths, protector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, protector);
    }
}
