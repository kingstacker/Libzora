using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;

namespace Libzora;

public partial class MainWindow
{
    private AccountCenterView? _accountCenterView;
    private bool _deviceChecksStarted;
    private AccountStatus? _syncAccountStatus;
    private AccountMembership? _accountNavigationMembership;
    private int _accountSummaryRevision;

    private async void AccountNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (_accountCenterView is null)
        {
            _accountCenterView = new AccountCenterView(_modules.Accounts.Service, _modules.Accounts.Membership,
                AccountAvailability.RegistrationAvailable);
            _accountCenterView.AccountStateChanged += (_, state) => PresentAccountSummary(state.Account, state.Membership);
            // Signing in changes account/feature state, never the user's storage provider.
            AccountCenterHost.Content = _accountCenterView;
        }
        ShowStage3Page(AccountCenterHost, AccountNavigationButton);
        await _accountCenterView.RefreshAsync();
    }

    private void StartDeviceChecks()
    {
        if (_deviceChecksStarted) return;
        _deviceChecksStarted = true;
        _modules.Accounts.Service.DeviceInvalidated += OnDeviceInvalidated;
        _modules.Accounts.Monitor.MembershipExpired += OnMembershipExpired;
        _modules.Accounts.Monitor.MembershipExpiring += OnMembershipExpiring;
        _modules.Accounts.Monitor.Start();
    }

    private void OnMembershipExpired(object? sender, Libzora.Infrastructure.AccountMembershipExpiredEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetimeCancellation.IsCancellationRequested) return;
            await RefreshAccountSummaryAsync();
            await ShowMessageAsync(UiText.Get("试用订阅已到期"), e.Message);
        });

    private void OnMembershipExpiring(object? sender, Libzora.Infrastructure.AccountMembershipExpiringEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetimeCancellation.IsCancellationRequested) return;
            await RefreshAccountSummaryAsync();
            await ShowMessageAsync(UiText.Get("试用订阅即将到期"), e.Message);
        });

    private void OnDeviceInvalidated(object? sender, Libzora.Infrastructure.AccountDeviceInvalidatedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetimeCancellation.IsCancellationRequested) return;
            await RefreshAccountSummaryAsync();
            await ShowMessageAsync(UiText.Get("设备已失效"), UiText.Localize(e.Message));
        });

    private async Task RefreshAccountSummaryAsync()
    {
        var revision = ++_accountSummaryRevision;
        try
        {
            var status = await _modules.Accounts.Service.GetStatusAsync(cancellationToken: _lifetimeCancellation.Token);
            if (revision != _accountSummaryRevision || _lifetimeCancellation.IsCancellationRequested) return;
            PresentAccountSummary(status, null);
            revision = _accountSummaryRevision;
            if (status.IsLoggedIn)
            {
                try
                {
                    var membership = await _modules.Accounts.Membership.RefreshAsync(_lifetimeCancellation.Token);
                    if (revision == _accountSummaryRevision && !_lifetimeCancellation.IsCancellationRequested)
                        PresentAccountSummary(status, membership.Membership);
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A failed membership refresh keeps the known account
                    // label; login state and feature authorization are separate.
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_syncAccountStatus is { } known) PresentAccountSummary(known, null);
        }
    }

    private void PresentAccountSummary(AccountStatus status, AccountMembership? membership)
    {
        ++_accountSummaryRevision;
        if (_syncAccountStatus?.User?.Id != status.User?.Id || _syncAccountStatus?.Region != status.Region || !status.IsLoggedIn)
            _accountNavigationMembership = null;
        if (membership is not null && membership.AccountId == status.User?.Id && membership.Region == status.Region)
            _accountNavigationMembership = membership;
        _syncAccountStatus = status;
        var tier = AccountVisuals.Tier(_accountNavigationMembership);
        AccountNavigationText.Text = UiText.Get(status.IsLoggedIn ? "已登录" : "登录");
        AccountNavigationIcon.Data = AccountVisuals.MembershipIcon(tier, status.IsLoggedIn);
        AccountVisuals.SetTier(AccountNavigationIcon, tier);
        var region = UiText.Get(AccountRegions.DisplayName(status.Region));
        var tooltip = status.IsLoggedIn ? status.User!.Username + "\n" + region
            + (_accountNavigationMembership is null ? "" : " · " + UiText.Get(MemberEntitlements.TierName(tier))) : region;
        AccountNavigationButton.SetValue(Avalonia.Controls.ToolTip.TipProperty, tooltip);
        UpdateAccountSyncPresentation();
        UpdateS3SettingsDraftState();
    }

    private void UpdateAccountSyncPresentation()
    {
        SyncProviderBox.SetValue(Avalonia.Controls.ToolTip.TipProperty,
            UiText.Get("仅支持自备 S3 / WebDAV，不提供官方云同步。"));
        if (!AccountAvailability.EnforceSyncEntitlements)
        {
            SyncMembershipNoticeText.Text = "";
            SyncMembershipNoticeText.IsVisible = false;
            return;
        }
        string? notice = null;
        var status = _syncAccountStatus;
        if (status is { RequiresDataRestart: true } || status is not null && status.Region != _modules.DataRegion)
            notice = UiText.Get("区域已切换，请重新打开应用后同步。");
        else if (status is not { IsLoggedIn: true })
            notice = UiText.Get("登录后领取试用订阅或购买买断订阅，即可使用自备 S3 / WebDAV 多端同步。");
        else if (_accountNavigationMembership is { } membership
            && MemberEntitlements.EffectiveTier(membership, DateTimeOffset.UtcNow) == "free")
            notice = membership.FeatureExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow
                ? MemberEntitlements.TrialExpiryMessage(expiry, DateTimeOffset.UtcNow)
                : UiText.Get(MemberEntitlements.SyncRequirement);
        SyncMembershipNoticeText.Text = notice;
        SyncMembershipNoticeText.IsVisible = notice is not null;
    }

    private Task<S3SyncSettings> BindSyncRegionAsync(S3SyncSettings settings, CancellationToken token) =>
        settings.Provider == SyncProvider.AccountServer
            ? Task.FromException<S3SyncSettings>(new InvalidOperationException(MemberEntitlements.AccountCloudRequirement))
            : Task.FromResult(settings with { AccountRegion = _modules.DataRegion });
}
