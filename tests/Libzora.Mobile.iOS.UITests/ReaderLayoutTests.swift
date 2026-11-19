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
        XCTAssertEqual(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS '排版回归·'")).count, 2)
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
