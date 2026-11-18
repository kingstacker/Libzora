/// Every operation that persists the sync form uses the same key-change gate.
enum NativeSyncKeyConfirmation {
    enum Decision: Equatable { case proceed, mismatch, confirm }

    static func decision(operation: String, key: String, savedKey: String, confirmation: String) -> Decision {
        guard (operation == "save" || operation == "run"), key != savedKey else { return .proceed }
        if !key.isEmpty && key != confirmation { return .mismatch }
        return .confirm
    }
}
