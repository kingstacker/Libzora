using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed class ICloudSyncTests
{
    [Fact]
    public void ICloudHasANewStableValueAndDoesNotRequireOtherProvidersCredentials()
    {
        var settings = new S3SyncSettings
        {
            Provider = SyncProvider.ICloud, ProviderChosenByUser = true, EncryptionKey = "icloud-test-key",
            Endpoint = "invalid inactive S3 endpoint", WebDavEndpoint = "invalid inactive WebDAV endpoint"
        };
        Assert.Equal(3, (int)SyncProvider.ICloud);
        Assert.Equal(2, (int)SyncProvider.AccountServer);
        Assert.Equal("icloud", SyncProviderIds.FromProvider(settings.Provider));
        Assert.Equal(DeviceFeature.ICloud, MemberEntitlements.SyncFeature(settings.Provider));
        Assert.True(settings.IsConfigured);
        Assert.False((settings with { EncryptionKey = "" }).IsConfigured);
        Assert.True(settings.HasChosenCustomProvider);
        Assert.Equal("iCloud", settings.ProviderName);
        Assert.False((settings with { Prefix = "books\0invalid" }).IsConfigured);
        Assert.Throws<NotSupportedException>(() => SyncConnectionProfileService.FileExtension(settings.Provider));
        Assert.False(SyncProviderRegistry.CreateDefault().TryGet(SyncProvider.ICloud, out _));
        using var signedOut = new FakeICloudFileSystem { AccountIdentity = null };
        Assert.Contains("unbound", new ICloudSyncProviderFactory(signedOut).BuildStorageIdentity(settings));
    }

    [Fact]
    public void ProviderDeclaresEventualConsistencyAndIsolatesAccountDirectoryAndEncryptionBaselines()
    {
        using var fileSystem = new FakeICloudFileSystem();
        var factory = new ICloudSyncProviderFactory(fileSystem);
        var settings = factory.BindAccount(Settings());
        var capabilities = factory.Descriptor.Capabilities;
        Assert.False(capabilities.ReliableListing);
        Assert.False(capabilities.ReliableLastModified);
        Assert.False(capabilities.SupportsRangeRead);
        Assert.True(capabilities.SupportsAtomicReplace);
        Assert.Equal(1, capabilities.MaxConcurrentRequests);
        var identity = factory.BuildStorageIdentity(settings);
        Assert.NotEqual(identity, factory.BuildStorageIdentity(settings with { Prefix = "other" }));
        Assert.NotEqual(identity, factory.BuildStorageIdentity(settings with { EncryptionKey = "private-key" }));
        fileSystem.AccountIdentity = "other-account-fingerprint";
        Assert.Throws<InvalidOperationException>(() => factory.BindAccount(settings));
        var rebound = factory.BindAccount(settings, allowAccountChange: true);
        Assert.NotEqual(identity, factory.BuildStorageIdentity(rebound));
        Assert.False(factory.IsSameRemoteDirectory(settings, rebound));
    }

    [Fact]
    public void OnlyICloudCanonicalizesCaseAndUnicodeDirectoryAliases()
    {
        var raw = Settings() with { Prefix = "Library/Café" };
        var apple = S3SyncSettings.Normalize(raw);
        Assert.Equal("library/café", apple.Prefix);
        Assert.Equal("Library/Café", S3SyncSettings.Normalize(raw with { Provider = SyncProvider.S3 }).Prefix);
        Assert.Equal("Library/Café", S3SyncSettings.Normalize(raw with { Provider = SyncProvider.WebDav }).Prefix);
    }

    [Fact]
    public async Task ChangingOnlyPrefixCaseCannotBypassThePublishedEncryptionKeyGuard()
    {
        using var fileSystem = new FakeICloudFileSystem();
        await using var device = await Device.CreateAsync(fileSystem, Settings() with { EncryptionKey = "original-key" });
        await device.SyncAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.Service.SaveSettingsAsync(device.Id,
            device.Settings with { Prefix = "LIBZORA", EncryptionKey = "changed-key" }));
        Assert.Equal("original-key", (await device.Service.LoadSettingsAsync()).Settings.EncryptionKey);
    }

    [Fact]
    public async Task StoreAtomicallyReplacesFilesAndReturnsStableMaterializedReadCopies()
    {
        using var fileSystem = new FakeICloudFileSystem();
        using var store = new ICloudSyncProviderFactory(fileSystem).Create(Settings());
        const string key = "regions/china/libzora/objects/plain/book.bin";
        using var original = new MemoryStream("original complete object"u8.ToArray());
        await store.PutAsync(key, original, "application/octet-stream", CancellationToken.None);
        Assert.True(original.CanRead);
        using var stableRead = await store.OpenReadAsync(key, CancellationToken.None);
        using var replacement = new MemoryStream("replacement complete object"u8.ToArray());
        await store.PutAsync(key, replacement, "application/octet-stream", CancellationToken.None);
        Assert.Equal("original complete object", await ReadTextAsync(stableRead));
        fileSystem.Evict(fileSystem.PathFor(key));
        Assert.True(await store.ExistsAsync(key, CancellationToken.None));
        Assert.Equal(key, Assert.Single(await store.ListKeysAsync("regions/china/libzora/objects/", CancellationToken.None)));
        using var downloaded = await store.OpenReadAsync(key, CancellationToken.None, rangeStart: 3);
        Assert.False(downloaded.IsPartialResponse);
        Assert.Equal("replacement complete object", await ReadTextAsync(downloaded));
        Assert.Contains(ICloudSyncFileAccess.Read, fileSystem.Accesses);
        Assert.Contains(ICloudSyncFileAccess.Write, fileSystem.Accesses);
        await store.DeleteAsync(key, CancellationToken.None);
        await store.DeleteAsync(key, CancellationToken.None);
        Assert.False(await store.ExistsAsync(key, CancellationToken.None));
        Assert.Contains(ICloudSyncFileAccess.Delete, fileSystem.Accesses);
        await store.TestWriteAsync("libzora", CancellationToken.None);
        Assert.Empty(await store.ListKeysAsync("libzora/", CancellationToken.None));
        Assert.DoesNotContain(Directory.EnumerateFiles(fileSystem.Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(".libzora-upload-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokenInputCannotPublishAPartialReplacement()
    {
        using var fileSystem = new FakeICloudFileSystem();
        using var store = new ICloudSyncProviderFactory(fileSystem).Create(Settings());
        const string key = "libzora/devices/device/snapshot.bin";
        using var original = new MemoryStream("valid snapshot"u8.ToArray());
        await store.PutAsync(key, original, "application/octet-stream", CancellationToken.None);
        using var broken = new BrokenInputStream();
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(key, broken, "application/octet-stream", CancellationToken.None));
        using var read = await store.OpenReadAsync(key, CancellationToken.None);
        Assert.Equal("valid snapshot", await ReadTextAsync(read));
        Assert.Equal(key, Assert.Single(await store.ListKeysAsync("libzora/", CancellationToken.None)));
    }

    [Fact]
    public async Task MissingDownloadsListingFailuresAndCancellationNeverBecomeEmptyData()
    {
        using var fileSystem = new FakeICloudFileSystem();
        using var store = new ICloudSyncProviderFactory(fileSystem).Create(Settings());
        await Assert.ThrowsAsync<SyncObjectNotFoundException>(() => store.OpenReadAsync("missing.bin", CancellationToken.None));
        fileSystem.CoordinationFailure = new IOException("fixture listing unavailable");
        await Assert.ThrowsAsync<SyncListingUnavailableException>(() => store.ListKeysAsync("libzora/", CancellationToken.None));
        fileSystem.CoordinationFailure = null;
        fileSystem.BeforeDownload = (_, token) => Task.Delay(Timeout.Infinite, token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync("waiting.bin", cancellation.Token));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/outside")]
    [InlineData("libzora/../outside")]
    [InlineData("libzora//outside")]
    [InlineData("libzora\\outside")]
    [InlineData("libzora/invalid\0key")]
    public async Task StoreRejectsKeysOutsideItsNamespace(string key)
    {
        using var fileSystem = new FakeICloudFileSystem();
        using var store = new ICloudSyncProviderFactory(fileSystem).Create(Settings());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task SymbolicLinksCannotWriteOutsideTheICloudContainer()
    {
        using var fileSystem = new FakeICloudFileSystem();
        using var outside = new FakeICloudFileSystem();
        Directory.CreateSymbolicLink(Path.Combine(fileSystem.Root, "link"), outside.Root);
        using var store = new ICloudSyncProviderFactory(fileSystem).Create(Settings());
        using var bytes = new MemoryStream("must not escape"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("link/outside.bin", bytes, "application/octet-stream", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(outside.Root, "outside.bin")));
    }

    [Fact]
    public async Task EmptyEncryptionKeyIsRejectedForSyncAndConnectionTest()
    {
        using var fileSystem = new FakeICloudFileSystem();
        await using var device = await Device.CreateAsync(fileSystem, Settings() with { EncryptionKey = "" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.SyncAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.Service.TestConnectionAsync(device.Settings));
    }

    [Fact]
    public async Task SavingBindsTheAccountAndSwitchingOrSigningOutDisablesAutomaticSyncWithoutRewritingSettings()
    {
        using var fileSystem = new FakeICloudFileSystem();
        await using var device = await Device.CreateAsync(fileSystem);
        var saved = (await device.Service.LoadSettingsAsync()).Settings;
        Assert.True(saved.Enabled);
        Assert.Equal(fileSystem.AccountIdentity, saved.ICloudAccountIdentity);
        fileSystem.AccountIdentity = "new-account-fingerprint";
        Assert.False((await device.Service.LoadSettingsAsync()).Settings.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.Service.SyncAsync(device.Id, saved));
        Assert.Empty(Directory.EnumerateFiles(fileSystem.Root, "*", SearchOption.AllDirectories));
        var persisted = await new S3SyncSettingsStore(device.Paths, new TestHelpers.PlaintextSecretProtector()).LoadAsync();
        Assert.Equal(saved, persisted.Settings);
        await device.Service.SaveSettingsAsync(device.Id, saved);
        Assert.Equal(fileSystem.AccountIdentity, (await device.Service.LoadSettingsAsync()).Settings.ICloudAccountIdentity);
        fileSystem.AccountIdentity = null;
        Assert.False((await device.Service.LoadSettingsAsync()).Settings.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.Service.TestConnectionAsync(Settings()));
    }

    [Fact]
    public async Task UnsupportedPlatformKeepsICloudDisabledInsteadOfReusingS3Credentials()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var settings = Settings() with { AccessKey = "retained-access", SecretKey = "retained-secret", Bucket = "retained-bucket" };
            await new S3SyncSettingsStore(paths, protector).SaveAsync(Guid.NewGuid().ToString("N"), settings);
            var service = new S3SyncService(paths, protector);
            var loaded = await service.LoadSettingsAsync();
            Assert.Equal(SyncProvider.ICloud, loaded.Settings.Provider);
            Assert.False(loaded.Settings.Enabled);
            Assert.Equal(settings.SecretKey, loaded.Settings.SecretKey);
            Assert.Equal(2, service.AvailableProviders.Count);
            await Assert.ThrowsAsync<NotSupportedException>(() => service.TestConnectionAsync(settings));
            Assert.Null((await service.GetStatusAsync(loaded.DeviceId, loaded.Settings)).LastSyncAt);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task TwoDevicesUseTheExistingBookProgressAnnotationEncryptionAndDeletionProtocol()
    {
        using var fileSystem = new FakeICloudFileSystem();
        var settings = Settings() with { EncryptionKey = "shared-icloud-test-key" };
        await using var a = await Device.CreateAsync(fileSystem, settings);
        await using var b = await Device.CreateAsync(fileSystem, settings);
        var book = await a.AddBookAsync();
        var annotation = new ReaderAnnotation
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            StartOffset = 0, EndOffset = 4, SelectedText = "text", Note = "来自设备 A"
        };
        await a.Reader.SaveAnnotationAsync(annotation);
        await a.Reader.SaveProgressAsync(new ReaderProgressRow(book.BookId, book.FileId,
            "chapter.xhtml", "p1", 1, 12, 45, 0, DateTimeOffset.UtcNow));
        Assert.False((await a.SyncAsync()).IsPartial);
        var snapshot = fileSystem.PathFor($"regions/china/libzora/devices/{a.Id}/snapshot.bin");
        Assert.True((await File.ReadAllBytesAsync(snapshot)).AsSpan().StartsWith("LIBZORA-SYNC1"u8));
        // Both snapshots and book objects can be dataless on another device.
        fileSystem.Evict(snapshot);
        var blob = Assert.Single(Directory.EnumerateFiles(fileSystem.Root, "*", SearchOption.AllDirectories),
            path => path.Replace('\\', '/').Contains("/objects/", StringComparison.Ordinal));
        fileSystem.Evict(blob);
        var result = await b.SyncAsync();
        Assert.False(result.IsPartial);
        Assert.Equal(1, result.BooksAdded);
        Assert.Equal(0, result.FilesDownloaded);
        var remoteBook = Assert.Single(await b.Library.SearchAsync());
        var downloaded = await b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, Assert.Single(remoteBook.Files));
        Assert.Equal(book.Bytes, await File.ReadAllBytesAsync(downloaded));
        Assert.Equal(45, (await b.Reader.GetProgressAsync(book.FileId))!.ProgressPercent);
        var remoteNote = Assert.Single(await b.Reader.GetAllAnnotationsAsync());
        Assert.Equal(annotation.Note, remoteNote.Note);
        remoteNote.Note = "来自设备 B";
        remoteNote.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await b.Reader.SaveAnnotationAsync(remoteNote);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal(remoteNote.Note, Assert.Single(await a.Reader.GetAllAnnotationsAsync()).Note);
        await b.Reader.DeleteAnnotationAsync(remoteNote.Id);
        await b.Library.DeleteAsync(book.BookId);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Empty(await a.Library.SearchAsync());
        Assert.Empty(await a.Reader.GetAllAnnotationsAsync());
        // Eventual listings must never enable blob/snapshot garbage collection.
        Assert.True(File.Exists(blob));
        Assert.True(File.Exists(snapshot));
    }

    [Fact]
    public async Task PortableBackupsExcludeTheSystemAccountBindingAndRestoreICloudDisabled()
    {
        using var fileSystem = new FakeICloudFileSystem();
        await using var source = await Device.CreateAsync(fileSystem, Settings() with { EncryptionKey = "backup-key" });
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var protector = new TestHelpers.PlaintextSecretProtector();
            var target = new AppPaths(Path.Combine(root, "target"));
            await new SqliteBookLibraryService(target, new BookMetadataService()).InitializeAsync();
            await new ReaderDataService(target).InitializeAsync();
            var path = Path.Combine(root, "icloud.libzora");
            await new AppBackupService(source.Paths, protector).ExportAsync(path);
            using (var zip = await BackupTests.OpenBackupArchiveAsync(path, key: "backup-key"))
            using (var reader = new StreamReader(zip.GetEntry("settings/settings.json")!.Open()))
                Assert.DoesNotContain(fileSystem.AccountIdentity!, await reader.ReadToEndAsync(), StringComparison.Ordinal);
            await BackupTests.SetBackupKeyAsync(target, "backup-key");
            var restored = (await new AppBackupService(target, protector).ImportAsync(path)).S3Settings!;
            Assert.Equal(SyncProvider.ICloud, restored.Provider);
            Assert.True(restored.ProviderChosenByUser);
            Assert.False(restored.Enabled);
            Assert.Empty(restored.ICloudAccountIdentity);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RetryMustConfirmListedLocalObjectsBeforePublishingASnapshot()
    {
        using var fileSystem = new FakeICloudFileSystem();
        var settings = Settings() with { EncryptionKey = "retry-key" };
        await using var device = await Device.CreateAsync(fileSystem, settings);
        var book = await device.AddBookAsync();
        var snapshot = fileSystem.PathFor($"regions/china/libzora/devices/{device.Id}/snapshot.bin");
        var uploadChecks = 0;
        fileSystem.BeforeUpload = (path, _) =>
        {
            if (path.Replace('\\', '/').Contains("/objects/", StringComparison.Ordinal))
            {
                uploadChecks++;
                throw new IOException("fixture cloud quota exceeded");
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IOException>(() => device.SyncAsync());
        var localObject = Assert.Single(Directory.EnumerateFiles(fileSystem.Root, "*", SearchOption.AllDirectories),
            path => path.Replace('\\', '/').Contains("/objects/", StringComparison.Ordinal));
        Assert.False(File.Exists(snapshot));
        await Assert.ThrowsAsync<IOException>(() => device.SyncAsync());
        Assert.Equal(2, uploadChecks);
        Assert.False(File.Exists(snapshot));

        fileSystem.BeforeUpload = null;
        Assert.False((await device.SyncAsync()).IsPartial);
        Assert.True(File.Exists(snapshot));
        Assert.True(File.Exists(localObject));
        await using var other = await Device.CreateAsync(fileSystem, settings);
        Assert.Equal(1, (await other.SyncAsync()).BooksAdded);
        var imported = Assert.Single(await other.Library.SearchAsync());
        var downloaded = await other.Service.EnsureBookFileDownloadedAsync(other.Id, other.Settings, Assert.Single(imported.Files));
        Assert.Equal(book.Bytes, await File.ReadAllBytesAsync(downloaded));
    }

    private static S3SyncSettings Settings() => new()
    {
        Provider = SyncProvider.ICloud, ProviderChosenByUser = true, Enabled = true,
        AutomaticSyncEnabled = false, Prefix = "libzora", EncryptionKey = "icloud-test-key"
    };

    private static async Task<string> ReadTextAsync(SyncObjectRead read)
    {
        using var reader = new StreamReader(read.ResponseStream, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeICloudFileSystem : IICloudSyncFileSystem, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, byte[]> _evicted = new(StringComparer.Ordinal);
        public string Root { get; } = TestHelpers.CreateTempDirectory();
        public string ContainerIdentifier => ICloudSyncContainer.Identifier;
        public string? AccountIdentity { get; set; } = "fictional-apple-account-fingerprint";
        public List<ICloudSyncFileAccess> Accesses { get; } = [];
        public IOException? CoordinationFailure { get; set; }
        public Func<string, CancellationToken, Task>? BeforeDownload { get; set; }
        public Func<string, CancellationToken, Task>? BeforeUpload { get; set; }
        public string PathFor(string key) => Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
        private static string Placeholder(string path) => Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".icloud");
        public Task<string> GetContainerDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(Root);
        public bool ItemExists(string path) => File.Exists(path) || File.Exists(Placeholder(path));
        public void Evict(string path)
        {
            _evicted[path] = File.ReadAllBytes(path);
            File.Delete(path);
            File.WriteAllText(Placeholder(path), "iCloud placeholder metadata, not book bytes");
        }
        public async Task EnsureDownloadedAsync(string path, CancellationToken cancellationToken)
        {
            if (BeforeDownload is { } before) await before(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_evicted.Remove(path, out var bytes))
            {
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                File.Delete(Placeholder(path));
            }
            if (!File.Exists(path)) throw new SyncObjectNotFoundException("missing fixture object");
        }
        public async Task EnsureUploadedAsync(string path, CancellationToken cancellationToken)
        {
            if (BeforeUpload is { } before) await before(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ItemExists(path)) throw new SyncObjectNotFoundException("missing fixture upload");
        }
        public T Coordinate<T>(string path, ICloudSyncFileAccess access, Func<string, T> accessor, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CoordinationFailure is { } failure) throw failure;
                Accesses.Add(access);
                return accessor(path);
            }
        }
        public void DeleteItem(string path) { File.Delete(path); File.Delete(Placeholder(path)); }
        public void Dispose() => TestHelpers.TryDelete(Root);
    }

    private sealed class BrokenInputStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("fixture input failed"));
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("fixture input failed"));
    }

    private sealed class Device : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required AppPaths Paths { get; init; }
        public required SqliteBookLibraryService Library { get; init; }
        public required ReaderDataService Reader { get; init; }
        public required S3SyncService Service { get; init; }
        public required AccountService Accounts { get; init; }
        public required S3SyncSettings Settings { get; set; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public Task<S3SyncResult> SyncAsync() => Service.SyncAsync(Id, Settings);
        public static async Task<Device> CreateAsync(FakeICloudFileSystem fileSystem, S3SyncSettings? settings = null)
        {
            var root = TestHelpers.CreateTempDirectory();
            var paths = new AppPaths(root);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            var reader = new ReaderDataService(paths);
            await library.InitializeAsync();
            await reader.InitializeAsync();
            var accounts = new AccountService(paths, protector, dataRegion: AccountRegion.China);
            var service = new S3SyncService(paths, protector, accounts, AccountRegion.China,
                TestHelpers.ActivatedFeatures, SyncProviderRegistry.CreateDefault().Register(new ICloudSyncProviderFactory(fileSystem)));
            var device = new Device { Root = root, Paths = paths, Library = library, Reader = reader,
                Accounts = accounts, Service = service, Settings = settings ?? ICloudSyncTests.Settings() };
            await service.SaveSettingsAsync(device.Id, device.Settings);
            device.Settings = (await service.LoadSettingsAsync()).Settings;
            await service.InitializeDeletionTrackingAsync(deviceId: device.Id);
            return device;
        }
        public async Task<(Guid BookId, Guid FileId, byte[] Bytes)> AddBookAsync()
        {
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var bytes = RandomNumberGenerator.GetBytes(128 * 1024 + 123);
            var relative = Path.Combine("library", bookId.ToString("N"), "书籍.epub");
            var path = Path.Combine(Paths.Data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            await using var connection = new SqliteConnection($"Data Source={Paths.Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                VALUES ($book, 'iCloud fixture book', 'Fixture author', '', '', 0, 0, $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                VALUES ($file, $book, 'epub', $path, $size, $hash);
                """;
            command.Parameters.AddWithValue("$book", bookId.ToString());
            command.Parameters.AddWithValue("$file", fileId.ToString());
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$path", relative);
            command.Parameters.AddWithValue("$size", bytes.Length);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            await command.ExecuteNonQueryAsync();
            return (bookId, fileId, bytes);
        }
        public ValueTask DisposeAsync()
        {
            Accounts.Dispose();
            TestHelpers.TryDelete(Root);
            return ValueTask.CompletedTask;
        }
    }
}
