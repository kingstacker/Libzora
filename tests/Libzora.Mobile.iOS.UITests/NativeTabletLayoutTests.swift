import XCTest
import UIKit

/// Runs only on an explicitly selected iPad, using that simulator's isolated
/// preview library. No installed iPhone library or system mouse is involved.
final class NativeTabletLayoutTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        guard UIDevice.current.userInterfaceIdiom == .pad else {
            throw XCTSkip("Select an iPad simulator for tablet layout checks.")
        }
        continueAfterFailure = false
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.nativepreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
    }

    override func tearDownWithError() throws {
        guard let app else { return }
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.lifetime = .keepAlways
        add(attachment)
        app.terminate()
        XCUIDevice.shared.orientation = .portrait
    }

    func testLibraryAndCollectionsReflowAcrossRotation() {
        selectLayout("网格视图", identifier: "books.grid")
        let portraitColumns = firstBookRow().count
        XCTAssertGreaterThanOrEqual(portraitColumns, 4)
        checkpoint("iPad-书库-竖屏")

        rotate(.landscapeLeft)
        XCTAssertGreaterThan(firstBookRow().count, portraitColumns)
        checkpoint("iPad-书库-横屏")

        selectLayout("收藏夹视图", identifier: "collections.grid")
        defer { selectLayout("网格视图", identifier: "books.grid") }
        let folder = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.' AND identifier != 'collection.create'")).firstMatch
        XCTAssertTrue(folder.waitForExistence(timeout: 10))
        XCTAssertLessThanOrEqual(folder.frame.width, 220, "Landscape collections must not stretch to half the window.")
        checkpoint("iPad-收藏夹-横屏")

        rotate(.portrait)
        XCTAssertTrue(element("collections.grid").exists, "Rotation must retain the chosen library view.")
        XCTAssertLessThanOrEqual(folder.frame.width, 260)
        checkpoint("iPad-收藏夹-竖屏")
    }

    func testProfileAndBookDetailsRetainNavigationAcrossRotation() {
        rotate(.landscapeLeft)
        selectTab("我的")
        let content = element("profile.content")
        XCTAssertTrue(content.waitForExistence(timeout: 15))
        assertCentered(content, maximumWidth: window.frame.width)
        app.buttons["profile.theme"].tap()
        let menu = app.buttons["深色"]
        XCTAssertTrue(menu.waitForExistence(timeout: 10))
        XCTAssertLessThanOrEqual(menu.frame.width, 251)
        XCTAssertGreaterThanOrEqual(menu.frame.minX, window.frame.minX)
        XCTAssertLessThanOrEqual(menu.frame.maxX, window.frame.maxX)
        checkpoint("iPad-设置-液态玻璃菜单")

        rotate(.portrait)
        XCTAssertTrue(waitUntil { !menu.exists }, "A resized window must dismiss its obsolete menu anchor.")
        assertCentered(content, maximumWidth: window.frame.width)
        app.buttons["profile.librarySettings"].tap()
        let settings = app.switches["library.settings.colorful"]
        XCTAssertTrue(settings.waitForExistence(timeout: 10))
        rotate(.landscapeLeft)
        XCTAssertTrue(settings.exists && settings.isHittable, "Rotation must keep the settings navigation path.")
        checkpoint("iPad-书库设置-横屏")
        app.navigationBars.buttons.firstMatch.tap()
        XCTAssertTrue(content.waitForExistence(timeout: 10))

        selectTab("书库")
        selectLayout("网格视图", identifier: "books.grid")
        guard let book = firstBookRow().first else { return XCTFail("Expected real cover books in the preview.") }
        book.press(forDuration: 0.8)
        let details = app.buttons["book.action.details"]
        XCTAssertTrue(details.waitForExistence(timeout: 10))
        details.tap()
        let read = app.buttons["book.read"]
        XCTAssertTrue(read.waitForExistence(timeout: 10))
        checkpoint("iPad-书籍详情-横屏")
        assertCentered(read, maximumWidth: 420)
        // A group's accessibility frame unions visible text, not the hidden
        // cover or blank padding. The full-width action has a real hit rectangle.
        XCTAssertLessThanOrEqual(element("book.detail.content").frame.width, window.frame.width)
        let header = element("book.detail.header")
        let information = element("book.detail.information")
        XCTAssertLessThanOrEqual(header.frame.maxX, information.frame.minX + 1,
                                 "A wide detail view places the cover beside the information.")

        rotate(.portrait)
        XCTAssertTrue(read.exists && read.isHittable, "Rotation must not pop the book detail page.")
        if window.frame.width < 760 || window.frame.width < window.frame.height {
            XCTAssertGreaterThanOrEqual(information.frame.minY, header.frame.maxY - 1)
        } else {
            XCTAssertLessThanOrEqual(header.frame.maxX, information.frame.minX + 1)
        }
        checkpoint("iPad-书籍详情-竖屏")
        app.buttons["book.back"].tap()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 10))
    }

    func testReaderReflowsWithoutResettingProgress() {
        rotate(.landscapeLeft)
        selectLayout("网格视图", identifier: "books.grid")
        app.buttons["library.search.toggle"].tap()
        let search = app.textFields["library.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 10))
        search.tap()
        search.typeText("Native UI EPUB")
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(epub.waitForExistence(timeout: 15))
        epub.tap()
        revealReaderControls()
        let progress = app.sliders["reader.progress"]
        XCTAssertTrue(progress.waitForExistence(timeout: 10))
        progress.adjust(toNormalizedSliderPosition: 0.45)
        RunLoop.current.run(until: Date().addingTimeInterval(1.0))
        checkpoint("iPad-阅读-横屏工具栏")
        let page = element("reader.page")
        let expectedLandscapeWidth = min(window.frame.width, 1240)
        XCTAssertEqual(page.frame.width, expectedLandscapeWidth, accuracy: 3)
        assertCentered(page, maximumWidth: 1240)
        let toolbar = element("reader.toolbar")
        XCTAssertLessThanOrEqual(toolbar.frame.width, 720)
        let before = progressValue(progress)

        rotate(.portrait)
        revealReaderControls()
        XCTAssertEqual(progressValue(progress), before, accuracy: 1,
                       "Viewport reflow must preserve the reading position instead of reopening at the cover.")
        XCTAssertEqual(page.frame.width, min(window.frame.width, 840), accuracy: 3)
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.35)).tap()
        XCTAssertTrue(waitUntil { !self.app.buttons["reader.close"].isHittable })
        checkpoint("iPad-阅读-竖屏正文")

        revealReaderControls()
        app.buttons["reader.排版"].tap()
        let panel = element("typography.panel")
        XCTAssertTrue(panel.waitForExistence(timeout: 10))
        XCTAssertLessThanOrEqual(panel.frame.width, 760)
        XCTAssertLessThanOrEqual(panel.frame.height, 470)
        checkpoint("iPad-阅读设置")
        app.buttons["typography.done"].tap()
        revealReaderControls()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 10))
    }

    func testTopLevelPagesUseShelfHeadersAndEdges() {
        for orientation in [UIDeviceOrientation.portrait, .landscapeLeft] {
            rotate(orientation)
            let direction = orientation == .portrait ? "竖屏" : "横屏"
            selectTab("书库")
            selectLayout("网格视图", identifier: "books.grid")
            let title = app.staticTexts["library.view.title"]
            XCTAssertTrue(title.waitForExistence(timeout: 10))
            let reference = title.frame
            let grid = element("books.grid").frame
            XCTAssertEqual(grid.minX, window.frame.minX + 24, accuracy: 2)
            XCTAssertEqual(grid.maxX, window.frame.maxX - 24, accuracy: 2)
            checkpoint("页边界-\(direction)-书库")

            for (tab, identifier) in [("资料", "page.materials"), ("统计", "page.dashboard"), ("我的", "page.profile")] {
                selectTab(tab)
                let heading = app.staticTexts[identifier]
                XCTAssertTrue(heading.waitForExistence(timeout: 15))
                XCTAssertEqual(heading.frame.minX, reference.minX, accuracy: 1, "\(tab) shares the shelf's leading edge.")
                XCTAssertEqual(heading.frame.minY, reference.minY, accuracy: 1, "\(tab) shares the shelf's top position.")
                XCTAssertEqual(heading.frame.height, reference.height, accuracy: 1, "\(tab) shares the shelf's title row height.")
                XCTAssertEqual(heading.frame.width / CGFloat(heading.label.count), reference.width / 2, accuracy: 1,
                               "Chinese page titles share the same font size, not just a nominal font token.")
                if tab == "资料" {
                    let row = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.book.'")).firstMatch
                    if row.waitForExistence(timeout: 10) { assertShelfEdges(row, reference: grid) }
                } else if tab == "我的" {
                    assertShelfEdges(app.buttons["profile.theme"], reference: grid)
                }
                checkpoint("页边界-\(direction)-\(tab)")
            }
            app.buttons["profile.librarySettings"].tap()
            let setting = app.switches["library.settings.colorful"]
            XCTAssertTrue(setting.waitForExistence(timeout: 10))
            XCTAssertEqual(setting.frame.maxX, grid.maxX, accuracy: 2, "Settings rows also keep the shelf's trailing edge.")
            checkpoint("页边界-\(direction)-设置表单")
            app.navigationBars.buttons.firstMatch.tap()
            XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 10))
        }
    }

    private func assertShelfEdges(_ row: XCUIElement, reference: CGRect) {
        XCTAssertTrue(row.waitForExistence(timeout: 10))
        XCTAssertEqual(row.frame.minX, reference.minX, accuracy: 2)
        XCTAssertEqual(row.frame.maxX, reference.maxX, accuracy: 2)
    }

    func testReadingDataPagesUseShelfBoundaries() {
        rotate(.landscapeLeft)
        selectTab("统计")
        let dashboard = element("dashboard.content")
        XCTAssertTrue(dashboard.waitForExistence(timeout: 20))
        let started = app.staticTexts["已开始"]
        XCTAssertTrue(started.waitForExistence(timeout: 20))
        XCTAssertLessThanOrEqual(dashboard.frame.width, window.frame.width + 1)
        checkpoint("iPad-阅读数据-横屏")
        for title in ["已读完", "书签", "划线与笔记"] {
            // The distribution legend below the fold repeats these titles.
            // Measure the visible statistic, not an offscreen first match.
            let matches = app.staticTexts.matching(NSPredicate(format: "label == %@", title)).allElementsBoundByIndex
            guard let label = matches.first(where: { $0.isHittable }) else {
                return XCTFail("Expected the visible statistic \(title).")
            }
            XCTAssertEqual(label.frame.minY, started.frame.minY, accuracy: 3,
                           "All four statistic cards should share one wide row.")
        }
        selectTab("资料")
        let materials = element("materials.content")
        XCTAssertTrue(materials.waitForExistence(timeout: 20))
        XCTAssertGreaterThanOrEqual(materials.frame.minX, window.frame.minX)
        XCTAssertLessThanOrEqual(materials.frame.maxX, window.frame.maxX)
        checkpoint("iPad-阅读资料-横屏")
    }

    func testPdfRetainsFullLandscapeViewport() {
        rotate(.landscapeLeft)
        selectLayout("网格视图", identifier: "books.grid")
        app.buttons["library.search.toggle"].tap()
        let search = app.textFields["library.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 10))
        search.tap()
        search.typeText("Native UI PDF")
        let pdf = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI PDF'")).firstMatch
        XCTAssertTrue(pdf.waitForExistence(timeout: 15))
        pdf.tap()
        revealReaderControls()
        let page = element("reader.page")
        XCTAssertEqual(page.frame.width, window.frame.width, accuracy: 3,
                       "The EPUB width limit must not constrain PDF page fit or zoom.")
        XCTAssertLessThanOrEqual(element("reader.toolbar").frame.width, 720)
        checkpoint("iPad-PDF-横屏")
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 10))
    }

    func testAssistantSidebarClosesFromHeaderButton() {
        rotate(.landscapeLeft)
        selectLayout("网格视图", identifier: "books.grid")
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        book.tap()
        revealReaderControls()
        let aiButton = app.buttons["reader.AI"]
        XCTAssertTrue(aiButton.waitForExistence(timeout: 10))
        aiButton.tap()
        let closeSidebar = app.buttons["reader.assistant.close"]
        XCTAssertTrue(closeSidebar.waitForExistence(timeout: 10))
        XCTAssertTrue(waitUntil(timeout: 5) { !self.element("reader.toolbar").exists },
                      "Opening AI assistant must collapse the reader toolbar first.")
        XCTAssertTrue(closeSidebar.isHittable)
        closeSidebar.tap()
        XCTAssertTrue(waitUntil(timeout: 10) { !closeSidebar.exists },
                      "Tapping the AI sidebar close button must dismiss the docked iPad assistant.")
    }

    func testReaderSurvivesMultitaskingAppSwitch() {
        selectLayout("网格视图", identifier: "books.grid")
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        book.tap()
        revealReaderControls()
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.35)).tap()
        XCTAssertTrue(waitUntil { !self.app.buttons["reader.close"].isHittable })

        let safari = XCUIApplication(bundleIdentifier: "com.apple.mobilesafari")
        safari.launch()
        RunLoop.current.run(until: Date().addingTimeInterval(0.5))

        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")

        // Activate Libzora back into the reader body, then exercise:
        // 1) SpringBoard bottom-edge left/right multitasking swipes
        // 2) Rapid app activation switches between Safari and Libzora
        // 3) Opening the app switcher and returning to Libzora
        app.activate()
        XCTAssertTrue(element("reader.page").waitForExistence(timeout: 10))

        for round in 0..<3 {
            let sbBottomRight = springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.72, dy: 0.996))
            let sbBottomLeft = springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.28, dy: 0.996))
            sbBottomLeft.press(forDuration: 0.02, thenDragTo: sbBottomRight)
            RunLoop.current.run(until: Date().addingTimeInterval(0.35))
            sbBottomRight.press(forDuration: 0.02, thenDragTo: sbBottomLeft)
            RunLoop.current.run(until: Date().addingTimeInterval(0.35))

            safari.activate()
            RunLoop.current.run(until: Date().addingTimeInterval(0.15))
            app.activate()
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
            XCTAssertTrue(element("reader.page").waitForExistence(timeout: 10),
                          "Reader must remain responsive after multitasking switch round \(round)")
        }

        // Open iPad app switcher via SpringBoard bottom swipe-up and return to Libzora.
        let sbHomeStart = springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.997))
        let sbSwitcherHold = springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.65))
        sbHomeStart.press(forDuration: 0.02, thenDragTo: sbSwitcherHold)
        RunLoop.current.run(until: Date().addingTimeInterval(0.6))
        app.activate()
        XCTAssertTrue(element("reader.page").waitForExistence(timeout: 10))

        // Verify reader body touch/page-turn and toolbar still work smoothly.
        let beforePos = element("reader.position").label
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.82, dy: 0.5))
            .press(forDuration: 0.02, thenDragTo: window.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.5)))
        XCTAssertTrue(waitUntil(timeout: 10) { self.element("reader.position").label != beforePos },
                      "Page turn must still respond after multitasking app switches.")

        revealReaderControls()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 10))
    }

    private var window: XCUIElement { app.windows.firstMatch }

    private func element(_ identifier: String) -> XCUIElement {
        app.descendants(matching: .any).matching(identifier: identifier).firstMatch
    }

    private func selectTab(_ title: String) {
        let tab = app.tabBars.buttons[title].firstMatch
        if tab.exists { tab.tap() } else { app.buttons[title].firstMatch.tap() }
    }

    private func selectLayout(_ title: String, identifier: String) {
        if element(identifier).exists { return }
        app.buttons["library.display"].tap()
        let choice = app.buttons[title]
        XCTAssertTrue(choice.waitForExistence(timeout: 10))
        choice.tap()
        XCTAssertTrue(element(identifier).waitForExistence(timeout: 10))
    }

    private func firstBookRow() -> [XCUIElement] {
        let viewport = window.frame
        let candidates = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'"))
            .allElementsBoundByIndex.prefix(12)
        let books: [(element: XCUIElement, frame: CGRect)] = candidates.compactMap { book in
            guard book.identifier.count == 41 else { return nil }
            let frame = book.frame
            guard frame.width > 0, frame.height > 0, frame.intersects(viewport) else { return nil }
            return (book, frame)
        }
        guard let top = books.map({ $0.frame.minY }).min() else { return [] }
        return books.filter { abs($0.frame.minY - top) < 3 }.sorted { $0.frame.minX < $1.frame.minX }.map(\.element)
    }

    private func assertCentered(_ element: XCUIElement, maximumWidth: CGFloat) {
        XCTAssertTrue(element.waitForExistence(timeout: 10))
        XCTAssertLessThanOrEqual(element.frame.width, maximumWidth + 1)
        XCTAssertEqual(element.frame.midX, window.frame.midX, accuracy: 3)
    }

    private func rotate(_ orientation: UIDeviceOrientation) {
        XCUIDevice.shared.orientation = orientation
        let landscape = orientation == .landscapeLeft || orientation == .landscapeRight
        XCTAssertTrue(waitUntil {
            landscape ? self.window.frame.width > self.window.frame.height : self.window.frame.height > self.window.frame.width
        }, "The app window must actually rotate, not only report a device orientation.")
        RunLoop.current.run(until: Date().addingTimeInterval(1.0))
    }

    private func revealReaderControls() {
        XCTAssertTrue(waitUntil(timeout: 40) {
            self.element("reader.toolbar").exists || self.app.staticTexts["reader.position"].exists
        }, "Wait for an opened page, not the loading screen's close button.")
        let close = app.buttons["reader.close"]
        for _ in 0..<4 {
            if close.exists && close.isHittable { return }
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.35)).tap()
            RunLoop.current.run(until: Date().addingTimeInterval(0.6))
        }
        XCTAssertTrue(close.exists && close.isHittable)
    }

    private func progressValue(_ slider: XCUIElement) -> Double {
        let text = (slider.value as? String ?? "").replacingOccurrences(of: "%", with: "")
            .replacingOccurrences(of: ",", with: ".").trimmingCharacters(in: .whitespacesAndNewlines)
        guard let value = Double(text) else { XCTFail("Expected a numeric reading progress, got \(text)"); return 0 }
        return value
    }

    private func waitUntil(timeout: TimeInterval = 15, _ condition: () -> Bool) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.15))
        } while Date() < deadline
        return condition()
    }

    private func checkpoint(_ name: String) {
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        // The host captures this exact UI through CoreSimulator, which also
        // works on hosts where XCTest attachments render as black frames.
        RunLoop.current.run(until: Date().addingTimeInterval(2.0))
    }
}
