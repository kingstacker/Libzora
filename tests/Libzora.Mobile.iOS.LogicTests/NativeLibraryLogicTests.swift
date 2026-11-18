import Foundation

/// Runs the production Foundation-only models and policies. No app launch,
/// simulator, network connection or library data is involved.
@main
struct NativeLibraryLogicTests {
    private static var assertions = 0

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        assertions += 1
        guard condition() else { fatalError("Regression failed: \(message)") }
    }

    private static func book(_ id: String, _ title: String, author: String = "Author", series: String? = nil,
                             index: Double? = nil, files: [String]? = nil) -> LibraryBook {
        LibraryBook(id: id, title: title, authors: author, formats: "EPUB", size: "1 MB", coverPath: nil,
                    progress: 0, favorite: false, readingStatus: 0, syncStatus: nil, collectionIds: [],
                    updatedAt: "2026-11-18T00:00:00Z", createdAt: "2026-11-18T00:00:00Z", description: nil,
                    series: series, seriesIndex: index, fileTitles: files, publisher: nil, publishDate: nil,
                    isbn: nil, pageCount: nil, binding: nil, tags: "", category: "", onlineRating: nil)
    }

    private static func stack(_ books: [LibraryBook]) -> NativeBookSeries {
        let entries = NativeBookSeries.entries(books)
        check(entries.count == 1, "Expected one series stack")
        guard case .series(let series) = entries[0] else { fatalError("Expected a series, not a book") }
        return series
    }

    static func main() {
        let multiAuthor = stack([book("two", "Second", author: "B", series: "Collection", index: 2),
                                 book("one", "First", author: "A", series: "Collection", index: 1)])
        check(multiAuthor.books.map(\.id) == ["one", "two"], "Explicit multi-author series must group and sort")
        check(multiAuthor.id.authors.isEmpty, "Explicit series identity must not depend on authors")

        let folded = stack([book("two", "Second", series: "Ｃｏｌｌｅｃｔｉｏｎ", index: 2),
                            book("one", "First", series: " collection ", index: 1)])
        check(folded.id == multiAuthor.id, "Series identity folds width, case and surrounding whitespace")

        let inferred = stack([book("two", "Story Volume 2"), book("one", "Story Volume 1")])
        check(inferred.books.map(\.id) == ["one", "two"], "Inferred volume numbers define ordering")
        let differentAuthors = NativeBookSeries.entries([book("one", "Story Volume 1", author: "A"),
                                                        book("two", "Story Volume 2", author: "B")])
        check(differentAuthors.count == 2, "Inference must still disambiguate different authors")

        let mixed = stack([book("one", "First", series: "Story", index: 1), book("two", "Story Volume 2")])
        check(mixed.books.map(\.id) == ["one", "two"], "Matching author inference joins explicit metadata")
        let ambiguous = NativeBookSeries.entries([book("one", "First", author: "A", series: "Story", index: 1),
                                                 book("two", "Story Volume 2", author: "B")])
        check(ambiguous.count == 2, "Unrelated inferred authors must not join an explicit series")

        let cjk = stack([book("two", "故事 第2卷"), book("one", "故事 第1卷")])
        check(cjk.books.map(\.id) == ["one", "two"], "Escaped CJK volume markers still match")
        check(NativeBookSeries.entries([book("one", "History 1984"), book("two", "History 1985")]).count == 2,
              "Bare years are not volume markers")
        check(NativeBookSeries.entries([book("one", "Story Volume 1"), book("two", "Story Volume 1")]).count == 2,
              "Same-number inferred editions are not a multi-volume stack")
        check(NativeBookSeries.groups([book("one", "First", files: ["Story Volume 1", "Story Volume 2"])]).isEmpty,
              "Conflicting format filenames cannot infer a volume")

        let updated = NativeBookSeries.groups([book("one", "First", author: "A", series: "Collection", index: 3),
                                               book("two", "Second", author: "B", series: "Collection", index: 1),
                                               book("three", "Third", author: "C", series: "Collection", index: 2)])
        check(updated[multiAuthor.id]?.books.map(\.id) == ["two", "three", "one"],
              "Fresh detail snapshots include new members and updated ordering")
        let moved = NativeBookSeries.groups([book("one", "First", series: "Other", index: 1),
                                             book("two", "Second", series: "Collection", index: 2)])
        check(moved[multiAuthor.id]?.books.map(\.id) == ["two"], "Moved members must disappear from the old series")
        check(NativeBookSeries.entries([book("two", "Second", series: "Collection", index: 2)]).count == 1,
              "A single remaining volume appears as an ordinary shelf book")
        check(NativeBookSeries.groups([])[multiAuthor.id] == nil, "Removed series has no stale detail snapshot")
        let finite = stack([book("one", "Story Volume 1", series: "Story", index: .infinity),
                            book("two", "Story Volume 2", series: "Story", index: 2)])
        check(finite.books.map(\.id) == ["one", "two"], "Non-finite positions fall back to safe title inference")

        for operation in ["save", "run"] {
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "") == .mismatch,
                  "\(operation) rejects missing second key input")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "different") == .mismatch,
                  "\(operation) rejects inconsistent key input")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "new") == .confirm,
                  "\(operation) must request explicit confirmation before persistence")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "", savedKey: "old", confirmation: "") == .confirm,
                  "\(operation) must confirm clearing an existing key")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "old", savedKey: "old", confirmation: "") == .proceed,
                  "\(operation) preserves normal use of an unchanged key")
        }
        for operation in ["test", "import", "export"] {
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "") == .proceed,
                  "\(operation) does not persist the encryption key")
        }
        print("Passed \(assertions) iOS library and sync-key logic assertions.")
    }
}
