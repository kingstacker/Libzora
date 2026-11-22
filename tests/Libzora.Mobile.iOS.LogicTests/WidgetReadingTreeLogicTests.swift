import CoreGraphics
import Foundation

@main
struct WidgetReadingTreeLogicTests {
    private static var assertions = 0

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        assertions += 1
        guard condition() else { fatalError("Regression failed: \(message)") }
    }

    static func main() throws {
        // Stage boundary thresholds.
        check(KkTreeStage.stage(forTotalSeconds: -100) == .seed, "Negative duration clamps to seed")
        check(KkTreeStage.stage(forTotalSeconds: 0) == .seed, "Zero seconds is seed")
        check(KkTreeStage.stage(forTotalSeconds: 1) == .sprout, "First second germinates into sprout")
        check(KkTreeStage.stage(forTotalSeconds: 3_599) == .sprout, "Just under 1 hour stays sprout")
        check(KkTreeStage.stage(forTotalSeconds: 3_600) == .seedling, "1 hour reaches seedling")
        check(KkTreeStage.stage(forTotalSeconds: 18_000) == .sapling, "5 hours reaches sapling")
        check(KkTreeStage.stage(forTotalSeconds: 72_000) == .leafy, "20 hours reaches leafy")
        check(KkTreeStage.stage(forTotalSeconds: 216_000) == .lush, "60 hours reaches lush")
        check(KkTreeStage.stage(forTotalSeconds: 648_000) == .ancient, "180 hours reaches ancient tree")
        check(KkTreeStage.stage(forTotalSeconds: 3_600_000) == .ancient, "Beyond 180 hours stays ancient tree")

        // Stage progress and remaining time.
        let seedProfile = KkReadingTreeProfile(
            totalSeconds: 0, todaySeconds: 0, streakDays: 0, booksFinished: 3, annotationCount: 15)
        check(seedProfile.stage == .seed, "Seed profile stage")
        check(seedProfile.stageProgress == 0, "Seed stage starts at 0 progress")
        check(seedProfile.remainingSecondsToNext == 1, "Seed needs 1 second to sprout")
        check(seedProfile.renderedFruits == 0, "Seed does not render fruits before germination")
        check(seedProfile.renderedBlossoms == 0, "Seed does not render blossoms")
        check(seedProfile.foliageDensity == 0, "Seed has zero canopy foliage")

        let halfwaySeedling = KkReadingTreeProfile(
            totalSeconds: 10_800, todaySeconds: 900, streakDays: 4, booksFinished: 2, annotationCount: 9)
        check(halfwaySeedling.stage == .seedling, "3 hours is halfway through seedling")
        check(abs(halfwaySeedling.stageProgress - 0.5) < 0.0001, "Seedling progress is 0.5 at 3 hours")
        check(halfwaySeedling.remainingSecondsToNext == 7_200, "2 hours remaining to sapling")
        check(halfwaySeedling.renderedFruits == 2, "Finished books render as fruits within seedling cap")
        check(halfwaySeedling.renderedBlossoms == 3, "Every 3 annotations add a blossom within cap")
        check(halfwaySeedling.hasReadToday && halfwaySeedling.isThriving, "Today and streak flags reflect activity")

        // Caps and monotonic density across stages.
        let ancientOverflow = KkReadingTreeProfile(
            totalSeconds: 900_000, todaySeconds: 0, streakDays: 0, booksFinished: 99, annotationCount: 300)
        check(ancientOverflow.stage == .ancient, "High cumulative hours reach ancient stage")
        check(ancientOverflow.stageProgress == 1, "Ancient stage locks stage progress at 1.0")
        check(ancientOverflow.remainingSecondsToNext == nil, "Ancient stage has no next stage")
        check(ancientOverflow.renderedFruits == KkReadingTreeProfile.maxRenderedFruits, "Fruits clamp to visual cap")
        check(ancientOverflow.renderedBlossoms == KkReadingTreeProfile.maxRenderedBlossoms, "Blossoms clamp to visual cap")
        check(ancientOverflow.foliageDensity == 1, "Ancient tree has full foliage density")
        check(!ancientOverflow.hasReadToday, "Zero today seconds marks not read today without shrinking stage")

        // Foliage density increases monotonically with total reading seconds from seedling onward.
        var previousDensity = -1.0
        for seconds in stride(from: Int64(3_600), through: 700_000, by: 18_000) {
            let profile = KkReadingTreeProfile(
                totalSeconds: seconds, todaySeconds: 0, streakDays: 0, booksFinished: 0, annotationCount: 0)
            check(profile.foliageDensity >= previousDensity - 0.0001,
                  "Foliage density never decreases as cumulative reading grows (\(seconds)s)")
            previousDensity = profile.foliageDensity
        }

        // Snapshot JSON decoding compatibility.
        let json = """
        {
          "continueReading": null,
          "continueBooks": [],
          "reading": {
            "todaySeconds": 1200,
            "streakDays": 5,
            "weekSeconds": 7200,
            "totalSeconds": 186480,
            "booksStarted": 12,
            "booksFinished": 6,
            "bookmarkCount": 14,
            "annotationCount": 27,
            "days": [{ "date": "2026-11-21", "seconds": 1200 }]
          }
        }
        """
        let decoded = try JSONDecoder().decode(KkSnapshot.self, from: Data(json.utf8))
        let fromSnapshot = KkReadingTreeProfile(reading: decoded.reading)
        check(fromSnapshot.stage == .leafy, "51.8h sample decodes into leafy stage")
        check(fromSnapshot.renderedFruits == 6 && fromSnapshot.renderedBlossoms == 9,
              "Snapshot reading metrics drive tree fruits and blossoms")

        // Widget gallery sample books must use blank covers and generic sample titles.
        let zhSample = KkSnapshot.sample(isChinese: true)
        let zhBooks = zhSample.continueBooks ?? []
        check(zhBooks.count == 4, "Chinese widget sample includes 4 books")
        check(zhSample.continueReading?.id == zhBooks.first?.id, "Primary sample book matches first continue book")
        check(zhBooks.allSatisfy { $0.cover == nil }, "Chinese sample books use blank covers")
        check(zhBooks.allSatisfy { $0.title.hasPrefix("示例书籍") && $0.authors == "示例作者" },
              "Chinese sample books use generic sample titles and author")

        let enSample = KkSnapshot.sample(isChinese: false)
        let enBooks = enSample.continueBooks ?? []
        check(enBooks.count == 4, "English widget sample includes 4 books")
        check(enBooks.allSatisfy { $0.cover == nil }, "English sample books use blank covers")
        check(enBooks.allSatisfy { $0.title.hasPrefix("Sample Book ") && $0.authors == "Sample Author" },
              "English sample books use generic sample titles and author")

        // Compact cumulative time formatting for tree and stats widgets.
        check(KkFormat.compactHours(0, isChinese: true) == "0 分钟", "Zero seconds formats as 0 分钟")
        check(KkFormat.compactHours(30, isChinese: true) == "<1 分钟", "Sub-minute formats compactly as <1 分钟")
        check(KkFormat.compactHours(2_460, isChinese: true) == "41 分钟", "Under 1 hour formats in minutes")
        check(KkFormat.compactHours(3_600, isChinese: true) == "1 小时", "Exact 1 hour drops trailing .0")
        check(KkFormat.compactHours(5_400, isChinese: true) == "1.5 小时", "Fractional hour keeps one decimal")
        check(KkFormat.compactHours(35_999, isChinese: true) == "10 小时", "9.999h rounds cleanly to 10 小时 without .0")
        check(KkFormat.compactHours(186_480, isChinese: true) == "52 小时", "51.8h rounds to 52 小时")
        check(KkFormat.compactHours(30, isChinese: false) == "<1 min", "English sub-minute formats as <1 min")
        check(KkFormat.compactHours(3_600, isChinese: false) == "1 hr", "English exact 1 hour drops trailing .0")
        check(KkFormat.compactHours(5_400, isChinese: false) == "1.5 hr", "English fractional hour keeps one decimal")

        // Snapshot interface font follows the host app preference and defaults safely on older payloads.
        let legacyJson = """
        {"reading":{"todaySeconds":0,"streakDays":0,"weekSeconds":0,"totalSeconds":0,"booksStarted":0,"booksFinished":0,"bookmarkCount":0,"annotationCount":0,"days":[]}}
        """
        let legacySnapshot = try JSONDecoder().decode(KkSnapshot.self, from: Data(legacyJson.utf8))
        check(legacySnapshot.uiFont == nil && !legacySnapshot.useSystemFont,
              "Legacy snapshots without uiFont keep the bundled KingHwaOldSong font")

        let bundledJson = """
        {"uiFont":0,"reading":{"todaySeconds":0,"streakDays":0,"weekSeconds":0,"totalSeconds":0,"booksStarted":0,"booksFinished":0,"bookmarkCount":0,"annotationCount":0,"days":[]}}
        """
        let bundledSnapshot = try JSONDecoder().decode(KkSnapshot.self, from: Data(bundledJson.utf8))
        check(!bundledSnapshot.useSystemFont, "uiFont=0 selects the bundled KingHwaOldSong font")

        let systemJson = """
        {"uiFont":1,"reading":{"todaySeconds":0,"streakDays":0,"weekSeconds":0,"totalSeconds":0,"booksStarted":0,"booksFinished":0,"bookmarkCount":0,"annotationCount":0,"days":[]}}
        """
        let systemSnapshot = try JSONDecoder().decode(KkSnapshot.self, from: Data(systemJson.utf8))
        check(systemSnapshot.useSystemFont, "uiFont=1 switches widget text to the system font")

        let invalidJson = """
        {"uiFont":99,"reading":{"todaySeconds":0,"streakDays":0,"weekSeconds":0,"totalSeconds":0,"booksStarted":0,"booksFinished":0,"bookmarkCount":0,"annotationCount":0,"days":[]}}
        """
        let invalidSnapshot = try JSONDecoder().decode(KkSnapshot.self, from: Data(invalidJson.utf8))
        check(!invalidSnapshot.useSystemFont, "Unknown uiFont values fall back to the bundled font")

        // Mature reading tree geometry: wide umbrella crown with drooping outer branches, staying within widget bounds.
        let canvasSizes = [CGSize(width: 127, height: 79), CGSize(width: 134, height: 129)]
        for canvasSize in canvasSizes {
            var previousCanopyWidth: CGFloat = 0
            for stage in [KkTreeStage.seedling, .sapling, .leafy, .lush, .ancient] {
                let profile = KkReadingTreeProfile(
                    totalSeconds: stage.minimumSeconds + 3_600,
                    todaySeconds: 1_200,
                    streakDays: 5,
                    booksFinished: 8,
                    annotationCount: 24)
                let skeleton = KkTreeSkeleton.generate(profile: profile, size: canvasSize)
                check(!skeleton.branches.isEmpty && !skeleton.canopyNodes.isEmpty,
                      "\(stage) generates branches and canopy nodes at \(canvasSize)")
                for branch in skeleton.branches {
                    check(branch.end.x >= 0 && branch.end.x <= canvasSize.width
                          && branch.end.y >= 0 && branch.end.y <= canvasSize.height,
                          "\(stage) branch stays inside canvas bounds \(canvasSize)")
                    let sunDist = hypot(branch.end.x - skeleton.sun.center.x, branch.end.y - skeleton.sun.center.y)
                    check(sunDist >= skeleton.sun.clearanceRadius - 0.5,
                          "\(stage) branch end clears sun disc (\(sunDist) vs \(skeleton.sun.clearanceRadius)) at \(canvasSize)")
                }
                for node in skeleton.canopyNodes {
                    let sunDist = hypot(node.point.x - skeleton.sun.center.x, node.point.y - skeleton.sun.center.y)
                    check(sunDist >= skeleton.sun.clearanceRadius - 0.5,
                          "\(stage) canopy node clears sun disc (\(sunDist) vs \(skeleton.sun.clearanceRadius)) at \(canvasSize)")
                }
                let bounds = skeleton.canopyBounds
                check(bounds.width >= previousCanopyWidth - 1.0,
                      "Canopy width grows across stages (\(stage) at \(canvasSize))")
                previousCanopyWidth = bounds.width

                if stage.rawValue >= KkTreeStage.leafy.rawValue {
                    check(skeleton.hasDroopingBranches,
                          "\(stage) includes downward-drooping outer branches at \(canvasSize)")
                    check(bounds.width >= skeleton.viewport.width * 0.56,
                          "\(stage) spreads wide across the tree viewport instead of shooting straight up (\(bounds.width) vs \(skeleton.viewport.width))")
                }
            }
        }

        // Small and Medium left tree share exact geometric proportions via uniform viewport scaling.
        let smallSize = CGSize(width: 127, height: 79)
        let mediumSize = CGSize(width: 134, height: 129)
        for stage in [KkTreeStage.seedling, .sapling, .leafy, .lush, .ancient] {
            let profile = KkReadingTreeProfile(
                totalSeconds: stage.minimumSeconds + 3_600,
                todaySeconds: 1_200,
                streakDays: 5,
                booksFinished: 8,
                annotationCount: 24)
            let smallSkeleton = KkTreeSkeleton.generate(profile: profile, size: smallSize)
            let mediumSkeleton = KkTreeSkeleton.generate(profile: profile, size: mediumSize)
            check(smallSkeleton.branches.count == mediumSkeleton.branches.count
                  && smallSkeleton.canopyNodes.count == mediumSkeleton.canopyNodes.count,
                  "\(stage) small and medium trees have identical branch and canopy structure")
            let smallRatio = smallSkeleton.canopyBounds.width / max(1, smallSkeleton.canopyBounds.height)
            let mediumRatio = mediumSkeleton.canopyBounds.width / max(1, mediumSkeleton.canopyBounds.height)
            check(abs(smallRatio - mediumRatio) < 0.01,
                  "\(stage) small and medium trees keep matching aspect ratio (\(smallRatio) vs \(mediumRatio))")
            let smallSunDx = (smallSkeleton.sun.center.x - smallSkeleton.viewport.minX) / smallSkeleton.viewport.width
            let mediumSunDx = (mediumSkeleton.sun.center.x - mediumSkeleton.viewport.minX) / mediumSkeleton.viewport.width
            check(abs(smallSunDx - mediumSunDx) < 0.001,
                  "\(stage) sun/moon placement matches across small and medium canvases")
        }

        // Night mode replaces the sun disc with a crescent moon vector path.
        check(KkTreeSkeleton.celestialKind(isDark: false) == .sun, "Light mode shows sun")
        check(KkTreeSkeleton.celestialKind(isDark: true) == .moon, "Dark mode switches sun to moon")
        let moonCenter = CGPoint(x: 100, y: 50)
        let moonPath = KkTreeSkeleton.crescentMoonCGPath(center: moonCenter, radius: 10)
        check(moonPath.contains(CGPoint(x: 94, y: 53)),
              "Crescent moon path fills the lower-left lit crescent belly")
        check(!moonPath.contains(CGPoint(x: 102, y: 47)),
              "Crescent moon path carves out the upper-right dark cutout")
        check(!moonPath.contains(moonCenter),
              "Crescent moon cutout leaves the geometric center open so it reads as a crescent, not a disc")

        // Flower-to-fruit progression ("花果同枝"): buds -> blossoms -> young green fruits -> ripening -> ripe / heirloom fruits.
        let lifecycleProfile = KkReadingTreeProfile(
            totalSeconds: KkTreeStage.lush.minimumSeconds + 3_600,
            todaySeconds: 1_800,
            streakDays: 6,
            booksFinished: 6,
            annotationCount: 15,
            activeBookProgresses: [12, 38, 64, 88])
        check(lifecycleProfile.accents.contains { $0.kind == .bud }, "Low-progress book creates flower bud")
        check(lifecycleProfile.accents.contains { $0.kind == .blossom }, "Mid-early progress book creates full blossom")
        check(lifecycleProfile.accents.contains { $0.kind == .youngFruit && $0.hasDriftingPetal },
              "Mid-late progress book sets young green fruit with drifting petal")
        check(lifecycleProfile.accents.contains { $0.kind == .ripeningFruit }, "Near-finish book creates ripening fruit")
        check(lifecycleProfile.accents.filter { $0.kind == .ripeFruit }.count == 5
              && lifecycleProfile.accents.filter { $0.kind == .heirloomFruit }.count == 1,
              "Finished books on mature lush tree produce ripe and heirloom fruits concurrently")

        print("Passed \(assertions) widget reading tree regression assertions.")
    }
}
