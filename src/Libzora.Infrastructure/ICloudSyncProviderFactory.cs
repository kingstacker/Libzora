using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>Registered only by Apple platform heads with an entitled file system.</summary>
public sealed class ICloudSyncProviderFactory(IICloudSyncFileSystem fileSystem)
    : ISyncProviderFactory, ISyncProviderAccountBinding
{
    public SyncProviderDescriptor Descriptor { get; } = new(
        SyncProvider.ICloud,
        SyncProviderIds.ICloud,
        "iCloud",
        // iCloud Drive is eventually consistent, not an authoritative remote
        // listing. Never retire snapshots or collect blobs from its local view.
        new SyncProviderCapabilities(SupportsRangeRead: false, ReliableListing: false,
            ReliableLastModified: false, MaxConcurrentRequests: 1));

    public S3SyncSettings BindAccount(S3SyncSettings settings, bool allowAccountChange = false)
    {
        settings = S3SyncSettings.Normalize(settings);
        var identity = fileSystem.AccountIdentity;
        if (string.IsNullOrEmpty(identity))
            throw new InvalidOperationException(UiText.Get("请在系统设置中登录 Apple 账户并开启 iCloud 云盘。"));
        if (!allowAccountChange && settings.ICloudAccountIdentity.Length > 0
            && !string.Equals(settings.ICloudAccountIdentity, identity, StringComparison.Ordinal))
            throw new InvalidOperationException(UiText.Get("系统 iCloud 账户已更改。请确认当前 Apple 账户后重新保存同步设置。"));
        return settings with { ICloudAccountIdentity = identity };
    }

    public ISyncObjectStore Create(S3SyncSettings settings) =>
        new ICloudSyncObjectStore(fileSystem, BindAccount(settings));

    public string BuildStorageIdentity(S3SyncSettings settings) => string.Join(
        "|", "iCloud", fileSystem.ContainerIdentifier,
        settings.ICloudAccountIdentity.Length > 0
            ? settings.ICloudAccountIdentity : "unbound",
        settings.Prefix,
        settings.EncryptionKey.Length > 0
            ? $"encrypted:{SyncProviderIdentity.EncryptionKeyFingerprint(settings.EncryptionKey)}" : "plain");

    public bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
        string.Equals(left.ICloudAccountIdentity, right.ICloudAccountIdentity, StringComparison.Ordinal);
}
