import SwiftUI
import UIKit

/// A long-press preview owned by the app instead of `UIContextMenu`.
///
/// The system menu always draws its own rounded preview plate, which showed as
/// white arcs around the enlarged cover and shifted the card off its centre.
/// This overlay renders only the book card, scales it about its own centre,
/// and lives above the tab bar in the root view.
struct NativeBookPreviewRequest: Identifiable {
    let book: LibraryBook
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
    @State private var cardFrame: CGRect = .zero

    private static let scale: CGFloat = 1.1
    private static let gap: CGFloat = 12
    private static let menuWidth: CGFloat = 250
    private static let rowHeight: CGFloat = 46
    private static let tabBarClearance: CGFloat = 96

    private var menuHeight: CGFloat {
        CGFloat(request.actions.count) * Self.rowHeight + 10
    }

    var body: some View {
        let _ = uiLocale

        GeometryReader { proxy in
            let origin = proxy.frame(in: .global).origin
            let centerPoint = CGPoint(x: request.sourceFrame.midX - origin.x,
                                      y: request.sourceFrame.midY - origin.y)
            let cardHeight = (cardFrame.height > 0 ? cardFrame.height : request.coverWidth * 1.45) * Self.scale
            let below = centerPoint.y + cardHeight / 2 + Self.gap + menuHeight / 2
            let above = centerPoint.y - cardHeight / 2 - Self.gap - menuHeight / 2
            let menuCenter = below + menuHeight / 2 <= proxy.size.height - Self.tabBarClearance
                ? below : max(above, menuHeight / 2 + 12)
            let menuX = min(max(centerPoint.x, Self.menuWidth / 2 + 12),
                            proxy.size.width - Self.menuWidth / 2 - 12)

            ZStack(alignment: .topLeading) {
                Color.black.opacity(expanded ? 0.25 : 0)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture { center.dismiss() }
                    .accessibilityIdentifier("book.preview.dismiss")
                    .accessibilityLabel(L("关闭预览"))

                NativeBookTile(book: request.book, width: request.coverWidth, muted: request.muted,
                               showRating: request.showRating, showSyncStatus: request.showSyncStatus,
                               previewing: true)
                    .frame(width: request.coverWidth)
                    .foregroundStyle(NativeTheme.ink)
                    .background {
                        GeometryReader { card in
                            Color.clear.preference(key: NativeBookPreviewFrameKey.self,
                                                   value: card.frame(in: .named("book.preview")))
                        }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture {}
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier("book.preview.\(request.book.id)")
                    .scaleEffect(expanded ? Self.scale : 1)
                    .position(x: centerPoint.x, y: centerPoint.y)

                NativeBookPreviewMenu(actions: request.actions, width: Self.menuWidth) { action in
                    center.dismiss()
                    action()
                }
                .frame(width: Self.menuWidth)
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("book.preview.menu")
                .position(x: menuX, y: menuCenter)
            }
            .coordinateSpace(name: "book.preview")
            .onPreferenceChange(NativeBookPreviewFrameKey.self) { frame in
                if abs(frame.height - cardFrame.height) > 0.5 { cardFrame = frame }
            }
            .onAppear { withAnimation(.easeOut(duration: 0.18)) { expanded = true } }
        }
        .ignoresSafeArea()
        .transition(.opacity)
    }
}

private struct NativeBookPreviewFrameKey: PreferenceKey {
    static var defaultValue: CGRect { .zero }
    static func reduce(value: inout CGRect, nextValue: () -> CGRect) {
        let next = nextValue()
        if next != .zero { value = next }
    }
}

private struct NativeBookPreviewMenu: View {
    let actions: [NativeBookMenuItem]
    let width: CGFloat
    let perform: (() -> Void) -> Void

    private var panel: some View {
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
        .frame(width: width)
    }

    var body: some View {
        if #available(iOS 26.0, *) {
            panel
                .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 26, style: .continuous))
                .shadow(color: .black.opacity(0.10), radius: 18, y: 8)
        } else {
            panel
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
                .shadow(color: .black.opacity(0.18), radius: 24, y: 10)
        }
    }
}
