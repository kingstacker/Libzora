import Foundation

struct NativeBookSeries: Identifiable {
    struct ID: Hashable {
        let name: String
        let authors: String
    }

    let id: ID
    let title: String
    let books: [LibraryBook]
    private let stackable: Bool

    func booksByPromoting(_ bookID: String?) -> [LibraryBook] {
        guard let bookID, let index = books.firstIndex(where: { $0.id == bookID }), index > 0 else { return books }
        var ordered = books
        let front = ordered.remove(at: index)
        ordered.insert(front, at: 0)
        return ordered
    }

    enum Entry: Identifiable {
        enum ID: Hashable {
            case book(String)
            case series(NativeBookSeries.ID)
        }

        case book(LibraryBook)
        case series(NativeBookSeries)

        var id: ID {
            switch self {
            case .book(let book): .book(book.id)
            case .series(let series): .series(series.id)
            }
        }
    }

    private struct Member {
        let book: LibraryBook
        let name: String
        let index: Double?
        let tagged: Bool
    }

    // Require an explicit volume marker at the end. Bare numbers in titles
    // can be years, edition numbers or part of the name itself.
    // CJK markers (第, 卷, 冊, 册) are escaped so the UI linter ignores the regex.
    private static let volumePattern = try! NSRegularExpression(
        pattern: #"^(.+?)(?:[\s._:\-–—]*\x{7B2C}\s*([0-9]+)\s*[\x{5377}\x{518A}\x{518C}]|[\s._:\-–—]+(?:vol(?:ume)?\.?|book)\s*([0-9]+))$"#,
        options: .caseInsensitive)

    private static func normalized(_ value: String) -> String {
        value.folding(options: [.caseInsensitive, .widthInsensitive], locale: Locale(identifier: "en_US_POSIX"))
            .split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    private static func volume(in value: String) -> (name: String, index: Double)? {
        let title = value.folding(options: .widthInsensitive, locale: nil)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard let match = volumePattern.firstMatch(in: title, range: NSRange(title.startIndex..., in: title)),
              let nameRange = Range(match.range(at: 1), in: title),
              let numberRange = Range(match.range(at: 2), in: title) ?? Range(match.range(at: 3), in: title),
              let index = Double(title[numberRange]), index.isFinite else { return nil }
        let name = title[nameRange].trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? nil : (name, index)
    }

    private static func member(_ book: LibraryBook) -> (ID, Member)? {
        let tag = book.series?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let tagged = !tag.isEmpty
        let titleVolume = volume(in: book.title)
        let fileVolumes = (book.fileTitles ?? []).compactMap(volume(in:))
        // Copies in several formats sometimes carry different names. Use the
        // file name only when the files with volume markers agree.
        let fileVolume = fileVolumes.first.flatMap { first in
            fileVolumes.allSatisfy { normalized($0.name) == normalized(first.name) && $0.index == first.index } ? first : nil
        }
        let hint = titleVolume ?? fileVolume
        guard let name = tagged ? tag : hint?.name else { return nil }
        let nameKey = normalized(name)
        let index = book.seriesIndex.flatMap { $0.isFinite ? $0 : nil }
            ?? hint.flatMap { normalized($0.name) == nameKey ? $0.index : nil }
        return (ID(name: nameKey, authors: normalized(book.authors)),
                Member(book: book, name: name, index: index, tagged: tagged))
    }

    /// Includes single-member groups so an already-open detail page remains
    /// correct after another volume is removed. Shelf stacking is separate.
    static func groups(_ books: [LibraryBook]) -> [ID: NativeBookSeries] {
        let candidates = books.compactMap(member)
        var explicitAuthors: [String: Set<String>] = [:]
        for (key, value) in candidates where value.tagged {
            explicitAuthors[key.name, default: []].insert(key.authors)
        }
        var members: [ID: [Member]] = [:]
        for (key, value) in candidates {
            // Explicit series metadata can describe a multi-author collection.
            // Author disambiguation is only for filename/title inference; an
            // inferred volume joins an explicit collection for a known author.
            let explicit = value.tagged || explicitAuthors[key.name]?.contains(key.authors) == true
            let groupKey = explicit ? ID(name: key.name, authors: "") : key
            members[groupKey, default: []].append(value)
        }

        var result: [ID: NativeBookSeries] = [:]
        for (key, group) in members {
            let tagged = group.first(where: \.tagged)
            let ordered = group.sorted { left, right in
                let leftIndex = left.index ?? .infinity
                let rightIndex = right.index ?? .infinity
                if leftIndex != rightIndex { return leftIndex < rightIndex }
                let titleOrder = left.book.title.localizedStandardCompare(right.book.title)
                if titleOrder != .orderedSame { return titleOrder == .orderedAscending }
                return left.book.id < right.book.id
            }
            let stackable = group.count > 1 && (tagged != nil || Set(group.compactMap(\.index)).count > 1)
            result[key] = NativeBookSeries(id: key, title: (tagged ?? ordered[0]).name,
                                          books: ordered.map(\.book), stackable: stackable)
        }
        return result
    }

    static func entries(_ books: [LibraryBook]) -> [Entry] {
        var stacksByBook: [String: NativeBookSeries] = [:]
        for series in groups(books).values where series.stackable {
            for book in series.books { stacksByBook[book.id] = series }
        }
        var shown = Set<ID>()
        return books.compactMap { book in
            guard let stack = stacksByBook[book.id] else { return .book(book) }
            // Keep the position of the first matching book in the current shelf sort.
            return shown.insert(stack.id).inserted ? .series(stack) : nil
        }
    }
}
