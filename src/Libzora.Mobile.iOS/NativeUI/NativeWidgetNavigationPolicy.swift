enum NativeWidgetNavigationPolicy {
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
}
