using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.TestFixtures;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task CustomSyncSettingsKeepTheKeyEditableAndPreserveBothConnectionsWhenSaved() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var stored = await scope.Sync.LoadSettingsAsync();
        var custom = CustomMembershipSyncSettings();
        await scope.Sync.SaveSettingsAsync(stored.DeviceId, custom);
        scope.Set("_s3SyncStoredSettings", stored with { Settings = custom });
        scope.Call("PopulateS3SyncControls", (object?)null);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = true;
        await Render();

        Assert.Equal(2, scope.Get<ComboBox>("SyncProviderBox").Items.Count);
        Assert.Null(scope.Window.FindControl<Control>("AccountSyncProviderItem"));
        Assert.Null(scope.Window.FindControl<Control>("AccountSyncEncryptionKeyBox"));
        Assert.True(scope.Get<TextBox>("S3EncryptionKeyBox").IsEffectivelyVisible);
        Assert.Equal(custom.EncryptionKey, scope.Get<TextBox>("S3EncryptionKeyBox").Text);
        scope.Get<TextBox>("S3EncryptionKeyBox").Text = "fictional-updated-sync-key";
        Assert.Equal(custom, (await scope.Sync.LoadSettingsAsync()).Settings);
        Assert.True(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        scope.Get<TextBox>("S3EncryptionKeyConfirmBox").Text = "fictional-mismatched-sync-key";
        Assert.False(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));
        Assert.Equal(UiText.Get("两次输入的加密密钥不一致，请重新输入。"), scope.Get<TextBlock>("S3SyncStatusText").Text);
        Assert.Equal(custom, (await scope.Sync.LoadSettingsAsync()).Settings);
        scope.Get<TextBox>("S3EncryptionKeyConfirmBox").Text = "fictional-updated-sync-key";
        var saving = scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.Equal(UiText.Get("确认加密密钥"), scope.Get<TextBlock>("ConfirmationTitleText").Text);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await saving);
        var saved = (await scope.Sync.LoadSettingsAsync()).Settings;
        Assert.Equal(custom with { EncryptionKey = "fictional-updated-sync-key" }, saved);

        foreach (var provider in new[] { SyncProvider.WebDav, SyncProvider.S3 })
        {
            scope.Get<ComboBox>("SyncProviderBox").SelectedIndex = (int)provider;
            Assert.Equal(provider == SyncProvider.S3, scope.Get<Control>("S3ConnectionPanel").IsVisible);
            Assert.Equal(provider == SyncProvider.WebDav, scope.Get<Control>("WebDavConnectionPanel").IsVisible);
            Assert.True(scope.Get<Control>("SettingsS3AdvancedExpander").IsVisible);
            Assert.True(scope.Get<Control>("SettingsS3ProfileExpander").IsVisible);
            Assert.True(scope.Get<Button>("S3TestConnectionButton").IsVisible);
            Assert.Equal(custom.SecretKey, scope.Get<TextBox>("S3SecretKeyBox").Text);
            Assert.Equal(custom.WebDavPassword, scope.Get<TextBox>("WebDavPasswordBox").Text);
            Assert.Equal(saved.EncryptionKey, scope.Get<TextBox>("S3EncryptionKeyBox").Text);
        }
        Assert.Equal(saved, (await scope.Sync.LoadSettingsAsync()).Settings);
    });

    [Theory]
    [InlineData("signed-out")]
    [InlineData("free")]
    [InlineData("trial")]
    [InlineData("lifetime")]
    [InlineData("expired")]
    [InlineData("region-switch")]
    public Task CustomSyncWarningsDescribeFeatureAccessWithoutOfficialStorageOrRetention(string state) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var user = new AccountUser(Guid.NewGuid(), "fictional_reader", "reader@example.invalid");
        var region = state == "region-switch" ? AccountRegion.International : AccountRegion.China;
        var tier = state is "trial" or "expired" or "region-switch" ? "trial" : state;
        DateTimeOffset? expiry = tier == "trial" ? DateTimeOffset.UtcNow.AddDays(state == "expired" ? -5 : 7) : null;
        var member = state == "signed-out" ? null : new AccountMembership(user.Id, region, tier,
            tier == "lifetime" || expiry > DateTimeOffset.UtcNow, expiry, 6, 1, 1, 0, 0, 0, 0, 0, 0, 0, null);
        var status = new AccountStatus(AccountRegions.DefaultServerUrl(region), state == "signed-out" ? null : user,
            DateTimeOffset.UtcNow.AddDays(30), region, state == "region-switch");
        scope.Call("PresentAccountSummary", status, member);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();

        var notice = scope.Get<TextBlock>("SyncMembershipNoticeText");
        // The current release intentionally leaves custom synchronization
        // free. Keep the future-entitlement expectations only behind its gate.
        Assert.Equal(AccountAvailability.EnforceSyncEntitlements && state is not ("trial" or "lifetime"), notice.IsVisible);
        if (AccountAvailability.EnforceSyncEntitlements && state == "expired")
        {
            Assert.Contains("不会删除", notice.Text);
            Assert.Contains(expiry!.Value.LocalDateTime.ToString("yyyy/MM/dd"), notice.Text);
            Assert.DoesNotContain("销毁", notice.Text);
            Assert.DoesNotContain("保留期", notice.Text);
        }
        if (AccountAvailability.EnforceSyncEntitlements)
        {
            if (state == "signed-out") Assert.Contains("领取试用", notice.Text);
            if (state == "free") Assert.Contains("自备 S3 / WebDAV", notice.Text);
            if (state == "region-switch") Assert.Contains("重新打开应用", notice.Text);
        }
        else Assert.True(string.IsNullOrEmpty(notice.Text));
        Assert.Contains("不提供官方云同步", ToolTip.GetTip(scope.Get<ComboBox>("SyncProviderBox"))?.ToString());
        Assert.True(scope.Get<Button>("S3TestConnectionButton").IsVisible);
        Assert.True(scope.Get<Button>("AccountNavigationButton").IsEnabled);
        Assert.Null(scope.Window.FindControl<Control>("AccountSyncProviderItem"));
    });

    [Theory]
    [InlineData(SyncProvider.S3, true, false)]
    [InlineData(SyncProvider.S3, false, true)]
    [InlineData(SyncProvider.WebDav, true, false)]
    [InlineData(SyncProvider.WebDav, false, true)]
    public Task SigningInNeverChangesCustomStorageEvenWithoutASavedProviderChoice(SyncProvider provider, bool chosen, bool lifetime) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var stored = await scope.Sync.LoadSettingsAsync();
        var custom = CustomMembershipSyncSettings() with { Provider = provider, ProviderChosenByUser = chosen };
        await scope.Sync.SaveSettingsAsync(stored.DeviceId, custom);
        scope.Set("_s3SyncStoredSettings", stored with { Settings = custom });
        scope.Call("PopulateS3SyncControls", (object?)null);
        using var handler = new MembershipHttpHandler
        { LifetimeFeatures = lifetime, ProExpiresAt = lifetime ? null : DateTimeOffset.UtcNow.AddDays(7) };
        using var http = new HttpClient(handler);
        var accounts = scope.Field<ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(accounts, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password,
            true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        await scope.Call<Task>("RefreshAccountSummaryAsync");
        await Render();

        Assert.Equal(custom, (await scope.Sync.LoadSettingsAsync()).Settings);
        Assert.Equal((int)provider, scope.Get<ComboBox>("SyncProviderBox").SelectedIndex);
        Assert.Equal(custom.EncryptionKey, scope.Get<TextBox>("S3EncryptionKeyBox").Text);
        Assert.Null(typeof(MainWindow).GetMethod("PersistPlusCloudSyncSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic));
    });

    [Fact]
    public Task HistoricalOfficialSettingsDisplayDisabledWithoutLosingConnectionSecrets() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var stored = await scope.Sync.LoadSettingsAsync();
        var historical = CustomMembershipSyncSettings();
        await scope.Sync.SaveSettingsAsync(stored.DeviceId, historical);
        var path = Path.Combine(scope.Paths.Data, "s3-sync-settings.json");
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        persisted["Provider"] = (int)SyncProvider.AccountServer;
        persisted["ProviderChosenByUser"] = true;
        await File.WriteAllTextAsync(path, persisted.ToJsonString());
        var migrated = await scope.Sync.LoadSettingsAsync();
        scope.Set("_s3SyncStoredSettings", migrated);
        scope.Call("PopulateS3SyncControls", (object?)null);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.Equal((int)SyncProvider.S3, scope.Get<ComboBox>("SyncProviderBox").SelectedIndex);
        Assert.False(scope.Get<ToggleSwitch>("S3SyncEnabledCheck").IsChecked);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.Equal(historical.SecretKey, scope.Get<TextBox>("S3SecretKeyBox").Text);
        Assert.Equal(historical.WebDavPassword, scope.Get<TextBox>("WebDavPasswordBox").Text);
        Assert.Equal(historical.EncryptionKey, scope.Get<TextBox>("S3EncryptionKeyBox").Text);
        Assert.False(scope.Call<S3SyncSettings>("ReadS3SyncSettingsFromControls").ProviderChosenByUser);
        Assert.True(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
        Assert.Contains("确认自备", scope.Get<TextBlock>("S3DraftStatusText").Text);
        Assert.True(await scope.Call<Task<bool>>("SaveS3SyncSettingsFromControlsAsync", true, CancellationToken.None));
        var confirmed = await scope.Sync.LoadSettingsAsync();
        Assert.Equal(migrated.Settings with { ProviderChosenByUser = true }, confirmed.Settings);
        Assert.Equal(migrated.DeviceId, confirmed.DeviceId);
        Assert.False(confirmed.Settings.Enabled);
        Assert.True(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.False(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
    });

    [Theory]
    [InlineData(SyncProvider.S3, false, "zh-CN")]
    [InlineData(SyncProvider.S3, true, "zh-CN")]
    [InlineData(SyncProvider.WebDav, false, "zh-CN")]
    [InlineData(SyncProvider.WebDav, true, "zh-CN")]
    [InlineData(SyncProvider.S3, false, "en-US")]
    [InlineData(SyncProvider.S3, true, "en-US")]
    [InlineData(SyncProvider.WebDav, false, "en-US")]
    [InlineData(SyncProvider.WebDav, true, "en-US")]
    public Task UnconfirmedCustomTargetsBlockEveryDesktopTransferEntry(SyncProvider provider, bool enabled, string language) => Run(async () =>
    {
        var custom = CustomMembershipSyncSettings() with
        {
            Provider = provider, Enabled = enabled, ProviderChosenByUser = false, AutomaticSyncEnabled = true
        };
        var sync = new PortableSyncService { StoredSettings = new("test-device", custom) };
        await using var scope = await TestWindow.Create(new AppSettings
        {
            UiLanguage = language, OnboardingCompleted = true, NetworkEnabled = true,
            AutoUpdateCheckEnabled = false, AutoConnectDevice = false, GridGalleryDisplay = false
        }, syncService: sync);
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();
        var prompt = UiText.Get("请确认自备 S3 / WebDAV 连接信息并保存后再同步。");
        Assert.Contains(language == "en-US" ? "Confirm and save" : "确认自备", prompt);
        Assert.False(scope.Get<Button>("S3SyncNowButton").IsEnabled);
        Assert.True(scope.Get<Button>("S3SaveSettingsButton").IsEnabled);
        Assert.Contains(prompt, scope.Get<TextBlock>("S3DraftStatusText").Text);
        Assert.False(scope.Call<bool>("IsAutomaticS3SyncReady"));

        foreach (var silent in new[] { false, true })
        {
            Assert.False(await scope.Call<Task<bool>>("RunS3SyncAsync", silent, CancellationToken.None));
            Assert.Equal(prompt, scope.Get<TextBlock>("S3SyncStatusText").Text);
            Assert.False(scope.Field<bool>("_s3SyncBusy"));
        }
        scope.Call("S3SyncIndicatorButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.Equal(prompt, scope.Get<TextBlock>("S3SyncStatusText").Text);
        var missing = new BookFile
        {
            Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Format = "epub", RelativePath = "fictional-missing-book.epub"
        };
        Assert.Null(await scope.Call<Task<string?>>("EnsureBookFileAvailableAsync", missing, CancellationToken.None));
        Assert.Equal(0, sync.SyncCount);
        Assert.Equal(0, sync.FileDownloadCount);
        Assert.Equal(new S3SyncStoredSettings("test-device", custom), sync.StoredSettings);
    });

    [Theory]
    [InlineData(1024, 664, "zh-CN")]
    [InlineData(1294, 818, "en-US")]
    public Task OnlyCustomProvidersAndOrdinarySettingsActionsAreAvailableInBothLanguages(int width, int height, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = width; scope.Window.Height = height;
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        scope.Call("SystemS3SyncNavigationButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.Equal(2, scope.Get<ComboBox>("SyncProviderBox").Items.Count);
        foreach (var name in new[] { "SyncProviderBox", "S3AutomaticSyncCheck", "S3DownloadBookFilesCheck", "S3SaveSettingsButton", "S3TestConnectionButton" })
        {
            var control = scope.Get<Control>(name);
            control.BringIntoView();
            await Render();
            Assert.True(control.IsEffectivelyVisible, name);
            AssertWithinWindow(control, scope.Window);
        }
        scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded = true;
        scope.Get<TextBox>("S3EncryptionKeyBox").BringIntoView();
        await Render();
        Assert.True(scope.Get<TextBox>("S3EncryptionKeyBox").IsEffectivelyVisible);
        var help = scope.Get<TextBlock>("S3EncryptionKeyHelpText").Text ?? "";
        Assert.Contains(language == "en-US" ? "encryption is recommended" : "建议启用加密", help);
        Assert.DoesNotContain("Plus", help);
    });

    private static S3SyncSettings CustomMembershipSyncSettings() => S3SyncSettings.Normalize(new S3SyncSettings
    {
        Enabled = true, AutomaticSyncEnabled = false, ProviderChosenByUser = true, DownloadBookFilesOnSync = true,
        Endpoint = "https://s3.example.invalid", AccessKey = "fictional-access-key", SecretKey = "fictional-secret-key",
        Bucket = "fictional-bucket", Region = "fictional-region", Prefix = "fictional-library",
        WebDavEndpoint = "https://dav.example.invalid/dav", WebDavUsername = "fictional-dav-user",
        WebDavPassword = "fictional-dav-password", EncryptionKey = "fictional-initial-sync-key",
        IntervalMinutes = 75, TimeoutSeconds = 120, ConcurrentRequests = 6, PathStyle = true
    });
}
