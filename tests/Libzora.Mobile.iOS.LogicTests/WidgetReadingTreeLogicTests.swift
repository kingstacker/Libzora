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

        // Continue-reading slots use the visible book count and leave room for
        // a shared heading, title and progress without oversized covers.
        for size in [CGSize(width: 292, height: 141), CGSize(width: 338, height: 158), CGSize(width: 360, height: 170)] {
            let four = KkContinueBooksLayout(size: size, bookCount: 4)
            let two = KkContinueBooksLayout(size: size, bookCount: 2)
            check(two.slotWidth > four.slotWidth, "Two books do not reserve two empty slots")
            for count in 1...6 {
                let layout = KkContinueBooksLayout(size: size, bookCount: count)
                check(layout.slotCount == min(count, 4), "Continue widget displays at most four slots")
                let rowWidth = layout.slotWidth * CGFloat(layout.slotCount)
                    + KkContinueBooksLayout.spacing * CGFloat(layout.slotCount - 1)
                check(rowWidth <= size.width - KkContinueBooksLayout.horizontalPadding * 2 + 0.001,
                      "Book row stays within its horizontal insets")
                check(layout.coverWidth <= layout.slotWidth && layout.coverWidth <= 72,
                      "Covers remain bounded rather than stretching with two books")
                check(layout.coverHeight + 60 + KkContinueBooksLayout.verticalPadding * 2 <= size.height,
                      "Cover leaves room for heading and captions")
            }
        }
        let emptyLayout = KkContinueBooksLayout(size: .zero, bookCount: 0)
        check(emptyLayout.slotCount == 1 && emptyLayout.slotWidth == 0 && emptyLayout.coverHeight == 0,
              "Zero-size and empty layouts do not divide by zero or produce negative frames")

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

        // Listening companion and seasonal/festival atmosphere resolution.
        check(lifecycleProfile.listeningBirdCount == 0, "Zero listening duration renders no songbirds")
        let listeningProfile = KkReadingTreeProfile(
            totalSeconds: KkTreeStage.leafy.minimumSeconds + 3_600,
            todaySeconds: 2_400,
            streakDays: 4,
            booksFinished: 2,
            annotationCount: 6,
            totalListeningSeconds: 40_000,
            todayListeningSeconds: 1_200,
            atmosphere: KkTreeAtmosphere(season: .autumn, festival: .none, enabled: true))
        check(listeningProfile.hasListenedToday, "Positive todayListeningSeconds marks active listening today")
        check(listeningProfile.listeningBirdCount == 2, "10h+ cumulative listening spawns two perched songbirds")

        let gregorian = Calendar(identifier: .gregorian)
        func gDate(_ year: Int, _ month: Int, _ day: Int) -> Date {
            gregorian.date(from: DateComponents(year: year, month: month, day: day, hour: 12))!
        }
        check(KkTreeSeason.resolve(for: gDate(2026, 4, 15)) == .spring, "April resolves to spring")
        check(KkTreeSeason.resolve(for: gDate(2026, 7, 15)) == .summer, "July resolves to summer")
        check(KkTreeSeason.resolve(for: gDate(2026, 10, 15)) == .autumn, "October resolves to autumn")
        check(KkTreeSeason.resolve(for: gDate(2026, 12, 25)) == .winter, "December resolves to winter")
        check(KkTreeFestival.resolve(for: gDate(2026, 12, 25)) == .christmas, "Dec 25 resolves to Christmas")
        check(KkTreeFestival.resolve(for: gDate(2026, 1, 1)) == .newYear, "Jan 1 resolves to New Year")
        // Chinese lunar + Gregorian holidays & major US holidays in 2026:
        // Spring Festival = Feb 17, Valentine's = Feb 14, Lantern = Mar 3, Qingming = Apr 5,
        // Dragon Boat = Jun 19, Independence Day = Jul 4, Qixi = Aug 19, Mid-Autumn = Sep 25,
        // National Day = Oct 1, Double Ninth = Oct 18, Halloween = Oct 31, Thanksgiving = Nov 26.
        check(KkTreeFestival.resolve(for: gDate(2026, 2, 14)) == .valentinesDay, "Feb 14 resolves to Valentine's Day")
        check(KkTreeFestival.resolve(for: gDate(2026, 2, 17)) == .springFestival, "Lunar 1/1 resolves to Spring Festival")
        check(KkTreeFestival.resolve(for: gDate(2026, 3, 3)) == .lanternFestival, "Lunar 1/15 resolves to Lantern Festival")
        check(KkTreeFestival.resolve(for: gDate(2026, 4, 5)) == .qingming, "Qingming solar term resolves correctly")
        check(KkTreeFestival.resolve(for: gDate(2026, 6, 19)) == .dragonBoat, "Lunar 5/5 resolves to Dragon Boat Festival")
        check(KkTreeFestival.resolve(for: gDate(2026, 7, 4)) == .independenceDay, "Jul 4 resolves to Independence Day")
        check(KkTreeFestival.resolve(for: gDate(2026, 8, 19)) == .qixi, "Lunar 7/7 resolves to Qixi Festival")
        check(KkTreeFestival.resolve(for: gDate(2026, 9, 25)) == .midAutumn, "Lunar 8/15 resolves to Mid-Autumn Festival")
        check(KkTreeFestival.resolve(for: gDate(2026, 10, 1)) == .nationalDay, "Oct 1 resolves to National Day")
        check(KkTreeFestival.resolve(for: gDate(2026, 10, 18)) == .doubleNinth, "Lunar 9/9 resolves to Double Ninth Festival")
        check(KkTreeFestival.resolve(for: gDate(2026, 10, 31)) == .halloween, "Oct 31 resolves to Halloween")
        check(KkTreeFestival.resolve(for: gDate(2026, 11, 26)) == .thanksgiving, "4th Thursday of Nov resolves to Thanksgiving")
        check(KkTreeAtmosphere.resolve(for: gDate(2026, 9, 25), enabled: true).showsFullMoon,
              "Mid-Autumn with seasonal decorations enabled shows full moon")
        check(!KkTreeAtmosphere.resolve(for: gDate(2026, 12, 25), enabled: false).enabled,
              "Disabling seasonal decorations suppresses seasonal and festival overlays")

        // Snapshot decoding stays backward-compatible when seasonalDecorations or listening fields are omitted.
        let legacyListeningSnapshotJson = """
        {
          "reading": {
            "todaySeconds": 600,
            "weekSeconds": 1800,
            "totalSeconds": 7200,
            "streakDays": 2,
            "booksStarted": 3,
            "booksFinished": 1,
            "bookmarkCount": 4,
            "annotationCount": 3,
            "days": [{"date":"2026-04-01","seconds":600}]
          },
          "continueBooks": []
        }
        """
        let legacyDecoded = try? JSONDecoder().decode(KkSnapshot.self, from: Data(legacyListeningSnapshotJson.utf8))
        check(legacyDecoded != nil, "Legacy widget snapshot JSON decodes without new listening keys")
        check(legacyDecoded?.reading.safeTotalListeningSeconds == 0, "Missing listening seconds defaults to 0")
        check(KkReadingTreeProfile(snapshot: legacyDecoded!, date: gDate(2026, 12, 25)).atmosphere.festival == .christmas,
              "Snapshot reading tree enables seasonal and festival atmosphere by default")

        // Weekly chart aligns Monday -> Sunday even when reference date is Tuesday.
        let tuesdayRef = gDate(2026, 11, 24)
        let mondayFirst = KkFormat.mondayFirstWeekDays(
            [
                KkDay(date: "2026-11-22", seconds: 900),
                KkDay(date: "2026-11-23", seconds: 1200),
                KkDay(date: "2026-11-24", seconds: 480)
            ],
            referenceDate: tuesdayRef
        )
        check(mondayFirst.count == 7, "Monday-first week always returns 7 days")
        check(mondayFirst.first?.date == "2026-11-23" && mondayFirst.first?.seconds == 1200,
              "Monday-first week starts on Monday with Monday reading time")
        check(mondayFirst[1].date == "2026-11-24" && mondayFirst[1].seconds == 480,
              "Monday-first week places Tuesday second")
        check(mondayFirst.last?.date == "2026-11-29" && mondayFirst.last?.seconds == 0,
              "Monday-first week ends on Sunday with 0 for future days")

        // Stale widget snapshots roll over cleanly across days, weeks, and years.
        let rolloverReading = KkReading(
            todaySeconds: 900,
            streakDays: 500,
            weekSeconds: 4500,
            totalSeconds: 5_400_000, // 1,500 hours
            booksStarted: 120,
            booksFinished: 85,
            bookmarkCount: 640,
            annotationCount: 1850,
            days: [
                KkDay(date: "2026-11-23", seconds: 1800, listeningSeconds: nil),
                KkDay(date: "2026-11-24", seconds: 900, listeningSeconds: nil)
            ],
            todayListeningSeconds: 0,
            weekListeningSeconds: 0,
            totalListeningSeconds: 0
        )
        let sameDaySnapshot = "2026-11-24T09:00:00Z"
        let nextDay = gDate(2026, 11, 25)
        let twoDaysLater = gDate(2026, 11, 26)
        let nextYear = gDate(2028, 3, 1)
        check(KkFormat.effectiveTodaySeconds(reading: rolloverReading, generatedAt: sameDaySnapshot, on: tuesdayRef) == 900,
              "Same-day widget shows todaySeconds")
        check(KkFormat.effectiveTodaySeconds(reading: rolloverReading, generatedAt: sameDaySnapshot, on: nextDay) == 0,
              "Next-day widget without app launch clears yesterday todaySeconds to 0")
        check(KkFormat.effectiveStreakDays(reading: rolloverReading, generatedAt: sameDaySnapshot, on: nextDay) == 500,
              "500-day multi-year streak remains active through the day after last reading")
        check(KkFormat.effectiveStreakDays(reading: rolloverReading, generatedAt: sameDaySnapshot, on: twoDaysLater) == 0,
              "Streak resets cleanly to 0 after missing a full day")
        check(KkFormat.effectiveWeekSeconds(reading: rolloverReading, generatedAt: sameDaySnapshot, on: nextYear) == 0,
              "Week total resets to 0 when snapshot is from a prior year")
        check(KkFormat.compactHoursParts(rolloverReading.totalSeconds, isChinese: true) == ("1500", "小时"),
              "Multi-year 1,500h reading total formats cleanly")
        check(KkFormat.compactCount(9999, isChinese: true) == "9999",
              "Counts below 10k stay exact")
        check(KkFormat.compactCount(12_000, isChinese: true) == "1.2万",
              "Large Chinese counts >= 10k compact cleanly with 万")
        check(KkFormat.compactCount(25_600, isChinese: false) == "25.6k",
              "Large English counts >= 10k compact cleanly with k")

        print("Passed \(assertions) widget reading tree regression assertions.")
    }
}
