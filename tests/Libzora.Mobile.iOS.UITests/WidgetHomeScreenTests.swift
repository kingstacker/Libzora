import XCTest

/// Select explicitly after installing a widget preview and placing the small /
/// medium widgets on page 3, and the medium tree / large stats on page 4.
/// These checks exercise WidgetKit's real home-screen renderer, not mock views.
final class WidgetHomeScreenTests: XCTestCase {
    func testWidgetsHaveReadableMetricLabels() throws {
        continueAfterFailure = false
        XCUIDevice.shared.press(.home)
        let home = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        XCTAssertTrue(home.wait(for: .runningForeground, timeout: 15))
        RunLoop.current.run(until: Date().addingTimeInterval(1))
        XCUIDevice.shared.press(.home)
        RunLoop.current.run(until: Date().addingTimeInterval(1))

        home.swipeLeft()
        home.swipeLeft()
        RunLoop.current.run(until: Date().addingTimeInterval(3))
        print("WIDGET_PAGE: \(home.pageIndicators.firstMatch.value ?? "unknown")")
        XCTAssertTrue(element(containing: "阅读之树", in: home).waitForExistence(timeout: 20), home.debugDescription)
        let hasContinueBooks = element(containing: "继续阅读", in: home).exists
        let hasEmptyShelf = element(containing: "书架还空着", in: home).exists
        XCTAssertTrue(hasContinueBooks || hasEmptyShelf, "Continue widget must show either books or its empty shelf state: \(home.debugDescription)")
        XCTAssertTrue(element(containing: "阅读数据", in: home).exists, home.debugDescription)
        XCTAssertTrue(element(containing: "连续", in: home).exists, "Streak must have a readable label rather than only a dot and days")
        XCTAssertTrue(element(containing: "累计", in: home).exists, "Tree duration must explain that it is cumulative")
        capture("小组件-小号与中号")

        home.swipeLeft()
        RunLoop.current.run(until: Date().addingTimeInterval(3))
        XCTAssertTrue(element(containing: "阅读之树", in: home).waitForExistence(timeout: 15), home.debugDescription)
        XCTAssertTrue(element(containing: "阅读数据", in: home).exists, home.debugDescription)
        XCTAssertTrue(element(containing: "累计阅读", in: home).exists, home.debugDescription)
        capture("小组件-中号树与大号数据")
    }

    /// Requires a populated preview library and its real medium Continue widget.
    /// Pair CoreSimulator video with the checkpoints: iOS 26 does not expose
    /// the system status bar in the app's accessibility tree.
    func testMediumContinueWidgetOpensBooks() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora")
        let home = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        app.terminate()
        showWidgetPage(home)
        let books = home.buttons.matching(NSPredicate(format: "label CONTAINS '已读'"))
            .allElementsBoundByIndex.filter { $0.isHittable && $0.frame.width < 120 }
        XCTAssertGreaterThanOrEqual(books.count, 2, "The real 4×2 widget needs multiple covered books")
        let titles = books.suffix(2).map { $0.label.components(separatedBy: ", 已读")[0] }
        XCTAssertEqual(Set(titles).count, 2)
        capture("小组件换书-4×2桌面")
        for (index, title) in [titles[0], titles[1], titles[0]].enumerated() {
            if index > 0 { showWidgetPage(home) }
            let book = home.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", title)).firstMatch
            XCTAssertTrue(book.waitForExistence(timeout: 20) && book.isHittable, home.debugDescription)
            capture("小组件换书-点击前-\(index)")
            book.tap()
            XCTAssertTrue(app.wait(for: .runningForeground, timeout: 20))
            let page = app.descendants(matching: .any)["reader.page"]
            let loading = app.descendants(matching: .any)["reader.loading"]
            XCTAssertTrue(waitUntil(timeout: 50) { app.staticTexts["reader.position"].exists && !loading.exists }, app.debugDescription)
            capture("小组件换书-正文已就绪-\(index)")
            app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
            XCTAssertTrue(app.buttons["reader.close"].waitForExistence(timeout: 10))
            XCTAssertTrue(app.staticTexts[title].exists, "A different widget cover must open its own book")
            capture("小组件换书-工具栏可显示状态栏-\(index)")
            for _ in 0..<3 {
                app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
                if waitUntil(timeout: 2, { !app.buttons["reader.close"].exists }) { break }
            }
            XCTAssertFalse(app.buttons["reader.close"].exists)
        }
        app.terminate()
    }

    func testContinuousScrollHasClearPageEdges() throws {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.buttons["library.display"].waitForExistence(timeout: 40))
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Native UI EPUB'")).firstMatch
        for _ in 0..<15 {
            if book.exists && book.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(book.exists && book.isHittable, app.debugDescription)
        book.tap()
        let page = app.descendants(matching: .any)["reader.page"]
        let loading = app.descendants(matching: .any)["reader.loading"]
        XCTAssertTrue(waitUntil(timeout: 50) { app.staticTexts["reader.position"].exists && !loading.exists })
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(app.buttons["reader.排版"].waitForExistence(timeout: 10))
        app.buttons["reader.排版"].tap()
        let flow = app.segmentedControls["typography.flowMode"]
        for _ in 0..<10 {
            if flow.exists && flow.isHittable { break }
            app.scrollViews["typography.scroll"].swipeUp()
        }
        XCTAssertTrue(flow.exists && flow.isHittable)
        let originalMode = flow.buttons["翻页"].isSelected ? "翻页" : "滚动"
        flow.buttons["滚动"].tap()
        XCTAssertTrue(flow.buttons["滚动"].isSelected)
        app.buttons["typography.done"].tap()
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(waitUntil { !app.buttons["reader.close"].exists })
        capture("正文滚动-上下无渐隐遮罩")
        let start = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.6, dy: 0.76))
        let end = app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.6, dy: 0.26))
        start.press(forDuration: 0.05, thenDragTo: end)
        XCTAssertTrue(app.staticTexts["reader.position"].exists && !loading.exists)
        capture("正文滚动-滑动后上下文字清晰")
        start.press(forDuration: 0.05, thenDragTo: end)
        capture("正文滚动-再次滑动后无模糊")
        app.windows.firstMatch.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).tap()
        XCTAssertTrue(app.buttons["reader.排版"].waitForExistence(timeout: 10))
        app.buttons["reader.排版"].tap()
        for _ in 0..<10 {
            if flow.exists && flow.isHittable { break }
            app.scrollViews["typography.scroll"].swipeUp()
        }
        flow.buttons[originalMode].tap()
        XCTAssertTrue(flow.buttons[originalMode].isSelected)
        app.buttons["typography.done"].tap()
        app.terminate()
    }

    /// Only the synthetic short chapters from prepare-reader-scroll-preview.py.
    func testContinuousScrollTracksFingerAndCrossesChaptersBothWays() {
        continueAfterFailure = false
        let app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.readerscrollcheck")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS '手指位移'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 40))
        capture("连续滚动-三本真实封面测试书")
        book.tap()
        let position = app.staticTexts["reader.position"]
        XCTAssertTrue(position.waitForExistence(timeout: 40))
        XCTAssertTrue(app.staticTexts["第一章"].waitForExistence(timeout: 15))
        func percent() -> Double {
            let value = position.label.components(separatedBy: "·").last ?? ""
            return Double(value.replacingOccurrences(of: "%", with: "").trimmingCharacters(in: .whitespaces)) ?? -1
        }
        XCTAssertEqual(percent(), 0, "The top of a scrolling book must not start at one-page-chapter 50%")
        let window = app.windows.firstMatch
        let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.65, dy: 0.7))
        start.press(forDuration: 0.05, thenDragTo: start.withOffset(CGVector(dx: 0, dy: -50)),
                    withVelocity: .slow, thenHoldForDuration: 0.2)
        capture("连续滚动-短拖50点跟手且无上下遮罩")
        let shortProgress = percent()
        XCTAssertGreaterThan(shortProgress, 0, "Even movement within a single page must update the continuous percentage")
        start.press(forDuration: 0.05, thenDragTo: start.withOffset(CGVector(dx: 0, dy: -170)),
                    withVelocity: .slow, thenHoldForDuration: 0.2)
        capture("连续滚动-长拖170点")
        let longProgress = percent()
        XCTAssertGreaterThan(longProgress, shortProgress, "Longer finger displacement must advance the footer percentage")
        for _ in 0..<6 {
            if app.staticTexts["第二章"].exists { break }
            start.press(forDuration: 0.05,
                        thenDragTo: window.coordinate(withNormalizedOffset: CGVector(dx: 0.65, dy: 0.22)),
                        withVelocity: .fast, thenHoldForDuration: 0)
            RunLoop.current.run(until: Date().addingTimeInterval(0.4))
        }
        XCTAssertTrue(app.staticTexts["第二章"].waitForExistence(timeout: 15), "Upward finger flicks continuously enter chapter two")
        capture("连续滚动-快滑连续进入第二章")
        let secondChapterProgress = percent()
        XCTAssertGreaterThan(secondChapterProgress, longProgress)
        XCTAssertLessThanOrEqual(secondChapterProgress, 100)
        let reverse = window.coordinate(withNormalizedOffset: CGVector(dx: 0.65, dy: 0.25))
        for _ in 0..<6 {
            if app.staticTexts["第一章"].exists { break }
            reverse.press(forDuration: 0.05,
                          thenDragTo: window.coordinate(withNormalizedOffset: CGVector(dx: 0.65, dy: 0.74)),
                          withVelocity: .fast, thenHoldForDuration: 0)
            RunLoop.current.run(until: Date().addingTimeInterval(0.4))
        }
        XCTAssertTrue(app.staticTexts["第一章"].waitForExistence(timeout: 15), "Downward finger flicks continuously return to chapter one")
        capture("连续滚动-反向快滑连续返回第一章")
        XCTAssertLessThan(percent(), secondChapterProgress, "Reverse scrolling must decrease the same continuous percentage")
        app.terminate()
    }

    private func showWidgetPage(_ home: XCUIApplication) {
        XCUIDevice.shared.press(.home)
        XCTAssertTrue(home.wait(for: .runningForeground, timeout: 15))
        XCUIDevice.shared.press(.home)
        for _ in 0..<7 {
            let widget = home.icons.matching(NSPredicate(format: "label == 'Libzora'"))
                .allElementsBoundByIndex.first { $0.frame.width > home.windows.firstMatch.frame.width * 0.7 }
            if let widget, widget.isHittable {
                XCTAssertTrue(waitUntil(timeout: 20) {
                    home.buttons.matching(NSPredicate(format: "label CONTAINS '已读'"))
                        .allElementsBoundByIndex.filter { $0.isHittable && $0.frame.width < 120 }.count >= 2
                }, home.debugDescription)
                return
            }
            home.swipeLeft()
        }
        XCTFail("Install the main preview app's medium Continue widget on its own page before selecting this suite: \(home.debugDescription)")
    }

    private func waitUntil(timeout: TimeInterval = 15, _ condition: () -> Bool) -> Bool {
        let end = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.05))
        } while Date() < end
        return condition()
    }

    private func element(containing text: String, in home: XCUIApplication) -> XCUIElement {
        home.descendants(matching: .any).matching(NSPredicate(format: "label CONTAINS %@", text)).firstMatch
    }

    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        print("LIBZORA_VISUAL_CHECKPOINT:\(name)")
        RunLoop.current.run(until: Date().addingTimeInterval(2))
    }
}
