using Libzora.Core;

namespace Libzora.Tests;

public sealed class AccountAvailabilityTests
{
    [Fact]
    public void RegistrationAndSyncEntitlementsStayOffUntilTheServiceShips()
    {
        Assert.False(AccountAvailability.RegistrationAvailable);
        Assert.False(AccountAvailability.EnforceSyncEntitlements);
    }

    [Fact]
    public async Task FreeSyncAccessAllowsUserProvidedStorageOnly()
    {
        await AllowAllDeviceFeatureAccess.Instance.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await AllowAllDeviceFeatureAccess.Instance.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() =>
            AllowAllDeviceFeatureAccess.Instance.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
    }
}
