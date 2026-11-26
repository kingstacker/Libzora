import UIKit

/// Renderer-owned pixels travel only through the in-process bridge and live
/// in memory for the handoff. UIKit alone can omit a non-empty Metal surface.
struct NativeReaderRenderedSnapshot: Decodable {
    let image: String?
    let x: Double?
    let y: Double?
    let width: Double?
    let height: Double?

    var renderedPage: (image: UIImage, frame: CGRect)? {
        guard let image, let data = Data(base64Encoded: image), let bitmap = UIImage(data: data),
              let x, let y, let width, let height, width > 0, height > 0 else { return nil }
        return (bitmap, CGRect(x: x, y: y, width: width, height: height))
    }
}

@MainActor
extension NativeStore {
    /// Snapshot the reader's presentation, not the window: a window-level
    /// launch/entry cover must never be captured as the incoming reader page.
    func captureReaderPresentationSnapshot(sessionId: String? = nil) async -> UIView? {
        let expected = sessionId ?? session.appearanceSessionId
        guard !expected.isEmpty, expected == session.appearanceSessionId, readerBook != nil else { return nil }
        let pixels: NativeReaderRenderedSnapshot? = try? await request("reader.snapshot", ["sessionId": expected])
        guard !Task.isCancelled, expected == session.appearanceSessionId, readerBook != nil,
              let window = navigationWindow, let view = readerPresentationView,
              view.bounds.width > 10, view.bounds.height > 10 else { return nil }
        let snapshot = UIView(frame: window.bounds)
        snapshot.isUserInteractionEnabled = false
        snapshot.isAccessibilityElement = false
        snapshot.accessibilityElementsHidden = true
        if let chrome = view.snapshotView(afterScreenUpdates: false) {
            chrome.frame = snapshot.bounds
            snapshot.addSubview(chrome)
        } else {
            let format = UIGraphicsImageRendererFormat()
            format.scale = min(window.screen.scale, 2)
            let image = UIGraphicsImageRenderer(bounds: view.bounds, format: format).image { _ in
                view.drawHierarchy(in: view.bounds, afterScreenUpdates: false)
            }
            let chrome = UIImageView(image: image)
            chrome.frame = snapshot.bounds
            snapshot.addSubview(chrome)
        }
        if let page = pixels?.renderedPage, let surface = readerController.viewIfLoaded {
            let body = UIImageView(image: page.image)
            body.frame = surface.convert(page.frame, to: window)
            body.contentMode = .scaleToFill
            snapshot.addSubview(body)
        }
        return snapshot
    }
}
