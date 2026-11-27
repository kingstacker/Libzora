/// Every link supersedes the previous intent, even if its destination is
/// already visible. Async work may execute only for its captured revision.
struct NativeWidgetNavigationIntent {
    enum Destination: Equatable {
        case tab(Int)
        case book(String)
    }
    private(set) var revision = 0
    private(set) var destination: Destination?
    private(set) var presentedRevision: Int?

    mutating func begin(_ destination: Destination) -> Int {
        revision += 1
        self.destination = destination
        return revision
    }

    func isCurrent(_ expected: Int) -> Bool { revision == expected }

    @discardableResult
    mutating func markPresented(_ expected: Int) -> Bool {
        guard isCurrent(expected) else { return false }
        presentedRevision = expected
        return true
    }

    var hasPresentedCurrent: Bool { presentedRevision == revision }

    var isTabPresentationPending: Bool {
        if case .some(.tab(_)) = destination { return !hasPresentedCurrent }
        return false
    }
}

enum NativeWidgetNavigationPolicy {
    /// Backend opening/closing flags are intentionally not presentation gates.
    /// Tabs need only a prepared shell and an actually dismissed reader modal.
    static func canPresentTab(libraryPreparing: Bool, readerVisible: Bool) -> Bool {
        !libraryPreparing && !readerVisible
    }

    static func shouldAnimateTab(currentTab: Int, targetTab: Int, readerVisible: Bool,
                                 libraryReady: Bool, reduceMotion: Bool, powerSaving: Bool) -> Bool {
        libraryReady && !reduceMotion && !powerSaving && (currentTab != targetTab || readerVisible)
    }

    /// Only cold launches (or an interrupted launch restore) enter through the
    /// Libzora icon page; warm widget taps slide directly from the current view.
    static func shouldUseLaunchBrandCover(preparingLibrary: Bool, libraryLoaded: Bool,
                                          restoringReader: Bool) -> Bool {
        preparingLibrary || !libraryLoaded || restoringReader
    }

    /// Warm widget book opens arriving while waking from background must wait
    /// until the live foreground scene commits before snapshotting or mounting
    /// the reader; otherwise the snapshot captures a blank white surface.
    static func shouldAwaitForegroundBeforeBookOpen(isWidgetSource: Bool,
                                                    useBrandCover: Bool,
                                                    isApplicationActive: Bool) -> Bool {
        isWidgetSource && !useBrandCover && !isApplicationActive
    }
}
