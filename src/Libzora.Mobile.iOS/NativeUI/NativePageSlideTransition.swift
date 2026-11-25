import UIKit

/// Two immutable page snapshots animated by Core Animation. The live Avalonia
/// surface never moves, changes bounds or reflows during this handoff. UIKit
/// drives the composited layers at the display's supported refresh rate.
@MainActor
final class NativePageSlideTransition {
    let id = UUID()
    private weak var window: UIWindow?
    private let overlay: UIView
    private let oldPage: UIView
    private var animator: UIViewPropertyAnimator?
    private var continuation: CheckedContinuation<Void, Never>?
    private var finished = false

    init?(window: UIWindow?, oldPageView: UIView?, paper: UIColor) {
        guard let window, window.bounds.width > 10, window.bounds.height > 10 else { return nil }
        self.window = window
        overlay = UIView(frame: window.bounds)
        overlay.backgroundColor = paper
        overlay.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        overlay.isAccessibilityElement = false
        overlay.accessibilityElementsHidden = true
        overlay.layer.zPosition = 10_001
        let page = oldPageView ?? UIView(frame: overlay.bounds)
        page.frame = overlay.bounds
        page.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        overlay.addSubview(page)
        oldPage = page
        window.addSubview(overlay)
    }

    convenience init?(window: UIWindow?, snapshot: UIImage?, paper: UIColor) {
        let imageView = UIImageView()
        imageView.image = snapshot
        imageView.backgroundColor = paper
        imageView.contentMode = .scaleToFill
        self.init(window: window, oldPageView: imageView, paper: paper)
    }

    func show(_ view: UIView?, animation: NativeBookOpenAnimation, duration: Double,
              reverse: Bool = false) async {
        guard !finished, let view, view.bounds.width > 10, view.bounds.height > 10,
              let window, overlay.bounds.size == window.bounds.size else { finish(); return }
        let newPage: UIView
        if let snapshot = view.snapshotView(afterScreenUpdates: false) {
            // Render-server snapshots retain GPU-backed Skia content too;
            // rasterizing a newly mounted hierarchy can miss that first layer.
            newPage = snapshot
        } else {
            let format = UIGraphicsImageRendererFormat()
            format.scale = min(window.screen.scale, 2)
            let snapshot = UIGraphicsImageRenderer(bounds: view.bounds, format: format).image { _ in
                view.drawHierarchy(in: view.bounds, afterScreenUpdates: false)
            }
            let image = UIImageView(image: snapshot)
            image.contentMode = .scaleToFill
            newPage = image
        }
        newPage.frame = overlay.bounds
        newPage.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        overlay.addSubview(newPage)
        let width = overlay.bounds.width
        let direction: CGFloat = reverse ? -1 : 1
        if animation == .slide { newPage.transform = CGAffineTransform(translationX: direction * width, y: 0) }
        if animation == .fade { newPage.alpha = 0 }
        guard animation.isPageAnimation else { finish(); return }
        await withTaskCancellationHandler(operation: {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled, !finished else { continuation.resume(); finish(); return }
                self.continuation = continuation
                if !UIView.areAnimationsEnabled { UIView.setAnimationsEnabled(true) }
                let animator = UIViewPropertyAnimator(duration: duration, curve: .easeInOut) {
                    newPage.transform = .identity
                    newPage.alpha = 1
                    if animation == .slide {
                        self.oldPage.transform = CGAffineTransform(translationX: -direction * width, y: 0)
                    }
                }
                self.animator = animator
                animator.addCompletion { [weak self] _ in self?.finish() }
                animator.startAnimation()
                // A scene interruption or simulator capture can suppress the
                // UIKit completion callback. Never leave an invisible window
                // overlay trapping input after the animation's own duration.
                DispatchQueue.main.asyncAfter(deadline: .now() + duration + 0.08) { [weak self] in
                    self?.finish()
                }
            }
        }, onCancel: { Task { @MainActor in self.finish() } })
    }

    func finish() {
        guard !finished else { return }
        finished = true
        if animator?.state == .active { animator?.stopAnimation(true) }
        animator = nil
        overlay.removeFromSuperview()
        let completion = continuation
        continuation = nil
        completion?.resume()
    }
}
