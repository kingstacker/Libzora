import XCTest

/// Exercises the existing local EPUB and installed fonts only. No seeding,
/// downloads, or account/sync actions; the original font and footer are restored.
final class NativeFontSwitchTests: XCTestCase {
    private var app: XCUIApplication!
    private var originalFontKey: String?
    private var originalProgressIndex: Int?
    private var switchKeys: [String] = []
    private let optionPrefix = "fontPicker.option."

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.nativepreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        try openTextPage()
        try openTypography()
        let progress = app.segmentedControls["typography.progressDisplay"]
        originalProgressIndex = progress.buttons.allElementsBoundByIndex.firstIndex(where: { $0.isSelected })
        try require(originalProgressIndex != nil, "The original footer mode must be readable before changing settings.")
        try openFontPicker()
        var keys = Set<String>()
        let list = app.descendants(matching: .any)["fontPicker.list"].firstMatch
        for _ in 0..<14 {
            let options = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH %@", optionPrefix)).allElementsBoundByIndex
            keys.formUnion(options.map { String($0.identifier.dropFirst(optionPrefix.count)) })
            if let selected = options.first(where: { $0.isSelected }) {
                originalFontKey = String(selected.identifier.dropFirst(optionPrefix.count))
            }
            if originalFontKey != nil && keys.filter({ $0.hasPrefix("system:") }).count >= 3 { break }
            list.swipeUp()
        }
        try require(originalFontKey != nil, "The original installed font must be identified before changing it.")
        let preferred = ["system:PingFang SC", "system:Songti SC", "system:Kaiti SC"]
        let systems = preferred.filter { keys.contains($0) }
            + keys.filter { $0.hasPrefix("system:") && !preferred.contains($0) }.sorted()
        let builtin = keys.filter { !$0.hasPrefix("system:") && !$0.hasPrefix("catalog:") }.sorted().first
        try require(systems.count >= 2 && builtin != nil, "At least two existing system fonts and the built-in font are required.")
        switchKeys = Array(systems.prefix(3)) + [builtin!]
        app.buttons["fontPicker.done"].tap()
    }

    override func tearDownWithError() throws {
        guard let originalFontKey else { return }
        if app.state != .runningForeground {
            app.terminate()
            app.launch()
            try openTextPage()
        }
        if app.buttons["fontPicker.done"].exists { app.buttons["fontPicker.done"].tap() }
        try openTypography()
        if let originalProgressIndex {
            let progress = app.segmentedControls["typography.progressDisplay"]
            let button = progress.buttons.element(boundBy: originalProgressIndex)
            if !button.isSelected { button.tap() }
        }
        try selectFont(originalFontKey)
        try assertSelectedFont(originalFontKey)
        app.buttons["fontPicker.done"].tap()
        try closeTypography()
        let close = app.buttons["reader.close"]
        try require(reveal(close), "The reader must close after restoring the original font.")
        close.tap()
        if app.buttons["book.back"].waitForExistence(timeout: 5) { app.buttons["book.back"].tap() }
        try require(app.tabBars.firstMatch.waitForExistence(timeout: 15), "The existing library must remain available.")
    }

    func testInstalledFontSwitchKeepsReaderAlive() throws {
        let key = switchKeys[0]
        try selectFont(key)
        try assertSelectedFont(key)
        app.buttons["fontPicker.done"].tap()
        try closeTypography()
        try require(app.state == .runningForeground, "The updated app must remain alive after selecting an installed font.")
        capture("font-switch-installed-app-body")
    }

    func testRepeatedInstalledFontSwitchingKeepsReaderAliveAndPersists() throws {
        // Keep pagination active so its background estimate can overlap a reflow.
        app.segmentedControls["typography.progressDisplay"].buttons.element(boundBy: 0).tap()
        try closeTypography()
        capture("font-switch-original-body")
        for round in 0..<4 {
            for key in switchKeys {
                try openTypography()
                try selectFont(key)
                // Reopening waits for the pending preference save/reflow. The
                // selected trait proves the final choice, not just a stale label.
                try assertSelectedFont(key)
                app.buttons["fontPicker.done"].tap()
                try closeTypography()
                try require(app.state == .runningForeground, "The app exited after changing an installed font.")
                try require(waitUntil {
                    self.app.staticTexts["reader.position"].exists || self.app.sliders["reader.progress"].exists
                }, "The live reader footer or visible progress controls must remain available after reflow.")
                if round == 0 { capture("font-switch-first-\(key)-body") }
            }
            capture("font-switch-round-\(round + 1)-body")
        }
        print("LIBZORA_NATIVE_UI_REVIEW_FONT_SWITCH_COUNT: \(switchKeys.count * 4)")

        let persisted = switchKeys[0]
        try openTypography()
        try selectFont(persisted)
        try assertSelectedFont(persisted)
        app.buttons["fontPicker.done"].tap()
        try closeTypography()
        app.terminate()
        app.launch()
        try openTextPage()
        try openTypography()
        try assertSelectedFont(persisted)
        capture("font-switch-after-relaunch")
        app.buttons["fontPicker.done"].tap()
        try closeTypography()
        capture("font-switch-after-relaunch-body")
    }

    private func openTextPage() throws {
        try require(app.tabBars.firstMatch.waitForExistence(timeout: 30), "The populated preview must launch.")
        app.tabBars.buttons.element(boundBy: 0).tap()
        app.buttons["library.search.toggle"].tap()
        let search = app.textFields["library.search"]
        try require(search.waitForExistence(timeout: 10), "The existing library search must be available.")
        search.tap()
        search.typeText("Native UI EPUB\n")
        let book = app.buttons.matching(NSPredicate(format:
            "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        try require(waitUntil { book.exists && book.isHittable }, "The existing local EPUB must be visible without reseeding.")
        book.press(forDuration: 0.8)
        let details = app.buttons["book.action.details"]
        try require(details.waitForExistence(timeout: 10) && details.isHittable, "Book details must be reachable.")
        details.tap()
        try require(app.buttons["book.read"].waitForExistence(timeout: 10), "The existing EPUB must open in the reader.")
        app.buttons["book.read"].tap()
        try require(app.staticTexts["reader.position"].waitForExistence(timeout: 45), "The reader must finish opening.")
        let next = app.buttons.matching(NSPredicate(format: "label IN %@",
            ["Next Chapter or Page", "下一章或下一页"])).firstMatch
        try require(reveal(next), "The next chapter control must be available over the cover.")
        next.tap()
        try require(reveal(app.buttons["reader.排版"]), "The typography controls must be available over body text.")
    }

    private func openTypography() throws {
        if app.buttons["typography.fontFamily"].exists && app.buttons["typography.done"].exists { return }
        let typography = app.buttons["reader.排版"]
        try require(reveal(typography), "The typography control must be reachable.")
        typography.tap()
        try require(app.buttons["typography.fontFamily"].waitForExistence(timeout: 10), "The body font setting must be available.")
    }

    private func closeTypography() throws {
        let done = app.buttons["typography.done"]
        try require(done.waitForExistence(timeout: 10), "The typography panel must finish updating.")
        done.tap()
        try require(waitUntil { !self.app.buttons["typography.done"].exists }, "The typography panel must dismiss.")
        RunLoop.current.run(until: Date().addingTimeInterval(0.4))
    }

    private func openFontPicker() throws {
        if app.buttons["fontPicker.done"].exists { return }
        let row = app.buttons["typography.fontFamily"]
        try require(row.waitForExistence(timeout: 10), "The installed font picker must be reachable.")
        row.tap()
        try require(app.buttons["fontPicker.done"].waitForExistence(timeout: 10), "The font picker must open.")
        try require(waitUntil {
            self.app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH %@", self.optionPrefix)).count > 0
        }, "The installed font list must finish loading after preference writes.")
    }

    private func fontOption(_ key: String) throws -> XCUIElement {
        let option = app.buttons[optionPrefix + key]
        let list = app.descendants(matching: .any)["fontPicker.list"].firstMatch
        for _ in 0..<14 {
            if option.exists && option.isHittable { return option }
            list.swipeUp()
        }
        throw failure("The previously installed font is not reachable: \(key)")
    }

    private func selectFont(_ key: String) throws {
        // Catalog keys are allowed only for restoring the originally selected,
        // already installed font, never for initiating a download.
        try require(!key.hasPrefix("catalog:") || key == originalFontKey, "The stress test must not download a font.")
        try openFontPicker()
        let option = try fontOption(key)
        if option.isSelected {
            app.buttons["fontPicker.done"].tap()
            return
        }
        try require(option.isEnabled, "An installed font must be selectable.")
        option.tap()
        try require(waitUntil { !self.app.buttons["fontPicker.done"].exists }, "Changing an installed font must dismiss the picker.")
    }

    private func assertSelectedFont(_ key: String) throws {
        try openFontPicker()
        let option = try fontOption(key)
        try require(option.isSelected, "The saved body font must match the final selection: \(key)")
    }

    private func reveal(_ element: XCUIElement) -> Bool {
        let window = app.windows.firstMatch
        for y in [0.10, 0.18, 0.45, 0.62, 0.30] {
            if element.exists && element.isHittable { return true }
            if app.buttons["reader.media.close"].exists { app.buttons["reader.media.close"].tap() }
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: y)).tap()
            if element.waitForExistence(timeout: 2) && element.isHittable { return true }
        }
        return element.waitForExistence(timeout: 10) && element.isHittable
    }

    private func waitUntil(_ condition: @escaping () -> Bool) -> Bool {
        let expectation = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in condition() }, object: nil)
        return XCTWaiter.wait(for: [expectation], timeout: 30) == .completed
    }

    private func require(_ condition: Bool, _ message: String) throws {
        if !condition { throw failure(message) }
    }

    private func failure(_ message: String) -> NSError {
        NSError(domain: "NativeFontSwitchTests", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }

    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        print("LIBZORA_VISUAL_CHECKPOINT: \(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(2))
    }
}
