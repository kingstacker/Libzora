import Foundation

/// Foundation-only policies shared by native controls and regression tests.
struct NativeReflectionEditState: Equatable {
    private(set) var loaded = false
    private(set) var busy = false
    var canEdit: Bool { loaded && !busy }
    mutating func beginLoad() { loaded = false }
    mutating func finishLoad(succeeded: Bool) { loaded = succeeded }
    mutating func beginWrite() -> Bool {
        guard canEdit else { return false }
        busy = true
        return true
    }
    mutating func finishWrite() { busy = false }
}

enum NativeOnboardingPolicy {
    static func canOpenReader(confirmedAcceptance: Bool, debugBypass: Bool = false) -> Bool {
        confirmedAcceptance || debugBypass
    }
}

struct NativeDeletionResult: Decodable {
    let deleted: Bool
}

struct NativePendingMaterialDeletes {
    private(set) var ids: Set<String> = []
    mutating func begin(_ id: String) -> Bool { ids.insert(id).inserted }
    mutating func finish(_ id: String) { ids.remove(id) }
    func includes(_ id: String) -> Bool { ids.contains(id) }
}
