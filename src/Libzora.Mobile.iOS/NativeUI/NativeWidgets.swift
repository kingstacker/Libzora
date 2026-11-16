import WidgetKit

/// Called after the managed app writes a new App Group snapshot. WidgetKit
/// reloads all Libzora timelines so the home screen reflects a just-finished
/// book or a newly selected colourful-mode setting without waiting for the
/// next scheduled refresh.
@_cdecl("lz_ios_reload_widgets")
public func lzReloadWidgets() {
    if #available(iOS 14.0, *) {
        WidgetCenter.shared.reloadAllTimelines()
    }
}
