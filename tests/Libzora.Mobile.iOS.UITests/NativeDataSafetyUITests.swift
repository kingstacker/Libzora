import XCTest

/// Only the synthetic iossafetycheck bundle is touched; never the user's preview library.
final class NativeDataSafetyUITests: XCTestCase {
    private var app: XCUIApplication!
    private let title = "脚注回归·旧式锚点"

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.iossafetycheck")
        relaunch()
    }
    override func tearDownWithError() throws { app.terminate() }

    func testAFirstUseLinkWaitsForPersistedAgreement() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        let id = String(book().identifier.dropFirst("book.".count))
        relaunch(bypass: false, expectLibrary: false)
        XCTAssertTrue(app.buttons["welcome.skip"].waitForExistence(timeout: 20))
        app.open(try XCTUnwrap(URL(string: "libzora://read/\(id)")))
        XCTAssertTrue(app.buttons["welcome.skip"].waitForExistence(timeout: 15))
        XCTAssertFalse(app.descendants(matching: .any)["reader.page"].isHittable)
        capture("首次使用-深链接等待协议同意")
        app.buttons["welcome.skip"].tap()
        let accept = app.buttons["welcome.accept"]
        XCTAssertTrue(accept.waitForExistence(timeout: 10)); XCTAssertFalse(accept.isEnabled)
        app.buttons["welcome.agree.toggle"].tap()
        accept.tap()
        XCTAssertTrue(wait { self.app.descendants(matching: .any)["reader.page"].isHittable })
        closeReader()
    }

    func testRestoreButtonRespondsToOrdinaryTouch() {
        let id = book().identifier
        book().press(forDuration: 0.6)
        app.buttons["book.action.delete"].tap()
        let confirm = app.alerts.buttons["移到回收站"]
        XCTAssertTrue(confirm.waitForExistence(timeout: 10)); confirm.tap()
        XCTAssertTrue(wait { !self.app.buttons[id].exists })
        app.tabBars.buttons["我的"].tap()
        reveal(app.buttons["profile.trash"]); app.buttons["profile.trash"].tap()
        let restore = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'trash.restore.'")).firstMatch
        XCTAssertTrue(restore.waitForExistence(timeout: 15))
        capture("回收站-恢复按钮独立触摸区域")
        restore.tap()
        XCTAssertTrue(app.staticTexts["回收站是空的"].waitForExistence(timeout: 15))
        app.navigationBars.buttons.firstMatch.tap()
        app.tabBars.buttons["书库"].tap()
        XCTAssertTrue(app.buttons[id].waitForExistence(timeout: 15))
    }

    func testFailedReflectionLoadCannotOverwriteAndCanRetry() {
        let text = createReflection()
        relaunch(extra: ["--libzora-test-editor-load-failure"])
        openDetails(); app.buttons["book.reflection.edit"].tap()
        let retry = app.buttons["reflection.retry"]
        XCTAssertTrue(retry.waitForExistence(timeout: 15))
        XCTAssertFalse(app.buttons["reflection.save"].isEnabled)
        capture("读后思考-读取失败禁止覆盖")
        retry.tap()
        let editor = app.textViews["reflection.editor"]
        XCTAssertTrue(editor.waitForExistence(timeout: 15))
        XCTAssertTrue((editor.value as? String ?? "").contains(text))
        capture("读后思考-重试恢复原内容")
        app.buttons["reflection.cancel"].tap()
    }

    func testReflectionDeleteRequiresConfirmationAndCancelKeepsEditor() {
        let text = createReflection()
        openDetails(); app.buttons["book.reflection.edit"].tap()
        XCTAssertTrue(app.textViews["reflection.editor"].waitForExistence(timeout: 15))
        app.buttons["更多操作"].tap(); app.buttons["删除读后思考"].tap()
        let cancel = app.alerts.buttons["取消"]
        XCTAssertTrue(cancel.waitForExistence(timeout: 10))
        capture("读后思考-删除二次确认")
        cancel.tap()
        XCTAssertTrue(wait { self.app.buttons["reflection.save"].isEnabled })
        XCTAssertTrue((app.textViews["reflection.editor"].value as? String ?? "").contains(text))
        app.buttons["reflection.cancel"].tap()
    }

    func testMaterialsDeleteCancellationAndFailureKeepTheRecord() {
        _ = createReflection()
        relaunch()
        app.tabBars.buttons["资料"].tap()
        app.segmentedControls["materials.filter"].buttons["思考"].tap()
        var entry = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'")).firstMatch
        XCTAssertTrue(entry.waitForExistence(timeout: 15))
        let id = entry.identifier
        revealDelete(entry)
        let cancel = app.alerts.buttons["取消"]
        XCTAssertTrue(cancel.waitForExistence(timeout: 10)); cancel.tap()
        XCTAssertTrue(wait { self.app.buttons[id].isEnabled })
        relaunch(extra: ["--libzora-test-material-delete-failure"])
        app.tabBars.buttons["资料"].tap()
        app.segmentedControls["materials.filter"].buttons["思考"].tap()
        entry = app.buttons[id]
        XCTAssertTrue(entry.waitForExistence(timeout: 15))
        revealDelete(entry)
        let acknowledge = app.alerts.buttons["好"]
        XCTAssertTrue(acknowledge.waitForExistence(timeout: 10)); acknowledge.tap()
        XCTAssertTrue(wait { self.app.buttons[id].isEnabled })
        capture("阅读资料-删除失败记录仍可操作")
    }

    func testManualStatusUpdatesBookDetailLabel() {
        openDetails()
        reveal(app.buttons["book.readingStatus"]); app.buttons["book.readingStatus"].tap()
        app.buttons["book.status.2"].tap()
        XCTAssertTrue(wait { self.app.buttons["book.readingStatus"].label == "读完" })
        app.buttons["book.readingStatus"].tap(); app.buttons["book.status.0"].tap()
        XCTAssertTrue(wait { self.app.buttons["book.readingStatus"].label == "待读" })
        app.buttons["书籍选项"].tap()
        XCTAssertFalse(app.buttons["选择阅读格式"].exists)
        app.buttons["导出原书"].tap()
        let files = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.file.'"))
        XCTAssertTrue(files.firstMatch.waitForExistence(timeout: 15))
        XCTAssertGreaterThanOrEqual(files.count, 2)
        capture("书籍详情-手动阅读状态与原书导出列表")
    }

    func testExportBookOpensTheSystemShareSheet() {
        openDetails()
        app.buttons["书籍选项"].tap()
        let exportAction = app.buttons["导出原书"]
        XCTAssertTrue(exportAction.waitForExistence(timeout: 5))
        exportAction.tap()
        let file = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.file.'")).firstMatch
        XCTAssertTrue(file.waitForExistence(timeout: 15)); file.tap()
        let shared = wait(timeout: 15) {
            ["拷贝", "复制", "存储到“文件”", "存储到文件", "Copy", "Save to Files"].contains {
                self.app.descendants(matching: .any)[$0].exists
            }
        }
        capture("原书导出-系统分享面板")
        XCTAssertTrue(shared, app.debugDescription)
    }

    func testTxtPreviewSwitchPersists() {
        app.tabBars.buttons["我的"].tap()
        app.buttons["profile.librarySettings"].tap()
        let toggle = app.switches["library.settings.txtPreview"]
        reveal(toggle)
        if toggle.value as? String != "1" { toggle.tap() }
        XCTAssertTrue(wait { toggle.value as? String == "1" })
        capture("书库设置-TXT规则预览开关")
        relaunch()
        app.tabBars.buttons["我的"].tap(); app.buttons["profile.librarySettings"].tap()
        reveal(toggle)
        XCTAssertEqual(toggle.value as? String, "1")
    }

    func testSavingLocksTheEditorAndKeepsSubmittedText() {
        relaunch(extra: ["--libzora-test-slow-reflection-save"])
        openDetails(); app.buttons["book.reflection.edit"].tap()
        let editor = app.textViews["reflection.editor"]
        XCTAssertTrue(editor.waitForExistence(timeout: 15))
        let text = "保存保护" + String(UUID().uuidString.prefix(6))
        editor.tap(); editor.typeText(text)
        app.buttons["reflection.save"].tap()
        XCTAssertTrue(wait(timeout: 3) {
            self.app.buttons["reflection.save"].exists && !self.app.buttons["reflection.save"].isEnabled
                && !self.app.keyboards.firstMatch.exists
        })
        capture("读后思考-保存期间锁定输入")
        XCTAssertTrue(wait { !editor.exists })
        app.buttons["book.reflection.edit"].tap()
        XCTAssertTrue(editor.waitForExistence(timeout: 15))
        XCTAssertTrue((editor.value as? String ?? "").contains(text))
        app.buttons["reflection.cancel"].tap()
    }

    func testTxtRulesPreviewAndCancellation() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        app.tabBars.buttons["我的"].tap(); app.buttons["profile.librarySettings"].tap()
        let toggle = app.switches["library.settings.txtPreview"]
        reveal(toggle); if toggle.value as? String != "1" { toggle.tap() }
        XCTAssertTrue(wait { toggle.value as? String == "1" })
        relaunch()
        app.open(try XCTUnwrap(URL(string: "libzora://test-txt-import")))
        XCTAssertTrue(app.navigationBars["TXT 导入"].waitForExistence(timeout: 15))
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label CONTAINS '2 个目录项'")).firstMatch.waitForExistence(timeout: 15))
        capture("TXT导入-编码规则和真实目录预览")
        app.navigationBars.buttons["取消"].tap()
        XCTAssertTrue(app.staticTexts["TXT确认样例.txt"].waitForExistence(timeout: 15))
        let confirm = app.navigationBars["TXT 导入"].buttons["导入"]
        XCTAssertTrue(wait { confirm.isHittable && confirm.isEnabled })
        confirm.tap()
        let imported = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'TXT确认样例'")).firstMatch
        XCTAssertTrue(imported.waitForExistence(timeout: 20), app.debugDescription)
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'TXT规则样例'")).firstMatch.exists)
    }

    private func relaunch(extra: [String] = [], bypass: Bool = true, expectLibrary: Bool = true) {
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore"] + (bypass ? ["--libzora-skip-onboarding"] : []) + extra
        app.launch()
        if expectLibrary { XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40)) }
    }
    private func book() -> XCUIElement {
        let result = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", title)).firstMatch
        reveal(result)
        return result
    }
    private func openDetails() {
        if app.buttons["book.read"].exists && app.buttons["book.read"].isHittable { return }
        book().press(forDuration: 0.6)
        let detail = app.buttons["book.action.details"]
        XCTAssertTrue(detail.waitForExistence(timeout: 10)); detail.tap()
        XCTAssertTrue(app.buttons["book.reflection.edit"].waitForExistence(timeout: 15))
    }
    private func createReflection() -> String {
        openDetails(); app.buttons["book.reflection.edit"].tap()
        let editor = app.textViews["reflection.editor"]
        XCTAssertTrue(editor.waitForExistence(timeout: 15))
        let text = "安全回归" + String(UUID().uuidString.prefix(6))
        editor.tap(); editor.typeText(text)
        app.buttons["reflection.save"].tap()
        XCTAssertTrue(wait { !editor.exists })
        return text
    }
    private func revealDelete(_ entry: XCUIElement) {
        entry.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.5))
            .press(forDuration: 0.02, thenDragTo: entry.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)))
        let id = String(entry.identifier.dropFirst("materials.entry.".count))
        let button = app.buttons["materials.delete.\(id)"]
        XCTAssertTrue(button.waitForExistence(timeout: 10)); button.tap()
    }
    private func closeReader() {
        let close = app.buttons["reader.close"]
        for _ in 0..<5 {
            if close.exists && close.isHittable { break }
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
            if close.waitForExistence(timeout: 2) && close.isHittable { break }
        }
        XCTAssertTrue(close.isHittable, app.debugDescription)
        close.tap()
    }
    private func reveal(_ element: XCUIElement) {
        for _ in 0..<10 {
            if element.exists && element.isHittable { return }
            app.swipeUp()
        }
        XCTAssertTrue(element.exists && element.isHittable, app.debugDescription)
    }
    private func wait(timeout: TimeInterval = 25, _ condition: () -> Bool) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        } while Date() < deadline
        return condition()
    }
    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(1))
    }
}
