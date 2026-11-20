using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class SyncProviderRegistryTests
{
    [Fact]
    public void DefaultRegistryExposesOnlyTheBuiltInProviders()
    {
        var descriptors = SyncProviderRegistry.CreateDefault().Descriptors;

        Assert.Equal(new[] { SyncProvider.S3, SyncProvider.WebDav }, descriptors.Select(item => item.Provider));
        Assert.Equal(new[] { SyncProviderIds.S3, SyncProviderIds.WebDav }, descriptors.Select(item => item.Id));
        Assert.Equal("S3", descriptors[0].DisplayName);
        Assert.Equal("WebDAV", descriptors[1].DisplayName);
        Assert.True(descriptors[0].Capabilities.SupportsRangeRead);
        // WebDAV servers commonly rewrite or omit getlastmodified. The engine
        // must keep re-reading snapshots instead of trusting that metadata.
        Assert.False(descriptors[1].Capabilities.ReliableLastModified);
    }

    [Fact]
    public void RegistryPlacesICloudFirstWhenRegistered()
    {
        var registry = SyncProviderRegistry.CreateDefault()
            .Register(new StubFactory(SyncProvider.ICloud, SyncProviderIds.ICloud));

        Assert.Equal(
            new[] { SyncProvider.ICloud, SyncProvider.S3, SyncProvider.WebDav },
            registry.Descriptors.Select(item => item.Provider));
    }

    [Fact]
    public void ProviderIdsStayStableAndRejectRetiredProviders()
    {
        Assert.Equal("s3", SyncProviderIds.FromProvider(SyncProvider.S3));
        Assert.Equal("webdav", SyncProviderIds.FromProvider(SyncProvider.WebDav));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SyncProviderIds.FromProvider(SyncProvider.AccountServer));
    }

    [Fact]
    public void RegistryRejectsDuplicateAndReservedProviders()
    {
        var registry = new SyncProviderRegistry();
        registry.Register(new StubFactory(SyncProvider.S3, SyncProviderIds.S3));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new StubFactory(SyncProvider.S3, "s3-second")));
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new StubFactory(SyncProvider.AccountServer, "account-server")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            registry.Register(new StubFactory((SyncProvider)99, "future")));
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new StubFactory(SyncProvider.WebDav, " ")));
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void MissingProviderIsReportedAsUnsupported()
    {
        var registry = new SyncProviderRegistry();

        Assert.False(registry.TryGet(SyncProvider.S3, out _));
        Assert.Throws<NotSupportedException>(() => registry.Get(SyncProvider.S3));
    }

    [Fact]
    public async Task ServiceUsesTheStoreCreatedByTheInjectedRegistry()
    {
        var factory = new StubFactory(SyncProvider.S3, SyncProviderIds.S3);
        var registry = new SyncProviderRegistry().Register(factory);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var protector = new TestHelpers.PlaintextSecretProtector();
            using var accounts = new AccountService(paths, protector, dataRegion: AccountRegion.China);
            var service = new S3SyncService(
                paths,
                protector,
                accounts,
                AccountRegion.China,
                TestHelpers.ActivatedFeatures,
                registry);

            await service.TestConnectionAsync(new S3SyncSettings
            {
                Provider = SyncProvider.S3,
                ProviderChosenByUser = true,
                Endpoint = "https://s3.example.com",
                AccessKey = "access",
                SecretKey = "secret",
                Bucket = "library",
                Prefix = "libzora",
                EncryptionKey = "stub-sync-key"
            });

            Assert.Equal(1, factory.CreateCount);
            Assert.True(factory.Store.TestWriteCalled);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private sealed class StubFactory(SyncProvider provider, string id) : ISyncProviderFactory
    {
        public int CreateCount { get; private set; }
        public StubStore Store { get; } = new();

        public SyncProviderDescriptor Descriptor { get; } =
            new(provider, id, provider.ToString(), new SyncProviderCapabilities());

        public ISyncObjectStore Create(S3SyncSettings settings)
        {
            CreateCount++;
            return Store;
        }

        public string BuildStorageIdentity(S3SyncSettings settings) => $"stub|{id}|{settings.Prefix}";

        public bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
            left.Endpoint == right.Endpoint;
    }

    private sealed class StubStore : ISyncObjectStore
    {
        public bool TestWriteCalled { get; private set; }

        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(
            string prefix,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncObjectMetadata>>([]);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<SyncObjectRead> OpenReadAsync(
            string key,
            CancellationToken cancellationToken,
            long rangeStart = 0) =>
            throw new SyncObjectNotFoundException("stub store has no objects");

        public Task PutAsync(
            string key,
            Stream input,
            string contentType,
            CancellationToken cancellationToken,
            string? knownSha256 = null) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TestWriteAsync(string prefix, CancellationToken cancellationToken)
        {
            TestWriteCalled = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
