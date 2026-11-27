import Foundation

@MainActor
extension NativeStore {
    func frontBook(for series: NativeBookSeries) -> LibraryBook? {
        let mostRecentlyOpened = series.books.max { (bookOpenOrder[$0.id] ?? 0) < (bookOpenOrder[$1.id] ?? 0) }
        return mostRecentlyOpened.flatMap { bookOpenOrder[$0.id] == nil ? nil : $0 } ?? series.books.first
    }

    func booksInDisplayOrder(for series: NativeBookSeries) -> [LibraryBook] {
        series.booksByPromoting(frontBook(for: series)?.id)
    }
}
