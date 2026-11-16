import XCTest

/// Uses the populated preview without seeding, deleting books, or submitting
/// account/sync actions. Language and launch overrides are restored afterward.
final class NativeLocalizationTests: XCTestCase {
    private var app: XCUIApplication!
    private var originalChoice: String?
    private let baseArguments = ["--libzora-disable-restore"]

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.nativepreview")
        app.launchArguments = baseArguments
        app.launch()
        openProfile()
        originalChoice = try currentChoice()
    }

    override func tearDownWithError() throws {
        guard let originalChoice else { app?.terminate(); return }
        if app.buttons["reader.close"].exists { app.buttons["reader.close"].tap() }
        app.terminate()
        app.launchArguments = baseArguments
        app.launch()
        openProfile()
        select(originalChoice)
        settle()
    }

    func testManualLanguageRefreshesNativePagesAndPersists() {
        select("en-US")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "Profile")
        XCTAssertTrue(app.staticTexts["Reading & Appearance"].exists)
        XCTAssertTrue(app.segmentedControls["profile.theme"].buttons["System"].exists)
        XCTAssertTrue(waitUntil { self.app.staticTexts["Off"].exists
            && self.app.staticTexts.matching(NSPredicate(format: "label == 'Not Set'")).count == 2 })
        capture("language-english-profile")

        app.buttons["profile.librarySettings"].tap()
        XCTAssertTrue(app.navigationBars["Library Settings"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.switches["library.settings.gallery"].exists)
        XCTAssertEqual(app.switches["library.settings.gallery"].label, "Cover Gallery")
        capture("language-english-library-settings")
        app.navigationBars.buttons.firstMatch.tap()
        openProfile()

        app.buttons["profile.dictionary"].tap()
        XCTAssertTrue(app.textFields["reader.dictionary.query"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["Manage Dictionaries"].exists)
        capture("language-english-dictionary")
        app.navigationBars.buttons.firstMatch.tap()

        tab(1)
        XCTAssertTrue(app.staticTexts["page.materials"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["page.materials"].label, "Reading Notes")
        capture("language-english-reading-notes")
        tab(2)
        XCTAssertTrue(app.staticTexts["page.dashboard"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["page.dashboard"].label, "Reading Stats")
        capture("language-english-reading-stats")
        tab(0)
        XCTAssertTrue(app.staticTexts["library.view.title"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["library.view.title"].label, "Library")
        capture("language-english-library")

        settle()
        app.terminate()
        app.launch()
        openProfile()
        XCTAssertEqual(app.staticTexts["page.profile"].label, "Profile")
        XCTAssertEqual(try? currentChoice(), "en-US")
        capture("language-english-after-relaunch")

        select("zh-CN")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "我的")
        XCTAssertTrue(waitUntil { self.app.staticTexts["未开启"].exists
            && self.app.staticTexts.matching(NSPredicate(format: "label == '未设置'")).count == 2 })
        XCUIDevice.shared.press(.home)
        app.activate()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["page.profile"].label, "我的")
        capture("language-chinese-after-resume")
    }

    func testAutomaticLanguageUsesSystemPreferencesAndManualChoiceWins() {
        select("auto")
        launch(system: "en", locale: "en_US")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "Profile")
        XCTAssertEqual(try? currentChoice(), "auto")
        capture("language-system-english")

        select("zh-CN")
        launch(system: "en", locale: "en_US")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "我的")
        XCTAssertEqual(try? currentChoice(), "zh-CN")
        select("auto")
        XCTAssertTrue(waitUntil { self.app.staticTexts["page.profile"].label == "Profile" })

        launch(system: "zh-Hans", locale: "zh_CN")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "我的")
        XCTAssertEqual(try? currentChoice(), "auto")
        capture("language-system-chinese")

        select("en-US")
        launch(system: "zh-Hans", locale: "zh_CN")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "Profile")
        XCTAssertEqual(try? currentChoice(), "en-US")
        XCUIDevice.shared.press(.home)
        app.activate()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["page.profile"].label, "Profile")
        select("auto")
        XCTAssertTrue(waitUntil { self.app.staticTexts["page.profile"].label == "我的" })

        launch(system: "fr", locale: "fr_FR")
        XCTAssertEqual(app.staticTexts["page.profile"].label, "Profile")
        XCTAssertEqual(try? currentChoice(), "auto")
        capture("language-system-fallback-english")
    }

    func testEnglishReaderChromeSettingsAndContents() throws {
        select("en-US")
        tab(0)
        app.buttons["library.search.toggle"].tap()
        let search = app.textFields["library.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 10))
        search.tap()
        search.typeText("Native UI EPUB\n")
        let book = app.buttons.matching(NSPredicate(format:
            "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(waitUntil { book.exists && book.isHittable },
                      "The existing EPUB fixture must be visible without resetting the preview.")
        book.press(forDuration: 0.8)
        let details = app.buttons["book.action.details"]
        XCTAssertTrue(details.waitForExistence(timeout: 5))
        capture("language-english-book-actions")
        XCTAssertTrue(details.isHittable)
        details.tap()
        capture("language-english-details-navigation")
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        capture("language-english-book-details")
        app.buttons["book.read"].tap()
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 45))
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(reveal(typography))
        XCTAssertTrue(typography.label.contains("Typography"))
        capture("language-english-reader")
        typography.tap()
        XCTAssertTrue(app.otherElements["typography.panel"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["Paper"].exists)
        XCTAssertTrue(app.staticTexts["Footer Progress"].exists)
        capture("language-english-reader-settings")
        app.buttons["typography.done"].tap()

        let toc = app.buttons["reader.目录"]
        XCTAssertTrue(reveal(toc))
        toc.tap()
        XCTAssertTrue(app.buttons["Done"].waitForExistence(timeout: 10))
        capture("language-english-contents")
        app.buttons["Done"].tap()
        let close = app.buttons["reader.close"]
        XCTAssertTrue(reveal(close))
        close.tap()
        XCTAssertTrue(app.buttons["book.back"].waitForExistence(timeout: 15))
        app.buttons["book.back"].tap()
        XCTAssertTrue(app.tabBars.firstMatch.waitForExistence(timeout: 10))
    }

    private func launch(system: String, locale: String) {
        settle()
        app.terminate()
        // Argument-domain overrides affect this launch, not device settings.
        app.launchArguments = baseArguments + ["-AppleLanguages", "(\(system))", "-AppleLocale", locale]
        app.launch()
        openProfile()
    }

    private func tab(_ index: Int) {
        XCTAssertTrue(app.tabBars.firstMatch.waitForExistence(timeout: 30))
        let button = app.tabBars.buttons.element(boundBy: index)
        XCTAssertTrue(button.exists)
        button.tap()
    }

    private func openProfile() {
        tab(3)
        let title = app.staticTexts["page.profile"]
        XCTAssertTrue(title.waitForExistence(timeout: 15))
        for _ in 0..<8 {
            if app.buttons["profile.language"].isHittable { break }
            app.swipeDown()
        }
        XCTAssertTrue(app.buttons["profile.language"].isHittable)
    }

    private func currentChoice() throws -> String {
        let picker = app.buttons["profile.language"]
        let value = picker.value as? String ?? picker.label
        if value == "English" { return "en-US" }
        if value == "简体中文" { return "zh-CN" }
        if ["Follow System", "跟随系统"].contains(value) { return "auto" }
        throw NSError(domain: "NativeLocalizationTests", code: 1,
                      userInfo: [NSLocalizedDescriptionKey: "Unknown language choice: \(value)"])
    }

    private func select(_ choice: String) {
        let picker = app.buttons["profile.language"]
        XCTAssertTrue(picker.waitForExistence(timeout: 10))
        if (try? currentChoice()) == choice { return }
        picker.tap()
        let labels = choice == "en-US" ? ["English"] : choice == "zh-CN" ? ["简体中文"] : ["Follow System", "跟随系统"]
        let option = app.buttons.matching(NSPredicate(format: "label IN %@", labels)).firstMatch
        XCTAssertTrue(option.waitForExistence(timeout: 5))
        option.tap()
        XCTAssertTrue(waitUntil { (try? self.currentChoice()) == choice })
        settle()
    }

    private func reveal(_ element: XCUIElement) -> Bool {
        let window = app.windows.firstMatch
        for y in [0.18, 0.45, 0.62, 0.30] {
            if element.exists && element.isHittable { return true }
            if app.buttons["reader.media.close"].exists { app.buttons["reader.media.close"].tap() }
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: y)).tap()
            if element.waitForExistence(timeout: 2) && element.isHittable { return true }
        }
        return element.waitForExistence(timeout: 10)
    }

    private func waitUntil(_ condition: @escaping () -> Bool) -> Bool {
        let expectation = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in condition() }, object: nil)
        return XCTWaiter.wait(for: [expectation], timeout: 10) == .completed
    }

    private func settle() { RunLoop.current.run(until: Date().addingTimeInterval(1)) }

    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        print("LIBZORA_VISUAL_CHECKPOINT: \(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(2))
    }
}
