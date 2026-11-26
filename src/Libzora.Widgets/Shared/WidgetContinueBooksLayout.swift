import CoreGraphics

/// Fixed widget chrome, flexible book slots. Keep the title, captions and
/// progress inside the actual widget bounds instead of assuming four books.
struct KkContinueBooksLayout {
    static let horizontalPadding: CGFloat = 16
    static let verticalPadding: CGFloat = 14
    static let spacing: CGFloat = 12

    let slotCount: Int
    let slotWidth: CGFloat
    let coverWidth: CGFloat
    let coverHeight: CGFloat

    init(size: CGSize, bookCount: Int) {
        slotCount = min(4, max(1, bookCount))
        let count = CGFloat(slotCount)
        let width = max(0, size.width - Self.horizontalPadding * 2)
        let gaps = Self.spacing * (count - 1)
        slotWidth = max(0, (width - gaps) / count)
        let height = max(0, size.height - Self.verticalPadding * 2)
        // Reserve a masthead and two caption lines before sizing the covers.
        coverHeight = floor(min(max(0, height - 60), min(72, slotWidth) / 0.70))
        coverWidth = floor(coverHeight * 0.70)
    }
}
