using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Tests;

public sealed class AppDataMigrationTests
{
    [Fact]
    public async Task MigrationCopiesAllRegionsAndProtectedSettingsBeforeSwitchingRoot()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            Directory.CreateDirectory(configuration);
            var protector = new TestHelpers.PlaintextSecretProtector();
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            await AddBookAsync(installation, "installation book", "cover");

            await new AppSettingsStore(installation).SaveAsync(new AppSettings { OnboardingCompleted = true });
            var syncStore = new S3SyncSettingsStore(installation, protector);
            await syncStore.SaveAsync(Guid.NewGuid().ToString("N"), new S3SyncSettings
            {
                Enabled = true,
                Endpoint = "https://example.invalid",
                Bucket = "bucket",
                AccessKey = "access",
                SecretKey = "secret",
                EncryptionKey = "migration-encryption-key"
            });

            var region = AccountRegionPaths.ForRegion(installation, AccountRegion.International);
            Assert.Equal(installation.InstallationRoot, region.InstallationRoot);
            var regionLibrary = new SqliteBookLibraryService(region, new BookMetadataService());
            await regionLibrary.InitializeAsync();
            var regionBook = await AddBookAsync(region, "region book");

            Directory.CreateDirectory(Path.Combine(sourceRoot, "backups"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "backups", "old.libzora"), "backup");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "plugins"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "plugins", "plugin.dll"), "plugin");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "plugin-data"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "plugin-data", "state.json"), "{}");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "browser-data"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "browser-data", "session"), "not migrated");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "data", "logs"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "data", "logs", "app.log"), "volatile");

            var target = Path.Combine(root, "target");
            var result = await new AppDataMigrationService(region, configuration).MigrateAsync(target);

            var physicalTarget = AppDataMigrationService.ResolvePhysicalPath(target);
            Assert.True(result.RestartRequired);
            Assert.Equal(physicalTarget, result.TargetRoot);
            Assert.Equal(1, result.BookCount);
            Assert.Equal(1, result.FileCount);
            Assert.True(result.CopiedBytes > 0);
            Assert.Equal(physicalTarget, AppRootConfiguration.ResolveRoot(configuration));
            Assert.Equal(
                AppDataMigrationService.ResolvePhysicalPath(sourceRoot),
                AppRootConfiguration.TryReadPreviousRoot(configuration));

            var installationDatabase = Path.Combine(physicalTarget, "data", "libzora.db");
            Assert.True(File.Exists(installationDatabase));
            Assert.Equal(1L, await ScalarInt64Async(installationDatabase, "SELECT COUNT(*) FROM Books;"));
            var regionDatabase = Path.Combine(
                physicalTarget, "regions", "international", "data", "libzora.db");
            Assert.True(File.Exists(regionDatabase));
            Assert.Equal(1L, await ScalarInt64Async(regionDatabase, "SELECT COUNT(*) FROM Books;"));
            Assert.True(File.Exists(Path.Combine(
                physicalTarget, "regions", "international", "data",
                "library", regionBook.BookId.ToString("N"), "book.epub")));

            Assert.True(File.Exists(Path.Combine(physicalTarget, "backups", "old.libzora")));
            Assert.True(File.Exists(Path.Combine(physicalTarget, "plugins", "plugin.dll")));
            Assert.True(File.Exists(Path.Combine(physicalTarget, "plugin-data", "state.json")));
            Assert.False(Directory.Exists(Path.Combine(physicalTarget, "browser-data")));
            // Logs are written continuously by the running process and are
            // excluded from the snapshot so consistency checks stay useful.
            Assert.False(Directory.Exists(Path.Combine(physicalTarget, "data", "logs")));

            // A plain backup deliberately drops secrets; a same-machine move
            // must copy the protected settings file byte-for-byte.
            var targetSettingsPath = Path.Combine(physicalTarget, "data", "s3-sync-settings.json");
            Assert.Equal(
                await File.ReadAllBytesAsync(syncStore.SettingsPath),
                await File.ReadAllBytesAsync(targetSettingsPath));
            var reloaded = await new S3SyncSettingsStore(new AppPaths(physicalTarget), protector).LoadAsync();
            Assert.Equal("migration-encryption-key", reloaded.Settings.EncryptionKey);
            Assert.Equal("secret", reloaded.Settings.SecretKey);

            Assert.False(File.Exists(Path.Combine(physicalTarget, AppDataMigrationService.JournalFileName)));
            Assert.Empty(Directory.GetDirectories(
                physicalTarget, AppDataMigrationService.StageDirectoryPrefix + "*"));

            // The old root is retained exactly as it was.
            Assert.True(File.Exists(installation.Database));
            Assert.True(File.Exists(region.Database));
            // ...but long-running helpers that still resolve the old AppPaths
            // must not keep writing into the retired library.
            Assert.True(File.Exists(Path.Combine(sourceRoot, AppRootConfiguration.MigratedMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(region.Root, AppRootConfiguration.MigratedMarkerFileName)));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using var lease = await AppDataProcessLock.AcquireAsync(installation, CancellationToken.None);
            });
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task MigrationUsesSqliteSnapshotSoCommittedWalDataIsNotLost()
    {
        var root = TestHelpers.CreateTempDirectory();
        SqliteConnection? writer = null;
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();

            writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = installation.Database,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await writer.OpenAsync();
            using (var pragma = writer.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                await pragma.ExecuteNonQueryAsync();
            }
            var book = await AddBookAsync(
                installation, "wal book", coverContent: null, connection: writer);
            var walPath = installation.Database + "-wal";
            Assert.True(File.Exists(walPath));
            Assert.True(new FileInfo(walPath).Length > 0);

            var target = Path.Combine(root, "target");
            var result = await new AppDataMigrationService(installation, configuration).MigrateAsync(target);
            Assert.Equal(1, result.BookCount);
            Assert.Equal(1, result.FileCount);

            var targetDatabase = Path.Combine(
                AppDataMigrationService.ResolvePhysicalPath(target), "data", "libzora.db");
            Assert.Equal(1L, await ScalarInt64Async(targetDatabase, "SELECT COUNT(*) FROM Books;"));
            Assert.Equal(book.Hash, await ScalarStringAsync(
                targetDatabase, "SELECT Sha256 FROM BookFiles LIMIT 1;"));
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task CancellingMigrationKeepsOldRootAndRemovesTargetArtifacts()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            for (var index = 0; index < 5; index++)
                await AddBookAsync(installation, "content " + index);

            var target = Path.Combine(root, "target");
            using var cancellation = new CancellationTokenSource();
            var progress = new TestHelpers.InlineProgress<AppDataMigrationProgress>(value =>
            {
                if (value.Fraction >= 0.05) cancellation.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new AppDataMigrationService(installation, configuration)
                    .MigrateAsync(target, progress, cancellation.Token));

            Assert.False(File.Exists(Path.Combine(configuration, "app-root.json")));
            Assert.True(File.Exists(installation.Database));
            Assert.False(Directory.Exists(Path.Combine(target, "data")));
            Assert.False(File.Exists(Path.Combine(target, AppDataMigrationService.JournalFileName)));
            Assert.Empty(Directory.Exists(target)
                ? Directory.GetDirectories(target, AppDataMigrationService.StageDirectoryPrefix + "*")
                : Array.Empty<string>());
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task MigrationRefusesUserDataAndNestedTargets()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            await AddBookAsync(installation, "book");

            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "user data");
            var service = new AppDataMigrationService(installation, configuration);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.MigrateAsync(target));
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(configuration, "app-root.json")));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.MigrateAsync(Path.Combine(sourceRoot, "inside")));
            // A target that contains the source would copy the root into itself.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.MigrateAsync(root));
            Assert.False(Directory.Exists(Path.Combine(sourceRoot, "inside")));
            Assert.True(File.Exists(installation.Database));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task StaleCrashedStageIsCleanedAndMigrationRestarts()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            await AddBookAsync(installation, "book");

            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            var physicalTarget = AppDataMigrationService.ResolvePhysicalPath(target);
            var physicalSource = AppDataMigrationService.ResolvePhysicalPath(sourceRoot);
            var stageName = AppDataMigrationService.StageDirectoryPrefix + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(physicalTarget, stageName));
            await File.WriteAllTextAsync(Path.Combine(physicalTarget, stageName, "junk"), "junk");
            var journal = new AppDataMigrationService.MigrationJournal
            {
                Phase = AppDataMigrationService.MigrationPhase.Copying,
                SourceRoot = physicalSource,
                TargetRoot = physicalTarget,
                StageDirectory = stageName,
                ActiveDataRelativePath = "data"
            };
            await File.WriteAllTextAsync(
                Path.Combine(physicalTarget, AppDataMigrationService.JournalFileName),
                JsonSerializer.Serialize(journal, AppDataMigrationService.JournalJsonOptions));

            var result = await new AppDataMigrationService(installation, configuration)
                .MigrateAsync(physicalTarget);

            Assert.True(result.RestartRequired);
            Assert.False(File.Exists(Path.Combine(physicalTarget, AppDataMigrationService.JournalFileName)));
            Assert.False(Directory.Exists(Path.Combine(physicalTarget, stageName)));
            Assert.True(File.Exists(Path.Combine(physicalTarget, "data", "libzora.db")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task CommittedJournalDoesNotPublishStaleSnapshotAfterSourceChanges()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            await AddBookAsync(installation, "first");

            var target = Path.Combine(root, "target");
            await new AppDataMigrationService(installation, configuration).MigrateAsync(target);
            var physicalSource = AppDataMigrationService.ResolvePhysicalPath(sourceRoot);
            var physicalTarget = AppDataMigrationService.ResolvePhysicalPath(target);

            // Simulate a crash after the move but before app-root.json was
            // published: the target holds a complete copy and the journal is
            // still Committed. The user then keeps importing into the old
            // library.
            File.Delete(Path.Combine(physicalSource, AppRootConfiguration.MigratedMarkerFileName));
            var fingerprint = AppDataMigrationService.ComputeSourceFingerprint(
                physicalSource, CancellationToken.None);
            await AddBookAsync(installation, "second");

            var journal = new AppDataMigrationService.MigrationJournal
            {
                Phase = AppDataMigrationService.MigrationPhase.Committed,
                SourceRoot = physicalSource,
                TargetRoot = physicalTarget,
                StageDirectory = AppDataMigrationService.StageDirectoryPrefix + Guid.NewGuid().ToString("N"),
                ActiveDataRelativePath = "data",
                PayloadEntries = new[] { "data", "regions", "backups", "plugins", "plugin-data" }
                    .Where(name => Directory.Exists(Path.Combine(physicalTarget, name)))
                    .ToList(),
                CopiedDatabasePaths = ["data/libzora.db"],
                SourceFingerprint = fingerprint
            };
            await File.WriteAllTextAsync(
                Path.Combine(physicalTarget, AppDataMigrationService.JournalFileName),
                JsonSerializer.Serialize(journal, AppDataMigrationService.JournalJsonOptions));

            var result = await new AppDataMigrationService(installation, configuration)
                .MigrateAsync(physicalTarget);

            // The changed source must win: recovery re-copies instead of
            // adopting the one-book snapshot.
            Assert.Equal(2, result.BookCount);
            Assert.Equal(
                2L,
                await ScalarInt64Async(
                    Path.Combine(physicalTarget, "data", "libzora.db"),
                    "SELECT COUNT(*) FROM Books;"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task OrphanedStageWithoutJournalIsCleanedAndMigrationRestarts()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            await AddBookAsync(installation, "book");

            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            var physicalTarget = AppDataMigrationService.ResolvePhysicalPath(target);
            var stageName = AppDataMigrationService.StageDirectoryPrefix + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(physicalTarget, stageName));
            await File.WriteAllTextAsync(Path.Combine(physicalTarget, stageName, "junk"), "junk");

            var result = await new AppDataMigrationService(installation, configuration)
                .MigrateAsync(physicalTarget);

            Assert.True(result.RestartRequired);
            Assert.False(Directory.Exists(Path.Combine(physicalTarget, stageName)));
            Assert.True(File.Exists(Path.Combine(physicalTarget, "data", "libzora.db")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task MigrationStateBlocksWritersThatBypassProcessLock()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var installation = new AppPaths(Path.Combine(root, "source"));
            var fontPath = Path.Combine(root, "sample.ttf");
            await File.WriteAllBytesAsync(fontPath, [0, 1, 2, 3]);
            var dictionaryPath = Path.Combine(root, "sample.txt");
            await File.WriteAllTextAsync(dictionaryPath, "word\texplain");

            using var scope = DataRootMigrationState.Begin(installation.Root);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new FontLibraryService(installation).ImportAsync(fontPath));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new DictionaryService(installation).ImportAsync(dictionaryPath));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task MigrationRejectsSymlinkedPayloadEntries()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var configuration = Path.Combine(root, "config");
            var installation = new AppPaths(sourceRoot);
            var library = new SqliteBookLibraryService(installation, new BookMetadataService());
            await library.InitializeAsync();
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(installation.Library, "linked"), outside);

            var target = Path.Combine(root, "target");
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                new AppDataMigrationService(installation, configuration).MigrateAsync(target));
            Assert.Contains("符号链接", exception.Message);
            Assert.False(File.Exists(Path.Combine(configuration, "app-root.json")));
            Assert.True(File.Exists(installation.Database));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static async Task<(Guid BookId, Guid FileId, string Hash)> AddBookAsync(
        AppPaths paths,
        string content,
        string? coverContent = null,
        SqliteConnection? connection = null)
    {
        var bookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var relativePath = Path.Combine("library", bookId.ToString("N"), "book.epub");
        var absolutePath = Path.Combine(paths.Data, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(absolutePath, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        string? coverRelativePath = null;
        if (coverContent is not null)
        {
            coverRelativePath = Path.Combine("covers", bookId.ToString("N") + ".jpg");
            var coverPath = Path.Combine(paths.Data, coverRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!);
            await File.WriteAllTextAsync(coverPath, coverContent);
        }

        var ownsConnection = connection is null;
        connection ??= new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            if (ownsConnection) await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt)
                VALUES ($book, 'Test book', 'Author', '', '', 0, 0, $cover, $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                VALUES ($file, $book, 'epub', $path, $size, $hash);
                """;
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            command.Parameters.AddWithValue("$book", bookId.ToString());
            command.Parameters.AddWithValue("$file", fileId.ToString());
            command.Parameters.AddWithValue("$cover", (object?)coverRelativePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$time", timestamp);
            command.Parameters.AddWithValue("$path", relativePath);
            command.Parameters.AddWithValue("$size", bytes.Length);
            command.Parameters.AddWithValue("$hash", hash);
            await command.ExecuteNonQueryAsync();
            return (bookId, fileId, hash);
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync();
        }
    }

    private static async Task<long> ScalarInt64Async(string databasePath, string sql)
    {
        await using var connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(string databasePath, string sql)
    {
        await using var connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static SqliteConnection OpenReadOnly(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
}
