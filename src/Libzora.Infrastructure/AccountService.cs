using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>Real server authentication, with a protected, device-local session.</summary>
public sealed class AccountService : IDisposable
{
    public static string DefaultServerUrl => AccountRegions.DefaultServerUrl(AccountRegion.China);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AccountSessionStore _store;
    private readonly DeviceIdentityStore _identity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly AccountRegion? _dataRegion;
    private readonly Func<AccountRegion, string> _defaultServerUrl;
    private bool _loaded;
    private AccountRegion _region;
    private Dictionary<AccountRegion, RegionalAccountConnection> _connections = [];
    private string _serverUrl = "";
    private AccountSessionResponse? _session;

    public AccountService(AppPaths paths, ISecretProtector protector, HttpClient? http = null, TimeProvider? clock = null,
        AccountRegion? dataRegion = null, Func<AccountRegion, string>? defaultServerUrl = null)
    {
        _store = new(paths, protector);
        _identity = new(paths, protector);
        _ownsHttp = http is null;
        // A redirect must never forward a password or a bearer token to another host.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? TimeProvider.System;
        _dataRegion = dataRegion;
        _defaultServerUrl = defaultServerUrl ?? AccountRegions.DefaultServerUrl;
    }

    public event EventHandler<AccountDeviceInvalidatedEventArgs>? DeviceInvalidated;
    public Task<DeviceEnrollmentRequest> GetDeviceAsync(CancellationToken token = default) => _identity.GetAsync(token);

    internal async Task<(AccountStatus Scope, T Value)> MemberRequestAsync<T>(HttpMethod method, string action,
        JsonTypeInfo<T> metadata, JsonContent? content, CancellationToken token, string? signInMessage = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (_session is null) throw new DeviceFeatureRequiredException(signInMessage ?? "请先登录当前区域的会员账号后使用多端同步。");
            var scope = Status();
            using var request = Request(method, _serverUrl, action, _session.Token);
            request.Content = content;
            using var response = await SendAsync(request, token);
            await EnsureMemberSuccessAsync(response, token);
            return (scope, await ReadAsync(response, metadata, token, maximumBytes: 64 * 1024));
        }
        finally { _gate.Release(); }
    }

    internal async Task MemberActionAsync(string action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (_session is null) throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, action, _session.Token);
            using var response = await SendAsync(request, token);
            await EnsureMemberSuccessAsync(response, token);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureMemberSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.Headers.TryGetValues("X-Libzora-Error", out var values) ? values.FirstOrDefault() : null;
        if (response.StatusCode == HttpStatusCode.Unauthorized || code is "account_banned" or "device_mismatch")
        {
            await InvalidateAsync(token);
            throw new DeviceFeatureRequiredException("此设备已退出登录或账号已失效，请重新登录。");
        }
        var message = ApiMessage(code);
        if (code is "membership_required" or "plus_required") throw new DeviceFeatureRequiredException(message!);
        throw new InvalidOperationException(message ?? "账号服务暂时不可用，请稍后重试。");
    }

    private async Task InvalidateAsync(CancellationToken token)
    {
        var hadSession = _session is not null;
        await SetAsync(_serverUrl, null, token);
        if (hadSession) DeviceInvalidated?.Invoke(this, new("此设备已退出登录或会话已过期，请重新登录。"));
    }

    private static string? ApiMessage(string? code) => code switch
    {
        "device_limit_reached" => UiText.Get("此账号已登录 {0} 台设备，请在已登录设备的账号页踢出旧设备，再登录。", MemberEntitlements.DeviceLimit),
        "device_blocked" => "此设备已被管理员停用，请联系管理员。",
        "device_mismatch" => "本机设备身份已改变，请重新登录。",
        "invalid_device" => "设备信息无效，请检查系统密钥库或更新客户端。",
        "membership_required" => MemberEntitlements.SyncRequirement,
        "official_cloud_removed" or "plus_required" or "pro_required" or "traffic_quota_exceeded"
            or "new_user_traffic_exceeded" or "metadata_traffic_exceeded" or "storage_quota_exceeded"
            or "download_link_used" or "transfer_expired" or "download_rate_limited" or "cloud_retention_due"
            => MemberEntitlements.AccountCloudRequirement,
        "invalid_membership" => "账号服务返回了无效的响应。",
        "rate_limited" => "请求过于频繁，请稍后再试。",
        "registration_limit_reached" => "此网络的注册次数已达上限，请稍后再试或联系管理员。",
        "registration_blocked" => "此网络或账号信息暂不支持注册，请联系管理员。",
        "username_blocked" => "此用户名暂不可用，请选择其他用户名。",
        "trial_unavailable" => "此账号已领取过试用、已有买断订阅或历史功能权益，不能再次领取。",
        "region_mismatch" => "账号服务区域不匹配，请检查所选区域。",
        "username_exists" => "该用户名已注册，请换一个用户名。",
        "email_exists" => "该邮箱已被其他账号使用。",
        "email_required" => "注册时必须填写邮箱。",
        "email_change_not_allowed" => "注册邮箱不可修改。",
        "invalid_email" => "请输入有效的邮箱地址。",
        "invalid_username" => "用户名需为 3–32 位字母、数字、点、下划线或连字符。",
        "current_password_required" => "请输入当前密码。",
        "current_password_incorrect" => "当前密码不正确。",
        "invalid_password" => "密码需为 8–128 位，且不能全部为空格。",
        "confirmation_password_required" => "请输入确认登录密码。",
        "passwords_do_not_match" => "两次输入的密码不一致。",
        "password_unchanged" => "新密码不能与当前密码相同。",
        "invalid_reset_code" => "验证码无效或已过期，请重新获取。",
        "invalid_email_code" => "邮箱验证码无效或已过期，请重新获取。",
        "email_unavailable" => "邮件服务暂不可用，请稍后再试。",
        "email_not_set" => "此账号未设置注册邮箱，无法验证或找回密码。",
        "confirmation_required" => "请输入“停用账号”确认文字。",
        "apple_token_invalid" => "Apple 身份验证无效或已过期，请重试。",
        "apple_already_bound" => "此 Apple 账号已绑定其他 Libzora 账号。",
        "apple_not_bound" => "当前账号尚未绑定 Apple ID。",
        "apple_unbind_requires_password" => "请先设置登录密码后再解绑 Apple ID，以免无法登录。",
        "apple_login_disabled" => "当前账号服务暂未开启 Apple 登录。",
        _ => null
    };

    public async Task<AccountStatus> SelectRegionAsync(AccountRegion region, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(region)) throw new ArgumentOutOfRangeException(nameof(region));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var connection = ResolveConnection(region, _connections.GetValueOrDefault(region));
            var updated = new Dictionary<AccountRegion, RegionalAccountConnection>(_connections) { [region] = connection };
            await _store.SaveAsync(region, updated, cancellationToken);
            _connections = updated; _region = region;
            _serverUrl = connection.ServerUrl; _session = connection.Session;
            await LoadAsync(cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Legacy callers cannot create an official-cloud connection.</summary>
    public Task<S3SyncSettings> CreateSyncSettingsAsync(S3SyncSettings current, CancellationToken cancellationToken = default) =>
        Task.FromException<S3SyncSettings>(new InvalidOperationException(MemberEntitlements.AccountCloudRequirement));

    public async Task EnsureSyncRegionAsync(AccountRegion dataRegion, CancellationToken token = default)
    {
        var status = await GetStatusAsync(cancellationToken: token);
        if (status.Region != dataRegion || status.RequiresDataRestart)
            throw new InvalidOperationException("区域已切换，请重新打开应用后使用该区域的独立书库和同步。");
    }

    /// <summary>Fail-closed source compatibility: no feedback content is read or sent.</summary>
    public Task<FeedbackReceipt> SubmitFeedbackAsync(FeedbackDraft draft, IReadOnlyList<FeedbackImage> images,
        CancellationToken cancellationToken = default) =>
        SubmitFeedbackAsync(draft, images, softwareLog: null, cancellationToken);

    public Task<FeedbackReceipt> SubmitFeedbackAsync(FeedbackDraft draft, IReadOnlyList<FeedbackImage> images,
        string? softwareLog, CancellationToken cancellationToken = default) =>
        Task.FromException<FeedbackReceipt>(FeedbackRemoved());

    /// <summary>The retired account-server feedback list is never requested.</summary>
    public Task<FeedbackItem[]> GetFeedbackAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<FeedbackItem[]>([]);

    public Task<FeedbackItem> GetFeedbackAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromException<FeedbackItem>(FeedbackRemoved());

    private static InvalidOperationException FeedbackRemoved() =>
        new(UiText.Get("账号服务不接收反馈、截图或日志，请使用 GitHub Issues。"));

    public async Task<AccountStatus> GetStatusAsync(bool validateSession = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            if (validateSession && _session is { } session)
            {
                using var request = Request(HttpMethod.Get, _serverUrl, "session", session.Token);
                using var response = await SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    await InvalidateAsync(cancellationToken);
                else
                {
                    EnsureSuccess(response, false);
                    var info = await ReadAsync(response, AccountJsonContext.Default.AccountSessionInfo, cancellationToken);
                    if (info.Region != _region || info.User is null || info.User.Id != session.User.Id || !AccountValidation.IsUsername(info.User.Username)
                        || info.ExpiresAt <= _clock.GetUtcNow())
                        throw new InvalidOperationException("账号服务返回了无效的会话。");
                    await SetAsync(_serverUrl, new AccountSessionResponse
                        { User = info.User, Token = session.Token, ExpiresAt = info.ExpiresAt, Region = _region }, cancellationToken);
                }
            }
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> SignInAsync(string serverUrl, string username, string password,
        bool register = false, CancellationToken cancellationToken = default, string? email = null, string? confirmPassword = null)
    {
        var endpoint = AccountValidation.ServerUri(serverUrl).AbsoluteUri.TrimEnd('/');
        username = username.Trim();
        if (!(register ? AccountValidation.IsUsername(username) : AccountValidation.IsLogin(username)))
            throw new ArgumentException(register ? "用户名需为 3–32 位字母、数字、点、下划线或连字符。" : "请输入有效的用户名或邮箱。");
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (register && email is null) throw new ArgumentException("注册时必须填写邮箱。");
        if (email is not null && !AccountValidation.IsEmail(email)) throw new ArgumentException("请输入有效的邮箱地址。");
        if (!AccountValidation.IsPassword(password))
            throw new ArgumentException("密码需为 8–128 位，且不能全部为空格。");
        if (register && !AccountValidation.IsPassword(confirmPassword)) throw new ArgumentException("请输入确认登录密码。");
        if (register && password != confirmPassword) throw new ArgumentException("两次输入的密码不一致。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var device = await _identity.GetAsync(cancellationToken);
            using var request = Request(HttpMethod.Post, endpoint, register ? "register" : "login");
            request.Content = JsonContent.Create(new AccountCredentials { Username = username, Email = email, Password = password,
                ConfirmPassword = register ? confirmPassword : null, Region = _region,
                DeviceId = device.DeviceId, DeviceName = device.DeviceName, Platform = device.Platform },
                AccountJsonContext.Default.AccountCredentials);
            using var response = await SendAsync(request, cancellationToken);
            EnsureSuccess(response, register);
            var session = await ReadAsync(response, AccountJsonContext.Default.AccountSessionResponse, cancellationToken);
            if (session.Region != _region) throw new InvalidOperationException("账号服务区域不匹配，请检查所选区域和服务地址。");
            if (session.User is null || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)
                || register && session.User.Email != email
                || session.Token is not { Length: 43 }
                || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || session.ExpiresAt <= _clock.GetUtcNow())
                throw new InvalidOperationException("账号服务返回了无效的会话。");
            await SetAsync(endpoint, session, cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> SignInWithAppleAsync(
        string serverUrl,
        AppleSignInCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var endpoint = AccountValidation.ServerUri(serverUrl).AbsoluteUri.TrimEnd('/');
        var identityToken = credential.IdentityToken?.Trim() ?? "";
        if (string.IsNullOrEmpty(identityToken))
            throw new ArgumentException("Apple 身份验证无效，请重试。", nameof(credential));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var device = await _identity.GetAsync(cancellationToken);
            using var request = Request(HttpMethod.Post, endpoint, "apple/login");
            request.Content = JsonContent.Create(new AppleSignInRequest
            {
                Region = _region,
                IdentityToken = identityToken,
                AuthorizationCode = string.IsNullOrWhiteSpace(credential.AuthorizationCode) ? null : credential.AuthorizationCode.Trim(),
                Nonce = string.IsNullOrWhiteSpace(credential.Nonce) ? null : credential.Nonce.Trim(),
                Email = string.IsNullOrWhiteSpace(credential.Email) ? null : credential.Email.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(credential.DisplayName) ? null : credential.DisplayName.Trim(),
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Platform = device.Platform
            }, AccountJsonContext.Default.AppleSignInRequest);
            using var response = await SendAsync(request, cancellationToken);
            EnsureAppleSuccess(response);
            var session = await ReadAsync(response, AccountJsonContext.Default.AccountSessionResponse, cancellationToken);
            if (session.Region != _region)
                throw new InvalidOperationException("账号服务区域不匹配，请检查所选区域和服务地址。");
            if (session.User is null || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)
                || session.Token is not { Length: 43 }
                || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || session.ExpiresAt <= _clock.GetUtcNow())
                throw new InvalidOperationException("账号服务返回了无效的会话。");
            await SetAsync(endpoint, session, cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> BindAppleAsync(
        AppleSignInCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var identityToken = credential.IdentityToken?.Trim() ?? "";
        if (string.IsNullOrEmpty(identityToken))
            throw new ArgumentException("Apple 身份验证无效，请重试。", nameof(credential));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var current = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "apple/bind", current.Token);
            request.Content = JsonContent.Create(new AppleBindRequest
            {
                IdentityToken = identityToken,
                AuthorizationCode = string.IsNullOrWhiteSpace(credential.AuthorizationCode) ? null : credential.AuthorizationCode.Trim(),
                Nonce = string.IsNullOrWhiteSpace(credential.Nonce) ? null : credential.Nonce.Trim()
            }, AccountJsonContext.Default.AppleBindRequest);
            using var response = await SendAsync(request, cancellationToken);
            await EnsureAppleAccountChangeSuccessAsync(response, cancellationToken);
            var user = await ReadAsync(response, AccountJsonContext.Default.AccountUser, cancellationToken);
            if (user.Id != current.User.Id || !AccountValidation.IsUsername(user.Username))
                throw new InvalidOperationException("账号服务返回了无效的账号资料。");
            await SetAsync(_serverUrl, new AccountSessionResponse
            {
                Region = _region,
                User = user with { AppleLinked = true },
                Token = current.Token,
                ExpiresAt = current.ExpiresAt
            }, cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> UnbindAppleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var current = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "apple/unbind", current.Token);
            using var response = await SendAsync(request, cancellationToken);
            await EnsureAppleAccountChangeSuccessAsync(response, cancellationToken);
            var user = await ReadAsync(response, AccountJsonContext.Default.AccountUser, cancellationToken);
            if (user.Id != current.User.Id || !AccountValidation.IsUsername(user.Username))
                throw new InvalidOperationException("账号服务返回了无效的账号资料。");
            await SetAsync(_serverUrl, new AccountSessionResponse
            {
                Region = _region,
                User = user with { AppleLinked = false },
                Token = current.Token,
                ExpiresAt = current.ExpiresAt
            }, cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAppleAccountChangeSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await InvalidateAsync(token);
            throw new InvalidOperationException("会话已失效，请重新登录。");
        }
        EnsureAppleSuccess(response);
    }

    private static void EnsureAppleSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed)
            throw new InvalidOperationException("当前账号服务暂未开启 Apple 登录，请待服务端更新后再试。");
        EnsureSuccess(response, register: false);
    }

    public async Task<AccountStatus> UpdateProfileAsync(string username, string currentPassword, CancellationToken token = default)
    {
        username = username.Trim();
        if (!AccountValidation.IsUsername(username)) throw new ArgumentException("用户名需为 3–32 位字母、数字、点、下划线或连字符。");
        if (!AccountValidation.IsPassword(currentPassword)) throw new ArgumentException("请输入当前密码。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var session = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "profile", session.Token);
            request.Content = JsonContent.Create(new AccountProfileUpdate { Username = username, CurrentPassword = currentPassword },
                AccountJsonContext.Default.AccountProfileUpdate);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            var user = await ReadAsync(response, AccountJsonContext.Default.AccountUser, token);
            if (user.Id != session.User.Id || user.Username != username || user.Email != session.User.Email)
                throw new InvalidOperationException("账号服务返回了无效的账号资料。");
            await SetAsync(_serverUrl, new AccountSessionResponse { Region = _region, User = user, Token = session.Token, ExpiresAt = session.ExpiresAt }, token);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken token = default)
    {
        if (!AccountValidation.IsPassword(currentPassword)) throw new ArgumentException("请输入当前密码。");
        if (!AccountValidation.IsPassword(newPassword)) throw new ArgumentException("密码需为 8–128 位，且不能全部为空格。");
        if (currentPassword == newPassword) throw new ArgumentException("新密码不能与当前密码相同。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var current = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "password", current.Token);
            request.Content = JsonContent.Create(new AccountPasswordChange { CurrentPassword = currentPassword, NewPassword = newPassword },
                AccountJsonContext.Default.AccountPasswordChange);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            var session = await ReadAsync(response, AccountJsonContext.Default.AccountSessionResponse, token);
            if (session.Region != _region || session.User is null || session.User.Id != current.User.Id || !AccountValidation.IsUsername(session.User.Username)
                || session.Token is not { Length: 43 } || !session.Token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                || session.ExpiresAt <= _clock.GetUtcNow()) throw new InvalidOperationException("账号服务返回了无效的会话。");
            // The server rotated this device's token and revoked the other devices.
            await SetAsync(_serverUrl, session, CancellationToken.None);
            return Status();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Asks the server to email a reset code. Unknown accounts get the same answer.</summary>
    public async Task RequestPasswordResetAsync(string serverUrl, string username, CancellationToken token = default)
    {
        var endpoint = AccountValidation.ServerUri(serverUrl).AbsoluteUri.TrimEnd('/');
        username = username.Trim();
        if (!AccountValidation.IsLogin(username))
            throw new ArgumentException("请输入有效的用户名或邮箱。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            using var request = Request(HttpMethod.Post, endpoint, "password-reset/request");
            request.Content = JsonContent.Create(new AccountPasswordResetRequest { Username = username },
                AccountJsonContext.Default.AccountPasswordResetRequest);
            using var response = await SendAsync(request, token);
            EnsureSuccess(response, register: false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Confirms an emailed code and sets a new password. The server revokes every session.</summary>
    public async Task<AccountUser> ResetPasswordAsync(string serverUrl, string username, string code,
        string newPassword, string confirmPassword, CancellationToken token = default)
    {
        var endpoint = AccountValidation.ServerUri(serverUrl).AbsoluteUri.TrimEnd('/');
        username = username.Trim();
        code = code.Trim();
        if (!AccountValidation.IsLogin(username)) throw new ArgumentException("请输入有效的用户名或邮箱。");
        if (!IsEmailCode(code)) throw new ArgumentException("请输入邮件中的 8 位验证码。");
        if (!AccountValidation.IsPassword(newPassword)) throw new ArgumentException("密码需为 8–128 位，且不能全部为空格。");
        if (newPassword != confirmPassword) throw new ArgumentException("两次输入的密码不一致。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            using var request = Request(HttpMethod.Post, endpoint, "password-reset/confirm");
            request.Content = JsonContent.Create(new AccountPasswordResetConfirm { Username = username, Code = code,
                NewPassword = newPassword, ConfirmPassword = confirmPassword }, AccountJsonContext.Default.AccountPasswordResetConfirm);
            using var response = await SendAsync(request, token);
            EnsureSuccess(response, register: false);
            var user = await ReadAsync(response, AccountJsonContext.Default.AccountUser, token);
            if (user.Id == Guid.Empty || !AccountValidation.IsUsername(user.Username))
                throw new InvalidOperationException("账号服务返回了无效的账号资料。");
            // Resetting revokes every server session; drop this device's stored token for that account.
            if (_session is { } session && session.User.Id == user.Id && string.Equals(_serverUrl, endpoint, StringComparison.Ordinal))
                await SetAsync(_serverUrl, null, CancellationToken.None);
            return user;
        }
        finally { _gate.Release(); }
    }

    public Task RequestEmailVerificationAsync(CancellationToken token = default) => MemberActionAsync("email/verify/request", token);

    /// <summary>Confirms the emailed code and marks the registered email as verified.</summary>
    public async Task<AccountStatus> ConfirmEmailVerificationAsync(string code, CancellationToken token = default)
    {
        code = code.Trim();
        if (!IsEmailCode(code)) throw new ArgumentException("请输入邮件中的 8 位验证码。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var current = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "email/verify", current.Token);
            request.Content = JsonContent.Create(new AccountEmailVerificationConfirm { Code = code },
                AccountJsonContext.Default.AccountEmailVerificationConfirm);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            var user = await ReadAsync(response, AccountJsonContext.Default.AccountUser, token);
            if (user.Id != current.User.Id || !AccountValidation.IsUsername(user.Username) || !user.EmailVerified)
                throw new InvalidOperationException("账号服务返回了无效的账号资料。");
            await SetAsync(_serverUrl, new AccountSessionResponse { Region = _region, User = user,
                Token = current.Token, ExpiresAt = current.ExpiresAt }, token);
            return Status();
        }
        finally { _gate.Release(); }
    }

    private static bool IsEmailCode(string code) => code.Length == 8 && code.All(char.IsAsciiDigit);

    public async Task CloseAccountAsync(string currentPassword, string confirmation, CancellationToken token = default)
    {
        if (confirmation != "停用账号") throw new ArgumentException("请输入“停用账号”确认文字。");
        if (!AccountValidation.IsPassword(currentPassword)) throw new ArgumentException("请输入当前密码。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var session = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "close", session.Token);
            request.Content = JsonContent.Create(new AccountClosureRequest { CurrentPassword = currentPassword, Confirmation = confirmation },
                AccountJsonContext.Default.AccountClosureRequest);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            await SetAsync(_serverUrl, null, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAccountChangeSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Wrong current passwords are 403 and keep this device signed in.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        { await InvalidateAsync(token); throw new InvalidOperationException("会话已失效，请重新登录。"); }
        EnsureSuccess(response, register: false);
    }

    /// <returns>False when the device signed out but the server could not revoke the session.</returns>
    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var revoked = true;
            if (_session is { } session)
            {
                try
                {
                    using var request = Request(HttpMethod.Post, _serverUrl, "logout", session.Token);
                    using var response = await SendAsync(request, cancellationToken);
                    revoked = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized;
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
                {
                    revoked = false;
                }
            }
            // Even cancellation or a network outage must not leave this device signed in.
            await SetAsync(_serverUrl, null, CancellationToken.None);
            return revoked;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (!_loaded)
        {
            (_region, _connections) = await _store.LoadAsync(token);
            foreach (var region in _connections.Keys.ToArray())
                _connections[region] = ResolveConnection(region, _connections[region]);
            var connection = ResolveConnection(_region, _connections.GetValueOrDefault(_region));
            _connections[_region] = connection;
            _serverUrl = connection.ServerUrl; _session = connection.Session;
            _loaded = true;
        }
        if (_session is { } session && (session.ExpiresAt <= _clock.GetUtcNow() || session.User is null
            || session.Region != _region || session.Token is not { Length: 43 }
            || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)))
            await InvalidateAsync(token);
        if (_session is null)
        {
            var connection = ResolveConnection(_region, _connections.GetValueOrDefault(_region));
            _connections[_region] = connection;
            _serverUrl = connection.ServerUrl;
        }
    }

    private string DefaultEndpoint(AccountRegion region) =>
        AccountValidation.ServerUri(_defaultServerUrl(region)).AbsoluteUri.TrimEnd('/');

    private RegionalAccountConnection ResolveConnection(AccountRegion region, RegionalAccountConnection? connection)
    {
        if (connection is null) return new(DefaultEndpoint(region), null, true);
        var followsDefault = connection.UsesDefaultServerUrl
            ?? AccountServerEndpoints.IsLegacyDevelopmentDefault(region, connection.ServerUrl);
        // A stored token is always bound to its stored server, even if a release
        // or environment override changes the configured default endpoint.
        if (connection.Session is null && followsDefault) return new(DefaultEndpoint(region), null, true);
        return new(connection.ServerUrl, connection.Session, followsDefault);
    }

    private async Task SetAsync(string server, AccountSessionResponse? session, CancellationToken token)
    {
        var previous = _connections.GetValueOrDefault(_region);
        var followsDefault = previous is not null && string.Equals(previous.ServerUrl, server, StringComparison.Ordinal)
            ? previous.UsesDefaultServerUrl == true
            : string.Equals(DefaultEndpoint(_region), server, StringComparison.Ordinal);
        // Persist token removal before resolving a new default. Even an invalid
        // deployment override must not prevent local logout; the next status
        // read resolves (and validates) the signed-out default connection.
        var connection = new RegionalAccountConnection(server, session, followsDefault);
        var updated = new Dictionary<AccountRegion, RegionalAccountConnection>(_connections) { [_region] = connection };
        await _store.SaveAsync(_region, updated, token);
        _connections = updated;
        _serverUrl = connection.ServerUrl;
        _session = connection.Session;
    }

    private AccountStatus Status() => new(_serverUrl, _session?.User, _session?.ExpiresAt, _region,
        _dataRegion is { } region && region != _region);

    private HttpRequestMessage Request(HttpMethod method, string server, string action, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, new Uri(AccountValidation.ServerUri(server), "api/accounts/" + action));
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Libzora-Region", AccountRegions.Slug(_region));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接账号服务，请检查服务地址和网络。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("账号服务连接超时，请稍后重试。"); }
    }

    private static void EnsureSuccess(HttpResponseMessage response, bool register)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.Headers.TryGetValues("X-Libzora-Error", out var values) && values.Contains("account_banned"))
            throw new InvalidOperationException("此账号已被封禁，请联系管理员。");
        if (response.Headers.TryGetValues("X-Libzora-Error", out values) && ApiMessage(values.FirstOrDefault()) is { } message)
            throw new InvalidOperationException(message);
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "用户名、邮箱或密码错误。",
            HttpStatusCode.Forbidden => "账号服务区域不匹配，请检查所选区域和服务地址。",
            HttpStatusCode.Conflict when register => "该用户名已注册，请登录或换一个用户名。",
            HttpStatusCode.TooManyRequests => "尝试次数过多，请稍后再试。",
            HttpStatusCode.BadRequest => "账号信息无效，请检查用户名和密码。",
            _ => "账号服务暂时不可用，请检查地址或稍后重试。"
        });
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> metadata, CancellationToken token,
        int maximumBytes = 16 * 1024)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Bound replies, including replies from a mistakenly configured server.
            await response.Content.LoadIntoBufferAsync(maximumBytes).WaitAsync(deadline.Token);
            return await response.Content.ReadFromJsonAsync(metadata, deadline.Token)
                ?? throw new InvalidOperationException("账号服务返回了无效的响应。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("账号服务连接超时，请稍后重试。"); }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or NotSupportedException)
        { throw new InvalidOperationException("账号服务返回了无效的响应。"); }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public sealed class AccountDeviceInvalidatedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
