import Foundation

@main
struct NativeReaderPresentationLogicTests {
    static func main() {
        var assertions = 0
        func check(_ condition: Bool, _ message: String) {
            assertions += 1
            if !condition { fatalError(message) }
        }
        var entry = NativeReaderPresentationPolicy()
        check(!entry.begin(ready: false, frameReady: false, frameRevision: 0,
            acknowledgedRevision: nil, hasError: false, sessionIsCurrent: true), "A mounted reader is not a rendered page")
        check(!entry.begin(ready: true, frameReady: false, frameRevision: 1,
            acknowledgedRevision: 1, hasError: false, sessionIsCurrent: true), "Ready alone must not expose blank paper")
        check(!entry.begin(ready: true, frameReady: true, frameRevision: 1,
            acknowledgedRevision: 2, hasError: false, sessionIsCurrent: true), "Old viewport frames cannot reveal a new layout")
        check(!entry.begin(ready: true, frameReady: true, frameRevision: 2,
            acknowledgedRevision: 2, hasError: false, sessionIsCurrent: false), "Canceled sessions cannot reveal")
        check(entry.begin(ready: true, frameReady: true, frameRevision: 2,
            acknowledgedRevision: 2, hasError: false, sessionIsCurrent: true), "A drawn and acknowledged current viewport can reveal")
        check(!entry.begin(ready: true, frameReady: true, frameRevision: 3,
            acknowledgedRevision: 3, hasError: false, sessionIsCurrent: true), "A later reflow cannot replay the entry animation")
        var failed = NativeReaderPresentationPolicy()
        check(failed.begin(ready: false, frameReady: false, frameRevision: 0,
            acknowledgedRevision: nil, hasError: true, sessionIsCurrent: true), "Errors must release the entry cover without waiting for rendering")
        var zero = NativeReaderPresentationPolicy()
        check(!zero.begin(ready: true, frameReady: true, frameRevision: 0,
            acknowledgedRevision: 0, hasError: false, sessionIsCurrent: true), "An unarmed frame is not valid")
        check(NativeWidgetNavigationPolicy.shouldAnimateTab(currentTab: 0, targetTab: 2,
            readerVisible: false, libraryReady: true, reduceMotion: false, powerSaving: false), "Reading tree/stat widgets slide to dashboard")
        check(!NativeWidgetNavigationPolicy.shouldAnimateTab(currentTab: 2, targetTab: 2,
            readerVisible: false, libraryReady: true, reduceMotion: false, powerSaving: false), "Same-page widget navigation must not replay")
        check(NativeWidgetNavigationPolicy.shouldAnimateTab(currentTab: 2, targetTab: 2,
            readerVisible: true, libraryReady: true, reduceMotion: false, powerSaving: false), "A reader covering the dashboard still needs a page handoff")
        check(!NativeWidgetNavigationPolicy.shouldAnimateTab(currentTab: 0, targetTab: 2,
            readerVisible: false, libraryReady: true, reduceMotion: true, powerSaving: false), "Tab navigation respects Reduce Motion")
        check(!NativeWidgetNavigationPolicy.shouldAnimateTab(currentTab: 0, targetTab: 2,
            readerVisible: false, libraryReady: false, reduceMotion: false, powerSaving: false), "A cold launch must not flash a shelf to animate it out")
        check(NativeReaderPresentationPolicy.widgetTransitionDuration <= 0.35, "Widget navigation stays snappy")
        print("Passed \(assertions) reader presentation and widget navigation assertions.")
    }
}
