import Foundation

/// Retains the latest intent while the previous reader is tearing down.
/// Revision checks also protect tasks already scheduled on the main actor.
struct NativeReaderOpenQueue<Request> {
    private(set) var revision = 0
    private(set) var pending: Request?

    mutating func enqueue(_ request: Request) {
        revision += 1
        pending = request
    }

    mutating func clear() {
        revision += 1
        pending = nil
    }

    mutating func take(revision expected: Int) -> Request? {
        guard expected == revision else { return nil }
        let request = pending
        pending = nil
        return request
    }
}

enum NativeReaderOpenPolicy {
    static func isDuplicate(bookId: String, openingBookId: String?, openingGeneration: Int?,
                            currentGeneration: Int, readerBookId: String?, closing: Bool,
                            hasPending: Bool) -> Bool {
        guard !closing, !hasPending else { return false }
        if let openingBookId {
            return openingBookId == bookId && openingGeneration == currentGeneration
        }
        return readerBookId == bookId
    }
}
