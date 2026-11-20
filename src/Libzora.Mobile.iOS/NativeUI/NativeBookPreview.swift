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

    private init() {}

    func show(_ request: NativeBookPreviewRequest) {
        UIImpactFeedbackGenerator(style: .light).impactOccurred()
        withAnimation(.easeOut(duration: 0.16)) { self.request = request }
    }

    func dismiss() {
        guard request != nil else { return }
        withAnimation(.easeOut(duration: 0.14)) { request = nil }
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
            let halfWidth = (request.isListRow ? request.sourceFrame.width + 24 : request.coverWidth) * scale / 2
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
            let naturalHeight = measuredMenuHeight > 0 ? measuredMenuHeight : CGFloat(request.actions.count) * Self.rowHeight + 10
            let opensBelow = roomBelow >= naturalHeight || (roomAbove < naturalHeight && roomBelow >= roomAbove)
            let menuHeight = min(naturalHeight, opensBelow ? roomBelow : roomAbove)
            let menuCenter = opensBelow ? below + menuHeight / 2 : above - menuHeight / 2
            let menuWidth = min(Self.menuWidth, bounds.width)
            let menuX = min(max(centerPoint.x, bounds.minX + menuWidth / 2), bounds.maxX - menuWidth / 2)

            ZStack(alignment: .topLeading) {
                Color.black.opacity(expanded ? 0.25 : 0)
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
                            .padding(.horizontal, 12)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                            .overlay {
                                RoundedRectangle(cornerRadius: 16, style: .continuous)
                                    .strokeBorder(.primary.opacity(0.10), lineWidth: 0.5)
                            }
                            .shadow(color: .black.opacity(0.12), radius: 14, y: 6)
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
                .scaleEffect(expanded ? 1 : 0.52, anchor: opensBelow ? .top : .bottom)
                .opacity(expanded ? 1 : 0)
                .position(x: menuX, y: menuCenter)
            }
            .onPreferenceChange(NativeBookPreviewHeightKey.self) { height in
                if height > 0 && abs(height - cardHeight) > 0.5 { cardHeight = height }
            }
            .onPreferenceChange(NativeBookPreviewMenuHeightKey.self) { height in
                if height > 0 && abs(height - measuredMenuHeight) > 0.5 { measuredMenuHeight = height }
            }
            .onAppear { withAnimation(.spring(response: 0.36, dampingFraction: 0.68)) { expanded = true } }
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

private struct NativeBookPreviewMenu: View {
    let actions: [NativeBookMenuItem]
    let width: CGFloat
    let height: CGFloat
    let perform: (() -> Void) -> Void

    private var rows: some View {
        VStack(spacing: 0) {
            ForEach(Array(actions.enumerated()), id: \.offset) { index, item in
                Button { perform(item.action) } label: {
                    Text(item.title)
                        .font(NativeTheme.font(16))
                        .foregroundStyle(item.destructive ? Color.red : NativeTheme.ink)
                        .frame(maxWidth: .infinity, minHeight: 46, alignment: .leading)
                        .padding(.horizontal, 24)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier(item.identifier ?? "book.preview.action.\(index)")
            }
        }
        .padding(.vertical, 5)
        .background {
            GeometryReader { rows in
                Color.clear.preference(key: NativeBookPreviewMenuHeightKey.self, value: rows.size.height)
            }
        }
    }

    private var panel: some View {
        ScrollView { rows }
            .frame(width: width, height: height)
            .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
    }

    var body: some View {
        if #available(iOS 26.0, *) {
            panel
                .glassEffect(.regular.interactive(), in: RoundedRectangle(cornerRadius: 26, style: .continuous))
                .shadow(color: .black.opacity(0.10), radius: 18, y: 8)
        } else {
            panel
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
                .shadow(color: .black.opacity(0.18), radius: 24, y: 10)
        }
    }
}
