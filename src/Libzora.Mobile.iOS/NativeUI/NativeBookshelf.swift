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
    @StateObject private var selection = NativeLibrarySelectionState()
    @State private var batchDeleting = false
    @StateObject private var bookDestinations = NativeShelfBookDestinations()
    @StateObject private var scrollState = NativeLibraryScrollState()
    @State private var mastheadHeight: CGFloat = NativeAdaptiveLayout.mastheadTopInset + NativeAdaptiveLayout.mastheadHeight + 4
    @State private var pageVisible = false
    @State private var viewportSize: CGSize = .zero
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
        return (books.count == 1 ? L("1 本") : L("{0} 本", books.count))
            + (reading > 0 ? L(" · {0} 本在读", reading) : "")
    }

    var body: some View {
        GeometryReader { geometry in
            let pageWidth = NativeAdaptiveLayout.pageWidth(viewportWidth: geometry.size.width)
            ScrollView {
                VStack(spacing: 0) {
                    NativeLibraryScrollReader(state: scrollState)
                    NativeLibraryInlineMastheadHost(chrome: scrollState.chrome) {
                        masthead(floating: false)
                    }
                    .background {
                        GeometryReader { proxy in
                            Color.clear
                                .onAppear {
                                    mastheadHeight = proxy.size.height
                                    scrollState.updateMastheadHeight(proxy.size.height)
                                }
                                .onChange(of: proxy.size.height) { height in
                                    mastheadHeight = height
                                    scrollState.updateMastheadHeight(height)
                                }
                        }
                    }
                    if !store.libraryLoaded, let error = store.libraryLoadError { libraryFailure(error) }
                    else if store.preferences.collectionsView { foldersView(width: pageWidth) }
                    else { booksView(width: pageWidth) }
                }
                .frame(width: pageWidth)
                .frame(maxWidth: .infinity)
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
                .modifier(NativeLibraryScrollPhaseObserver(state: scrollState))
                .accessibilityIdentifier(store.libraryLoaded ? "library.ready" : "library.loading")
                .coordinateSpace(name: NativeLibraryScrollState.space)
                .onAppear {
                    viewportSize = geometry.size
                    scrollState.onScrollBucketChanged = { offset in
                        updateVisibleAmbientRegions(offset: offset)
                    }
                    updateVisibleAmbientRegions(offset: scrollState.offset)
                }
                .onChange(of: geometry.size) { size in
                    viewportSize = size
                    updateVisibleAmbientRegions(offset: scrollState.offset)
                }
                .overlay(alignment: .top) {
                    NativeLibraryFloatingMastheadHost(chrome: scrollState.chrome, slideDistance: mastheadHeight) {
                        masthead(floating: true)
                            .frame(width: pageWidth)
                            .frame(maxWidth: .infinity)
                            .background {
                                NativeMastheadGlass(chrome: scrollState.chrome, colors: nil)
                                    .padding(.bottom, -64)
                                    .ignoresSafeArea(edges: .top)
                            }
                    }
                    .zIndex(1)
                }
        }.background(NativeAmbientBackground()).navigationBarHidden(true)
        .overlay(alignment: .top) {
            NativeLibraryStatusBarGlass(chrome: scrollState.chrome)
        }
        .overlay(alignment: .bottom) {
            if selection.active && !store.preferences.collectionsView {
                NativeLibraryBatchBar(
                    selectedCount: selection.selectedIds.count,
                    busy: batchDeleting,
                    onDelete: deleteSelectedBooks
                )
                .frame(maxWidth: NativeAdaptiveLayout.libraryBatchBarMaxWidth)
                .padding(.horizontal, NativeAdaptiveLayout.pageInset)
                .padding(.bottom, 12)
                .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .onAppear {
            bookDestinations.onMultiSelect = { bookId in
                withAnimation(.easeOut(duration: 0.18)) {
                    selection.enter(initialBookId: bookId)
                }
            }
        }
        .onChange(of: store.preferences.collectionsView) { collections in
            if collections && selection.active { selection.exit() }
        }
        .onChange(of: store.library.map(\.id)) { ids in
            selection.prune(validIds: Set(ids))
        }
        .onChange(of: isActive) { active in
            if !active { NativeBookSeriesCenter.shared.dismiss() }
        }
        .sheet(isPresented: $creatingFolder) {
            NativeNameSheet(store: store, title: L("新建收藏夹"), initial: "", button: L("创建")) { name in
                let _: CreatedFolder = try await store.request("collection.create", ["name": name]); try await store.refreshLibrary()
            }
        }
        .sheet(item: $renamingFolder) { folder in
            NativeNameSheet(store: store, title: L("重命名收藏夹"), initial: folder.name, button: L("保存")) { name in
                try await store.command("collection.rename", ["collectionId": folder.id, "name": name]); try await store.refreshLibrary()
            }
        }
        .sheet(isPresented: $showingFilters) {
            if let filterOptions {
                NativeLibraryFilterSheet(options: filterOptions, selection: advancedFilter, quickFilter: filter,
                                         books: store.library, query: query) { selection in
                    filter = NativeLibraryFilterPolicy.effectiveQuickFilter(filter, readingStatus: selection.readingStatus)
                    advancedFilter = selection
                }
            }
        }
        .onAppear {
            pageVisible = true
            updateVisibleAmbientRegions(offset: scrollState.offset)
            publishVisibleColors()
        }
        .onDisappear { pageVisible = false }
        .onChange(of: isActive) { active in
            if active {
                updateVisibleAmbientRegions(offset: scrollState.offset)
                publishVisibleColors()
            }
        }
        .onChange(of: store.preferences.colorfulMode) { _ in updateVisibleAmbientRegions(offset: scrollState.offset) }
        .onChange(of: store.preferences.listView) { _ in updateVisibleAmbientRegions(offset: scrollState.offset) }
        .onChange(of: store.preferences.galleryMode) { _ in updateVisibleAmbientRegions(offset: scrollState.offset) }
        .onChange(of: store.preferences.collectionsView) { _ in updateVisibleAmbientRegions(offset: scrollState.offset) }
        .onChange(of: store.preferences.stackBookSeries) { _ in updateVisibleAmbientRegions(offset: scrollState.offset) }
        .onChange(of: books.map(\.id)) { _ in updateVisibleAmbientRegions(offset: scrollState.offset) }
        .task(id: filterRequest) { await updateFilterResults(for: filterRequest) }
        .task(id: visibleCovers) {
            guard store.preferences.colorfulMode else { return }
            // Coalesce boundary changes without doing image decoding on each frame.
            do { try await Task.sleep(nanoseconds: 120_000_000) }
            catch { return }
            let missing = visibleCovers.filter { sampledColors[$0] == nil }
            guard !missing.isEmpty else { return }
            let samples = await Task.detached(priority: .utility) {
                missing.compactMap { cover -> (NativeVisibleCover, NativeAmbientColor)? in
                    guard let color = NativeAmbientPalette.extract(paths: [cover.path]).first else { return nil }
                    return (cover, color)
                }
            }.value
            guard !Task.isCancelled else { return }
            if sampledColors.count > 300 {
                let keep = Set(visibleCovers)
                sampledColors = sampledColors.filter { keep.contains($0.key) }
            }
            for (cover, color) in samples { sampledColors[cover] = color }
            publishVisibleColors()
        }
    }
    private func updateVisibleAmbientRegions(offset: CGFloat) {
        guard isActive, pageVisible, store.readerBook == nil, store.preferences.colorfulMode,
              !store.preferences.collectionsView else { return }
        let currentBooks = books
        let stack = store.preferences.stackBookSeries && query.isEmpty && filter == 0 && advancedFilter.count == 0
        let displayed: [LibraryBook] = stack
            ? NativeBookSeries.entries(currentBooks).compactMap { entry in
                switch entry {
                case .book(let book): return book
                case .series(let series): return store.frontBook(for: series)
                }
            }
            : currentBooks
        guard !displayed.isEmpty, viewportSize.height > 0 else { return }

        let pageWidth = NativeAdaptiveLayout.pageWidth(viewportWidth: viewportSize.width)
        let hasContinue = query.isEmpty && filter <= 1 && store.continueBookId.flatMap { id in
            currentBooks.first { $0.id == id }
        } != nil
        let headerHeight: CGFloat = 124 + (hasContinue ? 92 : 0)
        let viewportH = max(1, viewportSize.height)
        let listView = store.preferences.listView
        let gallery = store.preferences.galleryMode && !listView
        let cols = listView ? 1 : max(1, NativeBookShelf.columnCount(pageWidth: pageWidth))
        let coverW = NativeBookShelf.coverWidth(pageWidth: pageWidth)
        let rowStride: CGFloat = listView
            ? 86
            : coverW * 1.45 + (gallery ? 18 : 74)
        let totalRows = (displayed.count + cols - 1) / cols
        guard totalRows > 0 else { return }

        let firstRow = max(0, min(totalRows - 1, Int(floor((offset - headerHeight) / rowStride))))
        let lastRow = max(firstRow, min(totalRows - 1, Int(floor((offset + viewportH - headerHeight) / rowStride))))

        var regions: [NativeAmbientCoverRegion] = []
        regions.reserveCapacity(9)
        for rowIdx in firstRow...lastRow {
            let rowTop = headerHeight + CGFloat(rowIdx) * rowStride
            let rowBottom = rowTop + rowStride
            let visibleTop = max(offset, rowTop)
            let visibleBottom = min(offset + viewportH, rowBottom)
            let fraction = Double(max(0, visibleBottom - visibleTop)) / Double(max(1, rowStride))
            let rowCenterY = (visibleTop + visibleBottom) / 2 - offset
            let bandRow = min(2, max(0, Int(rowCenterY / viewportH * 3)))
            let visibility = max(0.25, (fraction * 4).rounded() / 4)

            for colIdx in 0..<cols {
                let idx = rowIdx * cols + colIdx
                guard idx < displayed.count else { break }
                guard let path = displayed[idx].coverPath else { continue }
                let bandCol = listView || cols <= 1 ? 1 : min(2, colIdx * 3 / cols)
                regions.append(NativeAmbientCoverRegion(
                    cover: NativeVisibleCover(path: path, version: displayed[idx].updatedAt),
                    column: bandCol,
                    row: bandRow,
                    visibility: visibility))
                if regions.count == 9 { break }
            }
            if regions.count == 9 { break }
        }

        let covers = regions.map(\.cover)
        if covers != visibleCovers { visibleCovers = covers }
        if regions != visibleRegions {
            visibleRegions = regions
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
        guard !colors.isEmpty, store.ambient.visibleColors != colors else { return }
        // `NativeAmbientBackground` animates color transitions locally so scroll
        // updates never schedule cross-tree SwiftUI animations on the shelf grid.
        store.ambient.visibleColors = colors
    }
    private func masthead(floating: Bool) -> some View {
        HStack(spacing: 0) {
            if selection.active && !store.preferences.collectionsView {
                let visibleIds = books.map(\.id)
                let allSelected = NativeLibraryBatchPolicy.areAllVisibleSelected(
                    visibleIds: visibleIds, in: selection.selectedIds)
                Button(allSelected ? L("取消全选") : L("全选")) {
                    selection.toggleAll(visibleIds: visibleIds)
                }
                .font(NativeTheme.font(15))
                .buttonStyle(.plain)
                .disabled(visibleIds.isEmpty || batchDeleting)
                .accessibilityIdentifier("library.selection.toggleAll")
                Spacer(minLength: 0)
                Text(L("已选 {0} 本", selection.selectedIds.count))
                    .font(NativeTheme.font(15))
                    .foregroundStyle(NativeTheme.ink)
                    .monospacedDigit()
                    .accessibilityIdentifier("library.selection.count")
                Spacer(minLength: 0)
                Button(L("完成")) {
                    withAnimation(.easeOut(duration: 0.18)) { selection.exit() }
                }
                .font(NativeTheme.font(15))
                .buttonStyle(.plain)
                .disabled(batchDeleting)
                .accessibilityIdentifier("library.selection.done")
            } else {
                if searching {
                    NativeSearchField(text: $query, prompt: store.preferences.collectionsView ? L("搜索收藏夹") : L("搜索书名或作者"),
                                      focused: $searchFocused, identifier: "library.search") { searchFocused = false }
                } else {
                    NativePageTitle(title: store.preferences.collectionsView ? L("收藏夹") : L("书库"),
                                    identifier: "library.view.title")
                    Spacer(minLength: 0)
                }
                Button {
                    searching.toggle(); searchFocused = searching
                    if !searching { query = "" }
                } label: {
                    Image(systemName: searching ? "xmark" : "magnifyingglass").font(.system(size: 20, weight: .regular))
                        .frame(width: 44, height: 44)
                }.buttonStyle(.plain).accessibilityLabel(searching ? L("关闭搜索") : L("搜索书库"))
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
                    .accessibilityLabel(store.preferences.collectionsView ? L("新建收藏夹") : L("导入书籍"))
                    .accessibilityIdentifier("library.import")
            }
        }.modifier(NativePageMasthead())
    }
    private var filters: some View {
        HStack(spacing: 0) {
            Picker(L("筛选"), selection: Binding(get: { filter }, set: { value in
                filter = value
                if value == 1 || value == 3 { advancedFilter.readingStatus = nil }
            })) {
                Text(L("全部")).tag(0); Text(L("在读")).tag(1); Text(L("收藏")).tag(2); Text(L("读完")).tag(3)
            }.pickerStyle(.segmented)
                .frame(maxWidth: NativeAdaptiveLayout.libraryFilterMaxWidth)
                .accessibilityIdentifier("library.filter")
            Spacer(minLength: 0)
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
                .accessibilityLabel(advancedFilter.count == 0 ? L("筛选书籍") : L("筛选书籍，已选 {0} 项", advancedFilter.count))
                .accessibilityIdentifier("library.filters.open")
            NativeLibraryDisplayMenu(
                store: store,
                onSelectBooks: selection.active || books.isEmpty ? nil : {
                    withAnimation(.easeOut(duration: 0.18)) { selection.enter() }
                }
            )
        }
    }
    private func booksView(width pageWidth: CGFloat) -> some View {
        // Filtering and sorting the whole library is the costly part of a render.
        let books = self.books
        return VStack(alignment: .leading, spacing: 0) {
            filters.padding(.trailing, -12).padding(.bottom, 12)
            continueCard(books: books)
            if filterPending {
                ProgressView(L("正在筛选书籍…")).frame(maxWidth: .infinity).padding(.top, 96)
            } else if let error = currentFilterResult?.error {
                VStack(spacing: 10) {
                    Text(L("筛选失败")).font(NativeTheme.font(17, relativeTo: .headline))
                    Text(error).font(NativeTheme.font(13)).foregroundStyle(secondaryText)
                    Button(L("重试")) { filterResult = nil; Task { await updateFilterResults(for: filterRequest) } }
                }.frame(maxWidth: .infinity).padding(.top, 96)
            } else {
                Text(summary(books)).font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(mutedText).padding(.bottom, 14)
                if books.isEmpty { emptyView }
                else {
                    NativeBookShelf(store: store, books: books, pageWidth: pageWidth, tracksVisibleCovers: true,
                                    stackSeries: store.preferences.stackBookSeries && query.isEmpty
                                        && filter == 0 && advancedFilter.count == 0,
                                    selectionMode: selection.active,
                                    selectedBookIds: selection.selectedIds,
                                    onToggleSelect: { selection.toggle($0.id) },
                                    onToggleSeriesSelect: { selection.toggleSeries($0.books.map(\.id)) },
                                    expandSeries: { id, frame in
                                        NativeBookSeriesCenter.shared.show(NativeBookSeriesRequest(
                                            seriesID: id, sourceFrame: frame, selection: selection
                                        ) { book, previewFrame, width in
                                            let dismiss = { NativeBookSeriesCenter.shared.dismiss(immediate: true) }
                                            bookDestinations.showPreview(book, source: .seriesVolume, frame: previewFrame, width: width,
                                                                         store: store, actions: bookActions(book, beforeReading: dismiss),
                                                                         beforeNavigation: dismiss)
                                        })
                                    },
                                    destinations: bookDestinations, actions: { bookActions($0) })
                        .id("\(filter)|\(query)|\(advancedFilter.count)|\(advancedFilter.readingStatus ?? "")|\(store.preferences.sort)|\(store.preferences.listView)|\(store.preferences.stackBookSeries)")
                }
            }
        }.padding(.horizontal, NativeAdaptiveLayout.pageInset).padding(.top, 2).padding(.bottom, selection.active ? 92 : 28)
    }

    private func libraryFailure(_ message: String) -> some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.circle").font(.system(size: 32, weight: .ultraLight))
                .foregroundStyle(mutedText)
            Text(L("书库加载失败")).font(NativeTheme.font(18, relativeTo: .headline))
            Text(message).font(NativeTheme.font(13)).foregroundStyle(secondaryText)
                .multilineTextAlignment(.center)
            Button { Task { await refresh() } } label: {
                HStack(spacing: 8) {
                    if loading { ProgressView() }
                    Text(loading ? L("正在重试…") : L("重新加载"))
                }
            }.buttonStyle(NativePrimaryButtonStyle()).disabled(loading)
                .accessibilityIdentifier("library.retry")
                .frame(maxWidth: 240).padding(.top, 8)
        }.padding(.horizontal, 32).frame(maxWidth: .infinity).padding(.top, 96)
            .accessibilityElement(children: .contain).accessibilityIdentifier("library.loadError")
    }
    private var emptyView: some View {
        VStack(spacing: 6) {
            if loading || store.preparingLibrary { ProgressView(L("正在加载书库…")) }
            else {
                Image(systemName: "book").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(mutedText)
                Text(store.library.isEmpty ? L("书库还是空的") : L("没有找到相关书籍"))
                    .font(NativeTheme.font(17, relativeTo: .headline)).padding(.top, 8)
                Text(store.library.isEmpty ? L("点右上角 + 导入 EPUB、PDF 或 TXT") : L("换个关键词或筛选条件试试"))
                    .font(NativeTheme.font(13, relativeTo: .subheadline)).foregroundStyle(mutedText)
                    .multilineTextAlignment(.center)
            }
        }.frame(maxWidth: .infinity).padding(.top, 96).padding(.bottom, 40)
    }
    @ViewBuilder private func continueCard(books: [LibraryBook]) -> some View {
        if !selection.active, query.isEmpty, filter <= 1, let id = store.continueBookId, let book = books.first(where: { $0.id == id }) {
            let key = "continue:\(book.id)"
            Button {
                store.perform { try await store.openReader(book, source: .continueReading) }
            } label: {
                HStack(spacing: 14) {
                    BookCover(book: book, width: 48, height: 68)
                        .trackBookOpenFrame(key: key)
                        .opacity(store.isBookCoverHidden(forKey: key) ? 0 : 1)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(L("继续阅读")).font(NativeTheme.font(13)).foregroundStyle(mutedText)
                        Text(book.title).font(NativeTheme.font(16)).lineLimit(1)
                        Spacer(minLength: 4)
                        HStack(spacing: 10) {
                            NativeThinProgress(value: book.progress)
                            Text("\(book.progress, specifier: "%.0f")%")
                                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
                        }
                    }
                }.frame(height: 68).contentShape(Rectangle())
            }.buttonStyle(.plain).allowsHitTesting(!store.openingReader).accessibilityIdentifier("library.continue")
                .padding(.top, 4).padding(.bottom, 20)
        }
    }
    private func foldersView(width: CGFloat) -> some View {
        let grid = NativeAdaptiveLayout.collectionGrid(pageWidth: width)
        let cardWidth = grid.itemWidth
        let folders = store.folders.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) }
        return VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(L("{0} 个收藏夹", folders.count)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
                Spacer()
                NativeLibraryDisplayMenu(store: store)
            }
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(cardWidth), spacing: grid.spacing), count: grid.columns), spacing: grid.spacing) {
                ForEach(folders) { folder in
                    NavigationLink { NativeCollectionPage(store: store, folderId: folder.id) } label: {
                        NativeFolderCard(folder: folder, books: store.library.filter { $0.collectionIds.contains(folder.id) }, width: cardWidth)
                    }.buttonStyle(.plain).accessibilityIdentifier("collection.\(folder.id)")
                        .contextMenu {
                            if !folder.automatic {
                                Button(L("重命名")) { renamingFolder = folder }
                                Button(L("清空书籍")) { folderAction("clear", folder: folder) }
                                Menu(L("合并到")) {
                                    ForEach(store.folders.filter { !$0.automatic && $0.id != folder.id }) { target in
                                        Button(target.name) { folderAction("merge", folder: folder, target: target.id) }
                                    }
                                }
                                Button(L("解散收藏夹"), role: .destructive) { folderAction("dissolve", folder: folder) }
                            }
                        }
                }
                Button { creatingFolder = true } label: {
                    VStack(spacing: 10) {
                        Image(systemName: "plus").font(.system(size: 20, weight: .regular))
                            .frame(width: 44, height: 44).background(NativeTheme.sunk, in: Circle())
                        Text(L("新建收藏夹")).font(NativeTheme.font(13)).foregroundStyle(mutedText)
                    }.frame(maxWidth: .infinity).frame(height: cardWidth * 1.48)
                    .overlay(RoundedRectangle(cornerRadius: 18).stroke(NativeTheme.strong, style: StrokeStyle(lineWidth: 1.4, dash: [3, 3])))
                }.buttonStyle(.plain).accessibilityIdentifier("collection.create")
            }.accessibilityElement(children: .contain).accessibilityIdentifier("collections.grid")
            Text(L("长按收藏夹可重命名、清空、合并或解散。"))
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
        }.padding(.horizontal, NativeAdaptiveLayout.pageInset).padding(.top, 14).padding(.bottom, 28).disabled(folderBusy)
    }
    private func bookActions(_ book: LibraryBook, beforeReading: @escaping () -> Void = {}) -> [NativeBookMenuItem] {
        [
            NativeBookMenuItem(L("阅读"), systemImage: "book") {
                beforeReading()
                store.perform { try await store.openReader(book, source: .library) }
            },
            NativeBookMenuItem(book.favorite ? L("取消收藏") : L("收藏"),
                               systemImage: book.favorite ? "star.slash" : "star") {
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
    private func deleteSelectedBooks() {
        let ids = Array(selection.selectedIds)
        guard !ids.isEmpty, !batchDeleting else { return }
        batchDeleting = true
        store.perform {
            defer { batchDeleting = false }
            do {
                let reply: BatchDeleteReply = try await store.request("book.deleteBatch", ["bookIds": ids])
                guard !reply.cancelled else { return }
                withAnimation(.easeOut(duration: 0.18)) { selection.exit() }
                try await store.refreshLibrary()
            } catch {
                try? await store.refreshLibrary()
                throw error
            }
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
