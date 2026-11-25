import Foundation
import CoreGraphics

/// Runs the production Foundation-only models and policies. No app launch,
/// simulator, network connection or library data is involved.
@main
struct NativeLibraryLogicTests {
    private static var assertions = 0

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        assertions += 1
        guard condition() else { fatalError("Regression failed: \(message)") }
    }

    private static func book(_ id: String, _ title: String, author: String = "Author", series: String? = nil,
                             index: Double? = nil, files: [String]? = nil) -> LibraryBook {
        LibraryBook(id: id, title: title, authors: author, formats: "EPUB", size: "1 MB", coverPath: nil,
                    progress: 0, favorite: false, readingStatus: 0, syncStatus: nil, collectionIds: [],
                    updatedAt: "2026-11-18T00:00:00Z", createdAt: "2026-11-18T00:00:00Z", description: nil,
                    series: series, seriesIndex: index, fileTitles: files, publisher: nil, publishDate: nil,
                    isbn: nil, pageCount: nil, binding: nil, tags: "", category: "", onlineRating: nil)
    }

    private static func stack(_ books: [LibraryBook]) -> NativeBookSeries {
        let entries = NativeBookSeries.entries(books)
        check(entries.count == 1, "Expected one series stack")
        guard case .series(let series) = entries[0] else { fatalError("Expected a series, not a book") }
        return series
    }

    private static func testAdaptiveLayout() {
        var lastBooks = 0
        var lastCollections = 0
        for width in stride(from: 260, through: 1400, by: 1) {
            let pageWidth = CGFloat(width)
            let books = NativeAdaptiveLayout.bookGrid(pageWidth: pageWidth)
            let collections = NativeAdaptiveLayout.collectionGrid(pageWidth: pageWidth)
            for grid in [books, collections] {
                let occupied = CGFloat(grid.columns) * grid.itemWidth
                    + CGFloat(grid.columns - 1) * grid.spacing + NativeAdaptiveLayout.pageInset * 2
                check(abs(occupied - pageWidth) < 0.001, "Grid fills the available width without overflow at \(width)")
                check(grid.columns > 0 && grid.itemWidth > 0, "Narrow windows retain usable grid geometry")
            }
            check(books.columns >= lastBooks, "Widening a window must not remove book columns")
            check(collections.columns >= lastCollections, "Widening a window must not remove collection columns")
            lastBooks = books.columns
            lastCollections = collections.columns
        }
        let phone = NativeAdaptiveLayout.bookGrid(pageWidth: 393)
        check(phone.columns == 3, "Keep iPhone 17's three-column shelf")
        check(abs(phone.itemWidth - (393 - 48 - 32) / 3) < 0.001, "Keep the existing phone cover width")
        check(NativeAdaptiveLayout.bookGrid(pageWidth: 320).columns == 2, "Compact iPad windows can use two columns")
        check(NativeAdaptiveLayout.collectionGrid(pageWidth: 393).columns == 2, "Keep the phone's collection layout")
        check(NativeAdaptiveLayout.collectionGrid(pageWidth: 820).columns == 4, "iPad portrait collections use four columns")
        check(NativeAdaptiveLayout.collectionGrid(pageWidth: 1180).columns == 6, "iPad landscape collections use six columns")
        check(NativeAdaptiveLayout.bookGrid(pageWidth: .nan).columns == 1, "Transient invalid widths are safe")
        check(NativeAdaptiveLayout.pageWidth(viewportWidth: 1180) == 1180,
              "All pages keep the shelf's full-width boundaries on iPad")
        check(NativeAdaptiveLayout.pageWidth(viewportWidth: 393) == 393,
              "Phone pages preserve their existing window boundaries")
        check(NativeAdaptiveLayout.pageInset == 24 && NativeAdaptiveLayout.mastheadHeight == 44
              && NativeAdaptiveLayout.mastheadTopInset == 16,
              "Every top-level page uses the shelf's title and edge geometry")
        check(NativeAdaptiveLayout.readerWidth(viewportWidth: 1180, isPdf: false) == 840, "EPUB text is constrained on iPad")
        check(NativeAdaptiveLayout.readerWidth(viewportWidth: 1180, isPdf: false, isTwoPage: true) == 1180,
              "EPUB two-page spread uses available width up to spread limit on iPad")
        check(NativeAdaptiveLayout.readerWidth(viewportWidth: 1366, isPdf: false, isTwoPage: true) == 1240,
              "EPUB two-page spread caps at spread maximum on 13-inch iPad")
        check(NativeAdaptiveLayout.readerWidth(viewportWidth: 1180, isPdf: true) == 1180, "PDF keeps its full-width viewport")
        check(NativeAdaptiveLayout.readerWidth(viewportWidth: 393, isPdf: false) == 393, "Phone reading remains full width")
        check(NativeAdaptiveLayout.isLandscapeSpread(
            viewportSize: CGSize(width: 1194, height: 834),
            isPdf: false, isManga: false, verticalWriting: false, twoPageInLandscape: true),
            "Full iPad landscape uses two-column spread")
        check(!NativeAdaptiveLayout.isLandscapeSpread(
            viewportSize: CGSize(width: 774, height: 834),
            isPdf: false, isManga: false, verticalWriting: false, twoPageInLandscape: true),
            "Docked AI sidebar narrowing reading viewport below height falls back to single column")
        check(!NativeAdaptiveLayout.isLandscapeSpread(
            viewportSize: CGSize(width: 680, height: 400),
            isPdf: false, isManga: false, verticalWriting: false, twoPageInLandscape: true),
            "Narrow landscape split view below 760pt stays single column")
        check(NativeAdaptiveLayout.noteWidth(viewportWidth: 1180) == 560, "An iPad note editor must not stretch across the screen")
        check(NativeAdaptiveLayout.dashboardColumns(pageWidth: 393) == 2, "Phone statistic cards remain in two columns")
        check(NativeAdaptiveLayout.dashboardColumns(pageWidth: 980) == 4, "All four statistics fill one wide row")

        let window = CGRect(x: 0, y: 0, width: 600, height: 700)
        check(NativeAdaptiveLayout.keyboardOverlap(windowBounds: window,
            keyboardFrame: CGRect(x: 0, y: 480, width: 600, height: 300)) == 220,
              "Docked keyboard overlap uses the resized window")
        check(NativeAdaptiveLayout.keyboardOverlap(windowBounds: window,
            keyboardFrame: CGRect(x: 100, y: 400, width: 300, height: 200)) == 0,
              "Floating keyboards must not lift the note card")
        check(NativeAdaptiveLayout.keyboardOverlap(windowBounds: window,
            keyboardFrame: CGRect(x: 700, y: 480, width: 600, height: 300)) == 0,
              "A keyboard in another window reserves nothing")
        check(NativeAdaptiveLayout.keyboardOverlap(windowBounds: window,
            keyboardFrame: CGRect(x: 0, y: 300, width: 600, height: 200)) == 0,
              "An undocked wide keyboard is not bottom padding")

        let safe = CGRect(x: 0, y: 24, width: 1180, height: 776)
        let padBounds = NativeAdaptiveLayout.popupBounds(in: CGSize(width: 1180, height: 820), safeArea: safe)
        check(padBounds.maxY == 792, "iPad top tabs do not reserve a phone-sized bottom strip")
        let bottomTabs = NativeAdaptiveLayout.popupBounds(in: CGSize(width: 1180, height: 820),
            safeArea: safe, bottomBarInset: 80)
        check(bottomTabs.maxY == 732, "A real bottom tab bar remains protected")
        for height in [CGFloat(160), 220, 320, 600, 1000] {
            for metadata in [CGFloat(62), 124] {
                let panel = NativeAdaptiveLayout.seriesPanel(in: CGSize(width: 600, height: height),
                    preferredCoverWidth: 220, headerHeight: 44, metadataHeight: metadata)
                check(panel.height <= height - 32, "Series preview fits short/resized windows")
                check(panel.width <= 568 && panel.coverWidth <= 160, "Series covers use panel geometry, not screen width")
                check(abs(panel.headerHeight + panel.rowHeight + 12 - panel.height) < 0.001,
                      "Series panel rows fit the actual panel height")
            }
        }
        check(NativeAdaptiveLayout.readerSettingsHeight(maxHeight: 1000, isPdf: false, isTablet: true) == 420,
              "Tablet typography sheets expose more controls")
        check(NativeAdaptiveLayout.readerSettingsHeight(maxHeight: 800, isPdf: false, isTablet: false) == 336,
              "Phone typography detents stay unchanged")
        check(NativeAdaptiveLayout.assistantSidebarWidth(contentWidth: 1180) == 420,
              "Default iPad AI sidebar keeps its balanced width")
        check(NativeAdaptiveLayout.assistantSidebarWidth(contentWidth: 1180, preferredWidth: 160) == 280,
              "AI sidebar width clamps at the readable minimum")
        check(NativeAdaptiveLayout.assistantSidebarWidth(contentWidth: 1180, preferredWidth: 800) == 600,
              "AI sidebar width leaves room for the reading surface")
    }

    static func main() {
        testAdaptiveLayout()
        let multiAuthor = stack([book("two", "Second", author: "B", series: "Collection", index: 2),
                                 book("one", "First", author: "A", series: "Collection", index: 1)])
        check(multiAuthor.books.map(\.id) == ["one", "two"], "Explicit multi-author series must group and sort")
        check(multiAuthor.id.authors.isEmpty, "Explicit series identity must not depend on authors")

        let folded = stack([book("two", "Second", series: "Ｃｏｌｌｅｃｔｉｏｎ", index: 2),
                            book("one", "First", series: " collection ", index: 1)])
        check(folded.id == multiAuthor.id, "Series identity folds width, case and surrounding whitespace")

        let inferred = stack([book("two", "Story Volume 2"), book("one", "Story Volume 1")])
        check(inferred.books.map(\.id) == ["one", "two"], "Inferred volume numbers define ordering")
        let differentAuthors = NativeBookSeries.entries([book("one", "Story Volume 1", author: "A"),
                                                        book("two", "Story Volume 2", author: "B")])
        check(differentAuthors.count == 2, "Inference must still disambiguate different authors")

        let mixed = stack([book("one", "First", series: "Story", index: 1), book("two", "Story Volume 2")])
        check(mixed.books.map(\.id) == ["one", "two"], "Matching author inference joins explicit metadata")
        let ambiguous = NativeBookSeries.entries([book("one", "First", author: "A", series: "Story", index: 1),
                                                 book("two", "Story Volume 2", author: "B")])
        check(ambiguous.count == 2, "Unrelated inferred authors must not join an explicit series")

        let cjk = stack([book("two", "故事 第2卷"), book("one", "故事 第1卷")])
        check(cjk.books.map(\.id) == ["one", "two"], "Escaped CJK volume markers still match")
        check(NativeBookSeries.entries([book("one", "History 1984"), book("two", "History 1985")]).count == 2,
              "Bare years are not volume markers")
        check(NativeBookSeries.entries([book("one", "Story Volume 1"), book("two", "Story Volume 1")]).count == 2,
              "Same-number inferred editions are not a multi-volume stack")
        check(NativeBookSeries.groups([book("one", "First", files: ["Story Volume 1", "Story Volume 2"])]).isEmpty,
              "Conflicting format filenames cannot infer a volume")

        let updated = NativeBookSeries.groups([book("one", "First", author: "A", series: "Collection", index: 3),
                                               book("two", "Second", author: "B", series: "Collection", index: 1),
                                               book("three", "Third", author: "C", series: "Collection", index: 2)])
        check(updated[multiAuthor.id]?.books.map(\.id) == ["two", "three", "one"],
              "Fresh detail snapshots include new members and updated ordering")
        let moved = NativeBookSeries.groups([book("one", "First", series: "Other", index: 1),
                                             book("two", "Second", series: "Collection", index: 2)])
        check(moved[multiAuthor.id]?.books.map(\.id) == ["two"], "Moved members must disappear from the old series")
        check(NativeBookSeries.entries([book("two", "Second", series: "Collection", index: 2)]).count == 1,
              "A single remaining volume appears as an ordinary shelf book")
        check(NativeBookSeries.groups([])[multiAuthor.id] == nil, "Removed series has no stale detail snapshot")
        let finite = stack([book("one", "Story Volume 1", series: "Story", index: .infinity),
                            book("two", "Story Volume 2", series: "Story", index: 2)])
        check(finite.books.map(\.id) == ["one", "two"], "Non-finite positions fall back to safe title inference")

        for operation in ["save", "run"] {
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "") == .mismatch,
                  "\(operation) rejects missing second key input")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "different") == .mismatch,
                  "\(operation) rejects inconsistent key input")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "new") == .confirm,
                  "\(operation) must request explicit confirmation before persistence")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "", savedKey: "old", confirmation: "") == .confirm,
                  "\(operation) must confirm clearing an existing key")
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "old", savedKey: "old", confirmation: "") == .proceed,
                  "\(operation) preserves normal use of an unchanged key")
        }
        for operation in ["test", "import", "export"] {
            check(NativeSyncKeyConfirmation.decision(operation: operation, key: "new", savedKey: "old", confirmation: "") == .proceed,
                  "\(operation) does not persist the encryption key")
        }

        check(NativeSyncKeyConfirmation.canEnableSync(key: "", savedKey: "", confirmation: "") == .missingKey,
              "Enabling sync with empty key requires setting key")
        check(NativeSyncKeyConfirmation.canEnableSync(key: "   ", savedKey: "old", confirmation: "") == .missingKey,
              "Enabling sync after clearing key requires setting key")
        check(NativeSyncKeyConfirmation.canEnableSync(key: "new", savedKey: "", confirmation: "") == .unconfirmedKey,
              "Enabling sync with unconfirmed new key prompts for confirmation")
        check(NativeSyncKeyConfirmation.canEnableSync(key: "new", savedKey: "old", confirmation: "diff") == .unconfirmedKey,
              "Enabling sync with mismatched key confirmation prompts for confirmation")
        check(NativeSyncKeyConfirmation.canEnableSync(key: "new", savedKey: "", confirmation: "new") == .allow,
              "Enabling sync succeeds once new key matches confirmation")
        check(NativeSyncKeyConfirmation.canEnableSync(key: "saved", savedKey: "saved", confirmation: "") == .allow,
              "Enabling sync succeeds when key is already saved")

        // Batch book selection policy.
        let toggled = NativeLibraryBatchPolicy.toggle("b1", in: [])
        check(toggled == ["b1"], "Toggling an unselected book adds it")
        check(NativeLibraryBatchPolicy.toggle("b1", in: toggled).isEmpty, "Toggling again removes it")
        check(NativeLibraryBatchPolicy.toggle("", in: []).isEmpty, "Empty IDs are ignored")

        let allSelected = NativeLibraryBatchPolicy.toggleAll(visibleIds: ["b1", "b2"], in: ["b3"])
        check(allSelected == ["b1", "b2", "b3"], "Selecting all visible books preserves off-filter selection")
        check(NativeLibraryBatchPolicy.areAllVisibleSelected(visibleIds: ["b1", "b2"], in: allSelected),
              "All visible books are recognized as selected")
        let deselectedVisible = NativeLibraryBatchPolicy.toggleAll(visibleIds: ["b1", "b2"], in: allSelected)
        check(deselectedVisible == ["b3"], "Deselecting all visible books keeps other selected IDs intact")
        check(!NativeLibraryBatchPolicy.areAllVisibleSelected(visibleIds: [], in: ["b1"]),
              "Empty visible shelf does not report all selected")
        check(NativeLibraryBatchPolicy.pruned(["b1", "b2", "gone"], validIds: ["b1", "b2"]) == ["b1", "b2"],
              "Pruning drops IDs no longer in the library")
        check(NativeLibraryBatchPolicy.selectedCount(in: ["b1", "b2", "b3"], from: ["b1", "b3"]) == 2,
              "Series selection count matches selected member volumes")

        // Interactive scroll-driven library masthead & mask policy.
        var chrome = NativeLibraryScrollChromePolicy()
        chrome.updateBounds(contentHeight: 2000, viewportHeight: 800)
        chrome.updateMastheadHeight(64)

        // Scrolling down from top keeps floating masthead hidden while fading in status-bar glass.
        chrome.update(clampedOffset: 24)
        check(chrome.mastheadProgress == 0, "Scrolling down from top keeps floating masthead hidden")
        check(chrome.glassOpacity > 0 && chrome.glassOpacity < 1, "Glass opacity ramps during initial scroll")
        chrome.update(clampedOffset: 300)
        check(chrome.mastheadProgress == 0 && chrome.glassOpacity == 1,
              "Deep downward scroll keeps floating masthead hidden and glass fully opaque")

        // Dragging back up reveals masthead and mask 1:1 with scroll distance.
        chrome.update(clampedOffset: 268) // -32pt => 50%
        check(abs(chrome.mastheadProgress - 0.5) < 0.001,
              "32pt upward scroll reveals half of a 64pt masthead (got \(chrome.mastheadProgress))")
        chrome.update(clampedOffset: 236) // -32pt => 100%
        check(abs(chrome.mastheadProgress - 1.0) < 0.001,
              "64pt upward scroll fully reveals the masthead")

        // Reversing downward hides masthead and mask 1:1 with scroll distance.
        chrome.update(clampedOffset: 252) // +16pt => 75%
        check(abs(chrome.mastheadProgress - 0.75) < 0.001,
              "16pt downward scroll hides 25% of the masthead (got \(chrome.mastheadProgress))")
        chrome.update(clampedOffset: 300) // +48pt => 0%
        check(chrome.mastheadProgress == 0,
              "64pt total downward scroll hides the masthead completely")

        // Returning from bottom overscroll must not reveal the masthead.
        chrome.update(clampedOffset: 1200)
        chrome.update(clampedOffset: 1350) // clamped to maximumOffset 1200
        chrome.update(clampedOffset: 1200)
        check(chrome.mastheadProgress == 0,
              "Bottom overscroll rubber-band does not trigger masthead reveal")

        // Reaching the very top hands off cleanly to the inline header with zero glass.
        chrome.update(clampedOffset: 300)
        chrome.update(clampedOffset: 200) // fully revealed
        check(chrome.mastheadProgress == 1, "Upward scroll reveals floating masthead before reaching top")
        chrome.update(clampedOffset: 20)
        check(chrome.mastheadProgress >= 1 - 20.0 / 64.0,
              "Top-zone invariant prevents inline header pop-in while floating masthead is active")
        chrome.update(clampedOffset: 0)
        check(chrome.mastheadProgress == 0 && chrome.glassOpacity == 0,
              "Returning to offset 0 resets floating masthead and glass for seamless inline handoff")

        // Idle settle completes partial reveals/hides without snapping prematurely while scrolling.
        chrome.update(clampedOffset: 300)
        chrome.update(clampedOffset: 280) // -20pt => 0.3125
        check(chrome.settleTarget() == 0 && chrome.mastheadProgress == 0,
              "Idle settle below 50% hides the masthead")
        chrome.update(clampedOffset: 240) // -40pt => 0.625
        check(chrome.settleTarget() == 1 && chrome.mastheadProgress == 1,
              "Idle settle at or above 50% completes the reveal")

        check(!NativeAdaptiveLayout.isWideBookDetail(viewportSize: CGSize(width: 820, height: 1180)),
              "Portrait iPad uses the stacked phone-style book detail layout instead of an empty two-column split")
        check(NativeAdaptiveLayout.isWideBookDetail(viewportSize: CGSize(width: 1180, height: 820)),
              "Landscape iPad keeps the two-column book detail layout")
        check(!NativeAdaptiveLayout.isWideBookDetail(viewportSize: CGSize(width: 759, height: 390)),
              "Wide breakpoint applies only at or above 760pt width in landscape")

        print("Passed \(assertions) iOS adaptive-layout, library and sync-key logic assertions.")
    }
}
