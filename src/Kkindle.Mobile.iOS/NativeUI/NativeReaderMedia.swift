import SwiftUI
import UIKit
import ImageIO

/// Full-screen figure/table inspector. The page stays paginated at its normal
/// width; this view shows the original image or a wide table snapshot and
/// leaves pinch/double-tap zoom to the system scroll view.
struct NativeReaderMediaViewer: View {
    let media: ReaderMedia
    let close: () -> Void
    @State private var failed = false
    @State private var hintVisible = true

    private var label: String { media.kind == "table" ? "表格" : "图片" }

    var body: some View {
        ZStack(alignment: .top) {
            Color.black.ignoresSafeArea()
            if failed {
                VStack(spacing: 12) {
                    Image(systemName: media.kind == "table" ? "tablecells" : "photo")
                        .font(.system(size: 34, weight: .light))
                    Text("无法显示\(label)")
                }.foregroundStyle(.white.opacity(0.82))
            } else {
                ZoomableReaderImage(
                    path: media.path,
                    maxPixelSize: media.kind == "table" ? nil : 4096,
                    onFailure: { failed = true })
                    .ignoresSafeArea()
            }
            HStack(spacing: 12) {
                Text("\(label) · \(media.title)")
                    .font(.system(size: 13, weight: .medium)).lineLimit(1)
                    .foregroundStyle(.white.opacity(0.88))
                Spacer(minLength: 12)
                Button(action: close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 15, weight: .semibold))
                        .frame(width: 44, height: 44)
                        .background(.black.opacity(0.42), in: Circle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.white)
                .accessibilityLabel("关闭")
                .accessibilityIdentifier("reader.media.close")
            }
            .padding(.horizontal, 16).padding(.top, 8)

            if hintVisible && !failed {
                VStack {
                    Spacer()
                    Text("双指缩放 · 双击放大")
                        .font(.system(size: 12))
                        .foregroundStyle(.white.opacity(0.5))
                        .padding(.bottom, 24)
                }.allowsHitTesting(false)
            }
        }
        .statusBarHidden(true)
        .preferredColorScheme(.dark)
        .onAppear {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) {
                withAnimation(.easeOut(duration: 0.3)) { hintVisible = false }
            }
        }
    }
}

private struct ZoomableReaderImage: UIViewRepresentable {
    let path: String
    let maxPixelSize: CGFloat?
    let onFailure: () -> Void

    func makeUIView(context: Context) -> ReaderZoomScrollView {
        let view = ReaderZoomScrollView()
        view.onFailure = onFailure
        view.load(path, maxPixelSize: maxPixelSize)
        return view
    }

    func updateUIView(_ view: ReaderZoomScrollView, context: Context) {
        view.onFailure = onFailure
        view.load(path, maxPixelSize: maxPixelSize)
    }
}

private final class ReaderZoomScrollView: UIScrollView, UIScrollViewDelegate {
    private let imageView = UIImageView()
    private var loadedPath: String?
    private var lastBoundsSize: CGSize = .zero
    var onFailure: (() -> Void)?

    init() {
        super.init(frame: .zero)
        backgroundColor = .black
        delegate = self
        minimumZoomScale = 1
        maximumZoomScale = 5
        bouncesZoom = true
        decelerationRate = .fast
        showsHorizontalScrollIndicator = false
        showsVerticalScrollIndicator = false
        contentInsetAdjustmentBehavior = .never
        imageView.contentMode = .scaleAspectFit
        imageView.clipsToBounds = true
        addSubview(imageView)
        let doubleTap = UITapGestureRecognizer(target: self, action: #selector(handleDoubleTap(_:)))
        doubleTap.numberOfTapsRequired = 2
        addGestureRecognizer(doubleTap)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    /// Decodes off the main thread so opening a large scanned illustration
    /// does not stall the cover animation. Figures are capped to a size that
    /// still benefits from the 5× zoom; table snapshots are already bounded
    /// by the renderer and keep their full resolution so columns stay sharp.
    func load(_ path: String, maxPixelSize: CGFloat?) {
        guard path != loadedPath else { return }
        loadedPath = path
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let image = ReaderZoomScrollView.decode(path, maxPixelSize: maxPixelSize)
            DispatchQueue.main.async {
                guard let self, self.loadedPath == path else { return }
                guard let image else {
                    self.imageView.image = nil
                    self.onFailure?()
                    return
                }
                self.imageView.image = image
                self.zoomScale = self.minimumZoomScale
                self.setNeedsLayout()
            }
        }
    }

    private static func decode(_ path: String, maxPixelSize: CGFloat?) -> UIImage? {
        let url = URL(fileURLWithPath: path) as CFURL
        guard let source = CGImageSourceCreateWithURL(url, nil) else { return nil }
        // Honour EXIF orientation, including the mirrored ones.
        let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        let width = (properties?[kCGImagePropertyPixelWidth] as? NSNumber)?.doubleValue ?? 0
        let height = (properties?[kCGImagePropertyPixelHeight] as? NSNumber)?.doubleValue ?? 0
        let needsDownsample = maxPixelSize.map { max(width, height) > Double($0) } ?? false
        if needsDownsample, let maxPixelSize {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
            ]
            if let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
                return UIImage(cgImage: image)
            }
        }
        guard let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        let exif = (properties?[kCGImagePropertyOrientation] as? NSNumber)?.uint32Value ?? 1
        return UIImage(cgImage: image, scale: 1, orientation: orientation(exif))
    }

    /// The thumbnail path bakes EXIF orientation in; the full-size path has
    /// to carry it on the UIImage instead.
    private static func orientation(_ exif: UInt32) -> UIImage.Orientation {
        switch exif {
        case 2: return .upMirrored
        case 3: return .down
        case 4: return .downMirrored
        case 5: return .leftMirrored
        case 6: return .right
        case 7: return .rightMirrored
        case 8: return .left
        default: return .up
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard bounds.size != lastBoundsSize || imageView.frame.isEmpty else { return }
        lastBoundsSize = bounds.size
        zoomScale = minimumZoomScale
        imageView.frame = bounds
        contentSize = bounds.size
    }

    func viewForZooming(in scrollView: UIScrollView) -> UIView? { imageView }

    @objc private func handleDoubleTap(_ gesture: UITapGestureRecognizer) {
        if zoomScale > minimumZoomScale + 0.01 {
            setZoomScale(minimumZoomScale, animated: true)
            return
        }

        let point = gesture.location(in: imageView)
        let size = CGSize(width: bounds.width / 2.4, height: bounds.height / 2.4)
        zoom(to: CGRect(x: point.x - size.width / 2,
                        y: point.y - size.height / 2,
                        width: size.width,
                        height: size.height),
             animated: true)
    }
}
