import SwiftUI
import UIKit
import ImageIO

/// Small RGB values can cross the background decoding task without carrying UI objects.
struct NativeAmbientColor: Equatable {
    let red: Double
    let green: Double
    let blue: Double
    var strength: Double = 1
    var color: Color { Color(red: red, green: green, blue: blue) }
}

struct NativeAmbientSample {
    let color: NativeAmbientColor
    let x: Double
    let y: Double
    let visibility: Double
}

enum NativeAmbientPalette {
    static let fieldColumns = 3
    static let fieldRows = 8
    static let fieldCenters: [UnitPoint] = (0..<(fieldColumns * fieldRows)).map { index in
        let column = index % fieldColumns
        let row = index / fieldColumns
        return UnitPoint(x: (Double(column) + 0.5) / Double(fieldColumns),
                         y: 0.06 + Double(row) * (0.90 / Double(fieldRows - 1)))
    }

    /// Distance-weighted blending across visible cover positions preserves every row and column band smoothly.
    static func regionalColors(_ samples: [NativeAmbientSample]) -> [NativeAmbientColor] {
        guard !samples.isEmpty else { return [] }
        return fieldCenters.map { anchor in
            var red = 0.0
            var green = 0.0
            var blue = 0.0
            var strength = 0.0
            var totalWeight = 0.0
            for sample in samples {
                let dx = (sample.x - Double(anchor.x)) * 1.15
                let dy = (sample.y - Double(anchor.y)) * 1.85
                let weight = sample.visibility / pow(0.0014 + dx * dx + dy * dy, 2.4)
                red += sample.color.red * weight
                green += sample.color.green * weight
                blue += sample.color.blue * weight
                strength += sample.color.strength * weight
                totalWeight += weight
            }
            guard totalWeight > 0 else { return samples[0].color }
            return NativeAmbientColor(red: red / totalWeight,
                                      green: green / totalWeight,
                                      blue: blue / totalWeight,
                                      strength: strength / totalWeight)
        }
    }

    /// One bucket of the cover thumbnail, kept in normalised RGB plus its hue.
    private struct Candidate {
        let red: Double
        let green: Double
        let blue: Double
        let hue: Double
        let saturation: Double
        let brightness: Double
        let count: Int

        /// Balance cover area with colour purity so dominant artwork beats paper borders.
        var score: Double {
            Double(count) * (0.45 + saturation * 0.55) * (0.70 + brightness * 0.30)
        }

        /// Preserve each book's true hue and richness while lifting near-black shadows gently.
        var vividColor: NativeAmbientColor {
            let saturation = min(0.95, self.saturation <= 0.22 ? self.saturation * 1.15 : self.saturation * 1.08)
            let brightness = min(0.95, max(0.54, self.brightness * 1.12 + 0.08))
            let rgb = NativeAmbientPalette.rgb(hue: hue, saturation: saturation, brightness: brightness)
            return NativeAmbientColor(red: rgb.red, green: rgb.green, blue: rgb.blue)
        }

        /// Used only when the whole cover is near-achromatic so ink and paper never invent a fake hue.
        var mutedColor: NativeAmbientColor {
            let peak = max(red, green, blue)
            let scale = peak == 0 ? 1 : min(2.0, 0.72 / peak)
            return NativeAmbientColor(red: min(1, red * scale), green: min(1, green * scale), blue: min(1, blue * scale), strength: 0.16)
        }
    }

    /// Read small thumbnails from actual library covers; no cover image is retained.
    static func extract(paths: [String], maxColors: Int = 4) -> [NativeAmbientColor] {
        var picked: [NativeAmbientColor] = []
        var neutral: [NativeAmbientColor] = []
        for path in paths {
            if picked.count >= maxColors { break }
            guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 32
                  ] as CFDictionary) else { continue }
            let size = 32
            var pixels = [UInt8](repeating: 0, count: size * size * 4)
            let rendered = pixels.withUnsafeMutableBytes { bytes -> Bool in
                guard let context = CGContext(data: bytes.baseAddress, width: size, height: size,
                    bitsPerComponent: 8, bytesPerRow: size * 4, space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue) else { return false }
                context.draw(image, in: CGRect(x: 0, y: 0, width: size, height: size))
                return true
            }
            guard rendered else { continue }
            var buckets: [Int: (count: Int, red: Int, green: Int, blue: Int)] = [:]
            for index in stride(from: 0, to: pixels.count, by: 4) {
                guard pixels[index + 3] > 200 else { continue }
                let r = Int(pixels[index]), g = Int(pixels[index + 1]), b = Int(pixels[index + 2])
                let high = max(r, g, b), low = min(r, g, b)
                let saturation = high == 0 ? 0 : Double(high - low) / Double(high)
                // Keep 36 fine hue buckets so teal/blue and orange/amber/red stay true to the cover.
                let key: Int
                if saturation >= 0.11 && high >= 28 {
                    key = Int(hue(red: Double(r), green: Double(g), blue: Double(b)) * 36) % 36
                } else {
                    key = 100 + high / 32
                }
                let old = buckets[key] ?? (0, 0, 0, 0)
                buckets[key] = (old.count + 1, old.red + r, old.green + g, old.blue + b)
            }
            let candidates: [Candidate] = buckets.values.map { bucket in
                let divisor = Double(bucket.count) * 255
                let red = Double(bucket.red) / divisor
                let green = Double(bucket.green) / divisor
                let blue = Double(bucket.blue) / divisor
                let high = max(red, green, blue), low = min(red, green, blue)
                let saturation = high == 0 ? 0 : (high - low) / high
                return Candidate(red: red, green: green, blue: blue,
                                 hue: hue(red: red, green: green, blue: blue),
                                 saturation: saturation, brightness: high, count: bucket.count)
            }
            let pixelCount = candidates.reduce(0) { $0 + $1.count }
            let vivid = candidates.filter {
                $0.saturation >= 0.20 && $0.brightness >= 0.14
                    && Double($0.count) >= Double(pixelCount) * 0.045
            }
            let subtle = candidates.filter {
                $0.saturation >= 0.11 && $0.brightness >= 0.11
                    && Double($0.count) >= Double(pixelCount) * 0.05
            }
            if let best = vivid.max(by: { $0.score < $1.score }) ?? subtle.max(by: { $0.score < $1.score }) {
                picked.append(best.vividColor)
            } else if let quiet = candidates.max(by: { $0.count < $1.count }) {
                neutral.append(quiet.mutedColor)
            }
        }
        // Neutral covers are a fallback, so ink and paper never muddy a colourful wash.
        return picked.isEmpty ? Array(neutral.prefix(maxColors)) : picked
    }

    private static func hue(red: Double, green: Double, blue: Double) -> Double {
        let high = max(red, green, blue), low = min(red, green, blue)
        let delta = high - low
        guard delta > 0 else { return 0 }
        var value: Double
        if high == red { value = ((green - blue) / delta).truncatingRemainder(dividingBy: 6) }
        else if high == green { value = (blue - red) / delta + 2 }
        else { value = (red - green) / delta + 4 }
        value /= 6
        return value < 0 ? value + 1 : value
    }

    private static func rgb(hue: Double, saturation: Double, brightness: Double) -> (red: Double, green: Double, blue: Double) {
        let position = (hue - floor(hue)) * 6
        let sector = Int(floor(position)) % 6
        let fraction = position - floor(position)
        let p = brightness * (1 - saturation)
        let q = brightness * (1 - saturation * fraction)
        let t = brightness * (1 - saturation * (1 - fraction))
        switch sector {
        case 0: return (brightness, t, p)
        case 1: return (q, brightness, p)
        case 2: return (p, brightness, t)
        case 3: return (p, q, brightness)
        case 4: return (t, p, brightness)
        default: return (brightness, p, q)
        }
    }
}

/// Screen-anchored fields use the library palette, independent of scroll geometry.
struct NativeAmbientWash: View {
    @EnvironmentObject private var store: NativeStore
    var colorsOverride: [NativeAmbientColor]? = nil
    var body: some View {
        GeometryReader { geometry in
            let colors = colorsOverride ?? store.ambientColors
            if !colors.isEmpty {
                let gridField = colors.count == NativeAmbientPalette.fieldCenters.count
                let radius = gridField
                    ? max(geometry.size.width * 0.36, geometry.size.height * 0.16)
                    : max(geometry.size.width * 0.55, geometry.size.height * 0.30)
                let anchors = gridField ? NativeAmbientPalette.fieldCenters : [
                    UnitPoint(x: 0.15, y: 0.12), UnitPoint(x: 0.85, y: 0.4),
                    UnitPoint(x: 0.15, y: 0.72), UnitPoint(x: 0.8, y: 0.95)
                ]
                ZStack {
                    if gridField {
                        // A smooth vertical band gradient ensures every row keeps its own hue cleanly.
                        let rowStops: [Gradient.Stop] = (0..<NativeAmbientPalette.fieldRows).map { row in
                            let centerColor = colors[row * NativeAmbientPalette.fieldColumns + 1]
                            let location = 0.06 + Double(row) * (0.90 / Double(NativeAmbientPalette.fieldRows - 1))
                            return .init(color: centerColor.color.opacity(0.72 * centerColor.strength), location: location)
                        }
                        LinearGradient(stops: rowStops, startPoint: .top, endPoint: .bottom)
                    } else {
                        let middle = colors[colors.count / 2]
                        LinearGradient(stops: [
                            .init(color: colors[0].color.opacity(0.45 * colors[0].strength), location: 0),
                            .init(color: middle.color.opacity(0.35 * middle.strength), location: 0.5),
                            .init(color: colors[colors.count - 1].color.opacity(0.45 * colors[colors.count - 1].strength), location: 1)
                        ], startPoint: .topLeading, endPoint: .bottomTrailing)
                    }
                    let centerOpacity = gridField ? 0.38 : 0.82
                    let midOpacity = gridField ? 0.14 : 0.52
                    ForEach(Array(colors.enumerated()), id: \.offset) { index, color in
                        Circle().fill(RadialGradient(gradient: Gradient(stops: [
                            .init(color: color.color.opacity(centerOpacity * color.strength), location: 0),
                            .init(color: color.color.opacity(midOpacity * color.strength), location: 0.55),
                            .init(color: color.color.opacity(0), location: 1)
                        ]), center: .center, startRadius: 0, endRadius: radius))
                            .frame(width: radius * 2, height: radius * 2)
                            .position(x: geometry.size.width * anchors[index % anchors.count].x,
                                      y: geometry.size.height * anchors[index % anchors.count].y)
                    }
                }.frame(width: geometry.size.width, height: geometry.size.height).clipped()
            }
        }.allowsHitTesting(false).accessibilityHidden(true)
    }
}

struct NativeAmbientBackground: View {
    @EnvironmentObject private var store: NativeStore
    @Environment(\.colorScheme) private var scheme
    let fallback: Color
    let colorsOverride: [NativeAmbientColor]?
    init(fallback: Color = NativeTheme.page, colorsOverride: [NativeAmbientColor]? = nil) {
        self.fallback = fallback; self.colorsOverride = colorsOverride
    }
    var body: some View {
        let colors = colorsOverride ?? store.ambientColors
        ZStack {
            fallback
            if store.preferences.colorfulMode && !colors.isEmpty {
                NativeAmbientWash(colorsOverride: colors).opacity(scheme == .dark ? 0.45 : 0.44)
            }
        }.animation(.easeInOut(duration: 0.55), value: colors)
            .ignoresSafeArea().allowsHitTesting(false).accessibilityHidden(true)
    }
}
