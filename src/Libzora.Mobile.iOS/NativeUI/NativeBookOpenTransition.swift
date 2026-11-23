import SwiftUI
import UIKit

/// Coordinates source cover geometry and the Apple Books-style cover expand &
/// 3D spine-flip transition when interactively opening or closing a book.
struct NativeBookOpenTransition: Identifiable, Equatable {
    let id = UUID()
    let book: LibraryBook
    let sourceFrame: CGRect
    let sourceKey: String?
    let isCentered: Bool
    let coverImage: UIImage?
    var pageSnapshot: UIImage? = nil

    static func == (lhs: NativeBookOpenTransition, rhs: NativeBookOpenTransition) -> Bool {
        lhs.id == rhs.id
            && lhs.book == rhs.book
            && lhs.sourceFrame == rhs.sourceFrame
            && lhs.sourceKey == rhs.sourceKey
            && lhs.isCentered == rhs.isCentered
    }
}

@MainActor
enum NativeBookCoverFrames {
    private final class WeakViewBox {
        weak var view: UIView?
        init(_ view: UIView) { self.view = view }
    }

    private static var anchors: [String: [ObjectIdentifier: WeakViewBox]] = [:]

    static func register(_ view: UIView, for key: String) {
        let id = ObjectIdentifier(view)
        anchors[key, default: [:]][id] = WeakViewBox(view)
    }

    static func unregister(_ view: UIView, for key: String) {
        let id = ObjectIdentifier(view)
        anchors[key]?.removeValue(forKey: id)
        if anchors[key]?.isEmpty == true {
            anchors.removeValue(forKey: key)
        }
    }

    /// Reads the live global screen frame directly from the attached UIView so
    /// frames never go stale across `.fullScreenCover` dismissal, programmatic
    /// scrolling, safe-area settlement, or tab switches.
    static func get(_ key: String) -> CGRect? {
        guard var bucket = anchors[key] else { return nil }
        var stale: [ObjectIdentifier] = []
        var resolved: CGRect?

        for (id, box) in bucket {
            guard let view = box.view else {
                stale.append(id)
                continue
            }
            guard let window = view.window,
                  isEffectivelyAttached(view),
                  view.bounds.width > 4,
                  view.bounds.height > 4 else {
                continue
            }
            let frame = view.convert(view.bounds, to: window)
            guard frame.width > 4, frame.height > 4,
                  frame.origin.x.isFinite, frame.origin.y.isFinite else {
                continue
            }
            if frame.intersects(window.bounds) {
                resolved = frame
                break
            }
            if resolved == nil {
                resolved = frame
            }
        }

        if !stale.isEmpty {
            for id in stale { bucket.removeValue(forKey: id) }
            if bucket.isEmpty {
                anchors.removeValue(forKey: key)
            } else {
                anchors[key] = bucket
            }
        }
        return resolved
    }

    private static func isEffectivelyAttached(_ view: UIView) -> Bool {
        var node: UIView? = view
        while let current = node {
            if current.isHidden { return false }
            node = current.superview
        }
        return true
    }
}

private final class NativeBookFrameAnchorView: UIView {
    var registeredKey: String?

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        backgroundColor = .clear
    }

    required init?(coder: NSCoder) { nil }

    @MainActor
    func bind(key: String?) {
        if registeredKey == key {
            if let key { NativeBookCoverFrames.register(self, for: key) }
            return
        }
        if let previous = registeredKey {
            NativeBookCoverFrames.unregister(self, for: previous)
        }
        registeredKey = key
        if let key {
            NativeBookCoverFrames.register(self, for: key)
        }
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if let key = registeredKey, window != nil {
            MainActor.assumeIsolated {
                NativeBookCoverFrames.register(self, for: key)
            }
        }
    }
}

private struct NativeBookFrameAnchor: UIViewRepresentable {
    let key: String

    func makeUIView(context: Context) -> NativeBookFrameAnchorView {
        let view = NativeBookFrameAnchorView()
        view.bind(key: key)
        return view
    }

    func updateUIView(_ uiView: NativeBookFrameAnchorView, context: Context) {
        uiView.bind(key: key)
    }

    static func dismantleUIView(_ uiView: NativeBookFrameAnchorView, coordinator: ()) {
        uiView.bind(key: nil)
    }
}

private struct NativeBookOpenFrameTracker: ViewModifier {
    let key: String?

    func body(content: Content) -> some View {
        content.background {
            if let key {
                NativeBookFrameAnchor(key: key)
            }
        }
    }
}

extension View {
    func trackBookOpenFrame(key: String?) -> some View {
        modifier(NativeBookOpenFrameTracker(key: key))
    }
}

/// Clips a full-screen view to an arbitrary rounded rectangle without adding
/// an offscreen `.mask` pass or changing the child view's structural identity.
private struct NativeBookInsetRoundedRect: Shape {
    var rect: CGRect
    var cornerRadius: CGFloat

    var animatableData: AnimatablePair<AnimatablePair<CGFloat, CGFloat>, AnimatablePair<AnimatablePair<CGFloat, CGFloat>, CGFloat>> {
        get {
            AnimatablePair(
                AnimatablePair(rect.origin.x, rect.origin.y),
                AnimatablePair(AnimatablePair(rect.size.width, rect.size.height), cornerRadius)
            )
        }
        set {
            rect = CGRect(
                x: newValue.first.first,
                y: newValue.first.second,
                width: newValue.second.first.first,
                height: newValue.second.first.second
            )
            cornerRadius = newValue.second.second
        }
    }

    func path(in bounds: CGRect) -> Path {
        if rect.isEmpty { return Path(bounds) }
        return Path(roundedRect: rect, cornerRadius: cornerRadius, style: .continuous)
    }
}

/// GPU-only AnimatableModifier that scales, clips, shadows and positions the
/// live `NativeReaderPage` body during the cover-open animation while keeping
/// the underlying Avalonia Skia surface at constant full-screen bounds and
/// unchanged SwiftUI view identity (zero reflow, zero remount flash).
struct NativeBookOpenPageTransform: AnimatableModifier {
    let active: Bool
    let sourceFrame: CGRect
    let containerFrame: CGRect
    let isCentered: Bool
    let colorScheme: ColorScheme
    var progress: Double

    var animatableData: Double {
        get { progress }
        set { progress = newValue }
    }

    func body(content: Content) -> some View {
        let targetW = max(1, containerFrame.width)
        let targetH = max(1, containerFrame.height)
        let t = active ? min(max(progress, 0), 1) : 1.0
        let localSource = CGRect(
            x: sourceFrame.minX - containerFrame.minX,
            y: sourceFrame.minY - containerFrame.minY,
            width: max(28, sourceFrame.width),
            height: max(38, sourceFrame.height)
        )
        let curW = active ? lerp(localSource.width, targetW, t) : targetW
        let curH = active ? lerp(localSource.height, targetH, t) : targetH
        let cx = active ? lerp(localSource.midX, targetW / 2, t) : targetW / 2
        let cy = active ? lerp(localSource.midY, targetH / 2, t) : targetH / 2

        let rawReveal = (active && isCentered) ? min(1.0, t / 0.18) : 1.0
        let smoothReveal = rawReveal * rawReveal * (3.0 - 2.0 * rawReveal)
        let cardScale = (active && isCentered) ? lerp(0.88, 1.0, smoothReveal) : 1.0
        let cardOpacity = (active && isCentered) ? smoothReveal : 1.0
        let cornerRadius = active ? lerp(6.0, 0.0, t) : 0.0
        let flipT = active ? min(max((t - 0.06) / 0.86, 0), 1) : 1.0
        let pageShadowOpacity = active ? 0.22 * (1.0 - flipT) : 0.0
        let flightArc = active ? sin(t * .pi) : 0.0

        let restingShadowOpacity = colorScheme == .dark ? 0.0 : 0.12
        let shadowOpacity = active ? (restingShadowOpacity * (1.0 - t) + 0.22 * flightArc) : 0.0
        let shadowRadius = active ? (5.0 * (1.0 - t) + 20.0 * flightArc) : 0.0
        let shadowY = active ? (2.0 * (1.0 - t) + 10.0 * flightArc) : 0.0

        let visW = curW * cardScale
        let visH = curH * cardScale
        // Keep aspect ratio locked (uniform scale) so body text never looks squashed during expand.
        let uniformScale = active ? max(0.01, visW / targetW) : 1.0
        let pageRevealOpacity = active ? min(1.0, max(0.0, t / 0.14)) : 1.0
        let clipRect = active
            ? CGRect(x: (targetW - visW) * 0.5, y: (targetH - visH) * 0.5, width: visW, height: visH)
            : .zero

        content
            .overlay(alignment: .leading) {
                LinearGradient(
                    colors: [
                        Color.black.opacity(pageShadowOpacity),
                        Color.black.opacity(pageShadowOpacity * 0.35),
                        Color.clear
                    ],
                    startPoint: .leading,
                    endPoint: UnitPoint(x: max(0.18, 0.72 * (1.0 - flipT * 0.65)), y: 0.5)
                )
                .allowsHitTesting(false)
            }
            .scaleEffect(uniformScale, anchor: .center)
            .clipShape(NativeBookInsetRoundedRect(rect: clipRect, cornerRadius: cornerRadius))
            .shadow(color: .black.opacity(shadowOpacity), radius: shadowRadius, x: 0, y: shadowY)
            .opacity(cardOpacity * pageRevealOpacity)
            .offset(x: cx - targetW * 0.5, y: cy - targetH * 0.5)
    }

    private func lerp(_ a: CGFloat, _ b: CGFloat, _ t: Double) -> CGFloat {
        a + (b - a) * CGFloat(t)
    }
}

/// Dimming backdrop and 3D-hinging hardcover leaf rendered directly over the
/// live `NativeReaderPage` during book open so body text renders concurrently
/// beneath the turning cover instead of waiting for the flip to end.
struct NativeBookOpenLiveCoverLayer: View, Animatable {
    let transition: NativeBookOpenTransition
    let containerFrame: CGRect
    var progress: Double

    var animatableData: Double {
        get { progress }
        set { progress = newValue }
    }

    var body: some View {
        let targetW = max(1, containerFrame.width)
        let targetH = max(1, containerFrame.height)
        let localSource = CGRect(
            x: transition.sourceFrame.minX - containerFrame.minX,
            y: transition.sourceFrame.minY - containerFrame.minY,
            width: max(28, transition.sourceFrame.width),
            height: max(38, transition.sourceFrame.height)
        )
        let t = min(max(progress, 0), 1)
        let curW = lerp(localSource.width, targetW, t)
        let curH = lerp(localSource.height, targetH, t)
        let cx = lerp(localSource.midX, targetW / 2, t)
        let cy = lerp(localSource.midY, targetH / 2, t)

        let rawReveal = transition.isCentered ? min(1.0, t / 0.18) : 1.0
        let smoothReveal = rawReveal * rawReveal * (3.0 - 2.0 * rawReveal)
        let cardScale = transition.isCentered ? lerp(0.88, 1.0, smoothReveal) : 1.0
        let cardOpacity = transition.isCentered ? smoothReveal : 1.0
        let cornerRadius = lerp(6.0, 0.0, t)

        let flipT = min(max((t - 0.06) / 0.86, 0), 1)
        let angle = -104.0 * flipT
        let coverOpacity = flipT < 0.72 ? 1.0 : max(0, 1.0 - (flipT - 0.72) / 0.22)
        let coverShading = 0.26 * sin(min(1, flipT / 0.85) * .pi * 0.5)

        Group {
            if let coverImage = transition.coverImage {
                Image(uiImage: coverImage)
                    .resizable()
                    .scaledToFill()
            } else {
                ZStack {
                    NativeTheme.sunk
                    Text(String(transition.book.title.prefix(1)))
                        .font(NativeTheme.font(curW * 0.27))
                        .foregroundStyle(NativeTheme.muted)
                }
            }
        }
        .frame(width: curW, height: curH)
        .clipped()
        .overlay(alignment: .leading) {
            LinearGradient(
                colors: [
                    Color.black.opacity(0.18),
                    Color.white.opacity(0.14),
                    Color.black.opacity(0.08),
                    Color.clear
                ],
                startPoint: .leading,
                endPoint: .trailing
            )
            .frame(width: max(8, curW * 0.045))
        }
        .overlay {
            Color.black.opacity(coverShading)
        }
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .rotation3DEffect(
            .degrees(angle),
            axis: (x: 0, y: 1, z: 0),
            anchor: .leading,
            anchorZ: 0,
            perspective: 0.32
        )
        .opacity(coverOpacity)
        .scaleEffect(cardScale, anchor: .center)
        .opacity(cardOpacity)
        .position(x: cx, y: cy)
        .frame(width: targetW, height: targetH)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func lerp(_ a: CGFloat, _ b: CGFloat, _ t: Double) -> CGFloat {
        a + (b - a) * CGFloat(t)
    }
}

/// Dimming wash behind the expanding book page during the live open transition.
struct NativeBookOpenDimmingLayer: View, Animatable {
    var progress: Double

    var animatableData: Double {
        get { progress }
        set { progress = newValue }
    }

    var body: some View {
        Color.black
            .opacity(0.26 * min(max(progress, 0), 1))
            .ignoresSafeArea()
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

/// Single-progress book flip scene shared by close (1 → 0) and fallback open.
/// Uses a pre-resolved cover image and interpolates the frame size directly so
/// the cover scales smoothly with zero aspect-ratio jump or placeholder flash.
private struct NativeBookFlipScene: View, Animatable {
    let book: LibraryBook
    let coverImage: UIImage?
    let pageSnapshot: UIImage?
    let sourceFrame: CGRect
    let isCentered: Bool
    let paper: Color
    var progress: Double

    @Environment(\.colorScheme) private var colorScheme

    var animatableData: Double {
        get { progress }
        set { progress = newValue }
    }

    var body: some View {
        GeometryReader { proxy in
            let container = proxy.frame(in: .global)
            let targetW = max(1, proxy.size.width)
            let targetH = max(1, proxy.size.height)
            let localSource = CGRect(
                x: sourceFrame.minX - container.minX,
                y: sourceFrame.minY - container.minY,
                width: max(28, sourceFrame.width),
                height: max(38, sourceFrame.height)
            )

            let t = min(max(progress, 0), 1)
            let curW = lerp(localSource.width, targetW, t)
            let curH = lerp(localSource.height, targetH, t)
            let cx = lerp(localSource.midX, targetW / 2, t)
            let cy = lerp(localSource.midY, targetH / 2, t)

            // Centered transitions (widget launch or off-screen fallback) softly
            // materialize/dissolve near t = 0 so the cover never pops into thin air.
            let rawReveal = isCentered ? min(1.0, t / 0.18) : 1.0
            let smoothReveal = rawReveal * rawReveal * (3.0 - 2.0 * rawReveal)
            let cardScale = isCentered ? lerp(0.88, 1.0, smoothReveal) : 1.0
            let cardOpacity = isCentered ? smoothReveal : 1.0

            // Exact 6pt radius at t = 0 matching BookCover → 0pt at full screen.
            let cornerRadius = lerp(6.0, 0.0, t)

            // Reverse the full 3D spine flip (-104°) from captured reader page into resting shelf cover.
            let flipT = min(max((t - 0.06) / 0.86, 0), 1)
            let angle = -104.0 * flipT
            let coverOpacity = flipT < 0.72 ? 1.0 : max(0, 1.0 - (flipT - 0.72) / 0.22)
            let coverShading = 0.26 * sin(min(1, flipT / 0.85) * .pi * 0.5)
            let pageShadowOpacity = 0.22 * (1.0 - flipT)
            let flightArc = sin(t * .pi)

            // Match BookCover's exact resting shadow at t = 0 so handoff never flickers.
            let restingShadowOpacity = colorScheme == .dark ? 0.0 : 0.12
            let shadowOpacity = restingShadowOpacity * (1.0 - t) + 0.22 * flightArc
            let shadowRadius = 5.0 * (1.0 - t) + 20.0 * flightArc
            let shadowY = 2.0 * (1.0 - t) + 10.0 * flightArc

            ZStack {
                // Softly dim the underlying screen as the book shrinks back to the shelf.
                Color.black
                    .opacity(0.22 * t)

                ZStack {
                    // Captured reader page (or paper fallback) crossfading into the cover.
                    ZStack(alignment: .leading) {
                        paper
                        if let pageSnapshot {
                            Image(uiImage: pageSnapshot)
                                .resizable()
                                .scaledToFill()
                                .frame(width: curW, height: curH)
                                .clipped()
                                .opacity(min(1.0, t / 0.65))
                        }
                        LinearGradient(
                            colors: [
                                Color.black.opacity(pageShadowOpacity),
                                Color.black.opacity(pageShadowOpacity * 0.35),
                                Color.clear
                            ],
                            startPoint: .leading,
                            endPoint: UnitPoint(x: max(0.18, 0.72 * (1.0 - flipT * 0.65)), y: 0.5)
                        )
                    }
                    .frame(width: curW, height: curH)
                    .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                    .shadow(
                        color: .black.opacity(shadowOpacity),
                        radius: shadowRadius,
                        x: 0,
                        y: shadowY
                    )

                    // Front hardcover leaf settling cleanly onto the shelf frame.
                    Group {
                        if let coverImage {
                            Image(uiImage: coverImage)
                                .resizable()
                                .scaledToFill()
                        } else {
                            ZStack {
                                NativeTheme.sunk
                                Text(String(book.title.prefix(1)))
                                    .font(NativeTheme.font(curW * 0.27))
                                    .foregroundStyle(NativeTheme.muted)
                            }
                        }
                    }
                    .frame(width: curW, height: curH)
                    .clipped()
                    .overlay(alignment: .leading) {
                        LinearGradient(
                            colors: [
                                Color.black.opacity(0.18),
                                Color.white.opacity(0.14),
                                Color.black.opacity(0.08),
                                Color.clear
                            ],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                        .frame(width: max(8, curW * 0.045))
                    }
                    .overlay {
                        Color.black.opacity(coverShading)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                    .rotation3DEffect(
                        .degrees(angle),
                        axis: (x: 0, y: 1, z: 0),
                        anchor: .leading,
                        anchorZ: 0,
                        perspective: 0.32
                    )
                    .opacity(coverOpacity)
                }
                .frame(width: curW, height: curH)
                .scaleEffect(cardScale, anchor: .center)
                .opacity(cardOpacity)
                .position(x: cx, y: cy)
            }
            .frame(width: targetW, height: targetH)
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func lerp(_ a: CGFloat, _ b: CGFloat, _ t: Double) -> CGFloat {
        a + (b - a) * CGFloat(t)
    }
}

/// Played over `NativeShell` while the live shelf (or widget launch backdrop) remains visible beneath.
struct NativeBookOpenOverlay: View {
    let transition: NativeBookOpenTransition
    let paper: Color
    let onReachedFullScreen: () -> Void
    let onFinished: () -> Void

    @State private var progress: Double = 0
    @State private var presented = false

    var body: some View {
        NativeBookFlipScene(
            book: transition.book,
            coverImage: transition.coverImage,
            pageSnapshot: nil,
            sourceFrame: transition.sourceFrame,
            isCentered: transition.isCentered,
            paper: paper,
            progress: progress
        )
        .task(id: transition.id) {
            progress = 0
            presented = false
            let duration = transition.isCentered
                ? NativeBookOpenTransitionPolicy.openFromCenterDuration
                : NativeBookOpenTransitionPolicy.openFromCoverDuration
            let mountDelay = transition.isCentered
                ? NativeBookOpenTransitionPolicy.openFromCenterMountDelay
                : NativeBookOpenTransitionPolicy.openFromCoverMountDelay
            let cleanupDelay = transition.isCentered
                ? NativeBookOpenTransitionPolicy.openFromCenterCleanupDelay
                : NativeBookOpenTransitionPolicy.openFromCoverCleanupDelay

            withAnimation(.timingCurve(0.22, 1.0, 0.36, 1.0, duration: duration)) {
                progress = 1
            }

            try? await Task.sleep(nanoseconds: UInt64(mountDelay * 1_000_000_000))
            guard !Task.isCancelled else { return }
            if !presented {
                presented = true
                onReachedFullScreen()
            }

            let remaining = max(0.02, cleanupDelay - mountDelay)
            try? await Task.sleep(nanoseconds: UInt64(remaining * 1_000_000_000))
            guard !Task.isCancelled else { return }
            onFinished()
        }
    }
}

/// Reverse cover-shut overlay played over `NativeShell` when returning from the reader.
/// Lands pixel-identically on the shelf cover before handing off in a single frame
/// (no fade-to-empty-hole flicker).
struct NativeBookCloseOverlay: View {
    let transition: NativeBookOpenTransition
    let paper: Color
    let onFinished: () -> Void

    @State private var progress: Double = 1

    var body: some View {
        NativeBookFlipScene(
            book: transition.book,
            coverImage: transition.coverImage,
            pageSnapshot: transition.pageSnapshot,
            sourceFrame: transition.sourceFrame,
            isCentered: transition.isCentered,
            paper: paper,
            progress: progress
        )
        .task(id: transition.id) {
            progress = 1
            let duration = transition.isCentered
                ? NativeBookOpenTransitionPolicy.closeToCenterDuration
                : NativeBookOpenTransitionPolicy.closeToCoverDuration
            let cleanupDelay = transition.isCentered
                ? NativeBookOpenTransitionPolicy.closeToCenterCleanupDelay
                : NativeBookOpenTransitionPolicy.closeToCoverCleanupDelay

            withAnimation(.timingCurve(0.22, 1.0, 0.36, 1.0, duration: duration)) {
                progress = 0
            }

            try? await Task.sleep(nanoseconds: UInt64(cleanupDelay * 1_000_000_000))
            guard !Task.isCancelled else { return }
            onFinished()
        }
    }
}
