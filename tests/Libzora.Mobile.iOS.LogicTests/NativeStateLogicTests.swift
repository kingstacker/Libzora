import Foundation

@main
struct NativeStateLogicTests {
    private struct Preferences: Codable, Equatable {
        var themeMode = 0
        var uiLanguage = "auto"
        var pdfZoom = 1.0
        var fontScale = 1.35
    }
    private static var assertions = 0
    private static func check(_ value: @autoclosure () -> Bool, _ message: String) {
        assertions += 1
        if !value() { fatalError("Regression failed: \(message)") }
    }

    static func main() throws {
        var edits = NativePreferenceEdits(Preferences())
        var next = edits.value
        next.themeMode = 1
        try edits.select(next)
        next.uiLanguage = "en-US"
        try edits.select(next)
        check(edits.changes.count == 2, "Coalesced edits retain both user choices")
        check(edits.changes["pdfZoom"] == nil, "Unedited renderer values are never sent")
        edits.failed(revision: edits.revision)
        check(edits.value == Preferences(), "Failure rolls back to the confirmed baseline, not the skipped choice")
        check(edits.changes.isEmpty, "Failed edits cannot leak into later saves")

        next = edits.value
        next.themeMode = 2
        try edits.select(next)
        let firstRevision = edits.revision
        let firstSaved = next
        next.uiLanguage = "zh-CN"
        try edits.select(next)
        try edits.saved(firstSaved, revision: firstRevision)
        check(edits.value.uiLanguage == "zh-CN", "An old successful reply preserves a newer tap")
        check(edits.changes.count == 1 && edits.changes["themeMode"] == nil, "Only the newer edit remains pending")
        edits.failed(revision: edits.revision)
        check(edits.value == firstSaved, "A later failure rolls back to the earlier successful save")

        next = edits.value
        next.fontScale = 1.5
        try edits.select(next)
        var renderer = edits.confirmed
        renderer.pdfZoom = 2.4
        try edits.receive(renderer)
        check(edits.value.pdfZoom == 2.4, "Renderer zoom reaches the native controls")
        check(edits.value.fontScale == 1.5, "A renderer event does not undo an optimistic font edit")
        check(edits.changes["pdfZoom"] == nil, "Renderer zoom is not accidentally written back as a user edit")
        renderer.fontScale = 1.5
        try edits.saved(renderer, revision: edits.revision)
        check(edits.value == renderer && edits.changes.isEmpty, "Successful save converges with managed preferences")

        next = edits.value
        next.themeMode = 1
        try edits.select(next)
        next.themeMode = 2
        try edits.select(next)
        check(edits.changes["themeMode"] as? Int == 2, "Returning to a previous value supersedes an in-flight edit")
        edits.failed(revision: edits.revision - 1)
        check(edits.value.themeMode == 2 && !edits.changes.isEmpty, "An old failure cannot discard a newer edit")

        check(NativeBookLinkResolution.decide(found: true, snapshotRevision: 1, requiredRevision: 2) == .open,
              "An existing book opens without an extra library refresh")
        check(NativeBookLinkResolution.decide(found: false, snapshotRevision: 1, requiredRevision: 2) == .refresh,
              "A missing widget target survives a stale snapshot")
        check(NativeBookLinkResolution.decide(found: false, snapshotRevision: 2, requiredRevision: 2) == .missing,
              "A genuinely deleted target stops after one fresh snapshot")
        for quick in 0...3 {
            check(NativeLibraryFilterPolicy.effectiveQuickFilter(quick, readingStatus: nil) == quick,
                  "Without an advanced status the quick filter remains unchanged")
            for status in ["0", "1", "2"] {
                let expected = quick == 1 || quick == 3 ? 0 : quick
                check(NativeLibraryFilterPolicy.effectiveQuickFilter(quick, readingStatus: status) == expected,
                      "Preview and Apply share the same status conflict rule")
            }
        }
        print("Passed \(assertions) iOS preference, widget-link and filter regression assertions.")
    }
}
