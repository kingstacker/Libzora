import Foundation

@main
struct NativeReaderOpenLogicTests {
    private static var assertions = 0
    private static func check(_ value: @autoclosure () -> Bool, _ message: String) {
        assertions += 1
        if !value() { fatalError("Regression failed: \(message)") }
    }

    static func main() {
        struct Request: Equatable {
            let book: String
            let location: Int?
            let source: String
        }
        var queue = NativeReaderOpenQueue<Request>()
        let materials = Request(book: "A", location: 345, source: "materials")
        queue.enqueue(materials)
        let first = queue.revision
        check(queue.pending == materials, "A close-time tap retains the material location and source")
        queue.enqueue(Request(book: "B", location: nil, source: "cover"))
        check(queue.take(revision: first) == nil, "A previously scheduled task cannot consume a newer tap")
        check(queue.pending?.book == "B", "Rejected stale tasks leave the latest intent intact")
        let second = queue.revision
        check(queue.take(revision: second)?.book == "B", "The latest tap opens when teardown releases it")
        check(queue.take(revision: second) == nil, "Duplicate scheduled drains cannot open twice")
        queue.enqueue(materials)
        let canceled = queue.revision
        queue.clear()
        check(queue.pending == nil, "A tab or widget request cancels the ordinary queued intent")
        queue.enqueue(materials)
        check(queue.take(revision: canceled) == nil, "Re-requesting the same book does not revive an old task")
        check(queue.take(revision: queue.revision) == materials, "A fresh same-book intent keeps its location")

        check(NativeReaderOpenPolicy.isDuplicate(bookId: "A", openingBookId: "A", openingGeneration: 3,
              currentGeneration: 3, readerBookId: nil, closing: false, hasPending: false),
              "An active same-book open is deduplicated")
        check(!NativeReaderOpenPolicy.isDuplicate(bookId: "A", openingBookId: "A", openingGeneration: 3,
              currentGeneration: 4, readerBookId: nil, closing: false, hasPending: false),
              "A canceled same-book open must not swallow a new widget link")
        check(!NativeReaderOpenPolicy.isDuplicate(bookId: "A", openingBookId: "A", openingGeneration: 3,
              currentGeneration: 4, readerBookId: "A", closing: false, hasPending: false),
              "A canceled open's still-visible cover does not deduplicate its replacement")
        check(!NativeReaderOpenPolicy.isDuplicate(bookId: "A", openingBookId: nil, openingGeneration: nil,
              currentGeneration: 4, readerBookId: "A", closing: true, hasPending: false),
              "Closing the same book permits a re-open intent")
        check(!NativeReaderOpenPolicy.isDuplicate(bookId: "A", openingBookId: nil, openingGeneration: nil,
              currentGeneration: 4, readerBookId: "A", closing: false, hasPending: true),
              "A newer request can replace a queued different-book intent")
        check(NativeReaderOpenPolicy.isDuplicate(bookId: "A", openingBookId: nil, openingGeneration: nil,
              currentGeneration: 4, readerBookId: "A", closing: false, hasPending: false),
              "An already-open book remains deduplicated")
        check(!NativeReaderOpenPolicy.isDuplicate(bookId: "B", openingBookId: "A", openingGeneration: 4,
              currentGeneration: 4, readerBookId: "A", closing: false, hasPending: false),
              "A different book is never dropped as a duplicate")
        print("Native reader open logic: \(assertions) assertions passed")
    }
}
