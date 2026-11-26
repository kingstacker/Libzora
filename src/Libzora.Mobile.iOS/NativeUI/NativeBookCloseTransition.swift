import SwiftUI
import UIKit

/// Keeps the rendered reader (including its Metal surface) above the modal
/// dismissal. All flight updates are layer transforms, never reader re-layouts
/// or SwiftUI re-mounts. The snapshot lives only until the shelf handoff.
@MainActor
final class NativeBookCloseTransition: UIView {
    private let dimming = UIView()
    private let shadow = UIView()
    private let page = UIView()
    private let snapshot: UIView
    private let leaf = UIImageView()
    private let shading = UIView()
    private let placeholder = UILabel()
    private let driver = NativeBookTransitionDriver(initialProgress: 1)
    private var transition: NativeBookOpenTransition?
    private var snapshotSize: CGSize
    private var progress = 1.0

    init?(window: UIWindow, readerView: UIView?, fallback: () -> UIImage?, paper: UIColor,
          preparedSnapshot: UIView?) {
        guard window.bounds.width > 10, window.bounds.height > 10 else { return nil }
        // A UIKit snapshot retains the compositor's Metal contents; drawing
        // just AvaloniaView into a bitmap can silently return blank paper.
        let chrome: UIView
        if let captured = preparedSnapshot ?? window.snapshotView(afterScreenUpdates: false)
            ?? readerView?.snapshotView(afterScreenUpdates: false) {
            chrome = captured
        } else if let image = fallback() {
            chrome = UIImageView(image: image)
        } else {
            return nil
        }
        snapshotSize = window.bounds.size
        snapshot = UIView(frame: CGRect(origin: .zero, size: snapshotSize))
        chrome.frame = snapshot.bounds
        snapshot.addSubview(chrome)
        super.init(frame: window.bounds)
        autoresizingMask = [.flexibleWidth, .flexibleHeight]
        backgroundColor = .clear
        // Shelf taps must reach the live library and queue their intent while
        // backend teardown is pending, even before the visual flight lands.
        isUserInteractionEnabled = false
        isAccessibilityElement = false
        accessibilityElementsHidden = true
        layer.zPosition = 10_000

        dimming.backgroundColor = .black
        addSubview(dimming)
        shadow.backgroundColor = paper
        shadow.layer.shadowColor = UIColor.black.cgColor
        addSubview(shadow)
        page.backgroundColor = paper
        page.clipsToBounds = true
        snapshot.isUserInteractionEnabled = false
        snapshot.frame = CGRect(origin: .zero, size: snapshotSize)
        page.addSubview(snapshot)
        addSubview(page)
        leaf.contentMode = .scaleAspectFill
        leaf.clipsToBounds = true
        leaf.layer.anchorPoint = CGPoint(x: 0, y: 0.5)
        placeholder.textAlignment = .center
        placeholder.textColor = UIColor(NativeTheme.muted)
        leaf.addSubview(placeholder)
        shading.backgroundColor = .black
        leaf.addSubview(shading)
        addSubview(leaf)
        window.addSubview(self)
        window.bringSubviewToFront(self)
        update(progress: 1)
    }

    required init?(coder: NSCoder) { nil }

    override func layoutSubviews() {
        super.layoutSubviews()
        update(progress: progress)
    }

    func prepare(_ transition: NativeBookOpenTransition) {
        self.transition = transition
        leaf.image = transition.coverImage
        placeholder.text = String(transition.book.title.prefix(1))
        placeholder.isHidden = transition.coverImage != nil
        // A missing cover still closes into a stable card, not an empty hole.
        leaf.backgroundColor = UIColor(NativeTheme.sunk)
        update(progress: 1)
    }

    func start(onFinished: @escaping () -> Void) {
        guard let transition else { onFinished(); return }
        let duration = transition.isCentered
            ? NativeBookOpenTransitionPolicy.closeToCenterDuration
            : NativeBookOpenTransitionPolicy.closeToCoverDuration
        driver.start(id: transition.id, from: 1, to: 0, duration: duration,
                     onProgress: { [weak self] value in self?.update(progress: value) },
                     onComplete: onFinished)
    }

    func finish() {
        driver.cancel()
        removeFromSuperview()
    }

    private func update(progress: Double) {
        self.progress = progress
        guard bounds.width > 0, bounds.height > 0 else { return }
        let target = transition.map { item in
            convert(item.sourceFrame, from: window)
        } ?? bounds
        let geometry = NativeBookOpenTransitionPolicy.closeGeometry(
            sourceFrame: target, viewport: bounds, progress: progress,
            isCentered: transition?.isCentered ?? false
        )
        let t = min(max(progress, 0), 1)
        let size = geometry.frame.size
        let center = CGPoint(x: geometry.frame.midX, y: geometry.frame.midY)
        let cornerRadius = CGFloat(6 * (1 - t))
        let flip = min(max((t - 0.06) / 0.86, 0), 1)
        let flightShadow = NativeBookOpenTransitionPolicy.flightShadow(
            progress: t, isDark: traitCollection.userInterfaceStyle == .dark
        )

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        dimming.frame = bounds
        dimming.alpha = NativeBookOpenTransitionPolicy.dimmingOpacity(progress: t)
        let shadowInset = NativeBookOpenTransitionPolicy.shadowBackdropInset(progress: t)
        shadow.bounds = CGRect(x: 0, y: 0, width: max(1, size.width - shadowInset),
                               height: max(1, size.height - shadowInset))
        shadow.center = center
        shadow.layer.cornerRadius = cornerRadius
        shadow.layer.shadowOpacity = Float(flightShadow.opacity)
        shadow.layer.shadowRadius = flightShadow.radius
        shadow.layer.shadowOffset = CGSize(width: 0, height: flightShadow.y)
        shadow.layer.shadowPath = UIBezierPath(roundedRect: shadow.bounds, cornerRadius: cornerRadius).cgPath
        shadow.transform = CGAffineTransform(scaleX: geometry.scale, y: geometry.scale)
        shadow.alpha = geometry.opacity

        page.bounds = CGRect(origin: .zero, size: size)
        page.center = center
        page.layer.cornerRadius = cornerRadius
        let pageScale = size.width / max(1, snapshotSize.width)
        snapshot.transform = CGAffineTransform(scaleX: pageScale, y: pageScale)
        snapshot.center = CGPoint(x: size.width / 2, y: size.height / 2)
        page.transform = CGAffineTransform(scaleX: geometry.scale, y: geometry.scale)
        // Do not fade the actual reader to white while the cover is still open.
        page.alpha = geometry.opacity * NativeBookOpenTransitionPolicy.pageBackingOpacity(progress: t)

        leaf.bounds = CGRect(origin: .zero, size: size)
        leaf.layer.position = CGPoint(x: center.x - size.width / 2, y: center.y)
        leaf.layer.cornerRadius = cornerRadius
        placeholder.frame = leaf.bounds
        placeholder.font = NativeTheme.uiKitFont(size.width * 0.27)
        shading.frame = leaf.bounds
        shading.alpha = 0.26 * sin(min(1, flip / 0.85) * .pi * 0.5)
        var transform = CATransform3DIdentity
        transform.m34 = -0.32 / max(1, size.width)
        transform = CATransform3DRotate(transform, CGFloat(-104 * flip * .pi / 180), 0, 1, 0)
        transform = CATransform3DScale(transform, geometry.scale, geometry.scale, 1)
        leaf.layer.transform = transform
        leaf.alpha = geometry.opacity * NativeBookOpenTransitionPolicy.coverLeafOpacity(flipProgress: flip)
        CATransaction.commit()
    }
}
