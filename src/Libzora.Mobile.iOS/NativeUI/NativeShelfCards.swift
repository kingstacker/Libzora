import SwiftUI
import UIKit

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
                                     store.perform { try await store.openReader(front, sourceFrame: frame, sourceKey: key, source: .library) }
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
            store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key, source: .library) }
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
                                       store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key, source: .library) }
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

struct NativeBookRatingBadge: View {
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

struct NativeBookSyncBadge: View {
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
