import CoreGraphics
import Foundation

@main
struct NativeStateLogicTests {
    private struct Preferences: Codable, Equatable {
        var themeMode = 0
        var uiLanguage = "auto"
        var uiFont = 0
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

        var fontEdits = NativePreferenceEdits(Preferences())
        var switchedFont = fontEdits.value
        switchedFont.uiFont = 1
        try fontEdits.select(switchedFont)
        check(fontEdits.changes["uiFont"] as? Int == 1, "Interface font toggle records only the edited uiFont field")
        try fontEdits.saved(switchedFont, revision: fontEdits.revision)
        check(fontEdits.value.uiFont == 1 && fontEdits.changes.isEmpty, "Saved interface font converges cleanly")
        switchedFont.uiFont = 0
        try fontEdits.select(switchedFont)
        fontEdits.failed(revision: fontEdits.revision)
        check(fontEdits.value.uiFont == 1, "Failed interface font save rolls back to confirmed choice")

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

        var search = NativeReaderSearchAccumulator()
        search.reset(searchId: "s1", query: "pond", busy: true)
        let hitLate = ReaderSearchHit(id: 0, title: "Ch 3", excerpt: "late", chapterIndex: 2, offset: 40)
        let hitEarly = ReaderSearchHit(id: 1, title: "Ch 1", excerpt: "early", chapterIndex: 0, offset: 10)
        check(search.apply(ReaderSearchBatch(generation: 1, searchId: "s1", query: "pond",
                                             items: [hitLate], scannedChapters: 2, totalChapters: 5,
                                             isFinal: false, error: nil, hasMore: false, isIncremental: true, totalHits: 1)),
              "Current search batch is accepted")
        check(!search.apply(ReaderSearchBatch(generation: 1, searchId: "stale", query: "pond",
                                              items: [hitEarly], scannedChapters: 1, totalChapters: 5,
                                              isFinal: true, error: nil, hasMore: false, isIncremental: true, totalHits: 1)),
              "Stale search IDs are ignored")
        check(search.apply(ReaderSearchBatch(generation: 1, searchId: "s1", query: "pond",
                                             items: [hitLate, hitEarly], scannedChapters: 3, totalChapters: 5,
                                             isFinal: true, error: nil, hasMore: true, isIncremental: true, totalHits: 2)),
              "Wrapped earlier chapter delta merges in reading order without duplicating IDs")
        check(search.hits.map(\.id) == [1, 0], "Merged search hits stay sorted by chapter and offset")
        check(search.hasMore && !search.busy && search.isFinal, "Paused search retains hasMore for continuation")

        search.beginContinue()
        check(search.busy && search.loadingMore && !search.isFinal, "Continuation marks search as loading more")
        let hitEnd = ReaderSearchHit(id: 2, title: "Ch 5", excerpt: "end", chapterIndex: 4, offset: 80)
        check(search.apply(ReaderSearchBatch(generation: 1, searchId: "s1", query: "pond",
                                             items: [hitEnd], scannedChapters: 5, totalChapters: 5,
                                             isFinal: true, error: nil, hasMore: false, isIncremental: true, totalHits: 3)),
              "Continuation batch appends remaining hits")
        check(search.hits.map(\.id) == [1, 0, 2] && !search.hasMore && !search.loadingMore && !search.busy,
              "Completed continuation clears loadingMore and hasMore")

        var cursor = NativeReaderSearchCursor()
        cursor.select(0, navigationRevision: 7)
        check(cursor.neighbor(-1, in: search.hits.map(\.id)) == 1 && cursor.neighbor(1, in: search.hits.map(\.id)) == 2,
              "Search cursor tracks stable hit IDs across wrapped and continued insertions")

        // Book open/close transition geometry, widget entry, and top-bar anti-flash policy.
        let viewport = CGRect(x: 0, y: 0, width: 393, height: 852)
        let centered = NativeBookOpenTransitionPolicy.centeredFrame(in: viewport)
        check(abs(centered.midX - viewport.midX) < 0.5 && abs(centered.midY - viewport.midY) < 0.5,
              "Centered hero cover sits squarely in the middle of the viewport")
        check(abs(centered.height / centered.width - NativeBookOpenTransitionPolicy.coverAspectRatio) < 0.01,
              "Centered hero cover preserves the shelf cover aspect ratio")

        let visibleShelfFrame = CGRect(x: 20, y: 260, width: 110, height: 159.5)
        let offscreenShelfFrame = CGRect(x: 20, y: -220, width: 110, height: 159.5)
        let buriedTabBarFrame = CGRect(x: 20, y: 810, width: 110, height: 159.5)
        let continueFrame = CGRect(x: 20, y: 140, width: 48, height: 68)
        check(NativeBookOpenTransitionPolicy.isVisibleFrame(visibleShelfFrame, in: viewport),
              "On-screen shelf cover counts as visible")
        check(!NativeBookOpenTransitionPolicy.isVisibleFrame(offscreenShelfFrame, in: viewport),
              "Scrolled-away cover above the viewport is rejected")
        check(!NativeBookOpenTransitionPolicy.isVisibleFrame(buriedTabBarFrame, in: viewport),
              "Cover mostly buried below the bottom tab bar is rejected")

        let widgetTarget = NativeBookOpenTransitionPolicy.resolveOpenTarget(
            bookId: "b1",
            explicitFrame: nil,
            explicitKey: nil,
            fromWidget: true,
            activeTab: 0,
            viewport: viewport,
            lookup: { key in key == "shelf:b1" ? visibleShelfFrame : continueFrame }
        )
        check(widgetTarget == .centered(frame: centered) && widgetTarget.sourceKey == nil && widgetTarget.isCentered,
              "Widget entry always expands from a centered hero card instead of a shelf corner")

        let offscreenShelfTarget = NativeBookOpenTransitionPolicy.resolveOpenTarget(
            bookId: "b1",
            explicitFrame: nil,
            explicitKey: nil,
            fromWidget: false,
            activeTab: 0,
            viewport: viewport,
            lookup: { key in
                if key == "shelf:b1" { return offscreenShelfFrame }
                if key == "continue:b1" { return continueFrame }
                return nil
            }
        )
        check(offscreenShelfTarget == .cover(frame: continueFrame, key: "continue:b1"),
              "Off-screen shelf frame falls through to the visible continue-reading cover")

        let otherTabOpenTarget = NativeBookOpenTransitionPolicy.resolveOpenTarget(
            bookId: "b1",
            explicitFrame: nil,
            explicitKey: nil,
            fromWidget: false,
            activeTab: 2,
            viewport: viewport,
            lookup: { key in key == "shelf:b1" ? visibleShelfFrame : nil }
        )
        check(otherTabOpenTarget == .centered(frame: centered),
              "Non-library tabs never borrow an invisible library shelf cover frame")

        let closeAfterWidgetOnLibrary = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: nil,
            activeTab: 0,
            viewport: viewport,
            lookup: { key in key == "shelf:b1" ? visibleShelfFrame : nil }
        )
        check(closeAfterWidgetOnLibrary == .cover(frame: visibleShelfFrame, key: "shelf:b1"),
              "Closing a widget-opened book lands on its live visible shelf cover when available")

        let closeSeriesFallback = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "vol2",
            preferredKey: "seriesVolume:vol2",
            activeTab: 0,
            viewport: viewport,
            lookup: { key in key == "shelf:vol1" ? visibleShelfFrame : nil },
            seriesFrontBookId: "vol1"
        )
        check(closeSeriesFallback == .cover(frame: visibleShelfFrame, key: "shelf:vol1"),
              "Closing a dismissed series volume lands on its visible series stack cover")

        let closeOnOtherTab = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "shelf:b1",
            activeTab: 2,
            viewport: viewport,
            lookup: { key in key == "shelf:b1" ? visibleShelfFrame : nil }
        )
        check(closeOnOtherTab == .centered(frame: centered),
              "Closing on a non-library tab folds to center instead of an invisible shelf slot")

        let closeAfterWidgetOffscreenShelf = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b12",
            preferredKey: nil,
            activeTab: 0,
            viewport: viewport,
            lookup: { key in
                if key == "continue:b12" { return continueFrame }
                return nil
            }
        )
        check(closeAfterWidgetOffscreenShelf == .cover(frame: continueFrame, key: "continue:b12"),
              "Closing a widget-opened book below the fold keeps the shelf at top and lands on the Continue Reading card")

        let materialsFrame = CGRect(x: 20, y: 180, width: 48, height: 68)
        let materialsOpenTarget = NativeBookOpenTransitionPolicy.resolveOpenTarget(
            bookId: "b1",
            explicitFrame: materialsFrame,
            explicitKey: "materials:b1",
            fromWidget: false,
            activeTab: 1,
            viewport: viewport,
            lookup: { key in
                if key == "materials:b1" { return materialsFrame }
                if key == "shelf:b1" { return visibleShelfFrame }
                return nil
            }
        )
        check(materialsOpenTarget == .cover(frame: materialsFrame, key: "materials:b1"),
              "Opening an annotation from Reading Materials anchors to that book's visible cover")

        let materialsCloseTarget = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "materials:b1",
            activeTab: 1,
            viewport: viewport,
            lookup: { key in
                if key == "materials:b1" { return materialsFrame }
                if key == "shelf:b1" { return visibleShelfFrame }
                return nil
            }
        )
        check(materialsCloseTarget == .cover(frame: materialsFrame, key: "materials:b1"),
              "Closing a book opened from Reading Materials lands back on its visible Reading Materials cover")

        let materialsScrolledOffClose = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "materials:b1",
            activeTab: 1,
            viewport: viewport,
            lookup: { key in
                if key == "materials:b1" { return offscreenShelfFrame }
                if key == "shelf:b1" { return visibleShelfFrame }
                return nil
            }
        )
        check(materialsScrolledOffClose == .centered(frame: centered),
              "Closing to Reading Materials when the book header scrolled off-screen folds cleanly to center")

        let occludedByStatusGlass = CGRect(x: 24, y: 12, width: 48, height: 68)
        let materialsOccludedClose = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "materials:b1",
            activeTab: 1,
            viewport: viewport,
            lookup: { key in key == "materials:b1" ? occludedByStatusGlass : nil },
            topInset: 75,
            bottomInset: 91
        )
        check(materialsOccludedClose == .centered(frame: centered),
              "Closing when the cover sits behind the status bar glass or tab bar folds to center")

        let materialsDetailOpen = NativeBookOpenTransitionPolicy.resolveOpenTarget(
            bookId: "b1",
            explicitFrame: materialsFrame,
            explicitKey: "materialsDetail:b1",
            fromWidget: false,
            activeTab: 1,
            viewport: viewport,
            lookup: { key in
                if key == "materialsDetail:b1" { return materialsFrame }
                if key == "materials:b1" { return offscreenShelfFrame }
                return nil
            }
        )
        check(materialsDetailOpen == .cover(frame: materialsFrame, key: "materialsDetail:b1"),
              "Opening an annotation from pushed Book Materials detail anchors to its detail header cover")

        let materialsDetailClose = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "materialsDetail:b1",
            activeTab: 1,
            viewport: viewport,
            lookup: { key in
                if key == "materialsDetail:b1" { return materialsFrame }
                if key == "materials:b1" { return offscreenShelfFrame }
                return nil
            }
        )
        check(materialsDetailClose == .cover(frame: materialsFrame, key: "materialsDetail:b1"),
              "Closing from an annotation lands back on the pushed Book Materials detail cover when visible")

        let materialsDetailScrolledClose = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "materialsDetail:b1",
            activeTab: 1,
            viewport: viewport,
            lookup: { key in
                if key == "materialsDetail:b1" { return offscreenShelfFrame }
                if key == "materials:b1" { return materialsFrame }
                return nil
            }
        )
        check(materialsDetailScrolledClose == .centered(frame: centered),
              "When Book Materials detail header scrolled off-screen, closing folds to center instead of the covered root list")

        let dashboardRecentFrame = CGRect(x: 24, y: 420, width: 96, height: 138)
        let dashboardMostReadFrame = CGRect(x: 24, y: 620, width: 38, height: 54)
        let dashboardOpenRecent = NativeBookOpenTransitionPolicy.resolveOpenTarget(
            bookId: "b1",
            explicitFrame: dashboardRecentFrame,
            explicitKey: "dashboardRecent:b1",
            fromWidget: false,
            activeTab: 2,
            viewport: viewport,
            lookup: { key in
                if key == "dashboardRecent:b1" { return dashboardRecentFrame }
                if key == "dashboardMostRead:b1" { return dashboardMostReadFrame }
                return nil
            }
        )
        check(dashboardOpenRecent == .cover(frame: dashboardRecentFrame, key: "dashboardRecent:b1"),
              "Tapping a recent book on Reading Dashboard expands from its cover")

        let dashboardCloseMostRead = NativeBookOpenTransitionPolicy.resolveCloseTarget(
            bookId: "b1",
            preferredKey: "dashboardMostRead:b1",
            activeTab: 2,
            viewport: viewport,
            lookup: { key in
                if key == "dashboardRecent:b1" { return dashboardRecentFrame }
                if key == "dashboardMostRead:b1" { return dashboardMostReadFrame }
                return nil
            }
        )
        check(dashboardCloseMostRead == .cover(frame: dashboardMostReadFrame, key: "dashboardMostRead:b1"),
              "Closing a book opened from Most Read returns to its Most Read cover instead of Recent")

        check(!NativeBookOpenTransitionPolicy.shouldShowReaderTopBar(
                  chromeVisible: false, ready: false, searching: false, hasError: false, loadingChromeVisible: false),
              "Top toolbar stays hidden during the initial fast book-open window so the search bar never flashes")
        check(NativeBookOpenTransitionPolicy.shouldShowReaderTopBar(
                  chromeVisible: false, ready: false, searching: false, hasError: true, loadingChromeVisible: false),
              "Load errors immediately reveal the top toolbar so the reader can be dismissed")
        check(NativeBookOpenTransitionPolicy.shouldShowReaderTopBar(
                  chromeVisible: false, ready: false, searching: false, hasError: false, loadingChromeVisible: true),
              "Slow loads reveal the top toolbar after the grace period")
        check(NativeBookOpenTransitionPolicy.shouldShowReaderTopBar(
                  chromeVisible: true, ready: true, searching: false, hasError: false, loadingChromeVisible: false),
              "Tapping the page reveals the top toolbar normally once ready")

        print("Passed \(assertions) iOS preference, widget-link, filter, search and transition regression assertions.")
    }
}
