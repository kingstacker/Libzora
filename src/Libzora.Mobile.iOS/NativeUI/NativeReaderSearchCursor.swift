import Foundation

struct ReaderSearchHit: Decodable, Identifiable, Equatable {
    let id: Int
    let title: String
    let excerpt: String
    let chapterIndex: Int
    let offset: Int
}

struct ReaderSearchJumpReply: Decodable { let jumped: Bool }

/// One progressive-search update. Items are incremental deltas with stable IDs
/// and document coordinates so already-visible results stay tappable while
/// later batches arrive or the user requests more results.
struct ReaderSearchBatch: Decodable, Equatable {
    let generation: Int
    let searchId: String
    let query: String
    let items: [ReaderSearchHit]
    let scannedChapters: Int
    let totalChapters: Int
    let isFinal: Bool
    let error: String?
    let hasMore: Bool
    let isIncremental: Bool
    let totalHits: Int
}

/// A search cursor follows stable hit IDs as progressive batches insert and
/// reorder matches. Ordinary navigation exits it; search jumps do not.
struct NativeReaderSearchCursor {
    private(set) var hitId: Int?
    private var navigationRevision: Int64?

    mutating func select(_ id: Int, navigationRevision: Int64?) {
        hitId = id
        self.navigationRevision = navigationRevision
    }

    mutating func clear() {
        hitId = nil
        navigationRevision = nil
    }

    mutating func navigated(to revision: Int64?) -> Bool {
        guard hitId != nil, revision != navigationRevision else { return false }
        clear()
        return true
    }

    func index(in ids: [Int]) -> Int? {
        hitId.flatMap { ids.firstIndex(of: $0) }
    }

    func neighbor(_ direction: Int, in ids: [Int]) -> Int? {
        guard let index = index(in: ids) else { return nil }
        let target = index + (direction < 0 ? -1 : 1)
        return ids.indices.contains(target) ? ids[target] : nil
    }
}

/// Accumulates incremental progressive-search deltas in reading order while
/// keeping stable hit IDs and tracking pause/continue state across batches.
struct NativeReaderSearchAccumulator {
    private(set) var searchId = ""
    private(set) var query = ""
    private(set) var hits: [ReaderSearchHit] = []
    private var seenIds: Set<Int> = []
    private(set) var busy = false
    private(set) var loadingMore = false
    private(set) var isFinal = false
    private(set) var hasMore = false
    private(set) var scanned = 0
    private(set) var total = 0
    private(set) var error: String?

    mutating func reset(searchId: String, query: String, busy: Bool) {
        self.searchId = searchId
        self.query = query
        self.hits = []
        self.seenIds.removeAll()
        self.busy = busy
        self.loadingMore = false
        self.isFinal = false
        self.hasMore = false
        self.scanned = 0
        self.total = 0
        self.error = nil
    }

    mutating func startRequest(searchId: String) {
        self.searchId = searchId
    }

    mutating func clearRequest() {
        searchId = ""
        loadingMore = false
    }

    mutating func failStart(error: String) {
        self.error = error
        busy = false
        loadingMore = false
        isFinal = true
        hasMore = false
    }

    mutating func beginContinue() {
        guard !busy, hasMore, !searchId.isEmpty else { return }
        busy = true
        loadingMore = true
        isFinal = false
        error = nil
    }

    mutating func failContinue(error: String) {
        self.error = error
        busy = false
        loadingMore = false
        isFinal = true
    }

    @discardableResult
    mutating func apply(_ batch: ReaderSearchBatch) -> Bool {
        guard !searchId.isEmpty,
              batch.searchId == searchId,
              batch.query == query else { return false }

        if !batch.isIncremental {
            hits = batch.items
            seenIds = Set(batch.items.map(\.id))
        } else if !batch.items.isEmpty {
            var appendedOnly = true
            for item in batch.items {
                guard seenIds.insert(item.id).inserted else { continue }
                if let last = hits.last, Self.precedes(item, last) {
                    appendedOnly = false
                }
                hits.append(item)
            }
            if !appendedOnly {
                hits.sort(by: Self.precedes)
            }
        }

        scanned = batch.scannedChapters
        total = batch.totalChapters
        isFinal = batch.isFinal
        hasMore = batch.isFinal ? batch.hasMore : hasMore
        busy = !batch.isFinal
        if batch.isFinal {
            loadingMore = false
        }
        if let batchError = batch.error {
            error = batchError
        }
        return true
    }

    private static func precedes(_ lhs: ReaderSearchHit, _ rhs: ReaderSearchHit) -> Bool {
        if lhs.chapterIndex != rhs.chapterIndex {
            return lhs.chapterIndex < rhs.chapterIndex
        }
        if lhs.offset != rhs.offset {
            return lhs.offset < rhs.offset
        }
        return lhs.id < rhs.id
    }
}
