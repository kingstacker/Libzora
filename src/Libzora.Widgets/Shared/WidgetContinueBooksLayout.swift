import CoreGraphics

/// Fixed four-column shelf geometry for the medium (2x1) continue-reading widget.
/// Up to four books sit left-aligned in equal slots so covers and captions stay
/// aligned regardless of how many books are currently in progress.
struct KkContinueBooksLayout {
    static let horizontalPadding: CGFloat = 14
    static let verticalPadding: CGFloat = 12
    static let spacing: CGFloat = 10
    static let maxSlots = 4

    let slotCount: Int
    let slotWidth: CGFloat
    let coverWidth: CGFloat
    let coverHeight: CGFloat
    let slotHeight: CGFloat

    init(size: CGSize, bookCount: Int) {
        slotCount = min(Self.maxSlots, max(1, bookCount))
        let width = max(0, size.width - Self.horizontalPadding * 2)
        slotWidth = max(0, (width - Self.spacing * CGFloat(Self.maxSlots - 1)) / CGFloat(Self.maxSlots))
        let height = max(0, size.height - Self.verticalPadding * 2)
        let maxCoverHeight = max(0, height - 44)
        coverHeight = floor(min(maxCoverHeight, floor(slotWidth / 0.70)))
        coverWidth = floor(coverHeight * 0.70)
        slotHeight = maxCoverHeight > 0 ? maxCoverHeight + 40 : 0
    }
}
