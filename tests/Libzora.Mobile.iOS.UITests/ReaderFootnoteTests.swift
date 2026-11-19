import XCTest

/// Uses prepare-reader-footnote-preview.py, never the user's populated library.
final class ReaderFootnoteTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.readerfootnotepreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.scrollViews["library.ready"].waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertEqual(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS '脚注回归·'")).count, 4)
    }

    override func tearDownWithError() throws { app.terminate() }

    func testLegacyCssFootnoteOpensWithoutChangingPosition() {
        checkpoint("脚注回归-真实封面书库")
        let original = openBook("旧式锚点")
        showFirstFootnote()
        XCTAssertTrue(noteText("旧式锚点的完整注释。").waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertEqual(position.label, original)
        checkpoint("脚注回归-CSS上标及旧式锚点")
        app.buttons["reader.footnote.close"].tap()
        XCTAssertTrue(waitUntil { !self.app.otherElements["reader.footnote.card"].exists })
        XCTAssertEqual(position.label, original)
    }

    func testCrossFileNoteKeepsAllParagraphsAndReturnsToSource() {
        let original = openBook("跨文件多段")
        showFirstFootnote()
        XCTAssertTrue(noteText("第一段注释内容。").waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(noteText("第二段注释内容。").exists, app.debugDescription)
        XCTAssertTrue(noteText("换行后的内容也应保留。").exists, app.debugDescription)
        XCTAssertFalse(noteText("不属于本条").exists)
        XCTAssertEqual(position.label, original)
        checkpoint("脚注回归-跨文件多段内容")
        app.buttons["reader.footnote.jump"].tap()
        let back = app.buttons["reader.footnote.return"]
        XCTAssertTrue(back.waitForExistence(timeout: 15), app.debugDescription)
        checkpoint("脚注回归-定位原注")
        back.tap()
        XCTAssertTrue(waitUntil { self.position.label == original && !back.exists }, app.debugDescription)
        checkpoint("脚注回归-返回原文保留位置")
    }

    func testImagePayloadUsesDataAttributeWhenAltIsEmpty() {
        let original = openBook("图片数据")
        showFirstFootnote()
        XCTAssertTrue(noteText("图片数据属性中的完整注释。").waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertFalse(noteText("未找到注释内容").exists)
        XCTAssertEqual(position.label, original)
        checkpoint("脚注回归-图片注释数据")
    }

    func testChapterLinkNavigatesWithoutOpeningFootnote() {
        let original = openBook("普通链接")
        app.windows.firstMatch.coordinate(withNormalizedOffset: .zero)
            .withOffset(CGVector(dx: 112, dy: 106)).tap()
        XCTAssertTrue(waitUntil { self.position.label != original }, app.debugDescription)
        XCTAssertFalse(app.otherElements["reader.footnote.card"].exists)
        checkpoint("脚注回归-普通链接不误识别")
    }

    private var position: XCUIElement { app.staticTexts["reader.position"] }

    private func openBook(_ label: String) -> String {
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", "脚注回归·" + label)).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        book.tap()
        XCTAssertTrue(position.waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertTrue(waitUntil { !self.app.otherElements["reader.loading"].exists }, app.debugDescription)
        return position.label
    }

    private func noteText(_ text: String) -> XCUIElement {
        app.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", text)).firstMatch
    }

    private func showFirstFootnote() {
        // On the default iPhone 17 the first "原文[1]。" marker occupies
        // x=67..90, y=94..106 in window points. reader.page's accessibility
        // bounds only describe its native children, not the self-drawn canvas;
        // anchoring a touch to that element can hit the footer instead.
        app.windows.firstMatch.coordinate(withNormalizedOffset: .zero)
            .withOffset(CGVector(dx: 76, dy: 100)).tap()
        let close = app.buttons["reader.footnote.close"]
        if !close.waitForExistence(timeout: 10) {
            checkpoint("脚注回归-识别失败现场")
            XCTFail("The first reference must open an in-page note card.\n" + app.debugDescription)
        }
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
