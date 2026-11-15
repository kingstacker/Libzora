import SwiftUI

struct NativeLibraryPage: View {
    @ObservedObject var store: NativeStore
    var isActive = true
    @State private var query = ""
    @State private var searching = false
    @FocusState private var searchFocused: Bool
    @State private var loading = false
    @State private var importing = false
    @State private var filter = 0
    @State private var advancedFilter = NativeLibraryFilterSelection()
    @State private var filterOptions: NativeLibraryFilterOptions?
    @State private var showingFilters = false
    @State private var loadingFilterOptions = false
    @State private var filterResult: NativeLibraryFilterResult?
    @State private var creatingFolder = false
    @State private var renamingFolder: LibraryFolder?
    @State private var folderBusy = false
    @StateObject private var scrollState = NativeLibraryScrollState()
    @State private var pageVisible = false
    @State private var visibleCovers: [NativeVisibleCover] = []
    @State private var visibleRegions: [NativeAmbientCoverRegion] = []
    @State private var sampledColors: [NativeVisibleCover: NativeAmbientColor] = [:]

    /// Page labels sit on the colour wash rather than a card; keep them readable there.
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    private var secondaryText: Color { store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary }

    private var filterRequest: NativeLibraryFilterRequest {
        NativeLibraryFilterRequest(selection: advancedFilter, books: store.library)
    }
    private var currentFilterResult: NativeLibraryFilterResult? {
        guard advancedFilter.count > 0, let filterResult, filterResult.request == filterRequest else { return nil }
        return filterResult
    }
    private var filterPending: Bool { advancedFilter.count > 0 && currentFilterResult == nil }
    private var floatingMasthead: Bool { scrollState.showMasthead }
    private var books: [LibraryBook] {
        if filterPending { return [] }
        let matchingIDs = currentFilterResult?.ids
        return NativeBookOrder.sorted(store.library.filter { book in
            (matchingIDs?.contains(book.id) ?? true)
            && (advancedFilter.readingStatus.flatMap(Int.init).map { $0 == NativeBookReadingStatus.value(book) } ?? true)
            && (query.isEmpty || book.title.localizedCaseInsensitiveContains(query) || book.authors.localizedCaseInsensitiveContains(query))
            && (filter == 0 || filter == 1 && NativeBookReadingStatus.value(book) == 1
                || filter == 2 && book.favorite || filter == 3 && NativeBookReadingStatus.value(book) == 2)
        }, sort: store.preferences.sort)
    }
    private func summary(_ books: [LibraryBook]) -> String {
        let reading = books.filter { NativeBookReadingStatus.value($0) == 1 }.count
        return "\(books.count) 本" + (reading > 0 ? " · \(reading) 本在读" : "")
    }

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(spacing: 0) {
                    NativeLibraryScrollReader(state: scrollState)
                    masthead(floating: false)
                        .opacity(floatingMasthead ? 0 : 1)
                        .accessibilityHidden(floatingMasthead)
                    if !store.libraryLoaded, let error = store.libraryLoadError { libraryFailure(error) }
                    else if store.preferences.collectionsView { foldersView(width: geometry.size.width) }
                    else { booksView(width: geometry.size.width) }
                }
                .background {
                    GeometryReader { content in
                        Color.clear
                            .onAppear { scrollState.updateBounds(contentHeight: content.size.height, viewportHeight: geometry.size.height) }
                            .onChange(of: content.size.height) { height in
                                scrollState.updateBounds(contentHeight: height, viewportHeight: geometry.size.height)
                            }
                            .onChange(of: geometry.size.height) { height in
                                scrollState.updateBounds(contentHeight: content.size.height, viewportHeight: height)
                            }
                    }
                }
            }.refreshable { await refresh() }
                .accessibilityIdentifier(store.libraryLoaded ? "library.ready" : "library.loading")
                .coordinateSpace(name: NativeLibraryScrollState.space)
                .onPreferenceChange(NativeCoverPositionKey.self) { positions in
                    guard isActive, pageVisible, store.readerBook == nil, store.preferences.colorfulMode,
                          !store.preferences.collectionsView else { return }
                    let viewport = CGRect(origin: .zero, size: geometry.size)
                    let regions = positions.filter { $0.frame.intersects(viewport) }
                        .sorted { left, right in
                            if abs(left.frame.minY - right.frame.minY) > 1 { return left.frame.minY < right.frame.minY }
                            return left.frame.minX < right.frame.minX
                        }.prefix(18).map { position in
                            let visible = position.frame.intersection(viewport)
                            let fraction = visible.height / max(1, position.frame.height)
                            return NativeAmbientCoverRegion(
                                cover: position.cover,
                                column: min(2, max(0, Int(position.frame.midX / max(1, viewport.width) * 3))),
                                row: min(2, max(0, Int(visible.midY / max(1, viewport.height) * 3))),
                                visibility: max(0.25, (Double(fraction) * 4).rounded() / 4))
                        }
                    // Quantise regions, not background positions, to avoid frame-by-frame drift.
                    let covers = regions.map(\.cover)
                    if covers != visibleCovers { visibleCovers = covers }
                    if regions != visibleRegions {
                        visibleRegions = regions
                        publishVisibleColors()
                    }
                }
                .overlay(alignment: .top) {
                    if floatingMasthead {
                        masthead(floating: true)
                            .zIndex(1)
                            .transition(.move(edge: .top).combined(with: .opacity))
                    }
                }
        }.background(NativeAmbientBackground()).navigationBarHidden(true)
        .overlay(alignment: .top) {
            if !floatingMasthead { NativeLibraryStatusBarGlass(scroll: scrollState) }
        }
        .sheet(isPresented: $creatingFolder) {
            NativeNameSheet(store: store, title: "新建收藏夹", initial: "", button: "创建") { name in
                let _: CreatedFolder = try await store.request("collection.create", ["name": name]); try await store.refreshLibrary()
            }
        }
        .sheet(item: $renamingFolder) { folder in
            NativeNameSheet(store: store, title: "重命名收藏夹", initial: folder.name, button: "保存") { name in
                try await store.command("collection.rename", ["collectionId": folder.id, "name": name]); try await store.refreshLibrary()
            }
        }
        .sheet(isPresented: $showingFilters) {
            if let filterOptions {
                NativeLibraryFilterSheet(options: filterOptions, selection: advancedFilter, quickFilter: filter,
                                         books: store.library, query: query) { selection in
                    if selection.readingStatus != nil && (filter == 1 || filter == 3) { filter = 0 }
                    advancedFilter = selection
                }
            }
        }
        .onAppear { pageVisible = true; publishVisibleColors() }
        .onDisappear { pageVisible = false }
        .onChange(of: isActive) { active in if active { publishVisibleColors() } }
        .task(id: filterRequest) { await updateFilterResults(for: filterRequest) }
        .task(id: visibleCovers) {
            guard store.preferences.colorfulMode else { return }
            // Coalesce boundary changes without doing image decoding on each frame.
            do { try await Task.sleep(nanoseconds: 120_000_000) }
            catch { return }
            let missing = visibleCovers.filter { sampledColors[$0] == nil }
            let samples = await Task.detached(priority: .utility) {
                missing.compactMap { cover -> (NativeVisibleCover, NativeAmbientColor)? in
                    guard let color = NativeAmbientPalette.extract(paths: [cover.path]).first else { return nil }
                    return (cover, color)
                }
            }.value
            guard !Task.isCancelled else { return }
            sampledColors = sampledColors.filter { visibleCovers.contains($0.key) }
            for (cover, color) in samples { sampledColors[cover] = color }
            publishVisibleColors()
        }
    }
    private func publishVisibleColors() {
        guard isActive, pageVisible, store.readerBook == nil, store.preferences.colorfulMode,
              !store.preferences.collectionsView else { return }
        let samples = visibleRegions.compactMap { region -> NativeAmbientSample? in
            guard let color = sampledColors[region.cover] else { return nil }
            return NativeAmbientSample(color: color, column: region.column, row: region.row,
                                       visibility: region.visibility)
        }
        let colors = NativeAmbientPalette.regionalColors(samples)
        guard !colors.isEmpty, store.visibleAmbientColors != colors else { return }
        withAnimation(.easeInOut(duration: 0.8)) { store.visibleAmbientColors = colors }
    }
    private func masthead(floating: Bool) -> some View {
        HStack(spacing: 0) {
            if searching {
                NativeSearchField(text: $query, prompt: store.preferences.collectionsView ? "搜索收藏夹" : "搜索书名或作者",
                                  focused: $searchFocused, identifier: "library.search") { searchFocused = false }
            } else {
                Text(store.preferences.collectionsView ? "收藏夹" : "书库")
                    .font(NativeTheme.pageTitleFont)
                    .frame(minHeight: 44)
                    .accessibilityIdentifier("library.view.title")
                Spacer(minLength: 0)
            }
            Button {
                searching.toggle(); searchFocused = searching
                if !searching { query = "" }
            } label: {
                Image(systemName: searching ? "xmark" : "magnifyingglass").font(.system(size: 20, weight: .regular))
                    .frame(width: 44, height: 44)
            }.buttonStyle(.plain).accessibilityLabel(searching ? "关闭搜索" : "搜索书库")
                .accessibilityIdentifier("library.search.toggle")
            Button {
                if store.preferences.collectionsView { creatingFolder = true }
                else {
                    importing = true
                    store.perform { defer { importing = false }; try await store.command("library.import"); try await store.refreshLibrary() }
                }
            } label: {
                Group { if importing { ProgressView() } else { Image(systemName: "plus").font(.system(size: 21, weight: .regular)) } }
                    .frame(width: 44, height: 44)
            }.buttonStyle(.plain).disabled(importing || folderBusy)
                .accessibilityLabel(store.preferences.collectionsView ? "新建收藏夹" : "导入书籍")
                .accessibilityIdentifier("library.import")
        }.padding(.leading, 24).padding(.trailing, 12).padding(.top, 16).padding(.bottom, 4)
            .background {
                if floating {
                    NativeMastheadGlass(scroll: scrollState, colors: nil)
                        .padding(.bottom, -64)
                        .ignoresSafeArea(edges: .top)
                }
            }
    }
    private var filters: some View {
        HStack(spacing: 0) {
            Picker("筛选", selection: Binding(get: { filter }, set: { value in
                filter = value
                if value == 1 || value == 3 { advancedFilter.readingStatus = nil }
            })) {
                Text("全部").tag(0); Text("在读").tag(1); Text("收藏").tag(2); Text("读完").tag(3)
            }.pickerStyle(.segmented).accessibilityIdentifier("library.filter")
            Button { openFilters() } label: {
                ZStack(alignment: .topTrailing) {
                    Group {
                        if loadingFilterOptions { ProgressView() }
                        else { Image(systemName: "slider.horizontal.3")
                            .font(.system(size: 18, weight: .regular)) }
                    }.frame(width: 38, height: 44)
                    if advancedFilter.count > 0 {
                        Circle().fill(NativeTheme.ink).frame(width: 6, height: 6).offset(x: -3, y: 5)
                    }
                }
            }.buttonStyle(.plain).foregroundStyle(mutedText).disabled(loadingFilterOptions)
                .accessibilityLabel(advancedFilter.count == 0 ? "筛选书籍" : "筛选书籍，已选 \(advancedFilter.count) 项")
                .accessibilityIdentifier("library.filters.open")
            NativeLibraryDisplayMenu(store: store)
        }
    }
    private func booksView(width pageWidth: CGFloat) -> some View {
        // Filtering and sorting the whole library is the costly part of a render.
        let books = self.books
        return VStack(alignment: .leading, spacing: 0) {
            filters.padding(.horizontal, -12).padding(.bottom, 12)
            continueCard(books: books)
            if filterPending {
                ProgressView("正在筛选书籍…").frame(maxWidth: .infinity).padding(.top, 96)
            } else if let error = currentFilterResult?.error {
                VStack(spacing: 10) {
                    Text("筛选失败").font(NativeTheme.font(17, relativeTo: .headline))
                    Text(error).font(NativeTheme.font(13)).foregroundStyle(secondaryText)
                    Button("重试") { filterResult = nil; Task { await updateFilterResults(for: filterRequest) } }
                }.frame(maxWidth: .infinity).padding(.top, 96)
            } else {
                Text(summary(books)).font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(mutedText).padding(.bottom, 14)
                if books.isEmpty { emptyView }
                else {
                    NativeBookShelf(store: store, books: books, pageWidth: pageWidth, tracksVisibleCovers: true) { book in bookActions(book) }
                }
            }
        }.padding(.horizontal, 24).padding(.top, 8).padding(.bottom, 28)
    }
    private func libraryFailure(_ message: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.circle").font(.system(size: 32, weight: .ultraLight))
                .foregroundStyle(mutedText)
            Text("书库加载失败").font(NativeTheme.font(18, relativeTo: .headline))
            Text(message).font(NativeTheme.font(13)).foregroundStyle(secondaryText)
                .multilineTextAlignment(.center)
            Button { Task { await refresh() } } label: {
                HStack(spacing: 8) {
                    if loading { ProgressView() }
                    Text(loading ? "正在重试…" : "重新加载")
                }
            }.buttonStyle(NativePrimaryButtonStyle()).disabled(loading)
                .accessibilityIdentifier("library.retry")
                .frame(maxWidth: 240).padding(.top, 8)
        }.padding(.horizontal, 32).frame(maxWidth: .infinity).padding(.top, 96)
            .accessibilityElement(children: .contain).accessibilityIdentifier("library.loadError")
    }
    private var emptyView: some View {
        VStack(spacing: 6) {
            if loading || store.preparingLibrary { ProgressView("正在加载书库…") }
            else {
                Image(systemName: "book").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(mutedText)
                Text(store.library.isEmpty ? "书库还是空的" : "没有找到相关书籍")
                    .font(NativeTheme.font(17, relativeTo: .headline)).padding(.top, 8)
                Text(store.library.isEmpty ? "点右上角 + 导入 EPUB、PDF 或 TXT" : "换个关键词或筛选条件试试")
                    .font(NativeTheme.font(13, relativeTo: .subheadline)).foregroundStyle(mutedText)
                    .multilineTextAlignment(.center)
            }
        }.frame(maxWidth: .infinity).padding(.top, 96).padding(.bottom, 40)
    }
    @ViewBuilder private func continueCard(books: [LibraryBook]) -> some View {
        if query.isEmpty, filter <= 1, let id = store.continueBookId, let book = books.first(where: { $0.id == id }) {
            Button { store.perform { try await store.openReader(book) } } label: {
                HStack(spacing: 14) {
                    BookCover(book: book, width: 48, height: 68)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("继续阅读").font(NativeTheme.font(13)).foregroundStyle(mutedText)
                        Text(book.title).font(NativeTheme.font(16)).lineLimit(1)
                        Spacer(minLength: 4)
                        HStack(spacing: 10) {
                            NativeThinProgress(value: book.progress)
                            Text("\(book.progress, specifier: "%.0f")%")
                                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
                        }
                    }
                }.frame(height: 68).contentShape(Rectangle())
            }.buttonStyle(.plain).disabled(store.openingReader).accessibilityIdentifier("library.continue")
                .padding(.top, 4).padding(.bottom, 20)
        }
    }
    private func foldersView(width: CGFloat) -> some View {
        let cardWidth = max(1, (width - 48 - 14) / 2)
        let folders = store.folders.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) }
        return VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("\(folders.count) 个收藏夹").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
                Spacer()
                NativeLibraryDisplayMenu(store: store)
            }
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)], spacing: 14) {
                ForEach(folders) { folder in
                    NavigationLink { NativeCollectionPage(store: store, folderId: folder.id) } label: {
                        NativeFolderCard(folder: folder, books: store.library.filter { $0.collectionIds.contains(folder.id) }, width: cardWidth)
                    }.buttonStyle(.plain).accessibilityIdentifier("collection.\(folder.id)")
                        .contextMenu {
                            if !folder.automatic {
                                Button("重命名") { renamingFolder = folder }
                                Button("清空书籍") { folderAction("clear", folder: folder) }
                                Menu("合并到") {
                                    ForEach(store.folders.filter { !$0.automatic && $0.id != folder.id }) { target in
                                        Button(target.name) { folderAction("merge", folder: folder, target: target.id) }
                                    }
                                }
                                Button("解散收藏夹", role: .destructive) { folderAction("dissolve", folder: folder) }
                            }
                        }
                }
                Button { creatingFolder = true } label: {
                    VStack(spacing: 10) {
                        Image(systemName: "plus").font(.system(size: 20, weight: .regular))
                            .frame(width: 44, height: 44).background(NativeTheme.sunk, in: Circle())
                        Text("新建收藏夹").font(NativeTheme.font(13)).foregroundStyle(mutedText)
                    }.frame(maxWidth: .infinity).frame(height: cardWidth * 1.48)
                    .overlay(RoundedRectangle(cornerRadius: 18).stroke(NativeTheme.strong, style: StrokeStyle(lineWidth: 1.4, dash: [3, 3])))
                }.buttonStyle(.plain).accessibilityIdentifier("collection.create")
            }
            Text("长按收藏夹可重命名、清空、合并或解散。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
        }.padding(.horizontal, 24).padding(.top, 14).padding(.bottom, 28).disabled(folderBusy)
    }
    private func bookActions(_ book: LibraryBook) -> [NativeBookMenuItem] {
        [
            NativeBookMenuItem("阅读") { store.perform { try await store.openReader(book) } },
            NativeBookMenuItem(book.favorite ? "取消收藏" : "收藏") {
                store.perform { try await store.command("book.favorite", ["bookId": book.id, "favorite": !book.favorite]); try await store.refreshLibrary() }
            }
        ]
    }
    private func folderAction(_ operation: String, folder: LibraryFolder, target: String? = nil) {
        folderBusy = true
        store.perform {
            defer { folderBusy = false }
            var args = ["collectionId": folder.id]; if let target { args["targetId"] = target }
            try await store.command("collection.\(operation)", args); try await store.refreshLibrary()
        }
    }
    private func openFilters() {
        guard !loadingFilterOptions else { return }
        loadingFilterOptions = true
        store.perform {
            defer { loadingFilterOptions = false }
            let options: NativeLibraryFilterOptions = try await store.request("library.filterOptions")
            filterOptions = options; showingFilters = true
        }
    }
    private func updateFilterResults(for request: NativeLibraryFilterRequest) async {
        guard request.selection.count > 0 else { filterResult = nil; return }
        let selection = request.selection
        var args: [String: Any] = [:]
        if let value = selection.author { args["author"] = value }
        if let value = selection.format { args["format"] = value }
        if let value = selection.tag { args["tag"] = value }
        if let value = selection.category { args["category"] = value }
        // The mobile shelf also reflects actual reading progress, even when a
        // book's manually assigned desktop status has not been updated.
        if args.isEmpty {
            filterResult = NativeLibraryFilterResult(request: request, ids: Set(request.books.map(\.id)), error: nil)
            return
        }
        do {
            let ids: [String] = try await store.request("library.filter", args)
            guard !Task.isCancelled && request == filterRequest else { return }
            filterResult = NativeLibraryFilterResult(request: request, ids: Set(ids), error: nil)
        } catch {
            guard !Task.isCancelled && request == filterRequest else { return }
            filterResult = NativeLibraryFilterResult(request: request, ids: [], error: error.localizedDescription)
        }
    }
    private func refresh() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do { try await store.refreshLibrary(timeoutNanoseconds: 30_000_000_000) }
        catch {
            if !Task.isCancelled {
                store.releaseLaunchRestore()
                // A failed initial load has an inline, retryable result. Keep
                // the existing alert behaviour when refreshing a loaded shelf.
                if store.libraryLoaded { store.failure = error.localizedDescription }
            }
        }
    }
}

private struct NativeVisibleCover: Hashable {
    let path: String
    let version: String
}

private struct NativeAmbientCoverRegion: Equatable {
    let cover: NativeVisibleCover
    let column: Int
    let row: Int
    let visibility: Double
}

private struct NativeCoverPosition: Equatable {
    let cover: NativeVisibleCover
    let frame: CGRect
}

private struct NativeCoverPositionKey: PreferenceKey {
    static var defaultValue: [NativeCoverPosition] { [] }
    static func reduce(value: inout [NativeCoverPosition], nextValue: () -> [NativeCoverPosition]) {
        value.append(contentsOf: nextValue())
    }
}

/// Tracks the scroll direction to reveal the shelf actions on a downward drag.
final class NativeLibraryScrollState: ObservableObject {
    static let space = "library.scroll"
    @Published private(set) var offset: CGFloat = 0
    @Published private(set) var showMasthead = false
    private var anchor: CGFloat?
    private var directionAnchor: CGFloat = 0
    private var maximumOffset: CGFloat = 0
    private var lastValue: CGFloat?
    private var lastSample = ProcessInfo.processInfo.systemUptime
    private var scrollSpeed: CGFloat = 0

    /// Fade the material as the inline header meets the floating header.
    var glassOpacity: Double { min(1, max(0, offset / 48)) }

    /// Finish the masthead transition in the time it takes the shelf to cover
    /// its height, so a flick snaps in and a slow drag eases with the finger.
    private var mastheadDuration: TimeInterval {
        min(0.26, max(0.05, 64 / Double(max(abs(scrollSpeed), 160))))
    }

    func updateBounds(contentHeight: CGFloat, viewportHeight: CGFloat) {
        guard contentHeight.isFinite, viewportHeight.isFinite, viewportHeight > 0 else { return }
        maximumOffset = max(0, contentHeight - viewportHeight)
        directionAnchor = min(directionAnchor, maximumOffset)
        if offset > maximumOffset {
            offset = maximumOffset
            showMasthead = false
        }
    }

    func update(top: CGFloat) {
        guard top.isFinite else { return }
        if anchor == nil { anchor = top }
        guard let anchor else { return }
        // Rubber-band motion beyond either edge is not a scroll reversal.
        // In particular, returning from bottom overscroll must not reveal the header.
        let value = min(maximumOffset, max(0, anchor - top))
        let now = ProcessInfo.processInfo.systemUptime
        if let lastValue, now > lastSample {
            let elapsed = now - lastSample
            // A pause ends the gesture; the next move should not inherit it.
            let instantaneous = (value - lastValue) / CGFloat(elapsed)
            scrollSpeed = elapsed < 0.1 ? instantaneous : 0
        }
        lastValue = value
        lastSample = now
        if abs(value - offset) > 0.5 {
            if value <= 0.5 {
                // Both headers have identical geometry here, so handing back to
                // the inline header does not make its controls jump or vanish.
                showMasthead = false
                directionAnchor = value
            } else if value > directionAnchor + 12 {
                if showMasthead { withAnimation(.easeOut(duration: mastheadDuration)) { showMasthead = false } }
                directionAnchor = value
            } else if value < directionAnchor - 12 {
                if value >= 64 && !showMasthead { withAnimation(.easeOut(duration: mastheadDuration)) { showMasthead = true } }
                directionAnchor = value
            }
            offset = value
        }
    }
}

private struct NativeLibraryScrollReader: View {
    @ObservedObject var state: NativeLibraryScrollState
    var body: some View {
        GeometryReader { proxy in
            Color.clear
                .onAppear { state.update(top: proxy.frame(in: .named(NativeLibraryScrollState.space)).minY) }
                .onChange(of: proxy.frame(in: .named(NativeLibraryScrollState.space)).minY) { top in
                    state.update(top: top)
                }
        }.frame(height: 0)
    }
}

private struct NativeMastheadGlass: View {
    @ObservedObject var scroll: NativeLibraryScrollState
    let colors: [NativeAmbientColor]?
    @EnvironmentObject private var store: NativeStore
    var body: some View {
        ZStack {
            Rectangle().fill(.regularMaterial)
            NativeTheme.page.opacity(0.15)
            if store.preferences.colorfulMode {
                NativeAmbientWash(colorsOverride: colors).opacity(0.22)
                    .animation(.easeInOut(duration: 0.6), value: colors)
            }
        }
            // Let the blur dissolve into the shelf instead of ending on a
            // hard line, like the system's soft scroll-edge treatment.
            .mask {
                LinearGradient(stops: [
                    .init(color: .black, location: 0),
                    .init(color: .black, location: 0.46),
                    .init(color: .black.opacity(0.88), location: 0.60),
                    .init(color: .black.opacity(0.5), location: 0.76),
                    .init(color: .black.opacity(0.14), location: 0.91),
                    .init(color: .clear, location: 1)
                ], startPoint: .top, endPoint: .bottom)
            }
            .opacity(scroll.glassOpacity)
    }
}

private struct NativeLibraryStatusBarGlass: View {
    @ObservedObject var scroll: NativeLibraryScrollState
    var body: some View {
        NativeStatusBarGlass().opacity(scroll.glassOpacity)
    }
}

enum NativeBookReadingStatus {
    static func value(_ book: LibraryBook) -> Int {
        if book.readingStatus == 2 || book.progress >= 99.5 { return 2 }
        if book.readingStatus == 1 || book.progress > 0 { return 1 }
        return 0
    }
}

private struct NativeLibraryFilterRequest: Equatable {
    let selection: NativeLibraryFilterSelection
    let books: [LibraryBook]
}

private struct NativeLibraryFilterResult {
    let request: NativeLibraryFilterRequest
    let ids: Set<String>
    let error: String?
}

enum NativeBookOrder {
    static func sorted(_ books: [LibraryBook], sort: Int) -> [LibraryBook] {
        books.sorted { left, right in
            switch sort {
            case 1: return left.title.localizedStandardCompare(right.title) == .orderedAscending
            case 2: return left.authors.localizedStandardCompare(right.authors) == .orderedAscending
            case 3: return left.createdAt > right.createdAt
            default: return left.updatedAt > right.updatedAt
            }
        }
    }
}

/// Menu entries are data so the long-press overlay can render them with the
/// same glass look as the rest of the native frontend.
struct NativeBookMenuItem {
    let title: String
    let destructive: Bool
    let action: () -> Void

    init(_ title: String, destructive: Bool = false, action: @escaping () -> Void) {
        self.title = title; self.destructive = destructive; self.action = action
    }
}

/// Both the library and an opened collection use the same cover geometry,
/// text, spacing, navigation and persisted grid/list preference.
struct NativeBookShelf: View {
    @ObservedObject var store: NativeStore
    @ObservedObject private var previewCenter = NativeBookPreviewCenter.shared
    @State private var detailBookID: String?
    @State private var showingDetail = false
    let books: [LibraryBook]
    let pageWidth: CGFloat
    let tracksVisibleCovers: Bool
    private let actions: (LibraryBook) -> [NativeBookMenuItem]

    init(store: NativeStore, books: [LibraryBook], pageWidth: CGFloat, tracksVisibleCovers: Bool = false,
         actions: @escaping (LibraryBook) -> [NativeBookMenuItem]) {
        self.store = store; self.books = books; self.pageWidth = pageWidth
        self.tracksVisibleCovers = tracksVisibleCovers; self.actions = actions
    }
    static func columnCount(pageWidth: CGFloat) -> Int { max(3, Int((pageWidth - 32) / 136)) }
    static func coverWidth(pageWidth: CGFloat) -> CGFloat {
        let columns = columnCount(pageWidth: pageWidth)
        return max(1, (pageWidth - 48 - CGFloat(columns - 1) * 16) / CGFloat(columns))
    }
    private var columns: Int { Self.columnCount(pageWidth: pageWidth) }
    private var coverWidth: CGFloat { Self.coverWidth(pageWidth: pageWidth) }
    private var galleryMode: Bool { store.preferences.galleryMode && !store.preferences.listView }
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }

    var body: some View {
        Group {
            if store.preferences.listView {
                LazyVStack(spacing: 0) { ForEach(books) { book in bookLink(book) } }
            } else {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 16, alignment: .top), count: columns),
                          alignment: .leading, spacing: galleryMode ? 18 : 26) {
                    ForEach(books) { book in bookLink(book) }
                }
            }
        }.accessibilityElement(children: .contain)
            .accessibilityIdentifier(store.preferences.listView ? "books.list" : "books.grid")
            .background {
                NavigationLink(isActive: $showingDetail) {
                    if let detailBookID { NativeBookDetailPage(store: store, bookId: detailBookID) }
                } label: { EmptyView() }
                    .hidden().accessibilityHidden(true)
            }
    }
    private func bookLink(_ book: LibraryBook) -> some View {
        // The app-owned preview draws its own copy, so the source tile hides
        // while it is up, exactly like the system lift used to.
        let lifted = previewCenter.request?.id == book.id
        return Button { store.perform { try await store.openReader(book) } } label: {
            Group {
                if store.preferences.listView { BookRow(book: book, showRating: store.preferences.showBookRating, showSyncStatus: store.preferences.showBookSyncStatus, muted: mutedText) }
                else { NativeBookTile(book: book, width: coverWidth, muted: mutedText, galleryMode: galleryMode,
                                      showRating: store.preferences.showBookRating,
                                      showSyncStatus: store.preferences.showBookSyncStatus) }
            }.opacity(lifted ? 0 : 1)
        }.buttonStyle(.plain).disabled(store.openingReader)
            .overlay {
                NativeBookLink(enabled: !store.openingReader,
                               tap: { store.perform { try await store.openReader(book) } },
                               longPress: { frame in showPreview(book, sourceFrame: frame) })
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .accessibilityHint("轻点阅读，长按查看更多操作")
            .accessibilityIdentifier("book.\(book.id)")
            .background {
                if tracksVisibleCovers, store.preferences.colorfulMode, let path = book.coverPath {
                    GeometryReader { proxy in
                        let tile = proxy.frame(in: .named(NativeLibraryScrollState.space))
                        let cover = CGRect(x: tile.minX, y: tile.minY + (store.preferences.listView ? 10 : 0),
                                           width: store.preferences.listView ? 46 : coverWidth,
                                           height: store.preferences.listView ? 66 : coverWidth * 1.45)
                        Color.clear.preference(key: NativeCoverPositionKey.self, value: [
                            NativeCoverPosition(cover: NativeVisibleCover(path: path, version: book.updatedAt), frame: cover)
                        ])
                    }
                }
            }
    }

    /// The preview request carries everything the overlay needs to draw the
    /// same card, plus the source's window frame so it scales about its own
    /// centre instead of following UIKit's menu placement.
    private func showPreview(_ book: LibraryBook, sourceFrame: CGRect) {
        NativeBookPreviewCenter.shared.show(NativeBookPreviewRequest(
            book: book,
            sourceFrame: sourceFrame,
            coverWidth: coverWidth,
            muted: mutedText,
            showRating: store.preferences.showBookRating,
            showSyncStatus: store.preferences.showBookSyncStatus,
            actions: actions(book) + [NativeBookMenuItem("书籍详情") { detailBookID = book.id; showingDetail = true }]))
    }
}

/// A transparent touch layer over the card. A quick tap opens the book; a
/// long press hands the card's window frame to the app-owned preview overlay,
/// which draws no system plate, mask or rounded card edge.
private struct NativeBookLink: UIViewRepresentable {
    let enabled: Bool
    let tap: () -> Void
    let longPress: (CGRect) -> Void

    func makeUIView(context: Context) -> NativeBookLinkView { NativeBookLinkView() }

    func updateUIView(_ view: NativeBookLinkView, context: Context) {
        view.isEnabled = enabled
        view.onTap = tap
        view.onLongPress = longPress
    }
}

private final class NativeBookLinkView: UIButton {
    var onTap: (() -> Void)?
    var onLongPress: ((CGRect) -> Void)?
    private var suppressTap = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        // The SwiftUI button underneath stays the accessibility element; this
        // transparent view only forwards taps and detects the long press.
        isAccessibilityElement = false
        backgroundColor = .clear
        addTarget(self, action: #selector(handleTap), for: .touchUpInside)
        let press = UILongPressGestureRecognizer(target: self, action: #selector(handleLongPress(_:)))
        press.minimumPressDuration = 0.45
        press.allowableMovement = 14
        press.cancelsTouchesInView = true
        addGestureRecognizer(press)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    @objc private func handleTap() {
        guard !suppressTap else { suppressTap = false; return }
        onTap?()
    }

    @objc private func handleLongPress(_ gesture: UILongPressGestureRecognizer) {
        switch gesture.state {
        case .began:
            guard isEnabled, let window else { return }
            suppressTap = true
            // The overlay positions the card using window coordinates, so it
            // lands exactly where the pressed card is, whatever the scroll.
            onLongPress?(convert(bounds, to: window))
        case .ended, .cancelled, .failed:
            DispatchQueue.main.async { [weak self] in self?.suppressTap = false }
        default:
            break
        }
    }
}

struct NativeLibraryDisplayMenu: View {
    @ObservedObject var store: NativeStore
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    var body: some View {
        Menu {
            Section("视图") {
                Picker("视图", selection: Binding(get: {
                    store.preferences.collectionsView ? 2 : store.preferences.listView ? 1 : 0
                }, set: { view in
                    var value = store.preferences
                    value.collectionsView = view == 2
                    if view != 2 { value.listView = view == 1 }
                    store.setPreferences(value)
                })) {
                    Label("网格视图", systemImage: "square.grid.2x2").tag(0)
                    Label("列表视图", systemImage: "list.bullet").tag(1)
                    Label("收藏夹视图", systemImage: "folder").tag(2)
                }
            }
            if !store.preferences.collectionsView {
                Section("排序") {
                    Picker("排序", selection: Binding(get: { store.preferences.sort }, set: { sort in
                        var value = store.preferences; value.sort = sort
                        store.setPreferences(value)
                    })) {
                        Text("最近更新").tag(0); Text("书名").tag(1); Text("作者").tag(2); Text("添加时间").tag(3)
                    }
                }
            }
        } label: {
            Image(systemName: "ellipsis").font(.system(size: 18, weight: .medium)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(mutedText)
            .accessibilityLabel("显示选项").accessibilityIdentifier("library.display")
    }
}

struct NativeBookSortMenu: View {
    @ObservedObject var store: NativeStore
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    var body: some View {
        Menu {
            Picker("排序", selection: Binding(get: { store.preferences.sort }, set: { sort in
                var value = store.preferences; value.sort = sort
                store.setPreferences(value)
            })) {
                Text("最近更新").tag(0); Text("书名").tag(1); Text("作者").tag(2); Text("添加时间").tag(3)
            }
        } label: {
            Image(systemName: "line.3.horizontal.decrease").font(.system(size: 18, weight: .regular)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(mutedText).accessibilityLabel("书籍排序")
    }
}

struct NativeBookLayoutButton: View {
    @ObservedObject var store: NativeStore
    let identifier: String
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    var body: some View {
        Button {
            var value = store.preferences; value.listView.toggle()
            store.setPreferences(value)
        } label: {
            Image(systemName: store.preferences.listView ? "square.grid.2x2" : "list.bullet")
                .font(.system(size: 18, weight: .regular)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(mutedText)
            .accessibilityLabel(store.preferences.listView ? "网格视图" : "列表视图")
            .accessibilityIdentifier(identifier)
    }
}

struct NativeBookTile: View {
    let book: LibraryBook
    let width: CGFloat
    var muted = NativeTheme.muted
    var galleryMode = false
    var showRating = false
    var showSyncStatus = false
    var previewing = false
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            BookCover(book: book, width: width)
                .overlay(alignment: .topLeading) {
                    if showRating, let rating = book.onlineRating {
                        NativeBookRatingBadge(rating: rating).padding(5).accessibilityHidden(true)
                    }
                }
                .overlay(alignment: .bottomTrailing) {
                    if showSyncStatus, let status = book.syncStatus, NativeBookSyncBadge.isPending(status) {
                        NativeBookSyncBadge(status: status).padding(5).accessibilityHidden(true)
                    }
                }
            if !galleryMode {
                previewMetadata(Text(book.title).font(NativeTheme.font(13.5)).lineLimit(2).lineSpacing(3).padding(.top, 8),
                                "book.preview.title")
                previewMetadata(Text(book.authors).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(muted)
                    .lineLimit(1).padding(.top, 2), "book.preview.author")
            }
        }.frame(maxWidth: .infinity, alignment: .topLeading).contentShape(Rectangle())
            .accessibilityElement(children: previewing ? .contain : .ignore)
            .accessibilityLabel(book.title
                + (showRating ? book.onlineRating.map { "，评分 \(NativeBookRatingBadge.text($0))" } ?? "" : "")
                + (showSyncStatus && NativeBookSyncBadge.isPending(book.syncStatus) ? NativeBookSyncBadge.description(book.syncStatus).map { "，\($0)" } ?? "" : ""))
    }

    /// Only the menu preview exposes separate title/author elements; the
    /// shelf tile stays one compact accessibility element.
    @ViewBuilder private func previewMetadata<Content: View>(_ content: Content, _ identifier: String) -> some View {
        if previewing { content.accessibilityIdentifier(identifier) } else { content }
    }
}

struct NativeFolderCard: View {
    let folder: LibraryFolder
    let books: [LibraryBook]
    let width: CGFloat
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ZStack {
                let covers = Array(books.sorted { $0.updatedAt > $1.updatedAt }.prefix(3))
                if covers.isEmpty {
                    RoundedRectangle(cornerRadius: 6).fill(NativeTheme.strong)
                        .frame(width: width * 0.46, height: width * 0.67)
                        .overlay(Image(systemName: "book").font(.system(size: 26, weight: .ultraLight)).foregroundStyle(NativeTheme.muted))
                } else {
                    ForEach(Array(covers.enumerated()), id: \.element.id) { index, book in
                        BookCover(book: book, width: width * 0.46)
                            .rotationEffect(.degrees(covers.count == 1 ? 0 : Double(index - 1) * 12))
                            .offset(x: covers.count == 1 ? 0 : CGFloat(index - 1) * width * 0.12,
                                    y: index == 1 ? -4 : 4)
                            .zIndex(index == 1 ? 3 : Double(index))
                    }
                }
            }.frame(maxWidth: .infinity).frame(height: width * 1.06)
            VStack(alignment: .leading, spacing: 3) {
                Text(folder.name).font(NativeTheme.font(15)).lineLimit(1)
                Text("\(folder.count) 本").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            }.padding(.horizontal, 16).padding(.bottom, 15)
        }.frame(maxWidth: .infinity).frame(height: width * 1.48, alignment: .top)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
}

private struct NativeBookRatingBadge: View {
    let rating: Double

    static func text(_ rating: Double) -> String { String(format: "%.1f", rating) }

    var body: some View {
        HStack(spacing: 2) {
            Image(systemName: "star.fill").font(.system(size: 8.5, weight: .semibold))
            Text(Self.text(rating)).font(NativeTheme.font(10, relativeTo: .caption2)).monospacedDigit()
        }.foregroundStyle(NativeTheme.ink)
            .padding(.horizontal, 5).padding(.vertical, 3)
            .background(.regularMaterial, in: Capsule())
            .accessibilityLabel("评分 \(Self.text(rating))")
    }
}

private struct NativeBookSyncBadge: View {
    let status: Int

    /// Settled books stay quiet: only unsynced or cloud-only books need a badge.
    static func isPending(_ status: Int?) -> Bool { status == 0 || status == 2 }

    static func description(_ status: Int?) -> String? {
        switch status {
        case 0: return "未同步"
        case 1, 3: return "已同步"
        case 2: return "已同步到云端，尚未下载"
        default: return nil
        }
    }

    var body: some View {
        let symbol = status == 0 ? "icloud.and.arrow.up" : status == 2 ? "icloud.and.arrow.down" : "checkmark.icloud"
        Image(systemName: symbol).font(.system(size: 11, weight: .medium))
            .foregroundStyle(NativeTheme.ink).padding(5)
            .background(.regularMaterial, in: Circle())
            .accessibilityLabel(Self.description(status) ?? "同步状态未知")
    }
}

struct BookRow: View {
    let book: LibraryBook
    var showRating = false
    var showSyncStatus = false
    var muted = NativeTheme.muted
    var body: some View {
        HStack(spacing: 16) {
            BookCover(book: book, width: 46, height: 66)
            VStack(alignment: .leading, spacing: 4) {
                Text(book.title).font(NativeTheme.font(15)).lineLimit(1)
                HStack(spacing: 10) {
                    Text(book.authors).font(NativeTheme.font(12.5, relativeTo: .caption)).lineLimit(1)
                    Spacer(minLength: 0)
                    if showRating, let rating = book.onlineRating { NativeBookRatingBadge(rating: rating) }
                    if showSyncStatus, let status = book.syncStatus, NativeBookSyncBadge.isPending(status) { NativeBookSyncBadge(status: status) }
                    Text("\(book.progress, specifier: "%.0f")%").font(NativeTheme.font(12, relativeTo: .caption))
                }.foregroundStyle(muted)
            }
        }.padding(.vertical, 10).contentShape(Rectangle())
    }
}
