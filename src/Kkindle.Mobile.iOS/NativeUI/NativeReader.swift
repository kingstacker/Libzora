import SwiftUI
import UIKit
import CoreText

private enum ReaderPanel: String, Identifiable {
    case toc, typography, speech, dictionary, assistant, note, annotation, translation, info
    var id: String { rawValue }
    var title: String {
        switch self {
        case .toc: return "目录"; case .typography: return "阅读设置"
        case .speech: return "朗读设置"; case .dictionary: return "本地词典"; case .assistant: return "AI 助手"
        case .note: return "笔记"; case .annotation: return "批注"; case .translation: return "翻译"; case .info: return "注释"
        }
    }
}

struct NativeReaderPage: View {
    @ObservedObject var store: NativeStore
    @ObservedObject var session: NativeReaderSession
    let book: LibraryBook
    let appColorScheme: ColorScheme
    let appearanceSessionId: String
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.colorScheme) private var readerColorEnvironment
    @State private var appearanceRevision = 0
    @State private var panel: ReaderPanel?
    @State private var dialog: ReaderDialog?
    @State private var selectedText = ""
    @State private var seekProgress = 0.0
    @State private var dragging = false
    @State private var closing = false
    @State private var searching = false
    @FocusState private var searchFocused: Bool
    @State private var searchQuery = ""
    @State private var searchSubmitted = ""
    @State private var searchHits: [ReaderSearchHit] = []
    @State private var searchBusy = false
    @State private var searchError: String?
    @State private var searchGeneration = 0
    @State private var showingSearchResults = false
    @State private var jumpingToSearch = false
    @State private var speechExpanded = true
    @State private var footnoteCardHeight: CGFloat = 140
    private let chromeSideInset: CGFloat = 8
    private let footerHeight: CGFloat = 24
    private var reader: ReaderSnapshot { session.state }
    private var speechActive: Bool { reader.speechState != 0 && reader.speechState != 4 }
    /// The footer is drawn over the bottom of the reader surface. The page
    /// already keeps its own bottom margin, so only the part of the footer
    /// strip that the margin does not cover has to be reserved. Reserving the
    /// whole footer height again left a blank band under the last line and
    /// pushed lines that would otherwise fit onto the next page.
    private var readerBottomInset: CGFloat {
        guard !reader.isPdf else { return footerHeight }
        let bodyInset = min(64, max(16, store.preferences.readerBodyPadding))
        return max(0, footerHeight - CGFloat(bodyInset))
    }
    private var currentAppColorScheme: ColorScheme {
        store.preferences.readerThemeFollowsAppearance ? readerColorEnvironment : appColorScheme
    }
    private var effectiveReaderTheme: Int {
        store.preferences.readerThemeFollowsAppearance ? (currentAppColorScheme == .dark ? 5 : 0) : store.preferences.readerTheme
    }
    private var readerColorScheme: ColorScheme? {
        store.preferences.readerThemeFollowsAppearance ? store.preferences.colorScheme : (store.preferences.readerTheme == 5 ? .dark : .light)
    }
    private var paper: Color { store.preferences.paper(for: effectiveReaderTheme) }
    // Assistant conversations take the full screen; the other reader tools
    // remain page sheets so the reading context stays visible.
    private var sheetPanel: Binding<ReaderPanel?> {
        Binding(get: { panel == .assistant ? nil : panel }, set: { panel = $0 })
    }
    private var assistantPanel: Binding<ReaderPanel?> {
        Binding(get: { panel == .assistant ? panel : nil }, set: { panel = $0 })
    }

    var body: some View {
        ZStack(alignment: .top) {
            readerContent.allowsHitTesting(!showingSearchResults).accessibilityHidden(showingSearchResults)
            if showingSearchResults {
                Button(action: dismissSearchResults) { Color.black.opacity(0.08).ignoresSafeArea() }
                    .buttonStyle(.plain).accessibilityLabel("收起搜索结果")
                    .accessibilityIdentifier("reader.search.dismiss")
            }
            VStack(spacing: 8) {
                if reader.chromeVisible || !reader.ready { topBar }
                if reader.footnoteReturnVisible {
                    Button(action: returnFromFootnote) {
                        HStack(spacing: 6) {
                            Image(systemName: "arrow.uturn.backward")
                                .font(.system(size: 12, weight: .semibold))
                            Text("返回原文").font(NativeTheme.font(13))
                        }
                        .padding(.horizontal, 14)
                        .frame(height: 34)
                    }
                    .buttonStyle(.plain)
                    .modifier(NativeFloatingSurface())
                    .accessibilityIdentifier("reader.footnote.return")
                    .transition(.opacity)
                }
                if showingSearchResults {
                    NativeReaderSearchResults(hits: searchHits, query: searchSubmitted, busy: searchBusy, error: searchError,
                                              jumping: jumpingToSearch, pick: jumpToSearch, close: dismissSearchResults)
                        .frame(height: searchHits.isEmpty ? 58 : min(300, CGFloat(searchHits.count) * 96 + 38))
                        .modifier(NativeFloatingSurface()).padding(.horizontal, 12)
                }
                Spacer(minLength: 0)
            }

        }
        .ignoresSafeArea(.keyboard)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .environment(\.symbolVariants, .none)
        .preferredColorScheme(readerColorScheme)
        .interactiveDismissDisabled(true)
        .sheet(item: sheetPanel) { panel in
            Group {
                if panel == .typography {
                    NativeTypographyPanel(store: store, isPdf: reader.isPdf) { self.panel = nil }
                } else {
                    NavigationView {
                        panelContent(panel)
                            .navigationTitle(panel.title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("完成") { self.panel = nil } } }
                    }.navigationViewStyle(.stack)
                        .modifier(NativeReaderTocSheetBackground(
                            enabled: panel == .toc, paper: paper,
                            usesAmbient: store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode,
                            ambientOpacity: readerColorEnvironment == .dark ? 0.14 : 0.10))
                }
            }
            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            .preferredColorScheme(readerColorScheme)
        }
        .fullScreenCover(item: assistantPanel) { panel in
            // The assistant is a conversation, so it owns the whole screen
            // instead of leaving a strip of the reader's toolbar visible.
            NavigationView {
                panelContent(panel)
                    .navigationTitle(panel.title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                    .toolbar { ToolbarItem(placement: .cancellationAction) { Button("完成") { self.panel = nil } } }
            }
            .navigationViewStyle(.stack)
            .background(NativeTheme.page.ignoresSafeArea())
            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            .preferredColorScheme(readerColorScheme)
        }
        .onAppear { seekProgress = reader.progress; syncAppAppearance() }
        .onChange(of: appColorScheme) { _ in syncAppAppearance() }
        .onChange(of: readerColorEnvironment) { _ in
            if store.preferences.readerThemeFollowsAppearance { syncAppAppearance() }
        }
        .onChange(of: store.preferences.readerThemeFollowsAppearance) { _ in syncAppAppearance() }
        .onChange(of: scenePhase) { if $0 == .active { syncAppAppearance() } }
        .onChange(of: reader.progress) { if !dragging { seekProgress = $0 } }
        .onChange(of: reader.chromeVisible) { if !$0 && searching { closeSearch() } }
        .onChange(of: session.dialog) { value in
            guard let value else { return }
            if searching { closeSearch() }
            dialog = value; panel = value.kind == "annotation" ? .annotation : .info
            session.dialog = nil
        }
        .task(id: searchGeneration) {
            let generation = searchGeneration
            guard !searchSubmitted.isEmpty else { return }
            do {
                let hits: [ReaderSearchHit] = try await store.request("reader.search", ["query": searchSubmitted])
                guard !Task.isCancelled, generation == searchGeneration else { return }
                searchHits = hits
            } catch {
                guard !Task.isCancelled, generation == searchGeneration else { return }
                searchError = error.localizedDescription
            }
            if !Task.isCancelled, generation == searchGeneration { searchBusy = false }
        }
    }
    private func syncAppAppearance() {
        appearanceRevision += 1
        let args: [String: Any] = ["sessionId": appearanceSessionId, "revision": appearanceRevision,
                                   "dark": currentAppColorScheme == .dark]
        Task { try? await store.command("reader.appearance", args) }
    }
    private var readerContent: some View {
        ZStack {
            paper.ignoresSafeArea()
            ReaderSurface(controller: store.readerController).padding(.bottom, readerBottomInset)
            if !reader.ready {
                VStack(spacing: 16) {
                    Text(book.title).font(NativeTheme.font(21, relativeTo: .title2)).multilineTextAlignment(.center)
                    if let error = reader.error { Text(error).foregroundStyle(NativeTheme.secondary).multilineTextAlignment(.center) }
                    else { ProgressView("正在打开书籍…") }
                }.padding(32).frame(maxWidth: .infinity, maxHeight: .infinity).background(paper)
            }
            VStack(spacing: 0) {
                Spacer(minLength: 0)
                // Toasts sit above whichever reader toolbars are showing instead of covering them.
                if let toast = store.toast { NativeToast(text: toast).padding(.bottom, 12) }
                if speechActive {
                    NativeReaderSpeechControls(state: reader.speechState, status: reader.speechStatus,
                        suspended: panel != nil || showingSearchResults, expanded: $speechExpanded,
                        settings: { panel = .speech }, pause: { run("speechPause") }, stop: { run("speechStop") })
                }
                if reader.chromeVisible && reader.ready { bottomBar }
                else {
                    HStack {
                        Text(reader.chapter).lineLimit(1)
                        Spacer(minLength: 12)
                        Text(reader.position).monospacedDigit().lineLimit(1).minimumScaleFactor(0.85)
                            .layoutPriority(1).accessibilityIdentifier("reader.position")
                    }.font(NativeTheme.font(11.5, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted).padding(.horizontal, 24).frame(height: footerHeight)
                        .background(paper).allowsHitTesting(false)
                }
            }
        }
        .overlay {
            if store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode {
                // One continuous wash covers both the rendered page and the
                // progress/footer paper, so following appearance never leaves a seam.
                NativeAmbientWash().opacity(readerColorEnvironment == .dark ? 0.14 : 0.10)
                    .ignoresSafeArea().allowsHitTesting(false)
            }
        }
        .overlay {
            GeometryReader { geometry in
                if let text = reader.selectedText, panel == nil, !showingSearchResults {
                    selectionBar(text)
                        .frame(width: NativeSelectionToolbarLayout.width(in: geometry.size),
                               height: NativeSelectionToolbarLayout.height)
                        .position(NativeSelectionToolbarLayout.position(bounds: reader.selectionBounds,
                            in: geometry.size, chromeVisible: reader.chromeVisible,
                            speechVisible: speechActive && speechExpanded))
                }
            }
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
                        .accessibilityLabel("关闭注释")
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
        store.perform { try await store.command("reader.footnoteReturn") }
    }
    private func closeSearch() {
        searchFocused = false
        dismissSearchResults()
        withAnimation(.easeOut(duration: 0.2)) { searching = false }
    }
    private func dismissSearchResults() {
        showingSearchResults = false; searchSubmitted = ""; searchBusy = false; searchGeneration += 1
    }
    private func submitSearch() {
        guard !jumpingToSearch else { return }
        let query = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return }
        searchFocused = false; searchSubmitted = query
        searchHits = []; searchError = nil; searchBusy = true; showingSearchResults = true; searchGeneration += 1
    }
    private func jumpToSearch(_ hit: ReaderSearchHit) {
        guard !searchBusy, !jumpingToSearch else { return }
        jumpingToSearch = true
        store.perform {
            defer { jumpingToSearch = false }
            try await store.command("reader.searchJump", ["id": hit.id]); closeSearch()
        }
    }
    private var topBar: some View {
        HStack(spacing: 0) {
            Button {
                if searching { closeSearch(); return }
                closing = true
                Task { await store.closeReader(); closing = false }
            } label: { Group { if closing { ProgressView() } else { Image(systemName: "chevron.left") } }.frame(width: 44, height: 44) }
                .disabled(closing).accessibilityLabel("退出阅读").accessibilityIdentifier("reader.close")
            ZStack(alignment: .trailing) {
                if searching {
                    NativeSearchField(text: $searchQuery, prompt: "搜索全书", focused: $searchFocused,
                                      identifier: "reader.search.query", submit: submitSearch)
                        .transition(.move(edge: .trailing).combined(with: .opacity))
                } else {
                    Text(book.title).font(NativeTheme.font(14)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.frame(maxWidth: .infinity, minHeight: 44).padding(.horizontal, 2).clipped()
            if searching {
                Button(action: closeSearch) { Image(systemName: "xmark").font(.system(size: 15)).frame(width: 44, height: 44) }
                    .accessibilityLabel("关闭搜索").accessibilityIdentifier("reader.search.close")
            } else {
                Button {
                    withAnimation(.easeOut(duration: 0.2)) { searching = true }
                    searchFocused = true
                } label: {
                    Image(systemName: "magnifyingglass").frame(width: 44, height: 44)
                }.disabled(!reader.ready).accessibilityLabel("搜索全书").accessibilityIdentifier("reader.search")
            }
            Button { run("bookmark") } label: { Image(systemName: reader.bookmark ? "bookmark.fill" : "bookmark").frame(width: 44, height: 44) }
                .disabled(!reader.ready).accessibilityLabel("书签").accessibilityIdentifier("reader.bookmark")
        }.font(.system(size: 19, weight: .regular)).buttonStyle(.plain).padding(4)
            .modifier(NativeFloatingSurface())
            .accessibilityElement(children: .contain).accessibilityIdentifier("reader.topToolbar")
            .padding(.horizontal, chromeSideInset).padding(.top, 8)
    }
    private var bottomBar: some View {
        VStack(spacing: 0) {
            HStack { Text(reader.chapter).lineLimit(1); Spacer(); Text("\(seekProgress, specifier: "%.0f")%") }
                .font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
            HStack(spacing: 0) {
                Button { run("chapter", ["direction": -1]) } label: { Image(systemName: "backward.end").frame(width: 36, height: 44) }.accessibilityLabel("上一章或上一页")
                Slider(value: $seekProgress, in: 0...100, onEditingChanged: { editing in
                    dragging = editing
                    if !editing { run("seek", ["progress": seekProgress]) }
                }).accessibilityLabel("阅读进度").accessibilityIdentifier("reader.progress")
                Button { run("chapter", ["direction": 1]) } label: { Image(systemName: "forward.end").frame(width: 36, height: 44) }.accessibilityLabel("下一章或下一页")
            }.font(.system(size: 17, weight: .regular)).buttonStyle(.plain).padding(.horizontal, -10).padding(.top, 2)
            HStack(spacing: 0) {
                tool("目录", "list.bullet") { panel = .toc }
                tool(reader.speechState == 0 || reader.speechState == 4 ? "听书" : "停止", "headphones") { run("speech") }
                tool("AI", "sparkles") { selectedText = ""; panel = .assistant }
                tool(reader.isPdf ? "显示" : "排版", "Aa") { panel = .typography }
            }
        }.padding(.horizontal, 18).padding(.top, 14).padding(.bottom, 6)
            .modifier(NativeFloatingSurface())
            .accessibilityElement(children: .contain).accessibilityIdentifier("reader.toolbar")
            .padding(.horizontal, chromeSideInset)
    }
    private func selectionBar(_ text: String) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 0) {
                selectionAction("复制", "doc.on.doc") {
                    UIPasteboard.general.string = text; run("clearSelection"); store.showToast("已复制")
                }
                selectionAction("划线", "underline") { run("annotate") }
                selectionAction("笔记", "square.and.pencil") { selectedText = text; dialog = nil; panel = .note }
                selectionAction("搜索", "magnifyingglass") {
                    store.perform {
                        try await store.command("reader.clearSelection")
                        try await store.command("reader.showChrome")
                        searching = true; searchQuery = text; submitSearch()
                    }
                }
                selectionAction("词典", "book.closed") { selectedText = text; panel = .dictionary }
                selectionAction("AI", "sparkles") { selectedText = text; panel = .assistant }
                selectionAction("翻译", "character.bubble") { selectedText = text; panel = .translation }
                selectionAction("朗读", "speaker.wave.2") { run("selectionSpeech") }
            }.padding(4)
        }
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18))
        .overlay(RoundedRectangle(cornerRadius: 18).stroke(NativeTheme.divider.opacity(0.6), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.12), radius: 12, y: 5)
        .accessibilityElement(children: .contain).accessibilityIdentifier("reader.selection.toolbar")
    }
    private func selectionAction(_ title: String, _ symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 19, weight: .regular))
                .frame(width: 44, height: 44).contentShape(Rectangle())
        }.buttonStyle(.plain).accessibilityLabel(title).accessibilityIdentifier("reader.\(title)")
    }
    private func tool(_ title: String, _ image: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 2) {
                if image == "Aa" { Text("Aa").font(NativeTheme.font(16)).frame(height: 21) }
                else { Image(systemName: image).font(.system(size: 21, weight: .regular)).frame(height: 21) }
                Text(title).font(NativeTheme.font(11, relativeTo: .caption2))
            }.frame(maxWidth: .infinity).frame(minHeight: 52).contentShape(Rectangle())
        }
            .buttonStyle(.plain).accessibilityIdentifier("reader.\(title)")
    }
    @ViewBuilder private func panelContent(_ panel: ReaderPanel) -> some View {
        switch panel {
        case .toc: NativeReaderTocView(store: store, paper: paper,
                                      usesAmbient: store.preferences.readerThemeFollowsAppearance && store.preferences.colorfulMode,
                                      ambientOpacity: readerColorEnvironment == .dark ? 0.14 : 0.10,
                                      close: { self.panel = nil })
        case .typography: NativeTypography(store: store, isPdf: reader.isPdf)
        case .speech: NativeSpeechSettings(store: store, close: { self.panel = nil })
        case .dictionary: NativeDictionary(store: store, initialTerm: selectedText)
        case .assistant: NativeAssistant(store: store, session: session, book: book, selection: selectedText)
        case .note, .annotation: NativeNoteEditor(store: store, quote: dialog?.text ?? selectedText, annotationId: dialog?.annotationId, initial: dialog?.note ?? "", close: { self.panel = nil })
        case .translation: NativeTranslation(store: store, text: selectedText)
        case .info: ScrollView { Text(dialog?.text ?? "").textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(24) }
        }
    }
    private func run(_ command: String, _ args: [String: Any] = [:]) { store.perform { try await store.command("reader.\(command)", args) } }
}

struct NativeTypography: View {
    @ObservedObject var store: NativeStore
    let isPdf: Bool
    @State private var fontPicker = false
    @State private var fontOptions: [ReaderFontOption] = []
    private var currentFontName: String {
        let key = store.preferences.readerFontFamily
        if let option = fontOptions.first(where: { $0.key == key }) { return option.name }
        if key.hasPrefix("system:") { return String(key.dropFirst("system:".count)) }
        if key.hasPrefix("catalog:") { return "已下载字体" }
        return "京华老宋体"
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
                row("纸张") {
                    Picker("纸张", selection: paperSelection) {
                        Text("跟随").tag(-1); Text("白").tag(0); Text("米").tag(1); Text("黑").tag(5)
                    }.pickerStyle(.segmented).accessibilityIdentifier("typography.paper")
                }
                row("翻页动画") {
                    Picker("翻页动画", selection: preference(\.readerPageAnimation)) { Text("无").tag(0); Text("淡入").tag(1); Text("滑动").tag(2); Text("墨水").tag(3) }.pickerStyle(.segmented)
                }
                VStack(alignment: .leading, spacing: 8) {
                    Text("底部进度").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                    Picker("底部进度", selection: preference(\.readerProgressDisplay)) {
                        Text("页码").tag(0); Text("章节").tag(1)
                        Text("全书剩余").tag(3)
                    }.pickerStyle(.segmented).accessibilityIdentifier("typography.progressDisplay")
                    if store.preferences.readerProgressDisplay >= 2 {
                        Text("全书剩余按你的阅读速度估算")
                            .font(NativeTheme.font(11, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.muted)
                    }
                }.padding(.vertical, 10)
                if isPdf {
                    row("缩放") {
                        Picker("缩放", selection: preference(\.pdfZoom)) { Text("整页").tag(1.0); Text("1.5 倍").tag(1.5); Text("2 倍").tag(2.0) }.pickerStyle(.segmented)
                    }
                } else {
                    row("正文字体") {
                        Button { fontPicker = true } label: {
                            HStack(spacing: 8) {
                                Text(currentFontName).lineLimit(1)
                                Spacer(minLength: 8)
                                Image(systemName: "chevron.right")
                                    .font(.system(size: 12, weight: .regular))
                                    .foregroundStyle(NativeTheme.muted)
                            }
                            .frame(minHeight: 44)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("typography.fontFamily")
                    }
                    row("字号") {
                        HStack(spacing: 0) {
                            Button { changeFont(-0.1) } label: { Text("A−").frame(width: 44, height: 44) }
                                .disabled(store.preferences.readerFontScale <= 0.8).accessibilityLabel("减小字号")
                            Text("\(store.preferences.readerFontScale * 100, specifier: "%.0f")%")
                                .monospacedDigit().frame(maxWidth: .infinity).accessibilityIdentifier("typography.fontScale")
                            Button { changeFont(0.1) } label: { Text("A+").frame(width: 44, height: 44) }
                                .disabled(store.preferences.readerFontScale >= 1.8).accessibilityLabel("增大字号")
                        }.buttonStyle(.plain).background(NativeTheme.sunk, in: Capsule())
                    }
                    row("行距") {
                        Picker("行距", selection: preference(\.readerLineHeight)) { Text("紧凑").tag(1.5); Text("标准").tag(1.75); Text("宽松").tag(2.1) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.lineHeight")
                    }
                    row("段间距") {
                        Picker("段间距", selection: preference(\.readerParagraphSpacing)) { Text("紧凑").tag(0.0); Text("标准").tag(0.5); Text("宽松").tag(1.0) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.paragraphSpacing")
                    }
                    row("页边距") {
                        Picker("页边距", selection: preference(\.readerBodyPadding)) { Text("窄").tag(16.0); Text("标准").tag(24.0); Text("宽").tag(36.0) }.pickerStyle(.segmented)
                    }
                    NativeToggle(title: "首行缩进", isOn: preference(\.readerParagraphIndent), identifier: "typography.indent")
                    row("排版方向") {
                        Picker("排版方向", selection: preference(\.readerVerticalWriting)) { Text("横排").tag(false); Text("竖排").tag(true) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.direction")
                    }
                }
            }.padding(.horizontal, 24).padding(.top, 2).padding(.bottom, 16)
        }.accessibilityIdentifier("typography.scroll")
            .task { await loadFonts() }
            .sheet(isPresented: $fontPicker, onDismiss: { Task { await loadFonts() } }) {
                NativeReaderFontPicker(store: store, close: { fontPicker = false })
            }
    }
    private func loadFonts() async {
        guard !isPdf else { return }
        guard let list: ReaderFontList = try? await store.request("reader.fonts") else { return }
        fontOptions = list.fonts
    }
    private func row<Content: View>(_ title: String, @ViewBuilder controls: () -> Content) -> some View {
        HStack(spacing: 12) {
            Text(title).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary).frame(width: 58, alignment: .leading)
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
        NavigationView {
            List {
                if loading {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text("正在读取字体…").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                    }
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
                } else {
                    if !builtinOptions.isEmpty { section("内置字体", builtinOptions) }
                    if !systemOptions.isEmpty { section("系统字体", systemOptions) }
                    if !downloadOptions.isEmpty { section("可下载字体", downloadOptions) }
                    Section {
                        Text("可下载字体均以 SIL OFL 1.1 授权，可免费商用。字体只保存在本机，许可文本随应用附带。")
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
            .navigationTitle("正文字体")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("完成", action: close).accessibilityIdentifier("fontPicker.done")
                }
            }
        }
        .navigationViewStyle(.stack)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .task { await load() }
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
                store.showToast("已下载并应用 \(option.name)")
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
    let close: () -> Void
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(isPdf ? "显示设置" : "阅读设置").font(NativeTheme.font(17, relativeTo: .headline))
                Spacer()
                Button("完成", action: close).font(NativeTheme.font(14)).frame(minWidth: 44, minHeight: 44)
                    .accessibilityIdentifier("typography.done")
            }.buttonStyle(.plain).padding(.horizontal, 24).padding(.top, 8)
            NativeTypography(store: store, isPdf: isPdf)
        }.background(NativeTheme.page).accessibilityElement(children: .contain)
            .accessibilityIdentifier("typography.panel").modifier(NativeTypographyPresentation(isPdf: isPdf))
    }
}

@available(iOS 16.0, *)
private struct NativeTypographyDetent: CustomPresentationDetent {
    static func height(in context: Context) -> CGFloat? { min(340, context.maxDetentValue * 0.42) }
}

@available(iOS 16.0, *)
private struct NativePdfTypographyDetent: CustomPresentationDetent {
    static func height(in context: Context) -> CGFloat? { min(220, context.maxDetentValue * 0.42) }
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
    let paper: Color
    let usesAmbient: Bool
    let ambientOpacity: Double
    let close: () -> Void
    @State private var items: [ReaderToc] = []
    @State private var collapsed: Set<Int> = []

    private var parentIDs: Set<Int> { NativeReaderTocOutline.parentIDs(items) }
    private var visibleRows: [NativeReaderTocOutline.Row] {
        NativeReaderTocOutline.visibleRows(items, collapsed: collapsed)
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                List(visibleRows) { row in
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
                            .accessibilityLabel("\(collapsed.contains(item.id) ? "展开" : "收起")\(item.title)的子章节")
                            .accessibilityIdentifier("reader.toc.toggle.\(item.id)")
                        } else if !parentIDs.isEmpty {
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
                if !parentIDs.isEmpty {
                    let allCollapsed = parentIDs.isSubset(of: collapsed)
                    Button {
                        withAnimation(.easeInOut(duration: 0.18)) {
                            if allCollapsed { collapsed.removeAll() }
                            else { collapsed = parentIDs }
                        }
                    } label: {
                        Image(systemName: allCollapsed ? "chevron.down" : "chevron.up")
                    }
                    .accessibilityLabel(allCollapsed ? "展开全部子目录" : "收起全部子目录")
                    .accessibilityIdentifier("reader.toc.toggleAll")
                }
            }
        }
        .task { do { items = try await store.request("reader.toc") } catch { if !Task.isCancelled { store.failure = error.localizedDescription } } }
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
    @Environment(\.colorScheme) private var colorScheme
    let hits: [ReaderSearchHit]
    let query: String
    let busy: Bool
    let error: String?
    let jumping: Bool
    let pick: (ReaderSearchHit) -> Void
    let close: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Group {
                if busy { ProgressView("正在搜索全书…") }
                else if let error { Text(error) }
                else { Text(hits.isEmpty ? "没有找到相关文字" : "找到 \(hits.count) 处结果") }
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
                        }.disabled(busy || jumping).accessibilityIdentifier("reader.search.hit.\(hit.id)")
                        NativeTheme.strong.frame(height: 0.5)
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
    @State private var speed = 1.0
    @State private var autoAdvance = true
    @State private var ready = false
    @State private var busy = false
    var body: some View {
        NativeForm {
            Section("系统语音") {
                Picker("语音", selection: $voice) {
                    Text("系统默认（中文）").tag("zh-CN-XiaoxiaoNeural")
                    ForEach(voices) { item in Text(item.name + (item.culture.map { " · " + $0 } ?? "")).tag(item.id) }
                }
                Text("语音由设备提供，可以在系统设置中下载更多语音。").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }
            Section("语速") { Text("\(speed, specifier: "%.1f")×"); Slider(value: $speed, in: 0.5...2, step: 0.1).accessibilityLabel("朗读语速") }
            Section { Toggle("自动翻到下一章或下一页", isOn: $autoAdvance).tint(NativeTheme.systemSwitchTint); Text("设置在下次开始朗读时生效。").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary) }
            Button("保存") {
                busy = true
                store.perform { defer { busy = false }; try await store.command("settings.speech.save", ["voice": voice, "speed": speed, "autoAdvance": autoAdvance]); close() }
            }
        }.disabled(!ready || busy).task {
            do { let value: SpeechSnapshot = try await store.request("settings.speech.get"); voices = value.voices; voice = value.settings.voice; speed = value.settings.speed; autoAdvance = value.settings.autoAdvance; ready = true }
            catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
        }
    }
}

struct NativeNoteEditor: View {
    @ObservedObject var store: NativeStore
    let quote: String
    let annotationId: String?
    let close: () -> Void
    @State private var note: String
    @State private var busy = false
    init(store: NativeStore, quote: String, annotationId: String?, initial: String, close: @escaping () -> Void) {
        self.store = store; self.quote = quote; self.annotationId = annotationId; self.close = close; _note = State(initialValue: initial)
    }
    var body: some View {
        NativeForm {
            Section("原文") { Text(quote).foregroundStyle(NativeTheme.secondary).textSelection(.enabled) }
            Section("笔记") { TextEditor(text: $note).frame(minHeight: 150) }
            Button("保存") {
                busy = true
                store.perform { defer { busy = false }; var args: [String: Any] = ["note": note]; if let annotationId { args["id"] = annotationId }
                    try await store.command(annotationId == nil ? "reader.annotate" : "reader.annotationEdit", args); close() }
            }
            if let annotationId {
                Button("删除批注", role: .destructive) { busy = true; store.perform { defer { busy = false }; try await store.command("reader.annotationDelete", ["id": annotationId]); close() } }
            }
        }.disabled(busy).modifier(NativeKeyboardDone())
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
                if let translated { Text(translated).textSelection(.enabled) } else { ProgressView("正在翻译…") }
            }.frame(maxWidth: .infinity, alignment: .leading).padding(24)
        }.task { do { let value: TextResult = try await store.request("reader.translate", ["text": text]); translated = value.text } catch { if !Task.isCancelled { translated = error.localizedDescription } } }
    }
}
