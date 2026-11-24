import UIKit

/// Wait for native display commits, not an assumed number of milliseconds.
/// The first callback can precede the pending layer transaction's commit;
/// the second gives that transaction a complete display interval.
@MainActor
final class NativeDisplayFrame: NSObject {
    private final class DisplayLinkProxy: NSObject {
        weak var owner: NativeDisplayFrame?
        init(owner: NativeDisplayFrame) { self.owner = owner }
        @objc func tick() {
            MainActor.assumeIsolated {
                owner?.tick()
            }
        }
    }

    private var link: CADisplayLink?
    private var continuation: CheckedContinuation<Void, Never>?
    private var remaining = 2

    static func waitForCommit() async {
        let frame = NativeDisplayFrame()
        await withTaskCancellationHandler(operation: {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled else { continuation.resume(); return }
                frame.continuation = continuation
                let proxy = DisplayLinkProxy(owner: frame)
                let link = CADisplayLink(target: proxy, selector: #selector(DisplayLinkProxy.tick))
                if #available(iOS 15.0, *) {
                    let bounds = NativeBookOpenTransitionPolicy.preferredFrameRateBounds(
                        maximumFramesPerSecond: Double(UIScreen.main.maximumFramesPerSecond)
                    )
                    link.preferredFrameRateRange = CAFrameRateRange(
                        minimum: bounds.minimum,
                        maximum: bounds.maximum,
                        preferred: bounds.preferred
                    )
                }
                frame.link = link
                link.add(to: .main, forMode: .common)
            }
        }, onCancel: {
            Task { @MainActor in frame.finish() }
        })
    }

    private func tick() {
        remaining -= 1
        if remaining == 0 { finish() }
    }

    private func finish() {
        link?.invalidate()
        link = nil
        let completion = continuation
        continuation = nil
        completion?.resume()
    }
}
