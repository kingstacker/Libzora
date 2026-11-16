using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Libzora.Core;

namespace Libzora.TestFixtures;

/// <summary>Fictional account responses for client protocol and UI behavior tests.</summary>
public sealed class MembershipHttpHandler : HttpMessageHandler
{
    public const string Password = "fictional-membership-test-password";
    public const string ResetCode = "12345678";
    public const string EmailVerificationCode = "87654321";
    public AccountUser User { get; private set; } = new(Guid.NewGuid(), "fictional_member", "member@example.invalid");
    private string _password = Password;
    private readonly string _bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public Guid CurrentDeviceId { get; private set; }
    public Guid OtherDeviceId { get; } = Guid.NewGuid();
    public List<AccountDevice> Devices { get; } = [];
    public bool LifetimeFeatures { get; set; } = true;
    public DateTimeOffset? ProExpiresAt { get; set; }
    public bool TrialAvailable { get; set; }
    public DateTimeOffset? TrialClaimedAt { get; private set; }
    public DateTimeOffset? TrialExpiresAt { get; private set; }
    public bool Revoked { get; set; }
    public bool Offline { get; set; }
    public int SessionChecks { get; private set; }
    public int FeatureChecks { get; private set; }
    public int PasswordResetRequests { get; private set; }
    public int EmailVerificationRequests { get; private set; }
    public bool EmailServiceAvailable { get; set; } = true;
    public Guid? LastKickedDevice { get; private set; }
    public List<string> RequestedActions { get; } = [];
    public int DeviceStatusChecks { get; private set; }
    public bool UseMinimalMembershipContract { get; set; } = true;
    public Func<DeviceFeatureStatus, DeviceFeatureStatus>? TransformStatus { get; set; }
    public bool AccountClosed { get; private set; }
    public string UnavailableUsername { get; set; } = "taken_reader";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Offline) throw new HttpRequestException("fictional network outage");
        var region = request.Headers.GetValues("X-Libzora-Region").Single() == "china" ? AccountRegion.China : AccountRegion.International;
        var action = request.RequestUri!.AbsolutePath.Split("/api/accounts/")[1];
        RequestedActions.Add(action);
        if (action is "login" or "register")
        {
            var credentials = await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountCredentials, token);
            if (action == "register" && (!AccountValidation.IsEmail(credentials!.Email) || credentials.Password != credentials.ConfirmPassword))
                return new(HttpStatusCode.BadRequest);
            if (!(credentials!.Username.Equals(User.Username, StringComparison.OrdinalIgnoreCase)
                || credentials.Username.Equals(User.Email, StringComparison.OrdinalIgnoreCase)) || credentials.Password != _password) return new(HttpStatusCode.Unauthorized);
            if (action == "register") User = User with { Email = credentials.Email!.Trim() };
            CurrentDeviceId = credentials.DeviceId;
            Revoked = false;
            Devices.Clear();
            Devices.Add(new(CurrentDeviceId, credentials.DeviceName, credentials.Platform, "active", Now, Now));
            Devices.Add(new(OtherDeviceId, "Fictional second device", "iOS", "active", Now, Now));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSessionResponse
                { Region = region, User = User, Token = _bearer, ExpiresAt = Now.AddDays(30) }, AccountJsonContext.Default.AccountSessionResponse) };
        }
        if (action == "password-reset/request")
        {
            PasswordResetRequests++;
            await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountPasswordResetRequest, token);
            return EmailServiceAvailable ? new(HttpStatusCode.Accepted) : Error(HttpStatusCode.ServiceUnavailable, "email_unavailable");
        }
        if (action == "password-reset/confirm")
        {
            var reset = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountPasswordResetConfirm, token))!;
            var identifier = reset.Username.Trim();
            var matches = identifier.Equals(User.Username, StringComparison.OrdinalIgnoreCase)
                || identifier.Equals(User.Email, StringComparison.OrdinalIgnoreCase);
            if (!matches || reset.Code != ResetCode || !EmailServiceAvailable) return Error(HttpStatusCode.BadRequest, "invalid_reset_code");
            _password = reset.NewPassword;
            Revoked = true;
            User = User with { EmailVerified = true };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(User, AccountJsonContext.Default.AccountUser) };
        }
        if (action == "session") SessionChecks++;
        if (request.Headers.Authorization?.Parameter != _bearer || Revoked) return new(HttpStatusCode.Unauthorized);
        if (action == "session") return new(HttpStatusCode.OK) { Content = JsonContent.Create(
            new AccountSessionInfo(User, Now.AddDays(30), region), AccountJsonContext.Default.AccountSessionInfo) };
        if (action == "logout") { Revoked = true; return new(HttpStatusCode.NoContent); }
        if (action == "profile")
        {
            var profile = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountProfileUpdate, token))!;
            if (profile.CurrentPassword != _password) return Error(HttpStatusCode.Forbidden, "current_password_incorrect");
            if (profile.Username == UnavailableUsername) return Error(HttpStatusCode.Conflict, "username_exists");
            User = User with { Username = profile.Username };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(User, AccountJsonContext.Default.AccountUser) };
        }
        if (action == "password")
        {
            var change = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountPasswordChange, token))!;
            if (change.CurrentPassword != _password) return Error(HttpStatusCode.Forbidden, "current_password_incorrect");
            _password = change.NewPassword;
            for (var index = 0; index < Devices.Count; index++)
                if (Devices[index].DeviceId != CurrentDeviceId) Devices[index] = Devices[index] with { State = "signed_out" };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSessionResponse
                { Region = region, User = User, Token = _bearer, ExpiresAt = Now.AddDays(30) }, AccountJsonContext.Default.AccountSessionResponse) };
        }
        if (action == "email/verify/request")
        {
            EmailVerificationRequests++;
            if (User.Email is null) return Error(HttpStatusCode.Conflict, "email_not_set");
            return EmailServiceAvailable ? new(HttpStatusCode.Accepted) : Error(HttpStatusCode.ServiceUnavailable, "email_unavailable");
        }
        if (action == "email/verify")
        {
            var verification = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountEmailVerificationConfirm, token))!;
            if (verification.Code != EmailVerificationCode) return Error(HttpStatusCode.BadRequest, "invalid_email_code");
            User = User with { EmailVerified = true };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(User, AccountJsonContext.Default.AccountUser) };
        }
        if (action == "close")
        {
            var closure = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountClosureRequest, token))!;
            if (closure.Confirmation != "停用账号") return Error(HttpStatusCode.BadRequest, "confirmation_required");
            if (closure.CurrentPassword != _password) return Error(HttpStatusCode.Forbidden, "current_password_incorrect");
            AccountClosed = true; Revoked = true;
            return new(HttpStatusCode.Accepted);
        }
        if (action.StartsWith("devices/", StringComparison.Ordinal))
        {
            var deviceId = Guid.Parse(action.Split('/')[1]);
            if (action.EndsWith("/kick", StringComparison.Ordinal))
            {
                LastKickedDevice = deviceId;
                var index = Devices.FindIndex(device => device.DeviceId == deviceId);
                if (index < 0) return new(HttpStatusCode.NotFound);
                if (Devices[index].State == "blocked")
                {
                    var blocked = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    blocked.Headers.Add("X-Libzora-Error", "device_blocked");
                    return blocked;
                }
                Devices[index] = Devices[index] with { State = "signed_out" };
                if (deviceId == CurrentDeviceId) Revoked = true;
                return new(HttpStatusCode.NoContent);
            }
            if (action.EndsWith("/features", StringComparison.Ordinal))
            {
                FeatureChecks++;
                if (!LifetimeFeatures && !((ProExpiresAt ?? TrialExpiresAt) > Now))
                {
                    var denied = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
                    denied.Headers.Add("X-Libzora-Error", "membership_required");
                    return denied;
                }
            }
            DeviceStatusChecks++;
            var status = Status(region);
            return new(HttpStatusCode.OK) { Content = UseMinimalMembershipContract
                ? JsonContent.Create(new { membership = MinimalMembership(status.Membership), devices = status.Devices, currentDeviceId = status.CurrentDeviceId })
                : JsonContent.Create(status, AccountJsonContext.Default.DeviceFeatureStatus) };
        }
        if (action == "trial")
        {
            if (TrialClaimedAt is null)
            {
                if (!TrialAvailable || LifetimeFeatures || ProExpiresAt is not null) return Error(HttpStatusCode.Conflict, "trial_unavailable");
                TrialClaimedAt = Now;
                TrialExpiresAt = Now.AddDays(MemberEntitlements.TrialDays);
            }
            var member = Status(region).Membership;
            return new(HttpStatusCode.OK) { Content = UseMinimalMembershipContract
                ? JsonContent.Create(MinimalMembership(member))
                : JsonContent.Create(member, AccountJsonContext.Default.AccountMembership) };
        }
        if (action is "products" or "orders" || action.StartsWith("orders/", StringComparison.Ordinal)
            || action.StartsWith("activation-codes/", StringComparison.Ordinal)) return new(HttpStatusCode.Gone);
        return new(HttpStatusCode.NotFound);
    }

    private DeviceFeatureStatus Status(AccountRegion region)
    {
        var active = Devices.Count(device => device.State == "active");
        var limitedFeatures = (ProExpiresAt ?? TrialExpiresAt) > Now;
        var features = LifetimeFeatures || limitedFeatures;
        var member = new AccountMembership(User.Id, region, LifetimeFeatures ? "lifetime" : limitedFeatures ? "trial" : "free", features,
            ProExpiresAt, MemberEntitlements.DeviceLimit, active, features ? active : 0,
            0, 0, 0, 0, 0, 0, 0, null,
            TrialAvailable: TrialAvailable && !LifetimeFeatures && TrialClaimedAt is null && ProExpiresAt is null,
            TrialClaimed: TrialClaimedAt is not null, TrialClaimedAt: TrialClaimedAt, TrialExpiresAt: TrialExpiresAt);
        var status = new DeviceFeatureStatus(member, Devices.ToArray(), CurrentDeviceId);
        return TransformStatus?.Invoke(status) ?? status;
    }

    // The activation service has no catalog, orders, codes, cloud usage or quota fields.
    private static object MinimalMembership(AccountMembership member) => new
    {
        accountId = member.AccountId,
        region = member.Region,
        tier = member.Tier,
        hasLifetimeFeatures = member.HasLifetimeFeatures,
        featureExpiresAt = member.Tier == "lifetime" ? null : member.FeatureExpiresAt,
        deviceLimit = member.DeviceLimit,
        loggedInDeviceCount = member.LoggedInDeviceCount,
        activatedDeviceCount = member.ActivatedDeviceCount,
        trialAvailable = member.TrialAvailable,
        trialClaimed = member.TrialClaimed,
        trialClaimedAt = member.TrialClaimedAt,
        trialExpiresAt = member.TrialExpiresAt
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var reply = new HttpResponseMessage(status);
        reply.Headers.Add("X-Libzora-Error", code);
        return reply;
    }
}
