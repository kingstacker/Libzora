import XCTest

/// Uses prepare-reader-layout-preview.py, never the user's populated library.
final class ReaderLayoutTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.readerlayoutpreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.scrollViews["library.ready"].waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertEqual(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS '排版回归·'")).count, 5)
    }

    override func tearDownWithError() throws { app.terminate() }

    func testFirstBatchLayoutRenders() {
        checkpoint("排版第一批-真实封面书库")
        openBook("列表与文字")
        checkpoint("排版第一批-列表与文字")
    }

    func testTocListDoesNotGainGeneratedNumbers() {
        openBook("真实目录")
        checkpoint("排版第一批-目录不生成编号")
    }

    func testHiddenCssOverridesAndTableRowsRender() {
        openBook("隐藏样式")
        checkpoint("排版修复-隐藏覆盖与可见表格")
    }

    func testMarkerlessItemsKeepNumbering() {
        openBook("列表计数器")
        checkpoint("排版修复-列表计数器与悬挂缩进")
    }

    func testEnglishHyphenationBreaksLongWords() {
        openBook("英文断词")
        checkpoint("排版修复-英文断词")
    }

    func testTypographyPanelAndPairedLatinFont() {
        openBook("英文断词")
        let page = app.otherElements["reader.page"]
        if !app.buttons["reader.排版"].exists {
            page.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        }
        let typoButton = app.buttons["reader.排版"]
        XCTAssertTrue(typoButton.waitForExistence(timeout: 10), app.debugDescription)
        typoButton.tap()

        let latinRow = app.buttons["typography.latinFontFamily"]
        XCTAssertTrue(latinRow.waitForExistence(timeout: 10), app.debugDescription)
        let scroll = app.scrollViews["typography.scroll"]
        if scroll.exists { scroll.swipeUp() }
        XCTAssertTrue(app.segmentedControls["typography.alignment"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.segmentedControls["typography.cjkLatinSpacing"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.segmentedControls["typography.widowsOrphans"].waitForExistence(timeout: 5))
        checkpoint("排版设置面板-新增中英选项")

        if scroll.exists { scroll.swipeDown() }
        XCTAssertTrue(latinRow.waitForExistence(timeout: 5))
        latinRow.tap()
        let georgia = app.buttons["latinFontPicker.option.system:Georgia"]
        XCTAssertTrue(georgia.waitForExistence(timeout: 10), app.debugDescription)
        checkpoint("英文字体选择器")
        georgia.tap()

        let done = app.buttons["typography.done"]
        if done.waitForExistence(timeout: 5) { done.tap() }
        // Tap center to hide chrome for clean body capture.
        if app.buttons["reader.排版"].exists {
            page.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        }
        checkpoint("英文正文-Georgia西文字体")
    }

    private func openBook(_ label: String) {
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@",
                                                    "排版回归·" + label)).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        book.tap()
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertTrue(waitUntil { !self.app.otherElements["reader.loading"].exists }, app.debugDescription)
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 10))
    }

    private func waitUntil(timeout: TimeInterval = 15, _ condition: () -> Bool) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        } while Date() < deadline
        return condition()
    }

    private func checkpoint(_ name: String) {
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(1.2))
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
