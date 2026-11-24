import SwiftUI
import WidgetKit

/// Botanical ink-and-paper tree rendered with SwiftUI Canvas. Growth stages,
/// foliage fullness, flower-to-fruit progression ("花果同枝"), and daily/streak
/// vitality accents are all driven deterministically by `KkReadingTreeProfile`.
struct KkReadingTreeCanvas: View {
    let profile: KkReadingTreeProfile
    @Environment(\.colorScheme) private var scheme
    @Environment(\.widgetRenderingMode) private var renderingMode

    private var isFullColor: Bool { renderingMode == .fullColor }

    var body: some View {
        Canvas { context, size in
            guard size.width > 12, size.height > 12 else { return }
            let palette = TreePalette(scheme: scheme, isFullColor: isFullColor, atmosphere: profile.atmosphere)
            let viewport = KkTreeSkeleton.viewport(in: size)
            let rect = viewport.frame
            let unit = viewport.unit
            let groundY = rect.minY + rect.height * 0.87
            let centerX = rect.midX
            let sun = KkTreeSkeleton.sunPlacement(in: size)

            drawGround(in: &context, rect: rect, groundY: groundY, palette: palette)

            switch profile.stage {
            case .seed:
                drawSeed(in: &context, centerX: centerX, groundY: groundY, unit: unit, palette: palette)
            case .sprout:
                drawSprout(in: &context, centerX: centerX, groundY: groundY, unit: unit, palette: palette)
            case .seedling, .sapling, .leafy, .lush, .ancient:
                drawTree(in: &context, size: size, unit: unit, sun: sun, palette: palette)
            }

            if profile.isThriving && profile.stage.rawValue >= KkTreeStage.seedling.rawValue {
                drawFloatingMotes(in: &context, rect: rect, groundY: groundY, unit: unit, sun: sun, palette: palette)
            }

            if profile.atmosphere.enabled {
                drawSeasonalParticles(in: &context, rect: rect, groundY: groundY, unit: unit, sun: sun, palette: palette)
            }

            // Draw sun (light), crescent moon (dark), or Mid-Autumn full moon unobstructed in upper-right sky.
            if profile.hasReadToday || profile.atmosphere.showsFullMoon {
                drawCelestialBody(in: &context, sun: sun, isDark: scheme == .dark, palette: palette)
            }
        }
        .accessibilityHidden(true)
    }

    // MARK: - Atmosphere & Ground

    private func drawCelestialBody(in context: inout GraphicsContext, sun: KkSunPlacement, isDark: Bool, palette: TreePalette) {
        let haloRect = CGRect(
            x: sun.center.x - sun.haloRadius,
            y: sun.center.y - sun.haloRadius,
            width: sun.haloRadius * 2,
            height: sun.haloRadius * 2)
        context.fill(
            Path(ellipseIn: haloRect),
            with: .radialGradient(
                Gradient(colors: [palette.celestialHalo, palette.celestialHalo.opacity(0.0)]),
                center: sun.center,
                startRadius: sun.coreRadius * 0.7,
                endRadius: sun.haloRadius))

        if profile.atmosphere.showsFullMoon {
            let fullRadius = sun.coreRadius * 1.12
            let coreRect = CGRect(
                x: sun.center.x - fullRadius,
                y: sun.center.y - fullRadius,
                width: fullRadius * 2,
                height: fullRadius * 2)
            context.fill(Path(ellipseIn: coreRect), with: .color(palette.fullMoonCore))
            return
        }

        switch KkTreeSkeleton.celestialKind(isDark: isDark) {
        case .sun:
            let coreRect = CGRect(
                x: sun.center.x - sun.coreRadius,
                y: sun.center.y - sun.coreRadius,
                width: sun.coreRadius * 2,
                height: sun.coreRadius * 2)
            context.fill(Path(ellipseIn: coreRect), with: .color(palette.celestialCore))
        case .moon:
            let moonPath = Path(KkTreeSkeleton.crescentMoonCGPath(center: sun.center, radius: sun.coreRadius * 1.18))
            context.fill(moonPath, with: .color(palette.celestialCore))
        }
    }

    private func drawGround(in context: inout GraphicsContext, rect: CGRect, groundY: CGFloat, palette: TreePalette) {
        let span = rect.width * 0.72
        let startX = rect.minX + (rect.width - span) * 0.5
        let endX = startX + span

        var mound = Path()
        mound.move(to: CGPoint(x: startX, y: groundY))
        mound.addQuadCurve(
            to: CGPoint(x: endX, y: groundY),
            control: CGPoint(x: rect.midX, y: groundY - max(4, rect.width * 0.04)))
        context.stroke(mound, with: .color(palette.groundLine),
                       style: StrokeStyle(lineWidth: 1.1, lineCap: .round))

        var wash = mound
        wash.addQuadCurve(
            to: CGPoint(x: startX, y: groundY),
            control: CGPoint(x: rect.midX, y: groundY + max(5, rect.width * 0.05)))
        wash.closeSubpath()
        context.fill(wash, with: .color(palette.groundWash))

        // Tiny grass tufts anchored on the earth line.
        let tuftOffsets: [(CGFloat, CGFloat)] = [(-0.22, 0.8), (-0.16, 0.6), (0.19, 0.75), (0.25, 0.55)]
        for (offset, scale) in tuftOffsets {
            let x = rect.midX + span * offset
            let h = max(3, rect.width * 0.032) * scale
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
                          unit: CGFloat, palette: TreePalette) {
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
                            unit: CGFloat, palette: TreePalette) {
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
                drawRipeFruit(in: &context, at: CGPoint(x: fx, y: fy), radius: fruitR, heirloom: false, palette: palette)
            }
        }
    }

    // MARK: - Procedural Tree (Seedling -> Ancient)

    private func drawTree(in context: inout GraphicsContext, size: CGSize, unit: CGFloat, sun: KkSunPlacement, palette: TreePalette) {
        let skeleton = KkTreeSkeleton.generate(profile: profile, size: size)

        // 1. Back-layer shaded foliage tucked behind the branches (crisp leaves only, no backdrop circles).
        drawFoliageLeaves(
            in: &context,
            nodes: skeleton.canopyNodes,
            sun: sun,
            unit: unit,
            palette: palette,
            backLayer: true)

        // 2. Root flare, tapered trunk, spreading boughs, and drooping outer twigs.
        if profile.stage.rawValue >= KkTreeStage.sapling.rawValue {
            drawRootFlare(in: &context, root: skeleton.rootPoint, thickness: skeleton.baseThickness, palette: palette)
        }
        for segment in skeleton.branches {
            drawTaperedSegment(in: &context, segment: segment, palette: palette)
        }

        // 3. Front sunlit leaf clusters and hanging leaf sprays along drooping twigs.
        drawFoliageLeaves(
            in: &context,
            nodes: skeleton.canopyNodes,
            sun: sun,
            unit: unit,
            palette: palette,
            backLayer: false)

        // 4. Flower-to-fruit lifecycle accents ("花果同枝") nestled along outer & drooping boughs.
        drawCanopyAccents(
            in: &context,
            nodes: skeleton.canopyNodes,
            sun: sun,
            unit: unit,
            palette: palette)

        // 5. Seasonal foliage snow, festival charms, and TTS songbird companions.
        drawTreeDecorations(
            in: &context,
            skeleton: skeleton,
            unit: unit,
            sun: sun,
            palette: palette)
    }

    private func drawRootFlare(in context: inout GraphicsContext, root: CGPoint, thickness: CGFloat, palette: TreePalette) {
        let flareSpread: CGFloat = profile.stage.rawValue >= KkTreeStage.lush.rawValue ? 1.48 : 1.22
        var flare = Path()
        flare.move(to: CGPoint(x: root.x - thickness * flareSpread, y: root.y + 0.6))
        flare.addQuadCurve(
            to: CGPoint(x: root.x - thickness * 0.38, y: root.y - thickness * 1.55),
            control: CGPoint(x: root.x - thickness * 0.56, y: root.y - thickness * 0.12))
        flare.addLine(to: CGPoint(x: root.x + thickness * 0.38, y: root.y - thickness * 1.55))
        flare.addQuadCurve(
            to: CGPoint(x: root.x + thickness * flareSpread, y: root.y + 0.6),
            control: CGPoint(x: root.x + thickness * 0.56, y: root.y - thickness * 0.12))
        flare.closeSubpath()
        context.fill(flare, with: .color(palette.trunk))
    }

    /// Renders a smoothly tapered organic limb along a quadratic Bezier curve.
    private func drawTaperedSegment(in context: inout GraphicsContext, segment: KkTreeBranch, palette: TreePalette) {
        let alpha = segment.depth <= 1 ? 1.0 : max(0.76, 1.0 - Double(segment.depth - 1) * 0.06)
        let color = palette.trunk.opacity(alpha)

        // Slender terminal twigs render crisply as rounded strokes.
        if segment.startWidth <= 1.6 {
            var path = Path()
            path.move(to: segment.start)
            path.addQuadCurve(to: segment.end, control: segment.control)
            context.stroke(
                path,
                with: .color(color),
                style: StrokeStyle(lineWidth: (segment.startWidth + segment.endWidth) * 0.5, lineCap: .round, lineJoin: .round))
            return
        }

        let steps = 7
        var leftEdge: [CGPoint] = []
        var rightEdge: [CGPoint] = []
        leftEdge.reserveCapacity(steps + 1)
        rightEdge.reserveCapacity(steps + 1)

        for i in 0...steps {
            let t = CGFloat(i) / CGFloat(steps)
            let mt = 1 - t
            let px = mt * mt * segment.start.x + 2 * mt * t * segment.control.x + t * t * segment.end.x
            let py = mt * mt * segment.start.y + 2 * mt * t * segment.control.y + t * t * segment.end.y
            let tx = 2 * mt * (segment.control.x - segment.start.x) + 2 * t * (segment.end.x - segment.control.x)
            let ty = 2 * mt * (segment.control.y - segment.start.y) + 2 * t * (segment.end.y - segment.control.y)
            let norm = max(0.001, hypot(tx, ty))
            let nx = -ty / norm
            let ny = tx / norm
            let halfW = (segment.startWidth + (segment.endWidth - segment.startWidth) * t) * 0.5
            leftEdge.append(CGPoint(x: px + nx * halfW, y: py + ny * halfW))
            rightEdge.append(CGPoint(x: px - nx * halfW, y: py - ny * halfW))
        }

        var polygon = Path()
        polygon.move(to: leftEdge[0])
        for point in leftEdge.dropFirst() { polygon.addLine(to: point) }
        for point in rightEdge.reversed() { polygon.addLine(to: point) }
        polygon.closeSubpath()
        context.fill(polygon, with: .color(color))

        // Rounded joint caps prevent tiny notch artifacts at branch forks.
        let endR = segment.endWidth * 0.5
        context.fill(
            Path(ellipseIn: CGRect(x: segment.end.x - endR, y: segment.end.y - endR, width: endR * 2, height: endR * 2)),
            with: .color(color))
    }

    private func isInsideSunDisc(_ point: CGPoint, sun: KkSunPlacement, padding: CGFloat) -> Bool {
        hypot(point.x - sun.center.x, point.y - sun.center.y) < sun.coreRadius + padding
    }

    private func drawFoliageLeaves(
        in context: inout GraphicsContext,
        nodes: [KkTreeCanopyNode],
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette,
        backLayer: Bool
    ) {
        guard !nodes.isEmpty else { return }
        var rng = KkSeededGenerator(seed: backLayer ? 0x4261636B : 0x4C656166)
        let density = profile.foliageDensity
        let perNodeBase = backLayer ? (2.3 + density * 3.5) : (3.0 + density * 4.5)
        let leavesPerNode = max(2, Int(perNodeBase.rounded()))
        let leafSizeBase = unit * (backLayer ? 0.056 : 0.044)
        let sunExclusion = sun.coreRadius + leafSizeBase * 1.15

        for node in nodes {
            for idx in 0..<leavesPerNode {
                let threshold = Double(idx) / Double(max(1, leavesPerNode))
                if threshold > density + 0.28 { continue }

                let spread = (CGFloat(rng.nextUnit()) - 0.5) * (node.isDrooping ? 1.50 : 2.50)
                let radial = unit * (0.008 + CGFloat(rng.nextUnit()) * (node.isDrooping ? 0.050 : 0.078)) * node.weight
                let pos = CGPoint(
                    x: node.point.x + cos(node.angle + spread) * radial,
                    y: node.point.y + sin(node.angle + spread) * radial)
                // Drooping sprays angle their leaves gently downward like willow/banyan shoots.
                let leafAngle = node.isDrooping
                    ? (node.angle * 0.65 + (.pi * 0.5) * 0.35 + (CGFloat(rng.nextUnit()) - 0.5) * 0.65)
                    : (node.angle + spread * 0.72)
                let length = leafSizeBase * (0.76 + CGFloat(rng.nextUnit()) * 0.52)
                let width = length * 0.52
                let tip = CGPoint(x: pos.x + cos(leafAngle) * length, y: pos.y + sin(leafAngle) * length)
                let mid = CGPoint(x: (pos.x + tip.x) * 0.5, y: (pos.y + tip.y) * 0.5)
                if isInsideSunDisc(pos, sun: sun, padding: sun.coreRadius * 0.9)
                    || hypot(tip.x - sun.center.x, tip.y - sun.center.y) < sunExclusion
                    || hypot(mid.x - sun.center.x, mid.y - sun.center.y) < sunExclusion {
                    continue
                }

                let colorRoll = rng.nextUnit()
                let fill: Color
                if backLayer {
                    fill = colorRoll < 0.65 ? palette.leafDark : palette.leafMid
                } else {
                    fill = colorRoll < 0.24 ? palette.leafDark : (colorRoll < 0.68 ? palette.leafMid : palette.leafLight)
                }
                drawLeaf(in: &context, origin: pos, angle: leafAngle, length: length, width: width,
                         fill: fill, vein: nil)
            }
        }
    }

    private func drawCanopyAccents(
        in context: inout GraphicsContext,
        nodes: [KkTreeCanopyNode],
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let accents = profile.accents
        guard !accents.isEmpty else { return }

        let outerNodes = nodes.filter { $0.depth >= max(1, profile.stage.branchDepth - 1) || $0.isDrooping }
        let pool = outerNodes.isEmpty ? nodes : outerNodes
        guard !pool.isEmpty else { return }

        // Separate blossom-bearing upper/sunlit nodes from fruit-hanging drooping/lower nodes,
        // and sample evenly by horizontal/angular span so left and right boughs stay balanced.
        let sortedY = pool.map(\.point.y).sorted()
        let medianY = sortedY[sortedY.count / 2]
        let blossomPool = pool.filter { !$0.isDrooping && $0.point.y <= medianY + unit * 0.05 }
        let fruitPool = pool.filter { $0.isDrooping || $0.point.y >= medianY - unit * 0.03 }

        let activeBlossomPool = balancedSpanOrder(blossomPool.isEmpty ? pool : blossomPool)
        let activeFruitPool = balancedSpanOrder(fruitPool.isEmpty ? pool : fruitPool)

        let blossomAccents = accents.filter { $0.kind.isBlossomLike }
        let fruitAccents = accents.filter { $0.kind.isFruitLike }

        var placedPoints: [CGPoint] = []
        let minSpacing = max(4.6, unit * 0.052)

        func nonOverlappingCenter(candidate: CGPoint) -> CGPoint {
            var pt = candidate
            if isInsideSunDisc(pt, sun: sun, padding: sun.coreRadius * 1.35) {
                pt.x = sun.center.x - sun.clearanceRadius
                pt.y = max(pt.y, sun.center.y + sun.coreRadius)
            }
            for step in 0..<5 {
                let tooClose = placedPoints.contains { hypot($0.x - pt.x, $0.y - pt.y) < minSpacing }
                if !tooClose { break }
                let nudgeAngle = CGFloat(step) * 1.35 + 0.5
                pt.x += cos(nudgeAngle) * minSpacing * 0.65
                pt.y += sin(nudgeAngle) * minSpacing * 0.45
            }
            placedPoints.append(pt)
            return pt
        }

        // 1. Buds and full blossoms perched evenly across upper outer twigs.
        if !blossomAccents.isEmpty {
            let slots = evenlySpacedIndices(count: blossomAccents.count, poolSize: activeBlossomPool.count, offset: 0)
            let baseR = max(2.4, unit * 0.025)
            for (i, accent) in blossomAccents.enumerated() {
                let node = activeBlossomPool[slots[i]]
                let offset = CGFloat((i % 3) - 1) * unit * 0.018
                let rawCenter = CGPoint(x: node.point.x + offset, y: node.point.y - unit * 0.011)
                let center = nonOverlappingCenter(candidate: rawCenter)
                switch accent.kind {
                case .bud:
                    drawFlowerBud(
                        in: &context,
                        at: center,
                        angle: node.angle,
                        radius: baseR * 0.96,
                        maturity: accent.maturity,
                        palette: palette)
                case .blossom:
                    drawBlossom(
                        in: &context,
                        at: center,
                        radius: baseR * CGFloat(0.96 + 0.16 * accent.maturity),
                        rotation: CGFloat(i) * 0.55,
                        palette: palette)
                default:
                    break
                }
            }
        }

        // 2. Developing young fruits, ripening fruits, and ripe/heirloom fruits hanging across both sides.
        if !fruitAccents.isEmpty {
            let slots = evenlySpacedIndices(count: fruitAccents.count, poolSize: activeFruitPool.count, offset: 1)
            let fruitBaseR = max(2.7, unit * 0.030)
            for (i, accent) in fruitAccents.enumerated() {
                let node = activeFruitPool[slots[i]]
                let xShift = CGFloat((i % 3) - 1) * unit * 0.018
                let yShift = unit * (0.016 + CGFloat(i % 2) * 0.010)
                let rawCenter = CGPoint(x: node.point.x + xShift, y: node.point.y + yShift)
                let center = nonOverlappingCenter(candidate: rawCenter)

                switch accent.kind {
                case .youngFruit:
                    drawYoungFruit(
                        in: &context,
                        at: center,
                        radius: fruitBaseR * CGFloat(0.72 + 0.14 * accent.maturity),
                        hasDriftingPetal: accent.hasDriftingPetal,
                        palette: palette)
                case .ripeningFruit:
                    drawRipeningFruit(
                        in: &context,
                        at: center,
                        radius: fruitBaseR * CGFloat(0.86 + 0.12 * accent.maturity),
                        maturity: accent.maturity,
                        palette: palette)
                case .ripeFruit:
                    drawRipeFruit(
                        in: &context,
                        at: center,
                        radius: fruitBaseR,
                        heirloom: false,
                        palette: palette)
                case .heirloomFruit:
                    drawRipeFruit(
                        in: &context,
                        at: center,
                        radius: fruitBaseR * 1.08,
                        heirloom: true,
                        palette: palette)
                default:
                    break
                }
            }
        }
    }

    private func drawFloatingMotes(in context: inout GraphicsContext, rect: CGRect, groundY: CGFloat, unit: CGFloat, sun: KkSunPlacement, palette: TreePalette) {
        let points: [(CGFloat, CGFloat, CGFloat)] = [
            (0.16, 0.34, -0.4),
            (0.88, 0.48, 0.5),
            (0.22, 0.64, 0.8),
            (0.82, 0.70, -0.6)
        ]
        for (px, py, angle) in points {
            let origin = CGPoint(x: rect.minX + rect.width * px, y: rect.minY + (groundY - rect.minY) * py)
            if isInsideSunDisc(origin, sun: sun, padding: sun.coreRadius * 1.6) { continue }
            drawLeaf(in: &context, origin: origin, angle: angle, length: unit * 0.030, width: unit * 0.015,
                     fill: palette.leafLight.opacity(0.75), vein: nil)
        }
    }

    // MARK: - Botanical Primitives

    func drawLeaf(in context: inout GraphicsContext, origin: CGPoint, angle: CGFloat,
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

    /// Closed/half-opening pink bud cupped by green sepals at the tip of a twiglet.
    private func drawFlowerBud(
        in context: inout GraphicsContext,
        at center: CGPoint,
        angle: CGFloat,
        radius: CGFloat,
        maturity: Double,
        palette: TreePalette
    ) {
        let dir = sin(angle) > 0.25 ? -.pi * 0.38 : angle
        let ux = cos(dir)
        let uy = sin(dir)
        let px = -uy
        let py = ux

        // Tiny stem.
        let base = CGPoint(x: center.x - ux * radius * 0.65, y: center.y - uy * radius * 0.65)
        let stalkStart = CGPoint(x: base.x - ux * radius * 0.55, y: base.y - uy * radius * 0.55)
        var stem = Path()
        stem.move(to: stalkStart)
        stem.addLine(to: base)
        context.stroke(stem, with: .color(palette.leafDark), style: StrokeStyle(lineWidth: 0.85, lineCap: .round))

        // Swelling teardrop bud.
        let spread = CGFloat(0.58 + maturity * 0.16)
        let tip = CGPoint(x: base.x + ux * radius * 1.45, y: base.y + uy * radius * 1.45)
        let midX = base.x + ux * radius * 0.65
        let midY = base.y + uy * radius * 0.65
        let spreadPx = px * radius * spread
        let spreadPy = py * radius * spread
        let budCtrl1 = CGPoint(x: midX + spreadPx, y: midY + spreadPy)
        let budCtrl2 = CGPoint(x: midX - spreadPx, y: midY - spreadPy)
        var bud = Path()
        bud.move(to: base)
        bud.addQuadCurve(to: tip, control: budCtrl1)
        bud.addQuadCurve(to: base, control: budCtrl2)
        bud.closeSubpath()
        context.fill(bud, with: .color(palette.budTip))

        // Cupping green sepal leaves around the base of the bud.
        let sepalTipForwardX = base.x + ux * radius * 0.62
        let sepalTipForwardY = base.y + uy * radius * 0.62
        let sepalCtrlForwardX = base.x + ux * radius * 0.25
        let sepalCtrlForwardY = base.y + uy * radius * 0.25
        let sepalMid = CGPoint(x: base.x + ux * radius * 0.35, y: base.y + uy * radius * 0.35)
        for side in [-1.0 as CGFloat, 1.0 as CGFloat] {
            let sideRadius = side * radius
            let sepalTip = CGPoint(
                x: sepalTipForwardX + px * sideRadius * 0.46,
                y: sepalTipForwardY + py * sideRadius * 0.46)
            let sepalCtrl = CGPoint(
                x: sepalCtrlForwardX + px * sideRadius * 0.42,
                y: sepalCtrlForwardY + py * sideRadius * 0.42)
            var sepal = Path()
            sepal.move(to: base)
            sepal.addQuadCurve(to: sepalTip, control: sepalCtrl)
            sepal.addLine(to: sepalMid)
            sepal.closeSubpath()
            context.fill(sepal, with: .color(palette.leafDark))
        }
    }

    /// 5-petal blossom with sunlit tint and dotted golden stamens.
    private func drawBlossom(
        in context: inout GraphicsContext,
        at center: CGPoint,
        radius: CGFloat,
        rotation: CGFloat,
        palette: TreePalette
    ) {
        let petalCount = 5
        for idx in 0..<petalCount {
            let theta = rotation + CGFloat(idx) * ((.pi * 2) / CGFloat(petalCount))
            let basePt = CGPoint(x: center.x + cos(theta) * (radius * 0.14), y: center.y + sin(theta) * (radius * 0.14))
            let tipPt = CGPoint(x: center.x + cos(theta) * (radius * 1.15), y: center.y + sin(theta) * (radius * 1.15))
            let midPt = CGPoint(x: (basePt.x + tipPt.x) * 0.55, y: (basePt.y + tipPt.y) * 0.55)
            let perp = theta + .pi / 2
            let petalHalfW = radius * 0.54

            var petal = Path()
            petal.move(to: basePt)
            petal.addQuadCurve(
                to: tipPt,
                control: CGPoint(x: midPt.x + cos(perp) * petalHalfW, y: midPt.y + sin(perp) * petalHalfW))
            petal.addQuadCurve(
                to: basePt,
                control: CGPoint(x: midPt.x - cos(perp) * petalHalfW, y: midPt.y - sin(perp) * petalHalfW))
            petal.closeSubpath()
            context.fill(petal, with: .color(palette.blossom))
        }

        let coreR = radius * 0.34
        let coreRect = CGRect(x: center.x - coreR, y: center.y - coreR, width: coreR * 2, height: coreR * 2)
        context.fill(Path(ellipseIn: coreRect), with: .color(palette.blossomCore))

        // Delicate stamen dots encircling the golden heart.
        let stamenR = max(0.45, radius * 0.10)
        for idx in 0..<5 {
            let theta = rotation + CGFloat(idx) * ((.pi * 2) / 5) + 0.3
            let sx = center.x + cos(theta) * (radius * 0.44)
            let sy = center.y + sin(theta) * (radius * 0.44)
            context.fill(
                Path(ellipseIn: CGRect(x: sx - stamenR, y: sy - stamenR, width: stamenR * 2, height: stamenR * 2)),
                with: .color(palette.blossomStamen))
        }
    }

    /// Small tender green young fruit right after petal fall, with persistent calyx and optional falling petal.
    private func drawYoungFruit(
        in context: inout GraphicsContext,
        at center: CGPoint,
        radius: CGFloat,
        hasDriftingPetal: Bool,
        palette: TreePalette
    ) {
        // Curved twiglet stem from branch above.
        var stem = Path()
        stem.move(to: CGPoint(x: center.x, y: center.y - radius * 0.72))
        stem.addQuadCurve(
            to: CGPoint(x: center.x + radius * 0.18, y: center.y - radius * 1.45),
            control: CGPoint(x: center.x - radius * 0.10, y: center.y - radius * 1.10))
        context.stroke(stem, with: .color(palette.trunk.opacity(0.75)),
                       style: StrokeStyle(lineWidth: 0.8, lineCap: .round))

        let bodyRect = CGRect(
            x: center.x - radius * 0.94,
            y: center.y - radius,
            width: radius * 1.88,
            height: radius * 2.02)
        context.fill(
            Path(ellipseIn: bodyRect),
            with: .radialGradient(
                Gradient(colors: [palette.youngFruitLight, palette.youngFruit]),
                center: CGPoint(x: center.x + radius * 0.30, y: center.y - radius * 0.30),
                startRadius: 0,
                endRadius: radius * 1.25))
        context.stroke(Path(ellipseIn: bodyRect), with: .color(palette.youngFruitBorder), lineWidth: 0.45)

        // Persistent blossom calyx star at bottom of young fruit.
        let calyxCenter = CGPoint(x: center.x, y: center.y + radius * 0.92)
        for dx in [-0.36 as CGFloat, 0.0, 0.36] {
            var tick = Path()
            tick.move(to: calyxCenter)
            tick.addLine(to: CGPoint(x: calyxCenter.x + radius * dx, y: calyxCenter.y + radius * 0.28))
            context.stroke(tick, with: .color(palette.youngFruitBorder), style: StrokeStyle(lineWidth: 0.6, lineCap: .round))
        }

        // Tiny drifting pink petal beside newly set green fruit.
        if hasDriftingPetal {
            let petalPos = CGPoint(x: center.x + radius * 1.25, y: center.y + radius * 0.95)
            drawLeaf(
                in: &context,
                origin: petalPos,
                angle: 0.95,
                length: radius * 0.85,
                width: radius * 0.42,
                fill: palette.blossom.opacity(0.88),
                vein: nil)
        }
    }

    /// Ripening fruit transitioning in a sunlit gradient from chartreuse/amber to coral blush.
    private func drawRipeningFruit(
        in context: inout GraphicsContext,
        at center: CGPoint,
        radius: CGFloat,
        maturity: Double,
        palette: TreePalette
    ) {
        var stem = Path()
        let stemTop = CGPoint(x: center.x + radius * 0.20, y: center.y - radius * 1.42)
        stem.move(to: CGPoint(x: center.x, y: center.y - radius * 0.76))
        stem.addQuadCurve(
            to: stemTop,
            control: CGPoint(x: center.x - radius * 0.08, y: center.y - radius * 1.15))
        context.stroke(stem, with: .color(palette.trunk.opacity(0.75)),
                       style: StrokeStyle(lineWidth: 0.85, lineCap: .round))

        let w = radius * 2.04
        let h = radius * 1.94
        let bodyRect = CGRect(x: center.x - w * 0.5, y: center.y - h * 0.5, width: w, height: h)
        let warmColor = maturity > 0.5 ? palette.ripeningBlush : palette.ripeningAmber

        context.fill(
            Path(ellipseIn: bodyRect),
            with: .linearGradient(
                Gradient(colors: [warmColor, palette.ripeningBase]),
                startPoint: CGPoint(x: bodyRect.maxX, y: bodyRect.minY),
                endPoint: CGPoint(x: bodyRect.minX, y: bodyRect.maxY)))
        context.stroke(Path(ellipseIn: bodyRect), with: .color(palette.ripeningBorder), lineWidth: 0.5)

        let glintR = radius * 0.25
        let glintRect = CGRect(
            x: center.x + radius * 0.16,
            y: center.y - radius * 0.50,
            width: glintR * 2,
            height: glintR * 2)
        context.fill(Path(ellipseIn: glintRect), with: .color(palette.fruitHighlight))
    }

    /// Full ripe (or prized heirloom) fruit with woody stem, tiny stem leaf, sunlit volume & bottom calyx.
    private func drawRipeFruit(
        in context: inout GraphicsContext,
        at center: CGPoint,
        radius: CGFloat,
        heirloom: Bool,
        palette: TreePalette
    ) {
        let stemTop = CGPoint(x: center.x + radius * 0.22, y: center.y - radius * 1.42)
        var stem = Path()
        stem.move(to: CGPoint(x: center.x, y: center.y - radius * 0.76))
        stem.addQuadCurve(
            to: stemTop,
            control: CGPoint(x: center.x - radius * 0.06, y: center.y - radius * 1.15))
        context.stroke(stem, with: .color(palette.trunk.opacity(0.80)),
                       style: StrokeStyle(lineWidth: 0.9, lineCap: .round))

        // Tiny crisp stem leaf sprouting beside the stalk.
        drawLeaf(
            in: &context,
            origin: CGPoint(x: center.x + radius * 0.08, y: center.y - radius * 1.12),
            angle: -0.52,
            length: radius * 0.92,
            width: radius * 0.36,
            fill: palette.leafMid,
            vein: nil)

        // Subtle organic slightly wider-than-tall fruit silhouette.
        let w = radius * 2.08
        let h = radius * 1.94
        let bodyRect = CGRect(x: center.x - w * 0.5, y: center.y - h * 0.5, width: w, height: h)
        let sunColor = heirloom ? palette.heirloomSun : palette.fruitSun
        let deepColor = heirloom ? palette.heirloomDeep : palette.fruit

        context.fill(
            Path(ellipseIn: bodyRect),
            with: .radialGradient(
                Gradient(colors: [sunColor, deepColor]),
                center: CGPoint(x: center.x + radius * 0.28, y: center.y - radius * 0.28),
                startRadius: 0,
                endRadius: radius * 1.28))
        context.stroke(
            Path(ellipseIn: bodyRect),
            with: .color(heirloom ? palette.heirloomBorder : palette.fruitBorder),
            lineWidth: 0.5)

        // Specular highlight toward upper-right sun + tiny calyx dimple at base.
        let glintR = radius * 0.27
        let glintRect = CGRect(
            x: center.x + radius * 0.16,
            y: center.y - radius * 0.52,
            width: glintR * 2,
            height: glintR * 2)
        context.fill(Path(ellipseIn: glintRect), with: .color(palette.fruitHighlight))

        let calyxR = max(0.55, radius * 0.13)
        let calyxRect = CGRect(x: center.x - calyxR, y: center.y + radius * 0.80, width: calyxR * 2, height: calyxR)
        context.fill(Path(ellipseIn: calyxRect), with: .color(palette.fruitBorder.opacity(0.75)))
    }

    private func balancedSpanOrder(_ nodes: [KkTreeCanopyNode]) -> [KkTreeCanopyNode] {
        nodes.sorted {
            if abs($0.point.x - $1.point.x) > 0.5 { return $0.point.x < $1.point.x }
            return $0.point.y < $1.point.y
        }
    }

    private func evenlySpacedIndices(count: Int, poolSize: Int, offset: Int) -> [Int] {
        guard count > 0, poolSize > 0 else { return [] }
        if poolSize == 1 { return Array(repeating: 0, count: count) }
        var indices: [Int] = []
        indices.reserveCapacity(count)
        let strideStep = Double(poolSize) / Double(count)
        for i in 0..<count {
            let raw = Int((Double(i) + 0.5) * strideStep + Double(offset)) % poolSize
            indices.append(raw)
        }
        return indices
    }
}

// MARK: - Palette

struct TreePalette {
    let trunk: Color
    let faintInk: Color
    let groundLine: Color
    let groundWash: Color
    let leafDark: Color
    let leafMid: Color
    let leafLight: Color
    let budTip: Color
    let blossom: Color
    let blossomCore: Color
    let blossomStamen: Color
    let youngFruitLight: Color
    let youngFruit: Color
    let youngFruitBorder: Color
    let ripeningBase: Color
    let ripeningAmber: Color
    let ripeningBlush: Color
    let ripeningBorder: Color
    let fruitSun: Color
    let fruit: Color
    let fruitBorder: Color
    let fruitHighlight: Color
    let heirloomSun: Color
    let heirloomDeep: Color
    let heirloomBorder: Color
    let celestialHalo: Color
    let celestialCore: Color
    let fullMoonCore: Color
    let paperHighlight: Color
    let birdBody: Color
    let birdWing: Color
    let birdTail: Color
    let birdBeak: Color
    let soundWave: Color
    let snowAccent: Color
    let festiveRed: Color
    let festiveGold: Color
    let festiveBlue: Color

    init(scheme: ColorScheme, isFullColor: Bool, atmosphere: KkTreeAtmosphere = .standard) {
        if !isFullColor {
            trunk = Color.primary.opacity(0.86)
            faintInk = Color.primary.opacity(0.45)
            groundLine = Color.primary.opacity(0.55)
            groundWash = Color.primary.opacity(0.08)
            leafDark = Color.primary.opacity(0.42)
            leafMid = Color.primary.opacity(0.30)
            leafLight = Color.primary.opacity(0.20)
            budTip = Color.primary.opacity(0.62)
            blossom = Color.primary.opacity(0.55)
            blossomCore = Color.primary.opacity(0.90)
            blossomStamen = Color.primary.opacity(0.70)
            youngFruitLight = Color.primary.opacity(0.36)
            youngFruit = Color.primary.opacity(0.52)
            youngFruitBorder = Color.primary.opacity(0.75)
            ripeningBase = Color.primary.opacity(0.56)
            ripeningAmber = Color.primary.opacity(0.68)
            ripeningBlush = Color.primary.opacity(0.76)
            ripeningBorder = Color.primary.opacity(0.84)
            fruitSun = Color.primary.opacity(0.72)
            fruit = Color.primary.opacity(0.88)
            fruitBorder = Color.primary.opacity(0.95)
            fruitHighlight = Color.primary.opacity(0.25)
            heirloomSun = Color.primary.opacity(0.80)
            heirloomDeep = Color.primary.opacity(0.95)
            heirloomBorder = Color.primary.opacity(0.98)
            celestialHalo = scheme == .dark ? Color.primary.opacity(0.14) : Color.primary.opacity(0.12)
            celestialCore = scheme == .dark ? Color.primary.opacity(0.65) : Color.primary.opacity(0.30)
            fullMoonCore = Color.primary.opacity(0.45)
            paperHighlight = Color.primary.opacity(0.28)
            birdBody = Color.primary.opacity(0.76)
            birdWing = Color.primary.opacity(0.42)
            birdTail = Color.primary.opacity(0.65)
            birdBeak = Color.primary.opacity(0.88)
            soundWave = Color.primary.opacity(0.58)
            snowAccent = Color.primary.opacity(0.35)
            festiveRed = Color.primary.opacity(0.72)
            festiveGold = Color.primary.opacity(0.52)
            festiveBlue = Color.primary.opacity(0.60)
            return
        }

        let isDark = scheme == .dark
        let seasonalLeaves: (Color, Color, Color)
        if !atmosphere.enabled {
            seasonalLeaves = isDark
                ? (
                    Color(red: 0.34, green: 0.50, blue: 0.39).opacity(0.88),
                    Color(red: 0.46, green: 0.62, blue: 0.50).opacity(0.86),
                    Color(red: 0.59, green: 0.73, blue: 0.61).opacity(0.84)
                )
                : (
                    Color(red: 0.28, green: 0.43, blue: 0.33).opacity(0.88),
                    Color(red: 0.40, green: 0.55, blue: 0.44).opacity(0.86),
                    Color(red: 0.54, green: 0.67, blue: 0.56).opacity(0.84)
                )
        } else {
            switch atmosphere.season {
            case .spring:
                seasonalLeaves = isDark
                    ? (
                        Color(red: 0.36, green: 0.56, blue: 0.38).opacity(0.88),
                        Color(red: 0.52, green: 0.70, blue: 0.48).opacity(0.86),
                        Color(red: 0.69, green: 0.80, blue: 0.58).opacity(0.84)
                    )
                    : (
                        Color(red: 0.31, green: 0.50, blue: 0.33).opacity(0.88),
                        Color(red: 0.46, green: 0.65, blue: 0.44).opacity(0.86),
                        Color(red: 0.63, green: 0.77, blue: 0.56).opacity(0.84)
                    )
            case .summer:
                seasonalLeaves = isDark
                    ? (
                        Color(red: 0.34, green: 0.50, blue: 0.39).opacity(0.88),
                        Color(red: 0.46, green: 0.62, blue: 0.50).opacity(0.86),
                        Color(red: 0.59, green: 0.73, blue: 0.61).opacity(0.84)
                    )
                    : (
                        Color(red: 0.28, green: 0.43, blue: 0.33).opacity(0.88),
                        Color(red: 0.40, green: 0.55, blue: 0.44).opacity(0.86),
                        Color(red: 0.54, green: 0.67, blue: 0.56).opacity(0.84)
                    )
            case .autumn:
                seasonalLeaves = isDark
                    ? (
                        Color(red: 0.66, green: 0.38, blue: 0.22).opacity(0.88),
                        Color(red: 0.82, green: 0.54, blue: 0.25).opacity(0.86),
                        Color(red: 0.90, green: 0.71, blue: 0.34).opacity(0.84)
                    )
                    : (
                        Color(red: 0.58, green: 0.31, blue: 0.16).opacity(0.88),
                        Color(red: 0.76, green: 0.47, blue: 0.19).opacity(0.86),
                        Color(red: 0.86, green: 0.65, blue: 0.26).opacity(0.84)
                    )
            case .winter:
                seasonalLeaves = isDark
                    ? (
                        Color(red: 0.24, green: 0.38, blue: 0.35).opacity(0.88),
                        Color(red: 0.34, green: 0.48, blue: 0.45).opacity(0.86),
                        Color(red: 0.48, green: 0.60, blue: 0.58).opacity(0.84)
                    )
                    : (
                        Color(red: 0.20, green: 0.33, blue: 0.31).opacity(0.88),
                        Color(red: 0.31, green: 0.44, blue: 0.41).opacity(0.86),
                        Color(red: 0.45, green: 0.56, blue: 0.54).opacity(0.84)
                    )
            }
        }

        leafDark = seasonalLeaves.0
        leafMid = seasonalLeaves.1
        leafLight = seasonalLeaves.2

        if isDark {
            trunk = Color(red: 0.78, green: 0.76, blue: 0.71)
            faintInk = KkTheme.faintInk(scheme)
            groundLine = Color.white.opacity(0.28)
            groundWash = Color.white.opacity(0.05)
            budTip = Color(red: 0.96, green: 0.72, blue: 0.72).opacity(0.98)
            blossom = Color(red: 0.98, green: 0.90, blue: 0.87).opacity(0.98)
            blossomCore = Color(red: 0.90, green: 0.65, blue: 0.36)
            blossomStamen = Color(red: 1.00, green: 0.87, blue: 0.54)
            youngFruitLight = Color(red: 0.80, green: 0.90, blue: 0.44)
            youngFruit = Color(red: 0.57, green: 0.75, blue: 0.32)
            youngFruitBorder = Color(red: 0.32, green: 0.48, blue: 0.18).opacity(0.85)
            ripeningBase = Color(red: 0.86, green: 0.80, blue: 0.34)
            ripeningAmber = Color(red: 0.95, green: 0.70, blue: 0.32)
            ripeningBlush = Color(red: 0.94, green: 0.52, blue: 0.32)
            ripeningBorder = Color(red: 0.65, green: 0.41, blue: 0.22).opacity(0.75)
            fruitSun = Color(red: 0.94, green: 0.52, blue: 0.36)
            fruit = Color(red: 0.83, green: 0.37, blue: 0.25)
            fruitBorder = Color(red: 0.62, green: 0.25, blue: 0.17).opacity(0.80)
            fruitHighlight = Color(red: 0.99, green: 0.84, blue: 0.73).opacity(0.78)
            heirloomSun = Color(red: 0.95, green: 0.71, blue: 0.34)
            heirloomDeep = Color(red: 0.77, green: 0.36, blue: 0.24)
            heirloomBorder = Color(red: 0.56, green: 0.26, blue: 0.16).opacity(0.85)
            celestialHalo = Color(red: 0.84, green: 0.88, blue: 0.96).opacity(0.18)
            celestialCore = Color(red: 0.96, green: 0.94, blue: 0.84).opacity(0.90)
            fullMoonCore = Color(red: 0.98, green: 0.93, blue: 0.74).opacity(0.94)
            paperHighlight = Color(red: 0.12, green: 0.12, blue: 0.12).opacity(0.65)
            birdBody = Color(red: 0.44, green: 0.68, blue: 0.74)
            birdWing = Color(red: 0.88, green: 0.83, blue: 0.71).opacity(0.82)
            birdTail = Color(red: 0.32, green: 0.54, blue: 0.60)
            birdBeak = Color(red: 0.95, green: 0.72, blue: 0.34)
            soundWave = Color(red: 0.58, green: 0.80, blue: 0.82)
            snowAccent = Color(red: 0.92, green: 0.95, blue: 0.99)
            festiveRed = Color(red: 0.88, green: 0.30, blue: 0.24)
            festiveGold = Color(red: 0.96, green: 0.78, blue: 0.36)
            festiveBlue = Color(red: 0.42, green: 0.64, blue: 0.92)
        } else {
            trunk = Color(red: 0.18, green: 0.16, blue: 0.14)
            faintInk = KkTheme.faintInk(scheme)
            groundLine = Color.black.opacity(0.26)
            groundWash = Color.black.opacity(0.04)
            budTip = Color(red: 0.92, green: 0.60, blue: 0.60).opacity(0.98)
            blossom = Color(red: 0.98, green: 0.88, blue: 0.85).opacity(0.98)
            blossomCore = Color(red: 0.86, green: 0.54, blue: 0.26)
            blossomStamen = Color(red: 0.99, green: 0.80, blue: 0.38)
            youngFruitLight = Color(red: 0.76, green: 0.88, blue: 0.38)
            youngFruit = Color(red: 0.51, green: 0.70, blue: 0.26)
            youngFruitBorder = Color(red: 0.28, green: 0.44, blue: 0.14).opacity(0.84)
            ripeningBase = Color(red: 0.84, green: 0.78, blue: 0.30)
            ripeningAmber = Color(red: 0.94, green: 0.66, blue: 0.26)
            ripeningBlush = Color(red: 0.90, green: 0.45, blue: 0.26)
            ripeningBorder = Color(red: 0.60, green: 0.34, blue: 0.17).opacity(0.70)
            fruitSun = Color(red: 0.90, green: 0.43, blue: 0.27)
            fruit = Color(red: 0.75, green: 0.27, blue: 0.18)
            fruitBorder = Color(red: 0.52, green: 0.18, blue: 0.11).opacity(0.72)
            fruitHighlight = Color(red: 0.99, green: 0.84, blue: 0.74).opacity(0.88)
            heirloomSun = Color(red: 0.91, green: 0.64, blue: 0.25)
            heirloomDeep = Color(red: 0.70, green: 0.27, blue: 0.17)
            heirloomBorder = Color(red: 0.49, green: 0.19, blue: 0.11).opacity(0.78)
            celestialHalo = Color(red: 0.88, green: 0.70, blue: 0.38).opacity(0.22)
            celestialCore = Color(red: 0.90, green: 0.68, blue: 0.32).opacity(0.62)
            fullMoonCore = Color(red: 0.96, green: 0.83, blue: 0.46).opacity(0.82)
            paperHighlight = Color.white.opacity(0.55)
            birdBody = Color(red: 0.28, green: 0.51, blue: 0.58)
            birdWing = Color(red: 0.85, green: 0.79, blue: 0.64).opacity(0.88)
            birdTail = Color(red: 0.21, green: 0.40, blue: 0.47)
            birdBeak = Color(red: 0.89, green: 0.57, blue: 0.24)
            soundWave = Color(red: 0.28, green: 0.52, blue: 0.58)
            snowAccent = Color(red: 0.94, green: 0.96, blue: 0.99)
            festiveRed = Color(red: 0.78, green: 0.20, blue: 0.16)
            festiveGold = Color(red: 0.89, green: 0.66, blue: 0.22)
            festiveBlue = Color(red: 0.20, green: 0.41, blue: 0.73)
        }
    }
}
