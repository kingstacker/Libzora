import CoreGraphics
import Foundation

/// Resolved anchor for a book open or close transition.
/// `.cover` anchors directly to a live visible cover on the active screen;
/// `.centered` uses a balanced hero card in the middle of the viewport
/// (used for Home Screen widget entry and off-screen / cross-tab fallbacks).
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

/// Pure geometry and visibility rules shared by book open/close transitions
/// and verified by host-side logic tests.
enum NativeBookOpenTransitionPolicy {
    static let openFromCoverDuration: Double = 0.62
    static let openFromCoverMountDelay: Double = 0.58
    static let openFromCoverCleanupDelay: Double = 0.66

    static let openFromCenterDuration: Double = openFromCoverDuration
    static let openFromCenterMountDelay: Double = openFromCoverMountDelay
    static let openFromCenterCleanupDelay: Double = openFromCoverCleanupDelay

    static let closeToCoverDuration: Double = 0.53
    static let closeToCoverCleanupDelay: Double = 0.57

    static let closeToCenterDuration: Double = 0.48
    static let closeToCenterCleanupDelay: Double = 0.52

    static let coverAspectRatio: CGFloat = 1.45
    static let readerLoadingChromeGraceSeconds: Double = 0.25

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
        fromWidget: Bool,
        activeTab: Int,
        viewport: CGRect,
        lookup: (String) -> CGRect?,
        seriesFrontBookId: String? = nil,
        topInset: CGFloat = 40,
        bottomInset: CGFloat = 52
    ) -> NativeBookTransitionTarget {
        if fromWidget {
            return .centered(frame: centeredFrame(in: viewport))
        }
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
        } else if activeTab == 1 {
            for key in ["materialsDetail:\(bookId)", "materials:\(bookId)"] {
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

    private static func libraryCandidateKeys(bookId: String, seriesFrontBookId: String?) -> [String] {
        var keys = ["shelf:\(bookId)"]
        if let frontId = seriesFrontBookId, frontId != bookId {
            keys.append("shelf:\(frontId)")
        }
        keys.append("continue:\(bookId)")
        return keys
    }
}
