import SwiftUI
import UIKit
import CoreText

private enum ReaderPanel: String, Identifiable {
    case toc, typography, speech, assistant, translation, info
    var id: String { rawValue }
    var title: String {
        switch self {
        case .toc: return L("目录"); case .typography: return L("阅读设置")
        case .speech: return L("朗读设置"); case .assistant: return L("AI 助手")
        case .translation: return L("翻译"); case .info: return L("注释")
        }
    }
}

private struct ReaderSurfaceInsets: Equatable {
    let left: CGFloat
    let top: CGFloat
    let right: CGFloat
    let bottom: CGFloat
}

/// A note being written or edited in the reader's floating card.
private struct ReaderNoteRequest: Identifiable, Equatable {
    let id = UUID()
    let quote: String
    let annotationId: String?
    let initialNote: String
    let initialStyle: String
    let initialColor: String
}

struct NativeReaderPage: View {
    @ObservedObject var store: NativeStore
    @ObservedObject var session: NativeReaderSession
    let book: LibraryBook
    let appColorScheme: ColorScheme
    let appearanceSessionId: String
    let openAnimation: NativeBookOpenAnimation
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var panel: ReaderPanel?
    @State private var assistantOpen = false
    @State private var dialog: ReaderDialog?
    @State private var noteRequest: ReaderNoteRequest?
    @State private var dictionaryTerm: String?
    @State private var keyboardHeight: CGFloat = 0
    @State private var selectedText = ""
    @State private var seekProgress = 0.0
    @State private var dragging = false
    @State private var closing = false
    @State private var returningFromFootnote = false
    @State private var loadingChromeVisible = false
    @State private var searching = false
    @FocusState private var searchFocused: Bool
    @State private var searchQuery = ""
    @State private var searchResults = NativeReaderSearchAccumulator()
    @State private var searchGeneration = 0
    @State private var showingSearchResults = false
    @State private var jumpingToSearch = false
    @State private var searchComposing = false
    @State private var pendingSearch: Task<Void, Never>?
    @State private var searchCursor = NativeReaderSearchCursor()
    @State private var speechExpanded = true
    @State private var footnoteCardHeight: CGFloat = 140
    @State private var showingHighlightPalette = false
    @State private var suppressNextHighlightTap = false
    /// PostScript name of the body font, so the reading footer matches the page.
    @State private var bodyFontName = "KingHwaOldSong"
    @State private var systemLowPowerMode = ProcessInfo.processInfo.isLowPowerModeEnabled
    @State private var acknowledgedFrameRevision: Int64?
    @State private var readerEntryPrepared = false
    private var entryInteractive: Bool { readerEntryPrepared || openAnimation == .cover }

    @StateObject private var openTransitionDriver = NativeBookTransitionDriver(initialProgress: 0)
    private var activeOpenTransition: NativeBookOpenTransition? {
        guard let transition = store.openingBookTransition, transition.book.id == book.id else { return nil }
        return transition
    }
    private var powerSavingActive: Bool {
        store.preferences.isPowerSaving(systemLowPower: systemLowPowerMode)
    }
    private let chromeSideInset: CGFloat = 8
    private let footerHeight: CGFloat = 24
    private var reader: ReaderSnapshot { session.state }
    private var speechActive: Bool { reader.speechState != 0 && reader.speechState != 4 }
    private var footerFont: Font {
        reader.isPdf
            ? NativeTheme.font(11.5, relativeTo: .caption2)
            : .custom(bodyFontName, size: 11.5, relativeTo: .caption2)
    }
    private var footerVisible: Bool { store.preferences.readerProgressDisplay != 4 }
    /// The footer is drawn over the bottom of the reader surface. The page
    /// already keeps its own bottom margin, so only the part of the footer
    /// strip that the margin does not cover has to be reserved. Reserving the
    /// whole footer height again left a blank band under the last line and
    /// pushed lines that would otherwise fit onto the next page.
    private var readerBottomInset: CGFloat {
        guard footerVisible else { return 0 }
        if reader.isManga { return 0 }
        guard !reader.isPdf else { return footerHeight }
        let bodyInset = min(64, max(16, store.preferences.readerBodyPadding))
        return max(0, footerHeight - CGFloat(bodyInset))
    }
    /// The app's effective appearance, which is what "follow" means. The
    /// reader's own environment is not a safe source: the ink paper forces the
    /// reader presentation dark, and that forced scheme can outlive the paper
    /// choice, which would keep "follow" on the dark paper.
    private var effectiveReaderTheme: Int {
        if (reader.isManga && store.preferences.mangaBlackBackground) || powerSavingActive { return 5 }
        return store.preferences.readerThemeFollowsAppearance
            ? (appColorScheme == .dark ? 5 : 0)
            : store.preferences.readerTheme
    }
    /// Resolve a concrete scheme even when the app follows the system. Passing
    /// nil can let the presentation retain its previous dark override after the
    /// ink paper, so switching back to "follow" could stay dark.
    private var readerColorScheme: ColorScheme {
        if (reader.isManga && store.preferences.mangaBlackBackground) || powerSavingActive { return .dark }
        return store.preferences.readerThemeFollowsAppearance
            ? appColorScheme
            : (store.preferences.readerTheme == 5 ? .dark : .light)
    }
    private var paper: Color {
        if (reader.isManga && store.preferences.mangaBlackBackground) || powerSavingActive { return .black }
        return store.preferences.paper(for: effectiveReaderTheme, pureBlackOled: powerSavingActive)
    }
    // Compact screens present the assistant in a resizable sheet; regular wide
    // windows dock it as a side rail beside the reading surface.
    @State private var viewportWidth: CGFloat = 0
    @State private var viewportHeight: CGFloat = 0
    @State private var cachedTopInsetByOrientation: [Bool: CGFloat] = [:]
    @AppStorage("libzora.assistantSidebarWidth") private var savedAssistantSidebarWidth: Double = 0
    @State private var assistantSidebarDragWidth: CGFloat?
    @State private var assistantSidebarDragOrigin: CGFloat?
    @State private var assistantInputFocused = false
    private var docksAssistantSidebar: Bool {
        horizontalSizeClass == .regular && viewportWidth >= 760
    }
    private var usesTypographyPopover: Bool {
        horizontalSizeClass == .regular
    }
    private var typographyPopover: Binding<Bool> {
        Binding(
            get: { panel == .typography && usesTypographyPopover },
            set: { open in
                if !open && panel == .typography { panel = nil }
            }
        )
    }
    private var sheetPanel: Binding<ReaderPanel?> {
        Binding(
            get: {
                if let panel, !(panel == .typography && usesTypographyPopover) {
                    return panel
                }
                if assistantOpen && !docksAssistantSidebar {
                    return .assistant
                }
                return nil
            },
            set: { updated in
                if updated == nil {
                    if let panel, !(panel == .typography && usesTypographyPopover) {
                        self.panel = nil
                    } else if assistantOpen && !docksAssistantSidebar {
                        assistantOpen = false
                    }
                } else if updated == .assistant {
                    assistantOpen = true
                } else {
                    panel = updated
                }
            })
    }

    private func isSidebarOpen(contentWidth: CGFloat) -> Bool {
        assistantOpen && horizontalSizeClass == .regular && contentWidth >= 760
    }

    private func assistantSidebarWidth(contentWidth: CGFloat, preferredWidth: CGFloat? = nil) -> CGFloat {
        guard isSidebarOpen(contentWidth: contentWidth) else { return 0 }
        let saved = savedAssistantSidebarWidth > 0 ? CGFloat(savedAssistantSidebarWidth) : nil
        return NativeAdaptiveLayout.assistantSidebarWidth(contentWidth: contentWidth, preferredWidth: preferredWidth ?? saved)
    }

    private func resolvedSafeInsets(isLandscape: Bool) -> EdgeInsets {
        let base = store.windowSafeAreaInsets
        let live: EdgeInsets
        if let window = store.readerController.viewIfLoaded?.window
            ?? store.rootViewController?.viewIfLoaded?.window {
            let ui = window.safeAreaInsets
            let isRTL = UIView.userInterfaceLayoutDirection(for: window.semanticContentAttribute) == .rightToLeft
            live = EdgeInsets(
                top: ui.top,
                leading: isRTL ? ui.right : ui.left,
                bottom: ui.bottom,
                trailing: isRTL ? ui.left : ui.right
            )
        } else {
            live = base
        }
        let fallbackTop: CGFloat
        if isLandscape && horizontalSizeClass != .regular {
            fallbackTop = 0
        } else {
            fallbackTop = max(
                cachedTopInsetByOrientation[isLandscape] ?? 0,
                isLandscape ? 0 : store.readerInitialTopSafeInset,
                base.top,
                horizontalSizeClass == .regular ? 24 : 0
            )
        }
        return EdgeInsets(
            top: live.top > 0 ? live.top : fallbackTop,
            leading: live.leading > 0 ? live.leading : base.leading,
            bottom: live.bottom > 0 ? live.bottom : base.bottom,
            trailing: live.trailing > 0 ? live.trailing : base.trailing
        )
    }

    var body: some View {
        GeometryReader { fullGeometry in
            let rawContainerFrame = fullGeometry.frame(in: .global)
            let containerFrame = (rawContainerFrame.width > 1 && rawContainerFrame.height > 1)
                ? rawContainerFrame
                : store.viewportRect
            let fullSize = containerFrame.size
            let isLandscape = fullSize.width > fullSize.height
            let safeInsets = resolvedSafeInsets(isLandscape: isLandscape)
            let contentWidth = max(1, fullSize.width - safeInsets.leading - safeInsets.trailing)
            let safeHeight = max(1, fullSize.height - safeInsets.top - safeInsets.bottom)
            let sidebarOpen = isSidebarOpen(contentWidth: contentWidth)
            let layoutSidebarWidth = assistantSidebarWidth(contentWidth: contentWidth)
            let sidebarWidth = assistantSidebarWidth(contentWidth: contentWidth, preferredWidth: assistantSidebarDragWidth)
            let readingViewportWidth = max(1, contentWidth - layoutSidebarWidth)
            let openTransition = activeOpenTransition
            let bodyInset = min(64, max(16, store.preferences.readerBodyPadding))
            let surfaceTopInset: CGFloat = horizontalSizeClass == .regular
                ? max(0, 24 - CGFloat(bodyInset))
                : max(safeInsets.top, isLandscape ? 0 : (cachedTopInsetByOrientation[false] ?? store.readerInitialTopSafeInset))
            let readingViewportHeight = max(1, fullSize.height - surfaceTopInset - safeInsets.bottom - readerBottomInset)
            let isLandscapeSpread = NativeAdaptiveLayout.isLandscapeSpread(
                viewportSize: CGSize(width: readingViewportWidth, height: readingViewportHeight),
                isPdf: reader.isPdf,
                isManga: reader.isManga,
                verticalWriting: store.preferences.readerVerticalWriting,
                twoPageInLandscape: store.preferences.readerTwoPageInLandscape
            )
            let readingWidth = NativeAdaptiveLayout.readerWidth(
                viewportWidth: readingViewportWidth,
                isPdf: reader.isPdf || reader.isManga,
                isTwoPage: isLandscapeSpread
            )
            let sideColumnInset = max(0, (readingViewportWidth - readingWidth) / 2)
            let surfaceInsets = ReaderSurfaceInsets(
                left: safeInsets.leading + sideColumnInset,
                top: surfaceTopInset,
                right: safeInsets.trailing + sideColumnInset + layoutSidebarWidth,
                bottom: safeInsets.bottom + readerBottomInset
            )
            NativeBookOpenTransitionHost(
                transition: openTransition,
                driver: openTransitionDriver,
                containerFrame: containerFrame,
                colorScheme: readerColorScheme,
                paper: paper,
                content: ZStack(alignment: .topLeading) {
                    paper
                    ReaderSurface(controller: store.readerController)
                        .frame(width: fullSize.width, height: fullSize.height)
                        .overlay { ambientOverlay(size: fullSize) }
                        .allowsHitTesting(openTransition == nil && !showingSearchResults && !jumpingToSearch)
                    readerContent()
                        .frame(width: readingWidth)
                        .frame(width: readingViewportWidth)
                        .frame(maxHeight: .infinity)
                        .padding(.top, safeInsets.top)
                        .padding(.bottom, safeInsets.bottom)
                        .padding(.leading, safeInsets.leading)
                        .allowsHitTesting(openTransition == nil && !showingSearchResults && !jumpingToSearch)
                        .accessibilityHidden(showingSearchResults)
                    if showingSearchResults {
                        Button(action: dismissSearchResults) { Color.black.opacity(0.08) }
                            .buttonStyle(.plain).accessibilityLabel(L("收起搜索结果"))
                            .accessibilityIdentifier("reader.search.dismiss")
                    }
                    if entryInteractive {
                        topChromeOverlay(
                            safeInsets: safeInsets,
                            readingViewportWidth: readingViewportWidth,
                            openTransition: openTransition)
                            .zIndex(10)
                        bottomChromeOverlay(
                            safeInsets: safeInsets,
                            readingViewportWidth: readingViewportWidth,
                            openTransition: openTransition)
                            .zIndex(10)
                        floatingModalOverlay(
                            safeInsets: safeInsets,
                            readingViewportWidth: readingViewportWidth)
                            .zIndex(15)
                    }

                    if sidebarOpen {
                        let sidebarTopInset = max(
                            safeInsets.top,
                            cachedTopInsetByOrientation[isLandscape]
                                ?? cachedTopInsetByOrientation[!isLandscape]
                                ?? store.readerInitialTopSafeInset,
                            24
                        )
                        assistantSidebarOverlay(
                            contentWidth: contentWidth,
                            sidebarWidth: sidebarWidth,
                            topInset: sidebarTopInset,
                            safeInsets: safeInsets,
                            fullSize: fullSize)
                            .zIndex(20)
                    }
                }
                .frame(width: fullSize.width, height: fullSize.height)
                .background { readerKeyboardShortcuts }
                .onAppear {
                    viewportWidth = contentWidth
                    viewportHeight = safeHeight
                    if safeInsets.top > 0 {
                        cachedTopInsetByOrientation[isLandscape] = safeInsets.top
                    }
                }
                .onChange(of: fullGeometry.size) { _ in
                    viewportWidth = contentWidth
                    viewportHeight = safeHeight
                    if safeInsets.top > 0 {
                        cachedTopInsetByOrientation[isLandscape] = safeInsets.top
                    }
                }
                .task(id: "\(appearanceSessionId)|\(surfaceInsets)|\(Int(readingViewportWidth.rounded()))x\(Int(readingViewportHeight.rounded()))|\(reader.ready)|\(reader.presentationFrameRevision)") {
                    acknowledgedFrameRevision = nil
                    let reply: NativeReaderInsetsAcknowledgement? = try? await store.request("reader.insets", [
                        "sessionId": appearanceSessionId,
                        "left": Double(surfaceInsets.left),
                        "top": Double(surfaceInsets.top),
                        "right": Double(surfaceInsets.right),
                        "bottom": Double(surfaceInsets.bottom),
                        "viewportLeft": Double(safeInsets.leading),
                        "viewportWidth": Double(readingViewportWidth),
                        "viewportHeight": Double(readingViewportHeight)
                    ])
                    guard !Task.isCancelled, reply?.accepted == true,
                          session.appearanceSessionId == appearanceSessionId else { return }
                    acknowledgedFrameRevision = reply?.revision
                }
            )
            .modifier(NativeReaderEntryPresentation(store: store, session: session,
                sessionId: appearanceSessionId, animation: openAnimation, paper: paper,
                acknowledgedRevision: acknowledgedFrameRevision, prepared: $readerEntryPrepared))
        }
        .ignoresSafeArea()
        .task(id: "\(activeOpenTransition?.id.uuidString ?? "")|\(store.openingBookReadyId?.uuidString ?? "")|\(store.readerPresentationActive)") {
            guard let transition = activeOpenTransition else {
                openTransitionDriver.prepare(progress: 0)
                return
            }
            guard store.openingBookReadyId == transition.id, store.readerPresentationActive else {
                return
            }
            let duration = transition.isCentered
                ? NativeBookOpenTransitionPolicy.openFromCenterDuration
                : NativeBookOpenTransitionPolicy.openFromCoverDuration
            let transitionId = transition.id
            openTransitionDriver.start(id: transitionId, from: 0, to: 1, duration: duration) {
                if store.openingBookTransition?.id == transitionId {
                    store.openingBookTransition = nil
                }
                if store.openingBookReadyId == transitionId {
                    store.openingBookReadyId = nil
                }
                store.restoringReader = false
            }
        }
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .environment(\.symbolVariants, .none)
        .statusBarHidden(
            NativeBookOpenTransitionPolicy.shouldHideReaderStatusBar(
                chromeVisible: reader.chromeVisible,
                hasOpenTransition: activeOpenTransition != nil
            )
        )
        .preferredColorScheme(readerColorScheme)
        .interactiveDismissDisabled(true)
        .sheet(item: sheetPanel) { panel in
            Group {
                if panel == .typography {
                    NativeTypographyPanel(
                        store: store,
                        isPdf: reader.isPdf,
                        allowSpreadOptions: horizontalSizeClass == .regular
                    ) { self.panel = nil }
                } else {
                    NavigationView {
                        panelContent(panel)
                            .navigationTitle(panel == .toc ? book.title : panel.title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(L("完成")) { self.panel = nil } } }
                    }.navigationViewStyle(.stack)
                        .modifier(NativeReaderTocSheetBackground(
                            enabled: panel == .toc, paper: paper,
                            usesAmbient: store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode,
                            ambientOpacity: appColorScheme == .dark ? 0.14 : 0.10))
                        .modifier(NativeAssistantSheetDetents(enabled: panel == .assistant))
                }
            }
            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            .preferredColorScheme(readerColorScheme)
        }
        .onAppear {
            seekProgress = reader.progress
            syncAppAppearance(appColorScheme)
            syncSystemLowPowerMode()
        }
        .task(id: "\(book.id)|\(reader.ready)|\(reader.error != nil)|\(activeOpenTransition != nil)") {
            if reader.ready {
                loadingChromeVisible = false
                return
            }
            if reader.error != nil {
                loadingChromeVisible = true
                return
            }
            if activeOpenTransition != nil {
                loadingChromeVisible = false
                return
            }
            loadingChromeVisible = false
            try? await Task.sleep(nanoseconds: UInt64(NativeBookOpenTransitionPolicy.readerLoadingChromeGraceSeconds * 1_000_000_000))
            guard !Task.isCancelled, !reader.ready, activeOpenTransition == nil else { return }
            withAnimation(.easeOut(duration: 0.2)) { loadingChromeVisible = true }
        }
        .task(id: store.preferences.readerFontFamily) { await loadBodyFont() }
        // Forward the new value: a transition can invoke the callback on the
        // previous view, whose captured appColorScheme is one change behind.
        .onChange(of: appColorScheme) { syncAppAppearance($0) }
        .onChange(of: store.preferences.readerThemeFollowsAppearance) { _ in resyncAppAppearance() }
        .onChange(of: scenePhase) { phase in
            guard phase == .active else { return }
            resyncAppAppearance()
            // Power-state notifications are not delivered while suspended, so
            // re-read the flag when the app becomes active again.
            syncSystemLowPowerMode()
        }
        .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillChangeFrameNotification)) { note in
            guard let frame = note.userInfo?[UIResponder.keyboardFrameEndUserInfoKey] as? CGRect else { return }
            guard let window = store.readerController.viewIfLoaded?.window else {
                keyboardHeight = 0
                return
            }
            let localFrame = window.convert(frame, from: window.screen.coordinateSpace)
            let overlap = NativeAdaptiveLayout.keyboardOverlap(windowBounds: window.bounds, keyboardFrame: localFrame)
            keyboardHeight = max(0, overlap - window.safeAreaInsets.bottom)
        }
        .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillHideNotification)) { _ in
            keyboardHeight = 0
        }
        .onChange(of: reader.progress) { if !dragging { seekProgress = $0 } }
        .onChange(of: reader.selectedText == nil) { cleared in
            if cleared {
                showingHighlightPalette = false
                suppressNextHighlightTap = false
            }
        }
        .onChange(of: reader.chromeVisible) { if !$0 && searching && !jumpingToSearch { closeSearch() } }
        .onChange(of: reader.navigationRevision) { revision in
            _ = searchCursor.navigated(to: revision)
        }
        .onChange(of: searchQuery) { _ in
            if searching && !searchComposing { scheduleLiveSearch() }
        }
        .onReceive(NotificationCenter.default.publisher(for: UITextField.textDidChangeNotification)) { note in
            guard searching, searchFocused, let field = note.object as? UITextField, field.isFirstResponder else { return }
            searchComposing = field.markedTextRange != nil
            if searchComposing { pendingSearch?.cancel() }
            else { scheduleLiveSearch() }
        }
        .onDisappear {
            pendingSearch?.cancel()
            openTransitionDriver.cancel()
        }
        .onChange(of: session.dialog) { value in
            guard let value else { return }
            if searching { closeSearch() }
            if value.kind == "annotation" {
                dialog = nil
                panel = nil
                beginNote(
                    value.text,
                    annotationId: value.annotationId,
                    initial: value.note ?? "",
                    style: value.underlineStyle ?? store.preferences.readerHighlightStyle,
                    color: value.color ?? store.preferences.readerHighlightColor
                )
            } else {
                dialog = value
                panel = .info
            }
            session.dialog = nil
        }
        .task(id: searchGeneration) {
            let generation = searchGeneration
            let requestId = searchResults.searchId
            let query = searchResults.query
            guard !query.isEmpty, !requestId.isEmpty else { return }
            do {
                let _: Done = try await store.request(
                    "reader.searchStart", ["query": query, "searchId": requestId])
                guard !Task.isCancelled, generation == searchGeneration else {
                    store.perform { try? await store.command("reader.searchCancel", ["searchId": requestId]) }
                    return
                }
            } catch {
                guard !Task.isCancelled, generation == searchGeneration else { return }
                searchResults.failStart(error: error.localizedDescription)
            }
        }
        .onReceive(session.searchBatches) { batch in
            searchResults.apply(batch)
        }
    }
    private func resyncAppAppearance() {
        // Lifecycle and preference callbacks can also hold an older view. Only
        // resend the latest app appearance, never that view's captured scheme.
        guard let colorScheme = session.appearanceColorScheme else { return }
        syncAppAppearance(colorScheme)
    }
    private func syncSystemLowPowerMode() {
        let lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
        guard lowPower != systemLowPowerMode else { return }
        systemLowPowerMode = lowPower
        run("refreshPowerState")
    }
    private func syncAppAppearance(_ colorScheme: ColorScheme) {
        session.appearanceColorScheme = colorScheme
        session.appearanceRevision += 1
        // Always report the app's appearance. While the paper follows, the
        // renderer must not inherit the dark scheme forced by the ink paper.
        let args: [String: Any] = ["sessionId": appearanceSessionId, "revision": session.appearanceRevision,
                                   "dark": colorScheme == .dark]
        Task { try? await store.command("reader.appearance", args) }
    }
    /// Resolves the reading font to the PostScript name CoreText needs. The
    /// footer sits outside the rendered page, so without this it would always
    /// use the app's default face instead of the book's body font.
    private func loadBodyFont() async {
        guard !reader.isPdf else { return }
        guard let list: ReaderFontList = try? await store.request("reader.fonts") else { return }
        for option in list.fonts where option.kind == "download" && option.installed {
            guard let path = option.path, FileManager.default.fileExists(atPath: path) else { continue }
            CTFontManagerRegisterFontsForURL(URL(fileURLWithPath: path) as CFURL, .process, nil)
        }
        let name = list.fonts.first(where: { $0.key == store.preferences.readerFontFamily })?.previewName ?? ""
        bodyFontName = name.isEmpty ? "KingHwaOldSong" : name
    }
    private func readerContent() -> some View {
        ZStack {
            if !reader.ready && entryInteractive {
                VStack(spacing: 16) {
                    if (loadingChromeVisible && activeOpenTransition == nil) || reader.error != nil {
                        Text(book.title).font(NativeTheme.font(21, relativeTo: .title2)).multilineTextAlignment(.center)
                        if let error = reader.error { Text(error).foregroundStyle(NativeTheme.secondary).multilineTextAlignment(.center) }
                        else { ProgressView(L("正在打开书籍…")) }
                    }
                }.padding(32).frame(maxWidth: .infinity, maxHeight: .infinity).background(paper.ignoresSafeArea())
                    .accessibilityElement(children: .contain).accessibilityIdentifier("reader.loading")
                    .transition(.opacity)
            }
            VStack(spacing: 0) {
                Spacer(minLength: 0)
                if !reader.chromeVisible && footerVisible && reader.ready {
                    HStack {
                        Text(reader.chapter).lineLimit(1)
                        Spacer(minLength: 12)
                        Text(reader.position).monospacedDigit().lineLimit(1).minimumScaleFactor(0.85)
                            .layoutPriority(1).accessibilityIdentifier("reader.position")
                    }.font(footerFont).foregroundStyle(NativeTheme.muted).padding(.horizontal, 24).frame(height: footerHeight)
                        // The page already reserves this footer space. Keep it
                        // transparent so the continuous ambient wash remains
                        // visible instead of ending at an opaque paper strip.
                        .allowsHitTesting(false)
                        .accessibilityElement(children: .contain).accessibilityIdentifier("reader.footer")
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .NSProcessInfoPowerStateDidChange)
            .receive(on: DispatchQueue.main)) { _ in
            syncSystemLowPowerMode()
        }
        .animation(.easeOut(duration: 0.16), value: reader.ready)
        .accessibilityElement(children: .contain).accessibilityIdentifier("reader.page")
        .overlay {
            GeometryReader { geometry in
                if let text = reader.selectedText, panel == nil, noteRequest == nil, dictionaryTerm == nil, !showingSearchResults {
                    let barWidth = NativeSelectionToolbarLayout.width(in: geometry.size)
                    selectionBar(text, width: barWidth)
                        .frame(width: barWidth,
                               height: NativeSelectionToolbarLayout.height)
                        .position(NativeSelectionToolbarLayout.position(bounds: reader.selectionBounds,
                            in: geometry.size, chromeVisible: reader.chromeVisible,
                            speechVisible: speechActive && speechExpanded, bottomInset: readerBottomInset))

                    if showingHighlightPalette {
                        NativeSelectionHighlightBar(
                            currentStyle: NativeHighlightStyle.normalize(store.preferences.readerHighlightStyle),
                            currentColorHex: NativeHighlightPalette.normalizeHex(store.preferences.readerHighlightColor),
                            onSelectStyle: { style in
                                let color = style == .marker
                                    ? NativeHighlightPalette.normalizeHex(store.preferences.readerHighlightColor)
                                    : "#000000"
                                applySelectionHighlight(style: style.rawValue, color: color)
                            },
                            onSelectColor: { option in
                                applySelectionHighlight(style: NativeHighlightStyle.marker.rawValue, color: option.hex)
                            }
                        )
                        .position(NativeSelectionToolbarLayout.palettePosition(bounds: reader.selectionBounds,
                            in: geometry.size, chromeVisible: reader.chromeVisible,
                            speechVisible: speechActive && speechExpanded, bottomInset: readerBottomInset))
                    }
                }
            }
            .transaction { $0.animation = nil }
        }
        .overlay {
            GeometryReader { geometry in
                if let footnote = session.footnote, panel == nil,
                   !showingSearchResults, reader.selectedText == nil {
                    ZStack {
                        Button(action: dismissFootnote) {
                            Color.black.opacity(0.03).ignoresSafeArea()
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(L("关闭注释"))
                        NativeReaderFootnoteCard(
                            footnote: footnote,
                            cardWidth: NativeFootnoteCardLayout.width(in: geometry.size),
                            close: dismissFootnote, jump: jumpToFootnote)
                            .frame(width: NativeFootnoteCardLayout.width(in: geometry.size))
                            .background(GeometryReader { proxy in
                                Color.clear.preference(key: NativeFootnoteCardHeightKey.self, value: proxy.size.height)
                            })
                            .position(NativeFootnoteCardLayout.center(
                                anchor: footnote.anchor, cardHeight: footnoteCardHeight,
                                in: geometry.size, bottomInset: readerBottomInset,
                                chromeVisible: reader.chromeVisible))
                    }
                }
            }
        }
        .onPreferenceChange(NativeFootnoteCardHeightKey.self) { height in
            if height > 0 { footnoteCardHeight = height }
        }
        .fullScreenCover(item: $session.media) { media in
            NativeReaderMediaViewer(media: media) { session.media = nil }
        }
        .onChange(of: panel) { _ in dismissFootnote() }
        .onChange(of: assistantOpen) { _ in dismissFootnote() }
        .onChange(of: showingSearchResults) { _ in dismissFootnote() }
        .onChange(of: reader.selectedText) { _ in dismissFootnote() }
        .onChange(of: reader.position) { _ in dismissFootnote() }
        .onChange(of: reader.chapter) { _ in dismissFootnote() }
    }
    private func dismissFootnote() {
        guard session.footnote != nil else { return }
        session.footnote = nil
        store.perform { try? await store.command("reader.footnoteDismiss") }
    }
    private func jumpToFootnote() {
        guard session.footnote?.canJump == true else { return }
        session.footnote = nil
        store.perform { try await store.command("reader.footnoteJump") }
    }
    private func returnFromFootnote() {
        guard !returningFromFootnote else { return }
        returningFromFootnote = true
        store.perform {
            defer { returningFromFootnote = false }
            try await store.command("reader.footnoteReturn")
        }
    }
    private func cancelSearchRequest() {
        let requestId = searchResults.searchId
        searchResults.clearRequest()
        searchGeneration += 1
        if !requestId.isEmpty {
            store.perform { try? await store.command("reader.searchCancel", ["searchId": requestId]) }
        }
    }
    private func closeSearch() {
        pendingSearch?.cancel(); pendingSearch = nil
        searchFocused = false; searchComposing = false; showingSearchResults = false
        withAnimation(.easeOut(duration: 0.2)) { searching = false }
    }
    private func dismissSearchResults() { closeSearch() }

    private var trimmedSearchQuery: String {
        searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
    }
    private func scheduleLiveSearch() {
        guard searching, !searchComposing else { return }
        pendingSearch?.cancel(); pendingSearch = nil
        let query = trimmedSearchQuery
        showingSearchResults = !query.isEmpty
        if query == searchResults.query, !searchResults.searchId.isEmpty { return }

        cancelSearchRequest()
        exitSearchNavigation()
        searchResults.reset(searchId: "", query: query, busy: !query.isEmpty)
        guard !query.isEmpty else { return }
        pendingSearch = Task {
            do { try await Task.sleep(nanoseconds: 250_000_000) }
            catch { return }
            guard !Task.isCancelled, searching, !searchComposing, query == trimmedSearchQuery else { return }
            startSearchRequest()
        }
    }
    private func startSearchRequest() {
        searchResults.startRequest(searchId: UUID().uuidString)
        searchGeneration += 1
    }
    private func submitSearch() {
        guard !jumpingToSearch, !searchComposing, !trimmedSearchQuery.isEmpty else { return }
        scheduleLiveSearch()
        searchFocused = false
        pendingSearch?.cancel(); pendingSearch = nil
        if searchResults.searchId.isEmpty { startSearchRequest() }
    }
    private func continueSearch() {
        guard !jumpingToSearch, !searchResults.busy, searchResults.hasMore,
              !searchResults.searchId.isEmpty else { return }
        searchFocused = false
        let requestId = searchResults.searchId
        searchResults.beginContinue()
        store.perform {
            do {
                let _: Done = try await store.request(
                    "reader.searchMore", ["searchId": requestId])
            } catch {
                if requestId == searchResults.searchId {
                    searchResults.failContinue(error: error.localizedDescription)
                }
            }
        }
    }
    private func openSearchResults(focus: Bool) {
        withAnimation(.easeOut(duration: 0.2)) { searching = true }
        searchFocused = focus; searchComposing = false
        scheduleLiveSearch()
    }
    private func exitSearchNavigation() {
        guard searchCursor.hitId != nil else { return }
        searchCursor.clear()
        run("searchClearHighlight")
    }
    private func jumpToSearch(_ hit: ReaderSearchHit) {
        guard !jumpingToSearch, !searchResults.searchId.isEmpty else { return }
        searchFocused = false
        jumpingToSearch = true
        let requestId = searchResults.searchId
        let revision = reader.navigationRevision
        store.perform {
            defer { jumpingToSearch = false }
            let reply: ReaderSearchJumpReply = try await store.request(
                "reader.searchJump", ["id": hit.id, "searchId": requestId])
            guard reply.jumped, requestId == searchResults.searchId, revision == reader.navigationRevision else { return }
            searchCursor.select(hit.id, navigationRevision: revision)
            closeSearch()
        }
    }
    private func moveSearchHit(_ direction: Int) {
        guard let id = searchCursor.neighbor(direction, in: searchResults.hits.map(\.id)),
              let hit = searchResults.hits.first(where: { $0.id == id }) else { return }
        jumpToSearch(hit)
    }
    private func searchNavigationBar(index: Int) -> some View {
        HStack(spacing: 0) {
            Button { moveSearchHit(-1) } label: {
                Image(systemName: "chevron.left").frame(width: 44, height: 44).contentShape(Rectangle())
            }.disabled(index == 0 || jumpingToSearch)
                .accessibilityLabel(L("上一条搜索结果")).accessibilityIdentifier("reader.search.previous")
            Button { openSearchResults(focus: false) } label: {
                HStack(spacing: 4) {
                    Image(systemName: "magnifyingglass").font(.system(size: 11))
                    let count = searchResults.hits.count
                    let moreSuffix = (!searchResults.isFinal || searchResults.hasMore) ? "+" : ""
                    Text("\(index + 1)/\(count)\(moreSuffix)")
                        .monospacedDigit().font(NativeTheme.font(13)).lineLimit(1)
                }.frame(minWidth: 76, minHeight: 44).contentShape(Rectangle())
            }.accessibilityLabel(L("第 {0} 条，共 {1} 条搜索结果", index + 1, searchResults.hits.count))
                .accessibilityIdentifier("reader.search.cursor")
            Button { moveSearchHit(1) } label: {
                Image(systemName: "chevron.right").frame(width: 44, height: 44).contentShape(Rectangle())
            }.disabled(index + 1 >= searchResults.hits.count || jumpingToSearch)
                .accessibilityLabel(L("下一条搜索结果")).accessibilityIdentifier("reader.search.next")
            Button(action: exitSearchNavigation) {
                Image(systemName: "xmark").font(.system(size: 14))
                    .frame(width: 44, height: 44).contentShape(Rectangle())
            }.disabled(jumpingToSearch).accessibilityLabel(L("退出搜索条目导航")).accessibilityIdentifier("reader.search.navigation.close")
        }.font(.system(size: 17)).buttonStyle(.plain)
            .modifier(NativeFloatingSurface(backgroundOpacity: 0.58, shadowStrength: 0.35, verticalInset: 4))
            .accessibilityElement(children: .contain).accessibilityIdentifier("reader.search.navigation")
    }

    private var topBar: some View {
        HStack(spacing: 0) {
            Button {
                if searching { closeSearch(); return }
                closing = true
                Task { await store.closeReader(); closing = false }
            } label: {
                Group { if closing { ProgressView() } else { Image(systemName: "chevron.left") } }
                    .frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
                .disabled(closing || jumpingToSearch).accessibilityLabel(searching ? L("关闭搜索") : L("退出阅读")).accessibilityIdentifier("reader.close")
            ZStack(alignment: .trailing) {
                if searching {
                    NativeSearchField(text: $searchQuery, prompt: L("搜索全书"), focused: $searchFocused,
                                      identifier: "reader.search.query", submit: submitSearch)
                        .disabled(jumpingToSearch)
                        .transition(.move(edge: .trailing).combined(with: .opacity))
                } else {
                    Text(book.title).font(NativeTheme.font(14)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.frame(maxWidth: .infinity, minHeight: 44).padding(.horizontal, 2).clipped()
            if searching {
                Button(action: closeSearch) {
                    Image(systemName: "xmark").font(.system(size: 15)).frame(width: 44, height: 44)
                        .contentShape(Rectangle())
                }
                    .disabled(jumpingToSearch).accessibilityLabel(L("关闭搜索")).accessibilityIdentifier("reader.search.close")
            } else {
                Button {
                    openSearchResults(focus: true)
                } label: {
                    Image(systemName: "magnifyingglass").frame(width: 44, height: 44)
                        .contentShape(Rectangle())
                }.disabled(!reader.ready).accessibilityLabel(L("搜索全书")).accessibilityIdentifier("reader.search")
            }
            Button { run("bookmark") } label: {
                Image(systemName: reader.bookmark ? "bookmark.fill" : "bookmark").frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
                .disabled(!reader.ready).accessibilityLabel(L("书签")).accessibilityIdentifier("reader.bookmark")
        }.font(.system(size: 19, weight: .regular)).buttonStyle(NativeGlassToolbarButtonStyle()).padding(4)
            .modifier(NativeLiquidGlassSurface(shape: Capsule()))
            .accessibilityElement(children: .contain).accessibilityIdentifier("reader.topToolbar")
            .padding(.horizontal, chromeSideInset).padding(.top, 8)
    }
    private var bottomBar: some View {
        VStack(spacing: 0) {
            HStack { Text(reader.chapter).lineLimit(1); Spacer(); Text("\(seekProgress, specifier: "%.0f")%") }
                .font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
            HStack(spacing: 0) {
                Button { run("chapter", ["direction": -1]) } label: {
                    Image(systemName: "backward.end").frame(width: 36, height: 44)
                        .contentShape(Rectangle())
                }.accessibilityLabel(L("上一章或上一页"))
                Slider(value: $seekProgress, in: 0...100, onEditingChanged: { editing in
                    dragging = editing
                    if !editing { run("seek", ["progress": seekProgress]) }
                }).accessibilityLabel(L("阅读进度")).accessibilityIdentifier("reader.progress")
                Button { run("chapter", ["direction": 1]) } label: {
                    Image(systemName: "forward.end").frame(width: 36, height: 44)
                        .contentShape(Rectangle())
                }.accessibilityLabel(L("下一章或下一页"))
            }.font(.system(size: 17, weight: .regular)).buttonStyle(NativeGlassToolbarButtonStyle()).padding(.horizontal, -10).padding(.top, 2)
            HStack(spacing: 0) {
                tool(L("目录"), "list.bullet", identifier: "reader.目录") { panel = .toc }
                tool(reader.speechState == 0 || reader.speechState == 4 ? L("听书") : L("停止"), "headphones",
                     identifier: reader.speechState == 0 || reader.speechState == 4 ? "reader.听书" : "reader.停止") { run("speech") }
                tool("AI", "sparkles", identifier: "reader.AI") { openAssistant(with: "") }
                tool(reader.isPdf ? L("显示") : L("排版"), "Aa", identifier: reader.isPdf ? "reader.显示" : "reader.排版") { panel = .typography }
                    .popover(isPresented: typographyPopover, arrowEdge: .bottom) {
                        NativeTypographyPanel(
                            store: store,
                            isPdf: reader.isPdf,
                            allowSpreadOptions: horizontalSizeClass == .regular,
                            isPopover: true,
                            popoverMaxHeight: viewportHeight > 0 ? viewportHeight : 800
                        ) { self.panel = nil }
                            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
                            .preferredColorScheme(readerColorScheme)
                    }
            }
        }.padding(.horizontal, 18).padding(.top, 14).padding(.bottom, 8)
            .modifier(NativeLiquidGlassSurface(shape: RoundedRectangle(cornerRadius: 26, style: .continuous)))
            .accessibilityElement(children: .contain).accessibilityIdentifier("reader.toolbar")
            .padding(.horizontal, chromeSideInset)
            .frame(maxWidth: NativeAdaptiveLayout.readerToolsMaxWidth)
            .frame(maxWidth: .infinity)
    }
    private func selectionBar(_ text: String, width: CGFloat) -> some View {
        let actions = HStack(spacing: 0) {
            selectionAction(L("复制"), "doc.on.doc", identifier: "reader.复制") {
                UIPasteboard.general.string = text; run("clearSelection"); store.showToast(L("已复制"))
            }
            selectionUnderlineAction(
                action: {
                    let style = NativeHighlightStyle.normalize(store.preferences.readerHighlightStyle)
                    let color = style == .marker
                        ? NativeHighlightPalette.normalizeHex(store.preferences.readerHighlightColor)
                        : "#000000"
                    applySelectionHighlight(style: style.rawValue, color: color)
                },
                onLongPress: {
                    UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                    showingHighlightPalette = true
                }
            )
            selectionAction(L("笔记"), "square.and.pencil", identifier: "reader.笔记", xOffset: 0.2, yOffset: -1.5) { beginNote(text) }
            selectionAction(L("搜索"), "magnifyingglass", identifier: "reader.搜索", xOffset: 0.17) {
                store.perform {
                    try await store.command("reader.clearSelection")
                    try await store.command("reader.showChrome")
                    searching = true; searchQuery = text; submitSearch()
                }
            }
            selectionAction(L("词典"), "book", identifier: "reader.词典", xOffset: 0.33) {
                selectedText = text
                dictionaryTerm = text
            }
            selectionAction("AI", "sparkles", identifier: "reader.AI", xOffset: 0.33) { openAssistant(with: text) }
            selectionAction(L("翻译"), "character.bubble", identifier: "reader.翻译", xOffset: 0.25) { selectedText = text; panel = .translation }
            selectionAction(L("朗读"), "speaker.wave.2", identifier: "reader.朗读", xOffset: 0.17) { run("selectionSpeech") }
        }
        return Group {
            if width >= 336 {
                actions
                    .padding(.horizontal, 10)
                    .frame(width: width, height: NativeSelectionToolbarLayout.height, alignment: .center)
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    actions
                        .padding(.horizontal, 10)
                        .frame(minWidth: max(width, 336), minHeight: NativeSelectionToolbarLayout.height, alignment: .center)
                }
            }
        }
        .modifier(NativeLiquidGlassSurface(shape: Capsule()))
        .accessibilityElement(children: .contain).accessibilityIdentifier("reader.selection.toolbar")
    }
    private func selectionAction(
        _ title: String,
        _ symbol: String,
        identifier: String,
        xOffset: CGFloat = 0,
        yOffset: CGFloat = 0,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 18, weight: .regular))
                .offset(x: xOffset, y: yOffset)
                .frame(width: 22, height: 22, alignment: .center)
                .frame(maxWidth: .infinity, minHeight: 44, alignment: .center)
                .contentShape(Rectangle())
        }.buttonStyle(NativeGlassToolbarButtonStyle()).accessibilityLabel(title).accessibilityIdentifier(identifier)
    }
    private func selectionUnderlineAction(
        action: @escaping () -> Void,
        onLongPress: @escaping () -> Void
    ) -> some View {
        let accent = NativeHighlightPalette.accentColor(
            style: store.preferences.readerHighlightStyle,
            hex: store.preferences.readerHighlightColor
        )
        return Button {
            if suppressNextHighlightTap {
                suppressNextHighlightTap = false
                return
            }
            action()
        } label: {
            ZStack {
                NativeSelectionUnderlineGlyph(includeBaseline: accent == nil)
                    .stroke(style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
                if let accent {
                    // Keep active highlight feedback centered directly in the underline bar
                    // instead of a corner dot that pulls the glyph off-center.
                    Capsule()
                        .fill(accent)
                        .frame(width: 18, height: 3.0)
                        .overlay(Capsule().stroke(NativeTheme.ink.opacity(0.22), lineWidth: 0.45))
                        .offset(y: 8.0)
                }
            }
            .frame(width: 18, height: 18, alignment: .center)
            .frame(width: 22, height: 22, alignment: .center)
            .frame(maxWidth: .infinity, minHeight: 44, alignment: .center)
            .contentShape(Rectangle())
        }
        .buttonStyle(NativeGlassToolbarButtonStyle())
        .simultaneousGesture(
            LongPressGesture(minimumDuration: 0.32, maximumDistance: 18)
                .onEnded { _ in
                    suppressNextHighlightTap = true
                    onLongPress()
                }
        )
        .accessibilityLabel(L("划线"))
        .accessibilityIdentifier("reader.划线")
    }
    private func tool(_ title: String, _ image: String, identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 2) {
                if image == "Aa" { Text("Aa").font(NativeTheme.font(16)).frame(height: 21) }
                else { Image(systemName: image).font(.system(size: 21, weight: .regular)).frame(height: 21) }
                Text(title).font(NativeTheme.font(11, relativeTo: .caption2))
                    .lineLimit(1).minimumScaleFactor(0.72)
            }.frame(maxWidth: .infinity).frame(minHeight: 52).contentShape(Rectangle())
        }
            .buttonStyle(NativeGlassToolbarButtonStyle()).accessibilityIdentifier(identifier)
    }
    @ViewBuilder private func panelContent(_ panel: ReaderPanel) -> some View {
        switch panel {
        case .toc: NativeReaderTocView(store: store, isPdf: reader.isPdf, paper: paper,
                                      usesAmbient: store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode,
                                      ambientOpacity: appColorScheme == .dark ? 0.14 : 0.10,
                                      close: { self.panel = nil })
        case .typography: NativeTypography(store: store, isPdf: reader.isPdf)
        case .speech: NativeSpeechSettings(store: store, close: { self.panel = nil })
        case .assistant: NativeAssistant(
            store: store,
            chat: session.aiChat(for: book.id),
            book: book,
            initialSelection: selectedText,
            isSidebar: false,
            onJumpToSource: jumpToAiSource,
            onClose: { assistantOpen = false })
        case .translation: NativeTranslation(store: store, text: selectedText)
        case .info: ScrollView { Text(dialog?.text ?? "").textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(24) }
        }
    }

    private func beginNote(
        _ quote: String,
        annotationId: String? = nil,
        initial: String = "",
        style: String? = nil,
        color: String? = nil
    ) {
        selectedText = quote
        let resolvedStyle = NativeHighlightStyle.normalize(style ?? store.preferences.readerHighlightStyle).rawValue
        let resolvedColor = NativeHighlightPalette.normalizeHex(
            color ?? (resolvedStyle == NativeHighlightStyle.marker.rawValue ? store.preferences.readerHighlightColor : "#000000")
        )
        noteRequest = ReaderNoteRequest(
            quote: quote,
            annotationId: annotationId,
            initialNote: initial,
            initialStyle: resolvedStyle,
            initialColor: resolvedColor
        )
    }

    private func applySelectionHighlight(style: String, color: String) {
        showingHighlightPalette = false
        suppressNextHighlightTap = false
        run("annotate", ["note": "", "style": style, "color": color])
    }

    private func dismissNoteEditor() {
        noteRequest = nil
    }

    private func dismissDictionaryCard() {
        dictionaryTerm = nil
    }

    @ViewBuilder
    private func ambientOverlay(size: CGSize) -> some View {
        if !powerSavingActive && store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode {
            // The wash spans the entire window, including safe areas and
            // iPad margins, without shrinking the transition surface.
            NativeAmbientWash()
                .opacity(appColorScheme == .dark ? 0.14 : 0.10)
                .frame(width: size.width, height: size.height)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }

    @ViewBuilder
    private func topChromeOverlay(
        safeInsets: EdgeInsets,
        readingViewportWidth: CGFloat,
        openTransition: NativeBookOpenTransition?
    ) -> some View {
        VStack(spacing: 8) {
            if openTransition == nil && NativeBookOpenTransitionPolicy.shouldShowReaderTopBar(
                chromeVisible: reader.chromeVisible,
                ready: reader.ready,
                searching: searching,
                hasError: reader.error != nil,
                loadingChromeVisible: loadingChromeVisible
            ) { topBar }
            if let id = searchCursor.hitId, !searching, let index = searchResults.hits.firstIndex(where: { $0.id == id }) {
                searchNavigationBar(index: index)
                    .padding(.top, reader.chromeVisible ? 0 : 8)
                    .transition(.opacity)
            }
            if reader.footnoteReturnVisible {
                Button(action: returnFromFootnote) {
                    HStack(spacing: 6) {
                        Image(systemName: "arrow.uturn.backward")
                            .font(.system(size: 12, weight: .semibold))
                        Text(L("返回原文")).font(NativeTheme.font(13))
                    }
                    .padding(.horizontal, 16)
                    .frame(minHeight: 40)
                    .contentShape(RoundedRectangle(cornerRadius: 16))
                }
                .buttonStyle(.plain)
                .disabled(returningFromFootnote)
                .modifier(NativeFloatingSurface())
                .contentShape(RoundedRectangle(cornerRadius: 16))
                .padding(.top, reader.chromeVisible ? 0 : 8)
                .accessibilityIdentifier("reader.footnote.return")
                .transition(.opacity)
            }
            if showingSearchResults {
                NativeReaderSearchResults(hits: searchResults.hits, query: searchResults.query, busy: searchResults.busy,
                                          loadingMore: searchResults.loadingMore, hasMore: searchResults.hasMore,
                                          isPdf: reader.isPdf, scanned: searchResults.scanned, total: searchResults.total,
                                          error: searchResults.error, jumping: jumpingToSearch,
                                          pick: jumpToSearch, loadMore: continueSearch, close: dismissSearchResults)
                    .frame(height: searchResults.hits.isEmpty ? 58 : min(340, CGFloat(searchResults.hits.count) * 96 + (searchResults.hasMore ? 96 : 38)))
                    .modifier(NativeFloatingSurface()).padding(.horizontal, 12)
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: NativeAdaptiveLayout.readerToolsMaxWidth)
        .frame(width: readingViewportWidth)
        .frame(maxHeight: .infinity, alignment: .top)
        .padding(.top, safeInsets.top)
        .padding(.bottom, safeInsets.bottom)
        .padding(.leading, safeInsets.leading)
    }

    @ViewBuilder
    private func bottomChromeOverlay(
        safeInsets: EdgeInsets,
        readingViewportWidth: CGFloat,
        openTransition: NativeBookOpenTransition?
    ) -> some View {
        VStack(spacing: 0) {
            Spacer(minLength: 0)
            if openTransition == nil {
                // Toasts sit above whichever reader toolbars are showing instead of covering them.
                if let toast = store.toast {
                    NativeToast(text: toast)
                        .padding(.bottom, 12)
                }
                if speechActive {
                    NativeReaderSpeechControls(state: reader.speechState, status: reader.speechStatus,
                        speed: reader.speechSpeed,
                        sleepMinutesRemaining: reader.speechSleepMinutesRemaining,
                        sleepAfterChapter: reader.speechSleepAfterChapter,
                        suspended: panel != nil || showingSearchResults || (assistantOpen && !docksAssistantSidebar), expanded: $speechExpanded,
                        setSpeed: { next in run("speechSpeed", ["speed": next]) },
                        setSleepTimer: { mins, afterChapter in run("speechSleep", ["minutes": mins, "afterChapter": afterChapter]) },
                        settings: { panel = .speech }, pause: { run("speechPause") }, stop: { run("speechStop") })
                }
                if reader.chromeVisible && reader.ready {
                    bottomBar
                } else if footerVisible && reader.ready {
                    Color.clear
                        .frame(height: footerHeight)
                        .allowsHitTesting(false)
                }
            }
        }
        .frame(width: readingViewportWidth)
        .frame(maxHeight: .infinity, alignment: .bottom)
        .padding(.top, safeInsets.top)
        .padding(.bottom, safeInsets.bottom)
        .padding(.leading, safeInsets.leading)
    }

    @ViewBuilder
    private func floatingModalOverlay(
        safeInsets: EdgeInsets,
        readingViewportWidth: CGFloat
    ) -> some View {
        if let term = dictionaryTerm {
            let bottomPad = keyboardHeight > 0 ? keyboardHeight + 12 : CGFloat(0)
            ZStack(alignment: keyboardHeight > 0 ? .bottom : .center) {
                Color.black.opacity(0.08).ignoresSafeArea()
                    .onTapGesture { dismissDictionaryCard() }
                NativeFloatingDictionaryCard(store: store, initialTerm: term) {
                    dismissDictionaryCard()
                }
                .frame(width: min(440, NativeAdaptiveLayout.noteWidth(viewportWidth: readingViewportWidth)))
                .padding(.horizontal, chromeSideInset)
                .padding(.bottom, bottomPad)
            }
            .frame(width: readingViewportWidth)
            .frame(maxHeight: .infinity)
            .padding(.top, safeInsets.top)
            .padding(.bottom, safeInsets.bottom)
            .padding(.leading, safeInsets.leading)
            .animation(.easeOut(duration: 0.22), value: keyboardHeight)
            .transition(.opacity)
        } else if let request = noteRequest {
            ZStack(alignment: .bottom) {
                Color.black.opacity(0.06).ignoresSafeArea()
                    .onTapGesture { dismissNoteEditor() }
                NativeFloatingNoteEditor(store: store, request: request) {
                    dismissNoteEditor()
                }
                // Match the reader chrome's side inset so the floating
                // note card lines up with the top and bottom bars.
                .frame(width: NativeAdaptiveLayout.noteWidth(viewportWidth: readingViewportWidth))
                .padding(.horizontal, chromeSideInset)
                .padding(.bottom, keyboardHeight + 12)
            }
            .frame(width: readingViewportWidth)
            .frame(maxHeight: .infinity)
            .padding(.top, safeInsets.top)
            .padding(.bottom, safeInsets.bottom)
            .padding(.leading, safeInsets.leading)
            .animation(.easeOut(duration: 0.22), value: keyboardHeight)
        }
    }

    private func assistantSidebarOverlay(
        contentWidth: CGFloat,
        sidebarWidth: CGFloat,
        topInset: CGFloat,
        safeInsets: EdgeInsets,
        fullSize: CGSize
    ) -> some View {
        let usesAmbient = store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode
        let resizing = assistantSidebarDragWidth != nil
        let assistantKeyboardInset = assistantInputFocused ? keyboardHeight : 0
        return HStack(spacing: 0) {
            Spacer(minLength: 0)
            ZStack(alignment: .leading) {
                HStack(spacing: 0) {
                    Rectangle()
                        .fill(resizing ? NativeTheme.ink.opacity(0.35) : NativeTheme.divider)
                        .frame(width: resizing ? 1.5 : 0.5)
                    NativeAssistant(
                        store: store,
                        chat: session.aiChat(for: book.id),
                        book: book,
                        initialSelection: selectedText,
                        isSidebar: true,
                        backgroundColor: .clear,
                        onJumpToSource: jumpToAiSource,
                        onClose: { assistantOpen = false },
                        onInputFocusChanged: { assistantInputFocused = $0 })
                }
                .frame(width: sidebarWidth)
                .padding(.top, topInset)
                .padding(.bottom, safeInsets.bottom + assistantKeyboardInset)
                .padding(.trailing, safeInsets.trailing)
                .background {
                    NativeReaderPaperBackground(
                        paper: paper,
                        usesAmbient: usesAmbient,
                        ambientOpacity: appColorScheme == .dark ? 0.14 : 0.10)
                }
                .contentShape(Rectangle())
                .animation(.easeOut(duration: 0.22), value: assistantKeyboardInset)
                .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
                .preferredColorScheme(readerColorScheme)

                assistantResizeHandle(contentWidth: contentWidth, currentWidth: sidebarWidth)
            }
        }
        .frame(width: fullSize.width, height: fullSize.height)
    }

    private func assistantResizeHandle(contentWidth: CGFloat, currentWidth: CGFloat) -> some View {
        let resizing = assistantSidebarDragWidth != nil
        return ZStack(alignment: .leading) {
            Capsule()
                .fill(resizing ? NativeTheme.ink.opacity(0.55) : NativeTheme.muted.opacity(0.35))
                .frame(width: 3.5, height: 34)
                .padding(.leading, 3.5)
        }
        .frame(width: 16)
        .frame(maxHeight: .infinity)
        .contentShape(Rectangle())
        .accessibilityLabel(L("调整 AI 助手宽度"))
        .accessibilityIdentifier("reader.assistant.resizeHandle")
        .onTapGesture(count: 2) {
            assistantSidebarDragOrigin = nil
            assistantSidebarDragWidth = nil
            savedAssistantSidebarWidth = 0
        }
        .gesture(
            DragGesture(minimumDistance: 2, coordinateSpace: .global)
                .onChanged { value in
                    if assistantSidebarDragOrigin == nil {
                        assistantSidebarDragOrigin = currentWidth
                    }
                    let origin = assistantSidebarDragOrigin ?? currentWidth
                    assistantSidebarDragWidth = NativeAdaptiveLayout.assistantSidebarWidth(
                        contentWidth: contentWidth,
                        preferredWidth: origin - value.translation.width
                    )
                }
                .onEnded { value in
                    let origin = assistantSidebarDragOrigin ?? currentWidth
                    let resolved = NativeAdaptiveLayout.assistantSidebarWidth(
                        contentWidth: contentWidth,
                        preferredWidth: origin - value.translation.width
                    )
                    assistantSidebarDragOrigin = nil
                    assistantSidebarDragWidth = nil
                    savedAssistantSidebarWidth = Double(resolved)
                }
        )
    }

    private var keyboardPagingReversed: Bool {
        !reader.isPdf && (store.preferences.readerVerticalWriting
            || (store.preferences.mangaMode && store.preferences.mangaDirection == 0))
    }

    private var keyboardPagingEnabled: Bool {
        reader.ready && !closing && !searching && !showingSearchResults
            && noteRequest == nil && dictionaryTerm == nil && session.media == nil
            && panel == nil && !assistantInputFocused && (!assistantOpen || docksAssistantSidebar)
    }

    @ViewBuilder
    private var readerKeyboardShortcuts: some View {
        Group {
            if keyboardPagingEnabled {
                Button("") { run("turn", ["direction": keyboardPagingReversed ? 1 : -1]) }
                    .keyboardShortcut(.leftArrow, modifiers: [])
                Button("") { run("turn", ["direction": keyboardPagingReversed ? -1 : 1]) }
                    .keyboardShortcut(.rightArrow, modifiers: [])
                Button("") { run("turn", ["direction": 1]) }
                    .keyboardShortcut(.space, modifiers: [])
            }
            if reader.ready && !closing && noteRequest == nil && dictionaryTerm == nil && session.media == nil {
                Button("") { openSearchResults(focus: true) }
                    .keyboardShortcut("f", modifiers: .command)
            }
            if !closing && noteRequest == nil && dictionaryTerm == nil && session.media == nil {
                Button("") { handleEscapeShortcut() }
                    .keyboardShortcut(.escape, modifiers: [])
                Button("") {
                    closing = true
                    Task { await store.closeReader(); closing = false }
                }
                .keyboardShortcut("w", modifiers: .command)
            }
        }
        .frame(width: 0, height: 0)
        .opacity(0)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func handleEscapeShortcut() {
        if searching || showingSearchResults { closeSearch(); return }
        if panel != nil { panel = nil; return }
        if assistantOpen { assistantOpen = false; return }
        if session.footnote != nil { dismissFootnote(); return }
        if reader.selectedText != nil { run("clearSelection"); return }
        closing = true
        Task { await store.closeReader(); closing = false }
    }

    private func jumpToAiSource(_ source: AiSource) {
        if !docksAssistantSidebar {
            assistantOpen = false
        }
        store.perform {
            let _: AiJumpResult = try await store.request("reader.aiJump", [
                "isPdf": source.isPdf,
                "chapterIndex": source.chapterIndex,
                "offset": source.offset,
                "pdfPage": source.pdfPage
            ])
        }
    }

    private func openAssistant(with selection: String) {
        selectedText = selection
        if reader.chromeVisible {
            store.perform {
                try? await store.command("reader.hideChrome")
                assistantOpen = true
            }
        } else {
            assistantOpen = true
        }
    }

    private func run(_ command: String, _ args: [String: Any] = [:]) { store.perform { try await store.command("reader.\(command)", args) } }
}

/// Strictly symmetric U + underline glyph matching the desktop selection bar proportions so
/// the icon centers cleanly alongside the other selection actions.
private struct NativeSelectionUnderlineGlyph: Shape {
    var includeBaseline = true

    func path(in rect: CGRect) -> Path {
        let w = rect.width
        let h = rect.height
        var path = Path()
        let left = rect.minX + w * 0.17
        let right = rect.maxX - w * 0.17
        let radius = (right - left) / 2
        let uTop = rect.minY + h * 0.03
        let arcCenterY = rect.minY + h * 0.73 - radius
        path.move(to: CGPoint(x: left, y: uTop))
        path.addLine(to: CGPoint(x: left, y: arcCenterY))
        path.addArc(center: CGPoint(x: rect.midX, y: arcCenterY),
                    radius: radius,
                    startAngle: .degrees(180),
                    endAngle: .degrees(0),
                    clockwise: true)
        path.addLine(to: CGPoint(x: right, y: uTop))
        if includeBaseline {
            let lineY = rect.maxY - h * 0.03
            path.move(to: CGPoint(x: rect.minX, y: lineY))
            path.addLine(to: CGPoint(x: rect.maxX, y: lineY))
        }
        return path
    }
}

struct NativeTypography: View {
    @ObservedObject var store: NativeStore
    let isPdf: Bool
    var allowSpreadOptions = UIDevice.current.userInterfaceIdiom == .pad
    var onOpenFontPicker: (() -> Void)? = nil
    @State private var fontPicker = false
    @State private var advancedTypography = false
    @State private var fontOptions: [ReaderFontOption] = []
    private var currentFontName: String {
        let key = store.preferences.readerFontFamily
        if let option = fontOptions.first(where: { $0.key == key }) { return option.name }
        if key.hasPrefix("system:") { return String(key.dropFirst("system:".count)) }
        if key.hasPrefix("catalog:") { return L("已下载字体") }
        return "京华老宋体"
    }
    private var currentFontPreview: Font {
        let key = store.preferences.readerFontFamily
        if let option = fontOptions.first(where: { $0.key == key }), option.installed, !option.previewName.isEmpty {
            return .custom(option.previewName, size: 14)
        }
        if key == "京华老宋体" { return .custom("KingHwaOldSong", size: 14) }
        return NativeTheme.font(14)
    }
    private var advancedSummary: String {
        let key = store.preferences.readerLatinFontFamily
        var parts: [String] = []
        if !key.isEmpty && key != "auto" {
            if let option = fontOptions.first(where: { $0.kind == "latin" && $0.key == key }) {
                parts.append(option.name)
            } else if key.hasPrefix("system:") {
                parts.append(String(key.dropFirst("system:".count)))
            }
        }
        if store.preferences.readerAlignLeft { parts.append(L("左对齐")) }
        if store.preferences.readerPowerSavingMode == 2 { parts.append(L("省电")) }
        return parts.isEmpty ? L("默认") : parts.joined(separator: " · ")
    }
    private func preference<Value>(_ key: WritableKeyPath<MobilePreferences, Value>) -> Binding<Value> {
        Binding(get: { store.preferences[keyPath: key] }, set: { newValue in
            var value = store.preferences; value[keyPath: key] = newValue
            store.setPreferences(value)
        })
    }
    private var paperSelection: Binding<Int> {
        Binding(get: { store.preferences.readerThemeFollowsAppearance ? -1 : store.preferences.readerTheme }, set: { selection in
            var value = store.preferences
            value.readerThemeFollowsAppearance = selection == -1
            if selection != -1 { value.readerTheme = selection }
            store.setPreferences(value)
        })
    }
    var body: some View {
        ScrollView {
            VStack(spacing: 4) {
                row(L("纸张")) {
                    Picker(L("纸张"), selection: paperSelection) {
                        Text(L("跟随")).tag(-1); Text(L("白")).tag(0); Text(L("米")).tag(1); Text(L("黑")).tag(5)
                    }.pickerStyle(.segmented).accessibilityIdentifier("typography.paper")
                }
                row(L("翻页动画")) {
                    Picker(L("翻页动画"), selection: preference(\.readerPageAnimation)) { Text(L("无")).tag(0); Text(L("淡入")).tag(1); Text(L("平移")).tag(3) }.pickerStyle(.segmented)
                }
                row(L("底部进度")) {
                    Picker(L("底部进度"), selection: preference(\.readerProgressDisplay)) {
                        Text(L("页码")).tag(0); Text(L("章节")).tag(1)
                        Text(L("剩余时间")).tag(3); Text(L("关")).tag(4)
                    }.pickerStyle(.segmented).accessibilityIdentifier("typography.progressDisplay")
                }
                if isPdf {
                    row(L("阅读模式")) {
                        Picker(L("阅读模式"), selection: preference(\.pdfDisplayMode)) {
                            Text(L("单页")).tag(0)
                            Text(L("滚动")).tag(1)
                            if allowSpreadOptions {
                                Text(L("双页")).tag(2)
                            }
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.pdfDisplayMode")
                    }
                    row(L("画面适配")) {
                        Picker(L("画面适配"), selection: preference(\.pdfFitMode)) {
                            Text(L("整页")).tag(0)
                            Text(L("适宽")).tag(1)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.pdfFitMode")
                    }
                    row(L("自动裁边")) {
                        Picker(L("自动裁边"), selection: preference(\.pdfAutoCrop)) {
                            Text(L("关")).tag(false)
                            Text(L("开")).tag(true)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.pdfAutoCrop")
                    }
                    row(L("缩放")) {
                        Picker(L("缩放"), selection: preference(\.pdfZoom)) {
                            Text(L("1 倍")).tag(1.0)
                            Text(L("1.5 倍")).tag(1.5)
                            Text(L("2 倍")).tag(2.0)
                            Text(L("3 倍")).tag(3.0)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.pdfZoom")
                    }
                    row(L("页面旋转")) {
                        Button {
                            store.perform { try await store.command("reader.pdfRotate") }
                        } label: {
                            HStack(spacing: 6) {
                                Image(systemName: "rotate.right").font(.system(size: 13))
                                Text(L("顺时针旋转 90°")).font(NativeTheme.font(13))
                            }
                            .frame(maxWidth: .infinity, minHeight: 34)
                            .background(NativeTheme.sunk, in: Capsule())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("typography.pdfRotate")
                    }
                    row(L("省电模式")) {
                        Picker(L("省电模式"), selection: preference(\.readerPowerSavingMode)) {
                            Text(L("关")).tag(0)
                            Text(L("跟随")).tag(1)
                            Text(L("开")).tag(2)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.powerSaving")
                    }
                } else if store.preferences.mangaMode {
                    row(L("漫画模式")) {
                        Picker(L("漫画模式"), selection: preference(\.mangaMode)) {
                            Text(L("关")).tag(false); Text(L("开")).tag(true)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.mangaMode")
                    }
                    row(L("阅读方向")) {
                        Picker(L("阅读方向"), selection: preference(\.mangaDirection)) {
                            Text(L("右开")).tag(0)
                            Text(L("左开")).tag(1)
                            Text(L("条漫")).tag(2)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.mangaDirection")
                    }
                    row(L("自动裁边")) {
                        Picker(L("自动裁边"), selection: preference(\.mangaAutoCrop)) {
                            Text(L("关")).tag(false)
                            Text(L("开")).tag(true)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.mangaAutoCrop")
                    }
                    row(L("纯黑背景")) {
                        Picker(L("纯黑背景"), selection: preference(\.mangaBlackBackground)) {
                            Text(L("跟随纸张")).tag(false)
                            Text(L("纯黑")).tag(true)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.mangaBlackBg")
                    }
                    row(L("省电模式")) {
                        Picker(L("省电模式"), selection: preference(\.readerPowerSavingMode)) {
                            Text(L("关")).tag(0)
                            Text(L("跟随")).tag(1)
                            Text(L("开")).tag(2)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.powerSaving")
                    }
                } else {
                    row(L("漫画模式")) {
                        Picker(L("漫画模式"), selection: preference(\.mangaMode)) {
                            Text(L("关")).tag(false); Text(L("开")).tag(true)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.mangaMode")
                    }
                    row(L("正文字体")) {
                        Button {
                            if let onOpenFontPicker { onOpenFontPicker() }
                            else { fontPicker = true }
                        } label: {
                            HStack(spacing: 6) {
                                Text(currentFontName)
                                    .font(currentFontPreview)
                                    .foregroundStyle(NativeTheme.ink)
                                    .lineLimit(1)
                                Spacer(minLength: 4)
                                Image(systemName: "chevron.up.chevron.down")
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(NativeTheme.secondary)
                            }
                            .padding(.horizontal, 14)
                            .frame(maxWidth: .infinity, minHeight: 38)
                            .background(NativeTheme.sunk, in: Capsule())
                            .contentShape(Capsule())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("typography.fontFamily")
                    }
                    row(L("字号")) {
                        HStack(spacing: 0) {
                            Button { changeFont(-0.1) } label: { Text("A−").frame(width: 44, height: 44) }
                                .disabled(store.preferences.readerFontScale <= 0.8).accessibilityLabel(L("减小字号"))
                            Text("\(store.preferences.readerFontScale * 100, specifier: "%.0f")%")
                                .monospacedDigit().frame(maxWidth: .infinity).accessibilityIdentifier("typography.fontScale")
                            Button { changeFont(0.1) } label: { Text("A+").frame(width: 44, height: 44) }
                                .disabled(store.preferences.readerFontScale >= 1.8).accessibilityLabel(L("增大字号"))
                        }.buttonStyle(.plain).background(NativeTheme.sunk, in: Capsule())
                    }
                    row(L("行距")) {
                        Picker(L("行距"), selection: preference(\.readerLineHeight)) { Text(L("紧凑")).tag(1.5); Text(L("标准")).tag(1.75); Text(L("宽松")).tag(2.1) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.lineHeight")
                    }
                    row(L("段间距")) {
                        Picker(L("段间距"), selection: preference(\.readerParagraphSpacing)) { Text(L("紧凑")).tag(0.0); Text(L("标准")).tag(0.5); Text(L("宽松")).tag(1.0) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.paragraphSpacing")
                    }
                    row(L("页边距")) {
                        Picker(L("页边距"), selection: preference(\.readerBodyPadding)) { Text(L("窄")).tag(16.0); Text(L("标准")).tag(24.0); Text(L("宽")).tag(36.0) }.pickerStyle(.segmented)
                    }
                    row(L("首行缩进")) {
                        Picker(L("首行缩进"), selection: preference(\.readerParagraphIndent)) {
                            Text(L("关")).tag(false); Text(L("开")).tag(true)
                        }.pickerStyle(.segmented).accessibilityIdentifier("typography.indent")
                    }
                    row(L("阅读模式")) {
                        Picker(L("阅读模式"), selection: preference(\.readerFlowMode)) { Text(L("翻页")).tag(1); Text(L("滚动")).tag(0) }
                            .pickerStyle(.segmented).disabled(store.preferences.readerVerticalWriting)
                            .accessibilityIdentifier("typography.flowMode")
                    }
                    if allowSpreadOptions {
                        row(L("横屏双栏")) {
                            Picker(L("横屏双栏"), selection: preference(\.readerTwoPageInLandscape)) { Text(L("关")).tag(false); Text(L("开")).tag(true) }
                                .pickerStyle(.segmented).disabled(store.preferences.readerVerticalWriting)
                                .accessibilityIdentifier("typography.twoColumn")
                        }
                    }
                    row(L("排版方向")) {
                        Picker(L("排版方向"), selection: preference(\.readerVerticalWriting)) { Text(L("横排")).tag(false); Text(L("竖排")).tag(true) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.direction")
                    }
                    row(L("高级排版")) {
                        Button { advancedTypography = true } label: {
                            HStack(spacing: 6) {
                                Text(advancedSummary)
                                    .font(NativeTheme.font(13))
                                    .foregroundStyle(NativeTheme.secondary)
                                    .lineLimit(1)
                                Spacer(minLength: 4)
                                Image(systemName: "chevron.right")
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(NativeTheme.secondary)
                            }
                            .padding(.horizontal, 14)
                            .frame(maxWidth: .infinity, minHeight: 38)
                            .background(NativeTheme.sunk, in: Capsule())
                            .contentShape(Capsule())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("typography.advanced")
                    }
                }
            }.padding(.horizontal, 18).padding(.top, 2).padding(.bottom, 16)
        }.accessibilityIdentifier("typography.scroll")
            .task { await loadFonts() }
            .onChange(of: store.preferences.readerFontFamily) { _ in Task { await loadFonts() } }
            .sheet(isPresented: $fontPicker, onDismiss: { Task { await loadFonts() } }) {
                NavigationView {
                    NativeReaderFontPicker(store: store, close: { fontPicker = false })
                        .navigationTitle(L("正文字体"))
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) {
                                Button(L("完成")) { fontPicker = false }.accessibilityIdentifier("fontPicker.done")
                            }
                        }
                }
                .navigationViewStyle(.stack)
                .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            }
            .sheet(isPresented: $advancedTypography, onDismiss: { Task { await loadFonts() } }) {
                NativeReaderAdvancedTypography(store: store, fontOptions: fontOptions, close: { advancedTypography = false })
            }
    }
    private func loadFonts() async {
        guard !isPdf else { return }
        guard let list: ReaderFontList = try? await store.request("reader.fonts") else { return }
        fontOptions = list.fonts
    }
    private func row<Content: View>(_ title: String, @ViewBuilder controls: () -> Content) -> some View {
        HStack(spacing: 10) {
            Text(title).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                .frame(width: NativeLocalization.isCjk ? 68 : 96, alignment: .leading)
                .lineLimit(2).minimumScaleFactor(0.82)
            controls().frame(maxWidth: .infinity)
        }.frame(minHeight: 44)
    }
    private func changeFont(_ delta: Double) {
        var value = store.preferences; value.readerFontScale = min(1.8, max(0.8, ((value.readerFontScale + delta) * 100).rounded() / 100))
        store.setPreferences(value)
    }
}

private struct NativeReaderFontPicker: View {
    @ObservedObject var store: NativeStore
    let close: () -> Void
    @State private var options: [ReaderFontOption] = []
    @State private var loading = true
    @State private var error: String?
    @State private var busyKey: String?

    private var builtinOptions: [ReaderFontOption] { options.filter { $0.kind == "builtin" } }
    private var systemOptions: [ReaderFontOption] { options.filter { $0.kind == "system" } }
    private var downloadOptions: [ReaderFontOption] { options.filter { $0.kind == "download" } }

    var body: some View {
        List {
            if loading {
                HStack(spacing: 10) {
                    ProgressView()
                    Text(L("正在读取字体…")).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                }
                .listRowBackground(Color.clear)
                .listRowSeparator(.hidden)
            } else {
                if !builtinOptions.isEmpty { section(L("内置字体"), builtinOptions) }
                if !systemOptions.isEmpty { section(L("系统字体"), systemOptions) }
                if !downloadOptions.isEmpty { section(L("可下载字体"), downloadOptions) }
                Section {
                    Text(L("可下载字体均以 SIL OFL 1.1 授权，可免费商用。字体只保存在本机，许可文本随应用附带。"))
                        .font(NativeTheme.font(11, relativeTo: .caption2))
                        .foregroundStyle(NativeTheme.muted)
                        .listRowBackground(Color.clear)
                }
            }
            if let error {
                Text(error)
                    .font(NativeTheme.font(12))
                    .foregroundStyle(NativeTheme.warning)
                    .listRowBackground(Color.clear)
            }
        }
        .listStyle(.plain)
        .accessibilityIdentifier("fontPicker.list")
        .task(id: store.uiLanguage) { await load() }
    }

    @ViewBuilder private func section(_ title: String, _ items: [ReaderFontOption]) -> some View {
        Section(header: Text(title).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)) {
            ForEach(items) { option in row(option) }
        }
    }

    private func row(_ option: ReaderFontOption) -> some View {
        Button { choose(option) } label: {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(option.name)
                        .font(previewFont(option, size: 17))
                        .lineLimit(1)
                    Text(option.detail)
                        .font(NativeTheme.font(11, relativeTo: .caption2))
                        .foregroundStyle(NativeTheme.muted)
                        .lineLimit(2)
                }
                Spacer(minLength: 8)
                if busyKey == option.key {
                    ProgressView()
                } else if option.installed {
                    if option.key == store.preferences.readerFontFamily {
                        Image(systemName: "checkmark").font(.system(size: 14, weight: .semibold))
                    }
                } else {
                    Image(systemName: "arrow.down.circle")
                        .font(.system(size: 18))
                        .foregroundStyle(NativeTheme.secondary)
                }
            }
            .contentShape(Rectangle())
            .frame(minHeight: 48)
        }
        .buttonStyle(.plain)
        .disabled(busyKey != nil)
        .accessibilityIdentifier("fontPicker.option.\(option.key)")
        .accessibilityAddTraits(option.installed && option.key == store.preferences.readerFontFamily ? .isSelected : [])
    }

    private func previewFont(_ option: ReaderFontOption, size: CGFloat) -> Font {
        guard option.installed, !option.previewName.isEmpty else { return NativeTheme.font(size) }
        return .custom(option.previewName, size: size)
    }

    private func load() async {
        do {
            let list: ReaderFontList = try await store.request("reader.fonts")
            registerFonts(list.fonts)
            options = list.fonts
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
        loading = false
    }

    private func registerFonts(_ items: [ReaderFontOption]) {
        for option in items where option.kind == "download" && option.installed {
            guard let path = option.path, FileManager.default.fileExists(atPath: path) else { continue }
            CTFontManagerRegisterFontsForURL(URL(fileURLWithPath: path) as CFURL, .process, nil)
        }
    }

    private func choose(_ option: ReaderFontOption) {
        if option.installed {
            apply(option.key)
            close()
            return
        }
        guard option.kind == "download" else { return }
        let catalogId = option.key.hasPrefix("catalog:")
            ? String(option.key.dropFirst("catalog:".count))
            : option.key
        busyKey = option.key
        error = nil
        Task {
            do {
                let install: ReaderFontInstall = try await store.request("reader.fontDownload", ["id": catalogId])
                CTFontManagerRegisterFontsForURL(URL(fileURLWithPath: install.path) as CFURL, .process, nil)
                await load()
                apply(install.key)
                store.showToast(L("已下载并应用 {0}", option.name))
                close()
            } catch {
                self.error = error.localizedDescription
            }
            busyKey = nil
        }
    }

    private func apply(_ key: String) {
        var value = store.preferences
        value.readerFontFamily = key
        store.setPreferences(value)
    }
}

private struct NativeTypographyPanel: View {
    @ObservedObject var store: NativeStore
    let isPdf: Bool
    var allowSpreadOptions = UIDevice.current.userInterfaceIdiom == .pad
    var isPopover = false
    var popoverMaxHeight: CGFloat = 800
    let close: () -> Void
    @State private var showingFontPicker = false
    var body: some View {
        let panelBody = VStack(spacing: 0) {
            if showingFontPicker {
                ZStack {
                    Text(L("正文字体")).font(NativeTheme.font(17, relativeTo: .headline))
                    HStack {
                        Button {
                            withAnimation(.easeInOut(duration: 0.2)) { showingFontPicker = false }
                        } label: {
                            HStack(spacing: 4) {
                                Image(systemName: "chevron.left").font(.system(size: 13, weight: .semibold))
                                Text(L("返回"))
                            }
                            .font(NativeTheme.font(14))
                            .frame(minHeight: 44)
                        }
                        .accessibilityIdentifier("fontPicker.done")
                        Spacer()
                        Button(L("完成"), action: close).font(NativeTheme.font(14)).frame(minWidth: 44, minHeight: 44)
                            .accessibilityIdentifier("fontPicker.close")
                    }
                }.buttonStyle(.plain).padding(.horizontal, 24).padding(.top, 8)
                NativeReaderFontPicker(store: store) {
                    withAnimation(.easeInOut(duration: 0.2)) { showingFontPicker = false }
                }
                .transition(.move(edge: .trailing))
            } else {
                HStack {
                    Text(isPdf ? L("显示设置") : L("阅读设置")).font(NativeTheme.font(17, relativeTo: .headline))
                    Spacer()
                    Button(L("完成"), action: close).font(NativeTheme.font(14)).frame(minWidth: 44, minHeight: 44)
                        .accessibilityIdentifier("typography.done")
                }.buttonStyle(.plain).padding(.horizontal, 24).padding(.top, 8)
                NativeTypography(store: store, isPdf: isPdf, allowSpreadOptions: allowSpreadOptions) {
                    withAnimation(.easeInOut(duration: 0.2)) { showingFontPicker = true }
                }
            }
        }
        .background(NativeTheme.page)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("typography.panel")

        if isPopover {
            panelBody
                .frame(
                    width: 390,
                    height: NativeAdaptiveLayout.readerSettingsHeight(
                        maxHeight: popoverMaxHeight, isPdf: isPdf, isTablet: true)
                )
        } else {
            panelBody.modifier(NativeTypographyPresentation(isPdf: isPdf))
        }
    }
}

@available(iOS 16.0, *)
private struct NativeTypographyDetent: CustomPresentationDetent {
    static func height(in context: Context) -> CGFloat? {
        NativeAdaptiveLayout.readerSettingsHeight(maxHeight: context.maxDetentValue, isPdf: false,
            isTablet: UIDevice.current.userInterfaceIdiom == .pad)
    }
}

@available(iOS 16.0, *)
private struct NativePdfTypographyDetent: CustomPresentationDetent {
    static func height(in context: Context) -> CGFloat? {
        NativeAdaptiveLayout.readerSettingsHeight(maxHeight: context.maxDetentValue, isPdf: true,
            isTablet: UIDevice.current.userInterfaceIdiom == .pad)
    }
}

private struct NativeTypographyPresentation: ViewModifier {
    let isPdf: Bool
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) {
            content.presentationDetents([isPdf ? .custom(NativePdfTypographyDetent.self) : .custom(NativeTypographyDetent.self)])
                .presentationDragIndicator(.visible)
        } else {
            content.background(NativeLegacyTypographyPresentation())
        }
    }
}

/// SwiftUI gained detents in iOS 16. iOS 15 still uses UIKit's medium sheet so
/// opening reader settings leaves the text above it available for preview.
private struct NativeLegacyTypographyPresentation: UIViewControllerRepresentable {
    func makeUIViewController(context: Context) -> Controller { Controller() }
    func updateUIViewController(_ controller: Controller, context: Context) {}
    final class Controller: UIViewController {
        override func viewWillAppear(_ animated: Bool) {
            super.viewWillAppear(animated)
            var current: UIViewController? = self
            while let controller = current {
                if let sheet = controller.sheetPresentationController {
                    sheet.detents = [.medium()]
                    sheet.prefersGrabberVisible = true
                    sheet.prefersScrollingExpandsWhenScrolledToEdge = false
                    break
                }
                current = controller.parent
            }
        }
    }
}

struct NativeReaderTocView: View {
    @ObservedObject var store: NativeStore
    let isPdf: Bool
    let paper: Color
    let usesAmbient: Bool
    let ambientOpacity: Double
    let close: () -> Void
    @State private var items: [ReaderToc] = []
    @State private var collapsed: Set<Int> = []
    @State private var jumpPageInput = ""
    /// Large books can carry hundreds of entries; recomputing the outline on
    /// every body evaluation made every checkmark and layout pass O(n).
    @State private var parents: Set<Int> = []
    @State private var rows: [NativeReaderTocOutline.Row] = []

    private func refreshOutline() {
        parents = NativeReaderTocOutline.parentIDs(items)
        rows = NativeReaderTocOutline.visibleRows(items, collapsed: collapsed)
    }

    private func submitPdfJump() {
        let digits = jumpPageInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let target = Int(digits), target >= 1 else { return }
        store.perform { try await store.command("reader.pdfJumpPage", ["page": target]); close() }
    }

    var body: some View {
        VStack(spacing: 0) {
            if isPdf {
                HStack(spacing: 10) {
                    TextField(L("输入页码跳转"), text: $jumpPageInput)
                        .keyboardType(.numberPad)
                        .submitLabel(.go)
                        .onSubmit { submitPdfJump() }
                        .padding(.horizontal, 12)
                        .frame(height: 36)
                        .background(NativeTheme.sunk, in: Capsule())
                        .accessibilityIdentifier("reader.toc.pdfJumpInput")
                    Button(L("跳转"), action: submitPdfJump)
                        .font(NativeTheme.font(14))
                        .padding(.horizontal, 14)
                        .frame(height: 36)
                        .background(NativeTheme.sunk, in: Capsule())
                        .buttonStyle(.plain)
                        .disabled(Int(jumpPageInput.trimmingCharacters(in: .whitespacesAndNewlines)) == nil)
                        .accessibilityIdentifier("reader.toc.pdfJumpGo")
                }
                .padding(.horizontal, 20)
                .padding(.top, 8)
                .padding(.bottom, 6)
            }
            ScrollViewReader { proxy in
                List(rows) { row in
                    let item = row.item
                    HStack(spacing: 0) {
                        if row.hasChildren {
                            Button {
                                withAnimation(.easeInOut(duration: 0.18)) {
                                    if collapsed.contains(item.id) { collapsed.remove(item.id) }
                                    else { collapsed.insert(item.id) }
                                }
                            } label: {
                                Image(systemName: collapsed.contains(item.id) ? "chevron.right" : "chevron.down")
                                    .font(.system(size: 12, weight: .medium)).frame(width: 32, height: 44)
                            }
                            .accessibilityLabel(L("{0}{1}的子章节", collapsed.contains(item.id) ? L("展开") : L("收起"), item.title))
                            .accessibilityIdentifier("reader.toc.toggle.\(item.id)")
                        } else if !parents.isEmpty {
                            Color.clear.frame(width: 32, height: 1)
                        }
                        Button {
                            store.perform { try await store.command("reader.jump", ["id": item.id]); close() }
                        } label: {
                            HStack {
                                Text(item.title).multilineTextAlignment(.leading).padding(.vertical, 8)
                                Spacer(minLength: 12)
                                if item.selected { Image(systemName: "checkmark") }
                            }.frame(minHeight: 44).contentShape(Rectangle())
                        }
                        .accessibilityIdentifier("reader.toc.\(item.id)")
                        .accessibilityAddTraits(item.selected ? .isSelected : [])
                    }
                    .padding(.leading, CGFloat(max(0, item.level) * 12)).buttonStyle(.borderless)
                    .listRowInsets(EdgeInsets(top: 0, leading: 20, bottom: 0, trailing: 20))
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
                }
                .listStyle(.plain)
                .modifier(NativeReaderTocMargins())
                .onChange(of: items.count) { _ in
                    guard let current = items.first(where: \.selected) else { return }
                    DispatchQueue.main.async { proxy.scrollTo(current.id, anchor: .center) }
                }
            }
        }
        .background {
            NativeReaderPaperBackground(paper: paper, usesAmbient: usesAmbient, ambientOpacity: ambientOpacity)
        }
        .modifier(NativeReaderTocNavigationBackground())
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                if !parents.isEmpty {
                    let allCollapsed = parents.isSubset(of: collapsed)
                    Button {
                        withAnimation(.easeInOut(duration: 0.18)) {
                            if allCollapsed { collapsed.removeAll() }
                            else { collapsed = parents }
                        }
                    } label: {
                        Image(systemName: allCollapsed ? "chevron.down" : "chevron.up")
                    }
                    .accessibilityLabel(allCollapsed ? L("展开全部子目录") : L("收起全部子目录"))
                    .accessibilityIdentifier("reader.toc.toggleAll")
                }
            }
        }
        .onChange(of: collapsed) { _ in refreshOutline() }
        .task(id: store.uiLanguage) {
            do {
                items = try await store.request("reader.toc")
                refreshOutline()
            } catch {
                if !Task.isCancelled { store.failure = error.localizedDescription }
            }
        }
    }
}

private struct NativeReaderTocMargins: ViewModifier {
    func body(content: Content) -> some View {
        if #available(iOS 17.0, *) {
            content.scrollContentBackground(.hidden).contentMargins(.top, 0, for: .scrollContent)
        } else if #available(iOS 16.0, *) { content.scrollContentBackground(.hidden) }
        else { content }
    }
}

private struct NativeReaderPaperBackground: View {
    let paper: Color
    let usesAmbient: Bool
    let ambientOpacity: Double
    var body: some View {
        ZStack {
            paper
            if usesAmbient { NativeAmbientWash().opacity(ambientOpacity) }
        }.ignoresSafeArea().allowsHitTesting(false).accessibilityHidden(true)
    }
}

private struct NativeReaderTocNavigationBackground: ViewModifier {
    func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.toolbarBackground(.hidden, for: .navigationBar) }
        else { content }
    }
}

private struct NativeReaderTocSheetBackground: ViewModifier {
    let enabled: Bool
    let paper: Color
    let usesAmbient: Bool
    let ambientOpacity: Double
    func body(content: Content) -> some View {
        if enabled, #available(iOS 16.4, *) {
            content.presentationBackground {
                NativeReaderPaperBackground(paper: paper, usesAmbient: usesAmbient, ambientOpacity: ambientOpacity)
            }
        } else { content }
    }
}

struct NativeReaderSearchResults: View {
    @Environment(\.locale) private var uiLocale
    @Environment(\.colorScheme) private var colorScheme
    let hits: [ReaderSearchHit]
    let query: String
    let busy: Bool
    let loadingMore: Bool
    let hasMore: Bool
    let isPdf: Bool
    let scanned: Int
    let total: Int
    let error: String?
    let jumping: Bool
    let pick: (ReaderSearchHit) -> Void
    let loadMore: () -> Void
    let close: () -> Void
    var body: some View {
        let _ = uiLocale

        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Group {
                    if let error { Text(error) }
                    else if busy && hits.isEmpty {
                        HStack(spacing: 8) {
                            ProgressView()
                            Text(scanned <= 1 ? L("正在搜索当前章节…") : L("正在搜索全书…"))
                        }
                    } else if loadingMore {
                        HStack(spacing: 8) {
                            ProgressView()
                            Text(L("正在继续搜索…"))
                        }
                    } else if busy {
                        HStack(spacing: 8) {
                            ProgressView()
                            Text(L("已找到 {0} 处，继续搜索…", hits.count))
                        }
                    } else if hasMore && total > 0 {
                        Text(isPdf
                             ? L("已找到 {0} 处（已搜索 {1}/{2} 页）", hits.count, scanned, total)
                             : L("已找到 {0} 处（已搜索 {1}/{2} 章）", hits.count, scanned, total))
                    } else {
                        Text(hits.isEmpty ? L("没有找到相关文字") : L("找到 {0} 处结果", hits.count))
                    }
                }
                Spacer(minLength: 8)
                if hasMore && !busy {
                    Button(action: loadMore) {
                        Text(L("继续搜索"))
                            .font(NativeTheme.font(12, relativeTo: .caption).weight(.medium))
                            .foregroundStyle(NativeTheme.ink)
                    }
                    .disabled(jumping)
                    .accessibilityIdentifier("reader.search.continue")
                }
            }.font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                .padding(.horizontal, 20).padding(.top, 14).padding(.bottom, 6).accessibilityIdentifier("reader.search.status")
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(hits) { hit in
                        Button { pick(hit) } label: {
                            VStack(alignment: .leading, spacing: 5) {
                                Text(highlighted(hit.title)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted).lineLimit(1)
                                Text(highlighted(hit.excerpt)).font(NativeTheme.font(14)).lineLimit(3).lineSpacing(5)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }.padding(.vertical, 14).contentShape(Rectangle())
                        }.disabled(jumping).accessibilityIdentifier("reader.search.hit.\(hit.id)")
                        NativeTheme.strong.frame(height: 0.5)
                    }
                    if loadingMore && !hits.isEmpty {
                        HStack(spacing: 8) {
                            ProgressView()
                            Text(L("正在继续搜索…"))
                                .font(NativeTheme.font(13, relativeTo: .subheadline))
                                .foregroundStyle(NativeTheme.muted)
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 16)
                    } else if hasMore && !busy {
                        let remaining = max(0, total - scanned)
                        Button(action: loadMore) {
                            VStack(spacing: 3) {
                                Text(L("继续搜索更多结果"))
                                    .font(NativeTheme.font(14).weight(.medium))
                                    .foregroundStyle(NativeTheme.ink)
                                if remaining > 0 {
                                    Text(isPdf ? L("还有 {0} 页未搜索", remaining) : L("还有 {0} 章未搜索", remaining))
                                        .font(NativeTheme.font(12, relativeTo: .caption))
                                        .foregroundStyle(NativeTheme.muted)
                                }
                            }
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 14)
                            .contentShape(Rectangle())
                        }
                        .disabled(jumping)
                        .accessibilityIdentifier("reader.search.loadMore")
                    }
                }.padding(.horizontal, 20).padding(.bottom, 20)
            }
        }.buttonStyle(.plain).accessibilityElement(children: .contain)
            .accessibilityIdentifier("reader.search.results").accessibilityAction(.escape, close)
    }

    private func highlighted(_ text: String) -> AttributedString {
        guard !query.isEmpty else { return AttributedString(text) }
        var result = AttributedString()
        var start = text.startIndex
        while start < text.endIndex,
              let match = text.range(of: query, options: .caseInsensitive, range: start..<text.endIndex, locale: .current),
              !match.isEmpty {
            result.append(AttributedString(text[start..<match.lowerBound]))
            var emphasis = AttributedString(text[match])
            emphasis.backgroundColor = colorScheme == .dark ? .white : .black
            emphasis.foregroundColor = colorScheme == .dark ? .black : .white
            emphasis.inlinePresentationIntent = .stronglyEmphasized
            result.append(emphasis)
            start = match.upperBound
        }
        result.append(AttributedString(text[start...]))
        return result
    }
}

struct NativeSpeechSettings: View {
    @ObservedObject var store: NativeStore
    let close: () -> Void
    @State private var voices: [SpeechVoice] = []
    @State private var voice = "zh-CN-XiaoxiaoNeural"
    @State private var detectedLanguage = "zh"
    @State private var speed = 1.0
    @State private var autoAdvance = true
    @State private var ready = false
    @State private var busy = false
    var body: some View {
        NativeForm {
            Section {
                Picker(L("语音"), selection: $voice) {
                    Text(detectedLanguage == "en" ? L("系统默认（英文）") : L("系统默认（中文）")).tag("zh-CN-XiaoxiaoNeural")
                    if !zhVoices.isEmpty {
                        Section(L("中文语音")) {
                            ForEach(zhVoices) { item in
                                Text(voiceDisplayLabel(item)).tag(item.id)
                            }
                        }
                    }
                    if !enVoices.isEmpty {
                        Section(L("英文语音")) {
                            ForEach(enVoices) { item in
                                Text(voiceDisplayLabel(item)).tag(item.id)
                            }
                        }
                    }
                }
            } header: {
                Text(L("系统语音"))
            } footer: {
                Text(L("优先推荐增强/高品质语音。可在系统「设置 → 辅助功能 → 朗读内容 → 语音」中下载离线高品质人声。")).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }
            Section(L("语速")) { Text("\(speed, specifier: "%.1f")×"); Slider(value: $speed, in: 0.5...2, step: 0.1).accessibilityLabel(L("朗读语速")) }
            Section {
                Toggle(L("自动翻到下一章或下一页"), isOn: $autoAdvance).tint(NativeTheme.systemSwitchTint)
            } footer: {
                Text(L("保存后从下一句起立即生效，并支持锁屏/后台持续听书。")).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }
            Section {
                Button(L("保存")) {
                    busy = true
                    store.perform { defer { busy = false }; try await store.command("settings.speech.save", ["voice": voice, "speed": speed, "autoAdvance": autoAdvance]); close() }
                }
            }
        }.disabled(!ready || busy).task {
            do {
                let value: SpeechSnapshot = try await store.request("settings.speech.get")
                voices = value.voices
                detectedLanguage = value.detectedLanguage ?? "zh"
                voice = value.voices.contains(where: { $0.id == value.settings.voice }) ? value.settings.voice : "zh-CN-XiaoxiaoNeural"
                speed = value.settings.speed
                autoAdvance = value.settings.autoAdvance
                ready = true
            }
            catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
        }
    }

    private var zhVoices: [SpeechVoice] {
        voices.filter { ($0.culture ?? "").lowercased().hasPrefix("zh") }
    }

    private var enVoices: [SpeechVoice] {
        voices.filter { ($0.culture ?? "").lowercased().hasPrefix("en") }
    }

    private func voiceDisplayLabel(_ item: SpeechVoice) -> String {
        var label = item.name
        if (item.qualityTier ?? 0) >= 2 {
            label += " · \(L("超清"))"
        } else if (item.qualityTier ?? 0) == 1 {
            label += " · \(L("高品质"))"
        }
        if let culture = item.culture, !culture.isEmpty {
            label += " · \(culture)"
        }
        return label
    }
}

private struct NativeFloatingNoteEditor: View {
    @ObservedObject var store: NativeStore
    let request: ReaderNoteRequest
    let close: () -> Void
    @State private var note: String
    @State private var style: NativeHighlightStyle
    @State private var colorHex: String
    @State private var busy = false
    @FocusState private var focused: Bool

    init(store: NativeStore, request: ReaderNoteRequest, close: @escaping () -> Void) {
        self.store = store
        self.request = request
        self.close = close
        _note = State(initialValue: request.initialNote)
        _style = State(initialValue: NativeHighlightStyle.normalize(request.initialStyle))
        _colorHex = State(initialValue: NativeHighlightPalette.normalizeHex(request.initialColor))
    }

    private var isEditing: Bool { request.annotationId != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text(L("批注"))
                    .font(NativeTheme.font(17, relativeTo: .headline))
                Spacer(minLength: 0)
                Button(action: close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 17, weight: .regular))
                        .foregroundStyle(NativeTheme.secondary)
                        .frame(width: 28, height: 28, alignment: .center)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(L("关闭"))
                .accessibilityIdentifier("reader.note.close")
            }
            Text(request.quote)
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
                .lineLimit(2)
            NativeHighlightStyleColorPickerRow(style: $style, colorHex: $colorHex)
            TextEditor(text: $note)
                .font(NativeTheme.font(15, relativeTo: .body))
                .modifier(NativeScrollContentBackgroundHidden())
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .frame(minHeight: 96, maxHeight: 150)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(NativeTheme.divider, lineWidth: 0.5))
                .focused($focused)
                .accessibilityIdentifier("reader.note.text")
            HStack(spacing: 16) {
                if isEditing {
                    Button(L("删除"), role: .destructive, action: delete)
                        .disabled(busy)
                        .accessibilityIdentifier("reader.note.delete")
                }
                Spacer(minLength: 0)
                Button(L("取消"), action: close)
                    .buttonStyle(.plain)
                    .foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("reader.note.cancel")
                Button(action: save) {
                    Text(L("保存"))
                        .font(NativeTheme.font(15, relativeTo: .body))
                        .foregroundStyle(NativeTheme.page)
                        .padding(.horizontal, 18)
                        .frame(height: 36)
                        .background(NativeTheme.ink, in: Capsule())
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .opacity(busy ? 0.5 : 1)
                .disabled(busy)
                .accessibilityIdentifier("reader.note.save")
            }
        }
        .padding(16)
        .modifier(NativeFloatingSurface())
        .onAppear {
            // Focus once the card is in the hierarchy, so the keyboard rises
            // for direct typing without covering the editor.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { focused = true }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reader.note.editor")
    }

    private func save() {
        guard !busy else { return }
        busy = true
        var args: [String: Any] = [
            "note": note,
            "style": style.rawValue,
            "color": style == .marker || colorHex != "#000000" ? colorHex : "#000000"
        ]
        if let annotationId = request.annotationId { args["id"] = annotationId }
        store.perform {
            defer { busy = false }
            try await store.command(
                request.annotationId == nil ? "reader.annotate" : "reader.annotationEdit", args)
            close()
        }
    }

    private func delete() {
        guard let annotationId = request.annotationId, !busy else { return }
        busy = true
        store.perform {
            defer { busy = false }
            try await store.command("reader.annotationDelete", ["id": annotationId])
            close()
        }
    }
}

struct NativeTranslation: View {
    @ObservedObject var store: NativeStore
    let text: String
    @State private var translated: String?
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                Text(text).foregroundStyle(NativeTheme.secondary).textSelection(.enabled)
                Divider()
                if let translated { Text(translated).textSelection(.enabled) } else { ProgressView(L("正在翻译…")) }
            }.frame(maxWidth: .infinity, alignment: .leading).padding(24)
        }.task { do { let value: TextResult = try await store.request("reader.translate", ["text": text]); translated = value.text } catch { if !Task.isCancelled { translated = error.localizedDescription } } }
    }
}

private struct NativeAssistantSheetDetents: ViewModifier {
    let enabled: Bool
    @State private var detent: Any?

    func body(content: Content) -> some View {
        if enabled {
            if #available(iOS 16.0, *) {
                content
                    .modifier(NativeAssistantDetentsIos16())
            } else {
                content
            }
        } else {
            content
        }
    }
}

@available(iOS 16.0, *)
private struct NativeAssistantDetentsIos16: ViewModifier {
    @State private var selection: PresentationDetent = .large

    func body(content: Content) -> some View {
        content
            .presentationDetents([.medium, .large], selection: $selection)
            .presentationDragIndicator(.visible)
    }
}
