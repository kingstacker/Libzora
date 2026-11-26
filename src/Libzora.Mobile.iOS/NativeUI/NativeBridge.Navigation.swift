import QuartzCore
import SwiftUI
import UIKit

/// Pure UIKit brand cover rendered synchronously on frame 0 during cold launch
/// and cold widget deep links, avoiding the one-frame transparent delay of UIHostingController.
@MainActor
private final class NativeLaunchBrandOverlayView: UIView {
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
    private let progressIndicator = UIActivityIndicatorView(style: .medium)

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

        stack.addArrangedSubview(titleLabel)

        progressIndicator.hidesWhenStopped = true
        progressIndicator.isHidden = true
        stack.addArrangedSubview(progressIndicator)

        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        refreshAppearance()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func traitCollectionDidChange(_ previousTraitCollection: UITraitCollection?) {
        super.traitCollectionDidChange(previousTraitCollection)
        updateBorderColor()
    }

    func refreshAppearance() {
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
        progressIndicator.color = Self.inkColor
        updateBorderColor()
    }

    func setShowProgress(_ visible: Bool) {
        progressIndicator.isHidden = !visible
        if visible {
            progressIndicator.startAnimating()
        } else {
            progressIndicator.stopAnimating()
        }
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
    private static let minimumLaunchBrandHoldNs: UInt64 = 380_000_000

    func openRequestedTab(_ tab: Int) {
        // Same-page links must cancel pending different destinations too.
        let revision = beginWidgetNavigation(.tab(tab))
        pendingReaderRestore = nil
        pendingOpenBookId = nil
        queuedReaderOpen.clear()
        let alreadyVisible = !preparingLibrary && libraryLoaded && activeTab == tab
            && !launchBrandActive && !restoringReader
            && readerBook == nil && !readerPresentationActive
        guard !alreadyVisible else {
            if openingReader { dismissReaderForTab() }
            widgetNavigationIntent.markPresented(revision)
            dismissWidgetEntrySnapshotCover()
            if !restoringReader { dismissLaunchBrandCover() }
            return
        }
        let useBrandCover = NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
            preparingLibrary: preparingLibrary, libraryLoaded: libraryLoaded, restoringReader: restoringReader
        )
        if useBrandCover {
            beginLaunchBrandCover(resetTimer: false)
            requestedTab = tab
            restoringReader = false
            dismissReaderForTab()
        } else {
            widgetTabTask = Task { @MainActor [weak self] in
                guard let self else { return }
                defer {
                    if self.widgetNavigationIntent.isCurrent(revision) { self.widgetTabTask = nil }
                }
                await self.waitForWidgetForegroundCommit()
                guard !Task.isCancelled, self.widgetNavigationIntent.isCurrent(revision),
                      self.pendingOpenBookId == nil else { return }
                let stillVisible = !self.preparingLibrary && self.libraryLoaded && self.activeTab == tab
                    && !self.launchBrandActive && !self.restoringReader
                    && self.readerBook == nil && !self.readerPresentationActive
                guard !stillVisible else {
                    self.widgetNavigationIntent.markPresented(revision)
                    return
                }
                await self.beginWidgetTabSlideTransition(revision: revision)
                guard !Task.isCancelled, self.widgetNavigationIntent.isCurrent(revision) else { return }
                self.requestedTab = tab
                self.restoringReader = false
                self.dismissReaderForTab()
            }
        }
    }

    @discardableResult
    func beginWidgetNavigation(_ destination: NativeWidgetNavigationIntent.Destination) -> Int {
        let revision = widgetNavigationIntent.begin(destination)
        // Finish the old flight BEFORE capturing a new starting page, otherwise
        // a quick second link freezes an intermediate, half-slid composition.
        widgetTabTask?.cancel()
        widgetTabTask = nil
        widgetNavigationTask?.cancel()
        widgetNavigationTask = nil
        widgetTabTransition?.finish()
        widgetTabTransition = nil
        activeWidgetTabTransition?.finish()
        activeWidgetTabTransition = nil
        readerEntryTransition?.finish()
        readerEntryTransition = nil
        if let closing = closingBookTransition { finishClosingBookTransition(id: closing.id) }
        requestedTab = nil
        return revision
    }

    func isWidgetTabReady(_ tab: Int) -> Bool {
        switch tab {
        case 0: return libraryLoaded
        case 1, 2: return readyWidgetTabs.contains(tab)
        default: return true
        }
    }

    /// Let the destination slide in before save/dispose work can block the UI.
    /// The backend remains serialized; only presentation is allowed to finish early.
    func waitForWidgetTabPresentation() async {
        let deadline = CACurrentMediaTime() + 12
        while widgetNavigationIntent.isTabPresentationPending,
              UIApplication.shared.applicationState == .active,
              CACurrentMediaTime() < deadline, !Task.isCancelled {
            try? await Task.sleep(nanoseconds: 16_666_667)
        }
    }

    var launchBrandPageColor: UIColor? {
        let style: UIUserInterfaceStyle
        switch preferences.themeMode {
        case 1: style = .light
        case 2: style = .dark
        default: style = navigationWindow?.traitCollection.userInterfaceStyle ?? .light
        }
        return style == .dark
            ? UIColor(red: 0x12 / 255.0, green: 0x12 / 255.0, blue: 0x12 / 255.0, alpha: 1)
            : .white
    }

    func beginLaunchBrandCover(resetTimer: Bool = true) {
        widgetTabTransition?.finish()
        widgetTabTransition = nil
        if resetTimer || launchBrandStartedNs == nil {
            launchBrandStartedNs = DispatchTime.now().uptimeNanoseconds
        }
        launchBrandActive = true
        guard !restartRequired, !dataBusy, let window = navigationWindow else { return }
        _ = ensureLaunchBrandOverlay(in: window)
        CATransaction.flush()
    }

    func beginWidgetTabSlideTransition(revision: Int) async {
        guard widgetNavigationIntent.isCurrent(revision), !Task.isCancelled else { return }
        widgetTabTransition?.finish()
        widgetTabTransition = nil
        dismissLaunchBrandCover()
        dismissWidgetEntrySnapshotCover()
        let canAnimate = !UIAccessibility.isReduceMotionEnabled
            && !preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        guard canAnimate, let window = navigationWindow else { return }
        while closingWindowTransition != nil, !Task.isCancelled, widgetNavigationIntent.isCurrent(revision) {
            await NativeDisplayFrame.waitForCommit()
        }
        let oldView = readerBook != nil
            ? await captureReaderPresentationSnapshot()
            : window.snapshotView(afterScreenUpdates: false)
        guard widgetNavigationIntent.isCurrent(revision), !Task.isCancelled else { return }
        if let oldView {
            widgetTabTransition = NativePageSlideTransition(
                window: window, oldPageView: oldView,
                paper: window.backgroundColor ?? .systemBackground
            )
        } else {
            widgetTabTransition = NativePageSlideTransition(
                window: window, snapshot: captureNavigationSnapshot(),
                paper: window.backgroundColor ?? .systemBackground
            )
        }
        CATransaction.flush()
    }

    func beginWidgetEntrySnapshotCover(snapshot preparedSnapshot: UIView? = nil) {
        dismissWidgetEntrySnapshotCover()
        guard let window = navigationWindow,
              window.bounds.width > 10, window.bounds.height > 10 else { return }
        let snapshotView: UIView
        if let live = preparedSnapshot ?? window.snapshotView(afterScreenUpdates: false) {
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
#if DEBUG
        // Deterministic latest-intent regression window; absent from release.
        if ProcessInfo.processInfo.arguments.contains("--libzora-test-slow-widget-navigation") {
            try? await Task.sleep(nanoseconds: 2_000_000_000)
        }
#endif
        guard !Task.isCancelled else { return }
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

    func keepLaunchBrandOverlayOnTop() {
        guard launchBrandActive, !restartRequired, !dataBusy, let window = navigationWindow else { return }
        _ = ensureLaunchBrandOverlay(in: window)
    }

    func refreshLaunchBrandAppearance() {
        guard let overlay = launchBrandOverlay as? NativeLaunchBrandOverlayView else { return }
        applyBrandOverlayStyle(overlay)
        overlay.refreshAppearance()
    }

    func setLaunchBrandProgressVisible(_ visible: Bool) {
        guard let overlay = launchBrandOverlay as? NativeLaunchBrandOverlayView else { return }
        overlay.setShowProgress(visible)
    }

    private func applyBrandOverlayStyle(_ view: UIView) {
        switch preferences.themeMode {
        case 1: view.overrideUserInterfaceStyle = .light
        case 2: view.overrideUserInterfaceStyle = .dark
        default: view.overrideUserInterfaceStyle = .unspecified
        }
    }

    private func ensureLaunchBrandOverlay(in window: UIWindow) -> UIView {
        if let existing = launchBrandOverlay as? NativeLaunchBrandOverlayView {
            existing.frame = window.bounds
            applyBrandOverlayStyle(existing)
            existing.refreshAppearance()
            if existing.superview !== window {
                window.addSubview(existing)
            }
            window.bringSubviewToFront(existing)
            existing.layoutIfNeeded()
            return existing
        }
        let overlay = NativeLaunchBrandOverlayView(frame: window.bounds)
        applyBrandOverlayStyle(overlay)
        window.addSubview(overlay)
        window.bringSubviewToFront(overlay)
        overlay.layoutIfNeeded()
        launchBrandOverlay = overlay
        return overlay
    }

    func waitForMinimumLaunchBrandHold() async {
        guard launchBrandActive,
              !UIAccessibility.isReduceMotionEnabled,
              !preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled),
              let started = launchBrandStartedNs else { return }
        let elapsed = DispatchTime.now().uptimeNanoseconds &- started
        if elapsed < Self.minimumLaunchBrandHoldNs {
            try? await Task.sleep(nanoseconds: Self.minimumLaunchBrandHoldNs - elapsed)
        }
    }

    func captureLaunchBrandSnapshot() -> UIImage? {
        guard let window = navigationWindow,
              window.bounds.width > 10, window.bounds.height > 10 else { return nil }
        let sourceView: UIView
        if let overlay = launchBrandOverlay as? NativeLaunchBrandOverlayView {
            overlay.frame = window.bounds
            applyBrandOverlayStyle(overlay)
            overlay.refreshAppearance()
            overlay.layoutIfNeeded()
            sourceView = overlay
        } else {
            let overlay = NativeLaunchBrandOverlayView(frame: window.bounds)
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

    func createLaunchBrandSlideTransition(paper: UIColor? = nil) -> NativePageSlideTransition? {
        let window = navigationWindow
        let canAnimate = !UIAccessibility.isReduceMotionEnabled
            && !preferences.isPowerSaving(systemLowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        guard canAnimate else { return nil }
        let snapshot = captureLaunchBrandSnapshot()
        return NativePageSlideTransition(
            window: window,
            snapshot: snapshot,
            paper: paper ?? launchBrandPageColor ?? window?.backgroundColor ?? .systemBackground
        )
    }

    func dismissLaunchBrandCover() {
        launchBrandOverlay?.removeFromSuperview()
        launchBrandOverlay = nil
        launchBrandStartedNs = nil
        launchBrandActive = false
    }
}
