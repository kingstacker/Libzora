import Foundation

/// Tracks field-level optimistic edits separately from the last managed snapshot.
/// A renderer update must not undo a tap, and a failed/coalesced save must not
/// turn an unsaved optimistic value into the rollback baseline.
struct NativePreferenceEdits<Value: Codable & Equatable> {
    private(set) var confirmed: Value
    private(set) var value: Value
    private(set) var revision = 0
    private var edits: [String: (revision: Int, value: Any)] = [:]

    init(_ value: Value) { self.confirmed = value; self.value = value }

    mutating func select(_ next: Value) throws {
        let before = try object(value)
        let after = try object(next)
        revision += 1
        for (key, entry) in after where !NSDictionary(dictionary: [key: entry]).isEqual(to: [key: before[key] ?? NSNull()]) {
            edits[key] = (revision, entry)
        }
        value = next
    }

    var changes: [String: Any] { edits.mapValues(\.value) }

    mutating func receive(_ snapshot: Value) throws {
        var merged = try object(snapshot)
        for (key, edit) in edits { merged[key] = edit.value }
        let next = try JSONDecoder().decode(Value.self, from: JSONSerialization.data(withJSONObject: merged))
        confirmed = snapshot
        value = next
    }

    mutating func saved(_ snapshot: Value, revision savedRevision: Int) throws {
        edits = edits.filter { $0.value.revision > savedRevision }
        try receive(snapshot)
    }

    mutating func failed(revision failedRevision: Int) {
        guard revision == failedRevision else { return }
        edits.removeAll()
        value = confirmed
    }

    private func object(_ value: Value) throws -> [String: Any] {
        guard let result = try JSONSerialization.jsonObject(with: JSONEncoder().encode(value)) as? [String: Any] else {
            throw CocoaError(.coderInvalidValue)
        }
        return result
    }
}

/// A missing link needs one fresh snapshot, not an infinite refresh loop.
enum NativeBookLinkResolution: Equatable {
    case open, refresh, missing
    static func decide(found: Bool, snapshotRevision: Int, requiredRevision: Int) -> Self {
        if found { return .open }
        return snapshotRevision >= requiredRevision ? .missing : .refresh
    }
}

/// Advanced reading-status choices replace the conflicting status quick filter.
enum NativeLibraryFilterPolicy {
    static func effectiveQuickFilter(_ quickFilter: Int, readingStatus: String?) -> Int {
        readingStatus != nil && (quickFilter == 1 || quickFilter == 3) ? 0 : quickFilter
    }
}
