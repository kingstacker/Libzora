import XCTest

/// Run only on an isolated preview seeded with fictional AI and dictionary data.
final class NativeReviewRegressionTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.nativepreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
    }

    override func tearDownWithError() throws { app.terminate() }

    func testSavedAiEndpointAndModelSurviveOpeningAndSaving() {
        openAi()
        XCTAssertTrue(waitUntil { self.app.textFields["ai.baseUrl"].isEnabled })
        // The former load-time observation replaced saved values asynchronously.
        RunLoop.current.run(until: Date().addingTimeInterval(1.5))
        XCTAssertEqual(app.textFields["ai.baseUrl"].value as? String, "https://saved-endpoint.invalid/v1")
        XCTAssertEqual(app.textFields["ai.model"].value as? String, "native-regression-model")
        capture("修复验证-AI保留已保存接口和模型")
        app.navigationBars["AI 接口"].buttons["保存"].tap()
        XCTAssertTrue(app.staticTexts["已保存"].waitForExistence(timeout: 10))
        app.terminate()
        app.launch()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
        openAi()
        XCTAssertTrue(waitUntil { self.app.textFields["ai.baseUrl"].isEnabled })
        RunLoop.current.run(until: Date().addingTimeInterval(1.5))
        XCTAssertEqual(app.textFields["ai.baseUrl"].value as? String, "https://saved-endpoint.invalid/v1")
        XCTAssertEqual(app.textFields["ai.model"].value as? String, "native-regression-model")
    }

    func testDictionaryToggleRefreshesTheCurrentDefinition() {
        app.tabBars.buttons["我的"].tap()
        app.buttons["profile.dictionary"].tap()
        let query = app.textFields["reader.dictionary.query"]
        XCTAssertTrue(query.waitForExistence(timeout: 10))
        query.tap()
        query.typeText("sun\n")
        if app.keyboards.firstMatch.exists {
            let done = app.buttons["完成"].firstMatch
            if done.exists && done.isHittable { done.tap() } else { app.swipeDown() }
        }
        let definition = app.staticTexts["Native review dictionary definition."]
        XCTAssertTrue(definition.waitForExistence(timeout: 20))
        let toggle = app.switches["dictionary.enabled.native-review"]
        reveal(toggle)
        XCTAssertEqual(toggle.value as? String, "1")
        // The identifier spans the whole row; the trailing UISwitch owns the tap.
        toggle.coordinate(withNormalizedOffset: CGVector(dx: 0.93, dy: 0.5)).tap()
        XCTAssertTrue(waitUntil { (toggle.value as? String) == "0" }, "The dictionary switch did not turn off.\n\(app.debugDescription)")
        capture("修复验证-关闭词典")
        XCTAssertTrue(waitUntil { !definition.exists },
                      "Disabling a dictionary must invalidate its current definition.\n\(app.debugDescription)")
        XCTAssertTrue(waitUntil { toggle.isEnabled })
        toggle.coordinate(withNormalizedOffset: CGVector(dx: 0.93, dy: 0.5)).tap()
        XCTAssertTrue(waitUntil { (toggle.value as? String) == "1" })
        XCTAssertTrue(definition.waitForExistence(timeout: 20), "Re-enabling must requery without another search tap.\n\(app.debugDescription)")
        capture("修复验证-词典启停后自动刷新")
    }

    func testAdvancedStatusPreviewMatchesAppliedResults() {
        app.segmentedControls.buttons["在读"].firstMatch.tap()
        app.buttons["library.filters.open"].tap()
        let finished = app.buttons["library.filters.option.status.2"]
        XCTAssertTrue(finished.waitForExistence(timeout: 10))
        finished.tap()
        let apply = app.buttons["library.filters.apply"]
        XCTAssertTrue(apply.waitForExistence(timeout: 10))
        XCTAssertNotEqual(apply.label, "查看 0 本书")
        capture("修复验证-筛选预览不再错误显示零本")
        apply.tap()
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        reveal(epub)
        XCTAssertTrue(epub.isHittable)
        capture("修复验证-筛选结果与预览一致")
    }

    func testPdfPinchZoomSurvivesOtherDisplayChanges() {
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI PDF'")).firstMatch
        reveal(book)
        book.tap()
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
        showDisplaySettings()
        let zoom = app.segmentedControls["typography.pdfZoom"]
        reveal(zoom)
        // Seeded per-book zoom (2x) must reach native preferences on open.
        XCTAssertTrue(waitUntil { zoom.buttons["2 倍"].isSelected },
                      "Per-book PDF zoom must be published to the native controls")
        let fit = app.segmentedControls["typography.pdfFitMode"]
        reveal(fit)
        fit.buttons["适宽"].tap()
        reveal(zoom)
        XCTAssertTrue(waitUntil { zoom.buttons["2 倍"].isSelected },
                      "Changing fit mode must not write a stale zoom back")
        capture("修复验证-PDF缩放不被其他设置重置")
    }

    private func openAi() {
        XCTAssertTrue(app.tabBars.buttons["我的"].waitForExistence(timeout: 30))
        app.tabBars.buttons["我的"].tap()
        let ai = app.buttons["profile.ai"]
        reveal(ai)
        ai.tap()
        XCTAssertTrue(app.textFields["ai.baseUrl"].waitForExistence(timeout: 10))
    }

    private func showDisplaySettings() {
        let display = app.buttons["reader.显示"]
        for _ in 0..<4 {
            if display.exists && display.isHittable { break }
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.4)).tap()
            RunLoop.current.run(until: Date().addingTimeInterval(0.4))
        }
        XCTAssertTrue(display.exists && display.isHittable)
        display.tap()
        XCTAssertTrue(app.buttons["typography.done"].waitForExistence(timeout: 10))
    }

    private func reveal(_ element: XCUIElement) {
        for _ in 0..<12 {
            if element.exists && element.isHittable { return }
            app.swipeUp()
        }
        XCTAssertTrue(element.exists && element.isHittable, app.debugDescription)
    }

    private func waitUntil(timeout: TimeInterval = 15, _ condition: () -> Bool) -> Bool {
        let end = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
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
