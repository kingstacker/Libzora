using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.TestFixtures;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task AccountRightsRequireLoginAndOnlyRefreshServerState(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
        var upgrades = 0;
        var signIns = 0;
        page.AccountStateChanged += (_, args) => { if (args.Membership?.Tier == "lifetime") upgrades++; };
        page.AccountSignedIn += (_, _) => signIns++;
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            AssertNoRetiredAccountControls(page);
            Assert.False(Get<StackPanel>("MembershipPanel").IsVisible);
            Assert.False(Get<Button>("LifetimeRightsButton").IsEnabled);
            var requests = handler.RequestedActions.ToArray();
            Get<Button>("LifetimeRightsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.RefreshAsync();
            Assert.Equal(requests, handler.RequestedActions);
            Assert.False(handler.LifetimeFeatures);

            Get<TextBox>("UsernameBox").Text = handler.User.Username;
            Get<TextBox>("PasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.True(Get<Button>("LifetimeRightsButton").IsEnabled);
            Assert.Equal(UiText.Get("刷新权益"), Get<Button>("LifetimeRightsButton").Content);
            Assert.Equal(0, upgrades);
            var checks = handler.DeviceStatusChecks;
            Get<Button>("LifetimeRightsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal("Free", Get<TextBlock>("TierBadgeText").Text);
            Assert.False(handler.LifetimeFeatures);
            Assert.Null(handler.TrialClaimedAt);

            // Only a fictional protected server grant changes the account's permanent rights.
            handler.LifetimeFeatures = true;
            checks = handler.DeviceStatusChecks;
            Get<Button>("LifetimeRightsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal(UiText.Get(MemberEntitlements.TierName("lifetime")), Get<TextBlock>("TierBadgeText").Text);
            Assert.True(upgrades > 0);
            Assert.Equal(1, signIns);
            Assert.False(Get<Button>("LifetimeRightsButton").IsEnabled);
            Assert.Equal(UiText.Get("已永久买断"), Get<Button>("LifetimeRightsButton").Content);
            AssertAccountHasNoOfficialCloudControls(page);
            AssertNoRetiredAccountRequests(handler);

            handler.LifetimeFeatures = false;
            await page.RefreshAsync();
            Assert.Equal("Free", Get<TextBlock>("TierBadgeText").Text);
            Assert.True(Get<Button>("LifetimeRightsButton").IsEnabled);
            Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Assert.False(Get<Button>("LifetimeRightsButton").IsEnabled);
        }
        finally
        {
            UnmountAccountCenter(scope, page);
            ((Libzora.App)Application.Current!).ApplyLanguage("zh-CN");
        }
    });

    [Fact]
    public Task RightsRefreshCannotExtendHistoricalPaidPeriods() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(45) };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var before = handler.ProExpiresAt;
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => page.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            AssertNoRetiredAccountControls(page);
            var refresh = page.FindControl<Button>("LifetimeRightsButton")!;
            Assert.True(refresh.IsEnabled);
            Assert.Null(refresh.Tag);
            var checks = handler.DeviceStatusChecks;
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.DeviceStatusChecks > checks && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.Equal(before, handler.ProExpiresAt);
            Assert.False(handler.LifetimeFeatures);
            Assert.Equal(UiText.Get(MemberEntitlements.TierName("trial")), page.FindControl<TextBlock>("TierBadgeText")!.Text);
            AssertNoRetiredAccountRequests(handler);
            AssertAccountHasNoOfficialCloudControls(page);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    [Fact]
    public Task ServerLifetimeGrantDuringTrialPreservesOwnStorageAndDoesNotSignalSignIn() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var saved = await scope.Sync.LoadSettingsAsync();
        var settings = saved.Settings with { Provider = SyncProvider.WebDav, WebDavEndpoint = "https://dav.example.invalid/dav",
            WebDavUsername = "fictional-dav-reader", WebDavPassword = "fictional-dav-password", Enabled = false, AutomaticSyncEnabled = false };
        await scope.Sync.SaveSettingsAsync(saved.DeviceId, settings);
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        var signIns = 0;
        page.AccountSignedIn += (_, _) => signIns++;
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => page.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.True(page.FindControl<Button>("LifetimeRightsButton")!.IsEnabled);
            handler.LifetimeFeatures = true;
            await page.RefreshAsync();
            Assert.Equal(UiText.Get(MemberEntitlements.TierName("lifetime")), page.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.Equal(0, signIns);
            Assert.Equal(settings, (await scope.Sync.LoadSettingsAsync()).Settings);
            Assert.False(page.FindControl<Button>("LifetimeRightsButton")!.IsEnabled);
            AssertNoRetiredAccountControls(page);
            AssertNoRetiredAccountRequests(handler);
            AssertAccountHasNoOfficialCloudControls(page);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    [Fact]
    public Task FailedRightsRefreshKeepsKnownIdentityAndCanRetryWithoutGrantingRights() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => page.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            handler.Offline = true;
            await page.RefreshAsync();
            Assert.Equal("Free", page.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            Assert.False(handler.LifetimeFeatures);
            Assert.True(page.FindControl<Button>("LifetimeRightsButton")!.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(page.FindControl<TextBlock>("StatusText")!.Text));
            handler.Offline = false;
            await page.RefreshAsync();
            Assert.Equal("Free", page.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.False(handler.LifetimeFeatures);
            AssertNoRetiredAccountRequests(handler);
            AssertNoRetiredAccountControls(page);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    private static void AssertNoRetiredAccountControls(AccountCenterView page)
    {
        foreach (var name in new[] { "ActivationCodeBox", "ActivationRedeemButton", "ActivationCard", "LifetimePurchaseButton",
                     "ProductsBox", "OrderListPanel", "OrdersExpander", "PlanPurchaseHintText" })
            Assert.Null(page.FindControl<Control>(name));
    }

    private static void AssertNoRetiredAccountRequests(MembershipHttpHandler handler) =>
        Assert.DoesNotContain(handler.RequestedActions, action => action is "products" or "orders"
            || action.StartsWith("orders/", StringComparison.Ordinal) || action.StartsWith("activation-codes/", StringComparison.Ordinal));
}
