using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task AppleICloudChoiceUsesItsWireValueHidesServerFieldsAndKeepsEncryption(string language) => Run(async () =>
    {
        var initial = new S3SyncSettings
        {
            Provider = SyncProvider.S3, ProviderChosenByUser = true, EncryptionKey = "test-icloud-key",
            AccessKey = "retained-s3-access", SecretKey = "retained-s3-secret", Bucket = "retained-s3-bucket",
            WebDavEndpoint = "https://example.invalid/dav", WebDavUsername = "retained-user", WebDavPassword = "retained-dav-password"
        };
        await using var scope = await TestWindow.Create(syncProviders: AppleProviders(), initialSyncSettings: initial);
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        var providers = scope.Get<ComboBox>("SyncProviderBox");
        Assert.Equal(new object[] { SyncProvider.ICloud, SyncProvider.S3, SyncProvider.WebDav },
            providers.Items.OfType<ComboBoxItem>().Select(item => item.Tag!).ToArray());
        providers.SelectedIndex = 0;
        Assert.Equal(SyncProvider.ICloud, ((ComboBoxItem)providers.SelectedItem!).Tag);
        foreach (var name in new[] { "S3ConnectionPanel", "WebDavConnectionPanel", "S3RegionRow", "S3PathStyleCheck",
                     "S3SkipTlsVerifyCheck", "S3ConcurrencyRow", "SettingsS3ProfileExpander" })
            Assert.False(scope.Get<Control>(name).IsVisible, name);
        Assert.True(scope.Get<Control>("ICloudConnectionPanel").IsVisible);
        Assert.Contains(language == "en-US" ? "iCloud Drive" : "iCloud 云盘",
            ToolTip.GetTip(providers)?.ToString());
        Assert.Equal(language == "en-US" ? "This sync method is not supported on the current platform." : "当前平台不支持此同步方式。",
            UiText.Get("当前平台不支持此同步方式。"));
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = true;
        await Render();
        Assert.True(scope.Get<TextBox>("S3EncryptionKeyBox").IsEffectivelyVisible);
        Assert.True(scope.Get<TextBox>("S3PrefixBox").IsEffectivelyVisible);
        scope.Get<ToggleSwitch>("S3AutomaticSyncCheck").IsChecked = false;
        scope.Get<ToggleSwitch>("S3SyncEnabledCheck").IsChecked = true;
        Assert.True(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));
        var saved = (await scope.Sync.LoadSettingsAsync()).Settings;
        Assert.Equal(SyncProvider.ICloud, saved.Provider);
        Assert.True(saved.Enabled);
        Assert.Equal("ui-icloud-account-fingerprint", saved.ICloudAccountIdentity);
        Assert.Equal(initial.SecretKey, saved.SecretKey);
        Assert.Equal(initial.WebDavPassword, saved.WebDavPassword);
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
        Assert.True(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.Equal(SyncProvider.ICloud, scope.Call<S3SyncSettings>("ReadS3SyncSettingsFromControls").Provider);
        providers.SelectedIndex = 1;
        Assert.False(scope.Get<Control>("ICloudConnectionPanel").IsVisible);
        Assert.True(scope.Get<Control>("S3ConnectionPanel").IsVisible);
        Assert.Equal(initial.SecretKey, scope.Get<TextBox>("S3SecretKeyBox").Text);
        scope.Call("S3DiscardSettingsButton_Click", null, new RoutedEventArgs());
        Assert.Equal(0, providers.SelectedIndex);
    });

    [Fact]
    public Task RestoredICloudChoiceSurvivesTheLaterAutoSaveSetup() => Run(async () =>
    {
        var saved = new S3SyncSettings { Provider = SyncProvider.ICloud, ProviderChosenByUser = true, Enabled = false };
        await using var scope = await TestWindow.Create(syncProviders: AppleProviders(), initialSyncSettings: saved);
        var providers = scope.Get<ComboBox>("SyncProviderBox");
        Assert.Equal(0, providers.SelectedIndex);
        Assert.Equal(SyncProvider.ICloud, ((ComboBoxItem)providers.SelectedItem!).Tag);
        Assert.Equal(saved, scope.Call<S3SyncSettings>("ReadS3SyncSettingsFromControls"));
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
    });

    [Fact]
    public Task NonAppleRegistryDoesNotOfferICloudOrEnableAnImportedAppleChoice() => Run(async () =>
    {
        var saved = new S3SyncSettings { Provider = SyncProvider.ICloud, ProviderChosenByUser = true, Enabled = true };
        await using var scope = await TestWindow.Create(initialSyncSettings: saved);
        var providers = scope.Get<ComboBox>("SyncProviderBox");
        Assert.Equal(2, providers.Items.Count);
        Assert.DoesNotContain(providers.Items.OfType<ComboBoxItem>(), item => Equals(item.Tag, SyncProvider.ICloud));
        Assert.False(scope.Get<ToggleSwitch>("S3SyncEnabledCheck").IsChecked);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.Equal(SyncProvider.ICloud, (await scope.Sync.LoadSettingsAsync()).Settings.Provider);
    });

    private static SyncProviderRegistry AppleProviders() => SyncProviderRegistry.CreateDefault().Register(new UiICloudFactory());

    // No iCloud/network operation is started by these presentation tests.
    private sealed class UiICloudFactory : ISyncProviderFactory, ISyncProviderAccountBinding
    {
        public SyncProviderDescriptor Descriptor { get; } = new(SyncProvider.ICloud, SyncProviderIds.ICloud, "iCloud",
            new SyncProviderCapabilities(SupportsRangeRead: false, ReliableListing: false,
                ReliableLastModified: false, MaxConcurrentRequests: 1));
        public S3SyncSettings BindAccount(S3SyncSettings settings, bool allowAccountChange = false) =>
            settings with { ICloudAccountIdentity = "ui-icloud-account-fingerprint" };
        public ISyncObjectStore Create(S3SyncSettings settings) => throw new InvalidOperationException("UI tests must not start a transfer.");
        public string BuildStorageIdentity(S3SyncSettings settings) => "ui-icloud|" + settings.ICloudAccountIdentity + "|" + settings.Prefix;
        public bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) => left.ICloudAccountIdentity == right.ICloudAccountIdentity;
    }
}
