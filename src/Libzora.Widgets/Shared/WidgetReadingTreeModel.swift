import CoreGraphics
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

/// Botanical stage of a single flower or fruit accent on the reading tree,
/// evolving from bud -> blossom -> young green fruit -> ripening fruit -> ripe fruit.
enum KkTreeAccentKind: Int, CaseIterable, Equatable {
    case bud = 0
    case blossom = 1
    case youngFruit = 2
    case ripeningFruit = 3
    case ripeFruit = 4
    case heirloomFruit = 5

    var isBlossomLike: Bool {
        self == .bud || self == .blossom
    }

    var isFruitLike: Bool {
        !isBlossomLike
    }
}

/// One flower or fruit ornament anchored on the canopy.
struct KkTreeAccent: Equatable {
    let kind: KkTreeAccentKind
    /// 0...1 fine maturity progress within the kind.
    let maturity: Double
    /// Marks a freshly formed young fruit accompanied by drifting fallen petals.
    let hasDriftingPetal: Bool
}

/// Seasonal palette mode for the Reading Tree widget, rendered purely with
/// vector colors and shapes (no image assets).
enum KkTreeSeason: Equatable, CaseIterable {
    case spring
    case summer
    case autumn
    case winter

    static func resolve(for date: Date, calendar: Calendar = Calendar(identifier: .gregorian)) -> KkTreeSeason {
        let month = calendar.component(.month, from: date)
        switch month {
        case 3, 4, 5: return .spring
        case 6, 7, 8: return .summer
        case 9, 10, 11: return .autumn
        default: return .winter
        }
    }
}

/// Calendar festival decorations layered subtly onto the Reading Tree canvas,
/// covering major Chinese and US holidays using pure vector shapes.
enum KkTreeFestival: Equatable {
    case none
    case newYear
    case valentinesDay
    case springFestival
    case lanternFestival
    case qingming
    case dragonBoat
    case independenceDay
    case qixi
    case midAutumn
    case nationalDay
    case doubleNinth
    case halloween
    case thanksgiving
    case christmas

    static func resolve(
        for date: Date,
        gregorian: Calendar = Calendar(identifier: .gregorian),
        chinese: Calendar = Calendar(identifier: .chinese)
    ) -> KkTreeFestival {
        let gYear = gregorian.component(.year, from: date)
        let gMonth = gregorian.component(.month, from: date)
        let gDay = gregorian.component(.day, from: date)
        if gMonth == 12 && (24...26).contains(gDay) { return .christmas }

        let lMonth = chinese.component(.month, from: date)
        let lDay = chinese.component(.day, from: date)
        let isLeapMonth = chinese.dateComponents([.month], from: date).isLeapMonth ?? false
        if !isLeapMonth {
            if lMonth == 1 && (1...7).contains(lDay) { return .springFestival }
            if lMonth == 12 && lDay >= 29 { return .springFestival }
            if lMonth == 1 && lDay == 15 { return .lanternFestival }
            if lMonth == 5 && lDay == 5 { return .dragonBoat }
            if lMonth == 7 && lDay == 7 { return .qixi }
            if lMonth == 8 && lDay == 15 { return .midAutumn }
            if lMonth == 9 && lDay == 9 { return .doubleNinth }
        }

        if gMonth == 1 && (1...3).contains(gDay) { return .newYear }
        if gMonth == 2 && gDay == 14 { return .valentinesDay }
        if gMonth == 4 && gDay == qingmingDay(year: gYear) { return .qingming }
        if gMonth == 7 && gDay == 4 { return .independenceDay }
        if gMonth == 10 && (1...3).contains(gDay) { return .nationalDay }
        if gMonth == 10 && gDay == 31 { return .halloween }
        if gMonth == 11
            && gregorian.component(.weekday, from: date) == 5
            && gregorian.component(.weekdayOrdinal, from: date) == 4 {
            return .thanksgiving
        }
        return .none
    }

    private static func qingmingDay(year: Int) -> Int {
        // Astronomical Qingming solar term date formula valid across 2000–2099 (Apr 4 or 5).
        let y = max(0, year % 100)
        return Int(Double(y) * 0.2422 + 4.81) - max(0, y - 1) / 4
    }
}

struct KkTreeAtmosphere: Equatable {
    let season: KkTreeSeason
    let festival: KkTreeFestival
    let enabled: Bool

    static let standard = KkTreeAtmosphere(season: .summer, festival: .none, enabled: false)

    static func resolve(for date: Date, enabled: Bool = true) -> KkTreeAtmosphere {
        guard enabled else {
            return KkTreeAtmosphere(season: .summer, festival: .none, enabled: false)
        }
        return KkTreeAtmosphere(
            season: KkTreeSeason.resolve(for: date),
            festival: KkTreeFestival.resolve(for: date),
            enabled: true
        )
    }

    var showsWinterSnow: Bool {
        enabled && (season == .winter || festival == .christmas)
    }

    var showsFullMoon: Bool {
        enabled && festival == .midAutumn
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
    let totalListeningSeconds: Int64
    let todayListeningSeconds: Int64
    let streakDays: Int
    let booksFinished: Int
    let annotationCount: Int
    let atmosphere: KkTreeAtmosphere

    /// Visible fruits rendered on the tree, one per finished book up to the cap.
    let renderedFruits: Int
    /// Subtle blossom accents derived from notes and highlights.
    let renderedBlossoms: Int
    /// Continuous 0...1 foliage fullness used to modulate leaf density.
    let foliageDensity: Double
    /// Complete flower-and-fruit lifecycle accents rendered concurrently on the canopy ("花果同枝").
    let accents: [KkTreeAccent]

    var hasReadToday: Bool { todaySeconds > 0 }
    var hasListenedToday: Bool { todayListeningSeconds > 0 }
    var hasListeningHistory: Bool { totalListeningSeconds > 0 }
    var listeningBirdCount: Int {
        guard stage.rawValue >= KkTreeStage.seedling.rawValue, totalListeningSeconds > 0 else { return 0 }
        return totalListeningSeconds >= 36_000 ? 2 : 1
    }
    var isThriving: Bool { streakDays >= 3 }
    var nextStage: KkTreeStage? { stage.next }

    init(
        totalSeconds: Int64,
        todaySeconds: Int64,
        streakDays: Int,
        booksFinished: Int,
        annotationCount: Int,
        activeBookProgresses: [Double] = [],
        totalListeningSeconds: Int64 = 0,
        todayListeningSeconds: Int64 = 0,
        atmosphere: KkTreeAtmosphere = .standard
    ) {
        let safeTotal = max(0, totalSeconds)
        let safeToday = max(0, todaySeconds)
        let safeTotalListening = max(0, totalListeningSeconds)
        let safeTodayListening = max(0, todayListeningSeconds)
        let safeStreak = max(0, streakDays)
        let safeFinished = max(0, booksFinished)
        let safeAnnotations = max(0, annotationCount)

        let resolvedStage = KkTreeStage.stage(forTotalSeconds: safeTotal)
        self.stage = resolvedStage
        self.totalSeconds = safeTotal
        self.todaySeconds = safeToday
        self.totalListeningSeconds = safeTotalListening
        self.todayListeningSeconds = safeTodayListening
        self.streakDays = safeStreak
        self.booksFinished = safeFinished
        self.annotationCount = safeAnnotations
        self.atmosphere = atmosphere

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

        // Build "花果同枝" lifecycle accents from in-progress books + finished books + annotations.
        var builtAccents: [KkTreeAccent] = []
        if resolvedStage.rawValue >= KkTreeStage.seedling.rawValue {
            let activeLimit: Int
            switch resolvedStage {
            case .seedling: activeLimit = 3
            case .sapling: activeLimit = 5
            case .leafy: activeLimit = 6
            case .lush, .ancient: activeLimit = 8
            default: activeLimit = 0
            }

            let validProgresses = activeBookProgresses
                .filter { $0.isFinite && $0 > 0 && $0 < 100 }
                .prefix(activeLimit)

            var activeBlossomCount = 0
            for rawProgress in validProgresses {
                let p = min(99.9, max(0.1, rawProgress))
                if p < 25 {
                    builtAccents.append(KkTreeAccent(kind: .bud, maturity: p / 25.0, hasDriftingPetal: false))
                    activeBlossomCount += 1
                } else if p < 55 {
                    builtAccents.append(KkTreeAccent(kind: .blossom, maturity: (p - 25.0) / 30.0, hasDriftingPetal: false))
                    activeBlossomCount += 1
                } else if p < 80 {
                    let mat = (p - 55.0) / 25.0
                    builtAccents.append(KkTreeAccent(kind: .youngFruit, maturity: mat, hasDriftingPetal: p < 70))
                } else {
                    let mat = (p - 80.0) / 20.0
                    builtAccents.append(KkTreeAccent(kind: .ripeningFruit, maturity: mat, hasDriftingPetal: false))
                }
            }

            // Remaining annotation blossoms enrich the crown with buds and full blooms.
            let extraBlossoms = max(0, self.renderedBlossoms - activeBlossomCount)
            for idx in 0..<extraBlossoms {
                // Mix a subtle ratio of fresh buds among full blossoms for organic variety.
                let isBud = extraBlossoms >= 3 && (idx % 4 == 3)
                builtAccents.append(KkTreeAccent(
                    kind: isBud ? .bud : .blossom,
                    maturity: isBud ? 0.65 : 0.85,
                    hasDriftingPetal: false))
            }

            // Finished books hang as ripe fruits; on mature trees every 5th becomes a prized heirloom fruit.
            let allowHeirloom = resolvedStage.rawValue >= KkTreeStage.lush.rawValue
            for idx in 0..<self.renderedFruits {
                let isHeirloom = allowHeirloom && ((idx + 1) % 5 == 0)
                builtAccents.append(KkTreeAccent(
                    kind: isHeirloom ? .heirloomFruit : .ripeFruit,
                    maturity: 1.0,
                    hasDriftingPetal: false))
            }
        } else if resolvedStage == .sprout {
            for _ in 0..<self.renderedFruits {
                builtAccents.append(KkTreeAccent(kind: .ripeFruit, maturity: 1.0, hasDriftingPetal: false))
            }
        }
        self.accents = builtAccents
    }

    init(reading: KkReading, activeBookProgresses: [Double] = [], atmosphere: KkTreeAtmosphere = .standard) {
        self.init(
            totalSeconds: reading.totalSeconds,
            todaySeconds: reading.todaySeconds,
            streakDays: reading.streakDays,
            booksFinished: reading.booksFinished,
            annotationCount: reading.annotationCount,
            activeBookProgresses: activeBookProgresses,
            totalListeningSeconds: reading.safeTotalListeningSeconds,
            todayListeningSeconds: reading.safeTodayListeningSeconds,
            atmosphere: atmosphere)
    }

    init(snapshot: KkSnapshot, date: Date = Date(), isPreview: Bool = false) {
        let books = snapshot.continueBooks ?? (snapshot.continueReading.map { [$0] } ?? [])
        let atmosphere = KkTreeAtmosphere.resolve(for: date)
        let todaySec = isPreview
            ? snapshot.reading.todaySeconds
            : KkFormat.effectiveTodaySeconds(reading: snapshot.reading, generatedAt: snapshot.generatedAt, on: date)
        let streak = isPreview
            ? snapshot.reading.streakDays
            : KkFormat.effectiveStreakDays(reading: snapshot.reading, generatedAt: snapshot.generatedAt, on: date)
        self.init(
            totalSeconds: snapshot.reading.totalSeconds,
            todaySeconds: todaySec,
            streakDays: streak,
            booksFinished: snapshot.reading.booksFinished,
            annotationCount: snapshot.reading.annotationCount,
            activeBookProgresses: books.map(\.progress),
            totalListeningSeconds: snapshot.reading.safeTotalListeningSeconds,
            todayListeningSeconds: todaySec > 0 ? snapshot.reading.safeTodayListeningSeconds : 0,
            atmosphere: atmosphere)
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

// MARK: - Procedural Botanical Tree Geometry

/// One curved, tapered limb or twig in the reading tree skeleton.
struct KkTreeBranch: Equatable {
    let start: CGPoint
    let control: CGPoint
    let end: CGPoint
    let startWidth: CGFloat
    let endWidth: CGFloat
    let depth: Int
    let isDrooping: Bool
}

/// Foliage attachment anchor along outer boughs, dome crests, and hanging twigs.
struct KkTreeCanopyNode: Equatable {
    let point: CGPoint
    let angle: CGFloat
    let depth: Int
    let weight: CGFloat
    let isDrooping: Bool
}

/// Celestial body shown in the widget's upper-right sky when reading today.
enum KkSkyCelestialKind: Equatable {
    case sun
    case moon
}

/// Aspect-locked viewport mapping the canonical medium tree (`134x129`) uniformly
/// into any widget canvas size so `systemSmall` and `systemMedium` share exact
/// tree proportions, branch geometry, and sky placement.
struct KkTreeViewport: Equatable {
    let frame: CGRect
    let scale: CGFloat

    var unit: CGFloat { KkTreeSkeleton.canonicalCanvasSize.height * scale }

    func map(_ point: CGPoint) -> CGPoint {
        CGPoint(x: frame.minX + point.x * scale, y: frame.minY + point.y * scale)
    }
}

/// Sun/moon geometry in the upper-right sky, shared by the canvas renderer and
/// skeleton generator so the canopy gracefully yields sky clearance around the sun/moon.
struct KkSunPlacement: Equatable {
    let center: CGPoint
    let coreRadius: CGFloat
    let haloRadius: CGFloat
    /// Minimum distance from `center` reserved around the sun so branches and
    /// foliage tips never cover the sun disc.
    let clearanceRadius: CGFloat
}

/// Deterministic, bounds-aware botanical tree skeleton shared by the widget
/// canvas and logic tests. Mature stages spread into a wide umbrella crown
/// with arching lateral boughs and graceful drooping outer twigs.
struct KkTreeSkeleton {
    static let canonicalCanvasSize = CGSize(width: 134, height: 129)

    let viewport: CGRect
    let rootPoint: CGPoint
    let trunkTop: CGPoint
    let baseThickness: CGFloat
    let sun: KkSunPlacement
    let branches: [KkTreeBranch]
    let canopyNodes: [KkTreeCanopyNode]

    var hasDroopingBranches: Bool {
        branches.contains { $0.isDrooping && $0.end.y > $0.start.y }
    }

    var canopyBounds: CGRect {
        guard let first = canopyNodes.first?.point else { return .zero }
        var minX = first.x
        var maxX = first.x
        var minY = first.y
        var maxY = first.y
        for node in canopyNodes {
            minX = min(minX, node.point.x)
            maxX = max(maxX, node.point.x)
            minY = min(minY, node.point.y)
            maxY = max(maxY, node.point.y)
        }
        return CGRect(x: minX, y: minY, width: max(0, maxX - minX), height: max(0, maxY - minY))
    }

    static func celestialKind(isDark: Bool) -> KkSkyCelestialKind {
        isDark ? .moon : .sun
    }

    /// Analytic crescent moon vector path for dark mode (two-circle intersection
    /// with upper-right bite), avoiding blend-mode dependencies in WidgetKit snapshots.
    static func crescentMoonCGPath(center: CGPoint, radius: CGFloat) -> CGPath {
        let rOuter = max(0.5, radius)
        let rInner = rOuter * 0.88
        let offsetDist = rOuter * 0.56
        let offsetAngle: CGFloat = -0.58
        let ux = cos(offsetAngle)
        let uy = sin(offsetAngle)
        let cutoutCenter = CGPoint(x: center.x + ux * offsetDist, y: center.y + uy * offsetDist)

        let a = (rOuter * rOuter - rInner * rInner + offsetDist * offsetDist) / (2 * offsetDist)
        let h = sqrt(max(0, rOuter * rOuter - a * a))
        let midX = center.x + a * ux
        let midY = center.y + a * uy
        let t1 = CGPoint(x: midX + h * (-uy), y: midY + h * ux)
        let t2 = CGPoint(x: midX - h * (-uy), y: midY - h * ux)

        let outerStart = atan2(t1.y - center.y, t1.x - center.x)
        let outerEnd = atan2(t2.y - center.y, t2.x - center.x)
        let innerStart = atan2(t2.y - cutoutCenter.y, t2.x - cutoutCenter.x)
        let innerEnd = atan2(t1.y - cutoutCenter.y, t1.x - cutoutCenter.x)

        let path = CGMutablePath()
        path.addArc(center: center, radius: rOuter, startAngle: outerStart, endAngle: outerEnd, clockwise: false)
        path.addArc(center: cutoutCenter, radius: rInner, startAngle: innerStart, endAngle: innerEnd, clockwise: true)
        path.closeSubpath()
        return path
    }

    static func viewport(in size: CGSize) -> KkTreeViewport {
        let safeW = max(1, size.width)
        let safeH = max(1, size.height)
        let scale = min(safeW / canonicalCanvasSize.width, safeH / canonicalCanvasSize.height)
        let renderedW = canonicalCanvasSize.width * scale
        let renderedH = canonicalCanvasSize.height * scale
        let frame = CGRect(
            x: (safeW - renderedW) * 0.5,
            y: (safeH - renderedH) * 0.5,
            width: renderedW,
            height: renderedH
        )
        return KkTreeViewport(frame: frame, scale: scale)
    }

    private static func canonicalSunPlacement(in size: CGSize) -> KkSunPlacement {
        let unit = min(size.width, size.height)
        let coreRadius = max(3.8, unit * 0.048)
        let haloRadius = coreRadius * 2.1
        let center = CGPoint(
            x: size.width - max(coreRadius + 6.5, size.width * 0.13),
            y: max(coreRadius + 5.5, size.height * 0.14)
        )
        let clearanceRadius = coreRadius + unit * 0.165
        return KkSunPlacement(
            center: center,
            coreRadius: coreRadius,
            haloRadius: haloRadius,
            clearanceRadius: clearanceRadius
        )
    }

    static func sunPlacement(in size: CGSize) -> KkSunPlacement {
        let vp = viewport(in: size)
        let canonical = canonicalSunPlacement(in: canonicalCanvasSize)
        return KkSunPlacement(
            center: vp.map(canonical.center),
            coreRadius: canonical.coreRadius * vp.scale,
            haloRadius: canonical.haloRadius * vp.scale,
            clearanceRadius: canonical.clearanceRadius * vp.scale
        )
    }

    static func generate(profile: KkReadingTreeProfile, size: CGSize) -> KkTreeSkeleton {
        let vp = viewport(in: size)
        let canonical = generateCanonical(profile: profile, size: canonicalCanvasSize)
        if abs(vp.scale - 1) < 0.0001 && abs(vp.frame.minX) < 0.0001 && abs(vp.frame.minY) < 0.0001 {
            return canonical
        }
        return KkTreeSkeleton(
            viewport: vp.frame,
            rootPoint: vp.map(canonical.rootPoint),
            trunkTop: vp.map(canonical.trunkTop),
            baseThickness: canonical.baseThickness * vp.scale,
            sun: KkSunPlacement(
                center: vp.map(canonical.sun.center),
                coreRadius: canonical.sun.coreRadius * vp.scale,
                haloRadius: canonical.sun.haloRadius * vp.scale,
                clearanceRadius: canonical.sun.clearanceRadius * vp.scale),
            branches: canonical.branches.map { branch in
                KkTreeBranch(
                    start: vp.map(branch.start),
                    control: vp.map(branch.control),
                    end: vp.map(branch.end),
                    startWidth: branch.startWidth * vp.scale,
                    endWidth: branch.endWidth * vp.scale,
                    depth: branch.depth,
                    isDrooping: branch.isDrooping)
            },
            canopyNodes: canonical.canopyNodes.map { node in
                KkTreeCanopyNode(
                    point: vp.map(node.point),
                    angle: node.angle,
                    depth: node.depth,
                    weight: node.weight,
                    isDrooping: node.isDrooping)
            }
        )
    }

    private static func generateCanonical(profile: KkReadingTreeProfile, size: CGSize) -> KkTreeSkeleton {
        let groundY = size.height * 0.87
        let centerX = size.width * 0.5
        let unit = min(size.width, size.height)
        let sun = canonicalSunPlacement(in: size)
        let availableHeight = max(24, groundY - size.height * 0.08)

        let stageScale: CGFloat
        let trunkHeightRatio: CGFloat
        let baseThickness: CGFloat
        let spreadWidthFactor: CGFloat

        switch profile.stage {
        case .seedling:
            stageScale = 0.56 + 0.08 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.28
            baseThickness = unit * 0.042
            spreadWidthFactor = 0.25
        case .sapling:
            stageScale = 0.72 + 0.08 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.30
            baseThickness = unit * 0.056
            spreadWidthFactor = 0.34
        case .leafy:
            stageScale = 0.85 + 0.06 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.31
            baseThickness = unit * 0.068
            spreadWidthFactor = 0.40
        case .lush:
            stageScale = 0.93 + 0.05 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.31
            baseThickness = unit * 0.080
            spreadWidthFactor = 0.43
        case .ancient:
            stageScale = 1.0
            trunkHeightRatio = 0.30
            baseThickness = unit * 0.092
            spreadWidthFactor = 0.455
        default:
            stageScale = 0.52
            trunkHeightRatio = 0.28
            baseThickness = unit * 0.040
            spreadWidthFactor = 0.24
        }

        let trunkLen = availableHeight * trunkHeightRatio * stageScale
        let crownCenterX = profile.stage.rawValue >= KkTreeStage.leafy.rawValue
            ? centerX - size.width * 0.025
            : centerX
        let rootPoint = CGPoint(x: centerX - size.width * 0.01, y: groundY - 1)
        let trunkTop = CGPoint(x: crownCenterX - unit * 0.01, y: groundY - trunkLen)
        let trunkControl = CGPoint(x: centerX + unit * 0.02, y: groundY - trunkLen * 0.48)

        // Organic elliptical dome envelope keeps the canopy naturally rounded and
        // inside the widget margins without flattening against rectangular edges.
        let crownHalfW = max(18, size.width * spreadWidthFactor)
        let topMargin = size.height * (0.14 + (1 - stageScale) * 0.22)
        let bottomCanopyY = min(groundY - availableHeight * 0.14, trunkTop.y + trunkLen * 0.45)
        let domeTopY = max(size.height * 0.13, topMargin)
        let domeCenterY = (domeTopY + bottomCanopyY) * 0.5
        let domeHalfH = max(14, (bottomCanopyY - domeTopY) * 0.5)

        // Separate horizontal & vertical reach so both wide Small canvases (127x79)
        // and wide Medium canvases (134x129) produce a generously spreading dome.
        let reachX = crownHalfW * 0.58
        let reachY = max(12, min(domeHalfH * 0.68, crownHalfW * 0.62))
        let crownReach = (reachX + reachY) * 0.5
        let xStretch = max(1.05, reachX / max(1, reachY))

        var branches: [KkTreeBranch] = []
        var canopyNodes: [KkTreeCanopyNode] = []
        var rng = KkSeededGenerator(seed: 0x4C69627A6F7261) // "Libzora"

        // Main trunk
        branches.append(KkTreeBranch(
            start: rootPoint,
            control: trunkControl,
            end: trunkTop,
            startWidth: baseThickness,
            endWidth: baseThickness * 0.64,
            depth: 0,
            isDrooping: false))

        let stageRaw = profile.stage.rawValue
        let maxDepth = profile.stage.branchDepth

        func trunkPoint(at t: CGFloat) -> CGPoint {
            let mt = 1 - t
            let x = mt * mt * rootPoint.x + 2 * mt * t * trunkControl.x + t * t * trunkTop.x
            let y = mt * mt * rootPoint.y + 2 * mt * t * trunkControl.y + t * t * trunkTop.y
            return CGPoint(x: x, y: y)
        }

        /// Keeps any point strictly outside the upper-right sun clearance zone
        /// so the sun stays visible in open sky across every growth stage.
        func clearSun(_ point: CGPoint) -> CGPoint {
            let sdx = point.x - sun.center.x
            let sdy = point.y - sun.center.y
            let sdist = hypot(sdx, sdy)
            guard sdist < sun.clearanceRadius else { return point }
            let awayX: CGFloat
            let awayY: CGFloat
            if sdist > 0.5 && (sdx < -0.15 * sdist || sdy > 0.15 * sdist) {
                awayX = sdx / sdist
                awayY = sdy / sdist
            } else {
                // Directly on or above-right of the sun: deflect smoothly down-left toward the crown.
                awayX = -0.76
                awayY = 0.65
            }
            return CGPoint(
                x: sun.center.x + awayX * sun.clearanceRadius,
                y: sun.center.y + awayY * sun.clearanceRadius
            )
        }

        /// Softly pulls any point outside the rounded canopy dome back along the
        /// ellipse radius so the crown always forms an organic domed silhouette.
        func fitInDome(_ point: CGPoint, radialScale: CGFloat = 1.0) -> CGPoint {
            let rx = max(10, crownHalfW * radialScale)
            let ry = max(10, domeHalfH * radialScale)
            let dx = (point.x - crownCenterX) / rx
            let dy = (point.y - domeCenterY) / ry
            let dist = hypot(dx, dy)
            var result = point
            if dist > 1.0 {
                result = CGPoint(
                    x: crownCenterX + (dx / dist) * rx,
                    y: domeCenterY + (dy / dist) * ry
                )
            }
            result = clearSun(result)
            // Also guarantee hard widget safety margins.
            return CGPoint(
                x: min(max(result.x, size.width * 0.045), size.width * 0.955),
                y: min(max(result.y, size.height * 0.12), groundY - availableHeight * 0.14)
            )
        }

        func stepVector(angle: CGFloat, length: CGFloat) -> CGVector {
            CGVector(dx: cos(angle) * length * xStretch, dy: sin(angle) * length)
        }

        // Spawns a graceful arching + drooping outer twig (分叉下垂枝条) hanging from the curved rim.
        func addDroopingSpray(
            from origin: CGPoint,
            side: CGFloat,
            length: CGFloat,
            width: CGFloat,
            depth: Int,
            cascadeCount: Int
        ) {
            // Angle outward-and-downward (22°..48° below horizontal) so twigs arch softly instead of hanging stiffly vertical.
            let dropPitch: CGFloat = 0.36 + CGFloat(rng.nextUnit()) * 0.44
            let droopAngle: CGFloat = side < 0 ? (.pi - dropPitch) : dropPitch
            let delta = stepVector(angle: droopAngle, length: length)
            let rawEnd = CGPoint(x: origin.x + delta.dx, y: origin.y + delta.dy)
            let end = fitInDome(rawEnd, radialScale: 1.04)
            // Only emit a downward droop if it genuinely descends below origin.
            let adjustedEnd = clearSun(CGPoint(
                x: end.x,
                y: min(groundY - availableHeight * 0.15, max(end.y, origin.y + max(3.0, length * 0.35)))))
            let control = fitInDome(CGPoint(
                x: origin.x + (adjustedEnd.x - origin.x) * 0.58,
                y: origin.y - length * 0.14), radialScale: 1.04)
            let tipWidth = max(0.85, width * 0.54)

            branches.append(KkTreeBranch(
                start: origin,
                control: control,
                end: adjustedEnd,
                startWidth: width,
                endWidth: tipWidth,
                depth: depth,
                isDrooping: true))

            let midOnCurve = clearSun(CGPoint(
                x: 0.25 * origin.x + 0.5 * control.x + 0.25 * adjustedEnd.x,
                y: 0.25 * origin.y + 0.5 * control.y + 0.25 * adjustedEnd.y))
            canopyNodes.append(KkTreeCanopyNode(point: midOnCurve, angle: droopAngle, depth: depth, weight: 0.82, isDrooping: true))
            canopyNodes.append(KkTreeCanopyNode(point: adjustedEnd, angle: droopAngle + side * 0.16, depth: depth + 1, weight: 0.94, isDrooping: true))

            // Delicate secondary weeping fork for lush & ancient stages.
            if cascadeCount > 0 {
                let subLen = length * (0.58 + CGFloat(rng.nextUnit()) * 0.14)
                let subPitch: CGFloat = 0.65 + CGFloat(rng.nextUnit()) * 0.36
                let subAngle: CGFloat = side < 0 ? (.pi - subPitch) : subPitch
                let subRaw = CGPoint(
                    x: adjustedEnd.x + cos(subAngle) * subLen,
                    y: adjustedEnd.y + sin(subAngle) * subLen)
                let subCleared = clearSun(CGPoint(
                    x: min(max(subRaw.x, size.width * 0.05), size.width * 0.95),
                    y: min(groundY - availableHeight * 0.14, max(subRaw.y, adjustedEnd.y + max(2.5, subLen * 0.4)))))
                let subEnd = CGPoint(
                    x: min(max(subCleared.x, size.width * 0.05), size.width * 0.95),
                    y: min(groundY - availableHeight * 0.14, max(subCleared.y, adjustedEnd.y + 2.0)))
                let subCtrl = CGPoint(
                    x: adjustedEnd.x + (subEnd.x - adjustedEnd.x) * 0.45 + side * subLen * 0.10,
                    y: adjustedEnd.y + (subEnd.y - adjustedEnd.y) * 0.28)
                branches.append(KkTreeBranch(
                    start: adjustedEnd,
                    control: subCtrl,
                    end: subEnd,
                    startWidth: tipWidth,
                    endWidth: max(0.7, tipWidth * 0.58),
                    depth: depth + 1,
                    isDrooping: true))
                canopyNodes.append(KkTreeCanopyNode(point: subEnd, angle: subAngle, depth: depth + 1, weight: 0.86, isDrooping: true))
            }
        }

        // Recursive spreading bough builder with rounded umbrella dome & natural drooping outer forks.
        func growBough(
            from start: CGPoint,
            angle: CGFloat,
            length: CGFloat,
            width: CGFloat,
            depth: Int,
            side: CGFloat
        ) {
            if depth > maxDepth {
                canopyNodes.append(KkTreeCanopyNode(
                    point: start,
                    angle: angle,
                    depth: depth - 1,
                    weight: 0.95,
                    isDrooping: sin(angle) > 0.05))
                return
            }

            let isOuter = abs(angle + .pi / 2) > 0.56
            let childCount: Int = (depth == 2 && stageRaw >= KkTreeStage.lush.rawValue && !isOuter) ? 3 : 2

            for idx in 0..<childCount {
                // Outer forks on leafy/lush/ancient curve gently outward and droop downward.
                let isDroopFork = (stageRaw >= KkTreeStage.leafy.rawValue)
                    && depth == max(2, maxDepth - 1)
                    && isOuter
                    && ((side < 0 && idx == 0) || (side > 0 && idx == childCount - 1))

                if isDroopFork {
                    let droopLen = length * (0.70 + CGFloat(rng.nextUnit()) * 0.16)
                    let cascades = (stageRaw >= KkTreeStage.lush.rawValue) ? 1 : 0
                    addDroopingSpray(
                        from: start,
                        side: side,
                        length: droopLen,
                        width: max(0.95, width * 0.72),
                        depth: depth,
                        cascadeCount: cascades)
                    continue
                }

                let spread: CGFloat = depth == 2 ? 0.50 : 0.44
                let offsetUnit: CGFloat
                if childCount == 3 {
                    offsetUnit = CGFloat(idx - 1) * 0.82
                } else {
                    offsetUnit = idx == 0 ? -0.60 : 0.60
                }
                let jitter = (CGFloat(rng.nextUnit()) - 0.5) * 0.14
                var childAngle = angle + offsetUnit * spread + jitter

                // Keep inner branches filling the rounded crown dome naturally while outer limbs fan wide.
                childAngle = min(max(childAngle, -.pi + 0.18), -0.18)

                let lenFactor = 0.68 + CGFloat(rng.nextUnit()) * 0.12
                let horizontalBonus: CGFloat = 1.0 + 0.12 * abs(cos(childAngle))
                let childLen = length * lenFactor * horizontalBonus
                let delta = stepVector(angle: childAngle, length: childLen)
                let rawEnd = CGPoint(x: start.x + delta.dx, y: start.y + delta.dy)
                let end = fitInDome(rawEnd, radialScale: depth == maxDepth ? 0.98 : 0.90)

                let mid = CGPoint(x: (start.x + end.x) * 0.5, y: (start.y + end.y) * 0.5)
                let archLift = childLen * (0.08 + CGFloat(rng.nextUnit()) * 0.07)
                let control = fitInDome(CGPoint(
                    x: mid.x + (CGFloat(rng.nextUnit()) - 0.5) * childLen * 0.08,
                    y: mid.y - archLift), radialScale: 0.98)

                let childWidth = max(0.9, width * 0.64)
                branches.append(KkTreeBranch(
                    start: start,
                    control: control,
                    end: end,
                    startWidth: width,
                    endWidth: childWidth,
                    depth: depth,
                    isDrooping: false))

                // Populate interior & outer canopy nodes so the crown is lush and full rather than hollow.
                if depth >= 2 {
                    canopyNodes.append(KkTreeCanopyNode(
                        point: end,
                        angle: childAngle,
                        depth: depth,
                        weight: depth == maxDepth ? 1.0 : 0.92,
                        isDrooping: false))
                    if stageRaw >= KkTreeStage.leafy.rawValue && depth <= 3 {
                        canopyNodes.append(KkTreeCanopyNode(
                            point: clearSun(mid),
                            angle: childAngle,
                            depth: depth,
                            weight: 0.82,
                            isDrooping: false))
                    }
                }

                let nextSide: CGFloat = side != 0 ? side : (end.x < centerX ? -1 : 1)
                growBough(
                    from: end,
                    angle: childAngle,
                    length: childLen,
                    width: childWidth,
                    depth: depth + 1,
                    side: nextSide)
            }
        }

        // Primary crown boughs fanning generously out of the trunk top.
        struct PrimarySpec {
            let angleOffset: CGFloat   // radians relative to straight up (-pi/2)
            let lengthFactor: CGFloat
            let widthFactor: CGFloat
            let side: CGFloat
        }

        let primaries: [PrimarySpec]
        switch profile.stage {
        case .seedling:
            primaries = [
                PrimarySpec(angleOffset: -0.78, lengthFactor: 0.96, widthFactor: 0.62, side: -1),
                PrimarySpec(angleOffset: -0.04, lengthFactor: 0.92, widthFactor: 0.64, side: 0),
                PrimarySpec(angleOffset: 0.74, lengthFactor: 0.94, widthFactor: 0.60, side: 1)
            ]
        case .sapling:
            primaries = [
                PrimarySpec(angleOffset: -0.96, lengthFactor: 1.04, widthFactor: 0.62, side: -1),
                PrimarySpec(angleOffset: -0.34, lengthFactor: 0.96, widthFactor: 0.62, side: -1),
                PrimarySpec(angleOffset: 0.36, lengthFactor: 0.96, widthFactor: 0.62, side: 1),
                PrimarySpec(angleOffset: 0.94, lengthFactor: 1.02, widthFactor: 0.60, side: 1)
            ]
        case .leafy:
            primaries = [
                PrimarySpec(angleOffset: -1.08, lengthFactor: 1.06, widthFactor: 0.62, side: -1),
                PrimarySpec(angleOffset: -0.48, lengthFactor: 0.98, widthFactor: 0.62, side: -1),
                PrimarySpec(angleOffset: 0.04, lengthFactor: 0.94, widthFactor: 0.60, side: 0),
                PrimarySpec(angleOffset: 0.52, lengthFactor: 0.98, widthFactor: 0.62, side: 1),
                PrimarySpec(angleOffset: 1.06, lengthFactor: 1.04, widthFactor: 0.60, side: 1)
            ]
        case .lush, .ancient:
            primaries = [
                PrimarySpec(angleOffset: -1.16, lengthFactor: 1.12, widthFactor: 0.64, side: -1),
                PrimarySpec(angleOffset: -0.56, lengthFactor: 1.02, widthFactor: 0.62, side: -1),
                PrimarySpec(angleOffset: -0.02, lengthFactor: 0.96, widthFactor: 0.60, side: 0),
                PrimarySpec(angleOffset: 0.54, lengthFactor: 1.02, widthFactor: 0.62, side: 1),
                PrimarySpec(angleOffset: 1.14, lengthFactor: 1.10, widthFactor: 0.64, side: 1)
            ]
        default:
            primaries = [
                PrimarySpec(angleOffset: -0.75, lengthFactor: 0.92, widthFactor: 0.60, side: -1),
                PrimarySpec(angleOffset: 0.72, lengthFactor: 0.92, widthFactor: 0.60, side: 1)
            ]
        }

        for spec in primaries {
            let jitter = (CGFloat(rng.nextUnit()) - 0.5) * 0.08
            let angle = -.pi / 2 + spec.angleOffset + jitter
            let len = crownReach * spec.lengthFactor
            let delta = stepVector(angle: angle, length: len)
            let rawEnd = CGPoint(x: trunkTop.x + delta.dx, y: trunkTop.y + delta.dy)
            let end = fitInDome(rawEnd, radialScale: 0.72)
            let mid = CGPoint(x: (trunkTop.x + end.x) * 0.5, y: (trunkTop.y + end.y) * 0.5)
            let lift = len * (abs(spec.angleOffset) > 0.8 ? 0.16 : 0.08)
            let control = fitInDome(CGPoint(x: mid.x, y: mid.y - lift), radialScale: 0.82)
            let startW = baseThickness * spec.widthFactor
            let endW = max(1.2, startW * 0.64)

            branches.append(KkTreeBranch(
                start: trunkTop,
                control: control,
                end: end,
                startWidth: startW,
                endWidth: endW,
                depth: 1,
                isDrooping: false))

            canopyNodes.append(KkTreeCanopyNode(
                point: end,
                angle: angle,
                depth: 1,
                weight: 0.88,
                isDrooping: false))

            growBough(
                from: end,
                angle: angle,
                length: len * 0.74,
                width: endW,
                depth: 2,
                side: spec.side)
        }

        // Lower sweeping lateral boughs emerging directly from the upper trunk on mature stages,
        // branching into an uplifting foliage bough + a graceful overhanging drooping spray.
        if stageRaw >= KkTreeStage.leafy.rawValue {
            let lateralConfigs: [(t: CGFloat, side: CGFloat, angleOff: CGFloat, lenScale: CGFloat)] =
                stageRaw >= KkTreeStage.lush.rawValue
                ? [
                    (0.74, -1, -1.30, 1.04),
                    (0.80, 1, 1.28, 1.02)
                ]
                : [
                    (0.78, -1, -1.26, 0.92),
                    (0.84, 1, 1.24, 0.90)
                ]

            for lateral in lateralConfigs {
                let origin = trunkPoint(at: lateral.t)
                let angle = -.pi / 2 + lateral.angleOff
                let len = crownReach * lateral.lenScale
                let delta = stepVector(angle: angle, length: len)
                let end = fitInDome(CGPoint(x: origin.x + delta.dx, y: origin.y + delta.dy), radialScale: 0.86)
                let control = fitInDome(CGPoint(
                    x: origin.x + (end.x - origin.x) * 0.48,
                    y: min(origin.y, end.y) - len * 0.20), radialScale: 0.90)
                let startW = baseThickness * 0.46
                let endW = max(1.1, startW * 0.58)

                branches.append(KkTreeBranch(
                    start: origin,
                    control: control,
                    end: end,
                    startWidth: startW,
                    endWidth: endW,
                    depth: 1,
                    isDrooping: false))

                canopyNodes.append(KkTreeCanopyNode(point: end, angle: angle, depth: 2, weight: 0.92, isDrooping: false))

                // Upper lift fork + graceful drooping outer spray from each lower bough.
                let liftAngle = -.pi / 2 + lateral.side * 0.84
                let liftLen = len * 0.56
                let liftEnd = fitInDome(CGPoint(x: end.x + cos(liftAngle) * liftLen, y: end.y + sin(liftAngle) * liftLen), radialScale: 0.95)
                let liftCtrl = fitInDome(CGPoint(
                    x: (end.x + liftEnd.x) * 0.5,
                    y: (end.y + liftEnd.y) * 0.5 - liftLen * 0.12), radialScale: 0.95)
                branches.append(KkTreeBranch(
                    start: end,
                    control: liftCtrl,
                    end: liftEnd,
                    startWidth: endW,
                    endWidth: max(0.9, endW * 0.58),
                    depth: 2,
                    isDrooping: false))
                canopyNodes.append(KkTreeCanopyNode(point: liftEnd, angle: liftAngle, depth: 3, weight: 0.95, isDrooping: false))

                addDroopingSpray(
                    from: end,
                    side: lateral.side,
                    length: len * (stageRaw == KkTreeStage.ancient.rawValue ? 0.62 : 0.52),
                    width: endW * 0.92,
                    depth: 2,
                    cascadeCount: stageRaw >= KkTreeStage.lush.rawValue ? 1 : 0)
            }
        }

        // Fill the inner dome core on mature trees so foliage looks genuinely lush and full ("枝繁叶茂"),
        // while keeping the lower trunk and main fork readable beneath.
        if stageRaw >= KkTreeStage.leafy.rawValue {
            let coreOffsets: [(dx: CGFloat, dy: CGFloat, w: CGFloat)] = [
                (-0.34, -0.18, 1.05),
                (0.0, -0.28, 1.12),
                (0.34, -0.18, 1.05),
                (-0.22, 0.06, 0.96),
                (0.22, 0.06, 0.96),
                (0.0, -0.02, 1.02)
            ]
            for core in coreOffsets {
                let pt = fitInDome(CGPoint(
                    x: crownCenterX + crownHalfW * core.dx,
                    y: domeCenterY + domeHalfH * core.dy), radialScale: 0.88)
                canopyNodes.append(KkTreeCanopyNode(
                    point: pt,
                    angle: -.pi / 2 + core.dx * 0.9,
                    depth: maxDepth,
                    weight: core.w,
                    isDrooping: false))
            }
        }

        return KkTreeSkeleton(
            viewport: CGRect(origin: .zero, size: size),
            rootPoint: rootPoint,
            trunkTop: trunkTop,
            baseThickness: baseThickness,
            sun: sun,
            branches: branches,
            canopyNodes: canopyNodes)
    }
}

struct KkSeededGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        self.state = seed == 0 ? 0x9E3779B97F4A7C15 : seed
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E3779B97F4A7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58476D1CE4E5B9
        z = (z ^ (z >> 27)) &* 0x94D049BB133111EB
        return z ^ (z >> 31)
    }

    mutating func nextUnit() -> Double {
        Double(next() >> 11) / Double(1 << 53)
    }
}
