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
    static let paletteHeight: CGFloat = 40
    static func width(in size: CGSize) -> CGFloat { min(360, max(0, size.width - 16)) }

    static func position(bounds: NativeSelectionBounds?, in size: CGSize,
                         chromeVisible: Bool, speechVisible: Bool) -> CGPoint {
        let width = width(in: size)
        let rect = barFrame(width: width, bounds: bounds, in: size,
                            chromeVisible: chromeVisible, speechVisible: speechVisible)
        return CGPoint(x: rect.midX, y: rect.midY)
    }

    static func palettePosition(bounds: NativeSelectionBounds?, in size: CGSize,
                                chromeVisible: Bool, speechVisible: Bool) -> CGPoint {
        let width = width(in: size)
        let bar = barFrame(width: width, bounds: bounds, in: size,
                           chromeVisible: chromeVisible, speechVisible: speechVisible)
        let minTop: CGFloat = chromeVisible ? 76 : 8
        let bottomInset: CGFloat = (chromeVisible ? 160 : 24) + (speechVisible ? 70 : 0) + 8
        let maxTop = max(minTop, size.height - bottomInset - paletteHeight)

        let placedBelowSelection: Bool = {
            guard let bounds, bounds.viewportHeight > 0, bounds.y.isFinite else { return false }
            let scaleY = max(0, size.height - 24) / bounds.viewportHeight
            return bar.midY >= CGFloat(bounds.y) * scaleY
        }()

        var top: CGFloat
        if placedBelowSelection {
            top = bar.maxY + 8
            if top > maxTop {
                top = max(minTop, bar.minY - 8 - paletteHeight)
            }
        } else {
            top = bar.minY - 8 - paletteHeight
            if top < minTop {
                let belowBar = bar.maxY + 8
                top = belowBar <= maxTop ? belowBar : minTop
            }
        }
        top = min(max(minTop, top), maxTop)
        return CGPoint(x: bar.midX, y: top + paletteHeight / 2)
    }

    private static func barFrame(width: CGFloat, bounds: NativeSelectionBounds?, in size: CGSize,
                                 chromeVisible: Bool, speechVisible: Bool) -> CGRect {
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
            // On phones where the 360pt bar spans nearly the full viewport width,
            // keep it horizontally centered so multi-line drags or initial word
            // selection never cause the pill to shift sideways after appearing.
            if size.width - width >= 80 {
                left = CGFloat(bounds.x) * scaleX
            }
            top = CGFloat(bounds.y) * scaleY - height - 10
            if top < minTop {
                let below = CGFloat(bounds.bottom) * scaleY + 10
                top = below <= maxTop ? below : minTop
            }
        }
        left = min(max(8, left), max(8, size.width - width - 8))
        top = min(max(minTop, top), maxTop)
        return CGRect(x: left, y: top, width: width, height: height)
    }
}
