import CoreGraphics
import CoreText
import Foundation
import ImageIO
import SwiftUI
import WidgetKit

#if os(iOS)
import UIKit
#else
import AppKit
#endif

private struct KkUseSystemFontKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var kkUseSystemFont: Bool {
        get { self[KkUseSystemFontKey.self] }
        set { self[KkUseSystemFontKey.self] = newValue }
    }
}

private struct KkFontModifier: ViewModifier {
    @Environment(\.kkUseSystemFont) private var useSystemFont
    let size: CGFloat
    let style: Font.TextStyle
    let weight: Font.Weight

    func body(content: Content) -> some View {
        content.font(KkTheme.font(size, relativeTo: style, weight: weight, useSystemFont: useSystemFont))
    }
}

extension View {
    func kkTitleFont(_ size: CGFloat, relativeTo style: Font.TextStyle = .headline) -> some View {
        modifier(KkFontModifier(size: size, style: style, weight: .semibold))
    }

    func kkBodyFont(_ size: CGFloat, relativeTo style: Font.TextStyle = .caption) -> some View {
        modifier(KkFontModifier(size: size, style: style, weight: .regular))
    }

    func kkLabelFont(_ size: CGFloat, weight: Font.Weight = .semibold) -> some View {
        modifier(KkFontModifier(size: size, style: .caption, weight: weight))
    }

    func kkNumberFont(_ size: CGFloat, weight: Font.Weight = .semibold) -> some View {
        modifier(KkFontModifier(size: size, style: .title, weight: weight))
    }
}

/// Paper, ink and restrained greys keep the hierarchy consistent across widget
/// sizes. Book covers supply the colour; the surrounding chrome stays neutral.
enum KkTheme {
    static func page(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0.067, green: 0.067, blue: 0.067)
            : .white
    }

    static func ink(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0.929, green: 0.929, blue: 0.929)
            : Color(red: 0.102, green: 0.102, blue: 0.102)
    }

    static func secondaryInk(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0.69, green: 0.69, blue: 0.69)
            : Color(red: 0.43, green: 0.43, blue: 0.43)
    }

    static func faintInk(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0.49, green: 0.49, blue: 0.49)
            : Color(red: 0.54, green: 0.54, blue: 0.54)
    }

    static func track(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.16) : Color.black.opacity(0.07)
    }

    static func hairline(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.12) : Color.black.opacity(0.08)
    }

    static func bar(_ scheme: ColorScheme, emphasized: Bool) -> Color {
        emphasized ? ink(scheme) : ink(scheme).opacity(scheme == .dark ? 0.42 : 0.26)
    }

    /// Subtle botanical accents matching the reading tree's muted ink-and-paper palette.
    static func treeLeafAccent(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0.52, green: 0.68, blue: 0.56)
            : Color(red: 0.32, green: 0.48, blue: 0.37)
    }

    static func treeFruitAccent(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0.87, green: 0.45, blue: 0.31)
            : Color(red: 0.79, green: 0.34, blue: 0.22)
    }

    static func title(_ size: CGFloat, relativeTo style: Font.TextStyle = .headline, useSystemFont: Bool = false) -> Font {
        font(size, relativeTo: style, weight: .semibold, useSystemFont: useSystemFont)
    }

    static func body(_ size: CGFloat, relativeTo style: Font.TextStyle = .caption, useSystemFont: Bool = false) -> Font {
        font(size, relativeTo: style, useSystemFont: useSystemFont)
    }

    static func label(_ size: CGFloat, weight: Font.Weight = .semibold, useSystemFont: Bool = false) -> Font {
        font(size, relativeTo: .caption, weight: weight, useSystemFont: useSystemFont)
    }

    static func number(_ size: CGFloat, weight: Font.Weight = .semibold, useSystemFont: Bool = false) -> Font {
        font(size, relativeTo: .title, weight: weight, useSystemFont: useSystemFont)
    }

    static func font(_ size: CGFloat, relativeTo style: Font.TextStyle,
                     weight: Font.Weight = .regular, useSystemFont: Bool = false) -> Font {
        if useSystemFont {
            return .system(size: size, weight: weight)
        }
        if KkWidgetFonts.available {
            return .custom(KkWidgetFonts.bookFace, size: size, relativeTo: style)
        }
        return .system(size: size, weight: weight, design: .serif)
    }
}

/// Fonts belong to each extension process. iOS can load UIAppFonts at launch;
/// explicit registration also handles macOS and snapshots rendered before that.
private enum KkWidgetFonts {
    static let bookFace = "KingHwaOldSong"
    static let available: Bool = {
        if isRegistered { return true }
        guard let url = Bundle.main.url(forResource: "KingHwaOldSong-v3.0",
                                        withExtension: "ttf", subdirectory: "Fonts") else { return false }
        CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
        return isRegistered
    }()

    private static var isRegistered: Bool {
        #if os(iOS)
        UIFont(name: bookFace, size: 12) != nil
        #else
        NSFont(name: bookFace, size: 12) != nil
        #endif
    }
}

/// A flat monochrome page. Painted through containerBackground so WidgetKit can
/// apply its own corner mask.
struct KkBackdrop: View {
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        KkTheme.page(scheme)
    }
}

struct KkCover: View {
    let book: KkBook
    var width: CGFloat
    var height: CGFloat
    var blankPlaceholder = false
    @Environment(\.colorScheme) private var scheme
    @Environment(\.widgetRenderingMode) private var renderingMode

    private var cornerRadius: CGFloat { max(3, width * 0.045) }
    private var isFullColor: Bool { renderingMode == .fullColor }

    private var image: CGImage? {
        guard !blankPlaceholder, let path = KkSnapshotStore.coverURL(book.cover)?.path else { return nil }
        return KkCoverCache.shared.image(path: path, pixels: Int(max(width, height) * 3))
    }

    private var titleInitial: String {
        let trimmed = book.title.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "" : String(trimmed.prefix(1))
    }

    var body: some View {
        Group {
            if let image {
                if #available(iOS 18.0, macOS 15.0, *) {
                    lzImage(image)
                        .resizable()
                        .widgetAccentedRenderingMode(.fullColor)
                        .scaledToFill()
                } else {
                    lzImage(image)
                        .resizable()
                        .scaledToFill()
                }
            } else if blankPlaceholder {
                ZStack {
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(isFullColor ? KkTheme.track(scheme) : Color.primary.opacity(0.12))
                    Image(systemName: "book.closed")
                        .font(.system(size: max(12, width * 0.28), weight: .ultraLight))
                        .foregroundStyle(isFullColor ? KkTheme.faintInk(scheme) : Color.primary.opacity(0.45))
                }
            } else {
                ZStack {
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(isFullColor ? KkTheme.track(scheme) : Color.primary.opacity(0.12))
                    VStack(spacing: height * 0.08) {
                        Image(systemName: "book.closed")
                            .font(.system(size: max(10, width * 0.18), weight: .light))
                        if !titleInitial.isEmpty {
                            Text(titleInitial)
                                .kkTitleFont(max(18, width * 0.35))
                                .multilineTextAlignment(.center)
                                .lineLimit(1)
                        }
                    }
                    .foregroundStyle(isFullColor ? KkTheme.secondaryInk(scheme) : Color.primary.opacity(0.8))
                    .padding(.horizontal, width * 0.12)
                }
            }
        }
        .frame(width: width, height: height)
        .clipped()
        .overlay(alignment: .leading) {
            if isFullColor {
                LinearGradient(colors: [.black.opacity(0.16), .clear, .white.opacity(0.10)],
                               startPoint: .leading, endPoint: .trailing)
                    .frame(width: max(3, width * 0.06))
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                .strokeBorder(isFullColor ? KkTheme.hairline(scheme) : Color.primary.opacity(0.24), lineWidth: 0.5))
        .shadow(color: isFullColor ? Color.black.opacity(scheme == .dark ? 0.28 : 0.14) : .clear,
                radius: isFullColor ? 5 : 0, x: 0, y: isFullColor ? 3 : 0)
        .accessibilityHidden(true)
    }
}

@ViewBuilder
private func lzImage(_ image: CGImage) -> Image {
    #if os(iOS)
    Image(uiImage: UIImage(cgImage: image))
    #else
    Image(nsImage: NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height)))
    #endif
}

/// Synchronous thumbnail cache used by the widget entry point. Widgets must be
/// ready when SwiftUI asks for their body; asynchronous loading can leave an
/// empty snapshot on the first frame.
final class KkCoverCache {
    static let shared = KkCoverCache()

    private final class Box {
        let image: CGImage
        init(_ image: CGImage) { self.image = image }
    }

    private let cache = NSCache<NSString, Box>()

    private init() {
        cache.countLimit = 24
    }

    func image(path: String, pixels: Int) -> CGImage? {
        let key = "\(path)|\(pixels)" as NSString
        if let box = cache.object(forKey: key) { return box.image }
        guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: max(64, pixels)
              ] as CFDictionary) else { return nil }
        cache.setObject(Box(image), forKey: key)
        return image
    }
}

struct KkProgressBar: View {
    let progress: Double
    @Environment(\.colorScheme) private var scheme
    var height: CGFloat = 4

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(KkTheme.track(scheme))
                if progress > 0 {
                    Capsule()
                        .fill(KkTheme.ink(scheme))
                        .frame(width: max(height, geometry.size.width * CGFloat(max(0, min(100, progress)) / 100)))
                }
            }
        }
        .frame(height: height)
        .accessibilityHidden(true)
    }
}

struct KkStat: View {
    let value: String
    let label: String
    var unit: String? = nil
    var inline = false
    var alignment: HorizontalAlignment = .leading
    var valueSize: CGFloat? = nil
    var labelSize: CGFloat = 10
    var stackSpacing: CGFloat = 4
    var expand = false
    @Environment(\.colorScheme) private var scheme

    private var frameAlignment: Alignment {
        switch alignment {
        case .center: return .center
        case .trailing: return .trailing
        default: return .leading
        }
    }

    var body: some View {
        Group {
            if inline {
                HStack(alignment: .firstTextBaseline, spacing: 4) {
                    labelText
                    valueWithUnit
                }
            } else {
                VStack(alignment: alignment, spacing: stackSpacing) {
                    valueWithUnit
                    labelText
                }
            }
        }
        .frame(maxWidth: expand ? .infinity : nil, alignment: frameAlignment)
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private var valueWithUnit: some View {
        if let unit, !unit.isEmpty {
            HStack(alignment: .firstTextBaseline, spacing: 2) {
                valueText.kkNumberFont(valueSize ?? 19)
                Text(unit)
                    .kkLabelFont(max(9, labelSize - 0.5), weight: .regular)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
            }
        } else {
            valueText.kkNumberFont(valueSize ?? (inline ? 13 : 19))
        }
    }

    private var valueText: some View {
        Text(value)
            .foregroundStyle(KkTheme.ink(scheme))
            .monospacedDigit()
            .lineLimit(1)
            .minimumScaleFactor(0.7)
    }

    private var labelText: some View {
        Text(label)
            .kkLabelFont(labelSize, weight: .regular)
            .foregroundStyle(KkTheme.secondaryInk(scheme))
            .lineLimit(1)
            .minimumScaleFactor(0.8)
    }
}
