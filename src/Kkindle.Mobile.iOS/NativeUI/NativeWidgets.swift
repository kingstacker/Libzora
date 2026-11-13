import WidgetKit

/// Called after the managed app writes a new App Group snapshot. WidgetKit
/// reloads all Kkindle timelines so the home screen reflects a just-finished
/// book or a newly selected colourful-mode setting without waiting for the
/// next scheduled refresh.
@_cdecl("kk_ios_reload_widgets")
public func kkReloadWidgets() {
    if #available(iOS 14.0, *) {
        WidgetCenter.shared.reloadAllTimelines()
    }
}
