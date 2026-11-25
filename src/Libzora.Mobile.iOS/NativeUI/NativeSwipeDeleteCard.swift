import SwiftUI
import UIKit

/// Owns one swipe at a time. Destructive work completes before a card can disappear.
struct NativeSwipeDeleteCard<Label: View>: View {
    let itemId: String
    @Binding var openedEntryId: String?
    let disabled: Bool
    let cardIdentifier: String
    let deleteIdentifier: String
    var cornerRadius: CGFloat = 10
    let onOpen: () -> Void
    let onDelete: () async -> Void
    @ViewBuilder let label: () -> Label

    @State private var offset: CGFloat = 0
    @State private var startOffset: CGFloat = 0
    @State private var width: CGFloat = 0
    @State private var dragging = false
    @State private var deleting = false
    @State private var armed = false

    var body: some View {
        Button(action: open) { label().contentShape(Rectangle()) }
            .buttonStyle(.plain)
            .disabled(disabled || deleting)
            .accessibilityIdentifier(cardIdentifier)
            .accessibilityAction(named: L("删除")) { delete() }
            .overlay {
                NativeMaterialSwipeOverlay(enabled: !disabled && !deleting,
                    isOpen: offset < -NativeSwipeDeletePolicy.actionWidth * 0.5,
                    onTap: open,
                    onDragStart: {
                        dragging = true; startOffset = offset; openedEntryId = itemId
                    },
                    onDragChange: { translation in
                        offset = NativeSwipeDeletePolicy.clampedDragOffset(startOffset: startOffset,
                            translationX: translation, cardWidth: width)
                        let next = NativeSwipeDeletePolicy.isPastFullSwipeThreshold(offset: offset, cardWidth: width)
                        if next != armed { UIImpactFeedbackGenerator(style: .light).impactOccurred(); armed = next }
                    },
                    onDragEnd: { velocity, cancelled in
                        dragging = false
                        if cancelled { settle(startOffset < 0); return }
                        switch NativeSwipeDeletePolicy.decideReleaseOutcome(startOffset: startOffset,
                            currentOffset: offset, velocityX: velocity, cardWidth: width) {
                        case .delete: delete()
                        case .open: settle(true)
                        case .closed: settle(false)
                        }
                    })
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
            .offset(x: offset)
            .background(alignment: .trailing) {
                if offset < 0 {
                    Button(role: .destructive, action: delete) {
                        Image(systemName: "trash").font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(.white).frame(width: 42, height: 42)
                            .background(Color(uiColor: .systemRed), in: Circle())
                            .scaleEffect(armed ? 1.1 : 1)
                            .frame(width: max(NativeSwipeDeletePolicy.actionWidth, -offset), alignment: .center)
                            .frame(maxHeight: .infinity).contentShape(Rectangle())
                    }.buttonStyle(.plain).disabled(disabled || deleting)
                        .accessibilityLabel(L("删除")).accessibilityIdentifier(deleteIdentifier)
                }
            }
            .background {
                GeometryReader { geometry in
                    Color.clear.onAppear { width = geometry.size.width }
                        .onChange(of: geometry.size.width) { width = $0 }
                }
            }
            .onChange(of: openedEntryId) { id in
                if id != itemId && !dragging && !deleting { settle(false) }
            }
    }

    private func settle(_ opened: Bool) {
        armed = false
        if opened { openedEntryId = itemId }
        else if openedEntryId == itemId { openedEntryId = nil }
        withAnimation(.interactiveSpring(response: 0.28, dampingFraction: 0.86)) {
            offset = opened ? -NativeSwipeDeletePolicy.actionWidth : 0
        }
    }
    private func open() {
        guard !disabled && !deleting else { return }
        if offset < 0 || openedEntryId != nil { settle(false); openedEntryId = nil }
        else { onOpen() }
    }
    private func delete() {
        guard !disabled && !deleting else { return }
        deleting = true
        settle(true)
        Task { @MainActor in
            // Confirmation and database acknowledgement are owned by the caller.
            await onDelete()
            deleting = false
            settle(false)
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
        view.isEnabled = enabled; view.isOpen = isOpen; view.onTap = onTap
        view.onDragStart = onDragStart; view.onDragChange = onDragChange; view.onDragEnd = onDragEnd
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
        isAccessibilityElement = false; backgroundColor = .clear
        addTarget(self, action: #selector(handleTap), for: .touchUpInside)
        pan.delegate = self; pan.cancelsTouchesInView = true; addGestureRecognizer(pan)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    @objc private func handleTap() { onTap?() }
    @objc private func handlePan(_ gesture: UIPanGestureRecognizer) {
        let space = window ?? superview ?? self
        switch gesture.state {
        case .began: onDragStart?()
        case .changed: onDragChange?(gesture.translation(in: space).x)
        case .ended: onDragEnd?(gesture.velocity(in: space).x, false)
        case .cancelled, .failed: onDragEnd?(0, true)
        default: break
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
        return abs(dx) > abs(dy) * 1.2 && (isOpen || dx < 0)
    }
    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
        shouldRecognizeSimultaneouslyWith otherGestureRecognizer: UIGestureRecognizer) -> Bool { false }
}
