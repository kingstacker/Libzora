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

/// Frame-synchronized CADisplayLink animator for 3D book open and close
/// transitions. Targets the display's maximum supported refresh rate (60Hz,
/// 120Hz ProMotion, or higher) and waits one commit interval after reaching
/// the endpoint so the terminal frame is displayed before overlay teardown.
@MainActor
final class NativeBookTransitionDriver: NSObject, ObservableObject {
    private final class DisplayLinkProxy: NSObject {
        weak var owner: NativeBookTransitionDriver?
        init(owner: NativeBookTransitionDriver) { self.owner = owner }
        @objc func tick(_ link: CADisplayLink) {
            MainActor.assumeIsolated {
                guard let owner else {
                    link.invalidate()
                    return
                }
                owner.tick(link)
            }
        }
    }

    @Published private(set) var progress: Double
    private var link: CADisplayLink?
    private var activeToken: UUID?
    private var startTimestamp: CFTimeInterval?
    private var duration: Double = 0
    private var fromValue: Double = 0
    private var toValue: Double = 1
    private var awaitingTerminalCommit = false
    private var onComplete: (() -> Void)?
    private var onProgress: ((Double) -> Void)?

    init(initialProgress: Double = 0) {
        self.progress = initialProgress
        super.init()
    }

    func prepare(progress value: Double) {
        cancel()
        progress = value
    }

    func start(
        id: UUID,
        from: Double,
        to: Double,
        duration: Double,
        onProgress: ((Double) -> Void)? = nil,
        onComplete: @escaping () -> Void
    ) {
        guard activeToken != id else { return }
        cancel()
        activeToken = id
        fromValue = from
        toValue = to
        progress = from
        self.duration = max(0.001, duration)
        startTimestamp = nil
        awaitingTerminalCommit = false
        self.onComplete = onComplete
        self.onProgress = onProgress
        onProgress?(from)

        let proxy = DisplayLinkProxy(owner: self)
        let displayLink = CADisplayLink(target: proxy, selector: #selector(DisplayLinkProxy.tick(_:)))
        if #available(iOS 15.0, *) {
            let bounds = NativeBookOpenTransitionPolicy.preferredFrameRateBounds(
                maximumFramesPerSecond: Double(UIScreen.main.maximumFramesPerSecond)
            )
            displayLink.preferredFrameRateRange = CAFrameRateRange(
                minimum: bounds.minimum,
                maximum: bounds.maximum,
                preferred: bounds.preferred
            )
        }
        link = displayLink
        displayLink.add(to: .main, forMode: .common)
    }

    func cancel() {
        link?.invalidate()
        link = nil
        activeToken = nil
        startTimestamp = nil
        awaitingTerminalCommit = false
        onComplete = nil
        onProgress = nil
    }

    private func tick(_ displayLink: CADisplayLink) {
        if awaitingTerminalCommit {
            let completion = onComplete
            cancel()
            completion?()
            return
        }
        if startTimestamp == nil {
            let step = NativeBookOpenTransitionPolicy.initialFrameStep(
                targetTimestamp: displayLink.targetTimestamp,
                timestamp: displayLink.timestamp
            )
            startTimestamp = displayLink.targetTimestamp - step
        }
        let elapsed = displayLink.targetTimestamp - (startTimestamp ?? displayLink.targetTimestamp)
        progress = NativeBookOpenTransitionPolicy.frameProgress(
            elapsed: elapsed,
            duration: duration,
            from: fromValue,
            to: toValue
        )
        if elapsed >= duration {
            progress = toValue
            awaitingTerminalCommit = true
        }
        onProgress?(progress)
    }
}

/// GPU-only modifier that scales, clips and positions the live
/// `NativeReaderPage` body during the cover-open animation while keeping the
/// underlying Avalonia Skia surface at constant full-screen bounds and
/// unchanged SwiftUI view identity (zero reflow, zero remount flash).
struct NativeBookOpenPageTransform: ViewModifier {
    let active: Bool
    let sourceFrame: CGRect
    let containerFrame: CGRect
    let isCentered: Bool
    let progress: Double

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

        let visW = curW * cardScale
        let visH = curH * cardScale
        // Keep aspect ratio locked (uniform scale) so body text never looks squashed during expand.
        let uniformScale = active ? max(0.01, visW / targetW) : 1.0
        let pageRevealOpacity = active ? NativeBookOpenTransitionPolicy.pageBackingOpacity(progress: t) : 1.0
        let clipRect = active
            ? CGRect(x: (targetW - visW) * 0.5, y: (targetH - visH) * 0.5, width: visW, height: visH)
            : .zero

        content
            .scaleEffect(uniformScale, anchor: .center)
            .clipShape(NativeBookInsetRoundedRect(rect: clipRect, cornerRadius: cornerRadius))
            .opacity(cardOpacity * pageRevealOpacity)
            .offset(x: cx - targetW * 0.5, y: cy - targetH * 0.5)
    }

    private func lerp(_ a: CGFloat, _ b: CGFloat, _ t: Double) -> CGFloat {
        a + (b - a) * CGFloat(t)
    }
}

/// Lightweight GPU-only ambient tint used during 60/120Hz 3D open/close
/// animations. Avoids `NativeAmbientWash`'s `.drawingGroup` Metal texture
/// reallocation on every resizing frame.
private struct NativeBookTransitionAmbientTint: View {
    @EnvironmentObject private var store: NativeStore
    let opacity: Double

    var body: some View {
        let colors = store.ambient.colors
        if !colors.isEmpty {
            let first = colors[0]
            let middle = colors[colors.count / 2]
            let last = colors[colors.count - 1]
            LinearGradient(
                stops: [
                    .init(color: first.color.opacity(0.65 * first.strength), location: 0),
                    .init(color: middle.color.opacity(0.55 * middle.strength), location: 0.5),
                    .init(color: last.color.opacity(0.65 * last.strength), location: 1)
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )
            .opacity(opacity)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }
}

/// Lightweight card shadow rendered behind the book page/cover so resting
/// `BookCover` shadows match seamlessly at `t = 0` without forcing an offscreen
/// shadow rasterization pass over the live reader surface or 3D cover leaf.
private struct NativeBookOpenCardShadowLayer: View {
    let sourceFrame: CGRect
    let containerFrame: CGRect
    let isCentered: Bool
    let colorScheme: ColorScheme
    let paper: Color
    let usesAmbient: Bool
    let ambientOpacity: Double
    let progress: Double

    var body: some View {
        let targetW = max(1, containerFrame.width)
        let targetH = max(1, containerFrame.height)
        let localSource = CGRect(
            x: sourceFrame.minX - containerFrame.minX,
            y: sourceFrame.minY - containerFrame.minY,
            width: max(28, sourceFrame.width),
            height: max(38, sourceFrame.height)
        )
        let t = min(max(progress, 0), 1)
        let curW = lerp(localSource.width, targetW, t)
        let curH = lerp(localSource.height, targetH, t)
        let cx = lerp(localSource.midX, targetW / 2, t)
        let cy = lerp(localSource.midY, targetH / 2, t)

        let rawReveal = isCentered ? min(1.0, t / 0.18) : 1.0
        let smoothReveal = rawReveal * rawReveal * (3.0 - 2.0 * rawReveal)
        let cardScale = isCentered ? lerp(0.88, 1.0, smoothReveal) : 1.0
        let cardOpacity = isCentered ? smoothReveal : 1.0
        let cornerRadius = lerp(6.0, 0.0, t)
        let inset = NativeBookOpenTransitionPolicy.shadowBackdropInset(progress: t)
        let shadow = NativeBookOpenTransitionPolicy.flightShadow(progress: t, isDark: colorScheme == .dark)

        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            .fill(paper)
            .overlay {
                if usesAmbient {
                    NativeBookTransitionAmbientTint(opacity: ambientOpacity)
                        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                }
            }
            .frame(width: max(1, curW - inset), height: max(1, curH - inset))
            .shadow(color: .black.opacity(shadow.opacity), radius: shadow.radius, x: 0, y: shadow.y)
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

/// 3D-hinging hardcover leaf rendered directly over the live `NativeReaderPage`
/// during book open so body text renders concurrently beneath the turning cover
/// instead of waiting for the flip to end.
struct NativeBookOpenLiveCoverLayer: View {
    let transition: NativeBookOpenTransition
    let containerFrame: CGRect
    let progress: Double

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
        let coverOpacity = NativeBookOpenTransitionPolicy.coverLeafOpacity(flipProgress: flipT)
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
struct NativeBookOpenDimmingLayer: View {
    let progress: Double

    var body: some View {
        Color.black
            .opacity(NativeBookOpenTransitionPolicy.dimmingOpacity(progress: progress))
            .ignoresSafeArea()
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

/// Isolates high-frequency `CADisplayLink` progress updates from `NativeReaderPage.body`
/// so the reader view hierarchy never re-evaluates at 60/120Hz during the open morph.
struct NativeBookOpenTransitionHost<Content: View>: View {
    let transition: NativeBookOpenTransition?
    @ObservedObject var driver: NativeBookTransitionDriver
    let containerFrame: CGRect
    let colorScheme: ColorScheme
    let paper: Color
    let usesAmbient: Bool
    let ambientOpacity: Double
    let content: Content

    var body: some View {
        let active = transition != nil
        let effectiveProgress = active ? driver.progress : 1.0
        ZStack {
            if let transition {
                NativeBookOpenDimmingLayer(progress: effectiveProgress)
                NativeBookOpenCardShadowLayer(
                    sourceFrame: transition.sourceFrame,
                    containerFrame: containerFrame,
                    isCentered: transition.isCentered,
                    colorScheme: colorScheme,
                    paper: paper,
                    usesAmbient: usesAmbient,
                    ambientOpacity: ambientOpacity,
                    progress: effectiveProgress
                )
            }

            content
                .modifier(
                    NativeBookOpenPageTransform(
                        active: active,
                        sourceFrame: transition?.sourceFrame ?? containerFrame,
                        containerFrame: containerFrame,
                        isCentered: transition?.isCentered ?? false,
                        progress: effectiveProgress
                    )
                )

            if let transition {
                NativeBookOpenLiveCoverLayer(
                    transition: transition,
                    containerFrame: containerFrame,
                    progress: effectiveProgress
                )
            }
        }
        .frame(width: max(1, containerFrame.width), height: max(1, containerFrame.height))
    }
}
