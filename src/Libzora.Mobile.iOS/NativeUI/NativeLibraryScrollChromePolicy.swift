import Foundation
import CoreGraphics

/// Pure scroll-driven state machine for the library's floating masthead and fade mask.
/// Both reveal and hide track scroll distance 1:1 with the finger's movement speed.
struct NativeLibraryScrollChromePolicy: Equatable {
    private(set) var offset: CGFloat = 0
    private(set) var maximumOffset: CGFloat = 0
    private(set) var mastheadHeight: CGFloat = defaultMastheadHeight
    private(set) var mastheadProgress: CGFloat = 0
    private(set) var glassOpacity: Double = 0

    static let defaultMastheadHeight: CGFloat = 64
    static let glassFadeDistance: CGFloat = 48
    static let topSnapThreshold: CGFloat = 0.5

    var effectiveMastheadHeight: CGFloat {
        mastheadHeight.isFinite && mastheadHeight > 1 ? mastheadHeight : Self.defaultMastheadHeight
    }

    mutating func updateMastheadHeight(_ height: CGFloat) {
        guard height.isFinite, height > 1 else { return }
        mastheadHeight = height
        enforceTopZoneInvariant()
    }

    mutating func updateBounds(contentHeight: CGFloat, viewportHeight: CGFloat) {
        guard contentHeight.isFinite, viewportHeight.isFinite, viewportHeight > 0 else { return }
        maximumOffset = max(0, contentHeight - viewportHeight)
        if offset > maximumOffset {
            offset = maximumOffset
            if offset <= Self.topSnapThreshold {
                mastheadProgress = 0
            } else {
                enforceTopZoneInvariant()
            }
            updateGlassOpacity()
        }
    }

    mutating func update(clampedOffset value: CGFloat) {
        guard value.isFinite else { return }
        let clamped = min(maximumOffset, max(0, value))
        let delta = clamped - offset
        guard abs(delta) > 0.25
            || (clamped <= Self.topSnapThreshold && (offset > Self.topSnapThreshold || mastheadProgress > 0)) else {
            return
        }

        let travel = effectiveMastheadHeight
        if clamped <= Self.topSnapThreshold {
            // Both headers share identical geometry at the top; hand back cleanly to the inline header.
            mastheadProgress = 0
        } else if mastheadProgress > 0 {
            // Floating masthead is active: follow both upward and downward drags 1:1 with the finger.
            let next = mastheadProgress - delta / travel
            mastheadProgress = min(1, max(0, next))
            enforceTopZoneInvariant(for: clamped)
        } else if delta < 0 && offset > travel {
            // Inline masthead is off-screen and the user is dragging downward: reveal 1:1 with the finger.
            let next = -delta / travel
            mastheadProgress = min(1, max(0, next))
            enforceTopZoneInvariant(for: clamped)
        }

        offset = clamped
        updateGlassOpacity()
    }

    /// Returns the target progress (`0` or `1`) to settle toward when scrolling comes to rest
    /// partway through a reveal/hide gesture, or `nil` if already settled.
    mutating func settleTarget() -> CGFloat? {
        guard mastheadProgress > 0 && mastheadProgress < 1 else { return nil }
        let target: CGFloat
        if offset < effectiveMastheadHeight {
            target = 1
        } else {
            target = mastheadProgress >= 0.5 ? 1 : 0
        }
        mastheadProgress = target
        return target
    }

    private mutating func enforceTopZoneInvariant(for currentOffset: CGFloat? = nil) {
        let value = currentOffset ?? offset
        let travel = effectiveMastheadHeight
        if mastheadProgress > 0 && value < travel {
            // Keep the floating masthead at least as revealed as the inline masthead's
            // natural position so the inline header never pops in or doubles up.
            let inlineReveal = max(0, min(1, 1 - value / travel))
            mastheadProgress = max(mastheadProgress, inlineReveal)
        }
    }

    private mutating func updateGlassOpacity() {
        let clamped = min(1, max(0, Double(offset / Self.glassFadeDistance)))
        let quantized = (clamped * 25).rounded() / 25
        if clamped == 0 {
            glassOpacity = 0
        } else if clamped == 1 {
            glassOpacity = 1
        } else if abs(quantized - glassOpacity) >= 0.03 {
            glassOpacity = quantized
        }
    }
}
