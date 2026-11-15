using System.Text.Json.Serialization;

namespace Libzora.Core;

public static class MemberEntitlements
{
    public const int DeviceLimit = 6;
    public const int TrialDays = 7;
    public const string SyncRequirement = "多端同步需要有效的试用订阅或买断订阅（¥88 永久）。仅支持自备 S3 / WebDAV，存储与流量费用由用户承担；不提供官方云同步。";
    public const string AccountCloudRequirement = "官方云同步已移除，请配置自备 S3 或 WebDAV。";

    public static string TierName(string tier) => tier switch
    { "trial" or "pro" => "试用订阅", "lifetime" => "买断订阅", _ => "Free" };

    /// <summary>Legacy paid periods remain time-limited feature access; they never become a permanent purchase.</summary>
    public static string EffectiveTier(AccountMembership? membership, DateTimeOffset now) => membership switch
    {
        { Tier: "lifetime" } => "lifetime",
        { Tier: "trial" or "pro", FeatureExpiresAt: { } expiry } when expiry > now => "trial",
        _ => "free"
    };

    public static DeviceFeature SyncFeature(SyncProvider provider) => provider switch
    {
        SyncProvider.S3 => DeviceFeature.CustomS3,
        SyncProvider.WebDav => DeviceFeature.CustomWebDav,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), "仅支持 S3 或 WebDAV 同步。")
    };

    /// <summary>The most urgent expiry reminder for a time-limited feature subscription.</summary>
    public static int? TrialRenewalReminder(DateTimeOffset expiry, DateTimeOffset now)
    {
        var remaining = expiry.ToUniversalTime() - now.ToUniversalTime();
        if (remaining <= TimeSpan.Zero) return null;
        if (remaining <= TimeSpan.FromDays(1)) return 1;
        if (remaining <= TimeSpan.FromDays(7)) return 7;
        if (remaining <= TimeSpan.FromDays(30)) return 30;
        return null;
    }

    public static string TrialRenewalMessage(DateTimeOffset expiry, DateTimeOffset now)
    {
        var days = Math.Max(1, (int)Math.Ceiling((expiry.ToUniversalTime() - now.ToUniversalTime()).TotalDays));
        return days <= 1
            ? UiText.Get("试用订阅将于 {0:yyyy/MM/dd} 到期（不足 1 天）。到期后 S3 / WebDAV 多端同步将停用，本地书库及自备存储中的数据不会删除。购买买断订阅可永久使用。", expiry.LocalDateTime)
            : UiText.Get("试用订阅将于 {0:yyyy/MM/dd} 到期（剩余 {1} 天）。到期后 S3 / WebDAV 多端同步将停用，本地书库及自备存储中的数据不会删除。购买买断订阅可永久使用。", expiry.LocalDateTime, days);
    }

    public static string TrialExpiryMessage(DateTimeOffset expiry, DateTimeOffset now) =>
        UiText.Get("试用订阅已于 {0:yyyy/MM/dd} 到期，S3 / WebDAV 多端同步已停用。本地书库及自备存储中的数据不会删除；购买买断订阅可永久恢复同步功能。", expiry.LocalDateTime);

    // Fail-closed compatibility for callers querying a retired annual product.
    public static bool CanExtendPlus(AccountMembership membership, int days, DateTimeOffset now) => false;
}

/// <summary>Trial and lifetime subscribers can use their own S3/WebDAV. The legacy cloud value is never granted.</summary>
public enum DeviceFeature { CustomS3, CustomWebDav, AccountCloudSync }

public interface IDeviceFeatureAccess
{
    Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default);
}

public sealed class DeviceFeatureRequiredException(string message) : InvalidOperationException(message);

public sealed record AccountDevice(Guid DeviceId, string Name, string Platform, string State,
    DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);

// UploadQuota is retained for wire compatibility; zero means no monthly upload limit. Monthly bytes are statistics.
public sealed record AccountMembership(Guid AccountId, AccountRegion Region, string Tier, bool HasLifetimeFeatures,
    DateTimeOffset? ProExpiresAt, int DeviceLimit, int LoggedInDeviceCount, int ActivatedDeviceCount,
    long UploadBytes, long UploadQuota, long DownloadBytes, long DownloadRemainingBytes, long DownloadCreditBytes,
    long StorageBytes, long StorageQuota, DateTimeOffset? StorageAddonNextExpiresAt,
    long? PlusDownloadQuotaBytes = null, long? PlusDownloadUsedBytes = null,
    DateTimeOffset? PlusDownloadStartsAt = null, DateTimeOffset? PlusDownloadExpiresAt = null,
    DateTimeOffset? PlusRenewalLimitAt = null, int PendingPlusDays = 0,
    long? PlusEarlyDownloadQuotaBytes = null, long? PlusEarlyDownloadUsedBytes = null,
    DateTimeOffset? PlusEarlyDownloadEndsAt = null,
    long? MetadataDownloadQuotaBytes = null, long? MetadataDownloadUsedBytes = null,
    DateTimeOffset? MetadataDownloadResetsAt = null,
    // A one-time feature trial. The historical cloud fields above are read-only compatibility data.
    bool TrialAvailable = false, bool TrialClaimed = false,
    DateTimeOffset? TrialClaimedAt = null, DateTimeOffset? TrialExpiresAt = null,
    [property: JsonPropertyName("featureExpiresAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? EntitlementExpiresAt = null)
{
    /// <summary>The activation API's expiry is authoritative; old responses retain their original limited period.</summary>
    [JsonIgnore]
    public DateTimeOffset? FeatureExpiresAt => EntitlementExpiresAt ?? ProExpiresAt ?? TrialExpiresAt;
    public bool IsTrialSubscription => MemberEntitlements.EffectiveTier(this, DateTimeOffset.UtcNow) == "trial";
    public bool IsTrialPlus => IsTrialSubscription;
}

public sealed record DeviceFeatureStatus(AccountMembership Membership, AccountDevice[] Devices, Guid CurrentDeviceId)
{
    public bool IsCurrentDeviceActive => Devices.Any(device => device.DeviceId == CurrentDeviceId && device.State == "active");
    public bool CanUseFeatures => Membership.HasLifetimeFeatures && IsCurrentDeviceActive
        && MemberEntitlements.EffectiveTier(Membership, DateTimeOffset.UtcNow) is "lifetime" or "trial";
    // Retained so old JSON callers fail closed rather than gaining removed storage access.
    public bool CanUseAccountCloudSync => false;
}

public sealed class DeviceEnrollmentRequest
{
    public Guid DeviceId { get; init; }
    public string DeviceName { get; init; } = "";
    public string Platform { get; init; } = "";
}

public sealed record MembershipProduct(string Id, string Name, string Kind, int PriceMinor, string Currency,
    int DurationDays, long Bytes, string Description);

public sealed class PurchaseRequest
{
    public string ProductId { get; init; } = "";
    public Guid RequestId { get; init; }
}

public sealed record PurchaseOrder(Guid Id, Guid AccountId, string ProductId, string ProductName, string Kind,
    int PriceMinor, string Currency, int DurationDays, long Bytes, string State,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? FulfilledAt);
