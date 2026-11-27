import SwiftUI
import UIKit

/// A modal identifies the reading session, not just the book. SwiftUI can
/// otherwise reuse a same-book reader with a stale appearance/inset session.
struct NativeReaderPresentation: Identifiable {
    let id: String
    let book: LibraryBook
    let animation: NativeBookOpenAnimation
}

struct NativeReaderOpenRequest {
    let book: LibraryBook
    let location: [String: Any]?
    let animated: Bool
    let sourceFrame: CGRect?
    let sourceKey: String?
    let source: NativeBookOpenSource
    let bookFileId: String?
}

@MainActor
extension NativeStore {
    /// A widget can deep-link into a book before the shelf has loaded, or
    /// while another book is open. Keep the request until a fresh library
    /// snapshot can resolve it and the reader is free.
    func openRequestedBook(_ bookId: String, source: NativeBookOpenSource) {
        let duplicate = NativeReaderOpenPolicy.isDuplicate(
            bookId: bookId, openingBookId: openingReaderBookId, openingGeneration: openingReaderGeneration,
            currentGeneration: readerOpenGeneration, readerBookId: readerBook?.id,
            closing: closingReader, hasPending: pendingOpenBookId != nil || queuedReaderOpen.pending != nil
        )
        // Repeated taps on the same active book should not cut its current
        // entry flight, but a pending tab intent still has to be superseded.
        if duplicate, widgetNavigationIntent.destination == .book(bookId),
           requestedTab == nil, (widgetTabTask == nil || awaitingWidgetBookForeground) { return }
        let revision = beginWidgetNavigation(.book(bookId))
        pendingReaderRestore = nil
        if duplicate { return }
        let useBrandCover = NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
            preparingLibrary: preparingLibrary,
            libraryLoaded: libraryLoaded,
            restoringReader: restoringReader
        )
        if canOpenReader && useBrandCover {
            beginLaunchBrandCover(resetTimer: false)
            pendingWidgetReaderSnapshot = nil
            restoringReader = true
        } else if !useBrandCover {
            dismissLaunchBrandCover()
            pendingWidgetReaderSnapshot = nil
            dismissWidgetEntrySnapshotCover()
        }
        readerEntrySnapshot = pendingWidgetReaderSnapshot
        queuedReaderOpen.clear()
        pendingOpenBookId = bookId
        pendingOpenSource = source
        pendingBookRequiredRevision = libraryRefreshRevision > appliedLibraryRevision
            ? libraryRefreshRevision : libraryRefreshRevision + 1
        if preparingLibrary || (source == .widget && !libraryLoaded) {
            restoringReader = true
        } else if openingReader {
            readerOpenGeneration += 1
        } else if libraryLoaded {
            if NativeWidgetNavigationPolicy.shouldAwaitForegroundBeforeBookOpen(
                isWidgetSource: source == .widget,
                useBrandCover: useBrandCover,
                isApplicationActive: UIApplication.shared.applicationState == .active
            ) {
                awaitingWidgetBookForeground = true
                widgetTabTask = Task { @MainActor [weak self] in
                    guard let self else { return }
                    defer {
                        if self.widgetNavigationIntent.isCurrent(revision) {
                            self.widgetTabTask = nil
                            self.awaitingWidgetBookForeground = false
                        }
                    }
                    await self.waitForWidgetForegroundCommit()
                    guard !Task.isCancelled, self.widgetNavigationIntent.isCurrent(revision) else { return }
                    self.awaitingWidgetBookForeground = false
                    self.openPendingReaderIfNeeded()
                }
            } else {
                openPendingReaderIfNeeded()
            }
        } else {
            perform { try await self.refreshLibrary() }
        }
    }

    func readerPresentationAppeared(for sessionId: String) {
        if readerPresentation?.id == sessionId { readerPresentationActive = true }
    }

    func readerPresentationDismissed() {
        readerPresentationActive = false
        openPendingReaderIfNeeded()
        openQueuedReaderIfNeeded()
    }

    /// Invalidates an in-flight reader open and dismisses a visible cover.
    func dismissReaderForTab() {
        queuedReaderOpen.clear()
        readerOpenGeneration += 1
        restoringReader = false
        if openingReader {
            openingBookReadyId = nil
            openingBookTransition = nil
            dismissReaderPresentation()
        } else if readerBook != nil {
            perform { await self.closeReader(animated: false, preserveWidgetCover: true) }
        }
    }

    func openPendingReaderIfNeeded() {
        guard canOpenReader else { restoringReader = false; return }
        guard let bookId = pendingOpenBookId, libraryLoaded, !preparingLibrary,
              !awaitingWidgetBookForeground,
              txtReview == nil, !txtPresentationActive,
              !openingReader, !closingReader, !restartRequired else { return }
        // When a reader is already open, let `closeReader` capture the real
        // rendered Metal page before mounting the cover overlay; snapshotting
        // the window early would capture a blank white Metal surface and flash.
        if readerBook == nil && pendingOpenSource == .widget && !launchBrandActive && widgetEntrySnapshotCover == nil {
            beginWidgetEntrySnapshotCover()
        }
        if readerBook != nil {
            perform {
                guard self.pendingOpenBookId != nil else { return }
                await self.closeReader(animated: false, preservePendingOpen: true)
            }
            return
        }
        guard !readerPresentationActive else { return }
        let book = library.first(where: { $0.id == bookId })
        switch NativeBookLinkResolution.decide(found: book != nil, snapshotRevision: appliedLibraryRevision,
                                              requiredRevision: pendingBookRequiredRevision) {
        case .refresh:
            guard pendingBookRefreshTask == nil else { return }
            pendingBookRefreshTask = Task {
                var refreshed = false
                do { try await refreshLibrary(); refreshed = true }
                catch {
                    if pendingOpenBookId == bookId {
                        readerEntrySnapshot = nil
                        pendingWidgetReaderSnapshot = nil
                        pendingOpenBookId = nil
                        dismissLaunchBrandCover()
                        dismissWidgetEntrySnapshotCover()
                        restoringReader = false
                        if !Task.isCancelled { failure = error.localizedDescription }
                    }
                }
                pendingBookRefreshTask = nil
                // A newer link may require a snapshot newer than this refresh.
                if refreshed || pendingOpenBookId != nil && pendingOpenBookId != bookId { openPendingReaderIfNeeded() }
            }
        case .missing:
            readerEntrySnapshot = nil
            pendingWidgetReaderSnapshot = nil
            pendingOpenBookId = nil
            dismissLaunchBrandCover()
            dismissWidgetEntrySnapshotCover()
            restoringReader = false
            showToast(L("书籍已不存在。"))
        case .open:
            guard let book else { return }
            let revision = queuedReaderOpen.revision
            perform {
                guard self.queuedReaderOpen.revision == revision,
                      self.pendingOpenBookId == bookId, self.readerBook == nil,
                      !self.readerPresentationActive, !self.openingReader, !self.closingReader else { return }
                let source = self.pendingOpenSource
                self.pendingOpenBookId = nil
                try await self.openReader(book, source: source)
            }
        }
    }

    /// Ordinary cover/material taps retain their full location and animation
    /// intent while the old reader or its modal presentation is still closing.
    func openQueuedReaderIfNeeded() {
        guard canOpenReader, txtReview == nil, !txtPresentationActive,
              queuedReaderOpen.pending != nil, !openingReader, !closingReader, !restartRequired else { return }
        if readerBook != nil {
            let revision = queuedReaderOpen.revision
            perform {
                guard self.queuedReaderOpen.revision == revision, self.queuedReaderOpen.pending != nil else { return }
                await self.closeReader(animated: false, preservePendingOpen: true)
            }
            return
        }
        guard !readerPresentationActive else { return }
        let revision = queuedReaderOpen.revision
        perform {
            guard !self.openingReader, !self.closingReader, !self.readerPresentationActive,
                  self.readerBook == nil,
                  let request = self.queuedReaderOpen.take(revision: revision) else { return }
            try await self.openReader(request.book, location: request.location, animated: request.animated,
                                      sourceFrame: request.sourceFrame, sourceKey: request.sourceKey,
                                      source: request.source, bookFileId: request.bookFileId)
        }
    }

    func restorePendingReaderIfNeeded() {
        guard canOpenReader else { restoringReader = false; return }
        guard let bookId = pendingReaderRestore,
              txtReview == nil, !txtPresentationActive,
              readerBook == nil, !openingReader, !restartRequired,
              pendingOpenBookId == nil else { return }
        guard !library.isEmpty else {
            // Keep waiting for a populated snapshot, but never trap the user
            // behind the launch cover on a shelf that is still loading.
            restoringReader = false
            return
        }
        pendingReaderRestore = nil
        guard let book = library.first(where: { $0.id == bookId }) else {
            UserDefaults.standard.removeObject(forKey: Self.lastReaderKey)
            restoringReader = false
            return
        }
        // Outlive a canceled refresh, but never a newer explicit navigation.
        let generation = readerOpenGeneration
        let revision = queuedReaderOpen.revision
        Task {
            guard self.readerOpenGeneration == generation, self.queuedReaderOpen.revision == revision,
                  self.pendingOpenBookId == nil, !self.closingReader else { return }
            try? await self.openReader(book, animated: true, source: .restore)
        }
    }

    /// A failed first shelf load must not leave the launch cover on screen.
    func releaseLaunchRestore() {
        if readerBook == nil && !openingReader { restoringReader = false }
    }

    func isBookCoverHidden(forKey key: String) -> Bool {
        NativeBookOpenTransitionPolicy.shouldHideSourceCover(
            openingSourceKey: openingBookTransition?.sourceKey,
            readerPresentationActive: readerPresentationActive,
            closingSourceKey: closingBookTransition?.sourceKey,
            forKey: key
        )
    }

    func finishClosingBookTransition(id: UUID) {
        guard closingBookTransition?.id == id else { return }
        closingBookTransition = nil
        let handoff = closingWindowTransition
        Task { @MainActor in
            // SwiftUI unhides the real cover on its next display commit. Keep
            // the landed replacement over it until that commit, avoiding a hole.
            await NativeDisplayFrame.waitForCommit()
            if closingWindowTransition === handoff { closingWindowTransition = nil }
            handoff?.finish()
            closingTransitionWaiters.removeValue(forKey: id)?.resume()
        }
    }

    private func clearClosingBookTransition() {
        closingWindowTransition?.finish()
        closingWindowTransition = nil
        closingBookTransition = nil
        let waiters = closingTransitionWaiters
        closingTransitionWaiters.removeAll()
        for waiter in waiters.values {
            waiter.resume()
        }
    }

    private func awaitClosingBookTransition(id: UUID, maxWait: Double) async {
        guard closingBookTransition?.id == id else { return }
        await withCheckedContinuation { continuation in
            guard closingBookTransition?.id == id else {
                continuation.resume()
                return
            }
            closingTransitionWaiters[id] = continuation
            DispatchQueue.main.asyncAfter(deadline: .now() + maxWait) { [weak self] in
                self?.finishClosingBookTransition(id: id)
            }
        }
    }

    private func seriesFrontBookId(for book: LibraryBook) -> String? {
        guard preferences.stackBookSeries else { return nil }
        if librarySeriesCache == nil { librarySeriesCache = NativeBookSeries.groups(library) }
        guard let series = librarySeriesCache?.values.first(where: { $0.books.contains(where: { $0.id == book.id }) }) else {
            return nil
        }
        return frontBook(for: series)?.id
    }

    private func resolveTransitionCoverImage(for book: LibraryBook, preferredWidth: CGFloat) async -> UIImage? {
        guard let path = book.coverPath else { return nil }
        let targetPixels = max(360, Int(min(max(preferredWidth, 160), 320) * 3))
        if let exact = NativeCoverCache.cachedImage(path: path, version: book.updatedAt, pixels: targetPixels) {
            return exact
        }
        if let cached = NativeCoverCache.bestCachedImage(path: path, version: book.updatedAt) {
            if max(cached.size.width, cached.size.height) < CGFloat(targetPixels) * 0.7 {
                let version = book.updatedAt
                Task { _ = await NativeCoverCache.image(path: path, version: version, pixels: targetPixels) }
            }
            return cached
        }
        return await NativeCoverCache.image(path: path, version: book.updatedAt, pixels: targetPixels)
    }

    func openReader(_ book: LibraryBook, location: [String: Any]? = nil,
                    animated: Bool = true, sourceFrame: CGRect? = nil,
                    sourceKey: String? = nil, source: NativeBookOpenSource = .other,
                    bookFileId: String? = nil) async throws {
        guard canOpenReader && txtReview == nil && !txtPresentationActive else {
            queuedReaderOpen.enqueue(NativeReaderOpenRequest(book: book, location: location, animated: animated,
                sourceFrame: sourceFrame, sourceKey: sourceKey, source: source, bookFileId: bookFileId))
            restoringReader = false
            return
        }
        if source != .widget, source != .restore, widgetNavigationIntent.isTabPresentationPending {
            beginWidgetNavigation(.book(book.id))
        }
        if openingReader || closingReader || readerPresentationActive || readerBook != nil {
            if NativeReaderOpenPolicy.isDuplicate(
                bookId: book.id, openingBookId: openingReaderBookId, openingGeneration: openingReaderGeneration,
                currentGeneration: readerOpenGeneration, readerBookId: readerBook?.id,
                closing: closingReader, hasPending: pendingOpenBookId != nil || queuedReaderOpen.pending != nil
            ), location == nil && bookFileId == nil { return }
            pendingReaderRestore = nil
            pendingOpenBookId = nil
            queuedReaderOpen.enqueue(NativeReaderOpenRequest(
                book: book, location: location, animated: animated, sourceFrame: sourceFrame,
                sourceKey: sourceKey, source: source, bookFileId: bookFileId))
            if openingReader { readerOpenGeneration += 1 }
            openQueuedReaderIfNeeded()
            return
        }
        pendingReaderRestore = nil
        pendingOpenBookId = nil
        queuedReaderOpen.clear()
        openingReader = true
        openingReaderBookId = book.id
        let generation = readerOpenGeneration
        openingReaderGeneration = generation
        defer {
            openingReaderBookId = nil
            openingReaderGeneration = nil
            openingReader = false
            openPendingReaderIfNeeded()
            openQueuedReaderIfNeeded()
        }
        UIApplication.shared.sendAction(#selector(UIResponder.resignFirstResponder), to: nil, from: nil, for: nil)
        NativeBookPreviewCenter.shared.dismiss(immediate: true)
        if source == .widget {
            NativeBookSeriesCenter.shared.dismiss()
        }
        session.reset()
        session.appearanceSessionId = UUID().uuidString
        session.appearanceRevision = 0
        session.appearanceColorScheme = nil
        var args: [String: Any] = ["bookId": book.id, "sessionId": session.appearanceSessionId]
        if let location { args["location"] = location }
        if let bookFileId { args["bookFileId"] = bookFileId }
        if let initialInsets = estimatedInitialSurfaceInsets(for: book) {
            args["insets"] = initialInsets
        }
        let powerSaving = preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        let animation = NativeBookOpenTransitionPolicy.openAnimation(
            source: source,
            animated: animated,
            reduceMotion: UIAccessibility.isReduceMotionEnabled,
            powerSaving: powerSaving,
            pageAnimation: preferences.readerPageAnimation
        )
        readerOpenSource = source
        readerOpenAnimation = animation
        let useLiveWindowCover = animation != .cover && !launchBrandActive && !preparingLibrary && !restoringReader
        if useLiveWindowCover && widgetEntrySnapshotCover == nil {
            beginWidgetEntrySnapshotCover()
        }
        readerEntrySnapshot = (source == .widget || source == .restore || source == .external) && launchBrandActive
            ? (pendingWidgetReaderSnapshot ?? captureLaunchBrandSnapshot())
            : nil
        pendingWidgetReaderSnapshot = nil
        lastBookOpenOrigin = animated && source != .widget ? sourceKey.map { (book.id, $0) } : nil
        clearClosingBookTransition()

        // Start backend book loading and layout immediately so it overlaps with
        // cover decoding, modal presentation, and display commit frames.
        let openRequest = Task { @MainActor [weak self] () throws -> Void in
            guard let self else { return }
            await self.readerTestDelay("--libzora-test-slow-reader-open")
            guard generation == self.readerOpenGeneration else { return }
            let _: Done = try await self.request("reader.open", args)
        }

        if animation == .cover {
            let target = NativeBookOpenTransitionPolicy.resolveOpenTarget(
                bookId: book.id,
                explicitFrame: sourceFrame,
                explicitKey: sourceKey,
                activeTab: activeTab,
                viewport: viewportRect,
                lookup: { NativeBookCoverFrames.get($0) },
                seriesFrontBookId: seriesFrontBookId(for: book),
                topInset: max(40, NativeTheme.statusBarHeight + 16),
                bottomInset: max(52, NativeTheme.bottomTabBarInset + 8)
            )
            lastBookOpenOrigin = target.sourceKey.map { (book.id, $0) }
            // Load the text concurrently while the cover opens over the live reader.
            let coverImage = await resolveTransitionCoverImage(for: book, preferredWidth: target.frame.width)
            guard generation == readerOpenGeneration else {
                openRequest.cancel()
                await discardReaderOpen()
                return
            }
            let transition = NativeBookOpenTransition(
                book: book,
                sourceFrame: target.frame,
                sourceKey: target.sourceKey,
                isCentered: target.isCentered,
                coverImage: coverImage
            )
            openingBookReadyId = nil
            openingBookTransition = transition
            var transaction = Transaction()
            transaction.disablesAnimations = true
            UIView.setAnimationsEnabled(false)
            withTransaction(transaction) {
                readerBook = book
            }
            // Wait for the display commit so UIKit finishes the non-animated
            // modal presentation at 60Hz/120Hz+ before starting the 3D morph.
            await NativeDisplayFrame.waitForCommit()
            UIView.setAnimationsEnabled(true)
            guard !Task.isCancelled,
                  generation == readerOpenGeneration,
                  openingBookTransition?.id == transition.id else {
                openRequest.cancel()
                await discardReaderOpen()
                return
            }
            openingBookReadyId = transition.id
            let transitionId = transition.id
            let fallbackTimeout = (target.isCentered
                ? NativeBookOpenTransitionPolicy.openFromCenterDuration
                : NativeBookOpenTransitionPolicy.openFromCoverDuration) + 0.55
            DispatchQueue.main.asyncAfter(deadline: .now() + fallbackTimeout) { [weak self] in
                guard let self else { return }
                if self.openingBookTransition?.id == transitionId {
                    self.openingBookTransition = nil
                }
                if self.openingBookReadyId == transitionId {
                    self.openingBookReadyId = nil
                }
                self.restoringReader = false
            }
        } else {
            openingBookReadyId = nil
            openingBookTransition = nil
            var transaction = Transaction()
            transaction.disablesAnimations = true
            UIView.setAnimationsEnabled(false)
            withTransaction(transaction) { readerBook = book }
            keepLaunchBrandOverlayOnTop()
            await NativeDisplayFrame.waitForCommit()
            UIView.setAnimationsEnabled(true)
            // The entry layer releases the launch cover only after the current
            // viewport's page has been drawn and committed by the native display.
        }
        do {
            try await openRequest.value
        } catch {
            await discardReaderOpen()
            if generation == readerOpenGeneration { throw error }
            return
        }
        guard generation == readerOpenGeneration else {
            await discardReaderOpen()
            return
        }
        UserDefaults.standard.set(book.id, forKey: Self.lastReaderKey)
        let nextOrder = (bookOpenOrder.values.max() ?? 0) + 1
        bookOpenOrder[book.id] = nextOrder
        UserDefaults.standard.set(bookOpenOrder, forKey: Self.bookOpenOrderKey)
    }

    private func estimatedInitialSurfaceInsets(for book: LibraryBook) -> [String: Double]? {
        guard let window = navigationWindow,
              window.bounds.width > 10, window.bounds.height > 10 else { return nil }
        let safe = window.safeAreaInsets
        readerInitialTopSafeInset = safe.top
        let contentWidth = max(1, window.bounds.width - safe.left - safe.right)
        let formats = book.formats
        let isPdfOnly = formats.localizedCaseInsensitiveContains("PDF")
            && !formats.localizedCaseInsensitiveContains("EPUB")
            && !formats.localizedCaseInsensitiveContains("AZW3")
            && !formats.localizedCaseInsensitiveContains("MOBI")
            && !formats.localizedCaseInsensitiveContains("TXT")
        let isPdfOrManga = isPdfOnly || preferences.mangaMode
        let footerVisible = preferences.readerProgressDisplay != 4
        let bodyInset = min(64, max(16, preferences.readerBodyPadding))
        let isContinuousScroll = !isPdfOrManga && preferences.readerFlowMode == 0 && !preferences.readerVerticalWriting
        let extraFooter: CGFloat = (isPdfOnly || isContinuousScroll) ? (footerVisible ? 24 : 0)
            : (preferences.mangaMode || !footerVisible ? 0 : max(0, 24 - CGFloat(bodyInset)))
        let isRegularWidth = window.traitCollection.horizontalSizeClass == .regular
        let topInset: CGFloat = isRegularWidth ? max(0, 24 - CGFloat(bodyInset)) : safe.top
        let contentHeight = max(1, window.bounds.height - topInset - safe.bottom - extraFooter)
        let isTwoPage = NativeAdaptiveLayout.isLandscapeSpread(
            viewportSize: CGSize(width: contentWidth, height: contentHeight),
            isPdf: isPdfOnly,
            isManga: preferences.mangaMode,
            verticalWriting: preferences.readerVerticalWriting,
            twoPageInLandscape: preferences.readerTwoPageInLandscape
        )
        let readingWidth = NativeAdaptiveLayout.readerWidth(
            viewportWidth: contentWidth,
            isPdf: isPdfOrManga,
            isTwoPage: isTwoPage
        )
        let sideColumnInset = max(0, (contentWidth - readingWidth) / 2)
        return [
            "left": Double(safe.left + sideColumnInset),
            "top": Double(topInset),
            "right": Double(safe.right + sideColumnInset),
            "bottom": Double(safe.bottom + extraFooter),
            "viewportLeft": Double(safe.left),
            "viewportWidth": Double(contentWidth),
            "viewportHeight": Double(contentHeight)
        ]
    }

    private func readerTestDelay(_ argument: String) async {
#if DEBUG
        // Deterministic UI regression windows; entirely absent from release builds.
        let environmentKey = argument.hasSuffix("open")
            ? "LIBZORA_TEST_SLOW_READER_OPEN" : "LIBZORA_TEST_SLOW_READER_CLOSE"
        if ProcessInfo.processInfo.arguments.contains(argument)
            || ProcessInfo.processInfo.environment[environmentKey] == "1" {
            try? await Task.sleep(nanoseconds: 10_000_000_000)
        }
#endif
    }

    private func closeReaderBackend() async throws {
        await readerTestDelay("--libzora-test-slow-reader-close")
        let _: Done = try await request("reader.close")
    }

    private func discardReaderOpen() async {
        if pendingOpenBookId == nil {
            readerEntrySnapshot = nil
            pendingWidgetReaderSnapshot = nil
        }
        openingBookReadyId = nil
        openingBookTransition = nil
        if pendingOpenBookId == nil && requestedTab == nil {
            dismissLaunchBrandCover()
            dismissWidgetEntrySnapshotCover()
        }
        restoringReader = false
        dismissReaderPresentation()
        do { try await closeReaderBackend() }
        catch { if !Task.isCancelled { failure = error.localizedDescription } }
        session.reset()
    }

    func closeReader(animated: Bool = true, preservePendingOpen: Bool = false, preserveWidgetCover: Bool = false) async {
        guard !closingReader, readerBook != nil || openingReader else { return }
        if !preservePendingOpen {
            readerEntrySnapshot = nil
            pendingWidgetReaderSnapshot = nil
            if !preserveWidgetCover {
                dismissLaunchBrandCover()
                dismissWidgetEntrySnapshotCover()
            }
            queuedReaderOpen.clear()
            pendingOpenBookId = nil
        }
        pendingReaderRestore = nil
        UserDefaults.standard.removeObject(forKey: Self.lastReaderKey)
        if openingReader {
            readerOpenGeneration += 1
            openingBookReadyId = nil
            openingBookTransition = nil
            restoringReader = false
            dismissReaderPresentation()
            return
        }
        closingReader = true
        defer {
            closingReader = false
            if readerBook == nil {
                openPendingReaderIfNeeded()
                openQueuedReaderIfNeeded()
            }
        }
        restoringReader = false
        openingBookReadyId = nil
        openingBookTransition = nil
        let closingBook = readerBook
        let navigationRevision = widgetNavigationIntent.revision
        let closingProgress = session.state.progress
        let powerSaving = preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        let closeAnimation = NativeBookOpenTransitionPolicy.closeAnimation(
            source: readerOpenSource,
            animated: animated,
            reduceMotion: UIAccessibility.isReduceMotionEnabled,
            powerSaving: powerSaving,
            pageAnimation: preferences.readerPageAnimation
        )
        let shouldAnimateClose = closeAnimation == .cover
        var pageSnapshot: UIView?
        if shouldAnimateClose || closeAnimation.isPageAnimation || (preservePendingOpen && pendingOpenSource == .widget) {
            pageSnapshot = await captureReaderPresentationSnapshot()
        }
        if preservePendingOpen, pendingOpenSource == .widget, pendingOpenBookId != nil,
           widgetNavigationIntent.isCurrent(navigationRevision), let pageSnapshot {
            beginWidgetEntrySnapshotCover(snapshot: pageSnapshot)
        }
        let preferredKey = (lastBookOpenOrigin?.bookId == closingBook?.id) ? lastBookOpenOrigin?.sourceKey : nil
        let preferredCoverWidth = NativeBookOpenTransitionPolicy.centeredFrame(in: viewportRect).width
        async let preloadedCover: UIImage? = {
            guard shouldAnimateClose, let book = closingBook else { return nil }
            return await resolveTransitionCoverImage(for: book, preferredWidth: preferredCoverWidth)
        }()
        let coverImage = await preloadedCover
        let closeWaitDuration: Double
        if shouldAnimateClose, let book = closingBook {
            lastBookOpenOrigin = nil
            let window = navigationWindow
            let appearance = readerController.traitCollection.userInterfaceStyle
            let theme = preferences.readerThemeFollowsAppearance
                ? (appearance == .dark ? 5 : 0) : preferences.readerTheme
            let paper = UIColor(preferences.paper(for: theme, pureBlackOled: false))
            let ambientWash = shouldUseReaderAmbient
                ? window.flatMap { NativeAmbientPalette.renderWashImage(colors: ambient.colors, size: $0.bounds.size) }
                : nil
            let ambientOpacity = readerAmbientOpacity
            let handoff = window.flatMap { window in
                NativeBookCloseTransition(window: window, readerView: readerPresentationView,
                    fallback: { self.captureNavigationSnapshot() }, paper: paper, preparedSnapshot: pageSnapshot,
                    ambientWash: ambientWash, ambientOpacity: ambientOpacity)
            }
            closingWindowTransition?.finish()
            closingWindowTransition = handoff
            defer {
                if closingWindowTransition === handoff { closingWindowTransition = nil }
                handoff?.finish()
            }
            // Commit the original rendered reader above the modal before
            // dismissing it, so neither an empty Metal surface nor the shelf
            // can flash through the handoff.
            await NativeDisplayFrame.waitForCommit()

            if closingProgress < 99.5, continueBookId != book.id,
               library.contains(where: { $0.id == book.id }) {
                continueBookId = book.id
            }
            dismissReaderPresentation()
            // A display interval alone does not acknowledge fullScreenCover's
            // dismissal. Keep the original page visible until UIKit releases it.
            let dismissalDeadline = CACurrentMediaTime() + 1.5
            while readerPresentationActive && CACurrentMediaTime() < dismissalDeadline && !Task.isCancelled {
                await NativeDisplayFrame.waitForCommit()
            }
            await NativeDisplayFrame.waitForCommit()

            let frontBookId = seriesFrontBookId(for: book)
            let topInset = max(40, NativeTheme.statusBarHeight + 16)
            let bottomInset = max(52, NativeTheme.bottomTabBarInset + 8)
            let target = NativeBookOpenTransitionPolicy.resolveCloseTarget(
                bookId: book.id,
                preferredKey: preferredKey,
                activeTab: activeTab,
                viewport: viewportRect,
                lookup: { NativeBookCoverFrames.get($0) },
                seriesFrontBookId: frontBookId,
                topInset: topInset,
                bottomInset: bottomInset
            )
            let transition = NativeBookOpenTransition(
                book: book,
                sourceFrame: target.frame,
                sourceKey: target.sourceKey,
                isCentered: target.isCentered,
                coverImage: coverImage
            )
            if let handoff, !Task.isCancelled, widgetNavigationIntent.isCurrent(navigationRevision) {
                closingBookTransition = transition
                handoff.prepare(transition)
                await NativeDisplayFrame.waitForCommit()
                handoff.start { [weak self] in
                    self?.finishClosingBookTransition(id: transition.id)
                }
                let closeDuration = target.isCentered
                    ? NativeBookOpenTransitionPolicy.closeToCenterDuration
                    : NativeBookOpenTransitionPolicy.closeToCoverDuration
                await awaitClosingBookTransition(id: transition.id, maxWait: closeDuration + 0.75)
            }
            handoff?.finish()
            if closingWindowTransition === handoff { closingWindowTransition = nil }
            // Saving/disposing the Metal reader can block the main thread.
            // Only start it once the final cover frame has been handed off.
            do { try await closeReaderBackend() }
            catch { if !Task.isCancelled { failure = error.localizedDescription } }
            closeWaitDuration = 0
        } else {
            let window = navigationWindow
            let handoff = closeAnimation.isPageAnimation && widgetNavigationIntent.isCurrent(navigationRevision)
                ? NativePageSlideTransition(window: window, oldPageView: pageSnapshot ?? window?.snapshotView(afterScreenUpdates: false),
                    paper: window?.backgroundColor ?? .systemBackground) : nil
            if let handoff { readerEntryTransition = handoff }
            defer {
                handoff?.finish()
                if readerEntryTransition === handoff { readerEntryTransition = nil }
            }
            lastBookOpenOrigin = nil
            dismissReaderPresentation()
            if let handoff {
                await NativeDisplayFrame.waitForCommit()
                await handoff.show(window?.rootViewController?.view, animation: closeAnimation,
                    duration: readerOpenSource == .widget
                        ? NativeReaderPresentationPolicy.widgetTransitionDuration
                        : NativeBookOpenTransitionPolicy.pageTransitionDuration,
                    reverse: true)
            }
            // Tab presentation is independent of backend lifetime. Let its
            // snapshot handoff finish before save/dispose work can stall frames.
            if preserveWidgetCover { await waitForWidgetTabPresentation() }
            // Keep teardown serialized; queued book opens still wait for it.
            do { try await closeReaderBackend() }
            catch { if !Task.isCancelled { failure = error.localizedDescription } }
            closeWaitDuration = 0
        }
        session.reset()
        readerCloseRevision += 1
        UserDefaults.standard.removeObject(forKey: Self.lastReaderKey)
        if closeWaitDuration > 0 {
            // Wait for the cover-shut animation to land on its shelf slot
            // before refreshing/re-sorting the library underneath it.
            try? await Task.sleep(nanoseconds: UInt64(closeWaitDuration * 1_000_000_000))
        }
        // Library refresh is not reader teardown. Do not keep ordinary book
        // taps blocked by database or sync-status work after the reader closes.
        perform { try await self.refreshLibrary() }
    }

    func completeReaderEntry(sessionId: String) {
        guard session.appearanceSessionId == sessionId else { return }
        readerEntrySnapshot = nil
        dismissLaunchBrandCover()
        dismissWidgetEntrySnapshotCover()
        restoringReader = false
    }

    var navigationWindow: UIWindow? {
        rootViewController?.viewIfLoaded?.window
            ?? readerController.viewIfLoaded?.window
            ?? UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene })
                .flatMap(\.windows).first(where: { $0.isKeyWindow })
            ?? UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene })
                .flatMap(\.windows).first
    }

    var readerPresentationView: UIView? {
        guard let window = readerController.viewIfLoaded?.window else { return nil }
        var view = readerController.viewIfLoaded
        // SwiftUI can present from a nested navigation controller rather than
        // rootViewController.presentedViewController. Resolve the actual reader
        // hierarchy, stopping before window-level transition overlays.
        while let parent = view?.superview, parent !== window { view = parent }
        return view
    }

    func captureNavigationSnapshot() -> UIImage? {
        guard let window = navigationWindow,
              window.bounds.width > 10, window.bounds.height > 10 else { return nil }
        let format = UIGraphicsImageRendererFormat()
        format.scale = min(window.screen.scale, 2)
        return UIGraphicsImageRenderer(bounds: window.bounds, format: format).image { _ in
            window.drawHierarchy(in: window.bounds, afterScreenUpdates: false)
        }
    }

    private func dismissReaderPresentation() {
        if pendingOpenBookId == nil { readerEntrySnapshot = nil }
        var transaction = Transaction()
        transaction.disablesAnimations = true
        UIView.setAnimationsEnabled(false)
        withTransaction(transaction) { readerBook = nil }
        keepLaunchBrandOverlayOnTop()
        DispatchQueue.main.async {
            UIView.setAnimationsEnabled(true)
        }
    }

}
