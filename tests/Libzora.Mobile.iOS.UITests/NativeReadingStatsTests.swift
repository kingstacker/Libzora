import XCTest

/// Only the independent, covered-book fixture from prepare-reading-stats-preview.py.
final class NativeReadingStatsTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.readingstatscheck")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
    }

    override func tearDownWithError() throws { app.terminate() }

    func testReadingListeningBreakdownInLightAndDark() {
        selectTheme("浅色")
        openBreakdown()
        capture("阅读统计-暖灰听书-浅色")
        app.tabBars.buttons["我的"].tap()
        selectTheme("深色")
        openBreakdown()
        capture("阅读统计-暖灰听书-深色")
        // Leave this dedicated demonstration app on its light statistics screen.
        app.tabBars.buttons["我的"].tap()
        selectTheme("浅色")
        openBreakdown()
    }

    func testStatusBarHidesWhileReaderIsStillLoading() {
        app.terminate()
        app.launchArguments.append("--libzora-test-slow-reader-open")
        app.launch()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        capture("打开阅读-点击前书库状态栏")
        book.tap()
        // On iOS 26 the status bar is not exposed in the app's accessibility
        // tree. The paired CoreSimulator captures verify the actual pixels.
        capture("打开阅读-加载期间状态栏已收起")
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
    }

    private func openBreakdown() {
        app.tabBars.buttons["统计"].tap()
        let heading = app.staticTexts["读得最久"]
        XCTAssertTrue(heading.waitForExistence(timeout: 20))
        let row = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'dashboard.mostRead.'")).firstMatch
        for _ in 0..<8 {
            if row.exists && row.isHittable { break }
            if row.exists && row.frame.maxY < app.windows.firstMatch.frame.minY + 100 { app.swipeDown() }
            else { app.swipeUp() }
        }
        XCTAssertTrue(row.exists && row.isHittable, app.debugDescription)
        // Place the longest-read list high enough to show several covered books.
        let window = app.windows.firstMatch
        let delta = max(-260, min(260, window.frame.minY + 125 - heading.frame.midY))
        if abs(delta) > 24 {
            let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5))
            start.press(forDuration: 0.05, thenDragTo: start.withOffset(CGVector(dx: 0, dy: delta)),
                        withVelocity: .slow, thenHoldForDuration: 0.2)
        }
        XCTAssertTrue(row.label.contains("听书"), "Mixed rows describe listening in their accessible value")
    }

    private func selectTheme(_ label: String) {
        app.tabBars.buttons["我的"].tap()
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 15))
        if theme.value as? String == label { return }
        theme.tap()
        let choice = app.buttons[label].firstMatch
        XCTAssertTrue(choice.waitForExistence(timeout: 5))
        choice.tap()
        XCTAssertTrue(waitUntil { (theme.exists && theme.value as? String == label) || (choice.exists && choice.isSelected) })
        if !theme.exists { app.buttons["chevron.left"].tap() }
        XCTAssertTrue(waitUntil { theme.exists && theme.value as? String == label })
    }

    private func waitUntil(timeout: TimeInterval = 15, _ condition: () -> Bool) -> Bool {
        let end = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        } while Date() < end
        return condition()
    }

    private func capture(_ name: String) {
        let image = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        image.name = name
        image.lifetime = .keepAlways
        add(image)
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(1))
    }
}
