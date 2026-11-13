import CoreGraphics
import CoreText
import Foundation
import ImageIO
import SwiftUI

#if os(iOS)
import UIKit
#else
import AppKit
#endif

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

    static func title(_ size: CGFloat, relativeTo style: Font.TextStyle = .headline) -> Font {
        font(size, relativeTo: style, weight: .semibold)
    }

    static func body(_ size: CGFloat, relativeTo style: Font.TextStyle = .caption) -> Font {
        font(size, relativeTo: style)
    }

    static func label(_ size: CGFloat, weight: Font.Weight = .semibold) -> Font {
        font(size, relativeTo: .caption, weight: weight)
    }

    static func number(_ size: CGFloat, weight: Font.Weight = .semibold) -> Font {
        font(size, relativeTo: .title, weight: weight)
    }

    private static func font(_ size: CGFloat, relativeTo style: Font.TextStyle,
                             weight: Font.Weight = .regular) -> Font {
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
    @Environment(\.colorScheme) private var scheme

    private var cornerRadius: CGFloat { max(3, width * 0.045) }

    private var image: CGImage? {
        guard let path = KkSnapshotStore.coverURL(book.cover)?.path else { return nil }
        return KkCoverCache.shared.image(path: path, pixels: Int(max(width, height) * 3))
    }

    var body: some View {
        Group {
            if let image {
                lzImage(image)
                    .resizable()
                    .scaledToFill()
            } else {
                ZStack {
                    KkTheme.track(scheme)
                    VStack(spacing: height * 0.08) {
                        Image(systemName: "book.closed")
                            .font(.system(size: max(10, width * 0.18), weight: .light))
                        Text(String(book.title.prefix(1)))
                            .font(KkTheme.title(max(18, width * 0.35)))
                            .multilineTextAlignment(.center)
                            .lineLimit(1)
                    }
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .padding(.horizontal, width * 0.12)
                }
            }
        }
        .frame(width: width, height: height)
        .clipped()
        .overlay(alignment: .leading) {
            LinearGradient(colors: [.black.opacity(0.16), .clear, .white.opacity(0.10)],
                           startPoint: .leading, endPoint: .trailing)
                .frame(width: max(3, width * 0.06))
        }
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                .strokeBorder(KkTheme.hairline(scheme), lineWidth: 0.5))
        .shadow(color: Color.black.opacity(scheme == .dark ? 0.28 : 0.14), radius: 5, x: 0, y: 3)
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
    var inline = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        Group {
            if inline {
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    valueText.font(KkTheme.number(13))
                    labelText
                }
            } else {
                VStack(alignment: .leading, spacing: 5) {
                    valueText.font(KkTheme.number(19))
                    labelText
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
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
            .font(KkTheme.label(10, weight: .regular))
            .foregroundStyle(KkTheme.secondaryInk(scheme))
            .lineLimit(1)
    }
}
