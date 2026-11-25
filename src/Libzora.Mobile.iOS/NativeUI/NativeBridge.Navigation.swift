import QuartzCore
import SwiftUI
import UIKit

/// Pure UIKit brand cover rendered synchronously on frame 0 when a widget deep
/// link arrives, avoiding the one-frame transparent delay of UIHostingController.
@MainActor
private final class NativeWidgetBrandOverlayView: UIView {
    private static let appIconImage: UIImage? = {
        if let icons = Bundle.main.infoDictionary?["CFBundleIcons"] as? [String: Any],
           let primary = icons["CFBundlePrimaryIcon"] as? [String: Any],
           let files = primary["CFBundleIconFiles"] as? [String] {
            for name in files.reversed() {
                if let image = UIImage(named: name) { return image }
            }
        }
        return UIImage(named: "Libzora60x60")
    }()

    private static let pageColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0x12 / 255.0, green: 0x12 / 255.0, blue: 0x12 / 255.0, alpha: 1)
            : .white
    }

    private static let inkColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0xed / 255.0, green: 0xed / 255.0, blue: 0xed / 255.0, alpha: 1)
            : UIColor(red: 0x1c / 255.0, green: 0x1c / 255.0, blue: 0x1c / 255.0, alpha: 1)
    }

    private let iconContainer = UIView()
    private let iconImageView = UIImageView()
    private let titleLabel = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        isOpaque = true
        backgroundColor = Self.pageColor
        autoresizingMask = [.flexibleWidth, .flexibleHeight]
        isUserInteractionEnabled = false
        isAccessibilityElement = false
        accessibilityElementsHidden = true
        layer.zPosition = 10_000

        let stack = UIStackView()
        stack.axis = .vertical
        stack.alignment = .center
        stack.spacing = 16
        stack.translatesAutoresizingMaskIntoConstraints = false

        if let icon = Self.appIconImage {
            iconContainer.translatesAutoresizingMaskIntoConstraints = false
            iconContainer.layer.shadowColor = UIColor.black.cgColor
            iconContainer.layer.shadowOpacity = 0.08
            iconContainer.layer.shadowRadius = 14
            iconContainer.layer.shadowOffset = CGSize(width: 0, height: 6)

            iconImageView.image = icon
            iconImageView.contentMode = .scaleAspectFit
            iconImageView.clipsToBounds = true
            iconImageView.layer.cornerRadius = 20
            iconImageView.layer.cornerCurve = .continuous
            iconImageView.layer.borderWidth = 0.5
            iconImageView.translatesAutoresizingMaskIntoConstraints = false

            iconContainer.addSubview(iconImageView)
            NSLayoutConstraint.activate([
                iconContainer.widthAnchor.constraint(equalToConstant: 82),
                iconContainer.heightAnchor.constraint(equalToConstant: 82),
                iconImageView.leadingAnchor.constraint(equalTo: iconContainer.leadingAnchor),
                iconImageView.trailingAnchor.constraint(equalTo: iconContainer.trailingAnchor),
                iconImageView.topAnchor.constraint(equalTo: iconContainer.topAnchor),
                iconImageView.bottomAnchor.constraint(equalTo: iconContainer.bottomAnchor),
            ])
            stack.addArrangedSubview(iconContainer)
        }

        let baseFont = NativeTheme.uiKitFont(24)
        let scaledFont = UIFontMetrics(forTextStyle: .title2).scaledFont(for: baseFont)
        titleLabel.attributedText = NSAttributedString(
            string: "Libzora",
            attributes: [
                .font: scaledFont,
                .kern: 2.5,
                .foregroundColor: Self.inkColor,
            ]
        )
        stack.addArrangedSubview(titleLabel)

        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        updateBorderColor()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func traitCollectionDidChange(_ previousTraitCollection: UITraitCollection?) {
        super.traitCollectionDidChange(previousTraitCollection)
        updateBorderColor()
    }

    private func updateBorderColor() {
        iconImageView.layer.borderColor = Self.inkColor
            .resolvedColor(with: traitCollection)
            .withAlphaComponent(0.08)
            .cgColor
    }
}

@MainActor
extension NativeStore {
    private static let minimumWidgetBrandHoldNs: UInt64 = 380_000_000

    func beginWidgetBrandCover() {
        widgetTabTransition?.finish()
        widgetTabTransition = nil
        widgetHandoffStartedNs = DispatchTime.now().uptimeNanoseconds
        widgetColdLaunch = true
        showsLaunchAnimation = false
        guard let window = navigationWindow else { return }
        _ = ensureWidgetBrandOverlay(in: window)
        CATransaction.flush()
    }

    func beginWidgetTabSlideTransition() {
        widgetTabTransition?.finish()
        widgetTabTransition = nil
        dismissWidgetBrandCover()
        dismissWidgetEntrySnapshotCover()
        showsLaunchAnimation = false
        let canAnimate = !UIAccessibility.isReduceMotionEnabled
            && !preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        guard canAnimate, let window = navigationWindow else { return }
        if let oldView = window.snapshotView(afterScreenUpdates: false) {
            widgetTabTransition = NativePageSlideTransition(
                window: window,
                oldPageView: oldView,
                paper: window.backgroundColor ?? .systemBackground
            )
        } else {
            widgetTabTransition = NativePageSlideTransition(
                window: window,
                snapshot: captureNavigationSnapshot(),
                paper: window.backgroundColor ?? .systemBackground
            )
        }
        CATransaction.flush()
    }

    func beginWidgetEntrySnapshotCover() {
        dismissWidgetEntrySnapshotCover()
        guard let window = navigationWindow,
              window.bounds.width > 10, window.bounds.height > 10 else { return }
        let snapshotView: UIView
        if let live = window.snapshotView(afterScreenUpdates: false) {
            snapshotView = live
        } else {
            let imageView = UIImageView(image: captureNavigationSnapshot())
            imageView.backgroundColor = window.backgroundColor ?? .systemBackground
            imageView.contentMode = .scaleToFill
            snapshotView = imageView
        }
        snapshotView.frame = window.bounds
        snapshotView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        snapshotView.isUserInteractionEnabled = false
        snapshotView.isAccessibilityElement = false
        snapshotView.accessibilityElementsHidden = true
        snapshotView.layer.zPosition = 10_000
        window.addSubview(snapshotView)
        window.bringSubviewToFront(snapshotView)
        widgetEntrySnapshotCover = snapshotView
        CATransaction.flush()
    }

    func takeWidgetEntrySnapshotCover() -> UIView? {
        let cover = widgetEntrySnapshotCover
        widgetEntrySnapshotCover = nil
        cover?.removeFromSuperview()
        return cover
    }

    func dismissWidgetEntrySnapshotCover() {
        widgetEntrySnapshotCover?.removeFromSuperview()
        widgetEntrySnapshotCover = nil
    }

    func takeWidgetTabTransition() -> NativePageSlideTransition? {
        let transition = widgetTabTransition
        widgetTabTransition = nil
        return transition
    }

    func waitForWidgetForegroundCommit() async {
        if UIApplication.shared.applicationState != .active {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                var observer: NSObjectProtocol?
                var completed = false
                let finish: @MainActor () -> Void = {
                    guard !completed else { return }
                    completed = true
                    if let observer {
                        NotificationCenter.default.removeObserver(observer)
                    }
                    continuation.resume()
                }
                observer = NotificationCenter.default.addObserver(
                    forName: UIApplication.didBecomeActiveNotification,
                    object: nil,
                    queue: .main
                ) { _ in
                    Task { @MainActor in finish() }
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    Task { @MainActor in finish() }
                }
            }
            await NativeDisplayFrame.waitForCommit()
        }
    }

    func keepWidgetBrandOverlayOnTop() {
        guard widgetColdLaunch, let window = navigationWindow else { return }
        _ = ensureWidgetBrandOverlay(in: window)
    }

    private func applyBrandOverlayStyle(_ view: UIView) {
        switch preferences.themeMode {
        case 1: view.overrideUserInterfaceStyle = .light
        case 2: view.overrideUserInterfaceStyle = .dark
        default: view.overrideUserInterfaceStyle = .unspecified
        }
    }

    private func ensureWidgetBrandOverlay(in window: UIWindow) -> UIView {
        if let existing = widgetBrandOverlay {
            existing.frame = window.bounds
            applyBrandOverlayStyle(existing)
            if existing.superview !== window {
                window.addSubview(existing)
            }
            window.bringSubviewToFront(existing)
            existing.layoutIfNeeded()
            return existing
        }
        let overlay = NativeWidgetBrandOverlayView(frame: window.bounds)
        applyBrandOverlayStyle(overlay)
        window.addSubview(overlay)
        window.bringSubviewToFront(overlay)
        overlay.layoutIfNeeded()
        widgetBrandOverlay = overlay
        return overlay
    }

    func waitForMinimumWidgetBrandHold() async {
        guard widgetColdLaunch,
              !UIAccessibility.isReduceMotionEnabled,
              !preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled),
              let started = widgetHandoffStartedNs else { return }
        let elapsed = DispatchTime.now().uptimeNanoseconds &- started
        if elapsed < Self.minimumWidgetBrandHoldNs {
            try? await Task.sleep(nanoseconds: Self.minimumWidgetBrandHoldNs - elapsed)
        }
    }

    func captureWidgetBrandSnapshot() -> UIImage? {
        guard let window = navigationWindow,
              window.bounds.width > 10, window.bounds.height > 10 else { return nil }
        let sourceView: UIView
        if let overlay = widgetBrandOverlay {
            overlay.frame = window.bounds
            applyBrandOverlayStyle(overlay)
            overlay.layoutIfNeeded()
            sourceView = overlay
        } else {
            let overlay = NativeWidgetBrandOverlayView(frame: window.bounds)
            applyBrandOverlayStyle(overlay)
            overlay.layoutIfNeeded()
            sourceView = overlay
        }
        let format = UIGraphicsImageRendererFormat()
        format.scale = min(window.screen.scale, 2)
        format.opaque = true
        return UIGraphicsImageRenderer(bounds: window.bounds, format: format).image { ctx in
            sourceView.layer.render(in: ctx.cgContext)
        }
    }

    func createWidgetBrandSlideTransition(paper: UIColor? = nil) -> NativePageSlideTransition? {
        let window = navigationWindow
        let canAnimate = !UIAccessibility.isReduceMotionEnabled
            && !preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        guard canAnimate else { return nil }
        let snapshot = captureWidgetBrandSnapshot()
        return NativePageSlideTransition(
            window: window,
            snapshot: snapshot,
            paper: paper ?? window?.backgroundColor ?? .systemBackground
        )
    }

    func dismissWidgetBrandCover() {
        widgetBrandOverlay?.removeFromSuperview()
        widgetBrandOverlay = nil
        widgetHandoffStartedNs = nil
        widgetColdLaunch = false
    }
}
