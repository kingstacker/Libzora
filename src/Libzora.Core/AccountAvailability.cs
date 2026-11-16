namespace Libzora.Core;

/// <summary>
/// Product switches for the public account service. The public service is not
/// deployed yet: registration stays closed, while local features and sync with
/// the user's own S3 / WebDAV storage remain free. Turn these on together when
/// the account service ships.
/// </summary>
public static class AccountAvailability
{
    /// <summary>Registration is announced as unavailable in distributed builds.</summary>
    public static bool RegistrationAvailable => false;

    /// <summary>Sync entitlement checks only apply once the account service is online.</summary>
    public static bool EnforceSyncEntitlements => false;
}

/// <summary>Keeps sync usable while account entitlements are not enforced.</summary>
public sealed class AllowAllDeviceFeatureAccess : IDeviceFeatureAccess
{
    public static AllowAllDeviceFeatureAccess Instance { get; } = new();

    private AllowAllDeviceFeatureAccess() { }

    public Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default) =>
        feature == DeviceFeature.AccountCloudSync
            ? Task.FromException(new DeviceFeatureRequiredException(MemberEntitlements.AccountCloudRequirement))
            : Task.CompletedTask;
}
