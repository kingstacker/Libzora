import Foundation

/// Cumulative growth stage of the reading tree, aligned with long-term
/// cumulative reading time.
enum KkTreeStage: Int, CaseIterable, Equatable {
    case seed = 0
    case sprout = 1
    case seedling = 2
    case sapling = 3
    case leafy = 4
    case lush = 5
    case ancient = 6

    /// Minimum cumulative reading seconds required to enter this stage.
    var minimumSeconds: Int64 {
        switch self {
        case .seed: return 0
        case .sprout: return 1
        case .seedling: return 3_600        // 1 hr
        case .sapling: return 18_000        // 5 hr
        case .leafy: return 72_000          // 20 hr
        case .lush: return 216_000          // 60 hr
        case .ancient: return 648_000       // 180 hr
        }
    }

    var next: KkTreeStage? {
        KkTreeStage(rawValue: rawValue + 1)
    }

    var chineseTitle: String {
        switch self {
        case .seed: return "埋下书种"
        case .sprout: return "破土萌芽"
        case .seedling: return "青枝幼苗"
        case .sapling: return "小树初成"
        case .leafy: return "亭亭如盖"
        case .lush: return "枝繁叶茂"
        case .ancient: return "参天古木"
        }
    }

    var englishTitle: String {
        switch self {
        case .seed: return "Seed"
        case .sprout: return "Sprout"
        case .seedling: return "Seedling"
        case .sapling: return "Sapling"
        case .leafy: return "Canopy"
        case .lush: return "Flourishing"
        case .ancient: return "Ancient Tree"
        }
    }

    /// Recursive branching depth for procedural trunk/branch generation.
    var branchDepth: Int {
        switch self {
        case .seed, .sprout: return 0
        case .seedling: return 2
        case .sapling: return 3
        case .leafy: return 4
        case .lush: return 4
        case .ancient: return 5
        }
    }

    static func stage(forTotalSeconds seconds: Int64) -> KkTreeStage {
        let clamped = max(0, seconds)
        var current: KkTreeStage = .seed
        for candidate in allCases where clamped >= candidate.minimumSeconds {
            current = candidate
        }
        return current
    }
}

/// Pure view-model derived from cumulative reading stats so both the widget
/// canvas and unit tests share exact growth calculations.
struct KkReadingTreeProfile: Equatable {
    static let maxRenderedFruits = 24
    static let maxRenderedBlossoms = 12

    let stage: KkTreeStage
    /// Continuous 0...1 progress within the current stage (1.0 when ancient).
    let stageProgress: Double
    /// Remaining seconds needed for the next stage, or nil at the highest stage.
    let remainingSecondsToNext: Int64?
    let totalSeconds: Int64
    let todaySeconds: Int64
    let streakDays: Int
    let booksFinished: Int
    let annotationCount: Int

    /// Visible fruits rendered on the tree, one per finished book up to the cap.
    let renderedFruits: Int
    /// Subtle blossom accents derived from notes and highlights.
    let renderedBlossoms: Int
    /// Continuous 0...1 foliage fullness used to modulate leaf density.
    let foliageDensity: Double

    var hasReadToday: Bool { todaySeconds > 0 }
    var isThriving: Bool { streakDays >= 3 }
    var nextStage: KkTreeStage? { stage.next }

    init(
        totalSeconds: Int64,
        todaySeconds: Int64,
        streakDays: Int,
        booksFinished: Int,
        annotationCount: Int
    ) {
        let safeTotal = max(0, totalSeconds)
        let safeToday = max(0, todaySeconds)
        let safeStreak = max(0, streakDays)
        let safeFinished = max(0, booksFinished)
        let safeAnnotations = max(0, annotationCount)

        let resolvedStage = KkTreeStage.stage(forTotalSeconds: safeTotal)
        self.stage = resolvedStage
        self.totalSeconds = safeTotal
        self.todaySeconds = safeToday
        self.streakDays = safeStreak
        self.booksFinished = safeFinished
        self.annotationCount = safeAnnotations

        if let next = resolvedStage.next {
            let floor = resolvedStage.minimumSeconds
            let span = max(1, next.minimumSeconds - floor)
            let gained = max(0, safeTotal - floor)
            self.stageProgress = min(1, max(0, Double(gained) / Double(span)))
            self.remainingSecondsToNext = max(0, next.minimumSeconds - safeTotal)
        } else {
            self.stageProgress = 1
            self.remainingSecondsToNext = nil
        }

        let stageFruitCap: Int
        switch resolvedStage {
        case .seed: stageFruitCap = 0
        case .sprout: stageFruitCap = 2
        case .seedling: stageFruitCap = 6
        case .sapling: stageFruitCap = 12
        case .leafy: stageFruitCap = 18
        case .lush, .ancient: stageFruitCap = Self.maxRenderedFruits
        }
        self.renderedFruits = min(safeFinished, stageFruitCap)

        let stageBlossomCap: Int
        switch resolvedStage {
        case .seed, .sprout: stageBlossomCap = 0
        case .seedling: stageBlossomCap = 4
        case .sapling: stageBlossomCap = 6
        case .leafy: stageBlossomCap = 9
        case .lush, .ancient: stageBlossomCap = Self.maxRenderedBlossoms
        }
        self.renderedBlossoms = min(safeAnnotations / 3, stageBlossomCap)

        if resolvedStage.rawValue <= KkTreeStage.sprout.rawValue {
            self.foliageDensity = 0
        } else {
            let base = Double(resolvedStage.rawValue - KkTreeStage.seedling.rawValue) / 4.0
            let step = resolvedStage == .ancient ? 0.0 : (0.25 * self.stageProgress)
            self.foliageDensity = min(1, max(0.22, 0.22 + (base + step) * 0.78))
        }
    }

    init(reading: KkReading) {
        self.init(
            totalSeconds: reading.totalSeconds,
            todaySeconds: reading.todaySeconds,
            streakDays: reading.streakDays,
            booksFinished: reading.booksFinished,
            annotationCount: reading.annotationCount)
    }

    func stageTitle(isChinese: Bool = KkFormat.isChinese) -> String {
        isChinese ? stage.chineseTitle : stage.englishTitle
    }

    func nextHint(isChinese: Bool = KkFormat.isChinese,
                  durationText: (Int64) -> String = KkFormat.duration) -> String {
        guard let next = nextStage, let remaining = remainingSecondsToNext else {
            return isChinese ? "长青不息" : "Evergreen"
        }
        let targetName = isChinese ? next.chineseTitle : next.englishTitle
        let formatted = durationText(remaining)
        return isChinese ? "距「\(targetName)」还差 \(formatted)" : "\(formatted) to \(targetName)"
    }
}
