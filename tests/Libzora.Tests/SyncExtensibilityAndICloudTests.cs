using System.Collections.Concurrent;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Tests;

public sealed class SyncExtensibilityAndICloudTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libzora-syncext-" + Guid.NewGuid().ToString("N"));

    public SyncExtensibilityAndICloudTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ICloudAdaptiveUploadTimeoutScalesForLargeBooks()
    {
        var baseTimeout = TimeSpan.FromSeconds(60);
        // Small snapshot stays right around base timeout
        var smallTimeout = ICloudSyncObjectStore.ComputeUploadTimeout(baseTimeout, 4096);
        Assert.InRange(smallTimeout.TotalSeconds, 60, 61);
        // 100 MB book gets generous upload allowance (~460s, capped at 900s)
        var largeTimeout = ICloudSyncObjectStore.ComputeUploadTimeout(baseTimeout, 100L * 1024 * 1024);
        Assert.InRange(largeTimeout.TotalSeconds, 400, 500);
        // 1 GB book caps cleanly at 15 minutes
        var hugeTimeout = ICloudSyncObjectStore.ComputeUploadTimeout(baseTimeout, 1024L * 1024 * 1024);
        Assert.Equal(TimeSpan.FromSeconds(900), hugeTimeout);
    }

    [Fact]
    public async Task ICloudListObjectsRefreshesMetadataBeforeEnumerating()
    {
        var containerDir = Path.Combine(_root, "icloud-container");
        Directory.CreateDirectory(containerDir);
        var fs = new RefreshTrackingICloudFileSystem(containerDir, "acct-1");
        using var store = new ICloudSyncObjectStore(fs, new S3SyncSettings
        {
            Provider = SyncProvider.ICloud,
            ICloudAccountIdentity = "acct-1",
            Prefix = "libzora"
        });

        var items = await store.ListObjectsAsync("regions/china/libzora/", CancellationToken.None);
        Assert.Equal(1, fs.RefreshCalls);
        Assert.Contains(items, item => item.Key == "regions/china/libzora/devices/dev1/snapshot.json");
    }

    [Fact]
    public async Task GuardedStoreEnforcesMaxObjectBytesAndRetriesRateLimits()
    {
        var inner = new FlakyRateLimitedStore(failuresBeforeSuccess: 2);
        var delays = new List<TimeSpan>();
        using var guarded = new GuardedSyncObjectStore(
            inner,
            new SyncProviderCapabilities(MaxObjectBytes: 64),
            (span, _) => { delays.Add(span); return Task.CompletedTask; });

        // Oversized upload is rejected before touching the inner store
        using var tooBig = new MemoryStream(new byte[128]);
        await Assert.ThrowsAsync<SyncObjectTooLargeException>(() =>
            guarded.PutAsync("blobs/big.bin", tooBig, "application/octet-stream", CancellationToken.None));
        Assert.Equal(0, inner.PutCalls);

        // Normal upload retries cleanly after 2 SyncRateLimitedException responses
        using var ok = new MemoryStream(Encoding.UTF8.GetBytes("hello-encrypted-blob"));
        await guarded.PutAsync("blobs/ok.bin", ok, "application/octet-stream", CancellationToken.None);
        Assert.Equal(3, inner.PutCalls);
        Assert.Equal(2, delays.Count);
        Assert.Equal("hello-encrypted-blob", Encoding.UTF8.GetString(inner.Stored["blobs/ok.bin"]));
    }

    [Fact]
    public async Task CustomExtensibleProviderSupportsEncryptedMultiDeviceSyncAndProtectedSecrets()
    {
        var sharedRemote = new InMemoryObjectStore();
        var factory = new TestOfficialCloudProviderFactory(sharedRemote);
        var registry = SyncProviderRegistry.CreateDefault().Register(factory);

        // Device A (e.g. macOS)
        var pathsA = new AppPaths(Path.Combine(_root, "device-a"));
        var protectorA = new PrefixSecretProtector("devA:");
        var libraryA = new SqliteBookLibraryService(pathsA, new BookMetadataService());
        await libraryA.InitializeAsync();
        await new ReaderDataService(pathsA).InitializeAsync();
        var sampleEpub = Path.Combine(_root, "sample.txt");
        await File.WriteAllTextAsync(sampleEpub, "Cross-device encrypted sync test content");
        await libraryA.ImportAsync([sampleEpub]);
        var serviceA = new S3SyncService(pathsA, protectorA, registry, new AllowAllFeatures());

        var settingsA = new S3SyncSettings
        {
            Provider = SyncProvider.Custom,
            CustomProviderId = "official-cloud-v2",
            ProviderChosenByUser = true,
            Enabled = true,
            DownloadBookFilesOnSync = true,
            Prefix = "libzora",
            EncryptionKey = "shared-e2e-passphrase-2026",
            ProviderOptions = new Dictionary<string, string> { ["serverUrl"] = "https://sync.example.com" },
            ProviderSecrets = new Dictionary<string, string> { ["accessToken"] = "secret-bearer-token-xyz" }
        };

        await serviceA.SaveSettingsAsync("device-a", settingsA);
        var statusA = await serviceA.EvaluateConnectionStatusAsync();
        Assert.Equal(SyncConnectionHealth.Ready, statusA.Health);
        Assert.Equal("official-cloud-v2", statusA.ProviderId);

        // Verify ProviderSecrets are protected on disk (never plaintext) and round-trip cleanly
        var rawJson = await File.ReadAllTextAsync(Path.Combine(pathsA.Data, "s3-sync-settings.json"));
        Assert.DoesNotContain("secret-bearer-token-xyz", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("shared-e2e-passphrase-2026", rawJson, StringComparison.Ordinal);
        var reloadedA = await serviceA.LoadSettingsAsync();
        Assert.Equal("secret-bearer-token-xyz", reloadedA.Settings.ProviderSecrets?["accessToken"]);
        Assert.Equal("https://sync.example.com", reloadedA.Settings.ProviderOptions?["serverUrl"]);

        // Sync Device A -> shared remote
        _ = await serviceA.SyncAsync("device-a", reloadedA.Settings);
        Assert.NotEmpty(sharedRemote.Objects);
        // Verify remote payload is genuinely client-side encrypted (no plaintext book content on server)
        Assert.All(sharedRemote.Objects.Values, payload =>
            Assert.DoesNotContain("Cross-device encrypted sync test content", Encoding.UTF8.GetString(payload), StringComparison.Ordinal));

        // Device B (e.g. iOS) connects to the same custom provider with the same encryption key
        var pathsB = new AppPaths(Path.Combine(_root, "device-b"));
        var protectorB = new PrefixSecretProtector("devB:");
        var libraryB = new SqliteBookLibraryService(pathsB, new BookMetadataService());
        await libraryB.InitializeAsync();
        await new ReaderDataService(pathsB).InitializeAsync();
        var serviceB = new S3SyncService(pathsB, protectorB, registry, new AllowAllFeatures());
        await serviceB.SaveSettingsAsync("device-b", settingsA);
        var reloadedB = await serviceB.LoadSettingsAsync();

        var syncResultB = await serviceB.SyncAsync("device-b", reloadedB.Settings);
        Assert.Equal(1, syncResultB.BooksAdded);
        Assert.Equal(1, syncResultB.FilesDownloaded);
        var booksB = await libraryB.SearchAsync();
        Assert.Single(booksB);
    }

    private sealed class AllowAllFeatures : IDeviceFeatureAccess
    {
        public Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PrefixSecretProtector(string prefix) : ISecretProtector
    {
        private readonly byte[] _prefixBytes = Encoding.UTF8.GetBytes(prefix);
        public byte[] Protect(byte[] plaintext) => [.. _prefixBytes, .. plaintext];
        public byte[] Unprotect(byte[] ciphertext) =>
            ciphertext.AsSpan().StartsWith(_prefixBytes) ? ciphertext[_prefixBytes.Length..] : [];
    }

    private sealed class RefreshTrackingICloudFileSystem(string containerDir, string accountId) : IICloudSyncFileSystem
    {
        public int RefreshCalls { get; private set; }
        public string ContainerIdentifier => ICloudSyncContainer.Identifier;
        public string? AccountIdentity => accountId;
        public Task<string> GetContainerDirectoryAsync(CancellationToken ct) => Task.FromResult(containerDir);
        public bool ItemExists(string path) => File.Exists(path);
        public Task EnsureDownloadedAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureUploadedAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public T Coordinate<T>(string path, ICloudSyncFileAccess access, Func<string, T> accessor, CancellationToken ct) => accessor(path);
        public void DeleteItem(string path) { if (File.Exists(path)) File.Delete(path); }
        public Task RefreshDirectoryAsync(string directoryPath, CancellationToken ct)
        {
            RefreshCalls++;
            var snapshotDir = Path.Combine(directoryPath, "regions", "china", "libzora", "devices", "dev1");
            Directory.CreateDirectory(snapshotDir);
            File.WriteAllText(Path.Combine(snapshotDir, ".snapshot.json.icloud"), "placeholder");
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyRateLimitedStore(int failuresBeforeSuccess) : ISyncObjectStore
    {
        private int _remainingFailures = failuresBeforeSuccess;
        public int PutCalls { get; private set; }
        public Dictionary<string, byte[]> Stored { get; } = [];
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken ct) => Task.FromResult<IReadOnlyList<SyncObjectMetadata>>([]);
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Stored.ContainsKey(key));
        public Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken ct, long rangeStart = 0) => throw new SyncObjectNotFoundException(key);
        public Task PutAsync(string key, Stream input, string contentType, CancellationToken ct, string? knownSha256 = null)
        {
            PutCalls++;
            if (_remainingFailures-- > 0)
                throw new SyncRateLimitedException("Too many requests", TimeSpan.FromMilliseconds(5));
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            Stored[key] = ms.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
        public Task TestWriteAsync(string prefix, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class InMemoryObjectStore : ISyncObjectStore
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        public Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SyncObjectMetadata>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).Select(k => new SyncObjectMetadata(k, DateTimeOffset.UtcNow)).ToArray());
        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Objects.ContainsKey(key));
        public Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken ct, long rangeStart = 0)
        {
            if (!Objects.TryGetValue(key, out var data)) throw new SyncObjectNotFoundException(key);
            var stream = new MemoryStream(data, writable: false);
            return Task.FromResult(new SyncObjectRead(stream, data.Length, stream));
        }
        public Task PutAsync(string key, Stream input, string contentType, CancellationToken ct, string? knownSha256 = null)
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            Objects[key] = ms.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct) { Objects.TryRemove(key, out _); return Task.CompletedTask; }
        public Task TestWriteAsync(string prefix, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TestOfficialCloudProviderFactory(InMemoryObjectStore shared) : ISyncProviderFactory, ISyncProviderValidator
    {
        public SyncProviderDescriptor Descriptor { get; } = new(
            SyncProvider.Custom,
            "official-cloud-v2",
            "Official Cloud",
            new SyncProviderCapabilities(MaxObjectBytes: 100L * 1024 * 1024),
            new SyncProviderUiHints(SupportsProfileImportExport: false, RequiresInteractiveAuth: true, SupportsClientSideEncryption: true));

        public ISyncObjectStore Create(S3SyncSettings settings) => shared;

        public string BuildStorageIdentity(S3SyncSettings settings) => string.Join(
            "|", "official-cloud-v2",
            settings.ProviderOptions?.GetValueOrDefault("serverUrl") ?? string.Empty,
            settings.Prefix,
            settings.EncryptionKey.Length > 0
                ? $"encrypted:{SyncProviderIdentity.EncryptionKeyFingerprint(settings.EncryptionKey)}" : "plain");

        public bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
            string.Equals(left.ProviderOptions?.GetValueOrDefault("serverUrl"), right.ProviderOptions?.GetValueOrDefault("serverUrl"), StringComparison.OrdinalIgnoreCase);

        public string? Validate(S3SyncSettings settings)
        {
            var url = settings.ProviderOptions?.GetValueOrDefault("serverUrl");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
                return "请配置官方同步服务器地址。";
            if (string.IsNullOrWhiteSpace(settings.ProviderSecrets?.GetValueOrDefault("accessToken")))
                return "请先登录官方同步账号。";
            return null;
        }
    }
}
