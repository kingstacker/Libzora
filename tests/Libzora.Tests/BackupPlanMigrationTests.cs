using System.IO.Compression;
using System.Text.Json.Nodes;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData(SyncProvider.S3)]
    [InlineData(SyncProvider.WebDav)]
    public async Task HistoricalOfficialBackupNeverOverwritesTheCurrentCustomConnectionOrItsSecrets(SyncProvider provider)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var protector = new TestHelpers.PlaintextSecretProtector();
            var source = new AppPaths(Path.Combine(root, "source"));
            await SetBackupKeyAsync(source);
            var sourceLibrary = new SqliteBookLibraryService(source, new BookMetadataService());
            await sourceLibrary.InitializeAsync();
            var epub = Path.Combine(root, "fictional-book.epub"); CreateEpub(epub);
            await sourceLibrary.ImportAsync([epub]);
            var backup = Path.Combine(root, "historical.libzora");
            await new AppBackupService(source, protector).ExportAsync(backup);
            using (var archive = await OpenBackupArchiveAsync(backup, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("settings/settings.json")!;
                JsonNode package;
                using (var reader = new StreamReader(entry.Open()))
                    package = JsonNode.Parse(await reader.ReadToEndAsync())!;
                var connection = package["s3"]!.AsObject();
                connection["provider"] = (int)SyncProvider.AccountServer;
                connection["enabled"] = true;
                connection["accountRegion"] = (int)AccountRegion.International;
                connection["accountServerUrl"] = "https://retired-accounts.example.invalid";
                connection["accountUserId"] = Guid.NewGuid().ToString();
                connection["endpoint"] = "https://unrelated-storage.example.invalid";
                connection["bucket"] = "fictional-unrelated-library";
                connection["prefix"] = "old-official-folder";
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("settings/settings.json").Open());
                await writer.WriteAsync(package.ToJsonString());
            }

            var target = new AppPaths(Path.Combine(root, "target"));
            var targetLibrary = new SqliteBookLibraryService(target, new BookMetadataService());
            await targetLibrary.InitializeAsync();
            var store = new S3SyncSettingsStore(target, protector);
            var device = Guid.NewGuid().ToString("N");
            var current = S3SyncSettings.Normalize(new S3SyncSettings
            {
                Provider = provider, ProviderChosenByUser = true, Enabled = true, AutomaticSyncEnabled = false,
                Endpoint = "https://own-storage.example.invalid", Bucket = "fictional-own-library", Prefix = "keep-my-prefix",
                AccessKey = "fictional-own-access", SecretKey = "fictional-own-secret",
                WebDavEndpoint = "https://own-dav.example.invalid/library", WebDavUsername = "fictional-own-reader",
                WebDavPassword = "fictional-own-password", EncryptionKey = BackupTestKey
            });
            await store.SaveAsync(device, current);
            var result = await new AppBackupService(target, protector).ImportAsync(backup);
            var restored = await store.LoadAsync();
            Assert.Equal(current with { Enabled = false }, result.S3Settings);
            Assert.Equal(current with { Enabled = false }, restored.Settings);
            Assert.Equal(device, restored.DeviceId);
            Assert.True(restored.Settings.ProviderChosenByUser);
            Assert.Equal(BackupTestKey, restored.Settings.EncryptionKey);
            await targetLibrary.InitializeAsync();
            Assert.Single(await targetLibrary.SearchAsync());
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
