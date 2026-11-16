using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Libzora.Core;
using Libzora.Infrastructure;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Libzora;

public partial class AccountCenterView : UserControl, IDisposable
{
    private readonly AccountService? _accounts;
    private readonly DeviceMembershipService? _membership;
    private readonly bool _registrationAvailable;
    private readonly CancellationTokenSource _lifetime = new();
    private AccountStatus? _status;
    private DeviceFeatureStatus? _memberStatus;
    private bool _busy;
    private bool _presenting;
    private bool _resetMode;
    private bool _emailCodeSent;
    private Task? _refreshTask;

    internal event EventHandler<AccountStateChangedEventArgs>? AccountStateChanged;
    // Authentication notification only; never requests provider selection or sync.
    internal event EventHandler<AccountStateChangedEventArgs>? AccountSignedIn;

    /// <summary>Design-time constructor; the form stays disabled without an account service.</summary>
    public AccountCenterView()
    {
        InitializeComponent();
        _registrationAvailable = false;
        PresentPlanBenefits();
        MembershipAvatarIcon.Data = AccountVisuals.User;
        MembershipStatus.IsVisible = false;
    }

    public AccountCenterView(AccountService accounts, DeviceMembershipService? membership = null,
        bool registrationAvailable = false) : this()
    {
        _registrationAvailable = registrationAvailable;
        _accounts = accounts;
        _membership = membership;
        accounts.DeviceInvalidated += OnDeviceInvalidated;
        UiText.LanguageChanged += OnLanguageChanged;
        AttachedToVisualTree += async (_, _) => await RefreshAsync();
        DetachedFromVisualTree += (_, _) => Deactivate();
    }

    internal Task RefreshAsync()
    {
        if (_lifetime.IsCancellationRequested || _accounts is null) return Task.CompletedTask;
        if (_busy) return _refreshTask is { IsCompleted: false } refreshing ? refreshing : Task.CompletedTask;
        return _refreshTask = LoadAsync();
    }

    internal void Deactivate()
    {
        ClearPasswords();
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_accounts is not null) _accounts.DeviceInvalidated -= OnDeviceInvalidated;
        UiText.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        Deactivate();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime.IsCancellationRequested) return;
        PresentPlanBenefits();
        if (_status is { } status) Present(status);
        if (_memberStatus is { } member) PresentMembership(member);
        PresentLoginMode();
        PresentFeaturePlans();
    });

    private void OnDeviceInvalidated(object? sender, AccountDeviceInvalidatedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetime.IsCancellationRequested) return;
            try { Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); StatusText.Text = UiText.Localize(e.Message); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });

    private bool Register => ModeBox.SelectedIndex == 1;

    private async Task LoadAsync()
    {
        if (_accounts is null) return;
        SetBusy(true);
        try { Present(await _accounts.GetStatusAsync(true, _lifetime.Token)); await LoadMembershipAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token)); }
            catch (Exception fallback) when (fallback is not OutOfMemoryException) { }
            StatusText.Text = UiText.Localize(exception.Message);
        }
        finally { SetBusy(false); }
    }

    private void Present(AccountStatus status)
    {
        if (_status?.User?.Id != status.User?.Id || _status?.Region != status.Region)
        {
            _memberStatus = null;
            TrialClaimButton.IsEnabled = false;
            TrialStatusText.IsVisible = false;
            ExpiryNoticeBorder.IsVisible = false;
            DeviceListPanel.Children.Clear();
            DeviceSlotsPanel.Children.Clear();
            DeviceCountText.Text = "—";
            DeviceCapacityText.Text = "";
            SetMembershipMessage("");
            ClearPasswords();
            ProfileExpander.IsExpanded = false;
            PasswordExpander.IsExpanded = false;
            AccountScrollViewer.ScrollToHome();
            PresentFeaturePlans();
        }
        _status = status;
        if (status.IsLoggedIn) _resetMode = false;
        if (status.User is null || status.User.EmailVerified) _emailCodeSent = false;
        _presenting = true;
        try { RegionBox.SelectedIndex = (int)status.Region; }
        finally { _presenting = false; }
        RestartText.IsVisible = status.RequiresDataRestart;
        AccountCard.IsVisible = status.IsLoggedIn;
        AccountPanel.IsVisible = status.IsLoggedIn;
        AccountSettingsCard.IsVisible = status.IsLoggedIn;
        SignInPanel.IsVisible = !status.IsLoggedIn;
        ApplyResetMode();
        SignOutButton.IsVisible = status.IsLoggedIn;
        MembershipPanel.IsVisible = status.IsLoggedIn && _membership is not null;
        UsernameText.Text = status.User?.Username;
        AccountRegionText.Text = UiText.Get(AccountRegions.DisplayName(status.Region));
        ProfileUsernameBox.Text = status.User?.Username ?? "";
        ProfileEmailBox.Text = status.User?.Email ?? "";
        if (_memberStatus is null)
        {
            SetMembershipAppearance("free");
            TierBadgeText.Text = UiText.Get("已登录");
            MembershipExpiryText.Text = "";
            MembershipSummary.Text = UiText.Get(_membership is null ? "账户已登录" : "正在读取会员信息…");
            MembershipHintText.Text = "";
        }
        if (status.IsLoggedIn) ClearPasswords();
        PresentEmailVerification();
        PublishAccountState();
    }

    private void ApplyResetMode()
    {
        ResetPanel.IsVisible = _resetMode;
        SubmitButton.IsVisible = !_resetMode && _status?.IsLoggedIn != true;
        ForgotPasswordButton.IsVisible = !_resetMode;
        LoginPasswordPanel.IsVisible = !_resetMode;
        ConfirmPasswordPanel.IsVisible = !_resetMode && Register;
        RegistrationEmailPanel.IsVisible = !_resetMode && Register;
        ModeBox.IsEnabled = !_resetMode;
    }

    private void PresentEmailVerification()
    {
        var user = _status?.User;
        var available = _status?.IsLoggedIn == true && user?.Email is not null;
        var verified = user?.EmailVerified == true;
        EmailVerifiedText.Text = available
            ? UiText.Get(verified ? "注册邮箱已验证。" : "注册邮箱尚未验证。验证后可找回密码。")
            : UiText.Get("此账号未设置注册邮箱，无法验证或找回密码。");
        SendEmailCodeButton.IsVisible = available && !verified && !_emailCodeSent;
        EmailCodePanel.IsVisible = available && !verified && _emailCodeSent;
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        RegionPanel.IsEnabled = !value;
        SignInPanel.IsEnabled = !value;
        SubmitButton.IsEnabled = !value && !(Register && !_registrationAvailable);
        SignOutButton.IsEnabled = !value;
        MembershipPanel.IsEnabled = !value;
        AccountSettingsCard.IsEnabled = !value;
        MembershipRefreshButton.IsEnabled = !value && _accounts is not null;
        LoadingIndicator.IsVisible = value;
        TrialClaimButton.IsEnabled = false;
        if (_memberStatus is { } status) PresentTrial(status.Membership);
        PresentFeaturePlans();
    }

    private async void OnRegionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_presenting || _busy || RegionPanel is null || _accounts is null || RegionBox.SelectedIndex < 0) return;
        SetBusy(true);
        _resetMode = false;
        _emailCodeSent = false;
        ClearPasswords();
        UsernameBox.Text = "";
        RegistrationEmailBox.Text = "";
        StatusText.Text = "";
        try
        {
            Present(await _accounts.SelectRegionAsync((AccountRegion)RegionBox.SelectedIndex, _lifetime.Token));
            Present(await _accounts.GetStatusAsync(true, _lifetime.Token));
            await LoadMembershipAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConfirmPasswordPanel is null) return;
        ConfirmPasswordPanel.IsVisible = Register;
        PresentLoginMode();
        ApplyResetMode();
        ClearPasswords();
        StatusText.Text = "";
    }

    private void PresentLoginMode()
    {
        SignInHeading.IsVisible = !Register;
        SignInSubtitle.IsVisible = !Register;
        RegisterHeading.IsVisible = Register;
        RegisterSubtitle.IsVisible = Register && _registrationAvailable;
        RegistrationNoticeBorder.IsVisible = Register && !_registrationAvailable;
        RegistrationNoticeText.Text = UiText.Get("注册暂未上线。当前版本无需注册即可免费使用全部功能。");
        RegisterSubtitle.Text = UiText.Get("创建账号后可主动领取一次 7 天试用，体验自备存储多端同步。");
        RegistrationEmailPanel.IsVisible = Register;
        LoginIdentifierLabel.Text = UiText.Get(Register ? "用户名" : "用户名或邮箱");
        UsernameHintText.Text = UiText.Get(Register ? "3–32 位字母、数字、点、下划线或连字符" : "使用用户名或邮箱登录同一个账号");
        UsernameBox.MaxLength = Register ? 32 : 254;
        SubmitButton.Content = UiText.Get(Register
            ? (_registrationAvailable ? "注册并登录" : "注册暂未上线")
            : "登录");
        SubmitButton.IsEnabled = !_busy && !(Register && !_registrationAvailable);
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if (Register && !_registrationAvailable)
        {
            StatusText.Text = UiText.Get("注册暂未上线。当前版本无需注册即可免费使用全部功能。");
            return;
        }
        if (Register && PasswordBox.Text != ConfirmPasswordBox.Text)
        { StatusText.Text = UiText.Get("两次输入的密码不一致。"); return; }
        SetBusy(true);
        StatusText.Text = UiText.Get(Register ? "正在注册…" : "正在登录…");
        try
        {
            Present(await _accounts.SignInAsync(_status?.ServerUrl ?? "", UsernameBox.Text ?? "", PasswordBox.Text ?? "", Register, _lifetime.Token,
                Register ? RegistrationEmailBox.Text : null, Register ? ConfirmPasswordBox.Text : null));
            StatusText.Text = UiText.Get("登录成功。");
            await LoadMembershipAsync();
            if (_status?.IsLoggedIn == true && _memberStatus is { } member)
                AccountSignedIn?.Invoke(this, new(_status, member.Membership));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnForgotPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null || Register) return;
        var identifier = (UsernameBox.Text ?? "").Trim();
        if (!AccountValidation.IsLogin(identifier))
        { StatusText.Text = UiText.Get("请输入有效的用户名或邮箱。"); return; }
        SetBusy(true);
        StatusText.Text = UiText.Get("正在发送验证码…");
        try
        {
            await _accounts.RequestPasswordResetAsync(_status?.ServerUrl ?? "", identifier, _lifetime.Token);
            _resetMode = true;
            ApplyResetMode();
            ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
            StatusText.Text = UiText.Get("若该账号已绑定邮箱，验证码已发送（15 分钟内有效）。请查收邮件并设置新密码。");
            ResetCodeBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnResetSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if ((ResetPasswordBox.Text ?? "") != (ResetConfirmPasswordBox.Text ?? ""))
        { StatusText.Text = UiText.Get("两次输入的密码不一致。"); return; }
        SetBusy(true);
        StatusText.Text = UiText.Get("正在重置密码…");
        try
        {
            var user = await _accounts.ResetPasswordAsync(_status?.ServerUrl ?? "", UsernameBox.Text ?? "",
                ResetCodeBox.Text ?? "", ResetPasswordBox.Text ?? "", ResetConfirmPasswordBox.Text ?? "", _lifetime.Token);
            UsernameBox.Text = user.Username;
            _resetMode = false;
            ApplyResetMode();
            ClearPasswords();
            StatusText.Text = UiText.Get("密码已重置，请使用新密码登录。");
            PasswordBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private void OnResetCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _resetMode = false;
        ApplyResetMode();
        ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
        StatusText.Text = UiText.Get("已返回登录。");
    }

    private async void OnSendEmailCodeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        StatusText.Text = UiText.Get("正在发送邮箱验证码…");
        try
        {
            await _accounts.RequestEmailVerificationAsync(_lifetime.Token);
            _emailCodeSent = true;
            EmailCodeBox.Text = "";
            PresentEmailVerification();
            EmailCodeBox.Focus();
            StatusText.Text = UiText.Get("验证码已发送至注册邮箱（15 分钟内有效）。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnConfirmEmailCodeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        StatusText.Text = UiText.Get("正在验证邮箱…");
        try
        {
            Present(await _accounts.ConfirmEmailVerificationAsync(EmailCodeBox.Text ?? "", _lifetime.Token));
            EmailCodeBox.Text = "";
            StatusText.Text = UiText.Get("邮箱验证成功。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        try
        {
            var revoked = await _accounts.SignOutAsync(_lifetime.Token);
            Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token));
            StatusText.Text = UiText.Get(revoked ? "已退出登录。" : "已在本机退出；服务器暂时不可用，远端会话将在到期后失效。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = UiText.Localize(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ClearPasswords()
    {
        PasswordBox.Text = ""; ConfirmPasswordBox.Text = ""; ProfilePasswordBox.Text = "";
        CurrentPasswordBox.Text = ""; NewPasswordBox.Text = ""; ConfirmNewPasswordBox.Text = "";
        ResetCodeBox.Text = ""; ResetPasswordBox.Text = ""; ResetConfirmPasswordBox.Text = "";
    }

    private async void OnProfileSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        SetBusy(true);
        try
        {
            Present(await _accounts.UpdateProfileAsync(ProfileUsernameBox.Text ?? "", ProfilePasswordBox.Text ?? "", _lifetime.Token));
            StatusText.Text = UiText.Get("账号资料已更新。");
            ProfileExpander.IsExpanded = false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { StatusText.Text = UiText.Localize(error.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnPasswordSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null) return;
        if (NewPasswordBox.Text != ConfirmNewPasswordBox.Text) { StatusText.Text = UiText.Get("两次输入的密码不一致。"); return; }
        SetBusy(true);
        try
        {
            Present(await _accounts.ChangePasswordAsync(CurrentPasswordBox.Text ?? "", NewPasswordBox.Text ?? "", _lifetime.Token));
            await LoadMembershipAsync();
            PasswordExpander.IsExpanded = false;
            StatusText.Text = UiText.Get("密码已修改，其他设备已退出登录。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { StatusText.Text = UiText.Localize(error.Message); }
        finally { ClearPasswords(); SetBusy(false); }
    }

    private async void OnCloseAccountClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accounts is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new AccountClosureWindow();
        if (!await dialog.ShowDialog<bool>(owner)) return;
        SetBusy(true);
        try
        {
            await _accounts.CloseAccountAsync(dialog.CurrentPassword, "停用账号", _lifetime.Token);
            Present(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token));
            StatusText.Text = UiText.Get("账号已停用，登录会话已撤销；必要的许可与审计记录、本地书库和自备存储数据保留。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { StatusText.Text = UiText.Localize(error.Message); }
        finally { dialog.ClearPassword(); ClearPasswords(); SetBusy(false); }
    }

    private async Task LoadMembershipAsync()
    {
        if (_membership is null || !MembershipPanel.IsVisible) return;
        try
        {
            var status = await _membership.RefreshAsync(_lifetime.Token);
            if (_status?.User?.Id != status.Membership.AccountId || _status.Region != status.Membership.Region) return;
            _memberStatus = status;
            PresentMembership(status);
            PublishAccountState();
            SetMembershipMessage("");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (_memberStatus is { } known && _status?.IsLoggedIn == true)
            {
                PresentMembership(known);
                PublishAccountState();
            }
            SetMembershipMessage(UiText.Localize(error.Message));
            return;
        }
    }

    private void PresentMembership(DeviceFeatureStatus status)
    {
        var member = status.Membership;
        var tier = AccountVisuals.Tier(member);
        var now = DateTimeOffset.UtcNow;
        var expired = tier == "free" && member.FeatureExpiresAt is { } expiry && expiry <= now;
        var expiring = tier == "trial" && member.FeatureExpiresAt is { } reminderAt
            && MemberEntitlements.TrialRenewalReminder(reminderAt, now) is not null;
        SetMembershipAppearance(tier);
        TierBadgeText.Text = UiText.Get(MemberEntitlements.TierName(tier));
        MembershipSummary.Text = UiText.Get(tier switch
        {
            "trial" => "试用中 · 自备存储同步已解锁",
            "lifetime" => "永久买断 · 自备存储同步已解锁",
            _ => "Free · 畅享本地阅读"
        });
        MembershipHintText.Text = UiText.Get(tier is "trial" or "lifetime"
            ? "使用自己的 S3 / WebDAV，在多端同步书籍、阅读进度、书签和批注。"
            : "本地阅读、USB Kindle 同步、插件与自配 AI 接口永久免费。Free 不含多端同步。");
        MembershipExpiryText.Text = tier == "trial" && member.FeatureExpiresAt is { } until
            ? UiText.Get("试用至 {0:yyyy/MM/dd}", until.LocalDateTime)
            : UiText.Get(tier == "lifetime" ? "永久有效" : expired ? "试用已到期" : "永久免费");
        ExpiryNoticeBorder.IsVisible = expired || expiring;
        ExpiryNoticeText.Text = expired
            ? MemberEntitlements.TrialExpiryMessage(member.FeatureExpiresAt!.Value, now)
            : expiring ? MemberEntitlements.TrialRenewalMessage(member.FeatureExpiresAt!.Value, now) : "";
        PresentDevices(status);
        PresentTrial(member);
        PresentFeaturePlans();
    }

    private void PresentTrial(AccountMembership member)
    {
        var tier = AccountVisuals.Tier(member);
        var active = tier == "trial";
        TrialClaimButton.IsEnabled = !_busy && member.TrialAvailable && !member.TrialClaimed && tier == "free";
        TrialClaimButton.Content = UiText.Get(active ? "试用中" : tier == "lifetime" ? "已永久买断"
            : member.TrialClaimed ? "已领取过试用" : member.TrialAvailable ? "领取 7 天试用" : "暂不可领取");
        TrialStatusText.IsVisible = active || member.TrialClaimed;
        TrialStatusText.Text = active && member.FeatureExpiresAt is { } until
            ? UiText.Get("试用期至 {0:yyyy/MM/dd}，可购买永久买断继续使用。", until.LocalDateTime)
            : member.TrialClaimed ? UiText.Get("每个账号仅可主动领取一次。") : "";
    }

    private void SetMembershipAppearance(string tier)
    {
        foreach (var control in new Control[] { AvatarBorder, MembershipAvatarIcon, TierBadgeBorder, TierBadgeText })
            AccountVisuals.SetTier(control, tier);
        MembershipAvatarIcon.Data = AccountVisuals.MembershipIcon(tier);
    }

    private void PresentDevices(DeviceFeatureStatus status)
    {
        var member = status.Membership;
        DeviceCountText.Text = UiText.Get("{0} / {1} 台", member.LoggedInDeviceCount, member.DeviceLimit);
        var remaining = Math.Max(0, member.DeviceLimit - member.LoggedInDeviceCount);
        DeviceCapacityText.Text = UiText.Get(remaining > 0 ? "还可登录 {0} 台设备" : "设备名额已满，可退出旧设备后登录新设备。", remaining);
        DeviceSlotsPanel.Children.Clear();
        for (var index = 0; index < member.DeviceLimit; index++)
        {
            var slot = new Border { Width = 16, Height = 4, CornerRadius = new CornerRadius(2) };
            slot.Bind(Border.BackgroundProperty, this.GetResourceObservable(index < member.LoggedInDeviceCount ? "SuccessBrush" : "SoftHoverBrush"));
            DeviceSlotsPanel.Children.Add(slot);
        }
        DeviceListPanel.Children.Clear();
        foreach (var device in status.Devices.Where(item => item.State == "active")
                     .OrderByDescending(item => item.DeviceId == status.CurrentDeviceId).ThenByDescending(item => item.LastSeenAt))
        {
            var current = device.DeviceId == status.CurrentDeviceId;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,Auto,Auto"), ColumnSpacing = 12 };
            var icon = new ShapePath { Data = AccountVisuals.DeviceIcon(device.Platform), Width = 18, Height = 18, Classes = { "accountDeviceIcon" } };
            icon.Classes.Set("current", current);
            var avatar = new Border { Width = 36, Height = 36, Child = icon, Classes = { "accountDeviceAvatar" } };
            avatar.Classes.Set("current", current);
            row.Children.Add(avatar);
            var description = new StackPanel { Spacing = 3, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var name = new TextBlock { Text = device.Name, FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, device.Name);
            description.Children.Add(name);
            if (current)
            {
                var badge = new TextBlock { Text = UiText.Get("当前设备"), FontSize = 10 };
                badge.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SuccessBrush"));
                var badgeBorder = new Border
                {
                    Child = badge, Padding = new Thickness(6, 2),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                badgeBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccountSuccessTintBrush"));
                Grid.SetColumn(badgeBorder, 2);
                row.Children.Add(badgeBorder);
            }
            var activity = new TextBlock { Text = UiText.Get("{0} · 最近活跃 {1}", device.Platform, LastActivity(device.LastSeenAt)), Classes = { "accountMuted" }, FontSize = 11 };
            ToolTip.SetTip(activity, device.LastSeenAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss", UiText.CurrentCulture));
            description.Children.Add(activity);
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            var button = new Button
            {
                Content = UiText.Get(current ? "退出此设备" : "退出设备"), Tag = device.DeviceId,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "accountTextAction" }
            };
            Avalonia.Automation.AutomationProperties.SetName(button, UiText.Get("退出设备 {0}", device.Name));
            button.Click += OnKickDeviceClick;
            Grid.SetColumn(button, 3);
            row.Children.Add(button);
            DeviceListPanel.Children.Add(new Border { Child = row, Classes = { "flatRow", "accountDeviceRow" } });
        }
        DeviceEmptyText.IsVisible = DeviceListPanel.Children.Count == 0;
    }

    private void PresentPlanBenefits()
    {
        AccountPageSubtitle.Text = UiText.Get(_registrationAvailable
            ? "管理账号、设备与自备存储同步权益"
            : "账号注册暂未上线，当前版本全部功能免费使用。");
        SignInSubtitle.Text = UiText.Get("登录后管理账号与最多 6 台设备，按需领取试用或购买永久买断。");
        RegisterSubtitle.Text = UiText.Get("创建账号后可主动领取一次 7 天试用，体验自备存储多端同步。");
        PlansSubtitleText.Text = UiText.Get("Free 免费本地阅读；7 天试用与永久买断享有相同的自备存储同步功能。");
        FreeTaglineText.Text = UiText.Get("永久免费本地阅读");
        FreeFooterText.Text = UiText.Get("本地书库与自配 AI 接口免费使用，不含多端同步。");
        TrialTitleText.Text = UiText.Get("7 天试用");
        TrialTaglineText.Text = UiText.Get("完整体验买断功能");
        TrialPeriodText.Text = UiText.Get("每账号主动领取一次，连续 7 天");
        TrialFooterText.Text = UiText.Get("到期不删除本地或自备存储数据，购买买断后可继续同步。");
        LifetimeNameText.Text = UiText.Get("永久买断");
        LifetimeTaglineText.Text = UiText.Get("一次购买，永久使用");
        LifetimePeriodText.Text = UiText.Get("一次付费，永久有效");
        LifetimeFooterText.Text = UiText.Get("存储服务由你自行配置和管理，试用与买断同步功能相同。");
        Benefits(FreeBenefitsPanel, ["本地书库管理与阅读", "USB Kindle 书籍与批注双向同步", "插件功能", "自配 AI 接口", "加密备份"]);
        string[] syncBenefits = ["包含全部 Free 权益", "自备 S3 / WebDAV 多端同步", "多端进度、书签、批注和书籍同步", "最多 6 台设备登录"];
        Benefits(TrialBenefitsPanel, syncBenefits);
        Benefits(LifetimeBenefitsPanel, syncBenefits);
    }

    private void Benefits(StackPanel panel, string[] benefits)
    {
        panel.Children.Clear();
        foreach (var benefit in benefits)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,8,*") };
            var check = new ShapePath { Classes = { "accountBenefitCheck" } };
            check.Bind(ShapePath.DataProperty, this.GetResourceObservable("AccountCheckGeometry"));
            row.Children.Add(check);
            var text = new TextBlock { Text = UiText.Get(benefit), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
    }

    private void PresentFeaturePlans()
    {
        var tier = _memberStatus is null ? "" : AccountVisuals.Tier(_memberStatus.Membership);
        var hasLifetime = tier == "lifetime";
        FreePlanCard.Classes.Set("current", tier == "free");
        TrialCard.Classes.Set("current", tier == "trial");
        LifetimePlanCard.Classes.Set("current", hasLifetime);
        FreePlanButton.Content = UiText.Get(tier == "free" ? "当前方案" : "永久免费");
        LifetimePriceText.Text = "¥88";
        LifetimeRightsButton.IsEnabled = !_busy && !hasLifetime
            && _membership is not null && _status?.IsLoggedIn == true;
        LifetimeRightsButton.Content = UiText.Get(hasLifetime ? "已永久买断" : "刷新权益");
        PlanEntitlementHintText.Text = UiText.Get("买断权益直接绑定账号，开通后刷新即可使用。服务端仅验证功能权益，不处理支付或订单。");
        PlansRegionText.Text = UiText.Get(AccountRegions.DisplayName(_status?.Region ?? AccountRegion.China));
        if (_memberStatus is { } status) PresentTrial(status.Membership);
    }

    private void PublishAccountState()
    {
        if (_status is { } status) AccountStateChanged?.Invoke(this, new(status, _memberStatus?.Membership));
    }

    private void SetMembershipMessage(string message)
    {
        MembershipStatus.Text = message;
        MembershipStatus.IsVisible = !string.IsNullOrWhiteSpace(message);
        if (MembershipStatus.IsVisible && _status?.IsLoggedIn == true) StatusText.Text = "";
    }

    private static string LastActivity(DateTimeOffset seen)
    {
        var elapsed = DateTimeOffset.UtcNow - seen;
        return elapsed.TotalMinutes < 1 ? UiText.Get("刚刚") : elapsed.TotalHours < 1 ? UiText.Get("{0} 分钟前", (int)elapsed.TotalMinutes)
            : elapsed.TotalDays < 1 ? UiText.Get("{0} 小时前", (int)elapsed.TotalHours) : elapsed.TotalDays < 7
                ? UiText.Get("{0} 天前", (int)elapsed.TotalDays) : seen.LocalDateTime.ToString("MM/dd HH:mm", UiText.CurrentCulture);
    }

    private async void OnMembershipRefreshClick(object? sender, RoutedEventArgs e)
    { if (_busy) return; await RefreshAsync(); }
    private async void OnKickDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || sender is not Button { Tag: Guid device }) return;
        SetBusy(true);
        try { await _membership.KickAsync(device, _lifetime.Token); Present(await _accounts!.GetStatusAsync(cancellationToken: _lifetime.Token)); await LoadMembershipAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetMembershipMessage(UiText.Localize(error.Message)); }
        finally { SetBusy(false); }
    }
    private async void OnTrialClaimClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _membership is null || _status?.IsLoggedIn != true
            || _memberStatus is not { Membership: { TrialAvailable: true, TrialClaimed: false } member }
            || AccountVisuals.Tier(member) != "free") return;
        SetBusy(true);
        SetMembershipMessage(UiText.Get("正在领取 7 天免费试用…"));
        try
        {
            var claimed = await _membership.ClaimTrialAsync(_lifetime.Token);
            if (_status?.User?.Id != claimed.AccountId || _status.Region != claimed.Region) return;
            if (_memberStatus is { } known)
            {
                _memberStatus = known with { Membership = claimed };
                PresentMembership(_memberStatus);
                PublishAccountState();
            }
            await LoadMembershipAsync();
            SetMembershipMessage(UiText.Get("7 天试用已开通，可使用自备 S3 / WebDAV 同步书籍、阅读进度、书签和批注。"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { SetMembershipMessage(UiText.Localize(error.Message)); }
        finally { SetBusy(false); }
    }
}

internal sealed record AccountStateChangedEventArgs(AccountStatus Account, AccountMembership? Membership);
