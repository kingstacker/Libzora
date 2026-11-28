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
        XCTAssertEqual(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS '脚注回归·'")).count, 5)
    }

    override func tearDownWithError() throws {
        if (testRun?.failureCount ?? 0) > 0 {
            let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            attachment.name = "阅读器回归失败现场"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        app.terminate()
    }

    func testLegacyCssFootnoteOpensWithoutChangingPosition() {
        checkpoint("脚注回归-真实封面书库")
        let original = openBook("旧式锚点")
        showFirstFootnote(x: 36)
        XCTAssertTrue(noteText("旧式锚点的完整注释。").waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertEqual(position.label, original)
        checkpoint("脚注回归-CSS上标及旧式锚点")
        app.buttons["reader.footnote.close"].tap()
        XCTAssertTrue(waitUntil { !self.app.otherElements["reader.footnote.card"].exists })
        XCTAssertEqual(position.label, original)
    }

    func testEarlyFootnoteSurvivesLateReaderLabelRefresh() {
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-late-footnote-labels"]
        app.launch()
        let book = libraryBook("旧式锚点")
        XCTAssertTrue(book.waitForExistence(timeout: 40))
        book.tap()
        // Tap as soon as the first native page position exists, without the
        // normal extra settling wait before a footnote interaction.
        XCTAssertTrue(position.waitForExistence(timeout: 40))
        let originalPosition = position.label
        showFirstFootnote(x: 36)
        let close = app.buttons["reader.footnote.close"]
        let deadline = Date().addingTimeInterval(3)
        var sawLabelRefresh = false
        repeat {
            sawLabelRefresh = sawLabelRefresh || position.label != originalPosition
            XCTAssertTrue(close.exists, "A late page-count/chapter-label refresh must not dismiss the newly opened note")
            XCTAssertTrue(noteText("旧式锚点的完整注释。").exists)
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        } while Date() < deadline
        XCTAssertTrue(sawLabelRefresh, "The fixture must actually deliver the deferred reader label update")
        checkpoint("脚注回归-刚开书点击注-标签刷新后浮窗仍在")
        close.tap()
        XCTAssertTrue(waitUntil { !self.app.otherElements["reader.footnote.card"].exists })
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

    func testPlainNumberedNoteJumpsToItsDefinition() {
        let original = openBook("纯文本注释")
        showFirstFootnote(x: 76)
        XCTAssertTrue(noteText("纯文本注释内容。").waitForExistence(timeout: 10), app.debugDescription)
        checkpoint("脚注回归-纯文本注释卡片")
        app.buttons["reader.footnote.jump"].tap()
        let back = app.buttons["reader.footnote.return"]
        XCTAssertTrue(back.waitForExistence(timeout: 15), app.debugDescription)
        checkpoint("脚注回归-纯文本注释定位")
        back.tap()
        XCTAssertTrue(waitUntil { self.position.label == original && !back.exists }, app.debugDescription)
        checkpoint("脚注回归-纯文本注释返回")
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

    func testCloseQueuesOneTapUntilSlowTeardownFinishes() {
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-close"]
        app.launchEnvironment["LIBZORA_TEST_SLOW_READER_CLOSE"] = "1"
        app.launch()
        _ = openBook("旧式锚点")
        showReaderChrome()
        app.buttons["reader.close"].tap()
        let next = libraryBook("跨文件多段")
        XCTAssertTrue(waitUntil(timeout: 3) { next.isHittable }, "The shelf must be interactive before slow teardown completes")
        next.tap() // No retry: this intent must survive the still-running close.
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !self.app.descendants(matching: .any)["reader.loading"].exists })
        showReaderChrome()
        XCTAssertTrue(app.otherElements["reader.topToolbar"].staticTexts["脚注回归·跨文件多段"].isHittable, app.debugDescription)
        checkpoint("阅读器竞态-关闭期间单次点击打开新书")
    }

    func testMaterialsExitReleasesTheModalBeforeSlowTeardown() {
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-close"]
        app.launchEnvironment["LIBZORA_TEST_SLOW_READER_CLOSE"] = "1"
        app.launch()
        let materialsTab = app.tabBars.buttons["资料"]
        XCTAssertTrue(materialsTab.waitForExistence(timeout: 15))
        materialsTab.tap()
        let filter = app.segmentedControls["materials.filter"].buttons["书签"]
        XCTAssertTrue(filter.waitForExistence(timeout: 10))
        filter.tap()
        let entry = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'")).firstMatch
        XCTAssertTrue(entry.waitForExistence(timeout: 15), app.debugDescription)
        entry.tap()
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !self.app.descendants(matching: .any)["reader.loading"].exists })
        let original = position.label
        showReaderChrome()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(waitUntil(timeout: 3) { entry.isHittable },
                      "The material list must not remain behind an invisible reader during slow close")
        entry.tap()
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !self.app.descendants(matching: .any)["reader.loading"].exists })
        XCTAssertEqual(position.label, original, "Queued material opens must retain the bookmark location")
        checkpoint("阅读器竞态-资料退出与书签位置重开")
    }

    func testCancelStartupRestoreDoesNotRestoreAgainOnNextLaunch() {
        _ = openBook("旧式锚点")
        checkpoint("阅读器竞态-准备上次阅读恢复")
        app.terminate()
        app.launchArguments = ["--libzora-test-slow-reader-open"]
        app.launchEnvironment["LIBZORA_TEST_SLOW_READER_OPEN"] = "1"
        app.launch()
        let close = app.buttons["reader.close"]
        XCTAssertTrue(waitUntil(timeout: 15) { close.isHittable && !self.position.exists }, app.debugDescription)
        close.tap()
        XCTAssertTrue(waitUntil(timeout: 3) { self.app.scrollViews["library.ready"].isHittable })
        // Relaunch immediately, before the canceled request can finish cleanup.
        app.terminate()
        app.launchArguments = []
        app.launchEnvironment.removeValue(forKey: "LIBZORA_TEST_SLOW_READER_OPEN")
        app.launch()
        XCTAssertTrue(waitUntil(timeout: 40) { self.app.scrollViews["library.ready"].isHittable })
        XCTAssertFalse(app.descendants(matching: .any)["reader.loading"].isHittable)
        XCTAssertFalse(position.isHittable)
        checkpoint("阅读器竞态-取消恢复后再次启动留在书库")
    }

    func testCanceledSameBookOpenAcceptsANewWidgetLink() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-open"]
        app.launchEnvironment["LIBZORA_TEST_SLOW_READER_OPEN"] = "1"
        app.launch()
        let book = libraryBook("旧式锚点")
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        let id = try XCTUnwrap(UUID(uuidString: String(book.identifier.dropFirst("book.".count))))
        book.tap()
        let close = app.buttons["reader.close"]
        XCTAssertTrue(waitUntil(timeout: 15) { close.isHittable && !self.position.exists }, app.debugDescription)
        app.open(try XCTUnwrap(URL(string: "libzora://library")))
        app.open(try XCTUnwrap(URL(string: "libzora://read/\(id.uuidString)")))
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !self.app.descendants(matching: .any)["reader.loading"].exists },
                      "The new same-book widget link must survive cancellation of the earlier open")
        showReaderChrome()
        XCTAssertTrue(app.otherElements["reader.topToolbar"].staticTexts["脚注回归·旧式锚点"].isHittable)
        checkpoint("阅读器竞态-取消后同书小组件链接仍可打开")
    }

    func testMaterialsEntryWaitsForRenderingAndSlidesBackToTheSameBookmark() {
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-open"]
        app.launchEnvironment["LIBZORA_TEST_SLOW_READER_OPEN"] = "1"
        app.launch()
        app.tabBars.buttons["资料"].tap()
        let filter = app.segmentedControls["materials.filter"].buttons["书签"]
        XCTAssertTrue(filter.waitForExistence(timeout: 15))
        filter.tap()
        let entry = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'")).firstMatch
        XCTAssertTrue(entry.waitForExistence(timeout: 15))
        entry.tap()
        let cover = app.descendants(matching: .any)["reader.entry.cover"]
        XCTAssertTrue(cover.waitForExistence(timeout: 5))
        XCTAssertFalse(position.isHittable, "A mounted reader must not expose an unrendered position")
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 5), "Slow loading must remain cancelable")
        checkpoint("首帧交接-资料慢加载稳定遮挡")
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !cover.exists })
        let original = position.label
        checkpoint("首帧交接-资料目标书签正文")
        showReaderChrome()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(waitUntil(timeout: 5) { entry.isHittable })
        entry.tap()
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !cover.exists })
        XCTAssertEqual(position.label, original)
        checkpoint("首帧交接-资料滑入滑出后仍是目标位置")
    }

    func testLibrary3DRepeatedOpenCloseReturnsToTheActualCover() {
        let labels = ["跨文件多段", "普通链接", "跨文件多段"]
        checkpoint("3D收尾-真实多书封面基准")
        for (index, label) in labels.enumerated() {
            _ = openBook(label)
            showReaderChrome()
            checkpoint("3D合书-第\(index + 1)次当前正文与关闭起点")
            app.buttons["reader.close"].tap()
            let book = libraryBook(label)
            XCTAssertTrue(waitUntil(timeout: 5) { book.isHittable && !self.position.isHittable })
            checkpoint("3D收尾-第\(index + 1)次打开关闭后封面无灰块")
        }
    }

    func testMaterialsEntryHonorsTheConfiguredPageAnimation() {
        app.tabBars.buttons["资料"].tap()
        let entry = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'")).firstMatch
        XCTAssertTrue(entry.waitForExistence(timeout: 15))
        entry.tap()
        XCTAssertTrue(waitUntil(timeout: 40) {
            self.position.isHittable && !self.app.descendants(matching: .any)["reader.entry.cover"].exists
        })
        let original = position.label
        checkpoint("资料转场-当前页面动画设置进入正文")
        showReaderChrome()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(waitUntil(timeout: 5) { entry.isHittable })
        entry.tap()
        XCTAssertTrue(waitUntil(timeout: 40) {
            self.position.isHittable && !self.app.descendants(matching: .any)["reader.entry.cover"].exists
        })
        XCTAssertEqual(position.label, original)
        checkpoint("资料转场-当前页面动画设置保留定位")
    }

    func testMaterialsSlowOpenCanBeCanceledBeforeTheBodyIsReady() {
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-open"]
        app.launchEnvironment["LIBZORA_TEST_SLOW_READER_OPEN"] = "1"
        app.launch()
        app.tabBars.buttons["资料"].tap()
        let entry = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'")).firstMatch
        XCTAssertTrue(entry.waitForExistence(timeout: 15))
        entry.tap()
        let close = app.buttons["reader.close"]
        XCTAssertTrue(waitUntil(timeout: 5) { close.isHittable && !self.position.isHittable },
                      "The stable loading cover must keep its own return button interactive")
        close.tap()
        XCTAssertTrue(waitUntil(timeout: 3) { entry.isHittable })
        checkpoint("首帧交接-资料慢加载可立即取消")
    }

    func testWidgetBookSlidesInAndClosesWithoutReturningToACover() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        let book = libraryBook("跨文件多段")
        let id = try XCTUnwrap(UUID(uuidString: String(book.identifier.dropFirst("book.".count))))
        checkpoint("小组件转场-真实多书封面起点")
        app.open(try XCTUnwrap(URL(string: "libzora://read/\(id.uuidString)")))
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !self.app.descendants(matching: .any)["reader.entry.cover"].exists })
        checkpoint("小组件转场-继续阅读直接到正文")
        showReaderChrome()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(waitUntil(timeout: 5) { book.isHittable })
        XCTAssertFalse(position.isHittable)
        checkpoint("小组件转场-普通滑出返回原页面")
    }

    func testWidgetDashboardSlidesFromAReaderAndDoesNotReopenIt() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        _ = openBook("跨文件多段")
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        let dashboard = app.staticTexts["page.dashboard"]
        XCTAssertTrue(waitUntil(timeout: 10) { dashboard.isHittable && !self.position.isHittable })
        checkpoint("小组件转场-阅读之树和阅读数据进入对应页")
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        XCTAssertTrue(dashboard.isHittable, "Same-page widget navigation must not hide or reset the dashboard")
        XCTAssertFalse(position.isHittable)
        checkpoint("小组件转场-同页不重复播放")
    }

    func testWidgetColdLaunchShowsTheRequestedBookRatherThanTheShelf() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        let book = libraryBook("跨文件多段")
        let id = try XCTUnwrap(UUID(uuidString: String(book.identifier.dropFirst("book.".count))))
        app.terminate()
        app.open(try XCTUnwrap(URL(string: "libzora://read/\(id.uuidString)")))
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !self.app.descendants(matching: .any)["reader.entry.cover"].exists })
        XCTAssertFalse(app.scrollViews["library.ready"].isHittable)
        showReaderChrome()
        XCTAssertTrue(app.otherElements["reader.topToolbar"].staticTexts["脚注回归·跨文件多段"].isHittable)
        checkpoint("小组件转场-冷启动直接到目标正文")
    }

    func testWidgetDashboardAppearsBeforeSlowReaderTeardown() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-close"]
        app.launch()
        _ = openBook("跨文件多段")
        checkpoint("小组件快跳-慢关闭前当前正文")
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        XCTAssertTrue(waitUntil(timeout: 3) {
            self.app.staticTexts["page.dashboard"].isHittable && !self.position.exists
        }, "The dashboard must slide in before the ten-second backend close finishes")
        checkpoint("小组件快跳-后台仍关闭时数据页已可用")
    }

    func testWidgetSamePageRequestCancelsAnOlderDifferentTarget() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-widget-navigation"]
        app.launch()
        XCTAssertTrue(app.scrollViews["library.ready"].waitForExistence(timeout: 40))
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        app.open(try XCTUnwrap(URL(string: "libzora://library")))
        RunLoop.current.run(until: Date().addingTimeInterval(2.5))
        XCTAssertTrue(app.scrollViews["library.ready"].isHittable)
        XCTAssertTrue(app.tabBars.buttons["书库"].isSelected)
        XCTAssertFalse(app.staticTexts["page.dashboard"].isHittable)
        checkpoint("小组件最新意图-同页书库请求取消旧数据页请求")
    }

    func testWidgetLastDifferentTabWinsTheNavigationBurst() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-widget-navigation"]
        app.launch()
        XCTAssertTrue(app.scrollViews["library.ready"].waitForExistence(timeout: 40))
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        app.open(try XCTUnwrap(URL(string: "libzora://library")))
        app.open(try XCTUnwrap(URL(string: "libzora://materials")))
        XCTAssertTrue(waitUntil(timeout: 8) { self.app.tabBars.buttons["资料"].isSelected })
        XCTAssertFalse(app.staticTexts["page.dashboard"].isHittable)
        checkpoint("小组件最新意图-最后一个资料页请求胜出")
    }

    func testWidgetTabCanCancelASlowOpeningReaderImmediately() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-open"]
        app.launch()
        let id = try XCTUnwrap(UUID(uuidString: String(libraryBook("跨文件多段").identifier.dropFirst("book.".count))))
        app.open(try XCTUnwrap(URL(string: "libzora://read/\(id.uuidString)")))
        XCTAssertTrue(app.descendants(matching: .any)["reader.entry.cover"].waitForExistence(timeout: 5))
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        XCTAssertTrue(waitUntil(timeout: 3) { self.app.staticTexts["page.dashboard"].isHittable && !self.position.exists })
        RunLoop.current.run(until: Date().addingTimeInterval(10.5))
        XCTAssertTrue(app.staticTexts["page.dashboard"].isHittable)
        XCTAssertFalse(position.exists, "The canceled book must not reappear when its delayed open finishes")
        checkpoint("小组件快跳-取消慢打开后保持数据页")
    }

    func testWidgetColdBookShowsTheIconBeforeItsRenderedPage() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        let id = try XCTUnwrap(UUID(uuidString: String(libraryBook("跨文件多段").identifier.dropFirst("book.".count))))
        checkpoint("小组件冷启动-阅读前的书库基准")
        app.terminate()
        app.launchArguments = ["--libzora-disable-restore", "--libzora-test-slow-reader-open",
                               "--libzora-test-cold-widget-book", id.uuidString]
        app.launch()
        let brand = app.descendants(matching: .any)["launch.brand"]
        XCTAssertTrue(brand.waitForExistence(timeout: 5), "Cold widget entry must begin at the Libzora icon page")
        XCTAssertFalse(position.isHittable)
        XCTAssertFalse(app.scrollViews["library.ready"].isHittable)
        checkpoint("小组件冷启动-目标未就绪时保持图标页")
        XCTAssertTrue(waitUntil(timeout: 40) { self.position.isHittable && !brand.exists })
        checkpoint("小组件冷启动-图标左滑进入目标正文")
    }

    func testWidgetColdDashboardStartsAtTheIconAndSelectsItsTab() throws {
        guard #available(iOS 16.4, *) else { throw XCTSkip("URL launch requires iOS 16.4") }
        checkpoint("小组件冷启动-数据页前的书库基准")
        app.terminate()
        app.open(try XCTUnwrap(URL(string: "libzora://dashboard")))
        XCTAssertTrue(app.staticTexts["page.dashboard"].waitForExistence(timeout: 40))
        XCTAssertTrue(app.tabBars.buttons["统计"].isSelected)
        XCTAssertFalse(position.exists)
        checkpoint("小组件冷启动-图标左滑进入阅读数据")
    }

    private func libraryBook(_ label: String) -> XCUIElement {
        app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", "脚注回归·" + label)).firstMatch
    }

    private func showReaderChrome() {
        let close = app.buttons["reader.close"]
        if !close.isHittable {
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.4)).tap()
        }
        XCTAssertTrue(waitUntil { close.isHittable }, app.debugDescription)
    }

    private var position: XCUIElement { app.staticTexts["reader.position"] }

    private func openBook(_ label: String) -> String {
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", "脚注回归·" + label)).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        book.tap()
        XCTAssertTrue(position.waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertTrue(waitUntil { !self.app.descendants(matching: .any)["reader.loading"].exists }, app.debugDescription)
        return position.label
    }

    private func noteText(_ text: String) -> XCUIElement {
        app.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", text)).firstMatch
    }

    private func showFirstFootnote(x: CGFloat = 76) {
        // On the default iPhone 17, "原文[1]。" occupies x=67..90;
        // the leading-reference fixture starts its marker at x=24 instead.
        // Both occupy y=94..106 in window points. reader.page's accessibility
        // bounds only describe its native children, not the self-drawn canvas;
        // anchoring a touch to that element can hit the footer instead.
        app.windows.firstMatch.coordinate(withNormalizedOffset: .zero)
            .withOffset(CGVector(dx: x, dy: 100)).tap()
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

/// Explicitly selected after preparing a user-provided book in bookopencheck.
/// No source-book title, path or contents are embedded in the test bundle.
final class ActualBookOpenTests: XCTestCase {
    func testProvidedBookRepeated3DOpenHasStableFirstPage() {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.bookopencheck")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        defer { app.terminate() }
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND NOT (label BEGINSWITH '脚注回归·')")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        checkpoint("实书3D-真实多书封面")
        for index in 0..<3 {
            XCTAssertTrue(book.isHittable)
            checkpoint("实书3D-打开前-\(index)")
            book.tap()
            XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 60))
            let deadline = Date().addingTimeInterval(20)
            while app.descendants(matching: .any)["reader.loading"].exists && Date() < deadline {
                RunLoop.current.run(until: Date().addingTimeInterval(0.1))
            }
            XCTAssertFalse(app.descendants(matching: .any)["reader.loading"].exists)
            checkpoint("实书3D-正文就绪-\(index)")
            let close = app.buttons["reader.close"]
            if !close.exists {
                app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.4)).tap()
            }
            XCTAssertTrue(close.waitForExistence(timeout: 10))
            close.tap()
            let end = Date().addingTimeInterval(15)
            while !book.isHittable && Date() < end { RunLoop.current.run(until: Date().addingTimeInterval(0.1)) }
            XCTAssertTrue(book.isHittable)
            checkpoint("实书3D-关闭返回-\(index)")
        }
    }

    private func checkpoint(_ name: String) {
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(1.2))
        let image = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        image.name = name
        image.lifetime = .keepAlways
        add(image)
    }
}
