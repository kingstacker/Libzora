import XCTest

/// Uses prepare-reader-media-preview.py, never the user's populated preview.
final class ReaderMediaTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        XCUIDevice.shared.orientation = .portrait
        app = XCUIApplication(bundleIdentifier: "com.kingstacker.libzora.readermediapreview")
        app.launchArguments = ["--libzora-disable-restore"]
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["书库"].waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertTrue(app.scrollViews["library.ready"].waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertFalse(app.staticTexts["书库还是空的"].exists)
        XCTAssertFalse(app.otherElements["launch.screen"].exists)
    }

    override func tearDownWithError() throws {
        XCUIDevice.shared.orientation = .portrait
        app.terminate()
    }

    func testColdLaunchRestoresReaderWithoutBranding() {
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Reader Media Fixture'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15))
        book.tap()
        let position = app.staticTexts["reader.position"]
        XCTAssertTrue(position.waitForExistence(timeout: 40), app.debugDescription)
        let savedPosition = position.label
        checkpoint("退出前的阅读页")

        app.terminate()
        app.launchArguments = []
        app.launch()
        XCTAssertFalse(app.staticTexts["launch.slogan"].exists)
        XCTAssertTrue(position.waitForExistence(timeout: 40), app.debugDescription)
        XCTAssertEqual(position.label, savedPosition)
        XCTAssertFalse(app.otherElements["launch.screen"].exists)
        XCTAssertFalse(app.staticTexts["launch.brand"].exists)
        XCTAssertFalse(app.tabBars.buttons["书库"].isHittable)
        checkpoint("直接恢复阅读及原页码")
    }

    func testDragDoesNotOpenImageAndViewerRotates() throws {
        let book = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'book.' AND label CONTAINS 'Reader Media Fixture'")).firstMatch
        XCTAssertTrue(book.waitForExistence(timeout: 15), app.debugDescription)
        checkpoint("预加载完成的书库")
        book.press(forDuration: 0.8)
        let details = app.buttons["书籍详情"]
        XCTAssertTrue(details.waitForExistence(timeout: 5), app.debugDescription)
        details.tap()
        let read = app.buttons["book.read"]
        XCTAssertTrue(read.waitForExistence(timeout: 10))
        read.tap()
        let position = app.staticTexts["reader.position"]
        XCTAssertTrue(position.waitForExistence(timeout: 40), app.debugDescription)
        let initialPosition = position.label
        let window = app.windows.firstMatch
        let close = app.buttons["reader.media.close"]
        let centre = window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45))
        let lower = window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.60))

        // A real drag starts and ends inside the figure. Its release must not
        // become a tap, including after the host has captured the pointer.
        centre.press(forDuration: 0.05, thenDragTo: lower)
        XCTAssertFalse(close.waitForExistence(timeout: 2), "Dragging an illustration opened the viewer")
        XCTAssertEqual(position.label, initialPosition)
        checkpoint("图片上滑动不展开")

        // A single tap over a figure stays an ordinary tap (for example when
        // turning manga pages or toggling controls); double-tapping opens it.
        centre.tap()
        XCTAssertFalse(close.waitForExistence(timeout: 1.5), "A single tap on a figure must not open the viewer")
        centre.doubleTap()
        XCTAssertTrue(close.waitForExistence(timeout: 8), app.debugDescription)
        let viewport = app.scrollViews["reader.media.viewport"]
        XCTAssertTrue(viewport.waitForExistence(timeout: 5), app.debugDescription)
        checkpoint("双击展开图片")
        viewport.pinch(withScale: 2, velocity: 1)
        checkpoint("图片双指缩放")

        XCUIDevice.shared.orientation = .landscapeLeft
        XCTAssertTrue(waitUntil {
            viewport.frame.width > viewport.frame.height && window.frame.width > window.frame.height
        }, "The media viewport and actual window must both rotate to landscape.\n\(app.debugDescription)")
        checkpoint("图片横屏显示")
        close.tap()
        XCTAssertTrue(waitUntil { !close.exists && window.frame.height > window.frame.width },
                      "Closing media while the device stays landscape must restore portrait")
        XCTAssertTrue(position.waitForExistence(timeout: 10))
        XCTAssertEqual(position.label, initialPosition, "Inspecting an image must preserve the reader position")
        checkpoint("关闭图片恢复竖屏及原页码")

        // The device remains sideways: another viewer must acquire its own
        // rotation scope, and dismissing it must clean that scope up again.
        window.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)).doubleTap()
        XCTAssertTrue(close.waitForExistence(timeout: 8))
        XCTAssertTrue(waitUntil { viewport.frame.width > viewport.frame.height })
        close.tap()
        XCTAssertTrue(waitUntil { !close.exists && window.frame.height > window.frame.width })
        XCUIDevice.shared.orientation = .portrait

        let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.66, dy: 0.45))
        let end = window.coordinate(withNormalizedOffset: CGVector(dx: 0.34, dy: 0.45))
        start.press(forDuration: 0.05, thenDragTo: end)
        XCTAssertFalse(close.waitForExistence(timeout: 2), "A page swipe must not open the figure")
        checkpoint("横向翻页不误开图片")
    }

    private func waitUntil(timeout: TimeInterval = 10, _ condition: () -> Bool) -> Bool {
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
