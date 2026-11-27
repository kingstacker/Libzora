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
