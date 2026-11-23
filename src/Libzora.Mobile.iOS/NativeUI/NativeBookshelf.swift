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
    @State private var pageVisible = false
    @State private var viewportSize: CGSize = UIScreen.main.bounds.size
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
        return (books.count == 1 ? L("1 本") : L("{0} 本", books.count))
            + (reading > 0 ? L(" · {0} 本在读", reading) : "")
    }

    var body: some View {
        GeometryReader { geometry in
            let pageWidth = NativeAdaptiveLayout.pageWidth(viewportWidth: geometry.size.width)
            ScrollView {
                VStack(spacing: 0) {
                    NativeLibraryScrollReader(state: scrollState)
                    masthead(floating: false)
                        .opacity(floatingMasthead ? 0 : 1)
                        .accessibilityHidden(floatingMasthead)
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
                    if floatingMasthead {
                        masthead(floating: true)
                            .frame(width: pageWidth)
                            .frame(maxWidth: .infinity)
                            .zIndex(1)
                            .transition(.move(edge: .top).combined(with: .opacity))
                    }
                }
        }.background(NativeAmbientBackground()).navigationBarHidden(true)
        .overlay(alignment: .top) {
            if !floatingMasthead { NativeLibraryStatusBarGlass(scroll: scrollState) }
        }
        .overlay(alignment: .bottom) {
            if selection.active && !store.preferences.collectionsView {
                NativeLibraryBatchBar(
                    selectedCount: selection.selectedIds.count,
                    busy: batchDeleting,
                    onDelete: deleteSelectedBooks
                )
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
            .background {
                if floating {
                    NativeMastheadGlass(glass: scrollState.glass, colors: nil)
                        .padding(.bottom, -64)
                        .ignoresSafeArea(edges: .top)
                }
            }
    }
    private var filters: some View {
        HStack(spacing: 0) {
            Picker(L("筛选"), selection: Binding(get: { filter }, set: { value in
                filter = value
                if value == 1 || value == 3 { advancedFilter.readingStatus = nil }
            })) {
                Text(L("全部")).tag(0); Text(L("在读")).tag(1); Text(L("收藏")).tag(2); Text(L("读完")).tag(3)
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
            filters.padding(.horizontal, -12).padding(.bottom, 12)
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
        }.padding(.horizontal, NativeAdaptiveLayout.pageInset).padding(.top, 8).padding(.bottom, selection.active ? 92 : 28)
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
                let frame = NativeBookCoverFrames.get(key)
                store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key) }
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
            }.buttonStyle(.plain).disabled(store.openingReader).accessibilityIdentifier("library.continue")
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
                store.perform { try await store.openReader(book) }
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

final class NativeLibraryGlassState: ObservableObject {
    @Published fileprivate(set) var opacity: Double = 0
}

/// Tracks the scroll direction to reveal the shelf actions on a downward drag.
final class NativeLibraryScrollState: ObservableObject {
    static let space = "library.scroll"
    let glass = NativeLibraryGlassState()
    @Published private(set) var showMasthead = false
    private(set) var offset: CGFloat = 0
    var onScrollBucketChanged: ((CGFloat) -> Void)?
    private var anchor: CGFloat?
    private var directionAnchor: CGFloat = 0
    private var maximumOffset: CGFloat = 0
    private var lastValue: CGFloat?
    private var lastSample = ProcessInfo.processInfo.systemUptime
    private var scrollSpeed: CGFloat = 0
    private var lastAmbientBucket = -1

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
            updateGlassOpacity()
        }
    }

    private func updateGlassOpacity() {
        let clamped = min(1, max(0, offset / 48))
        let quantized = (clamped * 25).rounded() / 25
        if abs(quantized - glass.opacity) >= 0.03 {
            glass.opacity = quantized
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
                if showMasthead { showMasthead = false }
                directionAnchor = value
            } else if value > directionAnchor + 12 {
                if showMasthead { withAnimation(.easeOut(duration: mastheadDuration)) { showMasthead = false } }
                directionAnchor = value
            } else if value < directionAnchor - 12 {
                if value >= 64 && !showMasthead { withAnimation(.easeOut(duration: mastheadDuration)) { showMasthead = true } }
                directionAnchor = value
            }
            offset = value
            updateGlassOpacity()
            let bucket = Int(max(0, value) / 64)
            if bucket != lastAmbientBucket {
                lastAmbientBucket = bucket
                onScrollBucketChanged?(value)
            }
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
    @ObservedObject var glass: NativeLibraryGlassState
    let colors: [NativeAmbientColor]?
    @EnvironmentObject private var store: NativeStore
    var body: some View {
        ZStack {
            Rectangle().fill(.regularMaterial)
            NativeTheme.page.opacity(0.15)
            if store.preferences.colorfulMode {
                NativeAmbientWash(colorsOverride: colors).opacity(0.22)
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
            .opacity(glass.opacity)
    }
}

private struct NativeLibraryStatusBarGlass: View {
    @ObservedObject var scroll: NativeLibraryScrollState
    var body: some View {
        NativeLibraryStatusBarGlassBody(glass: scroll.glass)
    }
}

private struct NativeLibraryStatusBarGlassBody: View {
    @ObservedObject var glass: NativeLibraryGlassState
    var body: some View {
        NativeStatusBarGlass().opacity(glass.opacity)
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
    let identifier: String?
    let systemImage: String?
    let action: () -> Void

    init(_ title: String, destructive: Bool = false, identifier: String? = nil,
         systemImage: String? = nil, action: @escaping () -> Void) {
        self.title = title; self.destructive = destructive
        self.identifier = identifier; self.systemImage = systemImage; self.action = action
    }
}

/// Both the library and an opened collection use the same cover geometry,
/// text, spacing, navigation and persisted grid/list preference.
struct NativeBookShelf: View {
    @ObservedObject var store: NativeStore
    @ObservedObject private var previewCenter = NativeBookPreviewCenter.shared
    @StateObject private var destinations: NativeShelfBookDestinations
    let books: [LibraryBook]
    let pageWidth: CGFloat
    let tracksVisibleCovers: Bool
    private let stackSeries: Bool
    private let selectionMode: Bool
    private let selectedBookIds: Set<String>
    private let onToggleSelect: (LibraryBook) -> Void
    private let onToggleSeriesSelect: (NativeBookSeries) -> Void
    private let expandSeries: (NativeBookSeries.ID, CGRect?) -> Void
    private let actions: (LibraryBook) -> [NativeBookMenuItem]

    init(store: NativeStore, books: [LibraryBook], pageWidth: CGFloat, tracksVisibleCovers: Bool = false,
         stackSeries: Bool = false,
         selectionMode: Bool = false,
         selectedBookIds: Set<String> = [],
         onToggleSelect: @escaping (LibraryBook) -> Void = { _ in },
         onToggleSeriesSelect: @escaping (NativeBookSeries) -> Void = { _ in },
         expandSeries: @escaping (NativeBookSeries.ID, CGRect?) -> Void = { _, _ in },
         destinations: NativeShelfBookDestinations? = nil,
         actions: @escaping (LibraryBook) -> [NativeBookMenuItem]) {
        self.store = store; self.books = books; self.pageWidth = pageWidth
        self.tracksVisibleCovers = tracksVisibleCovers; self.stackSeries = stackSeries
        self.selectionMode = selectionMode; self.selectedBookIds = selectedBookIds
        self.onToggleSelect = onToggleSelect; self.onToggleSeriesSelect = onToggleSeriesSelect
        self.actions = actions
        self.expandSeries = expandSeries
        _destinations = StateObject(wrappedValue: destinations ?? NativeShelfBookDestinations())
    }
    private static let columnSpacing: CGFloat = 16

    static func columnCount(pageWidth: CGFloat) -> Int { NativeAdaptiveLayout.bookGrid(pageWidth: pageWidth).columns }
    static func coverWidth(pageWidth: CGFloat) -> CGFloat { NativeAdaptiveLayout.bookGrid(pageWidth: pageWidth).itemWidth }
    private var columns: Int { Self.columnCount(pageWidth: pageWidth) }
    private var coverWidth: CGFloat { Self.coverWidth(pageWidth: pageWidth) }
    private var galleryMode: Bool { store.preferences.galleryMode && !store.preferences.listView }
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }

    var body: some View {
        Group {
            if store.preferences.listView {
                LazyVStack(spacing: 0) { ForEach(books) { book in bookLink(book) } }
            } else {
                let column = GridItem(.fixed(coverWidth), spacing: Self.columnSpacing, alignment: .topLeading)
                LazyVGrid(columns: Array(repeating: column, count: columns),
                          alignment: .leading, spacing: galleryMode ? 18 : 26) {
                    if stackSeries {
                        ForEach(NativeBookSeries.entries(books)) { entry in
                            switch entry {
                            case .book(let book): bookLink(book)
                            case .series(let series): seriesLink(series)
                            }
                        }
                    } else {
                        ForEach(books) { book in bookLink(book) }
                    }
                }
            }
        }.accessibilityElement(children: .contain)
            .accessibilityIdentifier(store.preferences.listView ? "books.list" : "books.grid")
            .background {
                NavigationLink(isActive: $destinations.showingDetail) {
                    if let id = destinations.detailBookID { NativeBookDetailPage(store: store, bookId: id) }
                } label: { EmptyView() }
                    .hidden().accessibilityHidden(true)
            }
            .sheet(item: $destinations.collectionBook) { book in
                NativeBookCollections(store: store, bookId: book.id)
            }
    }
    @ViewBuilder private func seriesLink(_ series: NativeBookSeries) -> some View {
        let volumes = series.books
        if let front = store.frontBook(for: series) {
            let selectedCount = NativeLibraryBatchPolicy.selectedCount(
                in: volumes.map(\.id), from: selectedBookIds)
            NativeBookSeriesTile(title: series.title, seriesID: series.id, volumes: volumes, front: front,
                                 width: coverWidth, muted: mutedText, galleryMode: galleryMode,
                                 showRating: store.preferences.showBookRating,
                                 showSyncStatus: store.preferences.showBookSyncStatus,
                                 enabled: !store.openingReader,
                                 selectionMode: selectionMode,
                                 selectedVolumeCount: selectedCount,
                                 lifted: (previewCenter.request?.source == .seriesStack && previewCenter.request?.id == front.id)
                                     || store.isBookCoverHidden(forKey: "shelf:\(front.id)"),
                                 open: {
                                     if selectionMode {
                                         onToggleSeriesSelect(series)
                                         return
                                     }
                                     let key = "shelf:\(front.id)"
                                     let frame = NativeBookCoverFrames.get(key)
                                     store.perform { try await store.openReader(front, sourceFrame: frame, sourceKey: key) }
                                 },
                                 expand: { frame in expandSeries(series.id, frame) },
                                 longPress: { frame in
                guard !selectionMode else { return }
                destinations.showPreview(front, source: .seriesStack, frame: frame, width: coverWidth,
                                         store: store, actions: actions(front))
            })
                .disabled(store.openingReader)
        }
    }

    private func bookLink(_ book: LibraryBook) -> some View {
        // The app-owned preview draws its own copy, so the source tile hides
        // while it is up, exactly like the system lift used to.
        let key = "shelf:\(book.id)"
        let previewLifted = previewCenter.request?.source == .shelf && previewCenter.request?.id == book.id
        let flightLifted = store.isBookCoverHidden(forKey: key)
        let isSelected = selectionMode && selectedBookIds.contains(book.id)
        return Button {
            if selectionMode {
                onToggleSelect(book)
                return
            }
            let frame = NativeBookCoverFrames.get(key)
            store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key) }
        } label: {
            Group {
                if store.preferences.listView {
                    HStack(spacing: 12) {
                        BookRow(book: book, showRating: store.preferences.showBookRating,
                                showSyncStatus: store.preferences.showBookSyncStatus, muted: mutedText,
                                coverFrameKey: key, hideCover: flightLifted)
                            .opacity(previewLifted ? 0 : 1)
                        if selectionMode {
                            NativeSelectionBadge(selected: isSelected)
                        }
                    }
                    .contentShape(Rectangle())
                } else {
                    NativeBookTile(book: book, width: coverWidth, muted: mutedText, galleryMode: galleryMode,
                                   showRating: store.preferences.showBookRating,
                                   showSyncStatus: store.preferences.showBookSyncStatus,
                                   coverFrameKey: key, hideCover: flightLifted)
                        .opacity(previewLifted ? 0 : 1)
                        .overlay(alignment: .topTrailing) {
                            if selectionMode {
                                NativeSelectionBadge(selected: isSelected).padding(6)
                            }
                        }
                }
            }
        }.buttonStyle(.plain).disabled(store.openingReader)
            .overlay {
                if !selectionMode {
                    NativeBookLink(enabled: !store.openingReader,
                                   tap: {
                                       let frame = NativeBookCoverFrames.get(key)
                                       store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key) }
                                   },
                                   longPress: { frame in
                        destinations.showPreview(book, source: .shelf, frame: frame, width: coverWidth,
                                                 isListRow: store.preferences.listView,
                                                 store: store, actions: actions(book))
                    })
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .accessibilityValue(selectionMode ? (isSelected ? L("已选中") : L("未选中")) : "")
            .accessibilityHint(selectionMode ? L("轻点选中或取消") : L("轻点阅读，长按查看更多操作"))
            .accessibilityIdentifier("book.\(book.id)")
    }
}

@MainActor
final class NativeShelfBookDestinations: ObservableObject {
    @Published var detailBookID: String?
    @Published var showingDetail = false
    @Published var collectionBook: LibraryBook?
    var onMultiSelect: ((String) -> Void)?

    func showPreview(_ book: LibraryBook, source: NativeBookPreviewRequest.Source, frame: CGRect, width: CGFloat,
                     isListRow: Bool = false, store: NativeStore, actions: [NativeBookMenuItem],
                     beforeNavigation: @escaping () -> Void = {}) {
        var menuItems = actions
        if let onMultiSelect {
            menuItems.append(NativeBookMenuItem(L("多选"), identifier: "book.action.multiSelect", systemImage: "checkmark.circle") {
                NativeBookPreviewCenter.shared.dismiss(immediate: true)
                beforeNavigation()
                onMultiSelect(book.id)
            })
        }
        menuItems.append(contentsOf: [
            NativeBookMenuItem(L("加入收藏夹"), identifier: "book.action.collections", systemImage: "folder.badge.plus") {
                NativeBookPreviewCenter.shared.dismiss(immediate: true)
                beforeNavigation()
                self.collectionBook = book
            },
            NativeBookMenuItem(L("书籍详情"), identifier: "book.action.details", systemImage: "info.circle") {
                NativeBookPreviewCenter.shared.dismiss(immediate: true)
                beforeNavigation()
                self.detailBookID = book.id
                self.showingDetail = true
            },
            NativeBookMenuItem(L("移到回收站"), destructive: true, identifier: "book.action.delete", systemImage: "trash") {
                store.perform {
                    try await store.command("book.delete", ["bookId": book.id])
                    try await store.refreshLibrary()
                }
            }
        ])
        NativeBookPreviewCenter.shared.show(NativeBookPreviewRequest(
            book: book,
            source: source,
            isListRow: isListRow,
            sourceFrame: frame,
            coverWidth: width,
            muted: store.preferences.colorfulMode && source != .seriesVolume ? NativeTheme.mutedOnColorful : NativeTheme.muted,
            showRating: source != .seriesStack && store.preferences.showBookRating,
            showSyncStatus: source != .seriesStack && store.preferences.showBookSyncStatus,
            actions: menuItems))
    }
}

/// A transparent touch layer over the card. A quick tap opens the book; a
/// long press hands the card's window frame to the app-owned preview overlay,
/// which draws no system plate, mask or rounded card edge.
struct NativeBookLink: UIViewRepresentable {
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

final class NativeBookLinkView: UIButton {
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
    var onSelectBooks: (() -> Void)? = nil
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    var body: some View {
        Menu {
            if let onSelectBooks {
                Button(action: onSelectBooks) {
                    Label(L("选择书籍"), systemImage: "checkmark.circle")
                }
                .accessibilityIdentifier("library.selectBooks")
            }
            Section(L("视图")) {
                Picker(L("视图"), selection: Binding(get: {
                    store.preferences.collectionsView ? 2 : store.preferences.listView ? 1 : 0
                }, set: { view in
                    var value = store.preferences
                    value.collectionsView = view == 2
                    if view != 2 { value.listView = view == 1 }
                    store.setPreferences(value)
                })) {
                    Label(L("网格视图"), systemImage: "square.grid.2x2").tag(0)
                    Label(L("列表视图"), systemImage: "list.bullet").tag(1)
                    Label(L("收藏夹视图"), systemImage: "folder").tag(2)
                }
            }
            if !store.preferences.collectionsView {
                Section(L("排序")) {
                    Picker(L("排序"), selection: Binding(get: { store.preferences.sort }, set: { sort in
                        var value = store.preferences; value.sort = sort
                        store.setPreferences(value)
                    })) {
                        Text(L("最近更新")).tag(0); Text(L("书名")).tag(1); Text(L("作者")).tag(2); Text(L("添加时间")).tag(3)
                    }
                }
            }
        } label: {
            Image(systemName: "ellipsis").font(.system(size: 18, weight: .medium)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(mutedText)
            .accessibilityLabel(L("显示选项")).accessibilityIdentifier("library.display")
    }
}

struct NativeBookSortMenu: View {
    @ObservedObject var store: NativeStore
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    var body: some View {
        Menu {
            Picker(L("排序"), selection: Binding(get: { store.preferences.sort }, set: { sort in
                var value = store.preferences; value.sort = sort
                store.setPreferences(value)
            })) {
                Text(L("最近更新")).tag(0); Text(L("书名")).tag(1); Text(L("作者")).tag(2); Text(L("添加时间")).tag(3)
            }
        } label: {
            Image(systemName: "line.3.horizontal.decrease").font(.system(size: 18, weight: .regular)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(mutedText).accessibilityLabel(L("书籍排序"))
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
            .accessibilityLabel(store.preferences.listView ? L("网格视图") : L("列表视图"))
            .accessibilityIdentifier(identifier)
    }
}

struct NativeBookTile: View {
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont
    let book: LibraryBook
    let width: CGFloat
    var muted = NativeTheme.muted
    var galleryMode = false
    var showRating = false
    var showSyncStatus = false
    var previewing = false
    var coverFrameKey: String? = nil
    var hideCover = false
    var body: some View {
        let _ = uiLocale
        let _ = uiFont

        VStack(alignment: .leading, spacing: 0) {
            BookCover(book: book, width: width)
                .trackBookOpenFrame(key: coverFrameKey)
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
                .opacity(hideCover ? 0 : 1)
            if !galleryMode {
                previewMetadata(Text(book.title).font(NativeTheme.font(13.5)).lineLimit(2).lineSpacing(3).padding(.top, 8),
                                "book.preview.title")
                previewMetadata(Text(book.authors).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(muted)
                    .lineLimit(1).padding(.top, 2), "book.preview.author")
            }
        }.frame(maxWidth: .infinity, alignment: .topLeading).contentShape(Rectangle())
            .accessibilityElement(children: previewing ? .contain : .ignore)
            .accessibilityLabel(book.title
                + (showRating ? book.onlineRating.map { L("，评分 {0}", NativeBookRatingBadge.text($0)) } ?? "" : "")
                + (showSyncStatus && NativeBookSyncBadge.isPending(book.syncStatus) ? NativeBookSyncBadge.description(book.syncStatus).map { "，\($0)" } ?? "" : ""))
    }

    /// Only the menu preview exposes separate title/author elements; the
    /// shelf tile stays one compact accessibility element.
    @ViewBuilder private func previewMetadata<Content: View>(_ content: Content, _ identifier: String) -> some View {
        if previewing { content.accessibilityIdentifier(identifier) } else { content }
    }
}

struct NativeBookSeriesRequest {
    let seriesID: NativeBookSeries.ID
    let sourceFrame: CGRect?
    var selection: NativeLibrarySelectionState? = nil
    let preview: (LibraryBook, CGRect, CGFloat) -> Void
}

@MainActor
final class NativeBookSeriesCenter: ObservableObject {
    static let shared = NativeBookSeriesCenter()

    @Published private(set) var request: NativeBookSeriesRequest?
    @Published private(set) var isVisible = false
    private var dismissTask: Task<Void, Never>?

    private init() {}

    func show(_ request: NativeBookSeriesRequest) {
        dismissTask?.cancel()
        dismissTask = nil
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        self.request = request
        isVisible = false
    }

    func reveal() {
        guard request != nil, !isVisible else { return }
        withAnimation(.spring(response: 0.36, dampingFraction: 0.70)) { isVisible = true }
    }

    func dismiss(immediate: Bool = false) {
        guard request != nil else { return }
        dismissTask?.cancel()
        if immediate {
            isVisible = false
            request = nil
            return
        }
        withAnimation(.spring(response: 0.30, dampingFraction: 0.86)) { isVisible = false }
        dismissTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 240_000_000)
            guard !Task.isCancelled else { return }
            self?.request = nil
        }
    }
}

private struct NativeBookSeriesTile: View {
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont
    let title: String
    let seriesID: NativeBookSeries.ID
    let volumes: [LibraryBook]
    let front: LibraryBook
    let width: CGFloat
    let muted: Color
    let galleryMode: Bool
    let showRating: Bool
    let showSyncStatus: Bool
    let enabled: Bool
    var selectionMode = false
    var selectedVolumeCount = 0
    let lifted: Bool
    let open: () -> Void
    let expand: (CGRect?) -> Void
    let longPress: (CGRect) -> Void

    var body: some View {
        let _ = uiLocale
        let _ = uiFont
        let count = L("{0} 册", volumes.count)
        let coverHeight = width * 1.45
        VStack(alignment: .leading, spacing: 0) {
            BookCover(book: front, width: width)
                .overlay { RoundedRectangle(cornerRadius: 6).strokeBorder(.primary.opacity(0.12)) }
                .overlay(alignment: .topLeading) {
                    if showRating, let rating = front.onlineRating {
                        NativeBookRatingBadge(rating: rating).padding(5)
                    }
                }
                .overlay(alignment: .bottomLeading) {
                    if showSyncStatus, let status = front.syncStatus, NativeBookSyncBadge.isPending(status) {
                        NativeBookSyncBadge(status: status).padding(5)
                    }
                }
                .overlay(alignment: .topTrailing) {
                    if selectionMode {
                        NativeSeriesSelectionBadge(
                            selectedCount: selectedVolumeCount,
                            totalCount: volumes.count
                        ).padding(6)
                    }
                }
                .accessibilityHidden(true)
                .allowsHitTesting(false)
                .frame(width: width, height: coverHeight, alignment: .leading)
                .overlay {
                    Button(action: open) {
                        Color.clear.frame(width: width, height: coverHeight).contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .overlay {
                        if !selectionMode {
                            NativeBookLink(enabled: enabled, tap: open, longPress: longPress)
                        }
                    }
                    .accessibilityLabel(front.title)
                    .accessibilityValue(selectionMode ? L("已选 {0}/{1} 册", selectedVolumeCount, volumes.count) : "")
                    .accessibilityHint(selectionMode ? L("轻点选中或取消") : L("轻点阅读，长按查看更多操作"))
                    .accessibilityIdentifier("books.series.front.\(front.id)")
                }
                .overlay(alignment: .bottomTrailing) {
                    NativeBookSeriesBadgeButton(
                        count: count,
                        width: width,
                        coverHeight: coverHeight,
                        enabled: enabled,
                        accessibilityLabel: L("展开系列：{0}", title),
                        identifier: "books.series.\(seriesID.name)",
                        action: expand
                    )
                }
                .opacity(lifted ? 0 : 1)
                .trackBookOpenFrame(key: "shelf:\(front.id)")
            if !galleryMode {
                let author = volumes.lazy.map(\.authors).first { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } ?? front.authors
                Text(title).font(NativeTheme.font(13.5)).lineLimit(2).lineSpacing(3).padding(.top, 8)
                Text(author).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(muted)
                    .lineLimit(1).padding(.top, 2)
            }
        }.frame(maxWidth: .infinity, alignment: .topLeading).contentShape(Rectangle())
    }
}

private struct NativeBookSeriesBadge: View {
    let count: String

    var body: some View {
        Text(count)
            .font(NativeTheme.font(10.5, relativeTo: .caption2))
            .monospacedDigit()
            .foregroundStyle(NativeTheme.ink)
            .padding(.horizontal, 7)
            .padding(.vertical, 3.5)
            .background(.regularMaterial, in: Capsule())
            .overlay { Capsule().strokeBorder(.primary.opacity(0.14), lineWidth: 0.5) }
            .shadow(color: .black.opacity(0.08), radius: 2, y: 1)
    }
}

private struct NativeBookSeriesBadgeButton: View {
    let count: String
    let width: CGFloat
    let coverHeight: CGFloat
    let enabled: Bool
    let accessibilityLabel: String
    let identifier: String
    let action: (CGRect?) -> Void

    var body: some View {
        Button { action(nil) } label: {
            NativeBookSeriesBadge(count: count)
                .padding(.trailing, 5)
                .padding(.bottom, 5)
                .frame(minWidth: 44, minHeight: 34, alignment: .bottomTrailing)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .overlay {
            NativeSeriesBadgeTapView(enabled: enabled) { badgeFrame in
                let coverFrame = badgeFrame.map {
                    CGRect(x: $0.maxX - width, y: $0.maxY - coverHeight, width: width, height: coverHeight)
                }
                action(coverFrame)
            }
        }
        .accessibilityLabel(accessibilityLabel)
        .accessibilityValue(count)
        .accessibilityIdentifier(identifier)
    }
}

private struct NativeSeriesBadgeTapView: UIViewRepresentable {
    let enabled: Bool
    let tap: (CGRect?) -> Void

    func makeUIView(context: Context) -> NativeSeriesBadgeTouchView { NativeSeriesBadgeTouchView() }

    func updateUIView(_ view: NativeSeriesBadgeTouchView, context: Context) {
        view.isEnabled = enabled
        view.onTap = tap
    }
}

private final class NativeSeriesBadgeTouchView: UIButton {
    var onTap: ((CGRect?) -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        isAccessibilityElement = false
        backgroundColor = .clear
        addTarget(self, action: #selector(handleTap), for: .touchUpInside)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    @objc private func handleTap() {
        guard isEnabled else { return }
        onTap?(window.map { convert(bounds, to: $0) })
    }
}

struct NativeBookSeriesOverlay: View {
    @ScaledMetric(relativeTo: .body) private var metadataHeight: CGFloat = 62
    @ScaledMetric(relativeTo: .headline) private var scaledHeaderHeight: CGFloat = 44
    @ObservedObject private var previewCenter = NativeBookPreviewCenter.shared
    let series: NativeBookSeries
    let sourceFrame: CGRect?
    @ObservedObject var store: NativeStore
    @ObservedObject var center: NativeBookSeriesCenter
    var selection: NativeLibrarySelectionState? = nil
    let close: () -> Void
    let preview: (LibraryBook, CGRect, CGFloat) -> Void

    var body: some View {
        GeometryReader { safeArea in
            overlay(in: safeArea.frame(in: .global))
                .ignoresSafeArea()
        }
        .allowsHitTesting(center.isVisible)
        .task { center.reveal() }
    }

    private func overlay(in safeArea: CGRect) -> some View {
        GeometryReader { proxy in
            let origin = proxy.frame(in: .global).origin
            let localSafeArea = safeArea.offsetBy(dx: -origin.x, dy: -origin.y)
            let bounds = NativeAdaptiveLayout.popupBounds(in: proxy.size,
                safeArea: localSafeArea,
                bottomBarInset: NativeTheme.bottomTabBarInset)
            let margin: CGFloat = 16
            let gap: CGFloat = 8
            let metrics = NativeAdaptiveLayout.seriesPanel(
                in: CGSize(width: proxy.size.width, height: min(proxy.size.height, bounds.height + 32)),
                preferredCoverWidth: NativeBookShelf.coverWidth(pageWidth: proxy.size.width),
                headerHeight: scaledHeaderHeight, metadataHeight: metadataHeight)
            let panelWidth = metrics.width
            let coverWidth = metrics.coverWidth
            let headerHeight = metrics.headerHeight
            let rowHeight = metrics.rowHeight
            let panelHeight = metrics.height
            let source = sourceFrame.map { $0.offsetBy(dx: -origin.x, dy: -origin.y) }
            let topLimit = bounds.minY
            let bottomLimit = bounds.maxY
            let minX = margin + panelWidth / 2
            let maxX = max(minX, proxy.size.width - margin - panelWidth / 2)
            let x = min(max(source?.midX ?? proxy.size.width / 2, minX), maxX)
            let below = (source?.maxY ?? bottomLimit) + gap
            let above = (source?.minY ?? bottomLimit) - gap
            let opensBelow = below + panelHeight <= bottomLimit || bottomLimit - below >= above - topLimit
            let preferredTop = opensBelow ? below : above - panelHeight
            let top = max(topLimit, min(preferredTop, max(topLimit, bottomLimit - panelHeight)))
            ZStack {
                Color.black.opacity(center.isVisible ? 0.22 : 0)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture(perform: close)
                    .accessibilityHidden(true)
                panel(coverWidth: coverWidth, headerHeight: headerHeight, rowHeight: rowHeight)
                    .frame(width: panelWidth, height: panelHeight)
                    .modifier(NativeBookSeriesGlass())
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier("books.series.detail")
                    .accessibilityAction(.escape, close)
                    .scaleEffect(center.isVisible ? 1 : 0.78, anchor: opensBelow ? .top : .bottom)
                    .opacity(center.isVisible ? 1 : 0)
                    .position(x: x, y: top + panelHeight / 2)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func panel(coverWidth: CGFloat, headerHeight: CGFloat, rowHeight: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text(series.title).font(NativeTheme.font(18, relativeTo: .headline)).lineLimit(1)
                    .accessibilityAddTraits(.isHeader)
                Text(L("{0} 册", series.books.count)).font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(NativeTheme.secondary)
                Spacer(minLength: 0)
                Button(action: close) {
                    Image(systemName: "xmark").font(.system(size: 14, weight: .semibold))
                        .frame(width: 44, height: 44).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(L("关闭预览"))
                .accessibilityIdentifier("books.series.close")
            }
            .frame(height: headerHeight)
            .padding(.leading, 18).padding(.trailing, 6)
            ScrollViewReader { scrollProxy in
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(alignment: .top, spacing: 14) {
                        ForEach(series.books) { book in
                            if let selection {
                                NativeSeriesVolumeTile(
                                    book: book,
                                    coverWidth: coverWidth,
                                    store: store,
                                    selection: selection,
                                    lifted: previewCenter.request?.source == .seriesVolume && previewCenter.request?.id == book.id,
                                    open: { open(book) },
                                    preview: { frame in preview(book, frame, coverWidth) }
                                )
                                .id(book.id)
                            } else {
                                let lifted = previewCenter.request?.source == .seriesVolume && previewCenter.request?.id == book.id
                                let volumeKey = "seriesVolume:\(book.id)"
                                Button { open(book) } label: {
                                    NativeBookTile(book: book, width: coverWidth,
                                                   showRating: store.preferences.showBookRating,
                                                   showSyncStatus: store.preferences.showBookSyncStatus,
                                                   coverFrameKey: volumeKey,
                                                   hideCover: store.isBookCoverHidden(forKey: volumeKey))
                                        .frame(width: coverWidth)
                                        .opacity(lifted ? 0 : 1)
                                }
                                .buttonStyle(.plain)
                                .disabled(store.openingReader)
                                .overlay {
                                    NativeBookLink(enabled: !store.openingReader, tap: { open(book) },
                                                   longPress: { frame in preview(book, frame, coverWidth) })
                                }
                                .accessibilityHint(L("轻点阅读，长按查看更多操作"))
                                .accessibilityIdentifier("books.series.volume.\(book.id)")
                                .id(book.id)
                            }
                        }
                    }
                    .padding(.horizontal, 18).padding(.bottom, 14)
                }
                .onAppear {
                    if let targetID = store.frontBook(for: series)?.id {
                        scrollProxy.scrollTo(targetID, anchor: .center)
                    }
                }
            }
            .frame(height: rowHeight)
            .accessibilityIdentifier("books.series.volumes")
        }
        .padding(.top, 4)
        .foregroundStyle(NativeTheme.ink)
    }

    private func open(_ book: LibraryBook) {
        let key = "seriesVolume:\(book.id)"
        let frame = NativeBookCoverFrames.get(key)
        close()
        store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key) }
    }
}

private struct NativeBookSeriesGlass: ViewModifier {
    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 26, style: .continuous)
        if #available(iOS 26.0, *) {
            content.glassEffect(.regular.interactive(), in: shape)
                .shadow(color: .black.opacity(0.10), radius: 18, y: 8)
        } else {
            content.background(.regularMaterial, in: shape)
                .shadow(color: .black.opacity(0.18), radius: 24, y: 10)
        }
    }
}

struct NativeFolderCard: View {
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont
    let folder: LibraryFolder
    let books: [LibraryBook]
    let width: CGFloat
    var body: some View {
        let _ = uiLocale
        let _ = uiFont

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
                Text(L("{0} 本", folder.count)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            }.padding(.horizontal, 16).padding(.bottom, 15)
        }.frame(maxWidth: .infinity).frame(height: width * 1.48, alignment: .top)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
}

private struct NativeBookRatingBadge: View {
    @Environment(\.locale) private var uiLocale
    let rating: Double

    static func text(_ rating: Double) -> String { String(format: "%.1f", rating) }

    var body: some View {
        let _ = uiLocale

        HStack(spacing: 2) {
            Image(systemName: "star.fill").font(.system(size: 8.5, weight: .semibold))
            Text(Self.text(rating)).font(NativeTheme.font(10, relativeTo: .caption2)).monospacedDigit()
        }.foregroundStyle(NativeTheme.ink)
            .padding(.horizontal, 5).padding(.vertical, 3)
            .background(.regularMaterial, in: Capsule())
            .accessibilityLabel(L("评分 {0}", Self.text(rating)))
    }
}

private struct NativeBookSyncBadge: View {
    @Environment(\.locale) private var uiLocale
    let status: Int

    /// Settled books stay quiet: only unsynced or cloud-only books need a badge.
    static func isPending(_ status: Int?) -> Bool { status == 0 || status == 2 }

    static func description(_ status: Int?) -> String? {
        switch status {
        case 0: return L("未同步")
        case 1, 3: return L("已同步")
        case 2: return L("已同步到云端，尚未下载")
        default: return nil
        }
    }

    var body: some View {
        let _ = uiLocale

        let symbol = status == 0 ? "icloud.and.arrow.up" : status == 2 ? "icloud.and.arrow.down" : "checkmark.icloud"
        Image(systemName: symbol).font(.system(size: 11, weight: .medium))
            .foregroundStyle(NativeTheme.ink).padding(5)
            .background(.regularMaterial, in: Circle())
            .accessibilityLabel(Self.description(status) ?? L("同步状态未知"))
    }
}

struct BookRow: View {
    @Environment(\.nativeUiFont) private var uiFont
    let book: LibraryBook
    var showRating = false
    var showSyncStatus = false
    var muted = NativeTheme.muted
    var previewing = false
    var coverFrameKey: String? = nil
    var hideCover = false
    var body: some View {
        let _ = uiFont
        HStack(spacing: 16) {
            BookCover(book: book, width: 46, height: 66)
                .trackBookOpenFrame(key: coverFrameKey)
                .opacity(hideCover ? 0 : 1)
            VStack(alignment: .leading, spacing: 4) {
                previewMetadata(Text(book.title).font(NativeTheme.font(15)).lineLimit(1), "book.preview.title")
                HStack(spacing: 10) {
                    previewMetadata(Text(book.authors).font(NativeTheme.font(12.5, relativeTo: .caption)).lineLimit(1), "book.preview.author")
                    Spacer(minLength: 0)
                    if showRating, let rating = book.onlineRating { NativeBookRatingBadge(rating: rating) }
                    if showSyncStatus, let status = book.syncStatus, NativeBookSyncBadge.isPending(status) { NativeBookSyncBadge(status: status) }
                    Text("\(book.progress, specifier: "%.0f")%").font(NativeTheme.font(12, relativeTo: .caption))
                }.foregroundStyle(muted)
            }
        }.padding(.vertical, 10).contentShape(Rectangle())
    }

    @ViewBuilder private func previewMetadata<Content: View>(_ content: Content, _ identifier: String) -> some View {
        if previewing { content.accessibilityIdentifier(identifier) } else { content }
    }
}
