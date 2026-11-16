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
    internal static string Resolve(AppPaths paths)
    {
        try
        {
            var container = NSFileManager.DefaultManager.GetContainerUrl(
                WidgetSnapshotPublisher.AppGroupIdentifier);
            if (container?.Path is { Length: > 0 } root)
                return Path.Combine(root, WidgetSnapshotPublisher.FolderName);
        }
        catch (Exception)
        {
            // The entitlement is missing or the profile does not authorize it.
        }
#if DEBUG
        Console.WriteLine("Libzora widgets: App Group unavailable; using the app sandbox.");
#endif
        return Path.Combine(paths.Root, "widgets");
    }
}
