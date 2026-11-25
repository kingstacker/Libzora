import Foundation

/// The library wire model is Foundation-only so its grouping and decoding can
/// be exercised without UIKit, a simulator or the user's application data.
struct LibraryBook: Decodable, Identifiable, Equatable {
    let id: String
    let title: String
    let authors: String
    let formats: String
    let size: String
    let coverPath: String?
    let progress: Double
    let favorite: Bool
    let readingStatus: Int
    let syncStatus: Int?
    let collectionIds: [String]
    let updatedAt: String
    let createdAt: String
    let description: String?
    let series: String?
    let seriesIndex: Double?
    let fileTitles: [String]?
    let publisher: String?
    let publishDate: String?
    let isbn: String?
    let pageCount: String?
    let binding: String?
    let tags: String
    let category: String
    let onlineRating: Double?
    var readingStatusExplicit: Bool?
}

enum NativeBookReadingStatus {
    static func value(_ book: LibraryBook) -> Int {
        if book.readingStatusExplicit == true || book.readingStatus == 1 || book.readingStatus == 2 {
            return min(2, max(0, book.readingStatus))
        }
        if book.progress >= 99.5 { return 2 }
        if book.progress > 0 { return 1 }
        return 0
    }
}
