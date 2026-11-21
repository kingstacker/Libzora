import SwiftUI
import WidgetKit

/// Botanical ink-and-paper tree rendered with SwiftUI Canvas. Growth stages,
/// foliage fullness, fruits (finished books), blossoms (annotations), and
/// daily/streak vitality accents are all driven deterministically by
/// `KkReadingTreeProfile`.
struct KkReadingTreeCanvas: View {
    let profile: KkReadingTreeProfile
    @Environment(\.colorScheme) private var scheme
    @Environment(\.widgetRenderingMode) private var renderingMode

    private var isFullColor: Bool { renderingMode == .fullColor }

    var body: some View {
        Canvas { context, size in
            guard size.width > 12, size.height > 12 else { return }
            let palette = TreePalette(scheme: scheme, isFullColor: isFullColor)
            let groundY = size.height * 0.87
            let centerX = size.width * 0.5

            if profile.hasReadToday {
                drawSunGlow(in: &context, size: size, groundY: groundY, palette: palette)
            }

            drawGround(in: &context, width: size.width, groundY: groundY, palette: palette)

            switch profile.stage {
            case .seed:
                drawSeed(in: &context, centerX: centerX, groundY: groundY, size: size, palette: palette)
            case .sprout:
                drawSprout(in: &context, centerX: centerX, groundY: groundY, size: size, palette: palette)
            case .seedling, .sapling, .leafy, .lush, .ancient:
                drawTree(in: &context, centerX: centerX, groundY: groundY, size: size, palette: palette)
            }

            if profile.isThriving && profile.stage.rawValue >= KkTreeStage.seedling.rawValue {
                drawFloatingMotes(in: &context, size: size, groundY: groundY, palette: palette)
            }
        }
        .accessibilityHidden(true)
    }

    // MARK: - Atmosphere & Ground

    private func drawSunGlow(in context: inout GraphicsContext, size: CGSize, groundY: CGFloat, palette: TreePalette) {
        let minDim = min(size.width, size.height)
        let sunCenter = CGPoint(x: size.width * 0.78, y: groundY * 0.24)
        let haloRadius = minDim * 0.16
        let coreRadius = minDim * 0.055

        context.fill(
            Path(ellipseIn: CGRect(
                x: sunCenter.x - haloRadius, y: sunCenter.y - haloRadius,
                width: haloRadius * 2, height: haloRadius * 2)),
            with: .color(palette.sunHalo))
        context.fill(
            Path(ellipseIn: CGRect(
                x: sunCenter.x - coreRadius, y: sunCenter.y - coreRadius,
                width: coreRadius * 2, height: coreRadius * 2)),
            with: .color(palette.sunCore))
    }

    private func drawGround(in context: inout GraphicsContext, width: CGFloat, groundY: CGFloat, palette: TreePalette) {
        let span = width * 0.72
        let startX = (width - span) * 0.5
        let endX = startX + span

        var mound = Path()
        mound.move(to: CGPoint(x: startX, y: groundY))
        mound.addQuadCurve(
            to: CGPoint(x: endX, y: groundY),
            control: CGPoint(x: width * 0.5, y: groundY - max(4, width * 0.04)))
        context.stroke(mound, with: .color(palette.groundLine),
                       style: StrokeStyle(lineWidth: 1.1, lineCap: .round))

        var wash = mound
        wash.addQuadCurve(
            to: CGPoint(x: startX, y: groundY),
            control: CGPoint(x: width * 0.5, y: groundY + max(5, width * 0.05)))
        wash.closeSubpath()
        context.fill(wash, with: .color(palette.groundWash))

        // Tiny grass tufts anchored on the earth line.
        let tuftOffsets: [(CGFloat, CGFloat)] = [(-0.22, 0.8), (-0.16, 0.6), (0.19, 0.75), (0.25, 0.55)]
        for (offset, scale) in tuftOffsets {
            let x = width * 0.5 + span * offset
            let h = max(3, width * 0.032) * scale
            var blade = Path()
            blade.move(to: CGPoint(x: x, y: groundY - 0.5))
            blade.addQuadCurve(
                to: CGPoint(x: x + (offset < 0 ? -h * 0.45 : h * 0.45), y: groundY - h),
                control: CGPoint(x: x, y: groundY - h * 0.55))
            context.stroke(blade, with: .color(palette.leafMid.opacity(0.65)),
                           style: StrokeStyle(lineWidth: 0.9, lineCap: .round))
        }
    }

    // MARK: - Early Stages

    private func drawSeed(in context: inout GraphicsContext, centerX: CGFloat, groundY: CGFloat,
                          size: CGSize, palette: TreePalette) {
        let unit = min(size.width, size.height)
        let seedW = unit * 0.085
        let seedH = unit * 0.115
        let seedY = groundY - seedH * 0.42

        // Dashed growth aura hinting at potential.
        let ringR = unit * 0.14
        let ring = Path(ellipseIn: CGRect(x: centerX - ringR, y: seedY - ringR, width: ringR * 2, height: ringR * 2))
        context.stroke(ring, with: .color(palette.faintInk.opacity(0.35)),
                       style: StrokeStyle(lineWidth: 0.9, lineCap: .round, dash: [2.5, 3.5]))

        var seedPath = Path()
        seedPath.move(to: CGPoint(x: centerX, y: seedY - seedH * 0.52))
        seedPath.addCurve(
            to: CGPoint(x: centerX, y: seedY + seedH * 0.48),
            control1: CGPoint(x: centerX + seedW * 0.78, y: seedY - seedH * 0.12),
            control2: CGPoint(x: centerX + seedW * 0.68, y: seedY + seedH * 0.48))
        seedPath.addCurve(
            to: CGPoint(x: centerX, y: seedY - seedH * 0.52),
            control1: CGPoint(x: centerX - seedW * 0.68, y: seedY + seedH * 0.48),
            control2: CGPoint(x: centerX - seedW * 0.78, y: seedY - seedH * 0.12))
        seedPath.closeSubpath()

        context.fill(seedPath, with: .color(palette.trunk))
        var crease = Path()
        crease.move(to: CGPoint(x: centerX, y: seedY - seedH * 0.3))
        crease.addQuadCurve(
            to: CGPoint(x: centerX - seedW * 0.1, y: seedY + seedH * 0.3),
            control: CGPoint(x: centerX + seedW * 0.18, y: seedY))
        context.stroke(crease, with: .color(palette.paperHighlight),
                       style: StrokeStyle(lineWidth: 0.9, lineCap: .round))
    }

    private func drawSprout(in context: inout GraphicsContext, centerX: CGFloat, groundY: CGFloat,
                            size: CGSize, palette: TreePalette) {
        let unit = min(size.width, size.height)
        let stemHeight = unit * (0.24 + 0.08 * profile.stageProgress)
        let top = CGPoint(x: centerX + unit * 0.015, y: groundY - stemHeight)

        var stem = Path()
        stem.move(to: CGPoint(x: centerX, y: groundY - 1))
        stem.addQuadCurve(
            to: top,
            control: CGPoint(x: centerX - unit * 0.025, y: groundY - stemHeight * 0.55))
        context.stroke(stem, with: .color(palette.trunk),
                       style: StrokeStyle(lineWidth: max(1.8, unit * 0.022), lineCap: .round))

        // Pair of cotyledon leaves.
        let leafLen = unit * (0.14 + 0.04 * profile.stageProgress)
        let leafWid = leafLen * 0.52
        drawLeaf(in: &context, origin: top, angle: -2.45, length: leafLen, width: leafWid,
                 fill: palette.leafLight, vein: palette.trunk.opacity(0.35))
        drawLeaf(in: &context, origin: top, angle: -0.58, length: leafLen * 0.92, width: leafWid * 0.92,
                 fill: palette.leafMid, vein: palette.trunk.opacity(0.35))

        // Early finished books appear as small fallen/resting fruits near the base.
        if profile.renderedFruits > 0 {
            let fruitR = max(2.6, unit * 0.028)
            for idx in 0..<profile.renderedFruits {
                let fx = centerX + (idx == 0 ? -unit * 0.14 : unit * 0.15)
                let fy = groundY - fruitR * 0.95
                drawFruit(in: &context, at: CGPoint(x: fx, y: fy), radius: fruitR, palette: palette)
            }
        }
    }

    // MARK: - Procedural Tree (Seedling -> Ancient)

    private struct BranchSegment {
        let start: CGPoint
        let control: CGPoint
        let end: CGPoint
        let startWidth: CGFloat
        let endWidth: CGFloat
        let depth: Int
        let maxDepth: Int
    }

    private struct CanopyNode {
        let point: CGPoint
        let angle: CGFloat
        let depth: Int
    }

    private func drawTree(in context: inout GraphicsContext, centerX: CGFloat, groundY: CGFloat,
                          size: CGSize, palette: TreePalette) {
        let unit = min(size.width, size.height)
        let stageFactor: CGFloat
        let trunkHeightRatio: CGFloat
        let baseThickness: CGFloat

        switch profile.stage {
        case .seedling:
            stageFactor = 0.52 + 0.10 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.24
            baseThickness = unit * 0.036
        case .sapling:
            stageFactor = 0.66 + 0.10 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.25
            baseThickness = unit * 0.048
        case .leafy:
            stageFactor = 0.80 + 0.08 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.26
            baseThickness = unit * 0.060
        case .lush:
            stageFactor = 0.91 + 0.06 * CGFloat(profile.stageProgress)
            trunkHeightRatio = 0.265
            baseThickness = unit * 0.072
        case .ancient:
            stageFactor = 1.0
            trunkHeightRatio = 0.27
            baseThickness = unit * 0.084
        default:
            stageFactor = 0.5
            trunkHeightRatio = 0.24
            baseThickness = unit * 0.04
        }

        let maxDepth = profile.stage.branchDepth
        let availableHeight = groundY - size.height * 0.09
        let trunkLen = availableHeight * trunkHeightRatio * stageFactor
        let rootPoint = CGPoint(x: centerX, y: groundY - 1)
        let trunkTop = CGPoint(x: centerX - unit * 0.01, y: groundY - trunkLen)

        var branches: [BranchSegment] = []
        var canopyNodes: [CanopyNode] = []

        // Root flare accents for sapling and above.
        if profile.stage.rawValue >= KkTreeStage.sapling.rawValue {
            drawRootFlare(in: &context, root: rootPoint, thickness: baseThickness, palette: palette)
        }

        let trunkControl = CGPoint(x: centerX + unit * 0.018, y: groundY - trunkLen * 0.52)
        branches.append(BranchSegment(
            start: rootPoint,
            control: trunkControl,
            end: trunkTop,
            startWidth: baseThickness,
            endWidth: baseThickness * 0.66,
            depth: 0,
            maxDepth: maxDepth))

        var rng = SeededGenerator(seed: 0x4C69627A6F7261) // "Libzora"
        buildBranches(
            from: trunkTop,
            angle: -.pi / 2,
            length: trunkLen * 0.82,
            width: baseThickness * 0.66,
            depth: 1,
            maxDepth: maxDepth,
            stageFactor: stageFactor,
            rng: &rng,
            branches: &branches,
            canopyNodes: &canopyNodes)

        // 1. Soft ink-wash foliage clouds behind branches for depth.
        drawCanopyWash(in: &context, nodes: canopyNodes, unit: unit, palette: palette)

        // 2. Trunk & branches drawn back-to-front by depth.
        for segment in branches {
            drawTaperedSegment(in: &context, segment: segment, palette: palette)
        }

        // 3. Individual botanical leaves clustered on terminal & sub-terminal twigs.
        drawFoliageLeaves(in: &context, nodes: canopyNodes, unit: unit, palette: palette)

        // 4. Blossoms (annotations) & Fruits (finished books) on outer canopy nodes.
        drawCanopyAccents(in: &context, nodes: canopyNodes, unit: unit, palette: palette)
    }

    private func buildBranches(
        from start: CGPoint,
        angle: CGFloat,
        length: CGFloat,
        width: CGFloat,
        depth: Int,
        maxDepth: Int,
        stageFactor: CGFloat,
        rng: inout SeededGenerator,
        branches: inout [BranchSegment],
        canopyNodes: inout [CanopyNode]
    ) {
        if depth > maxDepth {
            canopyNodes.append(CanopyNode(point: start, angle: angle, depth: depth - 1))
            return
        }

        let childCount = (depth == 1 && maxDepth >= 3) ? 3 : 2
        let spreadBase: CGFloat = depth == 1 ? 0.52 : 0.42

        for index in 0..<childCount {
            let offsetUnit: CGFloat
            if childCount == 3 {
                offsetUnit = CGFloat(index - 1) * 0.92
            } else {
                offsetUnit = index == 0 ? -0.58 : 0.58
            }
            let jitter = CGFloat(rng.nextUnit() - 0.5) * 0.16
            var childAngle = angle + offsetUnit * spreadBase + jitter
            // Gentle upward phototropism so outer branches stay inside the widget frame.
            childAngle = childAngle * 0.84 + (-.pi / 2) * 0.16

            let lenScale = (0.72 + CGFloat(rng.nextUnit()) * 0.14) * (childCount == 3 && index == 1 ? 0.92 : 1.0)
            let childLength = length * lenScale
            let end = CGPoint(
                x: start.x + cos(childAngle) * childLength,
                y: start.y + sin(childAngle) * childLength)
            let mid = CGPoint(x: (start.x + end.x) * 0.5, y: (start.y + end.y) * 0.5)
            let normalAngle = childAngle + .pi / 2
            let bow = (CGFloat(rng.nextUnit()) - 0.5) * childLength * 0.22
            let control = CGPoint(x: mid.x + cos(normalAngle) * bow, y: mid.y + sin(normalAngle) * bow)

            let childWidth = max(1.0, width * 0.66)
            branches.append(BranchSegment(
                start: start,
                control: control,
                end: end,
                startWidth: width,
                endWidth: childWidth,
                depth: depth,
                maxDepth: maxDepth))

            if depth >= max(1, maxDepth - 1) {
                canopyNodes.append(CanopyNode(point: end, angle: childAngle, depth: depth))
            }

            buildBranches(
                from: end,
                angle: childAngle,
                length: childLength,
                width: childWidth,
                depth: depth + 1,
                maxDepth: maxDepth,
                stageFactor: stageFactor,
                rng: &rng,
                branches: &branches,
                canopyNodes: &canopyNodes)
        }
    }

    private func drawRootFlare(in context: inout GraphicsContext, root: CGPoint, thickness: CGFloat, palette: TreePalette) {
        var flare = Path()
        flare.move(to: CGPoint(x: root.x - thickness * 1.15, y: root.y + 0.5))
        flare.addQuadCurve(
            to: CGPoint(x: root.x - thickness * 0.35, y: root.y - thickness * 1.4),
            control: CGPoint(x: root.x - thickness * 0.45, y: root.y - thickness * 0.15))
        flare.addLine(to: CGPoint(x: root.x + thickness * 0.35, y: root.y - thickness * 1.4))
        flare.addQuadCurve(
            to: CGPoint(x: root.x + thickness * 1.15, y: root.y + 0.5),
            control: CGPoint(x: root.x + thickness * 0.45, y: root.y - thickness * 0.15))
        flare.closeSubpath()
        context.fill(flare, with: .color(palette.trunk))
    }

    private func drawTaperedSegment(in context: inout GraphicsContext, segment: BranchSegment, palette: TreePalette) {
        var path = Path()
        path.move(to: segment.start)
        path.addQuadCurve(to: segment.end, control: segment.control)
        let avgWidth = (segment.startWidth + segment.endWidth) * 0.5
        let alpha = segment.depth <= 1 ? 1.0 : max(0.74, 1.0 - Double(segment.depth - 1) * 0.08)
        context.stroke(path, with: .color(palette.trunk.opacity(alpha)),
                       style: StrokeStyle(lineWidth: avgWidth, lineCap: .round, lineJoin: .round))
    }

    private func drawCanopyWash(in context: inout GraphicsContext, nodes: [CanopyNode], unit: CGFloat, palette: TreePalette) {
        guard !nodes.isEmpty else { return }
        let density = profile.foliageDensity
        let strideStep = max(1, nodes.count / 18)
        let baseRadius = unit * (0.065 + 0.04 * CGFloat(density))

        for (index, node) in nodes.enumerated() where index % strideStep == 0 {
            let r = baseRadius * (node.depth == profile.stage.branchDepth ? 1.0 : 0.85)
            let rect = CGRect(x: node.point.x - r, y: node.point.y - r * 0.88, width: r * 2, height: r * 1.76)
            context.fill(Path(ellipseIn: rect), with: .color(palette.canopyWash))
        }
    }

    private func drawFoliageLeaves(in context: inout GraphicsContext, nodes: [CanopyNode], unit: CGFloat, palette: TreePalette) {
        guard !nodes.isEmpty else { return }
        var rng = SeededGenerator(seed: 0x4C656166) // "Leaf"
        let density = profile.foliageDensity
        let leavesPerNode = max(2, Int((2.0 + density * 4.2).rounded()))
        let leafSizeBase = unit * (0.044 + 0.012 * (1 - CGFloat(profile.stage.rawValue - 2) / 5.0))

        for node in nodes {
            for idx in 0..<leavesPerNode {
                let threshold = Double(idx) / Double(max(1, leavesPerNode))
                if threshold > density + 0.22 { continue }

                let spread = (CGFloat(rng.nextUnit()) - 0.5) * 2.1
                let radial = unit * (0.01 + CGFloat(rng.nextUnit()) * 0.055)
                let pos = CGPoint(
                    x: node.point.x + cos(node.angle + spread) * radial,
                    y: node.point.y + sin(node.angle + spread) * radial)
                let leafAngle = node.angle + spread * 0.75
                let length = leafSizeBase * (0.75 + CGFloat(rng.nextUnit()) * 0.55)
                let width = length * 0.48
                let colorRoll = rng.nextUnit()
                let fill = colorRoll < 0.34 ? palette.leafDark : (colorRoll < 0.72 ? palette.leafMid : palette.leafLight)
                drawLeaf(in: &context, origin: pos, angle: leafAngle, length: length, width: width,
                         fill: fill, vein: nil)
            }
        }
    }

    private func drawCanopyAccents(in context: inout GraphicsContext, nodes: [CanopyNode], unit: CGFloat, palette: TreePalette) {
        let outerNodes = nodes.filter { $0.depth >= max(1, profile.stage.branchDepth - 1) }
        let pool = outerNodes.isEmpty ? nodes : outerNodes
        guard !pool.isEmpty else { return }

        // Blossoms from annotations / highlights.
        if profile.renderedBlossoms > 0 {
            let blossomSlots = distributedIndices(count: profile.renderedBlossoms, poolSize: pool.count, seed: 0x426C6F73)
            let blossomRadius = max(2.2, unit * 0.021)
            for (i, slot) in blossomSlots.enumerated() {
                let node = pool[slot]
                let offset = CGFloat((i % 3) - 1) * unit * 0.025
                let center = CGPoint(x: node.point.x + offset, y: node.point.y - unit * 0.018)
                drawBlossom(in: &context, at: center, radius: blossomRadius, palette: palette)
            }
        }

        // Fruits from finished books.
        if profile.renderedFruits > 0 {
            let fruitSlots = distributedIndices(count: profile.renderedFruits, poolSize: pool.count, seed: 0x46727569)
            let fruitRadius = max(2.6, unit * 0.027)
            for (i, slot) in fruitSlots.enumerated() {
                let node = pool[slot]
                let xShift = CGFloat((i % 3) - 1) * unit * 0.022
                let yShift = unit * (0.014 + CGFloat(i % 2) * 0.012)
                let center = CGPoint(x: node.point.x + xShift, y: node.point.y + yShift)
                drawFruit(in: &context, at: center, radius: fruitRadius, palette: palette)
            }
        }
    }

    private func drawFloatingMotes(in context: inout GraphicsContext, size: CGSize, groundY: CGFloat, palette: TreePalette) {
        let unit = min(size.width, size.height)
        let points: [(CGFloat, CGFloat, CGFloat)] = [
            (0.18, 0.34, -0.4),
            (0.84, 0.42, 0.5),
            (0.24, 0.62, 0.8),
            (0.79, 0.66, -0.6)
        ]
        for (px, py, angle) in points {
            let origin = CGPoint(x: size.width * px, y: groundY * py)
            drawLeaf(in: &context, origin: origin, angle: angle, length: unit * 0.032, width: unit * 0.016,
                     fill: palette.leafLight.opacity(0.75), vein: nil)
        }
    }

    // MARK: - Primitives

    private func drawLeaf(in context: inout GraphicsContext, origin: CGPoint, angle: CGFloat,
                          length: CGFloat, width: CGFloat, fill: Color, vein: Color?) {
        let tip = CGPoint(x: origin.x + cos(angle) * length, y: origin.y + sin(angle) * length)
        let mid = CGPoint(x: (origin.x + tip.x) * 0.5, y: (origin.y + tip.y) * 0.5)
        let perp = angle + .pi / 2
        let c1 = CGPoint(x: mid.x + cos(perp) * width, y: mid.y + sin(perp) * width)
        let c2 = CGPoint(x: mid.x - cos(perp) * width, y: mid.y - sin(perp) * width)

        var path = Path()
        path.move(to: origin)
        path.addQuadCurve(to: tip, control: c1)
        path.addQuadCurve(to: origin, control: c2)
        path.closeSubpath()

        context.fill(path, with: .color(fill))
        if let vein {
            var midrib = Path()
            midrib.move(to: origin)
            midrib.addLine(to: CGPoint(x: origin.x + cos(angle) * length * 0.78,
                                       y: origin.y + sin(angle) * length * 0.78))
            context.stroke(midrib, with: .color(vein), style: StrokeStyle(lineWidth: 0.7, lineCap: .round))
        }
    }

    private func drawFruit(in context: inout GraphicsContext, at center: CGPoint, radius: CGFloat, palette: TreePalette) {
        var stem = Path()
        stem.move(to: CGPoint(x: center.x, y: center.y - radius * 0.75))
        stem.addLine(to: CGPoint(x: center.x + radius * 0.25, y: center.y - radius * 1.35))
        context.stroke(stem, with: .color(palette.trunk.opacity(0.75)),
                       style: StrokeStyle(lineWidth: 0.85, lineCap: .round))

        let bodyRect = CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
        context.fill(Path(ellipseIn: bodyRect), with: .color(palette.fruit))
        context.stroke(Path(ellipseIn: bodyRect), with: .color(palette.fruitBorder), lineWidth: 0.5)

        let glintR = radius * 0.28
        let glintRect = CGRect(
            x: center.x - radius * 0.42,
            y: center.y - radius * 0.45,
            width: glintR * 2,
            height: glintR * 2)
        context.fill(Path(ellipseIn: glintRect), with: .color(palette.fruitHighlight))
    }

    private func drawBlossom(in context: inout GraphicsContext, at center: CGPoint, radius: CGFloat, palette: TreePalette) {
        let petalR = radius * 0.62
        for idx in 0..<4 {
            let theta = CGFloat(idx) * (.pi / 2) + .pi / 4
            let px = center.x + cos(theta) * (radius * 0.55)
            let py = center.y + sin(theta) * (radius * 0.55)
            let rect = CGRect(x: px - petalR, y: py - petalR, width: petalR * 2, height: petalR * 2)
            context.fill(Path(ellipseIn: rect), with: .color(palette.blossom))
        }
        let coreR = radius * 0.34
        let coreRect = CGRect(x: center.x - coreR, y: center.y - coreR, width: coreR * 2, height: coreR * 2)
        context.fill(Path(ellipseIn: coreRect), with: .color(palette.blossomCore))
    }

    private func distributedIndices(count: Int, poolSize: Int, seed: UInt64) -> [Int] {
        guard count > 0, poolSize > 0 else { return [] }
        var indices = Array(0..<poolSize)
        var rng = SeededGenerator(seed: seed)
        // Fisher-Yates shuffle for balanced canopy distribution without clumping.
        for i in stride(from: indices.count - 1, through: 1, by: -1) {
            let j = Int(rng.next() % UInt64(i + 1))
            indices.swapAt(i, j)
        }
        return (0..<count).map { indices[$0 % indices.count] }
    }
}

// MARK: - Palette & Deterministic PRNG

private struct TreePalette {
    let trunk: Color
    let faintInk: Color
    let groundLine: Color
    let groundWash: Color
    let canopyWash: Color
    let leafDark: Color
    let leafMid: Color
    let leafLight: Color
    let fruit: Color
    let fruitBorder: Color
    let fruitHighlight: Color
    let blossom: Color
    let blossomCore: Color
    let sunHalo: Color
    let sunCore: Color
    let paperHighlight: Color

    init(scheme: ColorScheme, isFullColor: Bool) {
        if !isFullColor {
            trunk = Color.primary.opacity(0.86)
            faintInk = Color.primary.opacity(0.45)
            groundLine = Color.primary.opacity(0.55)
            groundWash = Color.primary.opacity(0.08)
            canopyWash = Color.primary.opacity(0.08)
            leafDark = Color.primary.opacity(0.42)
            leafMid = Color.primary.opacity(0.30)
            leafLight = Color.primary.opacity(0.20)
            fruit = Color.primary.opacity(0.85)
            fruitBorder = Color.primary.opacity(0.95)
            fruitHighlight = Color.primary.opacity(0.25)
            blossom = Color.primary.opacity(0.55)
            blossomCore = Color.primary.opacity(0.90)
            sunHalo = Color.primary.opacity(0.08)
            sunCore = Color.primary.opacity(0.22)
            paperHighlight = Color.primary.opacity(0.28)
            return
        }

        if scheme == .dark {
            trunk = Color(red: 0.78, green: 0.76, blue: 0.71)
            faintInk = KkTheme.faintInk(scheme)
            groundLine = Color.white.opacity(0.28)
            groundWash = Color.white.opacity(0.05)
            canopyWash = Color(red: 0.36, green: 0.50, blue: 0.40).opacity(0.16)
            leafDark = Color(red: 0.34, green: 0.50, blue: 0.39).opacity(0.85)
            leafMid = Color(red: 0.46, green: 0.62, blue: 0.50).opacity(0.82)
            leafLight = Color(red: 0.59, green: 0.73, blue: 0.61).opacity(0.80)
            fruit = Color(red: 0.87, green: 0.45, blue: 0.31)
            fruitBorder = Color(red: 0.64, green: 0.28, blue: 0.18).opacity(0.75)
            fruitHighlight = Color(red: 0.98, green: 0.80, blue: 0.68).opacity(0.75)
            blossom = Color(red: 0.92, green: 0.82, blue: 0.75).opacity(0.92)
            blossomCore = Color(red: 0.82, green: 0.55, blue: 0.40)
            sunHalo = Color(red: 0.86, green: 0.72, blue: 0.46).opacity(0.14)
            sunCore = Color(red: 0.92, green: 0.77, blue: 0.50).opacity(0.42)
            paperHighlight = Color(red: 0.12, green: 0.12, blue: 0.12).opacity(0.65)
        } else {
            trunk = Color(red: 0.18, green: 0.16, blue: 0.14)
            faintInk = KkTheme.faintInk(scheme)
            groundLine = Color.black.opacity(0.26)
            groundWash = Color.black.opacity(0.04)
            canopyWash = Color(red: 0.43, green: 0.56, blue: 0.46).opacity(0.14)
            leafDark = Color(red: 0.28, green: 0.43, blue: 0.33).opacity(0.86)
            leafMid = Color(red: 0.40, green: 0.55, blue: 0.44).opacity(0.82)
            leafLight = Color(red: 0.54, green: 0.67, blue: 0.56).opacity(0.80)
            fruit = Color(red: 0.79, green: 0.34, blue: 0.22)
            fruitBorder = Color(red: 0.55, green: 0.20, blue: 0.12).opacity(0.65)
            fruitHighlight = Color(red: 0.97, green: 0.78, blue: 0.67).opacity(0.85)
            blossom = Color(red: 0.93, green: 0.79, blue: 0.73).opacity(0.95)
            blossomCore = Color(red: 0.74, green: 0.40, blue: 0.28)
            sunHalo = Color(red: 0.85, green: 0.68, blue: 0.38).opacity(0.16)
            sunCore = Color(red: 0.87, green: 0.66, blue: 0.34).opacity(0.46)
            paperHighlight = Color.white.opacity(0.55)
        }
    }
}

private struct SeededGenerator {
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
