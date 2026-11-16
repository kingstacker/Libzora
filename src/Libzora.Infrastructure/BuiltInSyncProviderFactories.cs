using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>Built-in S3 object-store provider.</summary>
internal sealed class S3SyncProviderFactory : ISyncProviderFactory
{
    public SyncProviderDescriptor Descriptor { get; } = new(
        SyncProvider.S3,
        SyncProviderIds.S3,
        "S3",
        new SyncProviderCapabilities());

    public ISyncObjectStore Create(S3SyncSettings settings) => new S3SyncObjectStore(settings);

    public string BuildStorageIdentity(S3SyncSettings settings) => string.Join(
        "|",
        // Keep the legacy S3 identity unchanged so existing baselines survive
        // an upgrade. A provider, account or remote-root change starts fresh.
        settings.Endpoint,
        settings.Bucket,
        settings.Region,
        settings.Prefix,
        settings.EncryptionKey.Length > 0
            ? $"encrypted:{SyncProviderIdentity.EncryptionKeyFingerprint(settings.EncryptionKey)}"
            : "plain");

    public bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
        left.Endpoint == right.Endpoint && left.Bucket == right.Bucket;
}

/// <summary>Built-in WebDAV provider.</summary>
internal sealed class WebDavSyncProviderFactory : ISyncProviderFactory
{
    public SyncProviderDescriptor Descriptor { get; } = new(
        SyncProvider.WebDav,
        SyncProviderIds.WebDav,
        "WebDAV",
        new SyncProviderCapabilities(ReliableLastModified: false));

    public ISyncObjectStore Create(S3SyncSettings settings) => new WebDavSyncObjectStore(settings);

    public string BuildStorageIdentity(S3SyncSettings settings) => string.Join(
        "|",
        "WebDAV",
        settings.WebDavEndpoint,
        SyncProviderIdentity.EncryptionKeyFingerprint(settings.WebDavUsername),
        settings.Prefix,
        settings.EncryptionKey.Length > 0
            ? $"encrypted:{SyncProviderIdentity.EncryptionKeyFingerprint(settings.EncryptionKey)}"
            : "plain");

    public bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right) =>
        left.WebDavEndpoint == right.WebDavEndpoint && left.WebDavUsername == right.WebDavUsername;
}
