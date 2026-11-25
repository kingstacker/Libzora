import CoreGraphics
import Foundation

/// Resolved anchor for a book open or close transition.
/// `.cover` anchors directly to a live visible cover on the active screen;
/// `.centered` uses a balanced hero card in the middle of the viewport
/// (used for off-screen or cross-tab fallbacks).
enum NativeBookTransitionTarget: Equatable {
    case cover(frame: CGRect, key: String)
    case centered(frame: CGRect)

    var frame: CGRect {
        switch self {
        case .cover(let frame, _), .centered(let frame):
            return frame
        }
    }

    var sourceKey: String? {
        switch self {
        case .cover(_, let key):
            return key
        case .centered:
            return nil
        }
    }

    var isCentered: Bool {
        if case .centered = self { return true }
        return false
    }
}

enum NativeBookOpenSource {
    case library
    case continueReading
    case widget
    case materials
    case dashboard
    case external
    case restore
    case other
}

enum NativeBookOpenAnimation: Equatable {
    case cover
    case slide
    case fade
    case none

    var isPageAnimation: Bool { self == .slide || self == .fade }
}

/// Pure geometry and visibility rules shared by book open/close transitions
/// and verified by host-side logic tests.
enum NativeBookOpenTransitionPolicy {
    static let openFromCoverDuration: Double = 0.68
    static let openFromCoverMountDelay = openFromCoverDuration - 0.04
    static let openFromCoverCleanupDelay = openFromCoverDuration + 0.04

    static let openFromCenterDuration: Double = openFromCoverDuration
    static let openFromCenterMountDelay: Double = openFromCoverMountDelay
    static let openFromCenterCleanupDelay: Double = openFromCoverCleanupDelay

    static let closeToCoverDuration: Double = 0.59
    static let closeToCoverCleanupDelay = closeToCoverDuration + 0.04

    static let closeToCenterDuration: Double = 0.54
    static let closeToCenterCleanupDelay = closeToCenterDuration + 0.04

    static let coverAspectRatio: CGFloat = 1.45
    static let readerLoadingChromeGraceSeconds: Double = 0.25
    static let pageTransitionDuration: Double = 0.24

    /// Hide the leaf before the -104° hinge crosses 90°. A partially visible
    /// rear face projects left of the spine as a flat gray slab.
    static func coverLeafOpacity(flipProgress: Double) -> Double {
        let t = min(max(flipProgress, 0), 1)
        return min(1, max(0, (0.82 - t) / 0.18))
    }

    /// At the shelf endpoint only the actual cover may remain, not a shaded
    /// rectangular paper backing underneath it.
    static func pageBackingOpacity(progress: Double) -> Double {
        min(1, max(0, progress / 0.12))
    }

    static func dimmingOpacity(progress: Double) -> Double {
        0.24 * min(max(progress, 0), 1)
    }

    static func flightShadow(progress: Double, isDark: Bool) -> (opacity: Double, radius: Double, y: Double) {
        let t = min(max(progress, 0), 1)
        let flightArc = (t <= 0 || t >= 1) ? 0.0 : sin(t * .pi)
        let restingOpacity = isDark ? 0.0 : 0.12
        return (
            opacity: restingOpacity * (1.0 - t) + 0.22 * flightArc,
            radius: 5.0 * (1.0 - t) + 20.0 * flightArc,
            y: 2.0 * (1.0 - t) + 10.0 * flightArc
        )
    }

    static func shadowBackdropInset(progress: Double) -> CGFloat {
        let t = min(max(progress, 0), 1)
        return 1.5 * CGFloat(max(0.0, 1.0 - t / 0.12))
    }

    /// Evaluates the cubic Bezier timing curve `(0.22, 1.0, 0.36, 1.0)` so
    /// CADisplayLink-driven open and close transitions share the exact curve at
    /// 60Hz, 120Hz, and any higher display refresh rate.
    static func timingCurveValue(at fraction: Double) -> Double {
        let x = min(max(fraction, 0), 1)
        if x <= 0 { return 0 }
        if x >= 1 { return 1 }
        let p1x = 0.22
        let p2x = 0.36
        let cx = 3.0 * p1x
        let bx = 3.0 * (p2x - p1x) - cx
        let ax = 1.0 - cx - bx

        var s = x
        for _ in 0..<6 {
            let currentX = ((ax * s + bx) * s + cx) * s - x
            let derivative = (3.0 * ax * s + 2.0 * bx) * s + cx
            if abs(currentX) < 1e-7 { break }
            if abs(derivative) < 1e-6 { break }
            s = min(max(s - currentX / derivative, 0), 1)
        }
        // P1.y = 1.0, P2.y = 1.0 => cy = 3, by = -3, ay = 1
        let y = ((1.0 * s - 3.0) * s + 3.0) * s * s
        return min(max(y, 0), 1)
    }

    static func frameProgress(elapsed: Double, duration: Double, from: Double, to: Double) -> Double {
        let u = duration > 0 ? min(max(elapsed / duration, 0), 1) : 1
        let eased = timingCurveValue(at: u)
        return from + (to - from) * eased
    }

    static func initialFrameStep(targetTimestamp: Double, timestamp: Double) -> Double {
        let delta = targetTimestamp - timestamp
        guard delta.isFinite, delta > 0 else { return 1.0 / 120.0 }
        return min(max(delta, 0.001), 1.0 / 30.0)
    }

    static func preferredFrameRateBounds(maximumFramesPerSecond: Double) -> (minimum: Float, maximum: Float, preferred: Float) {
        let safeMax = Float(max(1.0, maximumFramesPerSecond.isFinite ? maximumFramesPerSecond : 60.0))
        let safeMin = min(Float(60.0), safeMax)
        return (minimum: safeMin, maximum: safeMax, preferred: safeMax)
    }

    static func shouldHideSourceCover(
        openingSourceKey: String?,
        readerPresentationActive: Bool,
        closingSourceKey: String?,
        forKey key: String
    ) -> Bool {
        (openingSourceKey == key && readerPresentationActive) || closingSourceKey == key
    }

    static func openAnimation(
        source: NativeBookOpenSource,
        animated: Bool,
        reduceMotion: Bool,
        powerSaving: Bool,
        pageAnimation: Int
    ) -> NativeBookOpenAnimation {
        guard animated, !reduceMotion, !powerSaving else { return .none }
        if source == .library || source == .dashboard { return .cover }
        if source == .widget || source == .continueReading || source == .materials { return .slide }
        switch pageAnimation {
        case 0: return .none
        case 1: return .fade
        default: return .slide
        }
    }

    static func closeAnimation(
        source: NativeBookOpenSource,
        animated: Bool,
        reduceMotion: Bool,
        powerSaving: Bool,
        pageAnimation: Int
    ) -> NativeBookOpenAnimation {
        openAnimation(
            source: source,
            animated: animated,
            reduceMotion: reduceMotion,
            powerSaving: powerSaving,
            pageAnimation: pageAnimation
        )
    }

    static func centeredFrame(in viewport: CGRect) -> CGRect {
        let safeW = max(120, viewport.width.isFinite ? viewport.width : 390)
        let safeH = max(200, viewport.height.isFinite ? viewport.height : 844)
        let originX = viewport.origin.x.isFinite ? viewport.origin.x : 0
        let originY = viewport.origin.y.isFinite ? viewport.origin.y : 0
        let maxW = min(safeW * 0.44, 188)
        let maxH = safeH * 0.46
        let width = max(84, min(maxW, maxH / coverAspectRatio))
        let height = width * coverAspectRatio
        return CGRect(
            x: originX + (safeW - width) * 0.5,
            y: originY + (safeH - height) * 0.5,
            width: width,
            height: height
        )
    }

    /// Ensures a tracked cover frame is finite, non-trivial, and genuinely
    /// visible inside the usable viewport (not scrolled off-screen or buried
    /// behind the top safe area / bottom tab bar).
    static func isVisibleFrame(
        _ frame: CGRect,
        in viewport: CGRect,
        topInset: CGFloat = 40,
        bottomInset: CGFloat = 52
    ) -> Bool {
        guard frame.width > 12, frame.height > 16,
              frame.origin.x.isFinite, frame.origin.y.isFinite,
              frame.width.isFinite, frame.height.isFinite,
              viewport.width > 120, viewport.height > 160 else {
            return false
        }
        let usableHeight = max(80, viewport.height - topInset - bottomInset)
        let usable = CGRect(
            x: viewport.minX,
            y: viewport.minY + topInset,
            width: viewport.width,
            height: usableHeight
        )
        let visible = frame.intersection(usable)
        guard !visible.isNull, !visible.isEmpty else { return false }
        let area = frame.width * frame.height
        let visibleArea = visible.width * visible.height
        return visibleArea / area >= 0.45
    }

    static func resolveOpenTarget(
        bookId: String,
        explicitFrame: CGRect?,
        explicitKey: String?,
        activeTab: Int,
        viewport: CGRect,
        lookup: (String) -> CGRect?,
        seriesFrontBookId: String? = nil,
        topInset: CGFloat = 40,
        bottomInset: CGFloat = 52
    ) -> NativeBookTransitionTarget {
        if let explicitFrame, isVisibleFrame(explicitFrame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
            if let explicitKey {
                return .cover(frame: explicitFrame, key: explicitKey)
            }
            return .cover(frame: explicitFrame, key: "shelf:\(bookId)")
        }
        if let key = explicitKey, let frame = lookup(key), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
            return .cover(frame: frame, key: key)
        }
        if let detail = lookup("detail:\(bookId)"), isVisibleFrame(detail, in: viewport, topInset: topInset, bottomInset: bottomInset) {
            return .cover(frame: detail, key: "detail:\(bookId)")
        }
        if activeTab == 0 {
            let candidates = libraryCandidateKeys(bookId: bookId, seriesFrontBookId: seriesFrontBookId)
            for key in candidates {
                if let frame = lookup(key), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
                    return .cover(frame: frame, key: key)
                }
            }
        } else if activeTab == 2 {
            for key in ["dashboardRecent:\(bookId)", "dashboardMostRead:\(bookId)"] {
                if let frame = lookup(key), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
                    return .cover(frame: frame, key: key)
                }
            }
        }
        return .centered(frame: centeredFrame(in: viewport))
    }

    static func resolveCloseTarget(
        bookId: String,
        preferredKey: String?,
        activeTab: Int,
        viewport: CGRect,
        lookup: (String) -> CGRect?,
        seriesFrontBookId: String? = nil,
        topInset: CGFloat = 40,
        bottomInset: CGFloat = 52
    ) -> NativeBookTransitionTarget {
        if let preferredKey {
            let allowPreferred = preferredKey.hasPrefix("detail:")
                || activeTab == 0
                || (activeTab == 1 && (preferredKey.hasPrefix("materials:") || preferredKey.hasPrefix("materialsDetail:")))
                || (activeTab == 2 && (preferredKey.hasPrefix("dashboardRecent:") || preferredKey.hasPrefix("dashboardMostRead:")))
            if allowPreferred, let frame = lookup(preferredKey), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
                return .cover(frame: frame, key: preferredKey)
            }
        }
        if let detail = lookup("detail:\(bookId)"), isVisibleFrame(detail, in: viewport, topInset: topInset, bottomInset: bottomInset) {
            return .cover(frame: detail, key: "detail:\(bookId)")
        }
        if activeTab == 0 {
            let candidates = libraryCandidateKeys(bookId: bookId, seriesFrontBookId: seriesFrontBookId)
            for key in candidates {
                if let frame = lookup(key), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
                    return .cover(frame: frame, key: key)
                }
            }
        } else if activeTab == 1 {
            // When closing back into the pushed detail view (`materialsDetail:`), its
            // scrolled-off header must fold to center rather than shrinking onto the
            // covered root list cover (`materials:`) underneath the navigation stack.
            let keys = preferredKey?.hasPrefix("materialsDetail:") == true
                ? ["materialsDetail:\(bookId)"]
                : ["materialsDetail:\(bookId)", "materials:\(bookId)"]
            for key in keys {
                if let frame = lookup(key), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
                    return .cover(frame: frame, key: key)
                }
            }
        } else if activeTab == 2 {
            for key in ["dashboardRecent:\(bookId)", "dashboardMostRead:\(bookId)"] {
                if let frame = lookup(key), isVisibleFrame(frame, in: viewport, topInset: topInset, bottomInset: bottomInset) {
                    return .cover(frame: frame, key: key)
                }
            }
        }
        return .centered(frame: centeredFrame(in: viewport))
    }

    /// Prevents the reader's top toolbar (which contains the search button)
    /// from flashing during the brief initial loading window after opening a book.
    static func shouldShowReaderTopBar(
        chromeVisible: Bool,
        ready: Bool,
        searching: Bool,
        hasError: Bool,
        loadingChromeVisible: Bool
    ) -> Bool {
        if chromeVisible || searching { return true }
        return !ready && (hasError || loadingChromeVisible)
    }

    /// Keeps the status bar untouched while a transparent 3D cover-open
    /// transition is active over the library, preventing the underlying shelf
    /// or full-screen host from shifting vertically at t = 0.
    static func shouldHideReaderStatusBar(
        chromeVisible: Bool,
        hasOpenTransition: Bool
    ) -> Bool {
        !chromeVisible && !hasOpenTransition
    }

    private static func libraryCandidateKeys(bookId: String, seriesFrontBookId: String?) -> [String] {
        var keys = ["shelf:\(bookId)"]
        if let frontId = seriesFrontBookId, frontId != bookId {
            keys.append("shelf:\(frontId)")
        }
        keys.append("continue:\(bookId)")
        return keys
    }
}
