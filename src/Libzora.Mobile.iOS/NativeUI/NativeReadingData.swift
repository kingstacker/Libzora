import SwiftUI
import UIKit

struct NativeMaterials: View {
    @ObservedObject var store: NativeStore
    @State private var entries: [MaterialRecord] = []
    @State private var filter = "all"
    @State private var loading = true
    @State private var exporting = false
    @State private var openedEntryId: String?
    @State private var pendingDeletionIds: Set<String> = []
    @State private var editingReflection: (bookId: String, title: String)?
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    private struct BookGroup: Identifiable {
        let id: String
        var records: [MaterialRecord]
    }
    private func matches(_ item: MaterialRecord) -> Bool { filter == "all" || item.kind == filter }
    /// One pass over the entries. They arrive newest first, so the order in
    /// which books first appear also sorts the books by their latest entry.
    private var groups: [BookGroup] {
        var groups: [BookGroup] = []
        var index: [String: Int] = [:]
        for item in entries where matches(item) {
            if let position = index[item.bookId] { groups[position].records.append(item) }
            else { index[item.bookId] = groups.count; groups.append(BookGroup(id: item.bookId, records: [item])) }
        }
        return groups.map { group in
            let sorted = group.records.sorted { lhs, rhs in
                let lhsReflection = lhs.kind == "reflection"
                let rhsReflection = rhs.kind == "reflection"
                if lhsReflection != rhsReflection { return lhsReflection }
                return false
            }
            return BookGroup(id: group.id, records: sorted)
        }
    }

    var body: some View {
        let groups = self.groups
        let count = groups.reduce(0) { $0 + $1.records.count }
        let books = Dictionary(store.library.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return ScrollView {
            VStack(spacing: 0) {
                HStack(spacing: 0) {
                    NativePageTitle(title: L("阅读资料"), identifier: "page.materials")
                    Spacer(minLength: 0)
                    Text(count == 1 ? L("1 条") : L("{0} 条", count))
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
                        .lineLimit(1).padding(.trailing, 10)
                    Menu {
                        Button(L("导出 Markdown")) { export(true) }; Button(L("导出纯文本")) { export(false) }
                    } label: { Text(L("导出")).font(NativeTheme.font(14)).frame(width: 44, height: 44) }
                        .disabled(exporting || count == 0).accessibilityLabel(L("导出阅读资料"))
                }.modifier(NativePageMasthead())
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        filterChip(L("全部"), tag: "all")
                        filterChip(L("划线"), tag: "highlight")
                        filterChip(L("笔记"), tag: "note")
                        filterChip(L("书签"), tag: "bookmark")
                        filterChip(L("读后思考"), tag: "reflection")
                    }
                    .padding(.horizontal, 12)
                }
                .padding(.top, 8).padding(.bottom, 8)
                .onChange(of: filter) { _ in openedEntryId = nil }
                LazyVStack(alignment: .leading, spacing: 0) {
                    if loading { ProgressView(L("正在读取阅读资料…")).frame(maxWidth: .infinity).padding(.top, 96) }
                    if !loading && count == 0 {
                        VStack(spacing: 6) {
                            Image(systemName: "note.text").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(mutedText)
                            Text(L("还没有阅读资料")).font(NativeTheme.font(17)).padding(.top, 8)
                            Text(L("书签、划线、笔记与读后思考会汇集在这里")).font(NativeTheme.font(13)).foregroundStyle(mutedText)
                        }.frame(maxWidth: .infinity).padding(.top, 96)
                    }
                    ForEach(groups) { group in
                        let id = group.id
                        let records = group.records
                        let book = books[id]
                        let bookTitle = book?.title ?? records.first?.bookTitle ?? L("未知书名")
                        let coverKey = "materials:\(id)"
                        Button {
                            if let book {
                                store.perform {
                                    try await store.openReader(book, sourceKey: coverKey, source: .materials)
                                }
                            } else {
                                store.showToast(L("这本书已不在书库中"))
                            }
                        } label: {
                            HStack(spacing: 14) {
                                if let book {
                                    BookCover(book: book, width: 48, height: 68)
                                        .trackBookOpenFrame(key: coverKey)
                                        .opacity(store.isBookCoverHidden(forKey: coverKey) ? 0 : 1)
                                } else {
                                    Image(systemName: "book").font(.system(size: 28, weight: .ultraLight)).frame(width: 48, height: 68).foregroundStyle(mutedText)
                                }
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(bookTitle).font(NativeTheme.font(15)).lineLimit(2)
                                    Text(records.count == 1 ? L("1 条资料") : L("{0} 条资料", records.count))
                                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText)
                                }.frame(maxWidth: .infinity, alignment: .leading)
                            }.padding(.vertical, 12).contentShape(Rectangle())
                        }.id(id).buttonStyle(.plain).disabled(store.openingReader).accessibilityIdentifier("materials.book.\(id)")

                        VStack(spacing: 6) {
                            ForEach(records) { item in
                                materialEntry(item, book: book, coverKey: coverKey)
                            }
                        }
                        .padding(.bottom, 14)
                    }
                }.padding(.horizontal, NativeAdaptiveLayout.pageInset).padding(.top, 4).padding(.bottom, 28)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .contain).accessibilityIdentifier("materials.content")
        }.refreshable {
            openedEntryId = nil
            _ = await load()
        }
            .background(NativeAmbientBackground()).navigationBarHidden(true)
            .overlay(alignment: .top) { NativeStatusBarGlass() }
            .task(id: store.uiLanguage) { _ = await load() }
            .onChange(of: store.activeTab) { tab in
                openedEntryId = nil
                if tab == 1 { Task { _ = await load() } }
            }
            .onChange(of: store.openingReader) { opening in
                if opening { openedEntryId = nil }
            }
            .onChange(of: store.readerBook == nil) { closed in
                guard closed, store.activeTab == 1, store.closingBookTransition == nil else { return }
                Task { _ = await load() }
            }
            .onChange(of: store.closingBookTransition == nil) { settled in
                guard settled, store.readerBook == nil, store.activeTab == 1 else { return }
                Task { _ = await load() }
            }
            .sheet(isPresented: Binding(
                get: { editingReflection != nil },
                set: { if !$0 { editingReflection = nil } }
            )) {
                if let target = editingReflection {
                    NativeBookReflectionEditor(store: store, bookId: target.bookId, bookTitle: target.title) { _ in
                        Task { _ = await load() }
                    }
                }
            }
    }

    private func filterChip(_ title: String, tag: String) -> some View {
        let selected = filter == tag
        return Button {
            filter = tag
        } label: {
            Text(title)
                .font(NativeTheme.font(13.5, relativeTo: .subheadline).weight(selected ? .medium : .regular))
                .foregroundStyle(selected ? NativeTheme.ink : mutedText)
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
                .background(selected ? NativeTheme.sunk : Color.clear, in: Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("materials.filter.\(tag)")
    }

    private func materialEntry(_ item: MaterialRecord, book: LibraryBook?, coverKey: String) -> some View {
        NativeSwipeMaterialCard(
            item: item,
            openedEntryId: $openedEntryId,
            disabled: store.openingReader,
            onOpen: {
                if item.kind == "reflection" {
                    editingReflection = (item.bookId, book?.title ?? item.bookTitle)
                } else if let book {
                    store.perform {
                        try await store.openReader(
                            book,
                            location: item.location.foundationValue as? [String: Any],
                            sourceKey: coverKey,
                            source: .materials
                        )
                    }
                } else { store.showToast(L("这本书已不在书库中")) }
            },
            onDelete: { delete(item) }
        ) {
            HStack(alignment: .top, spacing: 10) {
                if let accent = NativeHighlightPalette.accentColor(style: item.underlineStyle, hex: item.highlightColor) {
                    RoundedRectangle(cornerRadius: 1.5)
                        .fill(accent)
                        .frame(width: 3)
                        .padding(.vertical, 2)
                } else {
                    Image(systemName: item.kind == "bookmark" ? "bookmark" : item.kind == "reflection" ? "square.and.pencil" : "note.text")
                        .font(.system(size: 17, weight: .regular)).foregroundStyle(mutedText).frame(width: 18).padding(.top, 1)
                }
                VStack(alignment: .leading, spacing: 5) {
                    Text("\(item.kindLabel) · \(item.locationText)").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(mutedText).lineLimit(1)
                    if item.kind == "reflection" {
                        let preview = NativeBookReflectionPolicy.plainPreviewText(from: item.note)
                        Text(preview.isEmpty ? L("（图片内容）") : preview)
                            .font(NativeTheme.font(14)).lineSpacing(4).lineLimit(3)
                    } else {
                        Text(item.quote).font(NativeTheme.font(14)).lineSpacing(4)
                        if !item.note.isEmpty { Text(item.note).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary).lineSpacing(4) }
                    }
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.horizontal, 12).padding(.vertical, 11).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
        }
    }

    private func delete(_ item: MaterialRecord) {
        if openedEntryId == item.id { openedEntryId = nil }
        pendingDeletionIds.insert(item.id)
        withAnimation(.easeInOut(duration: 0.22)) {
            entries.removeAll { $0.id == item.id }
        }
        store.perform {
            defer { pendingDeletionIds.remove(item.id) }
            do {
                try await store.command("materials.delete", ["id": item.id, "kind": item.kind])
            } catch {
                _ = await load()
                throw error
            }
        }
    }

    @discardableResult
    private func load() async -> Bool {
        defer { loading = false }
        do {
            let loaded: [MaterialRecord] = try await store.request("materials.get")
            let visible = loaded.filter { !pendingDeletionIds.contains($0.id) }
            guard visible != entries else { return false }
            entries = visible
            return true
        }
        catch {
            if !Task.isCancelled { store.failure = error.localizedDescription }
            return false
        }
    }
    private func export(_ markdown: Bool) {
        exporting = true; let ids = entries.filter(matches).map(\.id)
        store.perform { defer { exporting = false }; try await store.command("materials.export", ["ids": ids, "markdown": markdown]) }
    }
}

private struct NativeSwipeMaterialCard<Label: View>: View {
    let item: MaterialRecord
    @Binding var openedEntryId: String?
    let disabled: Bool
    let onOpen: () -> Void
    let onDelete: () -> Void
    @ViewBuilder let label: () -> Label

    @State private var dragOffset: CGFloat = 0
    @State private var dragStartOffset: CGFloat = 0
    @State private var isDragging = false
    @State private var isDeleting = false
    @State private var fullSwipeArmed = false
    @State private var cardWidth: CGFloat = 0

    var body: some View {
        Button {
            handleCardTap()
        } label: {
            label()
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(disabled || isDeleting)
        .accessibilityIdentifier("materials.entry.\(item.id)")
        .accessibilityAction(named: L("删除")) { performDelete() }
        .overlay {
            NativeMaterialSwipeOverlay(
                enabled: !disabled && !isDeleting,
                isOpen: dragOffset < -NativeSwipeDeletePolicy.actionWidth * 0.5,
                onTap: { handleCardTap() },
                onDragStart: {
                    isDragging = true
                    dragStartOffset = dragOffset
                    if openedEntryId != item.id { openedEntryId = item.id }
                },
                onDragChange: { translationX in
                    let next = NativeSwipeDeletePolicy.clampedDragOffset(
                        startOffset: dragStartOffset,
                        translationX: translationX,
                        cardWidth: cardWidth
                    )
                    let armed = NativeSwipeDeletePolicy.isPastFullSwipeThreshold(offset: next, cardWidth: cardWidth)
                    if armed != fullSwipeArmed {
                        UIImpactFeedbackGenerator(style: armed ? .medium : .light).impactOccurred()
                        fullSwipeArmed = armed
                    }
                    dragOffset = next
                },
                onDragEnd: { velocityX, cancelled in
                    isDragging = false
                    if cancelled {
                        let target: CGFloat = dragStartOffset < -NativeSwipeDeletePolicy.actionWidth * 0.5
                            ? -NativeSwipeDeletePolicy.actionWidth : 0
                        fullSwipeArmed = false
                        if target == 0, openedEntryId == item.id { openedEntryId = nil }
                        withAnimation(.interactiveSpring(response: 0.28, dampingFraction: 0.86)) { dragOffset = target }
                        return
                    }
                    switch NativeSwipeDeletePolicy.decideReleaseOutcome(
                        startOffset: dragStartOffset,
                        currentOffset: dragOffset,
                        velocityX: velocityX,
                        cardWidth: cardWidth
                    ) {
                    case .delete:
                        performDelete()
                    case .open:
                        openedEntryId = item.id
                        fullSwipeArmed = false
                        withAnimation(.interactiveSpring(response: 0.28, dampingFraction: 0.86)) {
                            dragOffset = -NativeSwipeDeletePolicy.actionWidth
                        }
                    case .closed:
                        if openedEntryId == item.id { openedEntryId = nil }
                        fullSwipeArmed = false
                        withAnimation(.interactiveSpring(response: 0.28, dampingFraction: 0.86)) {
                            dragOffset = 0
                        }
                    }
                }
            )
        }
        .offset(x: dragOffset)
        .background {
            GeometryReader { proxy in
                Color.clear
                    .onAppear { cardWidth = proxy.size.width }
                    .onChange(of: proxy.size.width) { cardWidth = $0 }
            }
        }
        .background(alignment: .trailing) {
            if dragOffset < 0 || isDeleting {
                ZStack(alignment: .trailing) {
                    Color(uiColor: .systemRed)
                    Button(role: .destructive) {
                        performDelete()
                    } label: {
                        VStack(spacing: 4) {
                            Image(systemName: "trash")
                                .font(.system(size: 16, weight: .semibold))
                            Text(L("删除"))
                                .font(NativeTheme.font(12, relativeTo: .caption))
                        }
                        .foregroundStyle(.white)
                        .frame(
                            width: fullSwipeArmed ? max(NativeSwipeDeletePolicy.actionWidth, -dragOffset) : NativeSwipeDeletePolicy.actionWidth,
                            alignment: fullSwipeArmed ? .leading : .center
                        )
                        .padding(.leading, fullSwipeArmed ? 24 : 0)
                        .frame(maxHeight: .infinity)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .disabled(isDeleting)
                    .accessibilityLabel(L("删除"))
                    .accessibilityIdentifier("materials.delete.\(item.id)")
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .onChange(of: openedEntryId) { activeId in
            guard activeId != item.id, !isDragging, !isDeleting, dragOffset != 0 else { return }
            fullSwipeArmed = false
            withAnimation(.interactiveSpring(response: 0.28, dampingFraction: 0.86)) {
                dragOffset = 0
            }
        }
    }

    private func handleCardTap() {
        guard !isDeleting else { return }
        if dragOffset < 0 || openedEntryId != nil {
            fullSwipeArmed = false
            openedEntryId = nil
            withAnimation(.interactiveSpring(response: 0.28, dampingFraction: 0.86)) {
                dragOffset = 0
            }
            return
        }
        onOpen()
    }

    private func performDelete() {
        guard !isDeleting else { return }
        isDeleting = true
        fullSwipeArmed = true
        let travel = -max(cardWidth, 400)
        withAnimation(.easeIn(duration: 0.16)) {
            dragOffset = travel
        }
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 160_000_000)
            onDelete()
        }
    }
}

private struct NativeMaterialSwipeOverlay: UIViewRepresentable {
    let enabled: Bool
    let isOpen: Bool
    let onTap: () -> Void
    let onDragStart: () -> Void
    let onDragChange: (CGFloat) -> Void
    let onDragEnd: (CGFloat, Bool) -> Void

    func makeUIView(context: Context) -> NativeMaterialSwipeView { NativeMaterialSwipeView() }

    func updateUIView(_ view: NativeMaterialSwipeView, context: Context) {
        view.isEnabled = enabled
        view.isOpen = isOpen
        view.onTap = onTap
        view.onDragStart = onDragStart
        view.onDragChange = onDragChange
        view.onDragEnd = onDragEnd
    }
}

private final class NativeMaterialSwipeView: UIControl, UIGestureRecognizerDelegate {
    var isOpen = false
    var onTap: (() -> Void)?
    var onDragStart: (() -> Void)?
    var onDragChange: ((CGFloat) -> Void)?
    var onDragEnd: ((CGFloat, Bool) -> Void)?
    private lazy var pan = UIPanGestureRecognizer(target: self, action: #selector(handlePan(_:)))

    override init(frame: CGRect) {
        super.init(frame: frame)
        // Keep the SwiftUI Button as the sole accessibility element for the entry.
        isAccessibilityElement = false
        backgroundColor = .clear
        addTarget(self, action: #selector(handleTap), for: .touchUpInside)
        pan.delegate = self
        pan.cancelsTouchesInView = true
        addGestureRecognizer(pan)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    @objc private func handleTap() {
        onTap?()
    }

    @objc private func handlePan(_ gesture: UIPanGestureRecognizer) {
        let space = window ?? superview ?? self
        switch gesture.state {
        case .began:
            onDragStart?()
        case .changed:
            onDragChange?(gesture.translation(in: space).x)
        case .ended:
            onDragEnd?(gesture.velocity(in: space).x, false)
        case .cancelled, .failed:
            onDragEnd?(0, true)
        default:
            break
        }
    }

    override func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === pan else { return true }
        guard isEnabled else { return false }
        let space = window ?? superview ?? self
        let velocity = pan.velocity(in: space)
        let translation = pan.translation(in: space)
        let dx = abs(velocity.x) > 1 || abs(velocity.y) > 1 ? velocity.x : translation.x
        let dy = abs(velocity.x) > 1 || abs(velocity.y) > 1 ? velocity.y : translation.y
        guard abs(dx) > abs(dy) * 1.2 else { return false }
        return isOpen || dx < 0
    }

    func gestureRecognizer(
        _ gestureRecognizer: UIGestureRecognizer,
        shouldRecognizeSimultaneouslyWith otherGestureRecognizer: UIGestureRecognizer
    ) -> Bool {
        false
    }
}

struct NativeReadingDashboard: View {
    @ObservedObject var store: NativeStore
    @State private var value: DashboardSnapshot?
    @State private var period = 0
    @State private var picked: String?
    @State private var allMedals = false
    private var dashboard: ReadingDashboard? { value?.dashboard }
    private var mutedText: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    private var secondaryText: Color { store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary }

    var body: some View {
        GeometryReader { geometry in
            let width = NativeAdaptiveLayout.pageWidth(viewportWidth: geometry.size.width)
            ScrollView {
            VStack(spacing: 0) {
                HStack(spacing: 0) {
                    NativePageTitle(title: L("阅读数据"), identifier: "page.dashboard")
                    Spacer(minLength: 0)
                }.modifier(NativePageMasthead())
            VStack(alignment: .leading, spacing: 28) {
                if let value {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(L("累计阅读")).font(NativeTheme.font(13)).foregroundStyle(mutedText)
                        HStack(alignment: .firstTextBaseline, spacing: 6) {
                            Text(durationValue(value.dashboard.totalSeconds)).font(NativeTheme.font(48, relativeTo: .largeTitle))
                                .lineLimit(1).minimumScaleFactor(0.6)
                            Text(value.dashboard.totalSeconds < 3600 ? L("分钟") : L("小时")).font(NativeTheme.font(16)).foregroundStyle(secondaryText)
                        }
                        if let listening = value.dashboard.listeningSeconds, listening > 0 {
                            Text(L("连续阅读 {0} 天 · 含听书 {1}", value.streak, readingDuration(listening)))
                                .font(NativeTheme.font(13)).foregroundStyle(mutedText)
                        } else {
                            Text(L("连续阅读 {0} 天", value.streak)).font(NativeTheme.font(13)).foregroundStyle(mutedText)
                        }
                    }
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10),
                        count: NativeAdaptiveLayout.dashboardColumns(pageWidth: width)), spacing: 10) {
                        statistic(L("已开始"), L("{0} 本", value.dashboard.booksStarted), detail: String(format: L("平均进度 %.1f%%"), value.dashboard.averageProgress))
                        statistic(L("已读完"), L("{0} 本", value.dashboard.booksFinished), detail: L("共 {0} 本书", value.dashboard.books.count))
                        statistic(L("书签"), "\(value.dashboard.bookmarkCount)")
                        statistic(L("划线与笔记"), "\(value.dashboard.annotationCount)")
                    }
                    dailyChart(value)
                    Text(L("最近在读")).font(NativeTheme.font(16, relativeTo: .headline))
                    if value.dashboard.recentBooks.isEmpty { Text(L("还没有阅读记录")).foregroundStyle(secondaryText) }
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(alignment: .top, spacing: 14) {
                            ForEach(value.dashboard.recentBooks.prefix(10)) { item in
                                if let book = store.library.first(where: { $0.id == item.bookId }) {
                                    let coverKey = "dashboardRecent:\(book.id)"
                                    Button {
                                        let frame = NativeBookCoverFrames.get(coverKey)
                                        store.perform {
                                            try await store.openReader(book, sourceFrame: frame, sourceKey: coverKey, source: .dashboard)
                                        }
                                    } label: {
                                        VStack(alignment: .leading, spacing: 6) {
                                            BookCover(book: book, width: 96, height: 138)
                                                .trackBookOpenFrame(key: coverKey)
                                                .opacity(store.isBookCoverHidden(forKey: coverKey) ? 0 : 1)
                                            Text(book.title).font(NativeTheme.font(13)).lineLimit(1)
                                            HStack(spacing: 6) {
                                                NativeThinProgress(value: item.progressPercent)
                                                Text("\(item.progressPercent, specifier: "%.0f")%").font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(mutedText)
                                            }
                                        }.frame(width: 96).contentShape(Rectangle())
                                    }.buttonStyle(.plain).disabled(store.openingReader)
                                        .accessibilityIdentifier("dashboard.recent.\(book.id)")
                                }
                            }
                        }.padding(.vertical, 4)
                    }
                    Text(L("读得最久")).font(NativeTheme.font(16, relativeTo: .headline))
                    VStack(spacing: 16) {
                        ForEach(value.dashboard.mostReadBooks.filter { $0.cumulativeSeconds > 0 }.prefix(5)) { item in dashboardBook(item, detail: readingDuration(item.cumulativeSeconds)) }
                    }
                    progressDistribution(value.dashboard.books)
                    HStack { Text(L("阅读勋章")).font(NativeTheme.font(17, relativeTo: .headline)); Spacer(); Button(allMedals ? L("已解锁") : L("全部")) { allMedals.toggle() }.font(NativeTheme.font(12, relativeTo: .caption)) }
                    Text(L("已解锁 {0} / {1}", value.medals.filter(\.isUnlocked).count, value.medals.count)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(secondaryText)
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 100))], spacing: 18) {
                        ForEach(value.medals.filter { allMedals || $0.isUnlocked }) { medal in
                            Button { store.showToast(medal.rule + (medal.isUnlocked ? "" : String(format: "（%.0f%%）", medal.fraction * 100))) } label: {
                                VStack(spacing: 9) {
                                    NativeMedalEmblem(icon: medal.icon, fraction: medal.fraction)
                                        .frame(width: 50, height: 50).accessibilityHidden(true)
                                    Text(medal.name).font(NativeTheme.font(12, relativeTo: .caption)).multilineTextAlignment(.center)
                                }.frame(maxWidth: .infinity)
                            }.buttonStyle(.plain).accessibilityIdentifier("dashboard.medal.\(medal.name)")
                        }
                    }
                } else { ProgressView(L("正在读取阅读数据…")).frame(maxWidth: .infinity).padding(.top, 70) }
            }.padding(.horizontal, NativeAdaptiveLayout.pageInset).padding(.top, 16).padding(.bottom, 36)
            }
            .frame(width: width)
            .accessibilityElement(children: .contain).accessibilityIdentifier("dashboard.content")
            .frame(maxWidth: .infinity)
            }.refreshable { await load() }
                .background(NativeAmbientBackground()).navigationBarHidden(true)
                .overlay(alignment: .top) { NativeStatusBarGlass() }
                .task(id: "\(period):\(store.uiLanguage)") { await load() }
                .onChange(of: store.activeTab) { tab in
                    if tab == 2 { Task { await load() } }
                }
                .onChange(of: store.readerBook == nil) { closed in
                    guard closed, store.activeTab == 2 else { return }
                    Task {
                        try? await Task.sleep(nanoseconds: UInt64((NativeBookOpenTransitionPolicy.closeToCoverCleanupDelay + 0.08) * 1_000_000_000))
                        guard store.readerBook == nil else { return }
                        await load()
                    }
                }
        }
    }
    private func durationValue(_ seconds: Int64) -> String {
        String(format: "%.1f", Double(seconds) / (seconds < 3600 ? 60 : 3600))
    }
    private func statistic(_ name: String, _ value: String, detail: String = "") -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(name).font(NativeTheme.font(12.5, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            Text(value).font(NativeTheme.font(24, relativeTo: .title2)).lineLimit(1).minimumScaleFactor(0.7)
            if !detail.isEmpty { Text(detail).font(NativeTheme.font(11.5, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted) }
        }.frame(maxWidth: .infinity, minHeight: 70, alignment: .topLeading).padding(16)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
    private func dailyChart(_ snapshot: DashboardSnapshot) -> some View {
        let values = snapshot.buckets
        let maximum = max(1, values.map(\.activeSeconds).max() ?? 1)
        let selected = values.first { $0.id == picked } ?? values.first { $0.contains(snapshot.today) }
        let title = snapshot.period == 0 ? L("本周阅读时长") : snapshot.period == 1 ? L("本月阅读时长") : L("今年阅读时长")
        let detail = (selected?.startDate ?? "") > snapshot.today ? L("尚未到来") : readingDuration(selected?.activeSeconds ?? 0)
        return VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 10) {
                Text(title).font(NativeTheme.font(16, relativeTo: .headline))
                Spacer(minLength: 0)
                Picker(L("阅读周期"), selection: $period) { Text(L("周")).tag(0); Text(L("月")).tag(1); Text(L("年")).tag(2) }
                    .pickerStyle(.segmented)
                    .frame(width: store.uiLanguage == "en-US" ? 160 : 120)
                    .accessibilityIdentifier("dashboard.period")
            }
            Text("\(bucketLabel(selected, yearly: snapshot.period == 2)) · \(detail)")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            HStack(alignment: .bottom, spacing: 5) {
                ForEach(values) { day in
                    Button { picked = day.id } label: {
                        VStack { Spacer(minLength: 0); RoundedRectangle(cornerRadius: 4).fill(day.activeSeconds > 0 ? NativeTheme.ink : NativeTheme.strong)
                            .frame(maxWidth: 16).frame(height: max(3, 124 * Double(day.activeSeconds) / Double(maximum))) }
                            .frame(maxWidth: .infinity).frame(height: 124).contentShape(Rectangle())
                    }.buttonStyle(.plain).accessibilityLabel("\(bucketLabel(day, yearly: snapshot.period == 2))，\(readingDuration(day.activeSeconds))")
                }
            }
            HStack { Text(values.first?.startDate ?? ""); Spacer(); Text(values.last?.endDate ?? "") }.font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.secondary)
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
    private func bucketLabel(_ bucket: ReadingPeriodBucket?, yearly: Bool) -> String {
        guard let bucket else { return "" }
        return yearly ? String(bucket.startDate.prefix(7)) : bucket.startDate
    }
    @ViewBuilder private func dashboardBook(_ item: DashboardBook, detail: String) -> some View {
        if let book = store.library.first(where: { $0.id == item.bookId }) {
            let coverKey = "dashboardMostRead:\(book.id)"
            Button {
                let frame = NativeBookCoverFrames.get(coverKey)
                store.perform {
                    try await store.openReader(book, sourceFrame: frame, sourceKey: coverKey, source: .dashboard)
                }
            } label: {
                HStack(spacing: 14) {
                    BookCover(book: book, width: 38, height: 54)
                        .trackBookOpenFrame(key: coverKey)
                        .opacity(store.isBookCoverHidden(forKey: coverKey) ? 0 : 1)
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            Text(book.title).font(NativeTheme.font(14)).lineLimit(1); Spacer()
                            if let listening = item.listeningSeconds, listening > 0 {
                                HStack(spacing: 4) {
                                    Image(systemName: "headphones")
                                        .font(.system(size: 11, weight: .regular))
                                    Text(detail)
                                }
                                .font(NativeTheme.font(12.5, relativeTo: .caption))
                                .foregroundStyle(mutedText)
                                .accessibilityLabel(L("{0}，含听书 {1}", detail, readingDuration(listening)))
                            } else {
                                Text(detail).font(NativeTheme.font(12.5, relativeTo: .caption)).foregroundStyle(mutedText)
                            }
                        }
                        NativeThinProgress(value: Double(item.cumulativeSeconds), total: Double(dashboard?.mostReadBooks.map(\.cumulativeSeconds).max() ?? 1), height: 4)
                    }
                }.contentShape(Rectangle())
            }.buttonStyle(.plain).disabled(store.openingReader)
                .accessibilityIdentifier("dashboard.mostRead.\(book.id)")
        } else { VStack(alignment: .leading, spacing: 8) { Text(item.title + L("（已移出书库）")); Text(detail).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(secondaryText) } }
    }
    private func progressDistribution(_ books: [DashboardBook]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("进度分布")).font(NativeTheme.font(16, relativeTo: .headline))
            Text(L("平均 {0}% · 共 {1} 本", String(format: "%.0f", locale: NativeLocalization.locale, dashboard?.averageProgress ?? 0), books.count)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            ForEach(0..<5) { index in
                let count = books.filter { ($0.progressPercent >= 99.5 ? 4 : min(3, Int($0.progressPercent / 25))) == index }.count
                HStack { Text(index == 4 ? L("已读完") : "\(index * 25)–\(index * 25 + 24)%").frame(width: 70, alignment: .leading)
                    NativeThinProgress(value: Double(count), total: Double(max(1, books.count)), height: 4); Text(L("{0} 本", count)).frame(width: 40, alignment: .trailing) }.font(NativeTheme.font(12, relativeTo: .caption))
            }
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
    private func load() async {
        do {
            let result: DashboardSnapshot = try await store.request("dashboard.get", ["period": period])
            if !Task.isCancelled {
                if !result.buckets.contains(where: { $0.id == picked }) { picked = nil }
                value = result
            }
        }
        catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
    }
}

/// Draw the same catalog artwork and clockwise progress reveal as the desktop.
private struct NativeMedalEmblem: View {
    let icon: String
    let fraction: Double

    var body: some View {
        let artwork = NativeMedalPath.parse(icon)
        let progress = min(1, max(0, fraction.isFinite ? fraction : 0))
        ZStack {
            if progress < 1 {
                layer(artwork, color: NativeTheme.secondary.opacity(0.35))
            }
            if progress >= 1 {
                layer(artwork, color: NativeTheme.ink)
            } else if progress > 0 {
                layer(artwork, color: NativeTheme.ink)
                    .clipShape(NativeMedalReveal(fraction: progress))
            }
        }
    }

    private func layer(_ artwork: CGPath?, color: Color) -> some View {
        GeometryReader { geometry in
            let size = min(geometry.size.width, geometry.size.height)
            ZStack {
                // Keep the ring inside the bounds so partial-progress clipping never shaves cardinal edges.
                Circle().strokeBorder(color, lineWidth: size * 1.45 / 42)
                if let artwork {
                    NativeMedalGlyph(artwork: Path(artwork))
                        .stroke(color, style: StrokeStyle(lineWidth: size * 1.35 / 42, lineCap: .round, lineJoin: .round))
                }
            }.frame(width: geometry.size.width, height: geometry.size.height)
        }
    }
}

/// Fit the actual path bounds, not its original coordinate canvas, in the badge.
private struct NativeMedalGlyph: Shape {
    let artwork: Path
    func path(in rect: CGRect) -> Path {
        let bounds = artwork.cgPath.boundingBoxOfPath
        guard !bounds.isNull && !bounds.isEmpty else { return Path() }
        let scale = min(rect.width, rect.height) * 20 / 42 / max(bounds.width, bounds.height)
        let transform = CGAffineTransform(a: scale, b: 0, c: 0, d: scale,
                                          tx: rect.midX - bounds.midX * scale,
                                          ty: rect.midY - bounds.midY * scale)
        return artwork.applying(transform)
    }
}

private struct NativeMedalReveal: Shape {
    let fraction: Double
    func path(in rect: CGRect) -> Path {
        guard fraction < 1 else { return Path(rect) }
        guard fraction > 0 else { return Path() }
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let radius = hypot(rect.width, rect.height) / 2 + 2
        var path = Path()
        path.move(to: center)
        path.addLine(to: CGPoint(x: center.x, y: center.y - radius))
        path.addArc(center: center, radius: radius, startAngle: .degrees(-90),
                    endAngle: .degrees(-90 + 360 * fraction), clockwise: false)
        path.closeSubpath()
        return path
    }
}
