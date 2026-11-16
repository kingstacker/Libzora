using System.Text.Json.Nodes;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class MembershipPlanMigrationTests
{
    [Fact]
    public async Task HistoricalOfficialSettingsAreDisabledWithoutLosingSecretsOrReactivatingTheChoice()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var store = new S3SyncSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            var device = Guid.NewGuid().ToString("N");
            var settings = CustomSettings();
            await store.SaveAsync(device, settings);
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(store.SettingsPath))!;
            persisted["Provider"] = (int)SyncProvider.AccountServer;
            persisted["ProviderChosenByUser"] = true;
            await File.WriteAllTextAsync(store.SettingsPath, persisted.ToJsonString());

            var migrated = await store.LoadAsync();
            Assert.Equal(device, migrated.DeviceId);
            Assert.Equal(SyncProvider.S3, migrated.Settings.Provider);
            Assert.False(migrated.Settings.Enabled); Assert.False(migrated.Settings.ProviderChosenByUser);
            Assert.Equal(settings.AccessKey, migrated.Settings.AccessKey);
            Assert.Equal(settings.SecretKey, migrated.Settings.SecretKey);
            Assert.Equal(settings.WebDavUsername, migrated.Settings.WebDavUsername);
            Assert.Equal(settings.WebDavPassword, migrated.Settings.WebDavPassword);
            Assert.Equal(settings.EncryptionKey, migrated.Settings.EncryptionKey);
            Assert.Equal(settings.Endpoint, migrated.Settings.Endpoint);
            Assert.Equal(settings.Bucket, migrated.Settings.Bucket);

            await store.SaveAsync(device, migrated.Settings);
            var reopened = await store.LoadAsync();
            Assert.False(reopened.Settings.Enabled);
            Assert.False(reopened.Settings.ProviderChosenByUser);
            Assert.Equal(settings.EncryptionKey, reopened.Settings.EncryptionKey);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData(SyncProvider.S3)]
    [InlineData(SyncProvider.WebDav)]
    public async Task OldCustomSettingsWithoutTheChoiceFlagKeepTheirExistingConnection(SyncProvider provider)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var store = new S3SyncSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            var settings = CustomSettings() with { Provider = provider };
            await store.SaveAsync(Guid.NewGuid().ToString("N"), settings);
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(store.SettingsPath))!.AsObject();
            persisted.Remove("ProviderChosenByUser");
            await File.WriteAllTextAsync(store.SettingsPath, persisted.ToJsonString());
            var loaded = await store.LoadAsync();
            Assert.Equal(provider, loaded.Settings.Provider);
            Assert.True(loaded.Settings.Enabled); Assert.True(loaded.Settings.ProviderChosenByUser);
            Assert.Equal(settings.SecretKey, loaded.Settings.SecretKey);
            Assert.Equal(settings.WebDavPassword, loaded.Settings.WebDavPassword);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void NormalizationRejectsTheRemovedProviderButLeavesCustomConnectionsUnchanged()
    {
        var custom = CustomSettings();
        Assert.Equal(custom, S3SyncSettings.Normalize(custom));
        var official = custom with { Provider = SyncProvider.AccountServer };
        Assert.Contains("官方云同步已移除", official.Validate());
        var disabled = S3SyncSettings.Normalize(official);
        Assert.False(disabled.Enabled); Assert.False(disabled.ProviderChosenByUser);
        Assert.Equal(custom.EncryptionKey, disabled.EncryptionKey);
        Assert.Throws<ArgumentOutOfRangeException>(() => MemberEntitlements.SyncFeature(SyncProvider.AccountServer));
    }

    private static S3SyncSettings CustomSettings() => new()
    {
        Enabled = true, ProviderChosenByUser = true, Provider = SyncProvider.S3,
        Endpoint = "https://storage.example.invalid", Bucket = "fictional-library",
        AccessKey = "fictional-access", SecretKey = "fictional-secret",
        WebDavEndpoint = "https://dav.example.invalid/library", WebDavUsername = "fictional-reader",
        WebDavPassword = "fictional-password", EncryptionKey = "fictional-sync-key"
    };
}
