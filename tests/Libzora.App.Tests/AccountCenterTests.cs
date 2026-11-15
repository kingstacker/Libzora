using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.TestFixtures;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    private static void MountAccountCenter(TestWindow scope, AccountCenterView page)
    {
        page.AccountStateChanged += (_, state) => scope.Call("PresentAccountSummary", state.Account, state.Membership);
        scope.Set("_accountCenterView", page);
        var host = scope.Get<ContentControl>("AccountCenterHost");
        host.Content = page;
        scope.Call("ShowStage3Page", host, scope.Get<Button>("AccountNavigationButton"));
    }

    private static void UnmountAccountCenter(TestWindow scope, AccountCenterView page)
    {
        scope.Call("ShowLibraryPage");
        page.Dispose();
        scope.Get<ContentControl>("AccountCenterHost").Content = null;
        scope.Set("_accountCenterView", null);
    }

    [Fact]
    public Task AccountCenterViewRegistersAndSignsOutThroughItsControls() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = 1024;
        scope.Window.Height = 664;
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(service, registrationAvailable: true);
        try
        {
            MountAccountCenter(scope, dialog);
            T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("RegionBox").SelectedIndex = 1;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Assert.Null(dialog.FindControl<TextBox>("ServerUrlBox"));
            Get<ComboBox>("ModeBox").SelectedIndex = 1;
            Assert.True(Get<StackPanel>("RegistrationEmailPanel").IsVisible);
            Assert.True(Get<TextBlock>("RegisterHeading").IsVisible);
            Assert.False(Get<TextBlock>("SignInHeading").IsVisible);
            await Render();
            Capture(scope.Window, "account-registration");
            Get<Button>("SubmitButton").BringIntoView();
            await Render();
            AssertWithinAccountViewport(Get<TextBox>("ConfirmPasswordBox"), Get<ScrollViewer>("AccountScrollViewer"));
            AssertWithinAccountViewport(Get<Button>("SubmitButton"), Get<ScrollViewer>("AccountScrollViewer"));
            Capture(scope.Window, "account-registration-action");
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible && Get<Button>("SignOutButton").IsEnabled);
            Assert.Equal("test_reader", Get<TextBlock>("UsernameText").Text);
            Assert.Equal(Libzora.Core.AccountRegion.International, (await service.GetStatusAsync()).Region);
            Assert.Equal(Libzora.Core.UiText.Get("国际区"), Get<TextBlock>("AccountRegionText").Text);
            Assert.Equal("reader@example.invalid", (await service.GetStatusAsync()).User!.Email);
            Assert.True(Get<TextBox>("ProfileEmailBox").IsReadOnly);
            Assert.Equal(Libzora.Core.AccountRegion.International, handler.LastRequestRegion);
            Assert.Equal("", Get<TextBox>("PasswordBox").Text);
            Assert.Equal("", Get<TextBox>("ConfirmPasswordBox").Text);
            await Render();
            AssertWithinAccountViewport(Get<Button>("SignOutButton"), Get<ScrollViewer>("AccountScrollViewer"));
            Capture(scope.Window, "account-signed-in");
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible);
            Assert.True(handler.Revoked);
            Assert.False((await service.GetStatusAsync()).IsLoggedIn);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task AccountRegistrationRequiresEmailAndMatchingPasswordBeforeSending() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(service, registrationAvailable: true);
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("ModeBox").SelectedIndex = 1;
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("SubmitButton").IsEnabled && Get<TextBlock>("StatusText").Text!.Contains("必须填写邮箱"));
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("RegistrationEmailBox").Text = "invalid email";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("SubmitButton").IsEnabled && Get<TextBlock>("StatusText").Text!.Contains("有效的邮箱"));
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = "fictional-mismatch-password";
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("密码不一致", Get<TextBlock>("StatusText").Text);
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible && Get<Button>("SignOutButton").IsEnabled);
            Assert.Equal("reader@example.invalid", (await service.GetStatusAsync()).User!.Email);
            Get<Button>("SignOutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("ModeBox").SelectedIndex = 0;
            Get<TextBox>("UsernameBox").Text = "READER@EXAMPLE.INVALID";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible);
            Assert.Equal("test_reader", (await service.GetStatusAsync()).User!.Username);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task RegistrationNoticeExplainsThatAccountsAreNotOnlineYet() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(service, registrationAvailable: false);
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<ComboBox>("ModeBox").SelectedIndex = 1;
            Assert.True(Get<Border>("RegistrationNoticeBorder").IsVisible);
            Assert.Contains("注册暂未上线", Get<TextBlock>("RegistrationNoticeText").Text);
            Assert.False(Get<TextBlock>("RegisterSubtitle").IsVisible);
            Assert.Contains("注册暂未上线", Get<TextBlock>("AccountPageSubtitle").Text);
            Assert.False(Get<Button>("SubmitButton").IsEnabled);
            Assert.Contains("注册暂未上线", Get<Button>("SubmitButton").Content!.ToString());
            await Render();
            Capture(scope.Window, "account-registration-unavailable");
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("注册暂未上线", Get<TextBlock>("StatusText").Text);
            Assert.Equal(0, handler.CredentialRequests);
            Assert.False(Get<StackPanel>("AccountPanel").IsVisible);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task SidebarAccountButtonShowsTheLoginPageInsideTheMainWindowAndKeepsNavigationAvailable() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = scope.Get<ContentControl>("AccountCenterHost");
        await Until(() => host.Content is AccountCenterView && host.IsVisible);
        var page = Assert.IsType<AccountCenterView>(host.Content);
        await Until(() => page.FindControl<Button>("SubmitButton")!.IsEnabled);
        await Render();
        Assert.Same(scope.Window, TopLevel.GetTopLevel(page));
        Assert.Empty(scope.Window.OwnedWindows);
        Assert.True(page.FindControl<StackPanel>("SignInPanel")!.IsVisible);
        Assert.Contains("active", scope.Get<Button>("AccountNavigationButton").Classes);
        Assert.DoesNotContain("active", scope.Get<Button>("AllBooksButton").Classes);
        Assert.False(scope.Get<Grid>("LibraryWorkspace").IsVisible);
        Assert.False(scope.Get<Grid>("SettingsPage").IsVisible);
        page.FindControl<TextBox>("PasswordBox")!.Text = "fictional-unsent-password";
        AssertNoRetiredAccountControls(page);
        scope.Get<Button>("SettingsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(host.IsVisible); Assert.True(scope.Get<Grid>("SettingsPage").IsVisible);
        Assert.DoesNotContain("active", scope.Get<Button>("AccountNavigationButton").Classes);
        Assert.Equal("", page.FindControl<TextBox>("PasswordBox")!.Text);
        AssertNoRetiredAccountControls(page);
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => host.IsVisible && page.FindControl<Button>("SubmitButton")!.IsEnabled);
        Assert.Same(page, host.Content); Assert.Empty(scope.Window.OwnedWindows);
        scope.Call("ShowLibraryPage");
        Assert.False(host.IsVisible); Assert.True(scope.Get<Grid>("LibraryWorkspace").IsVisible);
        Assert.Contains("active", scope.Get<Button>("AllBooksButton").Classes);

        scope.Set("_isTransferring", true);
        scope.Call("SetTransferNavigationEnabled", false);
        try
        {
            Assert.False(scope.Get<Button>("AccountNavigationButton").IsEnabled);
            scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(host.IsVisible);
            Assert.True(scope.Get<Grid>("LibraryWorkspace").IsVisible);
            Assert.Empty(scope.Window.OwnedWindows);
        }
        finally
        {
            scope.Set("_isTransferring", false);
            scope.Call("SetTransferNavigationEnabled", true);
        }
        Assert.True(scope.Get<Button>("AccountNavigationButton").IsEnabled);
    });

    [Fact]
    public Task MemberCanListKickDevicesAndRefreshRightsWithoutCreatingOrders() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false }; using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Contains("Free", Get<TextBlock>("MembershipSummary").Text);
            Assert.Equal("2 / 6 台", Get<TextBlock>("DeviceCountText").Text);
            Assert.Equal("¥88", Get<TextBlock>("LifetimePriceText").Text);
            Assert.Contains("直接绑定账号", Get<TextBlock>("PlanEntitlementHintText").Text);
            var kick = Get<StackPanel>("DeviceListPanel").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Tag is Guid id && id == handler.OtherDeviceId);
            kick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal(handler.OtherDeviceId, handler.LastKickedDevice);
            Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            var checks = handler.DeviceStatusChecks;
            Get<Button>("LifetimeRightsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.False(handler.LifetimeFeatures);
            AssertNoRetiredAccountRequests(handler);
            AssertNoRetiredAccountControls(dialog);
            Capture(scope.Window, "membership-devices-and-rights");
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData("free")]
    [InlineData("lifetime")]
    [InlineData("trial")]
    public Task AccountLoginAndDeviceSignOutUpdateTheSidebarImmediately(string tier) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = tier == "lifetime",
            ProExpiresAt = tier == "trial" ? DateTimeOffset.UtcNow.AddDays(7) : null
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Assert.Equal(UiText.Get("登录"), scope.Get<TextBlock>("AccountNavigationText").Text);
            Get<TextBox>("UsernameBox").Text = handler.User.Username;
            Get<TextBox>("PasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal("已登录", scope.Get<TextBlock>("AccountNavigationText").Text);
            Assert.Contains(tier, scope.Get<ShapePath>("AccountNavigationIcon").Classes);
            Assert.Contains(tier, Get<ShapePath>("MembershipAvatarIcon").Classes);
            Assert.Contains(handler.User.Username, ToolTip.GetTip(scope.Get<Button>("AccountNavigationButton"))!.ToString());
            AssertAccountHasNoOfficialCloudControls(dialog);

            var current = Get<StackPanel>("DeviceListPanel").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Tag is Guid id && id == handler.CurrentDeviceId);
            current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Assert.Equal(UiText.Get("登录"), scope.Get<TextBlock>("AccountNavigationText").Text);
            Assert.Contains("free", scope.Get<ShapePath>("AccountNavigationIcon").Classes);
            AssertAccountHasNoOfficialCloudControls(dialog);
            Assert.Empty(Get<StackPanel>("DeviceListPanel").Children);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData("zh-CN", 1294, 818, AppTheme.Classic)]
    [InlineData("zh-CN", 1024, 664, AppTheme.Green)]
    [InlineData("en-US", 1294, 818, AppTheme.Beige)]
    [InlineData("en-US", 1024, 664, AppTheme.Classic)]
    public Task AccountPlansShowFreeTrialAndLifetimeWithoutOfficialCloud(string language, int width, int height, AppTheme theme) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var app = (Libzora.App)Application.Current!;
        app.ApplyLanguage(language);
        app.ApplyTheme(theme);
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        scope.Window.Width = width;
        scope.Window.Height = height;
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            AssertAccountHasNoOfficialCloudControls(page);
            Assert.Equal(language == "en-US" ? "Trial" : MemberEntitlements.TierName("trial"), Get<TextBlock>("TierBadgeText").Text);
            Assert.Equal("¥88", Get<TextBlock>("LifetimePriceText").Text);
            Assert.True(Get<Button>("LifetimeRightsButton").IsEnabled);
            Assert.Equal(UiText.Get("{0} / {1} 台", 2, 6), Get<TextBlock>("DeviceCountText").Text);
            Assert.Equal(6, Get<StackPanel>("DeviceSlotsPanel").Children.Count);
            Assert.Equal(BenefitText(Get<StackPanel>("TrialBenefitsPanel")), BenefitText(Get<StackPanel>("LifetimeBenefitsPanel")));
            Assert.Contains(UiText.Get("多端进度、书签、批注和书籍同步"), BenefitText(Get<StackPanel>("TrialBenefitsPanel")));
            Assert.Contains(UiText.Get("自备 S3 / WebDAV 多端同步"), BenefitText(Get<StackPanel>("LifetimeBenefitsPanel")));
            Assert.DoesNotContain(BenefitText(Get<StackPanel>("FreeBenefitsPanel")), text => text.Contains("S3", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(Get<TextBlock>("PurchaseTermsText").Text));
            Assert.Null(page.FindControl<TextBlock>("NoRefundPolicyText"));
            await Render();
            AssertWithinAccountViewport(Get<Button>("SignOutButton"), Get<ScrollViewer>("AccountScrollViewer"));
            Assert.Equal(Colors.Transparent, ColorOf(page.Background));
            Capture(scope.Window, $"account-membership-{language}-{width}x{height}-{theme}");
            Get<StackPanel>("MembershipPlansPanel").BringIntoView();
            await Render();
            var viewport = Get<ScrollViewer>("AccountScrollViewer");
            foreach (var name in new[] { "FreePlanCard", "TrialCard", "LifetimePlanCard" })
            {
                var card = Get<Border>(name);
                var position = card.TranslatePoint(default, viewport)!.Value;
                Assert.True(position.X >= 0 && position.X + card.Bounds.Width <= viewport.Bounds.Width + 1, name);
            }
            Capture(scope.Window, $"account-plans-{language}-{width}x{height}-{theme}");
            Get<Button>("LifetimeRightsButton").BringIntoView();
            await Render();
            foreach (var name in new[] { "FreePlanButton", "TrialClaimButton", "LifetimeRightsButton" })
                AssertWithinAccountViewport(Get<Button>(name), viewport);
            Capture(scope.Window, $"account-plan-actions-{language}-{width}x{height}-{theme}");
        }
        finally
        {
            UnmountAccountCenter(scope, page);
            app.ApplyLanguage("zh-CN");
            app.ApplyTheme(AppTheme.Classic);
        }
    });

    [Theory]
    [InlineData("trial", 7, "trial", "zh-CN")]
    [InlineData("pro", 30, "trial", "zh-CN")]
    [InlineData("trial", -1, "free", "zh-CN")]
    [InlineData("pro", -1, "free", "zh-CN")]
    [InlineData("lifetime", -1, "lifetime", "zh-CN")]
    [InlineData("pro", 45, "trial", "zh-CN")]
    [InlineData("trial", 7, "trial", "en-US")]
    [InlineData("pro", 30, "trial", "en-US")]
    [InlineData("trial", -1, "free", "en-US")]
    [InlineData("pro", -1, "free", "en-US")]
    [InlineData("lifetime", -1, "lifetime", "en-US")]
    [InlineData("pro", 45, "trial", "en-US")]
    public Task TrialAndLegacyMembershipNeverGrantPermanentPurchase(string responseTier, int expiryDays, string expectedTier, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        var expiry = DateTimeOffset.UtcNow.AddDays(expiryDays);
        var entitled = responseTier == "lifetime" || expiryDays > 0;
        var claimedTrial = responseTier == "trial";
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = false,
            TransformStatus = status => status with { Membership = status.Membership with
            {
                Tier = responseTier, HasLifetimeFeatures = entitled,
                ActivatedDeviceCount = entitled ? status.Membership.LoggedInDeviceCount : 0,
                ProExpiresAt = responseTier is "pro" or "lifetime" ? expiry : null,
                TrialExpiresAt = claimedTrial ? expiry : null,
                TrialClaimed = claimedTrial,
                TrialClaimedAt = claimedTrial ? expiry.AddDays(-MemberEntitlements.TrialDays) : null,
                TrialAvailable = false
            } }
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => page.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.Contains(expectedTier, page.FindControl<ShapePath>("MembershipAvatarIcon")!.Classes);
            var expectedBadge = language == "en-US"
                ? expectedTier switch { "trial" => "Trial", "lifetime" => "Lifetime", _ => "Free" }
                : MemberEntitlements.TierName(expectedTier);
            Assert.Equal(expectedBadge, page.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.Equal(expectedTier != "lifetime", page.FindControl<Button>("LifetimeRightsButton")!.IsEnabled);
            var expectsNotice = expectedTier == "free" || expectedTier == "trial" && expiryDays <= 30;
            Assert.Equal(expectsNotice, page.FindControl<Border>("ExpiryNoticeBorder")!.IsVisible);
            if (expectsNotice)
            {
                var message = expectedTier == "free"
                    ? MemberEntitlements.TrialExpiryMessage(expiry, DateTimeOffset.UtcNow)
                    : MemberEntitlements.TrialRenewalMessage(expiry, DateTimeOffset.UtcNow);
                Assert.Equal(message, page.FindControl<TextBlock>("ExpiryNoticeText")!.Text);
                Assert.DoesNotContain(language == "en-US" ? "retention" : "保留期", message);
                Assert.DoesNotContain(language == "en-US" ? "renewal" : "续费", message);
                Assert.Contains(language == "en-US" ? expectedTier == "free" ? "are not deleted" : "will not be deleted" : "不会删除", message);
            }
            AssertAccountHasNoOfficialCloudControls(page);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public Task BothRegionsValidateAccountRightsWithoutPurchaseOrCodeInterfaces(AccountRegion region) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SelectRegionAsync(region);
        await accounts.SignInAsync(AccountRegions.DefaultServerUrl(region), handler.User.Username, MembershipHttpHandler.Password);
        var page = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, page);
            await Until(() => page.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            AssertNoRetiredAccountControls(page);
            AssertAccountHasNoOfficialCloudControls(page);
            var refresh = page.FindControl<Button>("LifetimeRightsButton")!;
            Assert.True(refresh.IsEnabled);
            Assert.Null(refresh.Tag);
            Assert.Equal("Free", page.FindControl<TextBlock>("TierBadgeText")!.Text);
            var checks = handler.DeviceStatusChecks;
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.DeviceStatusChecks > checks && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.False(handler.LifetimeFeatures);
            handler.LifetimeFeatures = true;
            await page.RefreshAsync();
            Assert.Equal(UiText.Get(MemberEntitlements.TierName("lifetime")), page.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.False(refresh.IsEnabled);
            AssertNoRetiredAccountRequests(handler);
            Assert.Equal(region, (await accounts.GetStatusAsync()).Region);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    [Fact]
    public Task AccountProfilePasswordAndClosureWorkThroughTheirControls() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler();
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Email!, MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        T Get<T>(string name) where T : Control => dialog.FindControl<T>(name)!;
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => Get<StackPanel>("MembershipPanel").IsEnabled);
            Get<TextBox>("ProfileUsernameBox").Text = handler.UnavailableUsername;
            Get<TextBox>("ProfilePasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("ProfileSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Border>("AccountSettingsCard").IsEnabled && Get<TextBlock>("StatusText").Text!.Contains("已注册"));
            Assert.Equal("fictional_member", (await accounts.GetStatusAsync()).User!.Username);
            Get<TextBox>("ProfileUsernameBox").Text = "renamed_reader";
            Assert.True(Get<TextBox>("ProfileEmailBox").IsReadOnly);
            Get<TextBox>("ProfilePasswordBox").Text = MembershipHttpHandler.Password;
            Get<Button>("ProfileSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Border>("AccountSettingsCard").IsEnabled && Get<TextBlock>("UsernameText").Text == "renamed_reader");
            Assert.Equal("member@example.invalid", (await accounts.GetStatusAsync()).User!.Email);
            Assert.Equal("member@example.invalid", Get<TextBox>("ProfileEmailBox").Text);
            Assert.Equal("", Get<TextBox>("ProfilePasswordBox").Text);
            Get<TextBox>("CurrentPasswordBox").Text = MembershipHttpHandler.Password;
            Get<TextBox>("NewPasswordBox").Text = "fictional-new-reader-password";
            Get<TextBox>("ConfirmNewPasswordBox").Text = "fictional-new-reader-password";
            Get<Button>("PasswordSaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Border>("AccountSettingsCard").IsEnabled && Get<StackPanel>("DeviceListPanel").Children.Count == 1);
            Assert.Equal("", Get<TextBox>("CurrentPasswordBox").Text);
            Get<Button>("CloseAccountButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Any());
            var confirmation = scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Single();
            await Render();
            Capture(confirmation, "account-closure-confirmation");
            confirmation.FindControl<TextBox>("ClosurePasswordBox")!.Text = "fictional-new-reader-password";
            confirmation.FindControl<TextBox>("ClosureConfirmationBox")!.Text = "停用";
            Assert.False(confirmation.FindControl<Button>("ClosureConfirmButton")!.IsEnabled);
            Assert.False(handler.AccountClosed);
            confirmation.FindControl<Button>("ClosureCancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !scope.Window.OwnedWindows.Any());
            Assert.False(handler.AccountClosed);
            Get<Button>("CloseAccountButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Any());
            confirmation = scope.Window.OwnedWindows.OfType<AccountClosureWindow>().Single();
            confirmation.FindControl<TextBox>("ClosurePasswordBox")!.Text = "fictional-new-reader-password";
            confirmation.FindControl<TextBox>("ClosureConfirmationBox")!.Text = "停用账号";
            await Until(() => confirmation.FindControl<Button>("ClosureConfirmButton")!.IsEnabled);
            Assert.True(confirmation.FindControl<Button>("ClosureConfirmButton")!.IsEnabled);
            confirmation.FindControl<Button>("ClosureConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.AccountClosed && Get<StackPanel>("SignInPanel").IsVisible);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
            Assert.Equal("", confirmation.FindControl<TextBox>("ClosurePasswordBox")!.Text);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task AccountCenterOnlyClaimsTheTrialWhenExplicitlyRequested(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false, TrialAvailable = true };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password,
            true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("MembershipPanel") is { IsVisible: true, IsEnabled: true }
                && dialog.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2);
            var claim = dialog.FindControl<Button>("TrialClaimButton")!;
            Assert.True(claim.IsEnabled);
            Assert.Null(handler.TrialClaimedAt);
            AssertAccountHasNoOfficialCloudControls(dialog);
            await dialog.RefreshAsync();
            Assert.Null(handler.TrialClaimedAt);
            Assert.True(claim.IsEnabled);

            claim.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.TrialClaimedAt is not null
                && dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.False(claim.IsEnabled);
            Assert.Equal(handler.Now.AddDays(7), handler.TrialExpiresAt);
            Assert.True(dialog.FindControl<TextBlock>("TrialStatusText")!.IsVisible);
            Assert.Contains(language == "en-US" ? "Trial" : "试用期至", dialog.FindControl<TextBlock>("TrialStatusText")!.Text);
            Assert.Equal(language == "en-US" ? "Trial" : MemberEntitlements.TierName("trial"), dialog.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.True(dialog.FindControl<Button>("LifetimeRightsButton")!.IsEnabled);
            var claimedAt = handler.TrialClaimedAt;
            claim.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.RefreshAsync();
            Assert.Equal(claimedAt, handler.TrialClaimedAt);
            AssertAccountHasNoOfficialCloudControls(dialog);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task LegacyCloudStatisticsDoNotAffectFeatureValidationOrCreatePurchaseUi() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = false, ProExpiresAt = DateTimeOffset.UtcNow.AddDays(30), UseMinimalMembershipContract = false,
            TransformStatus = status => status with { Membership = status.Membership with { StorageBytes = 24L * 1024 * 1024 * 1024 } }
        };
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && dialog.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            AssertAccountHasNoOfficialCloudControls(dialog);
            Assert.Equal(MemberEntitlements.TierName("trial"), dialog.FindControl<TextBlock>("TierBadgeText")!.Text);
            Assert.True(dialog.FindControl<Button>("LifetimeRightsButton")!.IsEnabled);
            Assert.Null(dialog.FindControl<Button>("LifetimeRightsButton")!.Tag);
            Assert.False(dialog.FindControl<TextBlock>("MembershipStatus")!.IsVisible);
            AssertNoRetiredAccountControls(dialog);
            AssertNoRetiredAccountRequests(handler);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task DailyInvalidationRestoresTheAccountCenterViewsLoginForm() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler(); using var http = new HttpClient(handler);
        using var accounts = new AccountService(scope.Paths, new AccountTestProtector(), http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        using var monitor = new AccountDeviceMonitor(accounts, scope.Paths);
        var dialog = new AccountCenterView(accounts, new DeviceMembershipService(accounts));
        try
        {
            MountAccountCenter(scope, dialog);
            await Until(() => dialog.FindControl<StackPanel>("MembershipPanel")!.IsVisible);
            handler.Revoked = true; await monitor.CheckIfDueAsync();
            await Until(() => dialog.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False(dialog.FindControl<StackPanel>("MembershipPanel")!.IsVisible);
            Assert.Contains("重新登录", dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
        }
        finally { UnmountAccountCenter(scope, dialog); }
    });

    [Fact]
    public Task ClosingTheMainWindowCancelsTheAccountRefreshAndClearsSensitiveInputs() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var membership = new MembershipHttpHandler();
        using var handler = new DeferredAccountRefreshHandler(membership);
        using var http = new HttpClient(handler);
        var accounts = scope.Field<Libzora.ApplicationServices.ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(accounts, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, membership.User.Username, MembershipHttpHandler.Password);
        handler.DeferSession = true;
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => handler.Started.Task.IsCompleted);
        var page = Assert.IsType<AccountCenterView>(scope.Get<ContentControl>("AccountCenterHost").Content);
        page.FindControl<TextBox>("PasswordBox")!.Text = "fictional-unsent-password";
        AssertNoRetiredAccountControls(page);
        scope.RequestQuit();
        await Until(() => handler.Cancelled.Task.IsCompleted);
        Assert.Equal("", page.FindControl<TextBox>("PasswordBox")!.Text);
        AssertNoRetiredAccountControls(page);
    });

    [Theory]
    [InlineData("pro")]
    [InlineData("storage")]
    [InlineData("download")]
    public Task LegacyButtonTagsCannotCreatePurchasesOrGrantRights(string kind) => Run(async () =>
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
            var refresh = page.FindControl<Button>("LifetimeRightsButton")!;
            Assert.True(refresh.IsEnabled);
            Assert.Null(refresh.Tag);
            refresh.Tag = kind;
            var checks = handler.DeviceStatusChecks;
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.DeviceStatusChecks > checks && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            Assert.False(handler.LifetimeFeatures);
            Assert.Equal("Free", page.FindControl<TextBlock>("TierBadgeText")!.Text);
            AssertNoRetiredAccountControls(page);
            AssertNoRetiredAccountRequests(handler);
            AssertAccountHasNoOfficialCloudControls(page);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    [Theory]
    [InlineData("trial")]
    [InlineData("lifetime")]
    public Task AccountLoginPreservesTheConfiguredSelfHostedProvider(string tier) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new MembershipHttpHandler
        {
            LifetimeFeatures = tier == "lifetime", ProExpiresAt = tier == "trial" ? DateTimeOffset.UtcNow.AddDays(7) : null
        };
        using var http = new HttpClient(handler);
        var accounts = scope.Field<Libzora.ApplicationServices.ApplicationModules>("_modules").Accounts.Service;
        typeof(AccountService).GetField("_http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(accounts, http);
        var saved = await scope.Sync.LoadSettingsAsync();
        var settings = saved.Settings with { Provider = SyncProvider.WebDav, WebDavEndpoint = "https://dav.example.invalid/dav",
            WebDavUsername = "fictional-dav-reader", WebDavPassword = "fictional-dav-password", Enabled = false, AutomaticSyncEnabled = false };
        await scope.Sync.SaveSettingsAsync(saved.DeviceId, settings);
        scope.Set("_s3SyncStoredSettings", new S3SyncStoredSettings(saved.DeviceId, settings));
        scope.Call("PopulateS3SyncControls", (object?)null);
        scope.Get<Button>("AccountNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = scope.Get<ContentControl>("AccountCenterHost");
        await Until(() => host.Content is AccountCenterView && host.IsVisible);
        var page = Assert.IsType<AccountCenterView>(host.Content);
        try
        {
            await Until(() => page.FindControl<Button>("SubmitButton")!.IsEnabled);
            page.FindControl<TextBox>("UsernameBox")!.Text = handler.User.Username;
            page.FindControl<TextBox>("PasswordBox")!.Text = MembershipHttpHandler.Password;
            page.FindControl<Button>("SubmitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => page.FindControl<StackPanel>("DeviceListPanel")!.Children.Count == 2 && page.FindControl<StackPanel>("MembershipPanel")!.IsEnabled);
            await scope.Call<Task>("RefreshAccountSummaryAsync");
            Assert.Equal(settings, (await scope.Sync.LoadSettingsAsync()).Settings);
            AssertAccountHasNoOfficialCloudControls(page);
        }
        finally { UnmountAccountCenter(scope, page); }
    });

    private static string[] BenefitText(StackPanel panel) => panel.GetVisualDescendants()
        .OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();

    private static void AssertAccountHasNoOfficialCloudControls(AccountCenterView page)
    {
        foreach (var name in new[] { "CloudUsagePanel", "CloudLockedPanel", "StorageCard", "TrafficCard", "ViewPlusButton",
                     "PlusPlanCard", "PlusPurchaseButton", "PlusBenefitsPanel", "StorageUsageRing", "TrafficUsageRing" })
            Assert.Null(page.FindControl<Control>(name));
    }

    private static void AssertWithinAccountViewport(Control control, ScrollViewer viewport)
    {
        var position = control.TranslatePoint(default, viewport)!.Value;
        var message = $"{control.Name}: position {position}, size {control.Bounds.Size}, viewport {viewport.Bounds.Size}";
        Assert.True(position.X >= -1 && position.Y >= -1, message);
        Assert.True(position.X + control.Bounds.Width <= viewport.Bounds.Width + 1, message);
        Assert.True(position.Y + control.Bounds.Height <= viewport.Bounds.Height + 1, message);
    }

    private sealed class DeferredAccountRefreshHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool DeferSession { get; set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (DeferSession && request.RequestUri!.AbsolutePath.EndsWith("/session", StringComparison.Ordinal))
            {
                Started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { Cancelled.TrySetResult(true); throw; }
            }
            return await base.SendAsync(request, token);
        }
    }

    private sealed class AccountTestProtector : Libzora.Core.ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
