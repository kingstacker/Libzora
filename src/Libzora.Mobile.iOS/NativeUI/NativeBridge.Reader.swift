import SwiftUI
import UIKit

struct NativeReaderOpenRequest {
    let book: LibraryBook
    let location: [String: Any]?
    let animated: Bool
    let sourceFrame: CGRect?
    let sourceKey: String?
    let source: NativeBookOpenSource
}

@MainActor
extension NativeStore {
    /// A widget can deep-link into a book before the shelf has loaded, or
    /// while another book is open. Keep the request until a fresh library
    /// snapshot can resolve it and the reader is free.
    func openRequestedBook(_ bookId: String, source: NativeBookOpenSource) {
        showsLaunchAnimation = false
        pendingReaderRestore = nil
        if NativeReaderOpenPolicy.isDuplicate(
            bookId: bookId, openingBookId: openingReaderBookId, openingGeneration: openingReaderGeneration,
            currentGeneration: readerOpenGeneration, readerBookId: readerBook?.id,
            closing: closingReader, hasPending: pendingOpenBookId != nil || queuedReaderOpen.pending != nil
        ) { return }
        queuedReaderOpen.clear()
        pendingOpenBookId = bookId
        pendingOpenSource = source
        pendingBookRequiredRevision = libraryRefreshRevision + 1
        if preparingLibrary {
            restoringReader = true
        } else if openingReader {
            readerOpenGeneration += 1
        } else if libraryLoaded {
            openPendingReaderIfNeeded()
        } else {
            perform { try await self.refreshLibrary() }
        }
    }

    func readerPresentationAppeared(for bookId: String) {
        if readerBook?.id == bookId { readerPresentationActive = true }
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
        if readerBook != nil && !openingReader {
            perform { await self.closeReader(animated: false) }
        }
    }

    func openPendingReaderIfNeeded() {
        guard let bookId = pendingOpenBookId, libraryLoaded, !preparingLibrary,
              !openingReader, !closingReader, !restartRequired else { return }
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
                    restoringReader = false
                    if !Task.isCancelled { failure = error.localizedDescription }
                }
                pendingBookRefreshTask = nil
                // A newer link may require a snapshot newer than this refresh.
                if refreshed { openPendingReaderIfNeeded() }
            }
        case .missing:
            pendingOpenBookId = nil
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
    private func openQueuedReaderIfNeeded() {
        guard queuedReaderOpen.pending != nil, !openingReader, !closingReader, !restartRequired else { return }
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
                                      source: request.source)
        }
    }

    func restorePendingReaderIfNeeded() {
        guard let bookId = pendingReaderRestore,
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
            try? await self.openReader(book, animated: false)
        }
    }

    /// A failed first shelf load must not leave the launch cover on screen.
    func releaseLaunchRestore() {
        if readerBook == nil && !openingReader { restoringReader = false }
    }

    func isBookCoverHidden(forKey key: String) -> Bool {
        openingBookTransition?.sourceKey == key || closingBookTransition?.sourceKey == key
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
                    sourceKey: String? = nil, source: NativeBookOpenSource = .cover) async throws {
        if openingReader || closingReader || readerPresentationActive || readerBook != nil {
            if NativeReaderOpenPolicy.isDuplicate(
                bookId: book.id, openingBookId: openingReaderBookId, openingGeneration: openingReaderGeneration,
                currentGeneration: readerOpenGeneration, readerBookId: readerBook?.id,
                closing: closingReader, hasPending: pendingOpenBookId != nil || queuedReaderOpen.pending != nil
            ), location == nil { return }
            pendingReaderRestore = nil
            pendingOpenBookId = nil
            queuedReaderOpen.enqueue(NativeReaderOpenRequest(
                book: book, location: location, animated: animated, sourceFrame: sourceFrame,
                sourceKey: sourceKey, source: source))
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
        readerPageExitAnimation = .none
        lastBookOpenOrigin = animated && source != .widget ? sourceKey.map { (book.id, $0) } : nil
        closingBookTransition = nil
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
            guard generation == readerOpenGeneration else { return }
            let transition = NativeBookOpenTransition(
                book: book,
                sourceFrame: target.frame,
                sourceKey: target.sourceKey,
                isCentered: target.isCentered,
                coverImage: coverImage
            )
            openingBookTransition = transition
            var transaction = Transaction()
            transaction.disablesAnimations = true
            UIView.setAnimationsEnabled(false)
            withTransaction(transaction) {
                readerBook = book
            }
            // Wait one frame so UIKit commits the non-animated presentation,
            // then immediately re-enable animations so the open morph runs concurrently.
            try? await Task.sleep(nanoseconds: 12_000_000)
            UIView.setAnimationsEnabled(true)
            let transitionId = transition.id
            let fallbackTimeout = (target.isCentered
                ? NativeBookOpenTransitionPolicy.openFromCenterCleanupDelay
                : NativeBookOpenTransitionPolicy.openFromCoverCleanupDelay) + 0.16
            DispatchQueue.main.asyncAfter(deadline: .now() + fallbackTimeout) { [weak self] in
                guard let self else { return }
                if self.openingBookTransition?.id == transitionId {
                    self.openingBookTransition = nil
                }
                self.restoringReader = false
            }
        } else if animated && source == .cover {
            openingBookTransition = nil
            readerBook = book
            restoringReader = false
        } else {
            openingBookTransition = nil
            var transaction = Transaction()
            transaction.disablesAnimations = true
            UIView.setAnimationsEnabled(false)
            withTransaction(transaction) {
                readerBook = book
            }
            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: 50_000_000)
                UIView.setAnimationsEnabled(true)
                self?.restoringReader = false
            }
        }
        do {
            await readerTestDelay("--libzora-test-slow-reader-open")
            if generation == readerOpenGeneration {
                let _: Done = try await request("reader.open", args)
            }
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
        openingBookTransition = nil
        restoringReader = false
        dismissReaderPresentation()
        do { try await closeReaderBackend() }
        catch { if !Task.isCancelled { failure = error.localizedDescription } }
        session.reset()
    }

    func closeReader(animated: Bool = true, preservePendingOpen: Bool = false) async {
        guard !closingReader, readerBook != nil || openingReader else { return }
        if !preservePendingOpen {
            queuedReaderOpen.clear()
            pendingOpenBookId = nil
        }
        pendingReaderRestore = nil
        UserDefaults.standard.removeObject(forKey: Self.lastReaderKey)
        if openingReader {
            readerOpenGeneration += 1
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
        openingBookTransition = nil
        let closingBook = readerBook
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
        let preferredKey = (lastBookOpenOrigin?.bookId == closingBook?.id) ? lastBookOpenOrigin?.sourceKey : nil
        let preferredCoverWidth = NativeBookOpenTransitionPolicy.centeredFrame(in: viewportRect).width
        let pageSnapshot = shouldAnimateClose ? captureReaderSnapshot() : nil
        async let preloadedCover: UIImage? = {
            guard shouldAnimateClose, let book = closingBook else { return nil }
            return await resolveTransitionCoverImage(for: book, preferredWidth: preferredCoverWidth)
        }()
        let coverImage = await preloadedCover
        let closeWaitDuration: Double
        if shouldAnimateClose, let book = closingBook {
            lastBookOpenOrigin = nil
            // Hold the live page snapshot beneath the fullScreenCover while
            // dismissing it so closing never flashes blank paper.
            closingBookSnapshot = pageSnapshot
            closingBookBackdrop = true
            defer {
                closingBookBackdrop = false
                closingBookSnapshot = nil
                UIView.setAnimationsEnabled(true)
            }
            if closingProgress < 99.5, continueBookId != book.id,
               library.contains(where: { $0.id == book.id }) {
                continueBookId = book.id
            }
            var transaction = Transaction()
            transaction.disablesAnimations = true
            UIView.setAnimationsEnabled(false)
            withTransaction(transaction) {
                readerBook = nil
            }
            // Start backend teardown immediately without blocking the shrink animation.
            let closeBackend = Task { @MainActor in
                do { try await self.closeReaderBackend() }
                catch { if !Task.isCancelled { self.failure = error.localizedDescription } }
            }
            try? await Task.sleep(nanoseconds: 16_000_000)
            UIView.setAnimationsEnabled(true)

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
            closingBookTransition = NativeBookOpenTransition(
                book: book,
                sourceFrame: target.frame,
                sourceKey: target.sourceKey,
                isCentered: target.isCentered,
                coverImage: coverImage,
                pageSnapshot: pageSnapshot
            )
            closingBookBackdrop = false
            closingBookSnapshot = nil
            closeWaitDuration = (target.isCentered
                ? NativeBookOpenTransitionPolicy.closeToCenterCleanupDelay
                : NativeBookOpenTransitionPolicy.closeToCoverCleanupDelay) + 0.03
            _ = await closeBackend.value
        } else {
            if closeAnimation.isPageAnimation {
                let duration = NativeBookOpenTransitionPolicy.pageTransitionDuration
                withAnimation(.easeIn(duration: duration)) { readerPageExitAnimation = closeAnimation }
                try? await Task.sleep(nanoseconds: UInt64(duration * 1_000_000_000))
            }
            // The page has finished its exit animation. Release the modal now,
            // but keep closingReader set until teardown finishes so queued opens
            // cannot race a save or an old reader.close.
            lastBookOpenOrigin = nil
            dismissReaderPresentation()
            do { try await closeReaderBackend() }
            catch { if !Task.isCancelled { failure = error.localizedDescription } }
            closeWaitDuration = 0
        }
        session.reset()
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

    private func dismissReaderPresentation() {
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) { readerBook = nil }
    }

    private func captureReaderSnapshot() -> UIImage? {
        guard let view = readerController.viewIfLoaded,
              view.bounds.width > 10, view.bounds.height > 10 else { return nil }
        let format = UIGraphicsImageRendererFormat()
        format.scale = min(UIScreen.main.scale, 2.0)
        format.opaque = false
        return UIGraphicsImageRenderer(bounds: view.bounds, format: format).image { _ in
            view.drawHierarchy(in: view.bounds, afterScreenUpdates: false)
        }
    }

}
