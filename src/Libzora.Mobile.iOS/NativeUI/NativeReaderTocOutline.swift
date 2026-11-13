import Foundation

/// TOC entries arrive in reading order; deeper levels belong to the previous parent.
enum NativeReaderTocOutline {
    struct Row: Identifiable {
        let item: ReaderToc
        let hasChildren: Bool
        var id: Int { item.id }
    }

    static func parentIDs(_ items: [ReaderToc]) -> Set<Int> {
        Set(items.indices.dropLast().compactMap { index in
            items[index + 1].level > items[index].level ? items[index].id : nil
        })
    }

    static func visibleRows(_ items: [ReaderToc], collapsed: Set<Int>) -> [Row] {
        let parents = parentIDs(items)
        var rows: [Row] = []
        var hiddenBelowLevel: Int?
        for item in items {
            if let level = hiddenBelowLevel {
                if item.level > level { continue }
                hiddenBelowLevel = nil
            }
            let hasChildren = parents.contains(item.id)
            rows.append(Row(item: item, hasChildren: hasChildren))
            if hasChildren && collapsed.contains(item.id) { hiddenBelowLevel = item.level }
        }
        return rows
    }
}
