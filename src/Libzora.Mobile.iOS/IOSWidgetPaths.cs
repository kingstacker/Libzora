using Foundation;
using Libzora.Infrastructure;

namespace Libzora.iOSHead;

/// <summary>
/// Resolves the App Group directory shared with the WidgetKit extension. The
/// container only exists when the app is signed with an entitlement and a
/// provisioning profile that both include this group; a profile-less
/// diagnostic build falls back to the app sandbox so publishing still works.
/// </summary>
internal static class IOSWidgetPaths
{
    private static readonly object CacheLock = new();
    private static string? _cachedRoot;
    private static string? _cachedDirectory;

    internal static string Resolve(AppPaths paths)
    {
#if DEBUG
        // Synthetic safety fixtures must never overwrite the user's shared widget snapshot.
        if (NSBundle.MainBundle.BundleIdentifier == "com.kingstacker.libzora.iossafetycheck")
            return Path.Combine(paths.Root, "widgets");
#endif
        lock (CacheLock)
        {
            if (_cachedDirectory is not null
                && string.Equals(_cachedRoot, paths.Root, StringComparison.Ordinal))
            {
                return _cachedDirectory;
            }
        }

        string resolved;
        try
        {
            var container = NSFileManager.DefaultManager.GetContainerUrl(
                WidgetSnapshotPublisher.AppGroupIdentifier);
            if (container?.Path is { Length: > 0 } root)
            {
                resolved = Path.Combine(root, WidgetSnapshotPublisher.FolderName);
            }
            else
            {
#if DEBUG
                Console.WriteLine("Libzora widgets: App Group unavailable; using the app sandbox.");
#endif
                resolved = Path.Combine(paths.Root, "widgets");
            }
        }
        catch (Exception)
        {
            // The entitlement is missing or the profile does not authorize it.
            resolved = Path.Combine(paths.Root, "widgets");
        }

        lock (CacheLock)
        {
            _cachedRoot = paths.Root;
            _cachedDirectory = resolved;
        }
        return resolved;
    }
}
