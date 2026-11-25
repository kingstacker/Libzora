import SwiftUI
import UIKit

/// A long-press preview owned by the app instead of `UIContextMenu`.
///
/// The system menu always draws its own rounded preview plate, which showed as
/// white arcs around the enlarged cover and shifted the card off its centre.
/// This overlay renders only the book card, scales it about its own centre,
/// and lives above the tab bar in the root view.
struct NativeBookPreviewRequest: Identifiable {
    enum Source { case shelf, seriesStack, seriesVolume }

    let book: LibraryBook
    var source: Source = .shelf
    var isListRow = false
    /// The source card's frame in window coordinates.
    let sourceFrame: CGRect
    let coverWidth: CGFloat
    let muted: Color
    let showRating: Bool
    let showSyncStatus: Bool
    let actions: [NativeBookMenuItem]

    var id: String { book.id }
}

@MainActor
final class NativeBookPreviewCenter: ObservableObject {
    static let shared = NativeBookPreviewCenter()

    @Published private(set) var request: NativeBookPreviewRequest?
    @Published private(set) var isDismissing = false
    private var dismissTask: Task<Void, Never>?

    private init() {}

    func show(_ request: NativeBookPreviewRequest) {
        dismissTask?.cancel()
        dismissTask = nil
        isDismissing = false
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        self.request = request
    }

    func dismiss(immediate: Bool = false) {
        guard let current = request else { return }
        dismissTask?.cancel()
        if immediate {
            isDismissing = false
            request = nil
            return
        }
        guard !isDismissing else { return }
        isDismissing = true
        let dismissingId = current.id
        dismissTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 360_000_000)
            guard !Task.isCancelled, let self, self.request?.id == dismissingId else { return }
            self.request = nil
            self.isDismissing = false
        }
    }
}

struct NativeBookPreviewOverlay: View {
    @Environment(\.locale) private var uiLocale
    let request: NativeBookPreviewRequest
    @ObservedObject var center: NativeBookPreviewCenter
    @State private var expanded = false
    @State private var cardHeight: CGFloat = 0
    @State private var measuredMenuHeight: CGFloat = 0

    private static let scale: CGFloat = 1.1
    private static let gap: CGFloat = 6
    private static let menuWidth: CGFloat = 250
    private static let rowHeight: CGFloat = 46

    var body: some View {
        let _ = uiLocale

        GeometryReader { safeArea in
            preview(in: safeArea.frame(in: .global))
                .ignoresSafeArea()
        }
        .transition(.opacity)
    }

    private func preview(in safeArea: CGRect) -> some View {
        GeometryReader { proxy in
            let origin = proxy.frame(in: .global).origin
            let scale: CGFloat = request.isListRow ? 1.03 : Self.scale
            let sourceX = request.sourceFrame.midX - origin.x
            let halfWidth = (request.isListRow ? request.sourceFrame.width : request.coverWidth) * scale / 2
            let centerX = min(max(sourceX, halfWidth), max(halfWidth, proxy.size.width - halfWidth))
            let centerPoint = CGPoint(x: centerX, y: request.sourceFrame.midY - origin.y)
            // The local size is measured before scaleEffect.
            let defaultHeight = request.isListRow ? max(66, request.sourceFrame.height) : request.coverWidth * 1.45
            let height = (cardHeight > 0 ? cardHeight : defaultHeight) * scale
            let below = centerPoint.y + height / 2 + Self.gap
            let above = centerPoint.y - height / 2 - Self.gap
            let bounds = NativeAdaptiveLayout.popupBounds(in: proxy.size,
                safeArea: safeArea.offsetBy(dx: -origin.x, dy: -origin.y),
                bottomBarInset: NativeTheme.bottomTabBarInset)
            let roomBelow = max(0, bounds.maxY - below)
            let roomAbove = max(0, above - bounds.minY)
            let naturalHeight = measuredMenuHeight > 0 ? measuredMenuHeight : CGFloat(request.actions.count) * Self.rowHeight + 12
            let opensBelow = roomBelow >= naturalHeight || (roomAbove < naturalHeight && roomBelow >= roomAbove)
            let menuHeight = naturalHeight
            let rawMenuCenter = opensBelow ? below + menuHeight / 2 : above - menuHeight / 2
            let minMenuCenter = bounds.minY + menuHeight / 2
            let maxMenuCenter = max(minMenuCenter, bounds.maxY - menuHeight / 2)
            let menuCenter = min(max(rawMenuCenter, minMenuCenter), maxMenuCenter)
            let menuWidth = min(Self.menuWidth, bounds.width)
            let menuX = min(max(centerPoint.x, bounds.minX + menuWidth / 2), bounds.maxX - menuWidth / 2)

            ZStack(alignment: .topLeading) {
                Color.black.opacity(expanded ? 0.14 : 0)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture { center.dismiss() }
                    .accessibilityIdentifier("book.preview.dismiss")
                    .accessibilityLabel(L("关闭预览"))

                Group {
                    if request.isListRow {
                        BookRow(book: request.book, showRating: request.showRating,
                                showSyncStatus: request.showSyncStatus, muted: request.muted,
                                previewing: true)
                            .frame(width: request.sourceFrame.width, alignment: .leading)
                    } else {
                        NativeBookTile(book: request.book, width: request.coverWidth, muted: request.muted,
                                       galleryMode: request.source == .seriesStack,
                                       showRating: request.showRating, showSyncStatus: request.showSyncStatus,
                                       previewing: true)
                            .frame(width: request.coverWidth)
                    }
                }
                    .foregroundStyle(NativeTheme.ink)
                    .background {
                        GeometryReader { card in
                            Color.clear.preference(key: NativeBookPreviewHeightKey.self, value: card.size.height)
                        }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture {}
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier("book.preview.\(request.book.id)")
                    .scaleEffect(expanded ? scale : 1)
                    .position(x: centerPoint.x, y: centerPoint.y)

                NativeBookPreviewMenu(actions: request.actions, width: menuWidth, height: menuHeight) { action in
                    center.dismiss()
                    action()
                }
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("book.preview.menu")
                .scaleEffect(expanded ? 1 : 0.72, anchor: opensBelow ? .top : .bottom)
                .opacity(expanded ? 1 : 0)
                .position(x: menuX, y: menuCenter)
            }
            .onPreferenceChange(NativeBookPreviewHeightKey.self) { height in
                if height > 0 && abs(height - cardHeight) > 0.5 { cardHeight = height }
            }
            .onPreferenceChange(NativeBookPreviewMenuHeightKey.self) { height in
                if height > 0 && abs(height - measuredMenuHeight) > 0.5 { measuredMenuHeight = height }
            }
            .onAppear {
                withAnimation(.spring(response: 0.36, dampingFraction: 0.68)) { expanded = true }
            }
            .onChange(of: center.isDismissing) { dismissing in
                guard dismissing else { return }
                withAnimation(.spring(response: 0.36, dampingFraction: 0.82)) { expanded = false }
            }
        }
    }
}

private struct NativeBookPreviewHeightKey: PreferenceKey {
    static var defaultValue: CGFloat { 0 }
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

private struct NativeBookPreviewMenuHeightKey: PreferenceKey {
    static var defaultValue: CGFloat { 0 }
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

private struct NativeBookPreviewRowFramesKey: PreferenceKey {
    static var defaultValue: [Int: CGRect] { [:] }
    static func reduce(value: inout [Int: CGRect], nextValue: () -> [Int: CGRect]) {
        value.merge(nextValue(), uniquingKeysWith: { _, new in new })
    }
}

private struct NativeBookPreviewMenu: View {
    let actions: [NativeBookMenuItem]
    let width: CGFloat
    let height: CGFloat
    let perform: (() -> Void) -> Void

    @State private var hoveredIndex: Int?
    @State private var rowFrames: [Int: CGRect] = [:]
    @State private var triggered = false
    private let menuSpace = "bookPreviewMenuSpace"

    private func trigger(_ index: Int) {
        guard !triggered, actions.indices.contains(index) else { return }
        triggered = true
        perform(actions[index].action)
    }

    private func hitIndex(at location: CGPoint) -> Int? {
        guard location.x >= 0, location.x <= width, location.y >= 0, location.y <= height else { return nil }
        if let direct = rowFrames.first(where: { location.y >= $0.value.minY && location.y <= $0.value.maxY })?.key {
            return direct
        }
        // Keep the highlight steady across the thin separator gap between rows.
        return rowFrames.min(by: { abs($0.value.midY - location.y) < abs($1.value.midY - location.y) })?.key
    }

    private var rows: some View {
        ZStack(alignment: .top) {
            if let hoveredIndex, let frame = rowFrames[hoveredIndex] {
                Capsule()
                    .fill(.primary.opacity(0.085))
                    .frame(width: max(0, width - 16), height: frame.height)
                    .offset(y: frame.minY)
                    .allowsHitTesting(false)
            }

            VStack(spacing: 0) {
                ForEach(Array(actions.enumerated()), id: \.offset) { index, item in
                    if item.destructive && index > 0 && !actions[index - 1].destructive {
                        Rectangle()
                            .fill(.primary.opacity(0.08))
                            .frame(height: 0.5)
                            .padding(.horizontal, 18)
                            .padding(.vertical, 4)
                    }
                    Button { trigger(index) } label: {
                        HStack(spacing: 12) {
                            Text(item.title)
                                .font(NativeTheme.font(16))
                            Spacer(minLength: 8)
                            if let systemImage = item.systemImage {
                                Image(systemName: systemImage)
                                    .font(.system(size: 16, weight: .regular))
                                    .frame(width: 24, alignment: .center)
                                    .accessibilityHidden(true)
                            }
                        }
                        .foregroundStyle(item.destructive ? Color.red : NativeTheme.ink)
                        .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                        .padding(.horizontal, 20)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .background {
                        GeometryReader { geo in
                            Color.clear.preference(
                                key: NativeBookPreviewRowFramesKey.self,
                                value: [index: geo.frame(in: .named(menuSpace))]
                            )
                        }
                    }
                    .accessibilityIdentifier(item.identifier ?? "book.preview.action.\(index)")
                }
            }
            .padding(.vertical, 8)
        }
        .coordinateSpace(name: menuSpace)
        .onPreferenceChange(NativeBookPreviewRowFramesKey.self) { frames in
            rowFrames = frames
        }
        .background {
            GeometryReader { rows in
                Color.clear.preference(key: NativeBookPreviewMenuHeightKey.self, value: rows.size.height)
            }
        }
        .simultaneousGesture(
            DragGesture(minimumDistance: 0, coordinateSpace: .named(menuSpace))
                .onChanged { value in
                    let next = hitIndex(at: value.location)
                    if next != hoveredIndex {
                        if next != nil {
                            UISelectionFeedbackGenerator().selectionChanged()
                        }
                        withAnimation(.spring(response: 0.22, dampingFraction: 0.86)) {
                            hoveredIndex = next
                        }
                    }
                }
                .onEnded { value in
                    let target = hitIndex(at: value.location)
                    withAnimation(.easeOut(duration: 0.12)) {
                        hoveredIndex = nil
                    }
                    if let target {
                        trigger(target)
                    }
                }
        )
    }

    var body: some View {
        if #available(iOS 26.0, *) {
            let shape = RoundedRectangle(cornerRadius: 30, style: .continuous)
            GlassEffectContainer {
                rows
                    .frame(width: width, height: height, alignment: .top)
                    .clipShape(shape)
                    .glassEffect(.regular.interactive(), in: shape)
            }
        } else {
            let shape = RoundedRectangle(cornerRadius: 22, style: .continuous)
            rows
                .frame(width: width, height: height, alignment: .top)
                .clipShape(shape)
                .background(.regularMaterial, in: shape)
                .shadow(color: .black.opacity(0.18), radius: 24, y: 10)
        }
    }
}
