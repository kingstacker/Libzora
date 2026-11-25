import Foundation
import CoreGraphics

/// Window-based geometry shared by the shelf, its cover prefetcher and reader.
/// No screen-size or orientation assumptions: iPad windows can resize without
/// changing the device's size class or rebuilding a navigation stack.
enum NativeAdaptiveLayout {
    static let pageInset: CGFloat = 24
    static let mastheadHeight: CGFloat = 44
    static let mastheadTopInset: CGFloat = 16
    static let readerMaxWidth: CGFloat = 840
    static let readerSpreadMaxWidth: CGFloat = 1240
    static let readerToolsMaxWidth: CGFloat = 720
    static let libraryFilterMaxWidth: CGFloat = 520
    static let libraryBatchBarMaxWidth: CGFloat = 520
    static let noteMaxWidth: CGFloat = 560

    struct Grid: Equatable {
        let columns: Int
        let itemWidth: CGFloat
        let spacing: CGFloat
    }

    static func bookGrid(pageWidth: CGFloat) -> Grid {
        let width = finiteWidth(pageWidth)
        // Preserve the phone's compact three-column shelf. Let covers grow
        // gradually on wider windows instead of dropping columns at a breakpoint.
        let minimum = min(132, max(88, (width - pageInset * 2) / 5))
        return grid(pageWidth: width, minimumItemWidth: minimum, spacing: 16)
    }

    static func collectionGrid(pageWidth: CGFloat) -> Grid {
        let width = finiteWidth(pageWidth)
        let minimum = min(164, max(124, (width - pageInset * 2) / 4))
        return grid(pageWidth: width, minimumItemWidth: minimum, spacing: 14)
    }

    private static func grid(pageWidth: CGFloat, minimumItemWidth: CGFloat, spacing: CGFloat) -> Grid {
        let available = max(1, pageWidth - pageInset * 2)
        let columns = max(1, Int((available + spacing) / (minimumItemWidth + spacing)))
        let itemWidth = max(1, (available - CGFloat(columns - 1) * spacing) / CGFloat(columns))
        return Grid(columns: columns, itemWidth: itemWidth, spacing: spacing)
    }

    /// Every page shares the shelf's window edges; only reader content and
    /// standalone overlays have independent width limits.
    static func pageWidth(viewportWidth: CGFloat) -> CGFloat { finiteWidth(viewportWidth) }

    static func dashboardColumns(pageWidth: CGFloat) -> Int {
        let available = max(1, finiteWidth(pageWidth) - pageInset * 2)
        return min(4, max(1, Int((available + 10) / 160)))
    }

    static func readerWidth(viewportWidth: CGFloat, isPdf: Bool, isTwoPage: Bool = false) -> CGFloat {
        let width = finiteWidth(viewportWidth)
        // PDF zoom and page fitting still use the whole window.
        if isPdf { return width }
        return min(isTwoPage ? readerSpreadMaxWidth : readerMaxWidth, width)
    }

    static func isLandscapeSpread(
        viewportSize: CGSize,
        isPdf: Bool,
        isManga: Bool,
        verticalWriting: Bool,
        twoPageInLandscape: Bool
    ) -> Bool {
        guard !isPdf, !isManga, !verticalWriting, twoPageInLandscape,
              viewportSize.width.isFinite, viewportSize.height.isFinite,
              viewportSize.width >= 760, viewportSize.height > 0 else { return false }
        return viewportSize.width > viewportSize.height
    }

    static func noteWidth(viewportWidth: CGFloat) -> CGFloat {
        min(noteMaxWidth, max(0, finiteWidth(viewportWidth) - 16))
    }

    /// safeArea is expressed in the overlay's local coordinates. In particular,
    /// an iPad's top tab bar must not imply a phone-sized bottom exclusion zone.
    static func popupBounds(in size: CGSize, safeArea: CGRect, bottomBarInset: CGFloat = 0) -> CGRect {
        let top = max(12, safeArea.minY + 8)
        let bottom = max(top, min(size.height - max(12, bottomBarInset + 8), safeArea.maxY - 8))
        return CGRect(x: 12, y: top, width: max(0, size.width - 24), height: bottom - top)
    }

    /// Both rectangles are in the owning window's coordinates, not UIScreen's.
    /// Floating keyboards and keyboards outside a resized window reserve nothing.
    static func keyboardOverlap(windowBounds: CGRect, keyboardFrame: CGRect) -> CGFloat {
        let overlap = windowBounds.intersection(keyboardFrame)
        guard windowBounds.width > 0, !overlap.isNull,
              overlap.width >= windowBounds.width * 0.8,
              overlap.maxY >= windowBounds.maxY - 1 else { return 0 }
        return max(0, overlap.height)
    }

    struct SeriesPanel: Equatable {
        let width: CGFloat
        let height: CGFloat
        let coverWidth: CGFloat
        let headerHeight: CGFloat
        let rowHeight: CGFloat
    }

    static func seriesPanel(in size: CGSize, preferredCoverWidth: CGFloat,
                            headerHeight: CGFloat, metadataHeight: CGFloat) -> SeriesPanel {
        let availableHeight = max(0, size.height - 32)
        let header = min(max(44, headerHeight), max(0, availableHeight - 12))
        let coverRoom = max(44, (availableHeight - header - metadataHeight - 26) / 1.45)
        let cover = min(160, max(1, preferredCoverWidth), coverRoom)
        let row = min(cover * 1.45 + metadataHeight + 14, max(0, availableHeight - header - 12))
        return SeriesPanel(width: min(600, max(0, size.width - 32)),
                           height: min(availableHeight, header + row + 12),
                           coverWidth: cover, headerHeight: header, rowHeight: row)
    }

    static func readerSettingsHeight(maxHeight: CGFloat, isPdf: Bool, isTablet: Bool) -> CGFloat {
        if isTablet { return min(isPdf ? 400 : 420, maxHeight * 0.65) }
        return min(isPdf ? 340 : 340, maxHeight * 0.42)
    }

    private static func finiteWidth(_ width: CGFloat) -> CGFloat {
        width.isFinite ? max(0, width) : 0
    }
}
