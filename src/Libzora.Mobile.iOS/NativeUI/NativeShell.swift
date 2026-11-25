import SwiftUI
import UIKit
import Combine
import ImageIO

struct NativeShell: View {
    @ObservedObject var store: NativeStore
    @ObservedObject private var previewCenter = NativeBookPreviewCenter.shared
    @ObservedObject private var seriesCenter = NativeBookSeriesCenter.shared
    @State private var selectedTab = 0
    @State private var widgetNavigationTask: Task<Void, Never>?
    @State private var systemLowPowerMode = ProcessInfo.processInfo.isLowPowerModeEnabled
    @Environment(\.colorScheme) private var systemColorScheme
    private var appColorScheme: ColorScheme {
        switch store.preferences.themeMode {
        case 1: return .light
        case 2: return .dark
        default: return systemColorScheme
        }
    }
    private var readerPaper: Color {
        let powerSaving = store.preferences.isPowerSaving(systemLowPower: systemLowPowerMode)
        if powerSaving { return .black }
        let theme = store.preferences.readerThemeFollowsAppearance
            ? (appColorScheme == .dark ? 5 : 0)
            : store.preferences.readerTheme
        return store.preferences.paper(for: theme, pureBlackOled: powerSaving)
    }
    private var ambientBooks: [LibraryBook] {
        Array(store.library.filter { $0.coverPath != nil }
            .sorted { $0.updatedAt == $1.updatedAt ? $0.id < $1.id : $0.updatedAt > $1.updatedAt }.prefix(18))
    }
    private var ambientKey: String {
        guard store.preferences.colorfulMode else { return "off" }
        return ambientBooks.map { "\($0.coverPath ?? "")|\($0.updatedAt)" }.joined(separator: "\n")
    }
    private var showingLaunch: Bool { store.preparingLibrary || store.restoringReader }
    private var showingWelcome: Bool {
        !showingLaunch
            && !store.restartRequired
            && !store.dataBusy
            && store.libraryLoaded
            && !store.canOpenReader
    }

    var body: some View {
        let activeSeries = seriesCenter.request.flatMap { request in
            store.seriesSnapshot(for: request.seriesID).map { (request, $0) }
        }
        GeometryReader { geometry in
            TabView(selection: $selectedTab) {
                NavigationView { NativeLibraryPage(store: store, isActive: selectedTab == 0) }.navigationViewStyle(.stack)
                    .tabItem { NativeTabLabel(title: L("书库"), symbol: "book") }.tag(0)
                NavigationView { NativeMaterials(store: store) }.navigationViewStyle(.stack)
                    .tabItem { NativeTabLabel(title: L("阅读资料"), symbol: "note.text") }.tag(1)
                NavigationView { NativeReadingDashboard(store: store) }.navigationViewStyle(.stack)
                    .tabItem { NativeTabLabel(title: L("阅读数据"), symbol: "chart.bar") }.tag(2)
                NavigationView { NativeProfilePage(store: store) }.navigationViewStyle(.stack)
                    .tabItem { NativeTabLabel(title: L("我的"), symbol: "person") }.tag(3)
            }
            .opacity(showingLaunch ? 0 : 1)
            .offset(x: showingWelcome && !UIAccessibility.isReduceMotionEnabled ? geometry.size.width : 0)
            .animation(
                showingWelcome || UIAccessibility.isReduceMotionEnabled
                    ? nil
                    : .timingCurve(0.22, 1, 0.36, 1, duration: 0.34),
                value: showingWelcome
            )
            .allowsHitTesting(!showingLaunch && !showingWelcome)
            .accessibilityHidden(showingLaunch || showingWelcome || (activeSeries != nil && seriesCenter.isVisible))
            .onAppear {
                store.activeTab = selectedTab
                store.keepWidgetBrandOverlayOnTop()
            }
            .task { await store.prepareLaunch(viewport: geometry.size) }
            .onChange(of: selectedTab) { tab in
                store.activeTab = tab
                previewCenter.dismiss()
                seriesCenter.dismiss()
            }
            .onChange(of: geometry.size) { _ in
                // A preview's source frame belongs to the previous window size.
                // Dismiss only the transient overlay, never the tab/navigation state.
                previewCenter.dismiss()
                seriesCenter.dismiss()
            }
        }
        .background {
            GeometryReader { fullWindow in
                let bounds = fullWindow.frame(in: .global)
                Color.clear
                    .onAppear { store.updateViewport(bounds) }
                    .onChange(of: bounds) { updated in store.updateViewport(updated) }
            }
            .ignoresSafeArea()
        }
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .environment(\.symbolVariants, .none)
        .background(NativeAmbientBackground()).preferredColorScheme(store.preferences.colorScheme)
        .fullScreenCover(item: $store.readerPresentation, onDismiss: store.readerPresentationDismissed) { presentation in
            NativeReaderPage(store: store, session: store.session, book: presentation.book,
                             appColorScheme: appColorScheme, appearanceSessionId: presentation.id,
                             openAnimation: presentation.animation)
                .id(presentation.id)
                .modifier(NativeClearFullScreenBackground())
                .onAppear { store.readerPresentationAppeared(for: presentation.id) }
        }
        .sheet(item: $store.txtReview, onDismiss: store.txtReviewDismissed) { review in
            NativeTxtReview(store: store, review: review).id(review.id)
                .onAppear { store.txtPresentationActive = true }
        }
        .modifier(NativeFeedback(store: store, active: store.readerBook == nil))
        .disabled(store.restartRequired || store.dataBusy)
        .task(id: ambientKey) {
            guard store.preferences.colorfulMode else {
                store.ambient.visibleColors = []; store.ambient.fallbackColors = []; return
            }
            if store.library.isEmpty { store.ambient.visibleColors = [] }
            let paths = ambientBooks.compactMap(\.coverPath)
            let colors = await Task.detached(priority: .utility) { NativeAmbientPalette.extract(paths: paths) }.value
            guard !Task.isCancelled else { return }
            store.ambient.fallbackColors = colors
        }
        .onReceive(store.$requestedTab) { tab in
            if tab != nil { showRequestedTabIfPossible(isNewRequest: true) }
        }
        .onReceive(store.$preparingLibrary) { preparing in
            if !preparing { showRequestedTabIfPossible() }
        }
        .onReceive(store.$readerPresentationActive) { active in
            if !active { showRequestedTabIfPossible() }
        }
        .onReceive(store.$closingReader) { closing in
            if !closing { showRequestedTabIfPossible() }
        }
        .onReceive(store.$openingReader) { opening in
            if !opening { showRequestedTabIfPossible() }
        }
        .onReceive(NotificationCenter.default.publisher(for: .NSProcessInfoPowerStateDidChange)
            .receive(on: DispatchQueue.main)) { _ in
            systemLowPowerMode = ProcessInfo.processInfo.isLowPowerModeEnabled
        }
        .onReceive(NotificationCenter.default.publisher(for: UIApplication.didBecomeActiveNotification)
            .receive(on: DispatchQueue.main)) { _ in
            systemLowPowerMode = ProcessInfo.processInfo.isLowPowerModeEnabled
        }
        .environmentObject(store)
        .overlay {
            if store.restartRequired {
                VStack(spacing: 16) {
                    Image(systemName: "arrow.clockwise").font(.largeTitle)
                    Text(L("请重新打开 Libzora")).font(NativeTheme.font(21, relativeTo: .title2))
                    Text(L("请完全关闭并重新打开应用后继续使用，以载入恢复的数据。"))
                        .multilineTextAlignment(.center).foregroundStyle(.secondary)
                }.padding(32).frame(maxWidth: .infinity, maxHeight: .infinity).background(NativeTheme.page)
            } else if store.dataBusy {
                VStack(spacing: 18) { ProgressView(); Text(store.busyMessage).foregroundStyle(.secondary) }
                    .frame(maxWidth: .infinity, maxHeight: .infinity).background(.regularMaterial)
            } else if showingLaunch {
                if store.widgetColdLaunch {
                    NativeWidgetLaunchBrandView()
                } else if store.showsLaunchAnimation {
                    NativeLaunchPage()
                        .transition(.opacity)
                } else {
                    readerPaper
                        .ignoresSafeArea()
                        .accessibilityElement(children: .contain)
                        .accessibilityIdentifier("launch.screen")
                }
            } else if showingWelcome {
                NativeWelcomeView(store: store)
                    .transition(
                        UIAccessibility.isReduceMotionEnabled
                            ? .opacity
                            : .asymmetric(insertion: .opacity, removal: .move(edge: .leading))
                    )
            }
        }
        .animation(store.showsLaunchAnimation && !store.restoringReader ? .easeOut(duration: 0.2) : nil, value: showingLaunch)
        .animation(
            showingWelcome
                ? .easeOut(duration: 0.22)
                : .timingCurve(0.22, 1, 0.36, 1, duration: 0.34),
            value: showingWelcome
        )
        .overlay {
            if let (request, series) = activeSeries {
                NativeBookSeriesOverlay(series: series, sourceFrame: request.sourceFrame,
                                        store: store, center: seriesCenter,
                                        selection: request.selection,
                                        close: { seriesCenter.dismiss() },
                                        preview: request.preview)
            }
        }
        .overlay {
            if let request = previewCenter.request {
                NativeBookPreviewOverlay(request: request, center: previewCenter)
            }
        }
        .overlay {
            if (store.pendingOpenBookId != nil || (store.openingReader && store.readerOpenAnimation != .cover)),
               store.pendingOpenSource == .widget,
               let snapshot = store.readerEntrySnapshot {
                Image(uiImage: snapshot).resizable().scaledToFill()
                    .ignoresSafeArea().allowsHitTesting(false).accessibilityHidden(true)
            }
        }
        .overlay {
            if let transition = store.closingBookTransition {
                NativeBookCloseOverlay(transition: transition, paper: readerPaper) {
                    store.finishClosingBookTransition(id: transition.id)
                }
            } else if store.closingBookBackdrop {
                ZStack {
                    readerPaper
                    if let snapshot = store.closingBookSnapshot {
                        Image(uiImage: snapshot)
                            .resizable()
                            .scaledToFill()
                    }
                }
                .ignoresSafeArea()
                .allowsHitTesting(false)
                .accessibilityHidden(true)
            }
        }
        // Include presented readers, sheets and standalone overlays without
        // changing view identity or resetting their navigation/session state.
        .environment(\.locale, Locale(identifier: store.uiLanguage))
        .environment(\.nativeUiFont, store.uiFont)
    }

    private func showRequestedTabIfPossible(isNewRequest: Bool = false) {
        // Readiness notifications fire while an already-accepted handoff waits
        // for a display commit; do not cancel the in-flight slide transition.
        guard isNewRequest || store.requestedTab != nil else { return }
        widgetNavigationTask?.cancel()
        widgetNavigationTask = Task { @MainActor in
            // @Published delivers before its stored value changes.
            await Task.yield()
            guard store.requestedTab != nil, !store.preparingLibrary, !store.closingReader,
                  !store.openingReader, store.readerBook == nil, !store.readerPresentationActive else { return }
            // Switch the tab quietly under the active snapshot/icon overlay while the destination loads.
            if let tab = store.requestedTab {
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) { selectedTab = tab }
            }
            if store.widgetColdLaunch {
                await store.waitForMinimumWidgetBrandHold()
                guard !Task.isCancelled else { return }
            }
            guard store.requestedTab != nil,
                  !store.preparingLibrary, !store.closingReader, !store.openingReader,
                  store.readerBook == nil, !store.readerPresentationActive else {
                if store.pendingOpenBookId == nil && store.requestedTab == nil {
                    store.dismissWidgetBrandCover()
                    store.widgetTabTransition?.finish()
                    store.widgetTabTransition = nil
                }
                return
            }
            store.requestedTab = nil
            let handoff: NativePageSlideTransition?
            if store.widgetColdLaunch {
                handoff = store.createWidgetBrandSlideTransition()
                store.dismissWidgetBrandCover()
            } else {
                handoff = store.takeWidgetTabTransition()
            }
            await NativeDisplayFrame.waitForCommit()
            guard !Task.isCancelled else {
                handoff?.finish(); return
            }
            await handoff?.show(store.navigationWindow?.rootViewController?.view, animation: .slide,
                                duration: NativeReaderPresentationPolicy.widgetTransitionDuration)
        }
    }
}

struct NativeWidgetLaunchBrandView: View {
    var showProgress = false
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont

    private static let appIconImage: UIImage? = {
        if let icons = Bundle.main.infoDictionary?["CFBundleIcons"] as? [String: Any],
           let primary = icons["CFBundlePrimaryIcon"] as? [String: Any],
           let files = primary["CFBundleIconFiles"] as? [String] {
            for name in files.reversed() {
                if let image = UIImage(named: name) { return image }
            }
        }
        return UIImage(named: "Libzora60x60")
    }()

    var body: some View {
        let _ = uiLocale
        let _ = uiFont

        VStack(spacing: 16) {
            Spacer()
            if let icon = Self.appIconImage {
                Image(uiImage: icon)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 82, height: 82)
                    .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 20, style: .continuous)
                            .stroke(NativeTheme.ink.opacity(0.08), lineWidth: 0.5)
                    )
                    .shadow(color: .black.opacity(0.08), radius: 14, y: 6)
            }
            Text("Libzora")
                .font(NativeTheme.font(24, relativeTo: .title2))
                .tracking(2.5)
                .foregroundStyle(NativeTheme.ink)
                .accessibilityIdentifier("launch.brand")
            if showProgress {
                ProgressView()
                    .padding(.top, 4)
            }
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(NativeTheme.page.ignoresSafeArea())
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("launch.screen")
    }
}

private struct NativeLaunchPage: View {
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var revealed = false

    private var isEnglish: Bool {
        uiLocale.identifier.lowercased().hasPrefix("en")
    }

    var body: some View {
        let _ = uiLocale
        let _ = uiFont

        VStack(spacing: 0) {
            Spacer()
            VStack(spacing: 20) {
                Text("Libzora")
                    .font(NativeTheme.font(34, relativeTo: .largeTitle)).tracking(isEnglish ? 3.5 : 4)
                    .accessibilityIdentifier("launch.brand")
                Text(L("为学日益，为道日损。"))
                    .font(NativeTheme.font(isEnglish ? 19 : 22, relativeTo: .title2))
                    .tracking(isEnglish ? 0.4 : 1.5)
                    .lineSpacing(isEnglish ? 6 : 0)
                    .multilineTextAlignment(.center)
                    .lineLimit(isEnglish ? 2 : 1)
                    .minimumScaleFactor(isEnglish ? 0.8 : 0.65)
                    .accessibilityIdentifier("launch.slogan")
            }
            .foregroundStyle(NativeTheme.ink)
            .opacity(revealed ? 1 : 0)
            .offset(y: revealed || reduceMotion ? 0 : 10)
            Spacer()
        }
        .padding(.horizontal, 32).padding(.top, 32).padding(.bottom, 36)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(NativeTheme.page.ignoresSafeArea())
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("launch.screen")
        .onAppear {
            withAnimation(.easeOut(duration: 0.35)) { revealed = true }
        }
    }
}

struct NativeTabLabel: View {
    let title: String
    let symbol: String
    var body: some View {
        // A concrete UIKit image retains the original outline symbol. TabView
        // otherwise substitutes filled SF Symbol variants for the selection.
        Label {
            Text(title)
        } icon: {
            Image(uiImage: UIImage(systemName: symbol)!)
        }
    }
}

struct NativeToast: View {
    @Environment(\.nativeUiFont) private var uiFont
    let text: String
    var body: some View {
        let _ = uiFont
        Text(text).font(NativeTheme.font(14, relativeTo: .subheadline)).padding(.horizontal, 18).padding(.vertical, 12)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 20).allowsHitTesting(false)
    }
}

struct NativeFeedback: ViewModifier {
    @ObservedObject var store: NativeStore
    let active: Bool
    func body(content: Content) -> some View {
        content.overlay(alignment: .bottom) {
            if active, let toast = store.toast { NativeToast(text: toast).padding(.bottom, 80) }
        }
    }
}

@MainActor
enum NativeCoverCache {
    private struct Request: Hashable {
        let path: String
        let version: String
        let pixels: Int
        var key: NSString { "\(path):\(version):\(pixels)" as NSString }
    }
    private static let images: NSCache<NSString, UIImage> = {
        let cache = NSCache<NSString, UIImage>()
        cache.totalCostLimit = 24 * 1024 * 1024
        return cache
    }()
    private static var pending: [Request: Task<UIImage?, Never>] = [:]
    private static var cachedPixelSizes: [String: Set<Int>] = [:]

    static func cachedImage(path: String, version: String, pixels: Int) -> UIImage? {
        images.object(forKey: Request(path: path, version: version, pixels: pixels).key)
    }

    /// Returns the highest-resolution already-decoded cover in cache for this
    /// asset so open/close transitions never flash a placeholder on frame 0.
    static func bestCachedImage(path: String, version: String) -> UIImage? {
        let assetKey = "\(path):\(version)"
        guard let sizes = cachedPixelSizes[assetKey] else { return nil }
        for pixels in sizes.sorted(by: >) {
            if let image = images.object(forKey: Request(path: path, version: version, pixels: pixels).key) {
                return image
            }
        }
        return nil
    }

    static func image(path: String, version: String, pixels: Int) async -> UIImage? {
        let request = Request(path: path, version: version, pixels: pixels)
        if let image = images.object(forKey: request.key) { return image }
        guard !Task.isCancelled else { return nil }
        let task: Task<UIImage?, Never>
        if let existing = pending[request] { task = existing }
        else {
            task = Task.detached(priority: .userInitiated) { () -> UIImage? in
                guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
                      let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                        kCGImageSourceCreateThumbnailFromImageAlways: true,
                        kCGImageSourceCreateThumbnailWithTransform: true,
                        kCGImageSourceThumbnailMaxPixelSize: pixels,
                        kCGImageSourceShouldCacheImmediately: true
                      ] as CFDictionary) else { return nil }
                return UIImage(cgImage: thumbnail)
            }
            pending[request] = task
        }
        let image = await task.value
        pending.removeValue(forKey: request)
        if let image {
            images.setObject(image, forKey: request.key, cost: Int(image.size.width * image.size.height) * 4)
            cachedPixelSizes["\(path):\(version)", default: []].insert(pixels)
        }
        return image
    }

    /// Warm the same cache and sizes that BookCover renders. Only the initial
    /// viewport is needed: grid/list order, collection stacks and continue card
    /// follow the actual shelf preferences, instead of preloading every cover.
    static func prepareLibrary(books: [LibraryBook], folders: [LibraryFolder], preferences: MobilePreferences,
                               continueBookId: String?, viewport: CGSize) async {
        var requests: [Request] = []
        func append(_ book: LibraryBook, width: CGFloat) {
            guard let path = book.coverPath else { return }
            let request = Request(path: path, version: book.updatedAt, pixels: Int(width * 3))
            if !requests.contains(request) { requests.append(request) }
        }
        let width = max(1, NativeAdaptiveLayout.pageWidth(viewportWidth: viewport.width))
        let height = max(1, viewport.height - 49) // Conservative initial toolbar estimate.
        if preferences.collectionsView {
            let grid = NativeAdaptiveLayout.collectionGrid(pageWidth: width)
            let cardWidth = grid.itemWidth
            let rows = max(1, Int(ceil(max(1, height - 124) / (cardWidth * 1.48 + grid.spacing))))
            for folder in folders.prefix(rows * grid.columns) {
                let covers = books.filter { $0.collectionIds.contains(folder.id) }
                    .sorted { $0.updatedAt > $1.updatedAt }.prefix(3)
                for book in covers { append(book, width: cardWidth * 0.46) }
            }
        } else {
            let ordered = NativeBookOrder.sorted(books, sort: preferences.sort)
            let continueBook = ordered.first { $0.id == continueBookId }
            if let continueBook { append(continueBook, width: 48) }
            let availableHeight = max(1, height - 180 - (continueBook == nil ? 0 : 88))
            if preferences.listView {
                let count = max(1, Int(ceil(availableHeight / 86)))
                for book in ordered.prefix(count) { append(book, width: 46) }
            } else {
                let coverWidth = NativeBookShelf.coverWidth(pageWidth: width)
                let rowHeight = coverWidth * 1.45 + (preferences.galleryMode ? 18 : 82)
                let rows = max(1, Int(ceil(availableHeight / rowHeight)))
                let count = rows * NativeBookShelf.columnCount(pageWidth: width)
                for book in ordered.prefix(count) { append(book, width: coverWidth) }
            }
        }
        // Four prefetch workers at most. The hidden shelf can request the same
        // cover; in-flight requests are shared rather than decoding it twice.
        await withTaskGroup(of: Void.self) { group in
            var remaining = requests.makeIterator()
            for _ in 0..<4 {
                guard let request = remaining.next() else { break }
                group.addTask { _ = await image(path: request.path, version: request.version, pixels: request.pixels) }
            }
            while await group.next() != nil {
                if Task.isCancelled { group.cancelAll(); break }
                if let request = remaining.next() {
                    group.addTask { _ = await image(path: request.path, version: request.version, pixels: request.pixels) }
                }
            }
        }
    }
}

struct BookCover: View {
    let book: LibraryBook
    var width: CGFloat = 62
    var height: CGFloat? = nil
    @Environment(\.colorScheme) private var scheme
    @State private var image: UIImage?
    private var displayImage: UIImage? {
        if let image { return image }
        guard let path = book.coverPath else { return nil }
        return NativeCoverCache.cachedImage(path: path, version: book.updatedAt, pixels: Int(width * 3))
            ?? NativeCoverCache.bestCachedImage(path: path, version: book.updatedAt)
    }
    var body: some View {
        Group {
            if let image = displayImage { Image(uiImage: image).resizable().scaledToFill() }
            else { ZStack { NativeTheme.sunk; Text(String(book.title.prefix(1))).font(NativeTheme.font(width * 0.27)).foregroundStyle(NativeTheme.muted) } }
        }
        .frame(width: width, height: height ?? width * 1.45)
        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        .shadow(color: .black.opacity(scheme == .dark ? 0 : 0.12), radius: 5, y: 2)
        .accessibilityHidden(true)
        .task(id: "\(book.coverPath ?? ""):\(book.updatedAt):\(width)") {
            guard let path = book.coverPath else { image = nil; return }
            let result = await NativeCoverCache.image(path: path, version: book.updatedAt, pixels: Int(width * 3))
            if !Task.isCancelled { image = result }
        }
    }
}

struct ReaderSurface: UIViewControllerRepresentable {
    let controller: UIViewController
    func makeUIViewController(context: Context) -> UIViewController { controller }
    func updateUIViewController(_ uiViewController: UIViewController, context: Context) {}
}

private struct NativeClearFullScreenBackground: ViewModifier {
    func body(content: Content) -> some View {
        if #available(iOS 16.4, *) {
            content.presentationBackground(.clear)
        } else {
            content
        }
    }
}
