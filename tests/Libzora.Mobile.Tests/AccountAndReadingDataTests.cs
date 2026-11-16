using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using Libzora.TestFixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class AccountAndReadingDataTests(MobileUiSession session)
{
    [Fact]
    public Task RegionButtonsKeepIndependentLoginsAndClearPasswordFields() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-regions-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(paths, protector, http, dataRegion: AccountRegion.China);
        var services = Services(paths, protector);
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var page = new AccountPage(accounts, shell, registrationAvailable: true);
        var window = new Window { Width = 411, Height = 880, Content = page };
        T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
        try
        {
            window.Show();
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Get<TextBox>("PasswordBox").Text = "fictional-draft-password";
            Click(Get<Button>("InternationalRegionButton"));
            await Until(() => Get<Button>("SubmitButton").IsEnabled && Get<Button>("InternationalRegionButton").Classes.Contains("on"));
            Assert.Equal("", Get<TextBox>("PasswordBox").Text);
            Assert.Equal(AccountRegions.DefaultServerUrl(AccountRegion.International), Get<TextBox>("ServerUrlBox").Text);
            Assert.True(Get<TextBlock>("RestartText").IsVisible);
            Click(Get<Button>("RegisterModeButton"));
            await Register();
            var foreign = (await accounts.GetStatusAsync()).User;
            Assert.Equal(AccountRegion.International, handler.LastRequestRegion);
            Click(Get<Button>("ChinaRegionButton"));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Assert.False(Get<TextBlock>("RestartText").IsVisible);
            Assert.Equal(AccountRegions.DefaultServerUrl(AccountRegion.China), Get<TextBox>("ServerUrlBox").Text);
            await Register();
            var chinese = (await accounts.GetStatusAsync()).User;
            Assert.NotEqual(chinese, foreign);
            Click(Get<Button>("InternationalRegionButton"));
            await Until(() => Get<Button>("SignOutButton").IsEnabled && Get<Button>("InternationalRegionButton").Classes.Contains("on"));
            Assert.Equal(foreign, (await accounts.GetStatusAsync()).User);
            Click(Get<Button>("SignOutButton"));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Click(Get<Button>("ChinaRegionButton"));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible && Get<Button>("SignOutButton").IsEnabled);
            Assert.Equal(chinese, (await accounts.GetStatusAsync()).User);
        }
        finally
        {
            page.OnClosed(); window.Close(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        async Task Register()
        {
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Click(Get<Button>("SubmitButton"));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible && Get<Button>("SignOutButton").IsEnabled);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task RegisterChecksConfirmationThenShowsAccountAndLogoutRestoresTheForm() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-account-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(paths, protector, http);
        var services = Services(paths, protector);
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var page = new AccountPage(accounts, shell, registrationAvailable: true);
        var window = new Window { Width = 411, Height = 880, Content = page };
        try
        {
            window.Show();
            T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Click(Get<Button>("RegisterModeButton"));
            Assert.True(Get<StackPanel>("ConfirmPasswordPanel").IsVisible);
            Assert.True(Get<StackPanel>("RegistrationEmailPanel").IsVisible);
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = "fictional-mismatch";
            Click(Get<Button>("SubmitButton"));
            Assert.Contains("密码不一致", Get<TextBlock>("StatusText").Text);
            Assert.Equal(0, handler.CredentialRequests);
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Click(Get<Button>("SubmitButton"));
            await Until(() => Get<StackPanel>("AccountPanel").IsVisible);
            Assert.Equal("test_reader", Get<TextBlock>("UsernameText").Text);
            Assert.Equal("", Get<TextBox>("PasswordBox").Text);
            Assert.Equal("", Get<TextBox>("ConfirmPasswordBox").Text);
            Assert.False(Get<StackPanel>("SignInPanel").IsVisible);
            using var restored = new AccountService(paths, protector, http);
            Assert.True((await restored.GetStatusAsync(true)).IsLoggedIn);
            Click(Get<Button>("SignOutButton"));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible);
            Assert.True(handler.Revoked);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
        }
        finally
        {
            page.OnClosed(); window.Close(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task RegistrationShowsUnavailableNoticeAndDoesNotSendCredentials() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-register-closed-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var accounts = new AccountService(paths, protector, http);
        var services = Services(paths, protector);
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var page = new AccountPage(accounts, shell);
        var window = new Window { Width = 411, Height = 880, Content = page };
        try
        {
            window.Show();
            T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
            await Until(() => Get<Button>("SubmitButton").IsEnabled);
            Click(Get<Button>("RegisterModeButton"));
            Assert.True(Get<Border>("RegistrationNoticeBorder").IsVisible);
            Assert.False(Get<Button>("SubmitButton").IsEnabled);
            Assert.Contains("注册暂未上线", Get<Button>("SubmitButton").Content!.ToString());
            Get<TextBox>("UsernameBox").Text = "test_reader";
            Get<TextBox>("RegistrationEmailBox").Text = "reader@example.invalid";
            Get<TextBox>("PasswordBox").Text = AccountHttpHandler.TestPassword;
            Get<TextBox>("ConfirmPasswordBox").Text = AccountHttpHandler.TestPassword;
            Click(Get<Button>("SubmitButton"));
            Assert.Contains("注册暂未上线", Get<TextBlock>("StatusText").Text);
            Assert.Equal(0, handler.CredentialRequests);
            Assert.True(Get<StackPanel>("SignInPanel").IsVisible);
        }
        finally
        {
            page.OnClosed(); window.Close(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task WrongPasswordKeepsTheLoginFormAndShowsAnError() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-login-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        using var http = new HttpClient(new AccountHttpHandler());
        using var accounts = new AccountService(paths, protector, http);
        var services = Services(paths, protector);
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var page = new AccountPage(accounts, shell);
        var window = new Window { Width = 411, Height = 880, Content = page };
        try
        {
            window.Show();
            await Until(() => page.FindControl<Button>("SubmitButton")!.IsEnabled);
            page.FindControl<TextBox>("UsernameBox")!.Text = "test_reader";
            page.FindControl<TextBox>("PasswordBox")!.Text = "fictional-wrong-password";
            Click(page.FindControl<Button>("SubmitButton")!);
            await Until(() => page.FindControl<TextBlock>("StatusText")!.Text == "用户名、邮箱或密码错误。");
            Assert.True(page.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
            Assert.Equal("", page.FindControl<TextBox>("PasswordBox")!.Text);
        }
        finally
        {
            page.OnClosed(); window.Close(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task MembershipPageManagesDevicesRightsRefreshAndDailyInvalidation() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-membership-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root); var protector = new TestProtector();
        using var handler = new MembershipHttpHandler(); using var http = new HttpClient(handler);
        using var accounts = new AccountService(paths, protector, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password, true, email: handler.User.Email, confirmPassword: MembershipHttpHandler.Password);
        var services = Services(paths, protector); using var modules = MobileComposition.Create(services);
        var confirmations = 0;
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths))
            { NativeConfirmation = (_, _, _) => { confirmations++; return Task.FromResult(true); } };
        var page = new AccountPage(accounts, shell, new DeviceMembershipService(accounts));
        var window = new Window { Width = 411, Height = 880, Content = page };
        T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
        try
        {
            window.Show();
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Contains("买断订阅", Get<TextBlock>("MembershipSummary").Text);
            Assert.Contains("已登录 2/6 台", Get<TextBlock>("MembershipSummary").Text);
            var kick = Get<StackPanel>("DeviceListPanel").Children.OfType<StackPanel>().SelectMany(row => row.Children).OfType<Button>()
                .Single(button => button.Tag is Guid id && id == handler.OtherDeviceId);
            Click(kick);
            await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 1 && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Equal(1, confirmations); Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            var checks = handler.DeviceStatusChecks;
            Click(Get<Button>("LifetimeRightsButton"));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            AssertNoRetiredMemberControls(page);
            AssertNoRetiredMemberRequests(handler);
            using var monitor = new AccountDeviceMonitor(accounts, paths); handler.Revoked = true;
            await monitor.CheckIfDueAsync(); await Until(() => Get<StackPanel>("SignInPanel").IsVisible);
            Assert.Contains("重新登录", Get<TextBlock>("StatusText").Text); Assert.False(Get<StackPanel>("MembershipPanel").IsVisible);
        }
        finally { page.OnClosed(); window.Close(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ReadingPeriodButtonsChangeTheChartAndPreserveTheOverview() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-periods-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = Services(paths, new TestProtector());
        using var modules = MobileComposition.Create(services);
        await modules.Library.Books.InitializeAsync();
        await modules.Reader.Data.InitializeAsync();
        await modules.Reader.Data.AddReadingTimeAsync(Guid.NewGuid(), Guid.NewGuid(), 120, 10, 0, 1);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show(); shell.ShowReadingData();
            var page = Assert.IsType<ReadingDataPage>(shell.CurrentPage);
            T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
            await Until(() => Get<ItemsControl>("DaysChart").Items.Count == 7);
            var overview = Get<TextBlock>("HeroDetailText").Text;
            Assert.Null(page.FindControl<Button>("Range14"));
            Click(Get<Button>("RangeYear"));
            var bars = Get<ItemsControl>("DaysChart").Items.Cast<ReadingDayBar>().ToArray();
            Assert.Equal(12, bars.Length);
            Assert.Equal(120, bars.Sum(bar => bar.Seconds));
            Assert.Contains("on", Get<Button>("RangeYear").Classes);
            Assert.Equal(overview, Get<TextBlock>("HeroDetailText").Text);
            Click(Get<Button>("RangeMonth"));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            Assert.Equal(DateTime.DaysInMonth(today.Year, today.Month), Get<ItemsControl>("DaysChart").Items.Count);
            Click(Get<Button>("RangeWeek"));
            Assert.Equal(7, Get<ItemsControl>("DaysChart").Items.Count);
            Assert.DoesNotContain("on", Get<Button>("RangeYear").Classes);
        }
        finally
        {
            window.Close(); await Task.Delay(100); SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task MobileRightsRefreshOnlyUsesServerGrantsAndKeepsRegionsSeparate() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-rights-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root); var protector = new TestProtector();
        using var handler = new MembershipHttpHandler { LifetimeFeatures = false }; using var http = new HttpClient(handler);
        using var accounts = new AccountService(paths, protector, http);
        await accounts.SignInAsync(AccountService.DefaultServerUrl, handler.User.Username, MembershipHttpHandler.Password);
        var services = Services(paths, protector); using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var page = new AccountPage(accounts, shell, new DeviceMembershipService(accounts));
        var window = new Window { Width = 411, Height = 880, Content = page };
        T Get<T>(string name) where T : Control => page.FindControl<T>(name)!;
        try
        {
            window.Show(); await Until(() => Get<StackPanel>("DeviceListPanel").Children.Count == 2 && Get<StackPanel>("MembershipPanel").IsEnabled);
            AssertNoRetiredMemberControls(page);
            Assert.Contains("Free", Get<TextBlock>("MembershipSummary").Text);
            var checks = handler.DeviceStatusChecks;
            Click(Get<Button>("LifetimeRightsButton"));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.False(handler.LifetimeFeatures);
            Assert.Contains("Free", Get<TextBlock>("MembershipSummary").Text);
            Assert.Null(handler.TrialClaimedAt);

            // A fictional protected server grant is the only source of permanent rights.
            handler.LifetimeFeatures = true;
            checks = handler.DeviceStatusChecks;
            Click(Get<Button>("LifetimeRightsButton"));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Contains("买断订阅", Get<TextBlock>("MembershipSummary").Text);
            Assert.DoesNotContain("Plus", Get<TextBlock>("MembershipSummary").Text);
            AssertNoRetiredMemberRequests(handler);

            handler.LifetimeFeatures = false;
            checks = handler.DeviceStatusChecks;
            Click(Get<Button>("LifetimeRightsButton"));
            await Until(() => handler.DeviceStatusChecks > checks && Get<StackPanel>("MembershipPanel").IsEnabled);
            Assert.Contains("Free", Get<TextBlock>("MembershipSummary").Text);
            Assert.True((await accounts.GetStatusAsync()).IsLoggedIn);
            Click(Get<Button>("SignOutButton"));
            await Until(() => Get<StackPanel>("SignInPanel").IsVisible && Get<Button>("SubmitButton").IsEnabled);
            Click(Get<Button>("InternationalRegionButton"));
            await Until(() => Get<Button>("SubmitButton").IsEnabled && (Get<TextBlock>("PlansRegionText").Text?.Contains("国际区") == true));
            Assert.False(Get<StackPanel>("MembershipPanel").IsVisible);
            Assert.False((await accounts.GetStatusAsync()).IsLoggedIn);
            Assert.Equal(AccountRegion.International, (await accounts.GetStatusAsync()).Region);
            AssertNoRetiredMemberControls(page);
            AssertNoRetiredMemberRequests(handler);
        }
        finally { page.OnClosed(); window.Close(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        return true;
    }, CancellationToken.None);

    private static void AssertNoRetiredMemberControls(AccountPage page)
    {
        foreach (var name in new[] { "ActivationCodeBox", "ActivationRedeemButton", "ActivationStatusText", "ProductsBox", "PurchaseButton", "OrderListPanel" })
            Assert.Null(page.FindControl<Control>(name));
    }

    private static void AssertNoRetiredMemberRequests(MembershipHttpHandler handler) =>
        Assert.DoesNotContain(handler.RequestedActions, action => action is "products" or "orders"
            || action.StartsWith("orders/", StringComparison.Ordinal) || action.StartsWith("activation-codes/", StringComparison.Ordinal));

    private static MobileServices Services(AppPaths paths, ISecretProtector protector) => new(paths, protector,
        new BookMetadataService(), new UnavailableBookFormatConverter(), new NoFiles());
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
    private sealed class NoFiles : IFileImportSource
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public void ReleaseFiles(IReadOnlyList<string> files) { }
    }
}
