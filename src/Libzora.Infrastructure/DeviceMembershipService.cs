using Libzora.Core;

namespace Libzora.Infrastructure;

public sealed class DeviceMembershipService(AccountService accounts) : IDeviceFeatureAccess
{
    public async Task<DeviceFeatureStatus> RefreshAsync(CancellationToken token = default)
    {
        var device = await accounts.GetDeviceAsync(token);
        var reply = await accounts.MemberRequestAsync(HttpMethod.Get, "devices/" + device.DeviceId,
            AccountJsonContext.Default.DeviceFeatureStatus, null, token);
        Validate(reply.Value, reply.Scope, device.DeviceId);
        return reply.Value;
    }

    public async Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(feature)) throw new ArgumentOutOfRangeException(nameof(feature));
        if (feature == DeviceFeature.AccountCloudSync)
            throw new DeviceFeatureRequiredException(MemberEntitlements.AccountCloudRequirement);
        var device = await accounts.GetDeviceAsync(cancellationToken);
        var reply = await accounts.MemberRequestAsync(HttpMethod.Post,
            "devices/" + device.DeviceId + "/features",
            AccountJsonContext.Default.DeviceFeatureStatus, null, cancellationToken);
        Validate(reply.Value, reply.Scope, device.DeviceId);
        if (reply.Scope.RequiresDataRestart) throw new DeviceFeatureRequiredException("区域已切换，请重新打开应用后使用该区域的账号和功能权益。");
        if (!reply.Value.CanUseFeatures)
            throw new DeviceFeatureRequiredException(MemberEntitlements.SyncRequirement);
    }

    public async Task KickAsync(Guid device, CancellationToken token = default)
    {
        await accounts.MemberActionAsync("devices/" + device + "/kick", token);
        var current = await accounts.GetDeviceAsync(token);
        if (current.DeviceId == device) await accounts.GetStatusAsync(validateSession: true, cancellationToken: token);
    }

    // Source-compatible retirement stubs: no catalog, orders or payment request reaches the activation server.
    public Task<MembershipProduct[]> ProductsAsync(CancellationToken token = default) => Task.FromResult<MembershipProduct[]>([]);
    public Task<PurchaseOrder[]> OrdersAsync(CancellationToken token = default) => Task.FromResult<PurchaseOrder[]>([]);
    public Task<PurchaseOrder> PurchaseAsync(string productId, Guid requestId, CancellationToken token = default) =>
        Task.FromException<PurchaseOrder>(new InvalidOperationException(UiText.Get("账号服务仅用于功能激活与验证，不处理购买订单。")));
    public Task CancelOrderAsync(Guid order, CancellationToken token = default) =>
        Task.FromException(new InvalidOperationException(UiText.Get("账号服务仅用于功能激活与验证，不处理购买订单。")));

    /// <summary>Claims the one-time free trial. Only called after the user turns on the claim switch.</summary>
    public async Task<AccountMembership> ClaimTrialAsync(CancellationToken token = default)
    {
        var reply = await accounts.MemberRequestAsync(HttpMethod.Post, "trial",
            AccountJsonContext.Default.AccountMembership, null, token,
            UiText.Get("请先登录当前区域的账号后领取免费试用。"));
        if (reply.Value.AccountId != reply.Scope.User?.Id || reply.Value.Region != reply.Scope.Region
            || !reply.Value.TrialClaimed || reply.Value.TrialClaimedAt is null || reply.Value.TrialExpiresAt is null)
            throw new InvalidOperationException("账号服务返回了无效的试用状态。");
        return reply.Value;
    }

    private static void Validate(DeviceFeatureStatus status, AccountStatus scope, Guid device)
    {
        if (status.Membership is null || status.Devices is null || status.Membership.AccountId != scope.User?.Id
            || status.Membership.Region != scope.Region || status.CurrentDeviceId != device
            || status.Membership.DeviceLimit != MemberEntitlements.DeviceLimit || status.Membership.Tier is not ("free" or "trial" or "lifetime" or "pro")
            || status.Membership.LoggedInDeviceCount is < 0 or > MemberEntitlements.DeviceLimit
            || status.Membership.PendingPlusDays < 0
            || status.Devices.Length > 100 || status.Devices.Any(item => item.DeviceId == Guid.Empty)
            || status.Devices.Select(item => item.DeviceId).Distinct().Count() != status.Devices.Length
            || status.Devices.Any(item => item.State is not ("active" or "signed_out" or "blocked" or "expired"))
            || status.Devices.Count(item => item.State == "active") != status.Membership.LoggedInDeviceCount
            || status.Membership.ActivatedDeviceCount != (status.Membership.HasLifetimeFeatures ? status.Membership.LoggedInDeviceCount : 0)
            || (status.Membership.TrialAvailable && (status.Membership.TrialClaimed || status.Membership.ProExpiresAt is not null))
            || status.Membership.TrialClaimed != (status.Membership.TrialClaimedAt is not null)
            || (status.Membership.TrialExpiresAt is not null && !status.Membership.TrialClaimed)
            || (status.Membership.TrialExpiresAt is { } trialExpiry && status.Membership.TrialClaimedAt is { } trialClaimed
                && trialExpiry <= trialClaimed))
            throw new InvalidOperationException("账号服务返回了无效的会员或设备状态。");
    }
}

internal sealed class DeniedDeviceFeatureAccess : IDeviceFeatureAccess
{
    internal static DeniedDeviceFeatureAccess Instance { get; } = new();
    public Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default) =>
        Task.FromException(new DeviceFeatureRequiredException(feature == DeviceFeature.AccountCloudSync
            ? MemberEntitlements.AccountCloudRequirement : MemberEntitlements.SyncRequirement));
}
