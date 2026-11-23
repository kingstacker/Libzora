import Foundation

/// Pure selection-set transitions for library batch operations.
enum NativeLibraryBatchPolicy {
    static func toggle(_ id: String, in selected: Set<String>) -> Set<String> {
        var next = selected
        if next.contains(id) {
            next.remove(id)
        } else if !id.isEmpty {
            next.insert(id)
        }
        return next
    }

    static func areAllVisibleSelected(visibleIds: [String], in selected: Set<String>) -> Bool {
        let nonEmpty = visibleIds.filter { !$0.isEmpty }
        return !nonEmpty.isEmpty && nonEmpty.allSatisfy { selected.contains($0) }
    }

    static func toggleAll(visibleIds: [String], in selected: Set<String>) -> Set<String> {
        let nonEmpty = visibleIds.filter { !$0.isEmpty }
        guard !nonEmpty.isEmpty else { return selected }
        var next = selected
        if nonEmpty.allSatisfy({ next.contains($0) }) {
            for id in nonEmpty { next.remove(id) }
        } else {
            for id in nonEmpty { next.insert(id) }
        }
        return next
    }

    static func pruned(_ selected: Set<String>, validIds: Set<String>) -> Set<String> {
        selected.intersection(validIds)
    }

    static func selectedCount(in seriesIds: [String], from selected: Set<String>) -> Int {
        seriesIds.reduce(0) { $0 + (selected.contains($1) ? 1 : 0) }
    }
}
