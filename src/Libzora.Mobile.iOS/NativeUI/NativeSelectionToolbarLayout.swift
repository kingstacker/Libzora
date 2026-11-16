import CoreGraphics

struct NativeSelectionBounds: Decodable, Equatable {
    let x: Double
    let y: Double
    let bottom: Double
    let viewportWidth: Double
    let viewportHeight: Double
}

enum NativeSelectionToolbarLayout {
    static let height: CGFloat = 52
    static func width(in size: CGSize) -> CGFloat { min(360, max(0, size.width - 16)) }

    static func position(bounds: NativeSelectionBounds?, in size: CGSize,
                         chromeVisible: Bool, speechVisible: Bool) -> CGPoint {
        let width = width(in: size)
        let minTop: CGFloat = chromeVisible ? 76 : 8
        let bottomInset: CGFloat = (chromeVisible ? 160 : 24) + (speechVisible ? 70 : 0) + 8
        let maxTop = max(minTop, size.height - bottomInset - height)
        var left = (size.width - width) / 2
        var top = max(minTop, size.height / 2 - height - 10)
        if let bounds, bounds.viewportWidth > 0, bounds.viewportHeight > 0,
           bounds.x.isFinite, bounds.y.isFinite, bounds.bottom.isFinite,
           bounds.viewportWidth.isFinite, bounds.viewportHeight.isFinite {
            let scaleX = size.width / bounds.viewportWidth
            let scaleY = max(0, size.height - 24) / bounds.viewportHeight
            left = CGFloat(bounds.x) * scaleX
            top = CGFloat(bounds.y) * scaleY - height - 10
            if top < minTop {
                let below = CGFloat(bounds.bottom) * scaleY + 10
                top = below <= maxTop ? below : minTop
            }
        }
        left = min(max(8, left), max(8, size.width - width - 8))
        top = min(max(minTop, top), maxTop)
        return CGPoint(x: left + width / 2, y: top + height / 2)
    }
}
