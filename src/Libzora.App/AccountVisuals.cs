using Avalonia.Controls;
using Avalonia.Media;
using Libzora.Core;

namespace Libzora;

internal static class AccountVisuals
{
    private const string UserWithBadge = "M10,3 A4,4 0 1 1 10,11 A4,4 0 1 1 10,3 M3,21 V20 Q3,13 10,13 Q14,13 16,16";
    internal static readonly Geometry User = Geometry.Parse("M12,3 A4,4 0 1 1 12,11 A4,4 0 1 1 12,3 M5,21 V20 Q5,13 12,13 Q19,13 19,20 V21");
    internal static readonly Geometry Lifetime = Geometry.Parse(UserWithBadge + " M16,20 L18,22 L22,18");
    internal static readonly Geometry Trial = Geometry.Parse(UserWithBadge + " M15,20 A4,4 0 1 0 23,20 A4,4 0 1 0 15,20 M19,17 V20 L21,21");
    internal static readonly Geometry Desktop = Geometry.Parse("M4,3 H20 A2,2 0 0 1 22,5 V15 A2,2 0 0 1 20,17 H4 A2,2 0 0 1 2,15 V5 A2,2 0 0 1 4,3 Z M8,21 H16 M12,17 V21");
    internal static readonly Geometry Phone = Geometry.Parse("M7,2 H17 A2,2 0 0 1 19,4 V20 A2,2 0 0 1 17,22 H7 A2,2 0 0 1 5,20 V4 A2,2 0 0 1 7,2 Z M10,5 H14 M12,18 H12.01");
    internal static readonly Geometry Tablet = Geometry.Parse("M4,2 H20 A2,2 0 0 1 22,4 V20 A2,2 0 0 1 20,22 H4 A2,2 0 0 1 2,20 V4 A2,2 0 0 1 4,2 Z M12,18 H12.01");

    // HasLifetimeFeatures describes access now, including a trial; only the
    // lifetime tier proves a permanent purchase. Legacy pro remains time-limited.
    internal static string Tier(AccountMembership? member) => member switch
    {
        { Tier: "lifetime" } => "lifetime",
        { IsTrialSubscription: true, FeatureExpiresAt: { } expiry } when expiry > DateTimeOffset.UtcNow => "trial",
        _ => "free"
    };

    internal static Geometry MembershipIcon(string tier, bool signedIn = true) => !signedIn ? User : tier switch
    {
        "trial" => Trial,
        "lifetime" => Lifetime,
        _ => User
    };

    internal static Geometry DeviceIcon(string platform) => platform.ToLowerInvariant() switch
    {
        "ios" or "android" => Phone,
        "ipados" or "ipad" => Tablet,
        _ => Desktop
    };

    internal static void SetTier(Control control, string tier)
    {
        foreach (var value in new[] { "free", "trial", "lifetime" })
            control.Classes.Set(value, value == tier);
    }
}
