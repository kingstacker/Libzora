enum NativeWidgetNavigationPolicy {
    static func shouldAnimateTab(currentTab: Int, targetTab: Int, readerVisible: Bool,
                                 libraryReady: Bool, reduceMotion: Bool, powerSaving: Bool) -> Bool {
        libraryReady && !reduceMotion && !powerSaving && (currentTab != targetTab || readerVisible)
    }
}
