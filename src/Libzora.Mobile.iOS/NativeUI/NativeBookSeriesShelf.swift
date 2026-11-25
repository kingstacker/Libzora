import SwiftUI
import UIKit

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

struct NativeBookSeriesTile: View {
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
        store.perform { try await store.openReader(book, sourceFrame: frame, sourceKey: key, source: .library) }
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

