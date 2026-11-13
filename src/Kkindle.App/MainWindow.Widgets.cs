using System.Runtime.InteropServices;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Keeps the shared WidgetKit snapshot in step with the desktop library. The
/// widget extension reads plain JSON and JPEG thumbnails from the App Group
/// container; it has no database access of its own.
/// </summary>
public partial class MainWindow
{
    private WidgetSnapshotCoordinator? _widgetSnapshots;

    private void InitializeWidgetSnapshots()
    {
        if (_widgetSnapshots is not null || !OperatingSystem.IsMacOS()) return;
        var sharedRoot = WidgetSharedRoot();
        if (sharedRoot is null) return;
        _widgetSnapshots = new WidgetSnapshotCoordinator(
            token => WidgetSnapshotPublisher.PublishAsync(
                _paths,
                _library,
                _readerData,
                _appSettings.ColorfulMode,
                0,
                sharedRoot,
                token),
            published: ReloadMacWidgets);
        _widgetSnapshots.Schedule();
    }

    private static string? WidgetSharedRoot()
    {
        // FileManager.containerURL needs a signed app group; the non-sandboxed
        // desktop app can still create and write the standard container path.
        // A sandboxed widget extension resolves the same directory through its
        // com.apple.security.application-groups entitlement.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return null;
        return Path.Combine(home, "Library", "Group Containers",
            WidgetSnapshotPublisher.AppGroupIdentifier, WidgetSnapshotPublisher.FolderName);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReloadWidgetsDelegate();

    private static ReloadWidgetsDelegate? _reloadWidgets;

    private static void ReloadMacWidgets()
    {
        try
        {
            if (_reloadWidgets is null)
            {
                // Load by explicit path: the dylib sits next to the apphost in
                // both single-architecture and universal payloads.
                var library = NativeLibrary.Load(
                    Path.Combine(AppContext.BaseDirectory, "libKkindleWidgetBridge.dylib"));
                _reloadWidgets = Marshal.GetDelegateForFunctionPointer<ReloadWidgetsDelegate>(
                    NativeLibrary.GetExport(library, "kk_macos_reload_widgets"));
            }
            _reloadWidgets();
        }
        catch (Exception)
        {
            // The bridge ships only in release bundles. Development builds
            // fall back to WidgetKit's scheduled refresh.
        }
    }
}
