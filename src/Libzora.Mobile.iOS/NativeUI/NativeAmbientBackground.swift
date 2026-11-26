import SwiftUI
import UIKit
import ImageIO

final class NativeAmbientState: ObservableObject {
    private static let cacheKey = "libzora.ambient.lastPalette.v1"

    private struct CachedPalette: Codable, Equatable {
        let visible: [NativeAmbientColor]
        let fallback: [NativeAmbientColor]
    }

    @Published var visibleColors: [NativeAmbientColor] = [] {
        didSet { if !restoring && oldValue != visibleColors { persistCache() } }
    }
    @Published var fallbackColors: [NativeAmbientColor] = [] {
        didSet { if !restoring && oldValue != fallbackColors { persistCache() } }
    }
    /// Shared per-cover color cache so launch pre-warming and `NativeLibraryPage`
    /// reuse the same extracted samples without a second decode pass or delayed palette jump.
    var sampledCoverColors: [NativeVisibleCover: NativeAmbientColor] = [:]
    private var restoring = false

    var colors: [NativeAmbientColor] {
        visibleColors.isEmpty ? fallbackColors : visibleColors
    }

    init() {
        guard let data = UserDefaults.standard.data(forKey: Self.cacheKey),
              let cached = try? JSONDecoder().decode(CachedPalette.self, from: data) else { return }
        restoring = true
        visibleColors = cached.visible
        fallbackColors = cached.fallback
        restoring = false
    }

    func clear() {
        restoring = true
        if !visibleColors.isEmpty { visibleColors = [] }
        if !fallbackColors.isEmpty { fallbackColors = [] }
        restoring = false
        sampledCoverColors.removeAll()
        UserDefaults.standard.removeObject(forKey: Self.cacheKey)
    }

    private func persistCache() {
        if visibleColors.isEmpty && fallbackColors.isEmpty {
            UserDefaults.standard.removeObject(forKey: Self.cacheKey)
            return
        }
        let payload = CachedPalette(visible: visibleColors, fallback: fallbackColors)
        if let data = try? JSONEncoder().encode(payload) {
            UserDefaults.standard.set(data, forKey: Self.cacheKey)
        }
    }
}

/// Small RGB values can cross the background decoding task without carrying UI objects.
struct NativeAmbientColor: Equatable, Codable {
    let red: Double
    let green: Double
    let blue: Double
    var strength: Double = 1
    var color: Color { Color(red: red, green: green, blue: blue) }
}

struct NativeAmbientSample {
    let color: NativeAmbientColor
    let column: Int
    let row: Int
    let visibility: Double
}

enum NativeAmbientPalette {
    static let fieldCenters: [UnitPoint] = (0..<9).map {
        UnitPoint(x: (Double($0 % 3) + 0.5) / 3, y: (Double($0 / 3) + 0.5) / 3)
    }

    /// Each fixed field follows nearby covers; colours never carry scroll coordinates.
    static func regionalColors(_ samples: [NativeAmbientSample]) -> [NativeAmbientColor] {
        guard !samples.isEmpty else { return [] }
        return (0..<9).map { index in
            let column = index % 3, row = index / 3
            return samples.max { left, right in
                func score(_ sample: NativeAmbientSample) -> Double {
                    let dx = Double(sample.column - column), dy = Double(sample.row - row)
                    return sample.visibility / (0.2 + dx * dx + dy * dy)
                }
                return score(left) < score(right)
            }!.color
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

        /// Area dominates: a tiny bright title must not outweigh the cover artwork.
        var score: Double {
            Double(count) * (0.8 + saturation * 0.2) * (0.8 + brightness * 0.2)
        }

        /// Covers are often printed in shadow. Lift the sample into a pastel
        /// range so the wash reads as colour instead of turning muddy.
        var vividColor: NativeAmbientColor {
            let saturation = min(0.95, max(0.55, self.saturation * 1.1))
            let brightness = min(1, max(0.88, self.brightness * 1.15 + 0.12))
            let rgb = NativeAmbientPalette.rgb(hue: hue, saturation: saturation, brightness: brightness)
            return NativeAmbientColor(red: rgb.red, green: rgb.green, blue: rgb.blue)
        }

        /// Used only when the whole shelf is black and white, where forcing
        /// saturation would invent a hue the books don't have.
        var mutedColor: NativeAmbientColor {
            let peak = max(red, green, blue)
            let scale = peak == 0 ? 1 : min(2.5, 0.75 / peak)
            return NativeAmbientColor(red: min(1, red * scale), green: min(1, green * scale), blue: min(1, blue * scale), strength: 0.12)
        }
    }

    /// Read small thumbnails from actual library covers; no cover image is retained.
    static func extract(paths: [String]) -> [NativeAmbientColor] {
        var picked: [NativeAmbientColor] = []
        var neutral: [NativeAmbientColor] = []
        for path in paths {
            if picked.count == 4 { break }
            guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 32,
                    kCGImageSourceShouldCache: false
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
                // Keep neutral samples for genuinely black-and-white covers;
                // the vivid candidate pass below still prefers coloured artwork.
                let high = max(r, g, b), low = min(r, g, b)
                let saturation = high == 0 ? 0 : Double(high - low) / Double(high)
                // Group shades of the same hue so shadows/highlights do not
                // split a dominant colour into many small RGB buckets.
                let key: Int
                if saturation >= 0.22 && high >= 38 {
                    key = Int(hue(red: Double(r), green: Double(g), blue: Double(b)) * 24) % 24
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
                $0.saturation >= 0.22 && $0.brightness >= 0.15
                    && Double($0.count) >= Double(pixelCount) * 0.06
            }
            if let best = vivid.max(by: { $0.score < $1.score }) {
                picked.append(best.vividColor)
            } else if let quiet = candidates.max(by: { $0.score < $1.score }) {
                neutral.append(quiet.mutedColor)
            }
        }
        // Neutral covers are a fallback, so ink and paper never muddy a colourful wash.
        return picked.isEmpty ? Array(neutral.prefix(4)) : picked
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

    /// Renders the same ambient wash as `NativeAmbientWash` into a UIImage so
    /// UIKit transition snapshots (3D book close, widget slide handoffs) keep
    /// the colorful wash over opaque Metal reader pixels.
    static func renderWashImage(colors: [NativeAmbientColor], size: CGSize) -> UIImage? {
        guard !colors.isEmpty, size.width > 1, size.height > 1 else { return nil }
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = false
        return UIGraphicsImageRenderer(size: size, format: format).image { context in
            let cg = context.cgContext
            let colorSpace = CGColorSpaceCreateDeviceRGB()
            let middle = colors[colors.count / 2]
            let linearColors: [CGColor] = [
                UIColor(red: colors[0].red, green: colors[0].green, blue: colors[0].blue, alpha: 0.12 * colors[0].strength).cgColor,
                UIColor(red: middle.red, green: middle.green, blue: middle.blue, alpha: 0.10 * middle.strength).cgColor,
                UIColor(red: colors[colors.count - 1].red, green: colors[colors.count - 1].green, blue: colors[colors.count - 1].blue, alpha: 0.12 * colors[colors.count - 1].strength).cgColor
            ]
            let linearLocations: [CGFloat] = [0, 0.5, 1]
            if let linear = CGGradient(colorsSpace: colorSpace, colors: linearColors as CFArray, locations: linearLocations) {
                cg.drawLinearGradient(
                    linear,
                    start: .zero,
                    end: CGPoint(x: size.width, y: size.height),
                    options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
                )
            }
            let radius = max(size.width * 0.55, size.height * 0.30)
            let anchors = colors.count == 9 ? fieldCenters : [
                UnitPoint(x: 0.15, y: 0.12), UnitPoint(x: 0.85, y: 0.4),
                UnitPoint(x: 0.15, y: 0.72), UnitPoint(x: 0.8, y: 0.95)
            ]
            let radialLocations: [CGFloat] = [0, 0.55, 1]
            for (index, item) in colors.enumerated() {
                let anchor = anchors[index % anchors.count]
                let center = CGPoint(x: size.width * anchor.x, y: size.height * anchor.y)
                let radialColors: [CGColor] = [
                    UIColor(red: item.red, green: item.green, blue: item.blue, alpha: 0.9 * item.strength).cgColor,
                    UIColor(red: item.red, green: item.green, blue: item.blue, alpha: 0.62 * item.strength).cgColor,
                    UIColor(red: item.red, green: item.green, blue: item.blue, alpha: 0).cgColor
                ]
                if let radial = CGGradient(colorsSpace: colorSpace, colors: radialColors as CFArray, locations: radialLocations) {
                    cg.drawRadialGradient(radial, startCenter: center, startRadius: 0, endCenter: center, endRadius: radius, options: [])
                }
            }
        }
    }
}

/// Screen-anchored fields use the library palette, independent of scroll geometry.
struct NativeAmbientWash: View {
    @EnvironmentObject private var store: NativeStore
    var colorsOverride: [NativeAmbientColor]? = nil
    var body: some View {
        NativeAmbientWashBody(ambient: store.ambient, colorsOverride: colorsOverride)
    }
}

private struct NativeAmbientWashBody: View {
    @ObservedObject var ambient: NativeAmbientState
    let colorsOverride: [NativeAmbientColor]?
    var body: some View {
        GeometryReader { geometry in
            let colors = colorsOverride ?? ambient.colors
            if !colors.isEmpty {
                let radius = max(geometry.size.width * 0.55, geometry.size.height * 0.30)
                let anchors = colors.count == 9 ? NativeAmbientPalette.fieldCenters : [
                    UnitPoint(x: 0.15, y: 0.12), UnitPoint(x: 0.85, y: 0.4),
                    UnitPoint(x: 0.15, y: 0.72), UnitPoint(x: 0.8, y: 0.95)
                ]
                let middle = colors[colors.count / 2]
                ZStack {
                    // A low-opacity base keeps the corners and the gaps between
                    // the radial fields tinted instead of fading to plain white.
                    LinearGradient(stops: [
                        .init(color: colors[0].color.opacity(0.12 * colors[0].strength), location: 0),
                        .init(color: middle.color.opacity(0.10 * middle.strength), location: 0.5),
                        .init(color: colors[colors.count - 1].color.opacity(0.12 * colors[colors.count - 1].strength), location: 1)
                    ], startPoint: .topLeading, endPoint: .bottomTrailing)
                    ForEach(Array(colors.enumerated()), id: \.offset) { index, color in
                        Circle().fill(RadialGradient(gradient: Gradient(stops: [
                            .init(color: color.color.opacity(0.9 * color.strength), location: 0),
                            .init(color: color.color.opacity(0.62 * color.strength), location: 0.55),
                            .init(color: color.color.opacity(0), location: 1)
                        ]), center: .center, startRadius: 0, endRadius: radius))
                            .frame(width: radius * 2, height: radius * 2)
                            .position(x: geometry.size.width * anchors[index % anchors.count].x,
                                      y: geometry.size.height * anchors[index % anchors.count].y)
                    }
                }
                .frame(width: geometry.size.width, height: geometry.size.height)
                .clipped()
                // Rasterize the 9 radial gradients into one Metal texture so
                // 120Hz shelf scrolling does not re-blend them every frame.
                .drawingGroup(opaque: false)
            }
        }.allowsHitTesting(false).accessibilityHidden(true)
    }
}

struct NativeAmbientBackground: View {
    @EnvironmentObject private var store: NativeStore
    let fallback: Color
    let colorsOverride: [NativeAmbientColor]?
    init(fallback: Color = NativeTheme.page, colorsOverride: [NativeAmbientColor]? = nil) {
        self.fallback = fallback; self.colorsOverride = colorsOverride
    }
    var body: some View {
        NativeAmbientBackgroundBody(
            colorfulMode: store.preferences.colorfulMode,
            animateChanges: !store.launchBrandActive && !store.preparingLibrary,
            ambient: store.ambient,
            fallback: fallback,
            colorsOverride: colorsOverride)
    }
}

private struct NativeAmbientBackgroundBody: View {
    @Environment(\.colorScheme) private var scheme
    let colorfulMode: Bool
    let animateChanges: Bool
    @ObservedObject var ambient: NativeAmbientState
    let fallback: Color
    let colorsOverride: [NativeAmbientColor]?
    var body: some View {
        let colors = colorsOverride ?? ambient.colors
        ZStack {
            fallback
            if colorfulMode && !colors.isEmpty {
                NativeAmbientWash(colorsOverride: colors).opacity(scheme == .dark ? 0.36 : 0.34)
            }
        }.animation(animateChanges ? .easeInOut(duration: 0.8) : nil, value: colors)
            .ignoresSafeArea().allowsHitTesting(false).accessibilityHidden(true)
    }
}

@MainActor
extension NativeStore {
    /// Pre-extracts the initial ambient palette during cold launch while the
    /// launch brand cover is still on screen, so neither the library nor a
    /// widget target slides in white and flashes into colorful mode afterward.
    func startInitialAmbientPreparation(viewport: CGSize) {
        initialAmbientTask?.cancel()
        guard preferences.colorfulMode else {
            ambient.clear()
            initialAmbientTask = nil
            return
        }
        guard !library.isEmpty else {
            ambient.clear()
            initialAmbientTask = nil
            return
        }

        let fallbackPaths: [String] = library
            .filter { $0.coverPath != nil }
            .sorted { $0.updatedAt == $1.updatedAt ? $0.id < $1.id : $0.updatedAt > $1.updatedAt }
            .prefix(18)
            .compactMap(\.coverPath)

        var initialRegions: [NativeAmbientCoverRegion] = []
        if !preferences.collectionsView {
            let sorted = NativeBookOrder.sorted(library, sort: preferences.sort)
            let displayed: [LibraryBook] = preferences.stackBookSeries
                ? NativeBookSeries.entries(sorted).compactMap { entry in
                    switch entry {
                    case .book(let book): return book
                    case .series(let series): return frontBook(for: series)
                    }
                }
                : sorted
            let effectiveWidth = viewport.width > 10 ? viewport.width : viewportRect.width
            let effectiveHeight = viewport.height > 100 ? max(1, viewport.height - 49) : max(1, viewportRect.height - 49)
            let pageWidth = NativeAdaptiveLayout.pageWidth(viewportWidth: effectiveWidth)
            let hasContinue = continueBookId.flatMap { id in library.first { $0.id == id } } != nil
            let headerHeight: CGFloat = 124 + (hasContinue ? 92 : 0)
            let listView = preferences.listView
            let gallery = preferences.galleryMode && !listView
            let cols = listView ? 1 : max(1, NativeBookShelf.columnCount(pageWidth: pageWidth))
            let coverW = NativeBookShelf.coverWidth(pageWidth: pageWidth)
            let rowStride: CGFloat = listView ? 86 : coverW * 1.45 + (gallery ? 18 : 74)
            let totalRows = (displayed.count + cols - 1) / cols
            if totalRows > 0 {
                let lastRow = max(0, min(totalRows - 1, Int(floor((effectiveHeight - headerHeight) / rowStride))))
                initialRegions.reserveCapacity(9)
                for rowIdx in 0...lastRow {
                    let rowTop = headerHeight + CGFloat(rowIdx) * rowStride
                    let rowBottom = rowTop + rowStride
                    let visibleTop = max(0, rowTop)
                    let visibleBottom = min(effectiveHeight, rowBottom)
                    let fraction = Double(max(0, visibleBottom - visibleTop)) / Double(max(1, rowStride))
                    let rowCenterY = (visibleTop + visibleBottom) / 2
                    let bandRow = min(2, max(0, Int(rowCenterY / effectiveHeight * 3)))
                    let visibility = max(0.25, (fraction * 4).rounded() / 4)
                    for colIdx in 0..<cols {
                        let idx = rowIdx * cols + colIdx
                        guard idx < displayed.count else { break }
                        guard let path = displayed[idx].coverPath else { continue }
                        let bandCol = listView || cols <= 1 ? 1 : min(2, colIdx * 3 / cols)
                        initialRegions.append(NativeAmbientCoverRegion(
                            cover: NativeVisibleCover(path: path, version: displayed[idx].updatedAt),
                            column: bandCol,
                            row: bandRow,
                            visibility: visibility
                        ))
                        if initialRegions.count == 9 { break }
                    }
                    if initialRegions.count == 9 { break }
                }
            }
        }

        let existingSamples = ambient.sampledCoverColors
        let regionsSnapshot = initialRegions
        let task = Task { @MainActor [weak self] in
            let result = await Task.detached(priority: .userInitiated) {
                () -> (fallback: [NativeAmbientColor], samples: [(NativeVisibleCover, NativeAmbientColor)], regional: [NativeAmbientColor]) in
                let fallback = NativeAmbientPalette.extract(paths: fallbackPaths)
                var extracted: [(NativeVisibleCover, NativeAmbientColor)] = []
                var lookup = existingSamples
                for region in regionsSnapshot where lookup[region.cover] == nil {
                    if let color = NativeAmbientPalette.extract(paths: [region.cover.path]).first {
                        lookup[region.cover] = color
                        extracted.append((region.cover, color))
                    }
                }
                let ambientSamples = regionsSnapshot.compactMap { region -> NativeAmbientSample? in
                    guard let color = lookup[region.cover] else { return nil }
                    return NativeAmbientSample(
                        color: color,
                        column: region.column,
                        row: region.row,
                        visibility: region.visibility
                    )
                }
                let regional = NativeAmbientPalette.regionalColors(ambientSamples)
                return (fallback, extracted, regional)
            }.value
            guard let self, !Task.isCancelled else { return }
            defer { self.initialAmbientTask = nil }
            guard self.preferences.colorfulMode else { return }
            for (cover, color) in result.samples {
                self.ambient.sampledCoverColors[cover] = color
            }
            if self.ambient.fallbackColors != result.fallback {
                self.ambient.fallbackColors = result.fallback
            }
            if !result.regional.isEmpty && self.ambient.visibleColors != result.regional {
                self.ambient.visibleColors = result.regional
            }
        }
        initialAmbientTask = task
    }

    func waitForInitialAmbientColorsIfNeeded(maxWaitNanoseconds: UInt64 = 1_500_000_000) async {
        guard preferences.colorfulMode else { return }
        // If the last session's palette was already restored from cache, we do
        // not need to block launch on cover decoding — the background task will
        // still refresh the palette if any covers changed since last launch.
        if ambient.colors.isEmpty, let task = initialAmbientTask {
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await task.value }
                group.addTask { try? await Task.sleep(nanoseconds: maxWaitNanoseconds) }
                _ = await group.next()
                group.cancelAll()
            }
        }
        // Let SwiftUI commit the settled palette while the launch brand cover
        // is still active (animateChanges == false), so dismissing the cover
        // never coalesces with an initial empty -> colorful fade-in animation.
        if !ambient.colors.isEmpty {
            await NativeDisplayFrame.waitForCommit()
        }
    }
}
