import SwiftUI
import UIKit

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
    }

    func keepWidgetBrandOverlayOnTop() {
        guard widgetColdLaunch, let window = navigationWindow else { return }
        _ = ensureWidgetBrandOverlay(in: window)
    }

    private func ensureWidgetBrandOverlay(in window: UIWindow) -> UIView {
        if let existing = widgetBrandOverlay {
            existing.frame = window.bounds
            if existing.superview !== window {
                window.addSubview(existing)
            }
            window.bringSubviewToFront(existing)
            existing.layoutIfNeeded()
            return existing
        }
        let hosting = UIHostingController(rootView: NativeWidgetLaunchBrandView())
        hosting.view.frame = window.bounds
        hosting.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        hosting.view.isUserInteractionEnabled = false
        hosting.view.isAccessibilityElement = false
        hosting.view.accessibilityElementsHidden = true
        switch preferences.themeMode {
        case 1: hosting.overrideUserInterfaceStyle = .light
        case 2: hosting.overrideUserInterfaceStyle = .dark
        default: hosting.overrideUserInterfaceStyle = .unspecified
        }
        window.addSubview(hosting.view)
        hosting.view.layoutIfNeeded()
        widgetBrandOverlayController = hosting
        widgetBrandOverlay = hosting.view
        return hosting.view
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
            overlay.layoutIfNeeded()
            sourceView = overlay
        } else {
            let hosting = UIHostingController(rootView: NativeWidgetLaunchBrandView())
            hosting.view.frame = window.bounds
            switch preferences.themeMode {
            case 1: hosting.overrideUserInterfaceStyle = .light
            case 2: hosting.overrideUserInterfaceStyle = .dark
            default: hosting.overrideUserInterfaceStyle = .unspecified
            }
            hosting.view.layoutIfNeeded()
            sourceView = hosting.view
        }
        let format = UIGraphicsImageRendererFormat()
        format.scale = min(window.screen.scale, 2)
        return UIGraphicsImageRenderer(bounds: window.bounds, format: format).image { _ in
            sourceView.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
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
        widgetBrandOverlayController = nil
        widgetHandoffStartedNs = nil
        widgetColdLaunch = false
    }
}
