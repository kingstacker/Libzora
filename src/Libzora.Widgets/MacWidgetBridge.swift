import WidgetKit

/// Loaded by the managed desktop app through P/Invoke. A WidgetKit extension
/// cannot be reloaded from another process, but the containing app may ask
/// WidgetKit to refresh all of its timelines after writing a new snapshot.
@_cdecl("kk_macos_reload_widgets")
public func kkMacOSReloadWidgets() {
    if #available(macOS 11.0, *) {
        WidgetCenter.shared.reloadAllTimelines()
    }
}
