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

        print("Passed \(assertions) widget reading tree regression assertions.")
    }
}
