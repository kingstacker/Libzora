import XCTest

/// Profile-only checks use the existing preview library, never submit account
/// or sync actions, and restore the appearance preferences they temporarily use.
final class NativeProfilePageTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.nativepreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["我的"].waitForExistence(timeout: 30))
    }

    func testProfileFlatGroupsAcrossThemes() {
        openProfile()
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        guard let originalTheme = theme.value as? String else {
            XCTFail("The current theme must be known before changing preferences.")
            return
        }
        let originalColorful = colorfulMode()
        defer {
            setColorfulMode(originalColorful)
            selectTheme(originalTheme)
            capture("我的-轻列表-恢复原有外观")
        }

        for (mode, colorful, checkpoint) in [
            ("浅色", false, "我的-轻列表-浅色"),
            ("浅色", true, "我的-轻列表-浅色炫彩"),
            ("深色", true, "我的-轻列表-深色炫彩"),
            ("深色", false, "我的-轻列表-深色")
        ] {
            setColorfulMode(colorful)
            selectTheme(mode)
            assertAlignedRows()
            capture(checkpoint)
        }

        // Navigation still belongs to the whole row, not just its trailing arrow.
        app.buttons["profile.account"].tap()
        XCTAssertTrue(app.navigationBars["用户账号"].waitForExistence(timeout: 10))
        app.navigationBars["用户账号"].buttons.firstMatch.tap()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 10))

        let about = app.buttons["profile.about"]
        for _ in 0..<6 {
            if about.exists && about.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(about.exists && about.isHittable)
        XCTAssertTrue(app.buttons["profile.feedback"].exists)
        capture("我的-轻列表-支持与关于")
        about.tap()
        XCTAssertTrue(app.navigationBars["关于"].waitForExistence(timeout: 10))
        app.navigationBars["关于"].buttons.firstMatch.tap()
        openProfile()
    }

    private func assertAlignedRows() {
        let title = app.staticTexts["page.profile"]
        XCTAssertTrue(title.exists && title.isHittable)
        for identifier in ["profile.account", "profile.sync", "profile.librarySettings",
                           "profile.dictionary", "profile.ai", "profile.kindle"] {
            let row = app.buttons[identifier]
            XCTAssertTrue(row.exists && row.isHittable, "Missing profile destination: \(identifier)")
            XCTAssertEqual(row.frame.minX, title.frame.minX, accuracy: 2,
                           "Rows should align with the page title instead of sitting inside an inset card.")
            XCTAssertGreaterThanOrEqual(row.frame.height, 44)
        }
        XCTAssertTrue(app.buttons["profile.theme"].isHittable)
    }

    private func openProfile() {
        app.tabBars.buttons["我的"].tap()
        let title = app.staticTexts["page.profile"]
        XCTAssertTrue(title.waitForExistence(timeout: 15))
        for _ in 0..<6 {
            if title.isHittable { break }
            app.swipeDown()
        }
        XCTAssertTrue(title.isHittable)
    }

    private func colorfulMode() -> Bool {
        openProfile()
        app.buttons["profile.librarySettings"].tap()
        let setting = app.switches["library.settings.colorful"]
        XCTAssertTrue(setting.waitForExistence(timeout: 10))
        let enabled = setting.value as? String == "1"
        app.navigationBars["书库设置"].buttons.firstMatch.tap()
        XCTAssertTrue(app.buttons["profile.theme"].waitForExistence(timeout: 10))
        return enabled
    }

    private func setColorfulMode(_ enabled: Bool) {
        openProfile()
        app.buttons["profile.librarySettings"].tap()
        let setting = app.switches["library.settings.colorful"]
        XCTAssertTrue(setting.waitForExistence(timeout: 10))
        if (setting.value as? String == "1") != enabled { setting.tap() }
        XCTAssertTrue(waitUntil { (setting.value as? String == "1") == enabled })
        app.navigationBars["书库设置"].buttons.firstMatch.tap()
        XCTAssertTrue(app.buttons["profile.theme"].waitForExistence(timeout: 10))
    }

    private func selectTheme(_ label: String) {
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        theme.tap()
        let option = app.buttons[label]
        XCTAssertTrue(option.waitForExistence(timeout: 5))
        option.tap()
        XCTAssertTrue(waitUntil { theme.value as? String == label })
    }

    private func waitUntil(_ condition: @escaping () -> Bool) -> Bool {
        let expectation = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in condition() }, object: nil)
        return XCTWaiter.wait(for: [expectation], timeout: 10) == .completed
    }

    private func capture(_ name: String) {
        print("LIBZORA_VISUAL_CHECKPOINT: \(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(2))
    }
}
