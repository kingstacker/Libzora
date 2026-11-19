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
