import SwiftUI

// MARK: - Seasonal, Festival & Listening Vector Decorations (0 Image Assets)

extension KkReadingTreeCanvas {
    func drawTreeDecorations(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        unit: CGFloat,
        sun: KkSunPlacement,
        palette: TreePalette
    ) {
        if profile.atmosphere.showsWinterSnow {
            drawWinterCanopySnow(in: &context, nodes: skeleton.canopyNodes, sun: sun, unit: unit, palette: palette)
        }

        if profile.atmosphere.enabled {
            switch profile.atmosphere.festival {
            case .none:
                break
            case .christmas:
                drawChristmasOrnaments(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .springFestival, .lanternFestival, .nationalDay:
                drawFestiveLanterns(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .newYear:
                drawNewYearSparkles(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .valentinesDay:
                drawValentinesAccents(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .qingming:
                drawQingmingWillow(in: &context, skeleton: skeleton, unit: unit, palette: palette)
            case .dragonBoat:
                drawDragonBoatCharm(in: &context, skeleton: skeleton, unit: unit, palette: palette)
            case .independenceDay:
                drawIndependenceFireworks(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .qixi:
                drawQixiMagpieRibbon(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .midAutumn:
                drawMidAutumnOsmanthus(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .doubleNinth:
                drawDoubleNinthChrysanthemums(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
            case .halloween:
                drawHalloweenPumpkin(in: &context, skeleton: skeleton, unit: unit, palette: palette)
            case .thanksgiving:
                drawThanksgivingHarvest(in: &context, skeleton: skeleton, unit: unit, palette: palette)
            }
        }

        drawListeningCompanion(in: &context, skeleton: skeleton, sun: sun, unit: unit, palette: palette)
    }

    func drawSeasonalParticles(
        in context: inout GraphicsContext,
        rect: CGRect,
        groundY: CGFloat,
        unit: CGFloat,
        sun: KkSunPlacement,
        palette: TreePalette
    ) {
        guard profile.atmosphere.enabled else { return }
        switch profile.atmosphere.season {
        case .spring:
            drawDriftingSpringPetals(in: &context, rect: rect, groundY: groundY, unit: unit, sun: sun, palette: palette)
        case .summer:
            drawSummerGrassAndGlow(in: &context, rect: rect, groundY: groundY, unit: unit, sun: sun, palette: palette)
        case .autumn:
            drawAutumnFallingLeaves(in: &context, rect: rect, groundY: groundY, unit: unit, sun: sun, palette: palette)
        case .winter:
            drawWinterSnowfall(in: &context, rect: rect, groundY: groundY, unit: unit, sun: sun, palette: palette)
        }
    }

    // MARK: - Listening Companion (Songbirds & Acoustic Waves)

    private func drawListeningCompanion(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let count = profile.listeningBirdCount
        guard count > 0, !skeleton.canopyNodes.isEmpty else { return }

        // Prefer outer upper-left or mid-crown branch tips that stay safely away from the sun disc.
        let candidates = skeleton.canopyNodes
            .filter { node in
                hypot(node.point.x - sun.center.x, node.point.y - sun.center.y) >= sun.clearanceRadius + unit * 0.08
                    && node.point.y < skeleton.rootPoint.y - unit * 0.24
            }
            .sorted { a, b in
                if abs(a.point.x - b.point.x) > 1 {
                    return a.point.x < b.point.x
                }
                return a.point.y < b.point.y
            }

        guard let firstPerch = candidates.dropFirst(min(1, max(0, candidates.count - 1))).first ?? candidates.first else {
            return
        }

        drawSongbird(
            in: &context,
            perch: CGPoint(x: firstPerch.point.x, y: firstPerch.point.y - unit * 0.015),
            facingRight: firstPerch.point.x <= skeleton.rootPoint.x + unit * 0.10,
            unit: unit,
            singing: profile.hasListenedToday,
            palette: palette)

        if count >= 2, candidates.count >= 4 {
            let secondIdx = max(0, (candidates.count * 2) / 3)
            let secondPerch = candidates[secondIdx]
            if hypot(secondPerch.point.x - firstPerch.point.x, secondPerch.point.y - firstPerch.point.y) > unit * 0.16 {
                drawSongbird(
                    in: &context,
                    perch: CGPoint(x: secondPerch.point.x, y: secondPerch.point.y - unit * 0.012),
                    facingRight: secondPerch.point.x <= skeleton.rootPoint.x,
                    unit: unit * 0.90,
                    singing: profile.hasListenedToday,
                    palette: palette)
            }
        }
    }

    private func drawSongbird(
        in context: inout GraphicsContext,
        perch: CGPoint,
        facingRight: Bool,
        unit: CGFloat,
        singing: Bool,
        palette: TreePalette
    ) {
        let dir: CGFloat = facingRight ? 1 : -1
        let r = max(2.2, unit * 0.028)

        // Tail feathers.
        var tail = Path()
        tail.move(to: CGPoint(x: perch.x - dir * r * 0.45, y: perch.y - r * 0.35))
        tail.addLine(to: CGPoint(x: perch.x - dir * r * 1.45, y: perch.y - r * 0.85))
        tail.addLine(to: CGPoint(x: perch.x - dir * r * 1.15, y: perch.y - r * 0.15))
        tail.closeSubpath()
        context.fill(tail, with: .color(palette.birdTail))

        // Rounded songbird body.
        var body = Path()
        body.addEllipse(in: CGRect(
            x: perch.x - r * 0.95,
            y: perch.y - r * 1.15,
            width: r * 1.90,
            height: r * 1.32))
        context.fill(body, with: .color(palette.birdBody))

        // Head.
        let headCenter = CGPoint(x: perch.x + dir * r * 0.68, y: perch.y - r * 1.05)
        let headRect = CGRect(x: headCenter.x - r * 0.48, y: headCenter.y - r * 0.48, width: r * 0.96, height: r * 0.96)
        context.fill(Path(ellipseIn: headRect), with: .color(palette.birdBody))

        // Soft chest / wing highlight.
        let wingRect = CGRect(
            x: perch.x - r * 0.58 + (facingRight ? -r * 0.08 : r * 0.08),
            y: perch.y - r * 0.88,
            width: r * 1.05,
            height: r * 0.64)
        context.fill(Path(ellipseIn: wingRect), with: .color(palette.birdWing))

        // Beak.
        var beak = Path()
        beak.move(to: CGPoint(x: headCenter.x + dir * r * 0.42, y: headCenter.y - r * 0.12))
        beak.addLine(to: CGPoint(x: headCenter.x + dir * r * 0.94, y: headCenter.y - r * 0.02))
        beak.addLine(to: CGPoint(x: headCenter.x + dir * r * 0.40, y: headCenter.y + r * 0.16))
        beak.closeSubpath()
        context.fill(beak, with: .color(palette.birdBeak))

        // Tiny eye dot.
        let eyeCenter = CGPoint(x: headCenter.x + dir * r * 0.16, y: headCenter.y - r * 0.12)
        context.fill(
            Path(ellipseIn: CGRect(x: eyeCenter.x - 0.7, y: eyeCenter.y - 0.7, width: 1.4, height: 1.4)),
            with: .color(palette.trunk))

        // Acoustic sound arcs when user has listened today.
        if singing {
            let soundOrigin = CGPoint(x: headCenter.x + dir * r * 1.02, y: headCenter.y - r * 0.18)
            for idx in 1...2 {
                let arcRadius = r * (0.55 + CGFloat(idx) * 0.48)
                var arc = Path()
                let startAngle = Angle.degrees(facingRight ? -42 : 138)
                let endAngle = Angle.degrees(facingRight ? 28 : 208)
                arc.addArc(center: soundOrigin, radius: arcRadius, startAngle: startAngle, endAngle: endAngle, clockwise: false)
                context.stroke(
                    arc,
                    with: .color(palette.soundWave.opacity(idx == 1 ? 0.78 : 0.48)),
                    style: StrokeStyle(lineWidth: max(0.75, unit * 0.008), lineCap: .round))
            }
        }
    }

    // MARK: - Seasonal Atmosphere Particles

    private func drawDriftingSpringPetals(
        in context: inout GraphicsContext,
        rect: CGRect,
        groundY: CGFloat,
        unit: CGFloat,
        sun: KkSunPlacement,
        palette: TreePalette
    ) {
        var rng = KkSeededGenerator(seed: 0x53505247)
        for idx in 0..<7 {
            let x = rect.minX + rect.width * (0.12 + CGFloat(rng.nextUnit()) * 0.78)
            let y = idx < 4
                ? (rect.minY + rect.height * (0.18 + CGFloat(rng.nextUnit()) * 0.48))
                : (groundY - unit * (0.01 + CGFloat(rng.nextUnit()) * 0.04))
            let pt = CGPoint(x: x, y: y)
            if hypot(pt.x - sun.center.x, pt.y - sun.center.y) < sun.clearanceRadius { continue }
            let angle = CGFloat(rng.nextUnit()) * .pi * 2
            let len = max(2.2, unit * 0.028)
            drawLeaf(in: &context, origin: pt, angle: angle, length: len, width: len * 0.58,
                     fill: palette.blossom.opacity(0.84), vein: nil)
        }
    }

    private func drawSummerGrassAndGlow(
        in context: inout GraphicsContext,
        rect: CGRect,
        groundY: CGFloat,
        unit: CGFloat,
        sun: KkSunPlacement,
        palette: TreePalette
    ) {
        var rng = KkSeededGenerator(seed: 0x53554D4D)
        for _ in 0..<6 {
            let x = rect.minX + rect.width * (0.14 + CGFloat(rng.nextUnit()) * 0.72)
            let height = max(2.8, unit * (0.025 + CGFloat(rng.nextUnit()) * 0.025))
            let lean = (CGFloat(rng.nextUnit()) - 0.5) * height * 0.65
            var blade = Path()
            blade.move(to: CGPoint(x: x, y: groundY + 0.5))
            blade.addQuadCurve(
                to: CGPoint(x: x + lean, y: groundY - height),
                control: CGPoint(x: x + lean * 0.3, y: groundY - height * 0.55))
            context.stroke(
                blade,
                with: .color(palette.leafMid.opacity(0.62)),
                style: StrokeStyle(lineWidth: max(0.8, unit * 0.008), lineCap: .round))
        }
        // Soft sunlit firefly specs when thriving.
        for _ in 0..<4 {
            let x = rect.minX + rect.width * (0.16 + CGFloat(rng.nextUnit()) * 0.68)
            let y = groundY - unit * (0.10 + CGFloat(rng.nextUnit()) * 0.38)
            if hypot(x - sun.center.x, y - sun.center.y) < sun.clearanceRadius { continue }
            let r = max(1.0, unit * 0.011)
            context.fill(
                Path(ellipseIn: CGRect(x: x - r, y: y - r, width: r * 2, height: r * 2)),
                with: .color(palette.blossomStamen.opacity(0.75)))
        }
    }

    private func drawAutumnFallingLeaves(
        in context: inout GraphicsContext,
        rect: CGRect,
        groundY: CGFloat,
        unit: CGFloat,
        sun: KkSunPlacement,
        palette: TreePalette
    ) {
        var rng = KkSeededGenerator(seed: 0x4155544D)
        for idx in 0..<8 {
            let x = rect.minX + rect.width * (0.12 + CGFloat(rng.nextUnit()) * 0.76)
            let y = idx < 4
                ? (rect.minY + rect.height * (0.24 + CGFloat(rng.nextUnit()) * 0.45))
                : (groundY - unit * (0.008 + CGFloat(rng.nextUnit()) * 0.032))
            if hypot(x - sun.center.x, y - sun.center.y) < sun.clearanceRadius { continue }
            let angle = CGFloat(rng.nextUnit()) * .pi * 2
            let len = max(2.4, unit * 0.032)
            let color = idx % 2 == 0 ? palette.ripeningAmber : palette.fruitSun
            drawLeaf(in: &context, origin: CGPoint(x: x, y: y), angle: angle, length: len, width: len * 0.56,
                     fill: color.opacity(0.88), vein: nil)
        }
    }

    private func drawWinterSnowfall(
        in context: inout GraphicsContext,
        rect: CGRect,
        groundY: CGFloat,
        unit: CGFloat,
        sun: KkSunPlacement,
        palette: TreePalette
    ) {
        // Subtle snow blanket along ground line.
        var snowCap = Path()
        snowCap.move(to: CGPoint(x: rect.minX + rect.width * 0.14, y: groundY - 0.5))
        snowCap.addQuadCurve(
            to: CGPoint(x: rect.maxX - rect.width * 0.14, y: groundY - 0.5),
            control: CGPoint(x: rect.midX, y: groundY - max(2.2, unit * 0.022)))
        context.stroke(
            snowCap,
            with: .color(palette.snowAccent.opacity(0.72)),
            style: StrokeStyle(lineWidth: max(1.6, unit * 0.016), lineCap: .round))

        var rng = KkSeededGenerator(seed: 0x57494E54)
        for idx in 0..<9 {
            let x = rect.minX + rect.width * (0.10 + CGFloat(rng.nextUnit()) * 0.80)
            let y = rect.minY + rect.height * (0.12 + CGFloat(rng.nextUnit()) * 0.65)
            if hypot(x - sun.center.x, y - sun.center.y) < sun.clearanceRadius { continue }
            let r = max(0.95, unit * (idx % 3 == 0 ? 0.014 : 0.010))
            context.fill(
                Path(ellipseIn: CGRect(x: x - r, y: y - r, width: r * 2, height: r * 2)),
                with: .color(palette.snowAccent.opacity(0.88)))
        }
    }

    private func drawWinterCanopySnow(
        in context: inout GraphicsContext,
        nodes: [KkTreeCanopyNode],
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let topNodes = nodes.filter { !$0.isDrooping }.enumerated().compactMap { idx, node in
            idx % 3 == 0 ? node : nil
        }
        for node in topNodes.prefix(8) {
            if hypot(node.point.x - sun.center.x, node.point.y - sun.center.y) < sun.clearanceRadius { continue }
            let w = max(4.2, unit * 0.068) * node.weight
            let h = w * 0.38
            let snowRect = CGRect(x: node.point.x - w * 0.5, y: node.point.y - h * 0.9, width: w, height: h)
            context.fill(Path(ellipseIn: snowRect), with: .color(palette.snowAccent.opacity(0.68)))
        }
    }

    // MARK: - Festival Accents

    private func drawChristmasOrnaments(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let topNode = skeleton.canopyNodes
            .filter { hypot($0.point.x - sun.center.x, $0.point.y - sun.center.y) >= sun.clearanceRadius }
            .min(by: { $0.point.y < $1.point.y })

        if let apex = topNode {
            drawFivePointStar(
                in: &context,
                center: CGPoint(x: apex.point.x, y: max(skeleton.viewport.minY + unit * 0.05, apex.point.y - unit * 0.045)),
                radius: max(3.6, unit * 0.046),
                fill: palette.festiveGold)
        }

        let ornamentNodes = skeleton.canopyNodes.filter { node in
            hypot(node.point.x - sun.center.x, node.point.y - sun.center.y) >= sun.clearanceRadius + unit * 0.03
        }
        let step = max(1, ornamentNodes.count / 5)
        for idx in stride(from: 0, to: ornamentNodes.count, by: step).prefix(5) {
            let anchor = ornamentNodes[idx].point
            let r = max(2.0, unit * 0.025)
            let center = CGPoint(x: anchor.x, y: anchor.y + r * 1.45)
            var cord = Path()
            cord.move(to: anchor)
            cord.addLine(to: CGPoint(x: center.x, y: center.y - r))
            context.stroke(cord, with: .color(palette.faintInk), lineWidth: max(0.55, unit * 0.005))

            let fill = idx % 2 == 0 ? palette.festiveRed : palette.festiveGold
            context.fill(
                Path(ellipseIn: CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2)),
                with: .color(fill))
            context.fill(
                Path(ellipseIn: CGRect(x: center.x - r * 0.42, y: center.y - r * 0.55, width: r * 0.46, height: r * 0.46)),
                with: .color(palette.paperHighlight))
        }
    }

    private func drawFestiveLanterns(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let hangingNodes = skeleton.canopyNodes
            .filter { ($0.isDrooping || $0.point.y > skeleton.trunkTop.y) &&
                hypot($0.point.x - sun.center.x, $0.point.y - sun.center.y) >= sun.clearanceRadius + unit * 0.05 }
        let pool = hangingNodes.isEmpty ? skeleton.canopyNodes : hangingNodes
        guard !pool.isEmpty else { return }

        let picks: [KkTreeCanopyNode]
        if pool.count >= 3 {
            picks = [pool[0], pool[pool.count / 2], pool[pool.count - 1]]
        } else {
            picks = Array(pool.prefix(2))
        }

        for node in picks {
            let drop = max(3.6, unit * 0.048)
            let w = max(3.8, unit * 0.044)
            let h = w * 1.25
            let top = node.point
            let lanternTop = CGPoint(x: top.x, y: top.y + drop)

            var string = Path()
            string.move(to: top)
            string.addLine(to: lanternTop)
            context.stroke(string, with: .color(palette.festiveGold), lineWidth: max(0.65, unit * 0.006))

            let bodyRect = CGRect(x: lanternTop.x - w * 0.5, y: lanternTop.y, width: w, height: h)
            context.fill(Path(roundedRect: bodyRect, cornerRadius: w * 0.42), with: .color(palette.festiveRed))

            // Top and bottom gold caps + short tassel.
            let capH = max(1.0, h * 0.16)
            context.fill(
                Path(CGRect(x: lanternTop.x - w * 0.30, y: lanternTop.y - capH * 0.3, width: w * 0.60, height: capH)),
                with: .color(palette.festiveGold))
            context.fill(
                Path(CGRect(x: lanternTop.x - w * 0.30, y: bodyRect.maxY - capH * 0.7, width: w * 0.60, height: capH)),
                with: .color(palette.festiveGold))

            var tassel = Path()
            tassel.move(to: CGPoint(x: lanternTop.x, y: bodyRect.maxY + capH * 0.2))
            tassel.addLine(to: CGPoint(x: lanternTop.x, y: bodyRect.maxY + drop * 0.62))
            context.stroke(tassel, with: .color(palette.festiveGold.opacity(0.92)), lineWidth: max(0.75, unit * 0.007))
        }
    }

    private func drawNewYearSparkles(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let nodes = skeleton.canopyNodes.filter {
            hypot($0.point.x - sun.center.x, $0.point.y - sun.center.y) >= sun.clearanceRadius
        }
        let step = max(1, nodes.count / 5)
        for idx in stride(from: 0, to: nodes.count, by: step).prefix(5) {
            let pt = CGPoint(x: nodes[idx].point.x, y: nodes[idx].point.y - unit * 0.025)
            drawFourPointSparkle(in: &context, center: pt, radius: max(2.4, unit * 0.032), fill: palette.festiveGold)
        }
    }

    private func drawDragonBoatCharm(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let base = CGPoint(x: skeleton.rootPoint.x + skeleton.baseThickness * 1.15, y: skeleton.rootPoint.y - unit * 0.06)
        let len = max(5.0, unit * 0.085)
        for idx in -1...1 {
            let angle = -.pi * 0.5 + CGFloat(idx) * 0.30
            drawLeaf(
                in: &context,
                origin: base,
                angle: angle,
                length: len,
                width: len * 0.30,
                fill: palette.leafDark,
                vein: nil)
        }
        context.fill(
            Path(ellipseIn: CGRect(x: base.x - unit * 0.015, y: base.y - unit * 0.015, width: unit * 0.030, height: unit * 0.030)),
            with: .color(palette.festiveRed))
    }

    private func drawMidAutumnOsmanthus(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let nodes = skeleton.canopyNodes.filter {
            hypot($0.point.x - sun.center.x, $0.point.y - sun.center.y) >= sun.clearanceRadius
        }
        let step = max(1, nodes.count / 8)
        for idx in stride(from: 0, to: nodes.count, by: step).prefix(8) {
            let pt = nodes[idx].point
            let r = max(1.4, unit * 0.018)
            context.fill(
                Path(ellipseIn: CGRect(x: pt.x - r, y: pt.y - r, width: r * 2, height: r * 2)),
                with: .color(palette.festiveGold.opacity(0.92)))
        }
    }

    private func drawFivePointStar(in context: inout GraphicsContext, center: CGPoint, radius: CGFloat, fill: Color) {
        var path = Path()
        let inner = radius * 0.44
        for idx in 0..<10 {
            let angle = -.pi * 0.5 + CGFloat(idx) * (.pi / 5.0)
            let r = idx % 2 == 0 ? radius : inner
            let pt = CGPoint(x: center.x + cos(angle) * r, y: center.y + sin(angle) * r)
            if idx == 0 {
                path.move(to: pt)
            } else {
                path.addLine(to: pt)
            }
        }
        path.closeSubpath()
        context.fill(path, with: .color(fill))
    }

    private func drawFourPointSparkle(in context: inout GraphicsContext, center: CGPoint, radius: CGFloat, fill: Color) {
        var path = Path()
        let inner = radius * 0.28
        for idx in 0..<8 {
            let angle = -.pi * 0.5 + CGFloat(idx) * (.pi / 4.0)
            let r = idx % 2 == 0 ? radius : inner
            let pt = CGPoint(x: center.x + cos(angle) * r, y: center.y + sin(angle) * r)
            if idx == 0 {
                path.move(to: pt)
            } else {
                path.addLine(to: pt)
            }
        }
        path.closeSubpath()
        context.fill(path, with: .color(fill))
    }

    private func drawValentinesAccents(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let nodes = skeleton.canopyNodes.filter {
            hypot($0.point.x - sun.center.x, $0.point.y - sun.center.y) >= sun.clearanceRadius
        }
        let step = max(1, nodes.count / 5)
        for idx in stride(from: 0, to: nodes.count, by: step).prefix(5) {
            drawHeart(in: &context, center: nodes[idx].point, size: max(3.0, unit * 0.036), fill: palette.festiveRed.opacity(0.90))
        }
    }

    private func drawHeart(in context: inout GraphicsContext, center: CGPoint, size: CGFloat, fill: Color) {
        let r = size * 0.5
        var path = Path()
        path.move(to: CGPoint(x: center.x, y: center.y + r * 0.95))
        path.addCurve(
            to: CGPoint(x: center.x - r, y: center.y - r * 0.15),
            control1: CGPoint(x: center.x - r * 0.95, y: center.y + r * 0.45),
            control2: CGPoint(x: center.x - r * 1.05, y: center.y - r * 0.15))
        path.addArc(
            center: CGPoint(x: center.x - r * 0.5, y: center.y - r * 0.25),
            radius: r * 0.5,
            startAngle: .degrees(180),
            endAngle: .degrees(0),
            clockwise: false)
        path.addArc(
            center: CGPoint(x: center.x + r * 0.5, y: center.y - r * 0.25),
            radius: r * 0.5,
            startAngle: .degrees(180),
            endAngle: .degrees(0),
            clockwise: false)
        path.addCurve(
            to: CGPoint(x: center.x, y: center.y + r * 0.95),
            control1: CGPoint(x: center.x + r * 1.05, y: center.y - r * 0.15),
            control2: CGPoint(x: center.x + r * 0.95, y: center.y + r * 0.45))
        path.closeSubpath()
        context.fill(path, with: .color(fill))
    }

    private func drawQingmingWillow(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        unit: CGFloat,
        palette: TreePalette
    ) {
        // Fine diagonal spring rain threads & fresh willow wisps along outer twigs.
        for idx in 0..<5 {
            let x = skeleton.viewport.minX + skeleton.viewport.width * (0.14 + CGFloat(idx) * 0.17)
            let y = skeleton.viewport.minY + unit * (0.14 + CGFloat(idx % 2) * 0.10)
            var rain = Path()
            rain.move(to: CGPoint(x: x, y: y))
            rain.addLine(to: CGPoint(x: x - unit * 0.024, y: y + unit * 0.065))
            context.stroke(
                rain,
                with: .color(palette.faintInk.opacity(0.62)),
                style: StrokeStyle(lineWidth: max(0.7, unit * 0.007), lineCap: .round))
        }
    }

    private func drawIndependenceFireworks(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let centers = [
            CGPoint(x: skeleton.viewport.minX + skeleton.viewport.width * 0.20, y: skeleton.viewport.minY + skeleton.viewport.height * 0.17),
            CGPoint(x: skeleton.viewport.minX + skeleton.viewport.width * 0.48, y: skeleton.viewport.minY + skeleton.viewport.height * 0.13)
        ]
        for (burstIdx, center) in centers.enumerated() {
            if hypot(center.x - sun.center.x, center.y - sun.center.y) < sun.clearanceRadius + unit * 0.05 { continue }
            let radius = max(5.0, unit * (burstIdx == 0 ? 0.068 : 0.054))
            for spoke in 0..<8 {
                let angle = CGFloat(spoke) * (.pi / 4.0)
                let inner = CGPoint(x: center.x + cos(angle) * radius * 0.28, y: center.y + sin(angle) * radius * 0.28)
                let outer = CGPoint(x: center.x + cos(angle) * radius, y: center.y + sin(angle) * radius)
                var ray = Path()
                ray.move(to: inner)
                ray.addLine(to: outer)
                let color = (spoke + burstIdx) % 3 == 0 ? palette.festiveRed : ((spoke + burstIdx) % 3 == 1 ? palette.festiveBlue : palette.festiveGold)
                context.stroke(ray, with: .color(color.opacity(0.88)), style: StrokeStyle(lineWidth: max(0.85, unit * 0.009), lineCap: .round))
            }
        }
    }

    private func drawQixiMagpieRibbon(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let leftStar = CGPoint(x: skeleton.viewport.minX + skeleton.viewport.width * 0.18, y: skeleton.viewport.minY + skeleton.viewport.height * 0.18)
        let rightStar = CGPoint(x: skeleton.viewport.minX + skeleton.viewport.width * 0.56, y: skeleton.viewport.minY + skeleton.viewport.height * 0.14)
        for star in [leftStar, rightStar] {
            if hypot(star.x - sun.center.x, star.y - sun.center.y) >= sun.clearanceRadius {
                drawFourPointSparkle(in: &context, center: star, radius: max(2.6, unit * 0.036), fill: palette.festiveGold)
            }
        }
        var bridge = Path()
        bridge.move(to: leftStar)
        bridge.addQuadCurve(
            to: rightStar,
            control: CGPoint(x: (leftStar.x + rightStar.x) * 0.5, y: min(leftStar.y, rightStar.y) - unit * 0.035))
        context.stroke(
            bridge,
            with: .color(palette.festiveGold.opacity(0.55)),
            style: StrokeStyle(lineWidth: max(0.7, unit * 0.007), lineCap: .round, dash: [2, 3]))
    }

    private func drawDoubleNinthChrysanthemums(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        sun: KkSunPlacement,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let nodes = skeleton.canopyNodes.filter {
            hypot($0.point.x - sun.center.x, $0.point.y - sun.center.y) >= sun.clearanceRadius
        }
        let step = max(1, nodes.count / 6)
        for idx in stride(from: 0, to: nodes.count, by: step).prefix(6) {
            let center = nodes[idx].point
            let radius = max(2.4, unit * 0.030)
            for petal in 0..<6 {
                let angle = CGFloat(petal) * (.pi / 3.0)
                let pt = CGPoint(x: center.x + cos(angle) * radius * 0.52, y: center.y + sin(angle) * radius * 0.52)
                let r = radius * 0.45
                context.fill(
                    Path(ellipseIn: CGRect(x: pt.x - r, y: pt.y - r, width: r * 2, height: r * 2)),
                    with: .color(palette.festiveGold.opacity(0.88)))
            }
            context.fill(
                Path(ellipseIn: CGRect(x: center.x - radius * 0.28, y: center.y - radius * 0.28, width: radius * 0.56, height: radius * 0.56)),
                with: .color(palette.ripeningAmber))
        }
    }

    private func drawHalloweenPumpkin(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let groundY = skeleton.rootPoint.y
        let center = CGPoint(x: skeleton.rootPoint.x - max(10, skeleton.baseThickness * 2.1), y: groundY - max(3.6, unit * 0.042))
        let w = max(7.0, unit * 0.095)
        let h = w * 0.78
        // Stem.
        var stem = Path()
        stem.move(to: CGPoint(x: center.x, y: center.y - h * 0.45))
        stem.addLine(to: CGPoint(x: center.x + w * 0.12, y: center.y - h * 0.76))
        context.stroke(stem, with: .color(palette.leafDark), style: StrokeStyle(lineWidth: max(1.1, unit * 0.012), lineCap: .round))
        // Pumpkin body.
        context.fill(
            Path(ellipseIn: CGRect(x: center.x - w * 0.5, y: center.y - h * 0.5, width: w, height: h)),
            with: .color(palette.ripeningAmber))
        // Glowing grin + eyes.
        let eyeR = max(0.8, w * 0.09)
        context.fill(Path(ellipseIn: CGRect(x: center.x - w * 0.20 - eyeR, y: center.y - h * 0.12, width: eyeR * 2, height: eyeR * 2)), with: .color(palette.trunk))
        context.fill(Path(ellipseIn: CGRect(x: center.x + w * 0.20 - eyeR, y: center.y - h * 0.12, width: eyeR * 2, height: eyeR * 2)), with: .color(palette.trunk))
        var smile = Path()
        smile.move(to: CGPoint(x: center.x - w * 0.22, y: center.y + h * 0.12))
        smile.addQuadCurve(
            to: CGPoint(x: center.x + w * 0.22, y: center.y + h * 0.12),
            control: CGPoint(x: center.x, y: center.y + h * 0.34))
        context.stroke(smile, with: .color(palette.trunk), style: StrokeStyle(lineWidth: max(0.9, w * 0.09), lineCap: .round))
    }

    private func drawThanksgivingHarvest(
        in context: inout GraphicsContext,
        skeleton: KkTreeSkeleton,
        unit: CGFloat,
        palette: TreePalette
    ) {
        let base = CGPoint(x: skeleton.rootPoint.x + max(9, skeleton.baseThickness * 1.9), y: skeleton.rootPoint.y)
        let len = max(6.0, unit * 0.095)
        for idx in -1...1 {
            let angle = -.pi * 0.5 + CGFloat(idx) * 0.26
            let tip = CGPoint(x: base.x + cos(angle) * len, y: base.y + sin(angle) * len)
            var stalk = Path()
            stalk.move(to: base)
            stalk.addLine(to: tip)
            context.stroke(stalk, with: .color(palette.festiveGold), style: StrokeStyle(lineWidth: max(0.8, unit * 0.008), lineCap: .round))
            drawLeaf(in: &context, origin: tip, angle: angle, length: len * 0.38, width: len * 0.16, fill: palette.ripeningAmber, vein: nil)
        }
    }
}
