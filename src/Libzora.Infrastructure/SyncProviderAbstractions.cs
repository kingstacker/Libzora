using System.Security.Cryptography;
using System.Text;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Stable, user-visible identifiers for built-in sync stores. The wire/config
/// format still persists <see cref="SyncProvider"/> for compatibility; these
/// ids are the extension point for a future connection model that can carry
/// more than the built-in enum.
/// </summary>
public static class SyncProviderIds
{
    public const string S3 = "s3";
    public const string WebDav = "webdav";
    public const string ICloud = "icloud";

    public static string FromProvider(SyncProvider provider) => provider switch
    {
        SyncProvider.S3 => S3,
        SyncProvider.WebDav => WebDav,
        SyncProvider.ICloud => ICloud,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), "不支持的同步方式。")
    };
}

/// <summary>
/// Transport capabilities a provider can honestly promise. Capabilities may
/// only degrade performance or destructive maintenance; they must never change
/// snapshot merge semantics.
/// </summary>
/// <param name="SupportsRangeRead">
/// Range reads are available. A provider that returns a full stream instead is
/// still usable; resume attempts simply restart from zero.
/// </param>
/// <param name="SupportsAtomicReplace">
/// Replacing an existing key is atomic for readers. Providers without this
/// guarantee need a staging/pointer or versioned-snapshot strategy before they
/// can be enabled for production.
/// </param>
/// <param name="ReliableListing">
/// A successful listing is complete and only contains published objects. When
/// false the engine confirms object existence individually and disables remote
/// snapshot retirement and object garbage collection.
/// </param>
/// <param name="ReliableLastModified">
/// Listing metadata changes only when the object changes. When false the
/// snapshot cache is disabled and snapshots are re-read.
/// </param>
/// <param name="MaxConcurrentRequests">Optional provider-side concurrency cap.</param>
/// <param name="MaxObjectBytes">Optional per-object size limit; reserved.</param>
public sealed record SyncProviderCapabilities(
    bool SupportsRangeRead = true,
    bool SupportsAtomicReplace = true,
    bool ReliableListing = true,
    bool ReliableLastModified = true,
    int? MaxConcurrentRequests = null,
    long? MaxObjectBytes = null);

/// <summary>
/// Declarative UI hints exposed by each provider so desktop and mobile settings
/// forms do not need to hardcode provider enum checks.
/// </summary>
public sealed record SyncProviderUiHints(
    bool SupportsProfileImportExport = true,
    bool RequiresInteractiveAuth = false,
    bool SupportsClientSideEncryption = true);

/// <summary>
/// Everything the engine and future settings UI need to know about a provider
/// without referencing its implementation.
/// </summary>
public sealed record SyncProviderDescriptor(
    SyncProvider Provider,
    string Id,
    string DisplayName,
    SyncProviderCapabilities Capabilities,
    SyncProviderUiHints? UiHints = null)
{
    public SyncProviderUiHints EffectiveUiHints => UiHints ?? new SyncProviderUiHints();
}

/// <summary>
/// Creates a storage adapter for one provider. Implementations must not merge
/// snapshots or infer deletions; that remains the engine's responsibility.
/// </summary>
public interface ISyncProviderFactory
{
    SyncProviderDescriptor Descriptor { get; }

    /// <summary>Creates a store for one sync run. The caller owns disposal.</summary>
    ISyncObjectStore Create(S3SyncSettings settings);

    /// <summary>
    /// Provider-specific part of the local baseline identity. The engine adds
    /// the data-region prefix. This string must stay stable for an existing
    /// remote or the next sync starts a fresh baseline.
    /// </summary>
    string BuildStorageIdentity(S3SyncSettings settings);

    /// <summary>
    /// Whether two settings point at the same remote directory. Used to refuse
    /// an encryption-key change against an already-published directory.
    /// </summary>
    bool IsSameRemoteDirectory(S3SyncSettings left, S3SyncSettings right);
}

/// <summary>
/// Optional system-account binding. Explicit settings saves may rebind; a
/// transfer must refuse a different account rather than publish a library to it.
/// </summary>
public interface ISyncProviderAccountBinding
{
    S3SyncSettings BindAccount(S3SyncSettings settings, bool allowAccountChange = false);
}

/// <summary>
/// Optional provider-owned validation and normalization. Future cloud drives and
/// official servers implement this instead of adding switches to <see cref="S3SyncSettings"/>.
/// </summary>
public interface ISyncProviderValidator
{
    string? Validate(S3SyncSettings settings);
    S3SyncSettings Normalize(S3SyncSettings settings) => settings;
}

/// <summary>
/// Optional interactive OAuth / token refresh contract for future cloud drives
/// (OneDrive, Google Drive, Dropbox) or a self-hosted official sync server.
/// </summary>
public interface ISyncProviderAuthHandler
{
    Task<S3SyncSettings> RefreshAuthAsync(S3SyncSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Provider registration point. The default registry contains only the
/// built-in S3 and WebDAV stores; platform heads can pass a registry with
/// additional factories through <c>ApplicationModuleOptions.SyncProviders</c>.
/// Future providers may register under <see cref="SyncProvider.Custom"/> with
/// distinct string <see cref="SyncProviderDescriptor.Id"/> values.
/// </summary>
public sealed class SyncProviderRegistry
{
    private readonly Dictionary<SyncProvider, ISyncProviderFactory> _factories = [];
    private readonly Dictionary<string, ISyncProviderFactory> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates a registry with the built-in S3 and WebDAV providers.</summary>
    public static SyncProviderRegistry CreateDefault()
    {
        var registry = new SyncProviderRegistry();
        registry.Register(new S3SyncProviderFactory());
        registry.Register(new WebDavSyncProviderFactory());
        return registry;
    }

    /// <summary>Registers a provider factory. Registration is idempotent only for distinct providers.</summary>
    /// <returns>This registry, for composition-time chaining.</returns>
    public SyncProviderRegistry Register(ISyncProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var provider = factory.Descriptor.Provider;
        if (provider == SyncProvider.AccountServer)
            throw new ArgumentException(MemberEntitlements.AccountCloudRequirement, nameof(factory));
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(factory), "同步方式必须是已定义的 SyncProvider 值。");
        var id = factory.Descriptor.Id?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("同步方式缺少稳定的标识。", nameof(factory));

        lock (_gate)
        {
            if (_byId.ContainsKey(id))
                throw new InvalidOperationException($"同步方式标识 {id} 已注册。");
            if (provider != SyncProvider.Custom && !_factories.TryAdd(provider, factory))
                throw new InvalidOperationException($"同步方式 {provider} 已注册。");
            _byId[id] = factory;
        }
        return this;
    }

    public bool TryGet(SyncProvider provider, out ISyncProviderFactory factory)
    {
        lock (_gate)
        {
            if (_factories.TryGetValue(provider, out var found))
            {
                factory = found;
                return true;
            }
        }
        factory = null!;
        return false;
    }

    public bool TryGetById(string? id, out ISyncProviderFactory factory)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            lock (_gate)
            {
                if (_byId.TryGetValue(id.Trim(), out var found))
                {
                    factory = found;
                    return true;
                }
            }
        }
        factory = null!;
        return false;
    }

    /// <summary>Resolves the factory for either a built-in provider or a string-keyed custom/official provider.</summary>
    public bool TryGet(S3SyncSettings settings, out ISyncProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Provider == SyncProvider.Custom
            ? TryGetById(settings.CustomProviderId, out factory)
            : TryGet(settings.Provider, out factory);
    }

    public ISyncProviderFactory Get(SyncProvider provider) =>
        TryGet(provider, out var factory)
            ? factory
            : throw new NotSupportedException($"同步方式 {provider} 尚未注册。");

    public ISyncProviderFactory Get(S3SyncSettings settings) =>
        TryGet(settings, out var factory)
            ? factory
            : throw new NotSupportedException($"同步方式 {settings.ProviderName} 尚未注册。");

    /// <summary>Registered descriptors, ordered by their persisted provider value then stable id.</summary>
    public IReadOnlyList<SyncProviderDescriptor> Descriptors
    {
        get
        {
            lock (_gate)
                return _byId.Values.Select(factory => factory.Descriptor)
                    .OrderBy(descriptor => descriptor.Provider)
                    .ThenBy(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
    }
}

/// <summary>Provider-independent helpers for local baseline identities.</summary>
internal static class SyncProviderIdentity
{
    public static string EncryptionKeyFingerprint(string encryptionKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey))).ToLowerInvariant();
}
