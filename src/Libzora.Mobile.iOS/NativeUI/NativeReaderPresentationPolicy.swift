import Foundation

/// Readiness is separate from loading feedback: no timer may expose an
/// unrendered page. A viewport acknowledgement also prevents a stale first
/// frame from winning a safe-area/PDF/manga inset update.
struct NativeReaderPresentationPolicy {
    private(set) var started = false

    mutating func begin(ready: Bool, frameReady: Bool, frameRevision: Int64,
                        acknowledgedRevision: Int64?, hasError: Bool,
                        sessionIsCurrent: Bool) -> Bool {
        guard !started, sessionIsCurrent,
              hasError || (ready && frameReady && frameRevision > 0
                  && acknowledgedRevision == frameRevision) else { return false }
        started = true
        return true
    }

    static let loadingFeedbackDelay: UInt64 = 750_000_000
    static let widgetTransitionDuration = 0.24
}
