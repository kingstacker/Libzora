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
        check(NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
            preparingLibrary: true, libraryLoaded: false, restoringReader: false),
            "Cold widget launch enters through the icon brand cover")
        check(NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
            preparingLibrary: false, libraryLoaded: false, restoringReader: false),
            "Unloaded shelf widget launch still uses the icon brand cover")
        check(NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
            preparingLibrary: false, libraryLoaded: true, restoringReader: true),
            "Interrupted launch restore still uses the icon brand cover")
        check(!NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
            preparingLibrary: false, libraryLoaded: true, restoringReader: false),
            "Warm widget tap slides directly from the current page without the icon cover")
        var navigation = NativeWidgetNavigationIntent()
        let dashboardRequest = navigation.begin(.tab(2))
        check(navigation.isCurrent(dashboardRequest) && navigation.isTabPresentationPending,
              "A new dashboard intent is pending until its actual slide completes")
        let samePageRequest = navigation.begin(.tab(0))
        check(!navigation.isCurrent(dashboardRequest) && navigation.destination == .tab(0),
              "A same-page link cancels the previous different destination rather than being ignored")
        check(!navigation.markPresented(dashboardRequest) && navigation.isTabPresentationPending,
              "A stale completion cannot release a newer page's presentation barrier")
        check(navigation.markPresented(samePageRequest) && !navigation.isTabPresentationPending,
              "An already visible page releases its barrier without replaying an animation")
        let bookRequest = navigation.begin(.book("b1"))
        check(!navigation.isCurrent(samePageRequest) && !navigation.isTabPresentationPending,
              "A book link supersedes tabs without waiting for a tab-only handoff")
        let latest = navigation.begin(.tab(1))
        check(!navigation.markPresented(bookRequest) && navigation.isCurrent(latest),
              "The latest materials link wins over an older book callback")
        check(NativeWidgetNavigationPolicy.canPresentTab(libraryPreparing: false, readerVisible: false),
              "A dismissed reader can reveal a tab while its backend is still opening or closing")
        check(!NativeWidgetNavigationPolicy.canPresentTab(libraryPreparing: true, readerVisible: false)
                && !NativeWidgetNavigationPolicy.canPresentTab(libraryPreparing: false, readerVisible: true),
              "Neither an unprepared shell nor a live modal can be exposed prematurely")
        print("Passed \(assertions) reader presentation and widget navigation assertions.")
    }
}
