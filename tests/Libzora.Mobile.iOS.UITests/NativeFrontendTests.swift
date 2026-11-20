import XCTest

/// Runs against an installed, isolated copy of the real .NET + SwiftUI app.
/// The copy has books/reader data but no credentials or sync configuration.
final class NativeFrontendTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        print("LIBZORA_NATIVE_UI_REVIEW_20261107_V2")
        let bundle = name.contains("testLegacyUpgrade") ? "com.kingstacker.libzora.nativeupgradepreview" : "com.kingstacker.libzora.nativepreview"
        app = XCUIApplication(bundleIdentifier: bundle)
        // Tests start on the shelf rather than reopening the last reading
        // session left by an earlier run.
        app.launchArguments = ["--libzora-disable-restore", "--libzora-enable-registration"]
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 30))
    }

    override func tearDownWithError() throws {
        if let app {
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testReaderBackButtonAcceptsTransparentPadding() {
        // Use an existing local book; this regression needs no reseeded library
        // and does not change appearance or library preferences.
        let books = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'"))
        for _ in 0..<8 {
            if books.allElementsBoundByIndex.contains(where: { $0.isHittable }) { break }
            app.swipeUp()
        }
        guard let book = books.allElementsBoundByIndex.first(where: { $0.isHittable }) else {
            XCTFail("Expected a visible local book in the preserved library.")
            return
        }
        book.tap()
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
        let search = app.buttons["reader.search"]
        XCTAssertTrue(toggleReaderChrome(until: search))
        XCTAssertTrue(waitUntil { search.isHittable })
        search.tap()
        let query = app.textFields["reader.search.query"]
        XCTAssertTrue(query.waitForExistence(timeout: 10))
        let back = app.buttons["reader.close"]
        XCTAssertTrue(waitUntil { back.isHittable })
        XCTAssertGreaterThanOrEqual(back.frame.width, 44)
        XCTAssertGreaterThanOrEqual(back.frame.height, 44)
        attachScreenshot("返回按钮-搜索状态透明留白")
        // At x=15% the tap lands well outside the centered chevron glyph.
        back.coordinate(withNormalizedOffset: CGVector(dx: 0.15, dy: 0.5)).tap()
        XCTAssertTrue(waitUntil { !query.exists && !self.app.keyboards.firstMatch.exists })
        XCTAssertTrue(waitUntil { self.app.buttons["reader.search"].isHittable && back.isHittable },
                      "The first back tap must close search and keep the reader toolbar visible.")
        // The underlying library remains in the accessibility tree while the
        // reader cover is up. Hittability, not existence, proves which is shown.
        XCTAssertFalse(app.buttons["library.display"].isHittable)
        // Also cover the empty area below the glyph in ordinary reading.
        back.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.88)).tap()
        XCTAssertTrue(waitUntil { self.app.buttons["library.display"].isHittable && !back.isHittable })
        attachScreenshot("返回按钮-底部留白返回书库")
    }

    func testBookTapReadsDirectlyAndLongPressOpensDetails() {
        showLibrary()
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        for _ in 0..<8 {
            if book.exists && book.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(book.exists && book.isHittable)
        book.tap()
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40),
                      "One tap must enter reading without visiting book details.")
        XCTAssertFalse(app.buttons["book.read"].exists)
        let close = app.buttons["reader.close"]
        for _ in 0..<3 {
            if close.exists && close.isHittable { break }
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
            if close.waitForExistence(timeout: 2) && close.isHittable { break }
        }
        XCTAssertTrue(close.exists && close.isHittable)
        app.buttons["reader.close"].tap()
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        openBookDetails(book)
        attachScreenshot("书籍-长按查看详情")
        app.buttons["book.back"].tap()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 10))
    }

    /// Assert the displayed preview, not just the source's lift animation.
    /// A menu with a nil preview provider used to pass a screenshot-only test.
    func testLongPressLiftsBookCoverAboveMenuAndTabBar() {
        showLibrary()
        selectLibraryLayout("网格视图")
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch
        for _ in 0..<8 {
            if book.exists && book.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(book.exists && book.isHittable, app.debugDescription)
        let identifier = book.identifier
        let sourceFrame = book.frame
        book.press(forDuration: 0.9)
        assertEnlargedBookPreview(identifier: identifier, sourceFrame: sourceFrame,
                                  capture: "书籍-菜单下弹贴近书卡", menuAbove: false)
        XCTAssertFalse(app.staticTexts["book.preview.title"].label.isEmpty)
        app.buttons["书籍详情"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
    }

    func testLongPressNearTabBarKeepsEntireBookPreviewInFront() {
        showLibrary()
        selectLibraryLayout("网格视图")
        let books = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'"))
        let tabBar = app.tabBars.firstMatch
        var candidate: XCUIElement?
        for _ in 0..<8 {
            candidate = books.allElementsBoundByIndex.filter { book in
                book.isHittable && book.frame.width < self.app.frame.width * 0.5
                    && book.frame.minY > self.app.frame.height * 0.5
                    && book.frame.minY < tabBar.frame.minY - 60
            }.max { $0.frame.minY < $1.frame.minY }
            if candidate != nil { break }
            app.swipeUp()
        }
        guard let book = candidate else {
            XCTFail("Expected a visible book near the tab bar.\n\(app.debugDescription)")
            return
        }
        let identifier = book.identifier
        let sourceFrame = book.frame
        print("BOOK_PREVIEW_SOURCE_NEAR_TAB: \(book.frame); tab=\(tabBar.frame)")
        for attempt in 1...2 {
            // Press the visible cover even when its caption extends under the bar.
            book.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.25)).press(forDuration: 0.9)
            assertEnlargedBookPreview(identifier: identifier, sourceFrame: sourceFrame,
                                      capture: "书籍-靠近底栏整体放大-\(attempt)",
                                      assertCentered: attempt == 1, menuAbove: true)
            let myTab = app.tabBars.buttons["我的"]
            XCTAssertFalse(myTab.isHittable, "The tab bar must be behind the preview overlay.")
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.98, dy: 0.1)).tap()
            XCTAssertTrue(waitUntil { !self.app.buttons["书籍详情"].exists && book.isHittable },
                          "Dismissal must restore the source card and allow another long press.")
            XCTAssertTrue(app.tabBars.buttons["书库"].isSelected,
                          "Dismissing the preview must leave the library tab selected.")
            // Let the dismissal animation finish before pressing again.
            RunLoop.current.run(until: Date().addingTimeInterval(0.8))
        }
    }

    func testListRowTitleLongPressEnlargesCoverAndCaptions() {
        showLibrary()
        selectLibraryLayout("列表视图")
        defer { selectLibraryLayout("网格视图") }
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        for _ in 0..<12 {
            if book.exists && book.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(book.exists && book.isHittable, app.debugDescription)
        XCTAssertGreaterThan(book.frame.width, app.frame.width * 0.7)
        let identifier = book.identifier
        let sourceFrame = book.frame
        // The title/author area, outside the small cover, must open the menu too.
        book.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.5)).press(forDuration: 0.9)
        assertEnlargedBookPreview(identifier: identifier, sourceFrame: sourceFrame,
                                  capture: "书籍-列表长按文字整体放大",
                                  assertCentered: true, sourceIsColumn: false)
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.98, dy: 0.1)).tap()
        XCTAssertTrue(waitUntil { !self.app.buttons["书籍详情"].exists && book.isHittable })
    }

    func testSpeechControlsAutoDockAndRemainUsable() throws {
        try openBook(containing: "银河系漫游指南")
        let dock = app.buttons["reader.speech.dock"]
        let pause = app.buttons["reader.speech.pause"]
        let stop = app.buttons["reader.speech.stop"]
        app.buttons["reader.听书"].tap()
        XCTAssertTrue(pause.waitForExistence(timeout: 15))
        attachScreenshot("朗读-展开")
        XCTAssertTrue(dock.waitForExistence(timeout: 8), "Speech progress must not keep the controls expanded.")
        XCTAssertFalse(pause.exists)
        XCTAssertGreaterThanOrEqual(dock.frame.width, 44)
        XCTAssertLessThanOrEqual(dock.frame.width, 48)
        XCTAssertEqual(dock.frame.maxX, app.windows.firstMatch.frame.maxX, accuracy: 1)
        attachScreenshot("朗读-自动收边")

        dock.tap()
        XCTAssertTrue(pause.waitForExistence(timeout: 2))
        pause.tap()
        XCTAssertTrue(waitUntil { pause.label == "继续朗读" })
        XCTAssertTrue(dock.waitForExistence(timeout: 8), "Pause must restart the docking delay.")
        XCTAssertEqual(dock.value as? String, "已暂停")
        dock.tap()
        XCTAssertTrue(pause.waitForExistence(timeout: 2))
        app.buttons["reader.speech.settings"].tap()
        let settings = app.navigationBars["朗读设置"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5))
        RunLoop.current.run(until: Date().addingTimeInterval(5))
        settings.buttons["完成"].tap()
        XCTAssertTrue(pause.waitForExistence(timeout: 2), "Controls must stay expanded while settings are open.")
        pause.tap()
        XCTAssertTrue(waitUntil { pause.label == "暂停朗读" })
        stop.tap()
        XCTAssertTrue(waitUntil { !pause.exists && !dock.exists })

        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        let listen = app.buttons["reader.听书"]
        XCTAssertTrue(listen.waitForExistence(timeout: 5))
        listen.tap()
        XCTAssertTrue(pause.waitForExistence(timeout: 15), "A new playback session must start expanded.")
        XCTAssertTrue(dock.waitForExistence(timeout: 8))
        dock.tap()
        XCTAssertTrue(stop.waitForExistence(timeout: 2))
        stop.tap()
        XCTAssertTrue(waitUntil { !pause.exists && !dock.exists })
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 5))
        app.buttons["reader.close"].tap()
    }

    func testNativeTabsAndNavigation() {
        for (name, identifier) in [("阅读资料", "page.materials"), ("阅读数据", "page.dashboard"), ("我的", "page.profile")] {
            app.tabBars.buttons[name].tap()
            XCTAssertTrue(app.staticTexts[identifier].waitForExistence(timeout: 15))
            attachScreenshot(name)
        }
        app.tabBars.buttons["书库"].tap()
        showLibrary()
        XCTAssertTrue(app.staticTexts["书库"].exists)
        attachScreenshot("书库")
        app.buttons["library.search.toggle"].tap()
        XCTAssertTrue(app.textFields["library.search"].waitForExistence(timeout: 5))
        app.buttons["library.search.toggle"].tap()
        XCTAssertFalse(app.textFields["library.search"].exists)
    }

    /// The custom back button must pop on long press too and keep edge-swipe back.
    func testAccountBackButtonLongPressAndSwipeBack() {
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        if colorful.value as? String != "1" { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        app.navigationBars.buttons.element(boundBy: 0).tap()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 15))
        tapAccountElement(app.buttons["profile.account"])
        let back = app.navigationBars["用户账号"].buttons.firstMatch
        XCTAssertTrue(back.waitForExistence(timeout: 15))
        attachScreenshot("账号-长按前")
        back.press(forDuration: 8.0)
        attachScreenshot("账号-返回长按")
        print("NAV_BUTTONS: \(app.navigationBars["用户账号"].buttons.allElementsBoundByIndex.map(\.label))")
        print("VISIBLE_BUTTONS: \(app.buttons.allElementsBoundByIndex.map(\.label).prefix(30))")
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 5))
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(app.navigationBars["用户账号"].waitForExistence(timeout: 10))
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.01, dy: 0.5))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5))
        start.press(forDuration: 0.05, thenDragTo: end)
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 5), "Swipe from the leading edge should still pop the page.")
        attachScreenshot("账号-滑动返回")
        // Later tests assume colorful mode starts off.
        openLibrarySettings()
        let setting = app.switches["library.settings.colorful"]
        XCTAssertTrue(setting.waitForExistence(timeout: 10))
        if setting.value as? String == "1" { setting.tap() }
        XCTAssertTrue(waitUntil { setting.value as? String == "0" })
    }

    /// Registration reveals its email and confirmation fields without leaving
    /// the page or hiding the login fields when switching back.
    func testAccountLoginAndRegistrationFieldsSwitch() {
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 15))
        tapAccountElement(app.buttons["profile.account"])
        if app.staticTexts["account.username"].exists {
            reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
        }
        let mode = app.segmentedControls["account.mode"]
        XCTAssertTrue(mode.waitForExistence(timeout: 15))
        XCTAssertTrue(app.textFields["account.usernameField"].exists)
        XCTAssertTrue(app.secureTextFields["account.password"].exists)
        XCTAssertFalse(app.textFields["account.emailField"].exists)
        XCTAssertFalse(app.secureTextFields["account.confirmation"].exists)
        XCTAssertFalse(app.textFields["account.server"].exists)
        attachScreenshot("账号-登录信息")
        tapAccountElement(mode.buttons["注册"])
        XCTAssertTrue(app.textFields["account.emailField"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.secureTextFields["account.confirmation"].exists)
        attachScreenshot("账号-注册信息")
        tapAccountElement(mode.buttons["登录"])
        XCTAssertTrue(waitUntil {
            !self.app.textFields["account.emailField"].exists && !self.app.secureTextFields["account.confirmation"].exists
        })
    }

    func testAboutShowsFeedbackGuideWithoutSignInOrUploadControls() {
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 15))
        let version = app.staticTexts["版本"]
        let feedback = app.buttons["profile.feedback"]
        reveal(feedback)
        XCTAssertTrue(version.exists)
        XCTAssertTrue(feedback.exists && feedback.isHittable)
        feedback.tap()
        XCTAssertTrue(app.navigationBars["问题反馈"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.descendants(matching: .any).matching(identifier: "feedback.issues").firstMatch.exists)
        XCTAssertTrue(app.descendants(matching: .any).matching(identifier: "feedback.qqGroup").firstMatch.exists)
        XCTAssertFalse(app.staticTexts["feedback.serviceBoundary"].exists)
        XCTAssertFalse(app.staticTexts["feedback.loginRequired"].exists)
        XCTAssertFalse(app.buttons["feedback.openAccount"].exists)
        XCTAssertFalse(app.buttons["feedback.submit"].exists)
        XCTAssertFalse(app.buttons["feedback.addScreenshot"].exists)
        XCTAssertFalse(app.switches["feedback.attachLogs"].exists)
        XCTAssertFalse(app.textViews["feedback.content"].exists)
        // Do not open or post to the external channel during this local UI test.
        attachScreenshot("我的-问题反馈说明")
    }

    /// System-hosted menus, segmented controls and tabs keep their native
    /// containers while their titles use the app's custom font.
    func testCustomFontOnSegmentedControlsAndTabs() {
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(app.buttons["profile.theme"].waitForExistence(timeout: 15))
        attachScreenshot("字体-外观菜单与底部标签")
        app.tabBars.buttons["书库"].tap()
        XCTAssertTrue(app.segmentedControls["library.filter"].waitForExistence(timeout: 15))
        attachScreenshot("字体-书库筛选分段")
    }

    func testLibraryDisplayMenuChangesLayoutAndSort() {
        showLibrary()
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        let pdf = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI PDF'")).firstMatch
        XCTAssertTrue(epub.waitForExistence(timeout: 15))
        XCTAssertTrue(pdf.waitForExistence(timeout: 15))
        let display = app.buttons["library.display"]
        XCTAssertTrue(display.exists)
        XCTAssertTrue(app.buttons["library.filters.open"].exists)

        display.tap()
        XCTAssertTrue(app.buttons["列表视图"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["收藏夹视图"].exists)
        XCTAssertTrue(app.buttons["书名"].exists)
        attachScreenshot("书库-显示选项")
        app.buttons["列表视图"].tap()
        XCTAssertTrue(waitUntil { epub.frame.width > self.app.frame.width * 0.7 })
        app.swipeUp()
        attachScreenshot("书库-上滑标题栏")
        app.swipeDown()

        display.tap()
        app.buttons["收藏夹视图"].tap()
        XCTAssertTrue(app.staticTexts["收藏夹"].waitForExistence(timeout: 10))
        display.tap()
        XCTAssertTrue(app.buttons["网格视图"].waitForExistence(timeout: 5))
        app.buttons["列表视图"].tap()
        XCTAssertTrue(waitUntil { epub.exists && epub.frame.width > self.app.frame.width * 0.7 })

        display.tap()
        app.buttons["书名"].tap()
        XCTAssertTrue(waitUntil { epub.frame.minY < pdf.frame.minY })
        display.tap()
        app.buttons["网格视图"].tap()
        XCTAssertTrue(waitUntil { epub.frame.width < self.app.frame.width * 0.5 })
        attachScreenshot("书库-精简工具栏")
    }

    /// Requires an isolated preview library with enough books to scroll.
    func testLibraryHeaderReturnsWhenScrollingDown() {
        showLibrary()
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        let originalY = book.frame.minY
        app.swipeUp()
        XCTAssertTrue(waitUntil { !book.exists || book.frame.minY < originalY - 30 })
        let search = app.buttons.matching(identifier: "library.search.toggle")
        XCTAssertTrue(waitUntil { !search.allElementsBoundByIndex.contains(where: { $0.isHittable }) })
        attachScreenshot("书库-书籍上滑标题栏")
        app.swipeUp()
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.48))
        start.press(forDuration: 0.1, thenDragTo: end)
        XCTAssertTrue(waitUntil { search.allElementsBoundByIndex.contains(where: { $0.isHittable }) })
        XCTAssertTrue(!book.exists || book.frame.minY < originalY - 30)
        attachScreenshot("书库-下滑恢复标题栏")
        search.allElementsBoundByIndex.first(where: { $0.isHittable })?.tap()
        XCTAssertTrue(app.textFields["library.search"].waitForExistence(timeout: 5))
    }

    /// Requires the long preview library; repeated swipes reach and bounce off its bottom.
    func testLibraryBottomBounceDoesNotRevealHeader() {
        showLibrary()
        for _ in 0..<6 { app.swipeUp() }
        let search = app.buttons.matching(identifier: "library.search.toggle")
        XCTAssertTrue(waitUntil { !search.allElementsBoundByIndex.contains(where: { $0.isHittable }) },
                      "Bottom rubber-band motion must not reveal the header.")
        app.swipeUp()
        XCTAssertTrue(waitUntil { !search.allElementsBoundByIndex.contains(where: { $0.isHittable }) })
        attachScreenshot("书库-底部回弹不显示标题栏")
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.48))
        start.press(forDuration: 0.1, thenDragTo: end)
        XCTAssertTrue(waitUntil { search.allElementsBoundByIndex.contains(where: { $0.isHittable }) },
                      "Scrolling back into the books must still reveal the header.")
        attachScreenshot("书库-底部主动回滑显示标题栏")
    }

    func testLibraryGalleryModeShowsCoversOnlyAndPersists() {
        showLibrary()
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(epub.waitForExistence(timeout: 15))
        XCTAssertTrue(epub.staticTexts["Native UI EPUB"].waitForExistence(timeout: 5))
        let regularHeight = epub.frame.height

        setGalleryMode()
        XCTAssertTrue(waitUntil { epub.frame.height < regularHeight - 24 })
        attachScreenshot("书库-画廊模式")
        XCTAssertFalse(epub.staticTexts["Native UI EPUB"].exists)

        app.terminate()
        app.launch()
        XCTAssertTrue(app.staticTexts["书库"].waitForExistence(timeout: 30))
        let reopened = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(reopened.waitForExistence(timeout: 15))
        XCTAssertTrue(waitUntil { reopened.frame.height < regularHeight - 24 })

        // Restore the default grid for the remaining tests in this isolated run.
        setGalleryMode()
        XCTAssertTrue(waitUntil { epub.staticTexts["Native UI EPUB"].exists })
    }

    func testLibraryColorfulBackground() {
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        if colorful.value as? String != "1" { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        attachScreenshot("书库设置-多书封面背景")
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        XCTAssertTrue(app.staticTexts["library.view.title"].waitForExistence(timeout: 10))
        attachScreenshot("书库-多书封面背景")
        app.buttons["library.filters.open"].tap()
        XCTAssertTrue(app.buttons["library.filters.option.status.all"].waitForExistence(timeout: 10))
        attachScreenshot("筛选书籍-炫彩背景")
        app.buttons["library.filters.cancel"].tap()
        XCTAssertTrue(waitUntil { !self.app.buttons["library.filters.option.status.all"].exists })
        openLibrarySettings()
        colorful.tap()
        XCTAssertTrue(waitUntil { colorful.value as? String == "0" })
    }

    /// Requires a long preview library with distinct cover colours.
    func testColorfulBackgroundChangesPaletteWhileScrolling() {
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        if colorful.value as? String != "1" { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        showLibrary()
        attachScreenshot("炫彩滚动-顶部")
        app.tabBars.buttons["阅读资料"].tap()
        XCTAssertTrue(app.staticTexts["page.materials"].waitForExistence(timeout: 15))
        app.tabBars.buttons["阅读数据"].tap()
        XCTAssertTrue(app.staticTexts["page.dashboard"].waitForExistence(timeout: 15))
        app.tabBars.buttons["书库"].tap()
        app.swipeUp()
        app.swipeUp()
        attachScreenshot("炫彩滚动-中间")
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.48))
        start.press(forDuration: 0.1, thenDragTo: end)
        XCTAssertTrue(waitUntil { self.app.buttons.matching(identifier: "library.search.toggle").allElementsBoundByIndex.contains(where: { $0.isHittable }) })
        attachScreenshot("炫彩滚动-工具栏")
        // Every tab inherits the new visible-cover palette after scrolling,
        // while the background field positions remain fixed.
        for (tab, page, capture) in [
            ("阅读资料", "page.materials", "炫彩滚动后-阅读资料"),
            ("阅读数据", "page.dashboard", "炫彩滚动后-阅读数据")
        ] {
            app.tabBars.buttons[tab].tap()
            XCTAssertTrue(app.staticTexts[page].waitForExistence(timeout: 15))
            attachScreenshot(capture)
        }
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(app.buttons["profile.theme"].waitForExistence(timeout: 10))
        attachScreenshot("炫彩滚动后-我的")
        app.tabBars.buttons["书库"].tap()
        attachScreenshot("炫彩滚动后-返回书库")
    }

    func testColorfulModeOnCollectionsMaterialsAndReadingData() {
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        if colorful.value as? String != "1" { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        showLibrary(true)
        attachScreenshot("炫彩-收藏夹")
        app.tabBars.buttons["阅读资料"].tap()
        XCTAssertTrue(app.staticTexts["page.materials"].waitForExistence(timeout: 15))
        attachScreenshot("炫彩-阅读资料")
        app.tabBars.buttons["阅读数据"].tap()
        XCTAssertTrue(app.staticTexts["page.dashboard"].waitForExistence(timeout: 15))
        attachScreenshot("炫彩-阅读数据")
        // The dark wash must keep the same even coverage without turning the
        // neutral cards into tinted blocks.
        app.tabBars.buttons["我的"].tap()
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        guard let originalTheme = theme.value as? String else {
            XCTFail("The current theme must be known before changing preferences.")
            return
        }
        selectTheme("深色")
        app.tabBars.buttons["书库"].tap()
        showLibrary(true)
        attachScreenshot("炫彩-深色-收藏夹")
        app.tabBars.buttons["阅读资料"].tap()
        XCTAssertTrue(app.staticTexts["page.materials"].waitForExistence(timeout: 15))
        attachScreenshot("炫彩-深色-阅读资料")
        app.tabBars.buttons["阅读数据"].tap()
        XCTAssertTrue(app.staticTexts["page.dashboard"].waitForExistence(timeout: 15))
        attachScreenshot("炫彩-深色-阅读数据")
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        selectTheme(originalTheme)
        app.tabBars.buttons["书库"].tap()
        showLibrary()
        openLibrarySettings()
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        if colorful.value as? String == "1" { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "0" })
    }

    func testLibraryColorfulModeShowsAndPersists() throws {
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        XCTAssertEqual(colorful.value as? String, "0")
        colorful.tap()
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        attachScreenshot("书库设置-炫彩模式")
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        XCTAssertTrue(app.staticTexts["library.view.title"].waitForExistence(timeout: 10))
        attachScreenshot("书库-炫彩模式")
        try openBook(containing: "Native UI EPUB", revealControls: false)
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
        attachScreenshot("阅读器-炫彩模式")
        if !app.buttons["reader.排版"].exists {
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        }
        XCTAssertTrue(waitUntil { self.app.buttons["reader.排版"].exists })
        app.terminate()
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["我的"].waitForExistence(timeout: 30))
        openLibrarySettings()
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        XCTAssertEqual(colorful.value as? String, "1")
        app.navigationBars.buttons.element(boundBy: 0).tap()
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        guard let originalTheme = theme.value as? String else {
            XCTFail("The current theme must be known before changing preferences.")
            return
        }
        selectTheme("深色")
        openLibrarySettings()
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        attachScreenshot("书库设置-深色炫彩模式")
        colorful.tap()
        XCTAssertTrue(waitUntil { colorful.value as? String == "0" })
        attachScreenshot("书库设置-炫彩关闭")
        app.navigationBars.buttons.element(boundBy: 0).tap()
        selectTheme(originalTheme)
    }

    func testLibrarySyncStatusSettingShowsAndPersists() {
        showLibrary()
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(epub.waitForExistence(timeout: 15))
        openLibrarySettings()
        let toggle = app.switches["library.settings.syncStatus"]
        XCTAssertTrue(toggle.waitForExistence(timeout: 10))
        XCTAssertEqual(toggle.value as? String, "0")
        toggle.tap()
        XCTAssertTrue(waitUntil { toggle.value as? String == "1" })
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        XCTAssertTrue(waitUntil { epub.label.contains("未同步") })
        app.terminate()
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["我的"].waitForExistence(timeout: 30))
        openLibrarySettings()
        XCTAssertTrue(toggle.waitForExistence(timeout: 10))
        XCTAssertEqual(toggle.value as? String, "1")
        toggle.tap()
    }

    func testLibraryCombinedFiltersAndClear() {
        showLibrary()
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        let pdf = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI PDF'")).firstMatch
        XCTAssertTrue(epub.waitForExistence(timeout: 15))
        XCTAssertTrue(pdf.waitForExistence(timeout: 15))

        app.buttons["library.filters.open"].tap()
        XCTAssertTrue(app.buttons["library.filters.option.format.EPUB"].waitForExistence(timeout: 10))
        attachScreenshot("书库-筛选面板")
        app.buttons["library.filters.option.format.EPUB"].tap()
        app.buttons["library.filters.cancel"].tap()
        XCTAssertTrue(epub.exists && pdf.exists)
        XCTAssertEqual(app.buttons["library.filters.open"].label, "筛选书籍")

        app.buttons["library.filters.open"].tap()
        app.buttons["library.filters.option.format.EPUB"].tap()
        app.buttons["library.filters.apply"].tap()
        XCTAssertTrue(waitUntil { epub.exists && !pdf.exists })
        XCTAssertTrue(app.buttons["library.filters.open"].label.contains("已选 1 项"))
        attachScreenshot("书库-按格式筛选")

        // The EPUB fixture is not finished, so this combination has no matches.
        app.buttons["library.filters.open"].tap()
        app.buttons["library.filters.option.status.2"].tap()
        app.buttons["library.filters.apply"].tap()
        XCTAssertTrue(waitUntil { self.app.staticTexts["没有找到相关书籍"].exists })
        XCTAssertFalse(app.buttons["library.continue"].exists)
        attachScreenshot("书库-组合筛选无结果")

        app.buttons["library.filters.open"].tap()
        app.buttons["library.filters.clear"].tap()
        app.buttons["library.filters.apply"].tap()
        XCTAssertTrue(waitUntil { epub.exists && pdf.exists })
        XCTAssertEqual(app.buttons["library.filters.open"].label, "筛选书籍")
    }

    /// Uses an isolated EPUB fixture with multiple authors, a tag and a category.
    func testLibraryFilterSearchAndMetadataCombination() throws {
        showLibrary()
        let epub = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        let pdf = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI PDF'")).firstMatch
        XCTAssertTrue(epub.waitForExistence(timeout: 15))
        app.buttons["library.filters.open"].tap()
        let authorRow = app.buttons["library.filters.metadata.author"]
        guard authorRow.waitForExistence(timeout: 10) else { throw XCTSkip("需要包含多个作者的隔离书库测试数据") }
        authorRow.tap()
        let search = app.buttons["library.filters.search.author"]
        XCTAssertTrue(search.waitForExistence(timeout: 5))
        attachScreenshot("书库-筛选作者")
        search.tap()
        let input = app.textFields["library.filters.search.input.author"]
        XCTAssertTrue(input.waitForExistence(timeout: 5))
        input.typeText("筛选作者12\n")
        let author = app.buttons["library.filters.option.author.筛选作者12"]
        XCTAssertTrue(author.waitForExistence(timeout: 5))
        author.tap()
        XCTAssertTrue(waitUntil { authorRow.exists && authorRow.isHittable })
        app.buttons["library.filters.metadata.tag"].tap()
        let tag = app.buttons["library.filters.option.tag.测试标签"]
        XCTAssertTrue(tag.waitForExistence(timeout: 5))
        XCTAssertTrue(tag.isHittable)
        tag.tap()
        XCTAssertTrue(waitUntil { authorRow.exists && authorRow.isHittable })
        app.buttons["library.filters.metadata.category"].tap()
        let category = app.buttons["library.filters.option.category.测试分类"]
        XCTAssertTrue(category.waitForExistence(timeout: 5))
        XCTAssertTrue(category.isHittable)
        category.tap()
        XCTAssertTrue(waitUntil { authorRow.exists && authorRow.isHittable })
        attachScreenshot("书库-组合筛选条件")
        app.buttons["library.filters.apply"].tap()
        XCTAssertTrue(waitUntil { epub.exists && !pdf.exists })
        XCTAssertTrue(app.buttons["library.filters.open"].label.contains("已选 3 项"))
        app.buttons["library.filters.open"].tap()
        app.buttons["library.filters.clear"].tap()
        app.buttons["library.filters.apply"].tap()
        XCTAssertTrue(waitUntil { epub.exists && pdf.exists })
    }

    /// Run on a fresh isolated preview seed, which has no sync key or credentials.
    func testBackupEncryptionKeyPrompt() {
        app.tabBars.buttons["我的"].tap()
        let entry = app.buttons["profile.backup"]
        XCTAssertTrue(entry.waitForExistence(timeout: 15))
        reveal(entry); entry.tap()
        for identifier in ["profile.backup.export", "profile.backup.restore"] {
            let button = app.buttons[identifier]
            XCTAssertTrue(button.waitForExistence(timeout: 10))
            reveal(button); button.tap()
            let prompt = app.alerts["操作失败"]
            XCTAssertTrue(prompt.waitForExistence(timeout: 15))
            let message = prompt.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "设置同步密钥")).firstMatch
            XCTAssertTrue(message.exists)
            XCTAssertTrue(message.label.contains("密钥不会上传"))
            XCTAssertTrue(message.label.contains("遗忘后无法解密"))
            attachScreenshot("备份-先设置同步密钥")
            prompt.buttons["好"].tap()
            XCTAssertTrue(waitUntil { !prompt.exists })
        }
    }

    func testReadingCalendarPeriodPicker() {
        app.tabBars.buttons["阅读数据"].tap()
        let picker = app.segmentedControls["dashboard.period"]
        XCTAssertTrue(picker.waitForExistence(timeout: 20))
        XCTAssertFalse(picker.buttons["14 天"].exists)
        for (label, title) in [("周", "本周阅读时长"), ("月", "本月阅读时长"), ("年", "今年阅读时长")] {
            picker.buttons[label].tap()
            XCTAssertTrue(app.staticTexts[title].waitForExistence(timeout: 10))
        }
        attachScreenshot("阅读数据-周月年")
    }

    func testReadingMedalsShowCatalogArtwork() {
        app.tabBars.buttons["阅读数据"].tap()
        XCTAssertTrue(app.segmentedControls["dashboard.period"].waitForExistence(timeout: 20))
        let showAll = app.buttons["全部"]
        reveal(showAll)
        XCTAssertTrue(showAll.exists && showAll.isHittable)
        showAll.tap()
        let first = app.buttons["dashboard.medal.初见书页"]
        let second = app.buttons["dashboard.medal.读完一程"]
        reveal(first)
        XCTAssertTrue(first.exists && second.exists)
        attachScreenshot("阅读勋章-全部")
        first.tap()
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "累计阅读 1 小时")).firstMatch.waitForExistence(timeout: 5))
    }

    /// Requires the two local account servers at 5088 and 5089, each with an isolated test database.
    func testRegionalAccountRegistrationAndFreeSyncRestriction() {
        let username = "region_" + String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12))
        let password = "fictional-native-password"
        print("LIBZORA_FICTIONAL_ACCOUNT:\(username)")
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        let regions = app.segmentedControls["account.region"]
        if app.staticTexts["account.username"].exists {
            reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
        }
        reveal(regions)
        XCTAssertTrue(regions.waitForExistence(timeout: 15))
        XCTAssertEqual(regions.buttons.count, 2)
        tapAccountElement(regions.buttons["中国区"])
        XCTAssertTrue(waitUntil { regions.buttons["中国区"].isSelected })
        scrollAccountToTop()
        register()
        verifySignOutAndLogin()
        attachScreenshot("账号-中国区")
        tapAccountElement(app.navigationBars["用户账号"].buttons.firstMatch)
        syncCurrentAccount()
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 15))
        // The region picker only exists on the signed-out form now.
        reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
        reveal(regions)
        XCTAssertTrue(regions.waitForExistence(timeout: 10))
        tapAccountElement(regions.buttons["国际区"])
        XCTAssertTrue(app.staticTexts["account.restart"].waitForExistence(timeout: 10))
        XCTAssertTrue(waitUntil { regions.buttons["国际区"].isSelected })
        scrollAccountToTop()
        register()
        verifySignOutAndLogin()
        attachScreenshot("账号-国际区-重开提示")
        app.terminate(); app.launch()
        XCTAssertTrue(app.tabBars.buttons["我的"].waitForExistence(timeout: 30))
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch.exists)
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 15))
        XCTAssertEqual(app.staticTexts["account.username"].label, username)
        XCTAssertFalse(app.staticTexts["account.restart"].exists)
        tapAccountElement(app.navigationBars["用户账号"].buttons.firstMatch)
        syncCurrentAccount()
        app.tabBars.buttons["书库"].tap()
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch.exists)
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 15))
        // Region switching is part of the signed-out form, so sign out first.
        reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
        reveal(regions)
        XCTAssertTrue(regions.waitForExistence(timeout: 10))
        tapAccountElement(regions.buttons["中国区"])
        scrollAccountToTop()
        XCTAssertTrue(app.segmentedControls["account.mode"].waitForExistence(timeout: 15))
        app.terminate(); app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 30))
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch.waitForExistence(timeout: 15))

        func register() {
            let mode = app.segmentedControls["account.mode"]
            XCTAssertTrue(mode.waitForExistence(timeout: 10))
            tapAccountElement(mode.buttons["注册"])
            let email = app.textFields["account.emailField"]
            reveal(email); email.tap(); email.typeText(username + "@example.invalid"); dismissKeyboard()
            let user = app.textFields["account.usernameField"]
            reveal(user); user.tap(); user.typeText(username); dismissKeyboard()
            let secret = app.secureTextFields["account.password"]
            typePassword(secret, password)
            XCTAssertEqual((secret.value as? String)?.count, password.count, "The password field must retain every typed character.")
            let confirmation = app.secureTextFields["account.confirmation"]
            typePassword(confirmation, password)
            XCTAssertEqual((confirmation.value as? String)?.count, password.count)
            tapAccountElement(app.buttons["account.submit"])
            XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
            XCTAssertEqual(app.staticTexts["account.username"].label, username)
        }

        func verifySignOutAndLogin() {
            reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
            let mode = app.segmentedControls["account.mode"]
            XCTAssertTrue(mode.waitForExistence(timeout: 15))
            tapAccountElement(mode.buttons["登录"])
            let secret = app.secureTextFields["account.password"]
            typePassword(secret, "fictional-wrong-password")
            tapAccountElement(app.buttons["account.submit"])
            XCTAssertTrue(app.staticTexts["用户名、邮箱或密码错误。"].waitForExistence(timeout: 15))
            XCTAssertFalse(app.staticTexts["account.username"].exists)
            let cleared = secret.value as? String ?? ""
            XCTAssertTrue(cleared.isEmpty || cleared == secret.placeholderValue, "Failed login must clear the password.")
            typePassword(secret, password)
            tapAccountElement(app.buttons["account.submit"])
            XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
            XCTAssertEqual(app.staticTexts["account.username"].label, username)
        }

        func typePassword(_ field: XCUIElement, _ value: String) {
            reveal(field); field.tap()
            // iOS can suggest a strong password after the first character.
            // Dismiss that system sheet before typing the rest of this fictional password.
            field.typeText(String(value.prefix(1)))
            let suggestion = app.staticTexts["使用强密码？"]
            if suggestion.waitForExistence(timeout: 2) {
                let close = app.buttons.matching(NSPredicate(format: "label IN {'关闭', 'Close', '取消', 'Cancel'}")).firstMatch
                XCTAssertTrue(close.exists, "The strong-password sheet must provide a dismiss button. Buttons: \(app.buttons.allElementsBoundByIndex.map(\.label))")
                close.tap()
                XCTAssertTrue(waitUntil { !suggestion.exists })
                // The system can reset the initial character when returning
                // focus to the secure field after declining its suggestion.
                field.typeText(XCUIKeyboardKey.delete.rawValue)
                field.typeText(value)
            } else {
                field.typeText(String(value.dropFirst()))
            }
            dismissKeyboard()
        }

        func syncCurrentAccount() {
            tapAccountElement(app.buttons["profile.sync"])
            let providers = app.segmentedControls["sync.provider"]
            XCTAssertTrue(providers.waitForExistence(timeout: 15))
            XCTAssertEqual(providers.buttons.count, 2)
            XCTAssertFalse(providers.buttons["账号云同步"].exists)
            tapAccountElement(providers.buttons["WebDAV"])
            let endpoint = app.textFields["服务地址"]
            reveal(endpoint); replaceText(endpoint, with: "https://fictional-storage.example.invalid/dav"); dismissKeyboard()
            let username = app.textFields["用户名"]
            reveal(username); replaceText(username, with: "fictional-sync-reader"); dismissKeyboard()
            typePassword(app.secureTextFields["密码"], "fictional-sync-password")
            let run = app.buttons["sync.run"]
            reveal(run); tapAccountElement(run)
            XCTAssertTrue(waitUntil { self.app.staticTexts["sync.status"].label.contains("需要有效的试用订阅或买断订阅") })
            attachScreenshot("Free-自备同步需功能订阅")
            tapAccountElement(app.navigationBars["同步"].buttons.firstMatch)
        }
    }

    /// Local demo only: registration, rights refresh without self-grants and current-device revocation.
    func testAccountMembershipDevicesAndRightsRefresh() {
        let username = "region_" + String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12))
        let password = "fictional-native-password"
        print("LIBZORA_FICTIONAL_ACCOUNT:\(username)")
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        let regions = app.segmentedControls["account.region"]
        if app.staticTexts["account.username"].exists {
            reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
        }
        reveal(regions)
        XCTAssertTrue(regions.waitForExistence(timeout: 15))
        tapAccountElement(regions.buttons["中国区"])
        scrollAccountToTop()
        let mode = app.segmentedControls["account.mode"]
        XCTAssertTrue(mode.waitForExistence(timeout: 15))
        tapAccountElement(mode.buttons["注册"])
        let email = app.textFields["account.emailField"]
        reveal(email); replaceText(email, with: username + "@example.invalid"); dismissKeyboard()
        let user = app.textFields["account.usernameField"]
        reveal(user); replaceText(user, with: username); dismissKeyboard()
        enterPassword(app.secureTextFields["account.password"])
        enterPassword(app.secureTextFields["account.confirmation"])
        tapAccountElement(app.buttons["account.submit"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
        XCTAssertEqual(app.staticTexts["account.username"].label, username)
        // Signing in recreates the form at the top. A busy simulator can keep
        // the old bottom offset for a moment, so walk back up before looking
        // for the membership rows below it.
        for _ in 0..<4 {
            let tier = app.staticTexts["membership.tier"]
            if tier.exists && tier.isHittable { break }
            app.swipeDown()
        }
        let tier = app.staticTexts["membership.tier"]
        reveal(tier)
        XCTAssertTrue(tier.waitForExistence(timeout: 20))
        XCTAssertEqual(tier.label, "Free")
        XCTAssertEqual(app.staticTexts["membership.sync"].label, "Free · 畅享本地阅读")
        let features = app.staticTexts["membership.sync.features"]
        reveal(features)
        XCTAssertTrue(features.waitForExistence(timeout: 15))
        XCTAssertTrue(features.label.contains("领取试用或购买买断订阅"))
        XCTAssertFalse(app.staticTexts["membership.storage.quota"].exists)
        XCTAssertFalse(app.staticTexts["membership.download.quota"].exists)
        attachScreenshot("会员-Free自备同步权益")
        let capacity = app.staticTexts["membership.devices.capacity"]
        reveal(capacity)
        XCTAssertTrue(capacity.waitForExistence(timeout: 15))
        XCTAssertEqual(capacity.label, "还可登录 5 台设备")
        let current = app.staticTexts.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.device.' AND label CONTAINS '当前设备'")).firstMatch
        reveal(current)
        XCTAssertTrue(current.waitForExistence(timeout: 15))
        attachScreenshot("会员-Free与当前设备")
        let refresh = app.buttons["membership.rights.refresh"]
        reveal(refresh)
        XCTAssertTrue(refresh.waitForExistence(timeout: 15))
        XCTAssertEqual(refresh.label, "刷新权益")
        tapAccountElement(refresh)
        XCTAssertTrue(waitUntil { refresh.isEnabled })
        reveal(tier)
        XCTAssertEqual(tier.label, "Free", "Refreshing must not grant permanent rights to a new account")
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.buy.'")).firstMatch.exists)
        XCTAssertFalse(app.staticTexts.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.order.'")).firstMatch.exists)
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.activation.'")).firstMatch.exists)
        XCTAssertFalse(app.textFields.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.activation.'")).firstMatch.exists)
        attachScreenshot("会员-账号权益刷新")
        let kick = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'membership.kick.'")).firstMatch
        for _ in 0..<8 { if kick.exists && kick.isHittable { break }; app.swipeDown() }
        tapAccountElement(kick)
        let confirm = app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "踢出 ")).firstMatch
        XCTAssertTrue(confirm.waitForExistence(timeout: 10)); confirm.tap()
        let invalidation = app.staticTexts["设备已失效"]
        XCTAssertTrue(invalidation.waitForExistence(timeout: 15))
        attachScreenshot("会员-设备失效提示")
        tapAccountElement(app.buttons["知道了"])
        XCTAssertTrue(mode.waitForExistence(timeout: 15))
        XCTAssertFalse(app.staticTexts["account.username"].exists)

        func enterPassword(_ field: XCUIElement) {
            reveal(field); field.tap(); field.typeText(String(password.prefix(1)))
            let suggestion = app.staticTexts["使用强密码？"]
            if suggestion.waitForExistence(timeout: 2) {
                let close = app.buttons.matching(NSPredicate(format: "label IN {'关闭', 'Close', '取消', 'Cancel'}")).firstMatch
                XCTAssertTrue(close.exists); close.tap(); XCTAssertTrue(waitUntil { !suggestion.exists })
                field.typeText(XCUIKeyboardKey.delete.rawValue); field.typeText(password)
            } else { field.typeText(String(password.dropFirst())) }
            dismissKeyboard()
        }
    }

    /// A new account claims Trial only by choice; the switch then locks and
    /// unlocks the same own-storage sync features as Lifetime, with no cloud quotas.
    func testTrialStaysUnclaimedUntilTheSwitchIsTurnedOn() {
        let username = "trial_" + String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12))
        let password = "fictional-native-password"
        print("LIBZORA_FICTIONAL_ACCOUNT:\(username)")
        app.tabBars.buttons["我的"].tap()
        tapAccountElement(app.buttons["profile.account"])
        let regions = app.segmentedControls["account.region"]
        if app.staticTexts["account.username"].exists {
            reveal(app.buttons["account.logout"]); tapAccountElement(app.buttons["account.logout"])
        }
        reveal(regions)
        XCTAssertTrue(regions.waitForExistence(timeout: 15))
        tapAccountElement(regions.buttons["中国区"])
        scrollAccountToTop()
        let mode = app.segmentedControls["account.mode"]
        XCTAssertTrue(mode.waitForExistence(timeout: 15))
        tapAccountElement(mode.buttons["注册"])
        let email = app.textFields["account.emailField"]
        reveal(email); replaceText(email, with: username + "@example.invalid"); dismissKeyboard()
        let user = app.textFields["account.usernameField"]
        reveal(user); replaceText(user, with: username); dismissKeyboard()
        enterPassword(app.secureTextFields["account.password"])
        enterPassword(app.secureTextFields["account.confirmation"])
        tapAccountElement(app.buttons["account.submit"])
        XCTAssertTrue(app.staticTexts["account.username"].waitForExistence(timeout: 20))
        XCTAssertEqual(app.staticTexts["account.username"].label, username)

        let toggle = app.switches["membership.trial.toggle"]
        for _ in 0..<6 {
            if toggle.exists && toggle.isHittable { break }
            app.swipeDown()
        }
        reveal(toggle)
        XCTAssertTrue(toggle.waitForExistence(timeout: 20))
        XCTAssertEqual(toggle.value as? String, "0", "Creating the account must not claim the trial.")
        XCTAssertEqual(app.staticTexts["membership.tier"].label, "Free")
        let trialStatus = app.staticTexts["membership.trial.status"]
        XCTAssertTrue(trialStatus.exists)
        XCTAssertTrue(trialStatus.label.contains("每账号主动领取一次"), trialStatus.label)
        attachScreenshot("试用-未领取")

        // The row label is a plain label; the claim switch itself is the control.
        let claimSwitch = toggle.switches.firstMatch
        XCTAssertTrue(claimSwitch.waitForExistence(timeout: 10))
        tapAccountElement(claimSwitch)
        XCTAssertTrue(waitUntil {
            trialStatus.exists && trialStatus.label.contains("试用期至")
        }, "Turning the switch on must claim the trial: \(app.debugDescription)")
        XCTAssertTrue(waitUntil { toggle.value as? String == "1" && !toggle.isEnabled },
                      "The claim switch must lock itself after claiming.")
        XCTAssertTrue(waitUntil { self.app.staticTexts["membership.tier"].label == "试用订阅" })
        let features = app.staticTexts["membership.sync.features"]
        reveal(features)
        XCTAssertTrue(features.waitForExistence(timeout: 15))
        XCTAssertTrue(features.label.contains("自备 S3 / WebDAV 多端同步已解锁"))
        XCTAssertFalse(app.staticTexts["membership.storage.quota"].exists)
        XCTAssertFalse(app.staticTexts["membership.download.quota"].exists)
        attachScreenshot("试用-已领取")

        func enterPassword(_ field: XCUIElement) {
            reveal(field); field.tap(); field.typeText(String(password.prefix(1)))
            let suggestion = app.staticTexts["使用强密码？"]
            if suggestion.waitForExistence(timeout: 2) {
                let close = app.buttons.matching(NSPredicate(format: "label IN {'关闭', 'Close', '取消', 'Cancel'}")).firstMatch
                XCTAssertTrue(close.exists); close.tap(); XCTAssertTrue(waitUntil { !suggestion.exists })
                field.typeText(XCUIKeyboardKey.delete.rawValue); field.typeText(password)
            } else { field.typeText(String(password.dropFirst())) }
            dismissKeyboard()
        }
    }

    /// Apple platforms expose iCloud without confusing its wire value (3)
    /// with the retired official-cloud provider (2). S3/WebDAV keep their tools.
    func testCustomSyncShowsAppleProvidersAndKeepsStorageToolsAvailable() {
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(app.staticTexts["page.profile"].waitForExistence(timeout: 15))
        XCTAssertFalse(app.buttons.matching(NSPredicate(format: "label CONTAINS %@", "开发者")).firstMatch.exists)
        app.buttons["profile.sync"].tap()
        let providers = app.segmentedControls["sync.provider"]
        XCTAssertTrue(providers.waitForExistence(timeout: 15))
        XCTAssertTrue(providers.buttons["iCloud"].waitForExistence(timeout: 15))
        XCTAssertEqual(providers.buttons.count, 3)
        XCTAssertEqual(providers.buttons.element(boundBy: 0).label, "iCloud")
        XCTAssertEqual(providers.buttons.element(boundBy: 1).label, "S3")
        XCTAssertEqual(providers.buttons.element(boundBy: 2).label, "WebDAV")
        XCTAssertFalse(providers.buttons["账号云同步"].exists)
        providers.buttons["S3"].tap()
        let prefix = app.textFields["目录前缀"]
        reveal(prefix)
        XCTAssertTrue(prefix.exists)
        let encryption = app.secureTextFields["同步密钥（备份必填，同步建议加密）"]
        reveal(encryption)
        XCTAssertTrue(encryption.waitForExistence(timeout: 10))
        for label in ["导入连接参数", "导出连接参数", "测试连接", "高级选项"] {
            let control = app.buttons[label]
            reveal(control)
            XCTAssertTrue(control.exists, label)
        }
        let run = app.buttons["sync.run"]
        reveal(run)
        XCTAssertTrue(run.exists)
        attachScreenshot("自备同步-连接工具")
        for _ in 0..<8 where !providers.isHittable { app.swipeDown() }
        providers.buttons["WebDAV"].tap()
        XCTAssertTrue(app.textFields["服务地址"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.textFields["用户名"].exists)
        XCTAssertTrue(app.secureTextFields["密码"].exists)
        XCTAssertFalse(app.buttons["sync.membership.manage"].exists)
        attachScreenshot("自备同步-WebDAV")
    }

    func testICloudSyncHidesCredentialsAndPersistsDisabledChoice() {
        showLibrary()
        attachScreenshot("iCloud同步-现有书库")
        app.tabBars.buttons["我的"].tap()
        app.buttons["profile.sync"].tap()
        let providers = app.segmentedControls["sync.provider"]
        XCTAssertTrue(providers.waitForExistence(timeout: 15))
        XCTAssertTrue(providers.buttons["iCloud"].waitForExistence(timeout: 15))
        providers.buttons["iCloud"].tap()
        let enabled = app.switches["启用同步"]
        if enabled.value as? String == "1" { enabled.tap() }
        XCTAssertTrue(app.descendants(matching: .any).matching(identifier: "sync.icloud.info").firstMatch.waitForExistence(timeout: 10))
        XCTAssertTrue(app.textFields["目录前缀"].exists)
        for label in ["服务地址", "用户名", "Bucket", "Region", "Access Key", "Endpoint（AWS 可留空）"] {
            XCTAssertFalse(app.textFields[label].exists, label)
        }
        XCTAssertFalse(app.secureTextFields["密码"].exists)
        XCTAssertFalse(app.secureTextFields["Secret Key"].exists)
        XCTAssertFalse(app.buttons["导入连接参数"].exists)
        XCTAssertFalse(app.buttons["导出连接参数"].exists)
        attachScreenshot("iCloud同步-无需服务器凭据")
        let advanced = app.buttons["高级选项"]
        reveal(advanced); advanced.tap()
        XCTAssertTrue(app.switches["同步时下载书籍文件"].exists)
        XCTAssertFalse(app.switches["跳过 TLS 验证"].exists)
        XCTAssertFalse(app.switches["Path Style"].exists)
        let key = app.secureTextFields["sync.encryptionKey"]
        reveal(key); XCTAssertTrue(key.exists)
        let save = app.buttons["sync.save"]
        reveal(save); save.tap()
        XCTAssertTrue(app.staticTexts["已保存"].waitForExistence(timeout: 10))
        app.navigationBars["同步"].buttons.firstMatch.tap()
        app.buttons["profile.sync"].tap()
        let iCloud = providers.buttons["iCloud"]
        // The form is initially disabled with fallback options until the
        // platform registry/settings request completes. Wait for that payload,
        // not merely the segmented control's first placeholder snapshot.
        XCTAssertTrue(iCloud.waitForExistence(timeout: 15))
        XCTAssertTrue(waitUntil { iCloud.isSelected })
        XCTAssertEqual(app.switches["启用同步"].value as? String, "0")
        attachScreenshot("iCloud同步-选择已保存")
        // This simulator has no provisioned iCloud account. The bridge must
        // still resolve its static native symbols and report a sign-in prompt,
        // never a missing-library/entrypoint error or a fake successful sync.
        let test = app.buttons["测试连接"]
        reveal(test); test.tap()
        let status = app.staticTexts["sync.status"]
        XCTAssertTrue(waitUntil(timeout: 15) { status.label.contains("Apple") && status.label.contains("iCloud") })
        XCTAssertFalse(status.label.contains("DllNotFound") || status.label.contains("EntryPoint"))
    }

    func testSyncKeyboardCanDismissAndRefocusAndSaveDisabledDraft() {
        app.tabBars.buttons["我的"].tap()
        app.buttons["profile.sync"].tap()
        XCTAssertTrue(app.navigationBars["同步"].waitForExistence(timeout: 15))
        app.segmentedControls.buttons["WebDAV"].tap()
        let endpoint = app.textFields["服务地址"]
        XCTAssertTrue(endpoint.waitForExistence(timeout: 10))
        replaceText(endpoint, with: "https://example.invalid/libzora")
        dismissKeyboard()
        let username = app.textFields["用户名"]
        username.tap()
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5))
        replaceText(username, with: "native-keyboard-test")
        dismissKeyboard()
        reveal(app.buttons["sync.save"])
        app.buttons["sync.save"].tap()
        XCTAssertTrue(app.staticTexts["已保存"].waitForExistence(timeout: 10))
        attachScreenshot("同步设置")
        app.navigationBars["同步"].buttons.firstMatch.tap()
        app.buttons["profile.sync"].tap()
        XCTAssertEqual(app.textFields["用户名"].value as? String, "native-keyboard-test")
    }

    func testCollectionPromptCanBeOpenedAgainAfterKeyboardDismissal() {
        showLibrary(true)
        for _ in 0..<2 {
            app.buttons["library.import"].tap()
            let input = app.textFields["name.input"]
            XCTAssertTrue(input.waitForExistence(timeout: 10))
            input.tap()
            input.typeText("原生测试-" + String(UUID().uuidString.prefix(6)))
            app.buttons["name.save"].tap()
            XCTAssertTrue(waitUntil { !input.exists })
        }
        attachScreenshot("收藏夹")
        showLibrary()
    }

    func testOpenedCollectionSharesLibraryGridAndListAndCanRemoveBooks() {
        showLibrary()
        app.buttons["library.display"].tap()
        app.buttons["网格视图"].tap()
        let source = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(source.waitForExistence(timeout: 15))
        let bookIdentifier = source.identifier
        let bookId = String(bookIdentifier.dropFirst("book.".count))
        let gridFrame = source.frame
        attachScreenshot("书库封面网格")

        showLibrary(true)
        app.buttons["library.import"].tap()
        let name = "同款书架-" + String(UUID().uuidString.prefix(6))
        let input = app.textFields["name.input"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        input.tap(); input.typeText(name)
        app.buttons["name.save"].tap()
        XCTAssertTrue(waitUntil { !input.exists })
        let folder = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.' AND label CONTAINS %@", name)).firstMatch
        XCTAssertTrue(folder.waitForExistence(timeout: 10))
        reveal(folder); folder.tap()
        app.buttons["collection.add"].tap()
        let picker = app.buttons["collection.pick.\(bookId)"]
        XCTAssertTrue(picker.waitForExistence(timeout: 10))
        picker.tap()
        XCTAssertTrue(waitUntil { !picker.exists })
        // Include the PDF and another local book to check a complete grid row.
        for _ in 0..<2 {
            let next = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.pick.'")).firstMatch
            guard next.exists else { break }
            let identifier = next.identifier
            next.tap()
            XCTAssertTrue(waitUntil { !self.app.buttons[identifier].exists })
        }
        app.buttons["完成"].tap()
        let collectedBook = app.buttons[bookIdentifier]
        XCTAssertTrue(collectedBook.waitForExistence(timeout: 10))
        XCTAssertEqual(collectedBook.frame.width, gridFrame.width, accuracy: 1)
        XCTAssertEqual(collectedBook.frame.height, gridFrame.height, accuracy: 2)
        let gridBooks = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).allElementsBoundByIndex
        for book in gridBooks {
            XCTAssertEqual(book.frame.minY, collectedBook.frame.minY, accuracy: 1)
        }
        attachScreenshot("收藏夹同款封面网格")

        app.buttons["collection.layout"].tap()
        XCTAssertTrue(waitUntil { collectedBook.frame.width > gridFrame.width * 2 })
        let listFrame = collectedBook.frame
        attachScreenshot("收藏夹同款图书列表")
        openBookDetails(collectedBook)
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.back"].tap()
        XCTAssertTrue(app.buttons["collection.back"].waitForExistence(timeout: 10))
        app.buttons["collection.back"].tap()
        showLibrary(false, returnToList: true)
        XCTAssertTrue(app.buttons["library.display"].exists)
        XCTAssertTrue(source.waitForExistence(timeout: 10))
        XCTAssertEqual(source.frame.width, listFrame.width, accuracy: 1)
        XCTAssertEqual(source.frame.height, listFrame.height, accuracy: 1)
        attachScreenshot("书库同款图书列表")

        showLibrary(true)
        reveal(folder); folder.tap()
        XCTAssertTrue(collectedBook.waitForExistence(timeout: 10))
        collectedBook.press(forDuration: 1)
        let remove = app.buttons["移出收藏夹"]
        XCTAssertTrue(remove.waitForExistence(timeout: 10))
        remove.tap()
        XCTAssertTrue(waitUntil { !collectedBook.exists })
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.'")).firstMatch.exists)
    }

    /// Uses the isolated preview EPUB containing “Fictional local sync test.”
    func testReaderSearchCloseButtonAndHighlightedResults() throws {
        try openBook(containing: "Native UI EPUB")
        let search = app.buttons["reader.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 10))
        search.tap()
        let input = app.textFields["reader.search.query"]
        let close = app.buttons["reader.search.close"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        XCTAssertTrue(close.exists)
        XCTAssertFalse(search.exists)
        XCTAssertLessThan(input.frame.maxX, close.frame.minX)
        attachScreenshot("阅读搜索-展开")
        input.typeText("LOCAL\n")
        let result = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.' AND label CONTAINS[c] 'local'")).firstMatch
        XCTAssertTrue(result.waitForExistence(timeout: 30), app.debugDescription)
        attachScreenshot("阅读搜索-关键词高亮")
        replaceText(input, with: "i")
        input.typeText("\n")
        let repeated = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.' AND label CONTAINS[c] 'Fictional'")).firstMatch
        XCTAssertTrue(repeated.waitForExistence(timeout: 30), app.debugDescription)
        attachScreenshot("阅读搜索-多次匹配高亮")
        close.tap()
        XCTAssertTrue(waitUntil { !input.exists && !self.app.descendants(matching: .any)["reader.search.results"].exists })
        XCTAssertTrue(search.exists)
    }

    func testReaderSearchHighlightInDarkTheme() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        typography.tap()
        let paper = app.segmentedControls["typography.paper"]
        XCTAssertTrue(paper.waitForExistence(timeout: 10))
        paper.buttons["黑"].tap()
        app.buttons["typography.done"].tap()
        let search = app.buttons["reader.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 10))
        search.tap()
        let input = app.textFields["reader.search.query"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        input.typeText("local\n")
        let result = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.' AND label CONTAINS[c] 'local'")).firstMatch
        XCTAssertTrue(result.waitForExistence(timeout: 30))
        attachScreenshot("阅读搜索-深色模式高亮")
        app.buttons["reader.search.close"].tap()
        typography.tap()
        XCTAssertTrue(paper.waitForExistence(timeout: 10))
        paper.buttons["白"].tap()
        app.buttons["typography.done"].tap()
    }

    func testReaderLiveSearchNavigatesMatchesUntilPageTurn() throws {
        try openBook(containing: "Walden", revealControls: false)
        let search = app.buttons["reader.search"]
        XCTAssertTrue(toggleReaderChrome(until: search))
        XCTAssertTrue(waitUntil { search.isHittable && search.isEnabled })
        XCTAssertGreaterThanOrEqual(search.frame.width, 44)
        XCTAssertGreaterThanOrEqual(search.frame.height, 44)
        // The empty padding around the icon must open search, not reach the page.
        search.coordinate(withNormalizedOffset: CGVector(dx: 0.15, dy: 0.5)).tap()
        let input = app.textFields["reader.search.query"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        input.typeText("pond")
        let hits = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.'"))
        XCTAssertTrue(hits.firstMatch.waitForExistence(timeout: 30), "Results should appear without a return key.")
        XCTAssertTrue(waitUntil { hits.count >= 2 })
        hits.firstMatch.tap()
        let position = app.buttons["reader.search.cursor"]
        XCTAssertTrue(position.waitForExistence(timeout: 30))
        XCTAssertTrue(waitUntil { !input.exists && !self.app.keyboards.firstMatch.exists })
        let next = app.buttons["reader.search.next"]
        let previous = app.buttons["reader.search.previous"]
        XCTAssertTrue(waitUntil { next.isEnabled && next.isHittable })
        next.tap()
        XCTAssertTrue(waitUntil { previous.isEnabled && previous.isHittable && next.isHittable })
        previous.tap()
        XCTAssertTrue(waitUntil { next.isHittable && next.isEnabled })
        attachScreenshot("实时搜索-正文条目导航")

        let window = app.windows.firstMatch
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.78, dy: 0.55))
            .press(forDuration: 0.05, thenDragTo: window.coordinate(withNormalizedOffset: CGVector(dx: 0.25, dy: 0.55)))
        XCTAssertTrue(waitUntil { !position.exists })
        XCTAssertTrue(toggleReaderChrome(until: search))
        search.tap()
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        XCTAssertEqual(input.value as? String, "pond")
        XCTAssertTrue(hits.firstMatch.waitForExistence(timeout: 5))
    }

    func testReaderSearchExpandsInToolbarAndJumpsToText() throws {
        try openBook(containing: "Native UI EPUB")
        let search = app.buttons["reader.search"]
        XCTAssertTrue(search.waitForExistence(timeout: 40))
        let progress = app.sliders["reader.progress"]
        let initialPosition = progress.value as? String
        // The toolbar can appear while its presentation animation is still
        // running; adjusting before it accepts touches silently does nothing.
        XCTAssertTrue(waitUntil { progress.isHittable }, "The progress slider must accept a seek.")
        // A short drag can fall inside the thumb's touch slop and be ignored.
        // Pick the far half so the target is always a large move.
        let currentPercent = Double((initialPosition ?? "").replacingOccurrences(of: "%", with: "")) ?? 0
        progress.adjust(toNormalizedSliderPosition: currentPercent > 50 ? 0.2 : 0.8)
        XCTAssertTrue(waitUntil { progress.value as? String != initialPosition },
                      "Seek stayed at \(initialPosition ?? "nil").")
        let beforeSearch = progress.value as? String
        search.tap()
        let input = app.textFields["reader.search.query"]
        XCTAssertTrue(input.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertEqual(app.textFields.count, 1, "Reuse one input in the current reader toolbar.")
        let closeSearch = app.buttons["reader.search.close"]
        XCTAssertTrue(closeSearch.exists)
        XCTAssertFalse(search.exists)
        XCTAssertLessThan(input.frame.maxX, closeSearch.frame.minX)
        XCTAssertEqual(input.frame.midY, closeSearch.frame.midY, accuracy: 2)
        XCTAssertLessThan(input.frame.maxY, app.frame.height * 0.2)
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5))
        attachScreenshot("阅读搜索框向左展开")
        input.typeText("libzora-no-match-" + String(UUID().uuidString.prefix(8)) + "\n")
        XCTAssertTrue(app.staticTexts["没有找到相关文字"].waitForExistence(timeout: 30))
        XCTAssertFalse(app.keyboards.firstMatch.exists)
        XCTAssertFalse(app.buttons["reader.排版"].isHittable)
        replaceText(input, with: "田鼠")
        input.typeText("\n")
        let result = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.'")).firstMatch
        XCTAssertTrue(result.waitForExistence(timeout: 30))
        XCTAssertTrue(result.isHittable)
        XCTAssertGreaterThan(result.frame.minY, input.frame.maxY)
        XCTAssertLessThan(result.frame.maxY, app.frame.height * 0.55)
        XCTAssertEqual(app.textFields.count, 1)
        attachScreenshot("阅读搜索结果浮层")
        // Results are ordered by reading position and the first hit can sit on
        // the current page, where progress would not change. Jump across the
        // list so this asserts a real navigation.
        let results = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.search.hit.'"))
        let destination = results.allElementsBoundByIndex.last ?? result
        destination.tap()
        XCTAssertTrue(waitUntil { !input.exists && !self.app.keyboards.firstMatch.exists })
        XCTAssertTrue(app.buttons["reader.search.cursor"].waitForExistence(timeout: 10))
        attachScreenshot("搜索结果跳转正文")

        XCTAssertTrue(toggleReaderChrome(until: search))
        let jumpedPosition = progress.value as? String
        XCTAssertNotEqual(jumpedPosition, beforeSearch)
        search.tap()
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        XCTAssertEqual(input.value as? String, "田鼠")
        XCTAssertTrue(app.keyboards.firstMatch.waitForExistence(timeout: 5))
        app.buttons["reader.search.close"].tap()
        XCTAssertTrue(waitUntil { !input.exists && !self.app.keyboards.firstMatch.exists })
        XCTAssertEqual(progress.value as? String, jumpedPosition)
        XCTAssertTrue(app.buttons["reader.close"].isHittable)
    }

    func testReaderToolbarMatchesSystemTypographySheet() throws {
        try openBook(containing: "Native UI EPUB")
        let toolbar = app.otherElements["reader.toolbar"]
        XCTAssertTrue(toolbar.waitForExistence(timeout: 10))
        app.buttons["reader.排版"].tap()
        assertCompactTypographyPanel()
        let panel = app.otherElements["typography.panel"]
        let frame = panel.frame
        print("SYSTEM_TYPOGRAPHY_FRAME: \(frame)")
        attachScreenshot("阅读设置-恢复系统弹层")
        app.buttons["typography.done"].tap()
        XCTAssertTrue(toolbar.waitForExistence(timeout: 10))
        XCTAssertTrue(waitUntil { abs(toolbar.frame.minX - frame.minX) <= 1 && abs(toolbar.frame.maxX - frame.maxX) <= 1 })
        let topToolbar = app.otherElements["reader.topToolbar"]
        XCTAssertTrue(topToolbar.waitForExistence(timeout: 10))
        XCTAssertEqual(topToolbar.frame.minX, frame.minX, accuracy: 1)
        XCTAssertEqual(topToolbar.frame.maxX, frame.maxX, accuracy: 1)
        attachScreenshot("阅读工具栏-与系统弹层同宽")
    }

    func testReaderFontPickerChoosesSystemAndInstallsCatalogFont() throws {
        openNativeFixtureTextPage()
        let typography = app.buttons["reader.排版"]
        typography.tap()
        let fontRow = app.buttons["typography.fontFamily"]
        XCTAssertTrue(fontRow.waitForExistence(timeout: 5))
        XCTAssertTrue(fontRow.label.contains("京华老宋体"), "Default body font: \(fontRow.label)")
        attachScreenshot("阅读设置-正文字体入口")
        fontRow.tap()

        let done = app.buttons["fontPicker.done"]
        XCTAssertTrue(done.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(app.staticTexts["内置字体"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["系统字体"].waitForExistence(timeout: 10))
        let systemOption = app.buttons["fontPicker.option.system:PingFang SC"]
        XCTAssertTrue(systemOption.waitForExistence(timeout: 10), "系统字体 must be listed: \(app.debugDescription)")
        attachScreenshot("正文字体-内置与系统字体")

        systemOption.tap()
        XCTAssertTrue(waitUntil { !done.exists })
        XCTAssertTrue(waitUntil { fontRow.label.contains("苹方") }, "System font must be applied: \(fontRow.label)")
        attachScreenshot("正文字体-已选苹方")

        fontRow.tap()
        XCTAssertTrue(done.waitForExistence(timeout: 10))
        let catalogOption = app.buttons["fontPicker.option.catalog:zhuque-fangsong"]
        let list = app.descendants(matching: .any)["fontPicker.list"]
        for _ in 0..<10 {
            if catalogOption.exists && catalogOption.isHittable { break }
            if list.exists { list.swipeUp() } else { app.swipeUp() }
        }
        XCTAssertTrue(catalogOption.exists && catalogOption.isHittable,
                      "可下载字体 must be reachable: \(app.debugDescription)")
        XCTAssertTrue(app.staticTexts["可下载字体"].exists)
        attachScreenshot("正文字体-可下载字体")
        catalogOption.tap()
        XCTAssertTrue(waitUntil { !done.exists }, "Download must close the picker.")
        XCTAssertTrue(waitUntil { fontRow.label.contains("朱雀仿宋") }, "Downloaded font must be applied: \(fontRow.label)")

        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
        app.terminate()
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 30))
        openNativeFixtureTextPage()
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        typography.tap()
        XCTAssertTrue(fontRow.waitForExistence(timeout: 5))
        XCTAssertTrue(fontRow.label.contains("朱雀仿宋"),
                      "Downloaded body font must survive a restart: \(fontRow.label)")
        attachScreenshot("正文字体-重启后保持")

        fontRow.tap()
        XCTAssertTrue(done.waitForExistence(timeout: 10))
        let builtin = app.buttons["fontPicker.option.京华老宋体"]
        for _ in 0..<6 {
            if builtin.exists && builtin.isHittable { break }
            if list.exists { list.swipeDown() } else { app.swipeDown() }
        }
        XCTAssertTrue(builtin.exists && builtin.isHittable)
        builtin.tap()
        XCTAssertTrue(waitUntil { !done.exists })
        XCTAssertTrue(waitUntil { fontRow.label.contains("京华老宋体") })
        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
    }

    func testProseTypographyPreservesChoicesAndPageNavigation() throws {
        for (title, language) in [("Walden", "英文"), ("银河系漫游指南", "中文")] {
            try openBook(containing: title, revealControls: false)
            let typography = app.buttons["reader.排版"]
            let footer = app.segmentedControls["typography.progressDisplay"]
            let direction = app.segmentedControls["typography.direction"]
            let font = app.buttons["typography.fontFamily"]
            let scale = app.staticTexts["typography.fontScale"]
            let position = app.staticTexts["reader.position"]
            XCTAssertTrue(toggleReaderChrome(until: typography))
            typography.tap()
            reveal(footer)
            let originalFooter = footer.buttons.allElementsBoundByIndex.first(where: { $0.isSelected })?.label ?? "页码"
            footer.buttons["关"].tap()
            reveal(direction)
            let originalDirection = direction.buttons["竖排"].isSelected ? "竖排" : "横排"
            direction.buttons["横排"].tap()
            let originalFont = font.label
            let originalScale = scale.label
            app.buttons["typography.done"].tap()
            XCTAssertTrue(typography.waitForExistence(timeout: 10))
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
            XCTAssertTrue(waitUntil { !typography.exists && !position.exists })
            attachScreenshot(language == "英文" ? "英文正文-适度两端对齐" : "中文正文-舒适排版")

            if language == "英文" {
                XCTAssertTrue(toggleReaderChrome(until: typography))
                typography.tap()
                reveal(footer)
                footer.buttons["页码"].tap()
                app.buttons["typography.done"].tap()
                XCTAssertTrue(toggleReaderChrome(until: position))
                func pageNumber() -> Int {
                    Int(position.label.components(separatedBy: "/")[0].trimmingCharacters(in: .whitespaces)) ?? -1
                }
                let before = pageNumber()
                XCTAssertGreaterThan(before, 0)
                let window = app.windows.firstMatch
                window.coordinate(withNormalizedOffset: CGVector(dx: 0.92, dy: 0.5)).tap()
                XCTAssertTrue(waitUntil { pageNumber() == before + 1 }, "English prose must advance by a complete page.")
                attachScreenshot("英文正文-连续下一页")
                window.coordinate(withNormalizedOffset: CGVector(dx: 0.08, dy: 0.5)).tap()
                XCTAssertTrue(waitUntil { pageNumber() == before }, "Returning must keep the same reading location.")
            }

            XCTAssertTrue(toggleReaderChrome(until: typography))
            typography.tap()
            reveal(footer)
            XCTAssertEqual(font.label, originalFont, "Language-aware typography must not replace the chosen font.")
            XCTAssertEqual(scale.label, originalScale, "Language-aware typography must not reset the chosen size.")
            footer.buttons[originalFooter].tap()
            reveal(direction)
            direction.buttons[originalDirection].tap()
            app.buttons["typography.done"].tap()
            app.buttons["reader.close"].tap()
            let backToLibrary = app.buttons["book.back"]
            XCTAssertTrue(backToLibrary.waitForExistence(timeout: 10))
            XCTAssertTrue(waitUntil {
                backToLibrary.isHittable && !self.app.descendants(matching: .any)["reader.page"].exists
            })
            XCTAssertGreaterThanOrEqual(backToLibrary.frame.width, 44)
            XCTAssertGreaterThanOrEqual(backToLibrary.frame.height, 44)
            backToLibrary.coordinate(withNormalizedOffset: CGVector(dx: 0.15, dy: 0.5)).tap()
            // Returning keeps the shelf's scroll position; the header may be offscreen.
            let library = app.scrollViews["library.ready"]
            let returnedBook = app.buttons.matching(NSPredicate(
                format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", title)).firstMatch
            XCTAssertTrue(waitUntil { !backToLibrary.exists && library.exists && returnedBook.isHittable },
                          app.debugDescription)
        }
    }

    func testReaderProgressDisplayModesPersist() throws {
        try openBook(containing: "Walden", revealControls: false)
        let typography = app.buttons["reader.排版"]
        let picker = app.segmentedControls["typography.progressDisplay"]
        let position = app.staticTexts["reader.position"]
        let footer = app.otherElements["reader.footer"]
        XCTAssertTrue(toggleReaderChrome(until: typography))
        typography.tap()
        reveal(picker)
        let original = picker.buttons.allElementsBoundByIndex.first(where: { $0.isSelected })?.label ?? "页码"
        for mode in ["页码", "章节", "剩余时间", "关"] {
            XCTAssertTrue(picker.buttons[mode].isHittable)
            picker.buttons[mode].tap()
            XCTAssertTrue(picker.buttons[mode].isSelected)
            app.buttons["typography.done"].tap()
            XCTAssertTrue(typography.waitForExistence(timeout: 5))
            if mode == "关" {
                app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
                XCTAssertTrue(waitUntil { !typography.exists && !position.exists && !footer.exists },
                              "Off must remove the whole footer while leaving the page visible.")
            } else {
                XCTAssertTrue(toggleReaderChrome(until: position),
                              "Chrome must hide over the resumed page.\n\(app.debugDescription)")
                XCTAssertTrue(footer.exists)
                XCTAssertTrue(waitUntil {
                    let text = position.label
                    guard text.contains("%") else { return false }
                    switch mode {
                    case "章节": return text.hasPrefix("第 ") && text.contains(" 章")
                    // The footer learns the reader's own pace; until enough time
                    // has been recorded it must say so instead of guessing.
                    case "剩余时间":
                        return text.hasPrefix("剩余约 ") || text.hasPrefix("阅读速度估计中")
                    default: return text.range(of: "^[0-9]+ / [0-9]+", options: .regularExpression) != nil
                    }
                }, "Footer must apply \(mode) immediately: \(position.label)")
                XCTAssertGreaterThan(position.frame.minY, app.frame.maxY - 100)
            }
            attachScreenshot("底部进度-\(mode)")
            XCTAssertTrue(toggleReaderChrome(until: typography),
                          "Chrome must return after the footer check.\n\(app.debugDescription)")
            typography.tap()
            reveal(picker)
        }
        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
        app.terminate()
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 30))
        try openBook(containing: "Walden", revealControls: false)
        XCTAssertTrue(toggleReaderChrome(until: typography))
        typography.tap()
        reveal(picker)
        XCTAssertTrue(picker.buttons["关"].isSelected, "Off must survive an app restart.")
        picker.buttons[original].tap()
        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
    }

    func testPdfFooterCanBeTurnedOffAndRestored() throws {
        try openBook(containing: "Native UI PDF", revealControls: false)
        let typography = app.buttons["reader.显示"]
        let picker = app.segmentedControls["typography.progressDisplay"]
        let position = app.staticTexts["reader.position"]
        let footer = app.otherElements["reader.footer"]
        XCTAssertTrue(toggleReaderChrome(until: typography))
        typography.tap()
        reveal(picker)
        let original = picker.buttons.allElementsBoundByIndex.first(where: { $0.isSelected })?.label ?? "页码"
        picker.buttons["页码"].tap()
        app.buttons["typography.done"].tap()
        XCTAssertTrue(toggleReaderChrome(until: position))
        XCTAssertTrue(footer.exists)
        attachScreenshot("PDF底部进度-开启")
        XCTAssertTrue(toggleReaderChrome(until: typography))
        typography.tap()
        reveal(picker)
        picker.buttons["关"].tap()
        XCTAssertTrue(picker.buttons["关"].isSelected)
        attachScreenshot("底部进度-关闭选项")
        app.buttons["typography.done"].tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 5))
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !typography.exists && !position.exists && !footer.exists })
        attachScreenshot("PDF底部进度-关")
        XCTAssertTrue(toggleReaderChrome(until: typography))
        typography.tap()
        reveal(picker)
        picker.buttons[original].tap()
        app.buttons["typography.done"].tap()
        if original != "关" {
            XCTAssertTrue(toggleReaderChrome(until: position))
            XCTAssertTrue(footer.exists, "Re-enabling the footer must restore its information and reserved space.")
        }
        XCTAssertTrue(toggleReaderChrome(until: typography))
        app.buttons["reader.close"].tap()
    }

    func testReaderSettingsWidthAndVerticalReadingDirection() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        let progress = app.sliders["reader.progress"]
        progress.adjust(toNormalizedSliderPosition: 0.45)
        let toolbar = app.otherElements["reader.toolbar"]
        XCTAssertTrue(toolbar.waitForExistence(timeout: 10))
        typography.tap()
        assertCompactTypographyPanel()
        let panel = app.otherElements["typography.panel"]
        let panelFrame = panel.frame
        app.buttons["typography.done"].tap()
        XCTAssertTrue(toolbar.waitForExistence(timeout: 10))
        XCTAssertEqual(panelFrame.minX, toolbar.frame.minX, accuracy: 1)
        XCTAssertEqual(panelFrame.maxX, toolbar.frame.maxX, accuracy: 1)
        typography.tap()
        XCTAssertTrue(panel.waitForExistence(timeout: 10))
        for identifier in ["typography.lineHeight", "typography.paragraphSpacing"] {
            let control = app.segmentedControls[identifier]
            reveal(control)
            XCTAssertTrue(control.isHittable)
            let original = control.buttons.allElementsBoundByIndex.first(where: { $0.isSelected })?.label ?? "标准"
            for label in ["紧凑", "标准", "宽松", original] {
                control.buttons[label].tap()
                XCTAssertTrue(waitUntil { control.buttons[label].isSelected })
            }
        }
        let direction = app.segmentedControls["typography.direction"]
        reveal(direction)
        let wasVertical = direction.buttons["竖排"].isSelected
        direction.buttons["竖排"].tap()
        XCTAssertTrue(direction.buttons["竖排"].isSelected)
        attachScreenshot("阅读设置-与工具栏同宽")
        app.buttons["typography.done"].tap()
        let window = app.windows.firstMatch
        let center = window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        center.tap()
        let position = app.staticTexts["reader.position"]
        XCTAssertTrue(position.waitForExistence(timeout: 10))
        func pageNumber() -> Int {
            Int(position.label.components(separatedBy: "/")[0].trimmingCharacters(in: .whitespaces)) ?? -1
        }
        let initialPage = pageNumber()
        XCTAssertGreaterThan(initialPage, 0)
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.08, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { pageNumber() == initialPage + 1 }, "Vertical reading must advance on a left-side tap.")
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.92, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { pageNumber() == initialPage }, "A right-side tap must return to the previous page.")
        let left = window.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.45))
        let right = window.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.45))
        left.press(forDuration: 0.05, thenDragTo: right)
        XCTAssertTrue(waitUntil { pageNumber() == initialPage + 1 }, "A rightward swipe must advance in vertical reading.")
        right.press(forDuration: 0.05, thenDragTo: left)
        XCTAssertTrue(waitUntil { pageNumber() == initialPage })
        attachScreenshot("竖排-左点前进右滑前进")
        center.tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        typography.tap()
        reveal(direction)
        direction.buttons[wasVertical ? "竖排" : "横排"].tap()
        app.buttons["typography.done"].tap()
    }

    func testReaderNaturalPaginationNeighbouringPages() throws {
        try openBook(containing: "Native UI EPUB")
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        center.tap()
        let position = app.staticTexts["reader.position"]
        XCTAssertTrue(position.waitForExistence(timeout: 10))
        let original = position.label.components(separatedBy: "·")[0]
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.08, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { position.label.components(separatedBy: "·")[0] != original })
        attachScreenshot("连续分页-前一页")
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.92, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { position.label.components(separatedBy: "·")[0] == original })
        attachScreenshot("连续分页-后一页")
    }

    func testReaderParagraphSpacingThreeLevelsPersist() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        typography.tap()
        let spacing = app.segmentedControls["typography.paragraphSpacing"]
        reveal(spacing)
        XCTAssertTrue(spacing.isHittable)
        for title in ["紧凑", "标准", "宽松", "标准"] {
            let choice = spacing.buttons[title]
            choice.tap()
            XCTAssertTrue(choice.isSelected)
        }
        assertCompactTypographyPanel()
        attachScreenshot("阅读设置段间距三级调整")
        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.read"].tap()
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
        if !typography.exists {
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        }
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        typography.tap()
        reveal(spacing)
        XCTAssertTrue(spacing.buttons["标准"].isSelected)
        app.buttons["typography.done"].tap()
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.back"].waitForExistence(timeout: 10))
        app.buttons["book.back"].tap()
    }

    func testReaderSettingsScrollAndBlockUnderlyingButtons() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        attachScreenshot("阅读工具栏")
        typography.tap()
        XCTAssertTrue(app.segmentedControls["typography.paper"].waitForExistence(timeout: 10))
        assertCompactTypographyPanel()
        XCTAssertFalse(app.buttons["reader.bookmark"].isHittable)
        let paper = app.segmentedControls["typography.paper"]
        let newPaper = paper.buttons["黑"].isSelected ? "米" : "黑"
        paper.buttons[newPaper].tap()
        XCTAssertTrue(paper.buttons[newPaper].isSelected)
        let fontScale = app.staticTexts["typography.fontScale"]
        let initialScale = try XCTUnwrap(Int(fontScale.label.replacingOccurrences(of: "%", with: "")))
        let delta = initialScale >= 180 ? -10 : 10
        let expectedScale = min(180, max(80, initialScale + delta))
        let fontButton = app.buttons[delta > 0 ? "增大字号" : "减小字号"]
        fontButton.tap()
        XCTAssertTrue(waitUntil { fontScale.label == "\(expectedScale)%" },
                      "Font did not change: before=\(initialScale)% expected=\(expectedScale)% actual=\(fontScale.label); \(fontButton.debugDescription)")
        attachScreenshot("阅读设置")

        let indent = app.segmentedControls["typography.indent"]
        reveal(indent)
        XCTAssertTrue(indent.isHittable)
        let newIndent = indent.buttons["开"].isSelected ? "关" : "开"
        indent.buttons[newIndent].tap()
        XCTAssertTrue(indent.buttons[newIndent].isSelected, "Segmented control did not change: \(indent.debugDescription)")
        let vertical = app.segmentedControls["typography.direction"].buttons["竖排"]
        reveal(vertical)
        let newDirection = vertical.isSelected ? "横排" : "竖排"
        let direction = app.segmentedControls["typography.direction"].buttons[newDirection]
        direction.tap()
        XCTAssertTrue(direction.isSelected)
        attachScreenshot("阅读设置滚动")
        app.buttons["typography.done"].tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.read"].tap()
        XCTAssertTrue(app.buttons["reader.排版"].waitForExistence(timeout: 40))
        app.buttons["reader.排版"].tap()
        XCTAssertTrue(app.segmentedControls["typography.paper"].buttons[newPaper].isSelected)
        XCTAssertEqual(app.staticTexts["typography.fontScale"].label, "\(expectedScale)%")
        reveal(app.segmentedControls["typography.indent"])
        XCTAssertTrue(waitUntil { self.app.segmentedControls["typography.indent"].buttons[newIndent].isSelected })
        let savedDirection = app.segmentedControls["typography.direction"].buttons[newDirection]
        reveal(savedDirection)
        XCTAssertTrue(savedDirection.isSelected)
    }

    func testPdfUsesNativeDisplaySettings() throws {
        try openBook(containing: "Native UI PDF")
        XCTAssertTrue(app.buttons["reader.显示"].waitForExistence(timeout: 40))
        app.buttons["reader.显示"].tap()
        let zoom = app.segmentedControls.buttons["1.5 倍"]
        XCTAssertTrue(zoom.waitForExistence(timeout: 10))
        XCTAssertFalse(app.segmentedControls["typography.paragraphSpacing"].exists)
        assertCompactTypographyPanel()
        zoom.tap()
        XCTAssertTrue(zoom.isSelected)
        XCTAssertFalse(app.buttons["reader.bookmark"].isHittable)
        attachScreenshot("PDF 显示设置")
    }

    /// The host installs/launches the legacy frontend, then updates its binary
    /// with --preserve-data. This checks the real restored UIKit scene session.
    func testLegacyUpgradePreservesLibraryAndPreferences() throws {
        app.tabBars.buttons["我的"].tap()
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        XCTAssertEqual(theme.value as? String, "浅色")
        app.tabBars.buttons["书库"].tap()
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        typography.tap()
        XCTAssertTrue(app.segmentedControls["typography.paper"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.segmentedControls["typography.paper"].buttons["米"].isSelected)
        XCTAssertEqual(app.staticTexts["typography.fontScale"].label, "125%")
        reveal(app.segmentedControls["typography.indent"])
        XCTAssertTrue(app.segmentedControls["typography.indent"].buttons["开"].isSelected)
        attachScreenshot("覆盖安装后保留阅读设置")
    }

    func testReaderStartsWithTextAndShowsControlsOnTap() throws {
        try openBook(containing: "Native UI EPUB", revealControls: false)
        let position = app.staticTexts["reader.position"]
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(position.waitForExistence(timeout: 40))
        XCTAssertFalse(typography.exists)
        attachScreenshot("阅读器-默认正文")
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        center.tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        XCTAssertFalse(position.exists, "The controls should replace the bottom chapter and progress line.")
        attachScreenshot("阅读器-底部控制栏")
        center.tap()
        XCTAssertTrue(position.waitForExistence(timeout: 10))
        XCTAssertFalse(typography.exists)
        center.tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        app.buttons["book.read"].tap()
        XCTAssertTrue(position.waitForExistence(timeout: 40))
        XCTAssertFalse(typography.exists)
    }

    func testReaderSurfaceTapsAndSwipesPassThroughTheNativeChrome() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        center.tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        let position = app.staticTexts["reader.position"]
        let before = position.label
        guard !before.contains("1 / 1") else { throw XCTSkip("需要多页 EPUB 才能验证滑动翻页") }
        let start = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.8, dy: 0.45))
        let end = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.45))
        start.press(forDuration: 0.05, thenDragTo: end)
        XCTAssertTrue(waitUntil { position.label != before })
        center.tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
    }

    func testNativeConfirmationCanCancelAndReopen() {
        showLibrary(true)
        app.buttons["library.import"].tap()
        let name = "确认测试-" + String(UUID().uuidString.prefix(6))
        let input = app.textFields["name.input"]
        XCTAssertTrue(input.waitForExistence(timeout: 10))
        input.tap(); input.typeText(name)
        app.buttons["name.save"].tap()
        XCTAssertTrue(waitUntil { !input.exists })
        let folder = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'collection.' AND label CONTAINS %@", name)).firstMatch
        XCTAssertTrue(folder.waitForExistence(timeout: 10))
        reveal(folder); folder.tap()
        for _ in 0..<2 {
            let menu = app.buttons["收藏夹选项"]
            XCTAssertTrue(waitUntil { menu.isEnabled })
            menu.tap(); app.buttons["清空书籍"].tap()
            XCTAssertTrue(app.alerts["清空收藏夹"].waitForExistence(timeout: 10))
            XCTAssertFalse(app.buttons["添加书籍"].isHittable)
            app.alerts.buttons["取消"].tap()
            XCTAssertTrue(waitUntil { !self.app.alerts.firstMatch.exists })
        }
    }

    /// The host runner compares the CoreSimulator captures: the SwiftUI paper
    /// around the page must use the same colour as the page rendered by C#.
    func testReaderPaperColorSurroundsThePage() throws {
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        let wasColorful = colorful.value as? String == "1"
        if !wasColorful { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        for paper in ["米", "黑", "白"] {
            if !typography.exists { center.tap() }
            XCTAssertTrue(typography.waitForExistence(timeout: 10))
            typography.tap()
            let picker = app.segmentedControls["typography.paper"]
            XCTAssertTrue(picker.waitForExistence(timeout: 10))
            picker.buttons[paper].tap()
            XCTAssertTrue(picker.buttons[paper].isSelected)
            app.buttons["typography.done"].tap()
            XCTAssertTrue(waitUntil { !picker.exists })
            center.tap()
            XCTAssertTrue(waitUntil { !typography.exists })
            attachScreenshot("纸张-\(paper)")
        }
        center.tap()
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        typography.tap()
        let paperPicker = app.segmentedControls["typography.paper"]
        XCTAssertTrue(paperPicker.waitForExistence(timeout: 10))
        paperPicker.buttons["跟随"].tap()
        XCTAssertTrue(paperPicker.buttons["跟随"].isSelected)
        app.buttons["typography.done"].tap()
        XCTAssertTrue(waitUntil { !paperPicker.exists })
        center.tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        attachScreenshot("纸张跟随-正文与底部连续炫彩")
        center.tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.back"].waitForExistence(timeout: 10))
        app.buttons["book.back"].tap()
        if !wasColorful {
            openLibrarySettings()
            XCTAssertTrue(colorful.waitForExistence(timeout: 10))
            colorful.tap()
            XCTAssertTrue(waitUntil { colorful.value as? String == "0" })
            app.navigationBars.buttons.element(boundBy: 0).tap()
            app.tabBars.buttons["书库"].tap()
        }
    }

    /// Following the app appearance after the ink paper must return to the
    /// app's light, colourful paper instead of keeping the reader dark.
    func testReaderFollowAfterInkReturnsToLightPaper() throws {
        app.tabBars.buttons["我的"].tap()
        let theme = app.buttons["profile.theme"]
        XCTAssertTrue(theme.waitForExistence(timeout: 15))
        guard let wasTheme = theme.value as? String else {
            XCTFail("The current theme must be known before changing preferences.")
            return
        }
        selectTheme("浅色")
        openLibrarySettings()
        let colorful = app.switches["library.settings.colorful"]
        XCTAssertTrue(colorful.waitForExistence(timeout: 10))
        let wasColorful = colorful.value as? String == "1"
        if !wasColorful { colorful.tap() }
        XCTAssertTrue(waitUntil { colorful.value as? String == "1" })
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()

        try openBook(containing: "Native UI EPUB", revealControls: false)
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(toggleReaderChrome(until: typography), "The reader toolbar should appear after opening the fixture.")
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        typography.tap()
        let paper = app.segmentedControls["typography.paper"]
        XCTAssertTrue(paper.waitForExistence(timeout: 10))
        paper.buttons["黑"].tap()
        XCTAssertTrue(paper.buttons["黑"].isSelected)
        app.buttons["typography.done"].tap()
        XCTAssertTrue(waitUntil { !paper.exists })
        center.tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        attachScreenshot("纸张-黑-切回跟随前")

        if !typography.exists { center.tap() }
        XCTAssertTrue(typography.waitForExistence(timeout: 10))
        typography.tap()
        XCTAssertTrue(paper.waitForExistence(timeout: 10))
        paper.buttons["跟随"].tap()
        XCTAssertTrue(paper.buttons["跟随"].isSelected)
        app.buttons["typography.done"].tap()
        XCTAssertTrue(waitUntil { !paper.exists })
        center.tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        attachScreenshot("纸张跟随-从黑切回")

        center.tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.back"].waitForExistence(timeout: 10))
        app.buttons["book.back"].tap()
        app.tabBars.buttons["我的"].tap()
        XCTAssertTrue(theme.waitForExistence(timeout: 10))
        selectTheme(wasTheme)
        if !wasColorful {
            openLibrarySettings()
            XCTAssertTrue(colorful.waitForExistence(timeout: 10))
            colorful.tap()
            XCTAssertTrue(waitUntil { colorful.value as? String == "0" })
            app.navigationBars.buttons.element(boundBy: 0).tap()
            app.tabBars.buttons["书库"].tap()
        }
    }

    func testReaderSelectionToolbarUsesDesktopOrderAndStaysNearSelection() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        typography.tap()
        let direction = app.segmentedControls["typography.direction"]
        reveal(direction)
        let wasVertical = direction.buttons["竖排"].isSelected
        direction.buttons["横排"].tap()
        app.buttons["typography.done"].tap()
        app.sliders["reader.progress"].adjust(toNormalizedSliderPosition: 0.45)
        let center = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        center.tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        selectText()
        let bar = app.descendants(matching: .any).matching(identifier: "reader.selection.toolbar").firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 10))
        XCTAssertLessThan(bar.frame.height, 70)
        XCTAssertLessThan(bar.frame.maxY, app.frame.maxY - 80, "Selection actions must not be docked to the bottom.")
        var previousX: CGFloat = -.infinity
        for title in ["复制", "划线", "笔记", "搜索", "词典", "AI", "翻译", "朗读"] {
            let action = app.buttons["reader.\(title)"]
            XCTAssertTrue(action.exists && action.isHittable)
            XCTAssertGreaterThan(action.frame.midX, previousX)
            previousX = action.frame.midX
        }
        attachScreenshot("选中文字-桌面顺序与就近浮窗")
        app.buttons["reader.复制"].tap()
        XCTAssertTrue(waitUntil { !bar.exists })
        selectText()
        app.buttons["reader.搜索"].tap()
        let query = app.textFields["reader.search.query"]
        XCTAssertTrue(query.waitForExistence(timeout: 10))
        XCTAssertFalse((query.value as? String ?? "").isEmpty)
        attachScreenshot("选中文字-搜索所选文字")
        app.buttons["reader.search.close"].tap()
        if wasVertical {
            XCTAssertTrue(typography.waitForExistence(timeout: 10))
            typography.tap()
            reveal(direction)
            direction.buttons["竖排"].tap()
            app.buttons["typography.done"].tap()
        }
    }

    func testReaderLongPressSelectsWordAndShowsToolbarWithoutDrag() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let window = app.windows.firstMatch
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        let highlight = app.buttons["reader.划线"]
        var selected = false
        for _ in 0..<6 where !selected {
            // No drag: the long press itself must pick the character or word
            // under the finger and show the same floating toolbar.
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.16))
                .press(forDuration: 1.0)
            selected = highlight.waitForExistence(timeout: 5)
            if !selected { window.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5)).tap() }
        }
        XCTAssertTrue(selected, "Long press without dragging should select text.\n\(app.debugDescription)")
        let bar = app.descendants(matching: .any).matching(identifier: "reader.selection.toolbar").firstMatch
        XCTAssertTrue(bar.waitForExistence(timeout: 10))
        XCTAssertLessThan(bar.frame.height, 70)
        var previousX: CGFloat = -.infinity
        for title in ["复制", "划线", "笔记", "搜索", "词典", "AI", "翻译", "朗读"] {
            let action = app.buttons["reader.\(title)"]
            XCTAssertTrue(action.exists && action.isHittable)
            XCTAssertGreaterThan(action.frame.midX, previousX)
            previousX = action.frame.midX
        }
        attachScreenshot("长按直接选中一个词")
    }

    func testReaderNoteOpensFloatingEditorForDirectTyping() throws {
        try openBook(containing: "Native UI EPUB")
        selectText()
        let note = app.buttons["reader.笔记"]
        XCTAssertTrue(note.waitForExistence(timeout: 10))
        note.tap()
        let editor = app.descendants(matching: .any)["reader.note.editor"]
        XCTAssertTrue(editor.waitForExistence(timeout: 10), "The note action should open a floating editor.")
        XCTAssertLessThan(editor.frame.height, 400)
        XCTAssertLessThan(editor.frame.width, app.frame.width)
        XCTAssertEqual(editor.frame.minX, app.frame.minX + 8, accuracy: 1,
                       "The floating editor should use the reader chrome's 8pt side inset.")
        XCTAssertEqual(editor.frame.maxX, app.frame.maxX - 8, accuracy: 1,
                       "The floating editor should keep the same inset on both sides.")
        let field = app.descendants(matching: .any)["reader.note.text"]
        XCTAssertTrue(field.waitForExistence(timeout: 10))
        XCTAssertTrue(waitUntil { self.app.keyboards.firstMatch.exists },
                      "The floating editor should focus its text field for direct typing.")
        XCTAssertLessThanOrEqual(editor.frame.maxY, app.keyboards.firstMatch.frame.minY + 1,
                                 "The floating editor should stay above the keyboard.")
        field.tap()
        field.typeText("浮窗批注测试")
        attachScreenshot("批注悬浮窗直接输入")
        app.buttons["reader.note.save"].tap()
        XCTAssertTrue(waitUntil { !editor.exists }, "Saving should close the floating note editor.")
    }

    func testReaderDictionaryIncludesWikipediaLookup() throws {
        try openBook(containing: "Native UI EPUB")
        selectText()
        app.buttons["reader.词典"].tap()
        XCTAssertTrue(app.navigationBars["词典与维基百科"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.descendants(matching: .any)["reader.dictionary.wikipedia"].waitForExistence(timeout: 10))
        let result = app.descendants(matching: .any)["reader.dictionary.wikipedia.result"]
        let noResult = app.staticTexts.matching(
            NSPredicate(format: "label CONTAINS '没有找到同名维基百科条目'")).firstMatch
        XCTAssertTrue(waitUntil { result.exists || noResult.exists },
                      "Wikipedia lookup must resolve to an article or an explicit no-result state.")
        XCTAssertFalse(app.staticTexts.matching(
            NSPredicate(format: "label CONTAINS '维基百科查询失败'")).firstMatch.exists)
        let query = app.textFields["reader.dictionary.query"]
        XCTAssertTrue(query.exists && query.isHittable)
        app.buttons["reader.dictionary.clear"].tap()
        query.tap()
        query.typeText("Quantum entanglement")
        app.buttons["查询"].tap()
        XCTAssertTrue(result.waitForExistence(timeout: 15),
                      "An existing English article must render after Chinese lookup falls back.")
        XCTAssertTrue(app.buttons["在维基百科中打开"].exists)
        attachScreenshot("选中文字-本地词典与维基百科")
    }

    func testReaderTapOutsideSelectionDismissesItWithoutOpeningToolbar() throws {
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let window = app.windows.firstMatch
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        let highlight = app.buttons["reader.划线"]
        selectText()
        attachScreenshot("长按拖动选中正文")
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.62)).tap()
        let dismissed = waitUntil { !highlight.exists }
        attachScreenshot("点击正文取消选中")
        XCTAssertTrue(dismissed, "Tapping the page should dismiss the selection toolbar.")
        XCTAssertFalse(typography.exists, "The tap that dismisses a selection must not also open the reader toolbar.")
    }

    /// The materials tab was loaded before the reader added the highlight, and an
    /// EPUB highlight shows its date instead of a file path inside the book.
    func testNewHighlightAppearsInMaterialsWithReadableLocation() throws {
        app.tabBars.buttons["阅读资料"].tap()
        XCTAssertTrue(app.staticTexts["page.materials"].waitForExistence(timeout: 15))
        app.tabBars.buttons["书库"].tap()
        try openBook(containing: "Native UI EPUB")
        let typography = app.buttons["reader.排版"]
        XCTAssertTrue(typography.waitForExistence(timeout: 40))
        let window = app.windows.firstMatch
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !typography.exists })
        selectText()
        let highlight = app.buttons["reader.划线"]
        highlight.tap()
        XCTAssertTrue(waitUntil { !highlight.exists }, "Saving a highlight should close the selection toolbar.")
        attachScreenshot("添加划线")
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
        app.buttons["reader.close"].tap()
        XCTAssertTrue(app.buttons["book.back"].waitForExistence(timeout: 15))
        app.buttons["book.back"].tap()

        app.tabBars.buttons["阅读资料"].tap()
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        book.tap()
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyy/MM/dd"
        let today = "划线 · " + formatter.string(from: Date())
        let entries = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'materials.entry.'"))
        let added = entries.matching(NSPredicate(format: "label CONTAINS %@", today)).firstMatch
        XCTAssertTrue(added.waitForExistence(timeout: 10), "The new highlight should be listed.\n\(app.debugDescription)")
        attachScreenshot("阅读资料新划线")
        XCTAssertEqual(entries.matching(NSPredicate(format: "label CONTAINS[c] 'html'")).count, 0,
                       "Material locations must not show EPUB file paths.")
    }

    func testReaderTocGlobalExpandAndCollapse() throws {
        try openBook(containing: "Native UI EPUB")
        app.buttons["reader.排版"].tap()
        let paper = app.segmentedControls["typography.paper"]
        XCTAssertTrue(paper.waitForExistence(timeout: 10))
        paper.buttons["米"].tap()
        app.buttons["typography.done"].tap()
        XCTAssertTrue(waitUntil { !paper.exists })
        app.buttons["reader.目录"].tap()
        let navigation = app.navigationBars["Native UI EPUB"]
        XCTAssertTrue(navigation.waitForExistence(timeout: 10))
        let toggle = app.buttons["reader.toc.toggleAll"]
        guard toggle.waitForExistence(timeout: 5) else {
            throw XCTSkip("需要包含子章节的 EPUB 测试数据")
        }
        XCTAssertTrue(toggle.isHittable)
        XCTAssertGreaterThanOrEqual(toggle.frame.midY, navigation.frame.minY)
        XCTAssertLessThanOrEqual(toggle.frame.midY, navigation.frame.maxY)
        XCTAssertTrue(toggle.label.contains("收起"))
        toggle.tap()
        XCTAssertTrue(waitUntil { toggle.label.contains("展开") })
        attachScreenshot("目录纸张-米")
        toggle.tap()
        XCTAssertTrue(waitUntil { toggle.label.contains("收起") })
        attachScreenshot("目录-全部展开")
        app.buttons["完成"].tap()
        XCTAssertTrue(waitUntil { !navigation.exists })
        app.buttons["reader.排版"].tap()
        XCTAssertTrue(paper.waitForExistence(timeout: 10))
        paper.buttons["跟随"].tap()
        app.buttons["typography.done"].tap()
        XCTAssertTrue(waitUntil { !paper.exists })
    }

    /// Uses an isolated EPUB fixture with three nested navigation levels.
    func testReaderTocExpandsAndCollapsesSubchapters() throws {
        try openBook(containing: "Native UI EPUB")
        app.buttons["reader.目录"].tap()
        XCTAssertTrue(app.navigationBars["Native UI EPUB"].waitForExistence(timeout: 10))
        guard app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.toc.' AND label CONTAINS '第一部'")).firstMatch.exists else {
            throw XCTSkip("需要包含第一部、第一章和第一节的隔离 EPUB 测试数据")
        }
        let parent = app.buttons["reader.toc.toggle.0"]
        let child = app.buttons["reader.toc.1"]
        let childToggle = app.buttons["reader.toc.toggle.1"]
        let grandchild = app.buttons["reader.toc.2"]
        XCTAssertTrue(parent.waitForExistence(timeout: 10))
        XCTAssertTrue(child.exists && grandchild.exists)
        attachScreenshot("目录-展开子章节")
        childToggle.tap()
        XCTAssertTrue(waitUntil { !grandchild.exists })
        XCTAssertTrue(child.exists)
        parent.tap()
        XCTAssertTrue(waitUntil { !child.exists })
        parent.tap()
        XCTAssertTrue(child.waitForExistence(timeout: 10))
        XCTAssertFalse(grandchild.exists, "A child branch should retain its collapsed state.")
        childToggle.tap()
        XCTAssertTrue(grandchild.waitForExistence(timeout: 10))
        grandchild.tap()
        XCTAssertTrue(waitUntil { !self.app.navigationBars["Native UI EPUB"].exists })
    }

    func testReaderTocOpensAtTheCurrentChapter() throws {
        try openBook(containing: "Native UI EPUB")
        let toc = app.buttons["reader.目录"]
        XCTAssertTrue(toc.waitForExistence(timeout: 40))
        let progress = app.sliders["reader.progress"]
        let before = progress.value as? String
        let percent = Double((before ?? "0").replacingOccurrences(of: "%", with: "")) ?? 0
        // Choose a different part of the book so this test is repeatable with saved progress.
        progress.adjust(toNormalizedSliderPosition: percent >= 50 ? 0.15 : 0.95)
        XCTAssertTrue(waitUntil { progress.value as? String != before })
        toc.tap()
        XCTAssertTrue(app.navigationBars["Native UI EPUB"].waitForExistence(timeout: 10))
        attachScreenshot("目录打开位置")
        let current = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'reader.toc.' AND selected == true")).firstMatch
        XCTAssertTrue(current.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(waitUntil { current.isHittable }, "The table of contents should open with the current chapter visible.")
        current.tap()
        XCTAssertTrue(waitUntil { !self.app.navigationBars["Native UI EPUB"].exists })
    }

    private func openLibrarySettings() {
        app.tabBars.buttons["我的"].tap()
        let settings = app.buttons["profile.librarySettings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 10))
        settings.tap()
    }

    private func setGalleryMode() {
        openLibrarySettings()
        let gallery = app.switches["library.settings.gallery"]
        XCTAssertTrue(gallery.waitForExistence(timeout: 10))
        gallery.tap()
        app.navigationBars.buttons.element(boundBy: 0).tap()
        app.tabBars.buttons["书库"].tap()
        showLibrary()
    }

    /// A tap on a block figure presents the full-screen zoomable viewer, and
    /// closing it returns to the same page. The book opens on its cover, which
    /// is itself a block figure.
    func testReaderFigureOpensFullScreenViewer() throws {
        try openBook(containing: "Native UI EPUB", revealControls: false)
        let window = app.windows.firstMatch
        let close = app.buttons["reader.media.close"]
        let center = window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))

        // The first tap dismisses the initial chrome if it is still visible;
        // after that the cover figure opens the full-screen viewer.
        var opened = false
        for _ in 0..<3 where !opened {
            center.tap()
            opened = close.waitForExistence(timeout: 4)
        }
        XCTAssertTrue(opened, "Tapping an illustration must open the full-screen viewer.\n\(app.debugDescription)")
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label BEGINSWITH '图片'")).firstMatch.exists)
        attachScreenshot("图片查看器-打开")
        window.pinch(withScale: 2.0, velocity: 1.0)
        attachScreenshot("图片查看器-缩放")
        close.tap()
        XCTAssertTrue(waitUntil { !close.exists })
    }

    private func selectLibraryLayout(_ title: String) {
        let identifier = title == "列表视图" ? "books.list" : "books.grid"
        if app.descendants(matching: .any)[identifier].exists { return }
        let display = app.buttons["library.display"]
        XCTAssertTrue(display.waitForExistence(timeout: 15))
        display.tap()
        let choice = app.buttons[title]
        XCTAssertTrue(choice.waitForExistence(timeout: 5))
        choice.tap()
        XCTAssertTrue(app.descendants(matching: .any)[identifier].waitForExistence(timeout: 10))
    }

    private func assertEnlargedBookPreview(identifier: String, sourceFrame: CGRect,
                                           expectsCaptions: Bool = true, capture: String,
                                           assertCentered: Bool = true,
                                           sourceIsColumn: Bool = true, menuAbove: Bool? = nil) {
        let details = app.buttons["书籍详情"]
        XCTAssertTrue(details.waitForExistence(timeout: 5), app.debugDescription)
        attachScreenshot(capture)
        let previewIdentifier = "book.preview." + identifier.dropFirst("book.".count)
        let preview = app.descendants(matching: .any)[previewIdentifier].firstMatch
        if !preview.waitForExistence(timeout: 5) {
            print("BOOK_PREVIEW_TREE: \(app.debugDescription)")
        }
        XCTAssertTrue(preview.exists,
                      "The long press must display the app-owned book preview.\n\(app.debugDescription)")
        // The overlay draws only the tile itself, so its frame is the 1.1x
        // card. A list row is not a column, so compare its preview against
        // the card's minimum width instead.
        if sourceIsColumn {
            let growth = preview.frame.width / sourceFrame.width
            XCTAssertGreaterThan(growth, 1.04, "The preview must be visibly enlarged.")
            XCTAssertLessThan(growth, 1.2, "The preview must only grow a little.")
        } else {
            XCTAssertGreaterThan(preview.frame.width, 100, "The row preview must show an enlarged card.")
        }
        if assertCentered {
            // Scale about the source card's centre: no jumping to the screen centre.
            XCTAssertEqual(preview.frame.midX, sourceFrame.midX, accuracy: 2)
            XCTAssertEqual(preview.frame.midY, sourceFrame.midY, accuracy: 2)
        }
        if expectsCaptions {
            let title = app.staticTexts["book.preview.title"]
            let author = app.staticTexts["book.preview.author"]
            XCTAssertTrue(title.exists && author.exists,
                          "The enlarged title and author must be part of the preview.")
            if title.exists && author.exists {
                XCTAssertFalse(author.label.isEmpty)
                if sourceIsColumn {
                    XCTAssertGreaterThan(title.frame.minY, preview.frame.minY + preview.frame.height * 0.4)
                } else {
                    XCTAssertGreaterThanOrEqual(title.frame.minY, preview.frame.minY - 1)
                }
                XCTAssertGreaterThanOrEqual(author.frame.minY, title.frame.maxY - 1)
                XCTAssertLessThanOrEqual(author.frame.maxY, preview.frame.maxY + 1)
                print("BOOK_PREVIEW_GEOMETRY: card=\(preview.frame); title=\(title.frame); author=\(author.frame)")
            }
        }
        if let menuAbove {
            let menu = app.descendants(matching: .any)["book.preview.menu"].firstMatch
            XCTAssertTrue(menu.exists)
            let gap = menuAbove ? preview.frame.minY - menu.frame.maxY : menu.frame.minY - preview.frame.maxY
            XCTAssertEqual(gap, 6, accuracy: 2, "The menu should sit just outside the enlarged card.")
        }
        // The overlay lives above the tab bar rather than inside the scroll
        // view, so no part of the card is clipped by the bar. The bar must be
        // unreachable while the preview is up.
        XCTAssertGreaterThanOrEqual(preview.frame.minY, 0)
        XCTAssertLessThanOrEqual(preview.frame.maxY, app.frame.maxY)
        XCTAssertFalse(app.tabBars.buttons["我的"].isHittable,
                       "The modal preview must own interaction above the tab bar.")
    }

    private func showLibrary(_ collections: Bool = false, returnToList: Bool = false) {
        let title = collections ? "收藏夹" : "书库"
        if app.staticTexts[title].exists { return }
        app.buttons["library.display"].tap()
        let choice = app.buttons[collections ? "收藏夹视图" : returnToList ? "列表视图" : "网格视图"]
        XCTAssertTrue(choice.waitForExistence(timeout: 5))
        choice.tap()
        XCTAssertTrue(app.staticTexts[title].waitForExistence(timeout: 10))
    }

    private func openBookDetails(_ book: XCUIElement) {
        book.press(forDuration: 0.8)
        let details = app.buttons["书籍详情"]
        XCTAssertTrue(details.waitForExistence(timeout: 5))
        details.tap()
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
    }

    private func openBook(containing title: String, revealControls: Bool = true) throws {
        showLibrary()
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS %@", title)).firstMatch
        for _ in 0..<12 {
            if book.exists && book.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(book.exists && book.isHittable, "Expected a local test book containing \(title).\n\(app.debugDescription)")
        openBookDetails(book)
        XCTAssertTrue(app.buttons["book.read"].waitForExistence(timeout: 10))
        attachScreenshot("书籍详情")
        app.buttons["book.read"].tap()
        let page = app.descendants(matching: .any)["reader.page"]
        let loading = app.descendants(matching: .any)["reader.loading"]
        XCTAssertTrue(waitUntil(timeout: 40) { page.exists && !loading.exists },
                      "The reader must finish opening before interacting with its chrome.\n\(app.debugDescription)")
        if revealControls {
            XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
            let window = app.windows.firstMatch
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
            if app.buttons["reader.media.close"].exists {
                // A figure sits under the center of the resumed page; that tap
                // opened the full-screen viewer instead of the chrome.
                app.buttons["reader.media.close"].tap()
                window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.18)).tap()
            }
            XCTAssertTrue(waitUntil { self.app.buttons["reader.排版"].exists || self.app.buttons["reader.显示"].exists })
        }
    }

    /// Toggles the reader chrome until `element` shows up. A tap that lands on
    /// a block figure opens the full-screen image viewer instead of toggling
    /// the toolbar, so close the viewer and try the paper above the figure.
    @discardableResult
    private func toggleReaderChrome(until element: XCUIElement) -> Bool {
        let window = app.windows.firstMatch
        let close = app.buttons["reader.media.close"]
        for dy in [0.45, 0.18, 0.62, 0.30] {
            if element.exists { return true }
            if close.exists { close.tap() }
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: dy)).tap()
            if close.waitForExistence(timeout: 0.8) { close.tap() }
            if element.waitForExistence(timeout: 1.5) { return true }
        }
        return element.waitForExistence(timeout: 10)
    }

    /// The seeded fixture opens on its cover illustration, where a center tap
    /// opens the full-screen image viewer instead of the chrome. Reveal the
    /// toolbar from the paper above the figure and turn to the next chapter so
    /// the typography controls sit over body text.
    private func openNativeFixtureTextPage() {
        try? openBook(containing: "Native UI EPUB", revealControls: false)
        XCTAssertTrue(app.staticTexts["reader.position"].waitForExistence(timeout: 40))
        let window = app.windows.firstMatch
        let next = app.buttons["下一章或下一页"]
        let close = app.buttons["reader.media.close"]
        let typography = app.buttons["reader.排版"]
        var turned = false
        for _ in 0..<6 {
            if close.exists {
                close.tap()
                RunLoop.current.run(until: Date().addingTimeInterval(0.4))
            }
            if next.exists && next.isHittable {
                next.tap()
                turned = true
                break
            }
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.10)).tap()
            RunLoop.current.run(until: Date().addingTimeInterval(0.7))
        }
        if !turned, next.waitForExistence(timeout: 3) {
            next.tap()
        }
        for _ in 0..<4 where !(typography.exists && typography.isHittable) {
            window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.10)).tap()
            RunLoop.current.run(until: Date().addingTimeInterval(0.7))
        }
        XCTAssertTrue(typography.exists && typography.isHittable,
                      "Reader chrome must be visible over a text page.\n\(app.debugDescription)")
    }

    /// Long-presses and drags across the first text lines. Illustration pages
    /// have no text, so it turns pages until the selection toolbar appears.
    private func selectText() {
        let window = app.windows.firstMatch
        let highlight = app.buttons["reader.划线"]
        // Start on the first text line at the left margin, where every text page has glyphs.
        let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.1, dy: 0.13))
        let end = window.coordinate(withNormalizedOffset: CGVector(dx: 0.6, dy: 0.2))
        var selected = false
        for _ in 0..<6 where !selected {
            start.press(forDuration: 1.0, thenDragTo: end)
            selected = highlight.waitForExistence(timeout: 5)
            if !selected { window.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5)).tap() }
        }
        XCTAssertTrue(selected, "Long-press and drag should select text.\n\(app.debugDescription)")
    }

    private func attachScreenshot(_ name: String) {
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        // The host runner can capture through simctl when XCTest screenshots
        // are black on machines with nonstandard simulator graphics drivers.
        RunLoop.current.run(until: Date().addingTimeInterval(2))
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func tapAccountElement(_ element: XCUIElement) {
        XCTAssertTrue(waitUntil {
            let savePrompt = self.app.staticTexts.matching(NSPredicate(
                format: "label CONTAINS[c] '保存密码' OR label CONTAINS[c] 'Save Password'")).firstMatch
            if savePrompt.exists {
                let later = self.app.buttons.matching(NSPredicate(
                    format: "label IN {'以后', '稍后', '以后再说', 'Not Now'}")).firstMatch
                if later.exists && later.isHittable { later.tap() }
                return false
            }
            return element.exists && element.isEnabled && element.isHittable
        }, "Account control must be ready after any system password-saving prompt.")
        element.tap()
    }

    private func dismissKeyboard() {
        if app.toolbars.buttons["完成"].exists { app.toolbars.buttons["完成"].tap() }
        else if app.buttons["完成"].exists { app.buttons["完成"].firstMatch.tap() }
        XCTAssertTrue(waitUntil { !self.app.keyboards.firstMatch.exists })
    }

    private func replaceText(_ element: XCUIElement, with text: String) {
        element.tap()
        if let current = element.value as? String, current != element.placeholderValue, !current.isEmpty {
            element.coordinate(withNormalizedOffset: CGVector(dx: 0.95, dy: 0.5)).tap()
            element.typeText(String(repeating: XCUIKeyboardKey.delete.rawValue, count: current.count))
        }
        element.typeText(text)
    }

    private func reveal(_ element: XCUIElement) {
        // The account form ends with the region picker, so a signed-in page
        // needs more than a few swipes to reach its final rows.
        for _ in 0..<12 {
            if element.exists && element.isHittable && element.frame.maxY < app.frame.maxY - 72 { return }
            let typography = app.scrollViews["typography.scroll"]
            if typography.exists { typography.swipeUp() }
            else { app.swipeUp() }
        }
    }

    /// After changing the region at the bottom of the account form, return to
    /// the login controls at the top without relying on a fixed offset.
    private func scrollAccountToTop() {
        for _ in 0..<12 {
            let mode = app.segmentedControls["account.mode"]
            let username = app.staticTexts["account.username"]
            if (mode.exists && mode.isHittable) || (username.exists && username.isHittable) { return }
            app.swipeDown()
        }
    }

    private func assertCompactTypographyPanel() {
        let panel = app.otherElements["typography.panel"]
        XCTAssertTrue(panel.waitForExistence(timeout: 5))
        XCTAssertGreaterThan(panel.frame.minY, app.frame.minY + app.frame.height * 0.5,
                             "Reader settings must leave at least the upper half of the page visible: \(panel.frame)")
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

    private func waitUntil(timeout: TimeInterval = 10, _ predicate: @escaping () -> Bool) -> Bool {
        let expectation = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in predicate() }, object: nil)
        return XCTWaiter.wait(for: [expectation], timeout: timeout) == .completed
    }
}


/// Temporary probe for reviewing the monochrome widgets on the seeded home
/// screen pages. Removed once the visual pass is complete.
final class WidgetHomeScreenProbe: XCTestCase {
    func testProbeMonochromeWidgets() {
        XCUIDevice.shared.press(.home)
        let home = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        XCTAssertTrue(home.wait(for: .runningForeground, timeout: 15))
        RunLoop.current.run(until: Date().addingTimeInterval(2))
        for _ in 0..<6 {
            home.swipeRight()
            RunLoop.current.run(until: Date().addingTimeInterval(1))
        }
        let pageControl = home.pageIndicators["Page control"]
        for target in 3...4 {
            for _ in 0..<8 {
                if (pageControl.value as? String ?? "").contains("第\(target)页") { break }
                home.swipeLeft()
                RunLoop.current.run(until: Date().addingTimeInterval(1.5))
            }
            print("PROBE_PAGE \(pageControl.value ?? "nil")")
            RunLoop.current.run(until: Date().addingTimeInterval(2))
            print("LIBZORA_VISUAL_CHECKPOINT:黑白-第\(target)页")
            RunLoop.current.run(until: Date().addingTimeInterval(2))
        }
    }
}
