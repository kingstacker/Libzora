import SwiftUI
import UIKit

/// Coordinates source cover geometry and the Apple Books-style cover expand &
/// 3D spine-flip transition when interactively opening or closing a book.
struct NativeBookOpenTransition: Identifiable, Equatable {
    let id = UUID()
    let book: LibraryBook
    let sourceFrame: CGRect
}

@MainActor
enum NativeBookCoverFrames {
    private static var frames: [String: CGRect] = [:]

    static func set(_ frame: CGRect, for key: String) {
        guard frame.width > 4, frame.height > 4,
              frame.origin.x.isFinite, frame.origin.y.isFinite else { return }
        frames[key] = frame
    }

    static func get(_ key: String) -> CGRect? {
        frames[key]
    }
}

extension View {
    func trackBookOpenFrame(key: String?) -> some View {
        background {
            if let key {
                GeometryReader { proxy in
                    let frame = proxy.frame(in: .global)
                    Color.clear
                        .onAppear { NativeBookCoverFrames.set(frame, for: key) }
                        .onChange(of: frame) { updated in
                            NativeBookCoverFrames.set(updated, for: key)
                        }
                }
            }
        }
    }
}

/// Single-progress GPU-driven book flip scene shared by open (0 → 1) and close (1 → 0).
/// Uses pure scale/position/3D transforms on a fixed-size cover so the image never
/// re-layouts mid-flight and the turning hardcover leaf is never clipped.
private struct NativeBookFlipScene: View, Animatable {
    let book: LibraryBook
    let sourceFrame: CGRect
    let paper: Color
    var progress: Double

    var animatableData: Double {
        get { progress }
        set { progress = newValue }
    }

    var body: some View {
        GeometryReader { proxy in
            let container = proxy.frame(in: .global)
            let targetW = max(1, proxy.size.width)
            let targetH = max(1, proxy.size.height)
            let localSource = CGRect(
                x: sourceFrame.minX - container.minX,
                y: sourceFrame.minY - container.minY,
                width: max(28, sourceFrame.width),
                height: max(38, sourceFrame.height)
            )

            let t = min(max(progress, 0), 1)
            let sx = lerp(localSource.width / targetW, 1.0, t)
            let sy = lerp(localSource.height / targetH, 1.0, t)
            let cx = lerp(localSource.midX, targetW / 2, t)
            let cy = lerp(localSource.midY, targetH / 2, t)

            // Keep visual corner radius crisp (~6pt on shelf → 0pt full-screen)
            // while compensating for non-uniform scaleEffect.
            let minScale = max(0.08, min(sx, sy))
            let cornerRadius = min(48, (6.0 / minScale) * (1.0 - t))

            // Cover starts swinging open right as the book lifts off the shelf
            // and passes edge-on (-90°) smoothly into the reading paper.
            let flipT = min(max((t - 0.06) / 0.86, 0), 1)
            let angle = -104.0 * flipT
            let coverOpacity = flipT < 0.72 ? 1.0 : max(0, 1.0 - (flipT - 0.72) / 0.22)
            let coverShading = 0.26 * sin(min(1, flipT / 0.85) * .pi * 0.5)
            let pageShadowOpacity = 0.22 * (1.0 - flipT)
            let flightArc = sin(t * .pi)

            ZStack {
                // Softly dim the shelf underneath as the book lifts into focus.
                Color.black
                    .opacity(0.26 * t)

                ZStack {
                    // Inner reading paper revealed beneath the turning cover.
                    ZStack(alignment: .leading) {
                        paper
                        // Cast shadow from the lifting cover leaf across the paper.
                        LinearGradient(
                            colors: [
                                Color.black.opacity(pageShadowOpacity),
                                Color.black.opacity(pageShadowOpacity * 0.35),
                                Color.clear
                            ],
                            startPoint: .leading,
                            endPoint: UnitPoint(x: max(0.18, 0.72 * (1.0 - flipT * 0.65)), y: 0.5)
                        )
                    }
                    .frame(width: targetW, height: targetH)
                    .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                    .shadow(
                        color: .black.opacity(lerp(0.14, 0.26, flightArc)),
                        radius: lerp(8, 26, flightArc),
                        x: 0,
                        y: lerp(4, 12, flightArc)
                    )

                    // Front hardcover leaf hinging smoothly around the left spine.
                    BookCover(book: book, width: targetW, height: targetH)
                        .overlay(alignment: .leading) {
                            // Subtle vertical hardcover spine crease.
                            LinearGradient(
                                colors: [
                                    Color.black.opacity(0.18),
                                    Color.white.opacity(0.14),
                                    Color.black.opacity(0.08),
                                    Color.clear
                                ],
                                startPoint: .leading,
                                endPoint: .trailing
                            )
                            .frame(width: max(12, targetW * 0.045))
                        }
                        .overlay {
                            Color.black.opacity(coverShading)
                        }
                        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                        .rotation3DEffect(
                            .degrees(angle),
                            axis: (x: 0, y: 1, z: 0),
                            anchor: .leading,
                            anchorZ: 0,
                            perspective: 0.32
                        )
                        .opacity(coverOpacity)
                }
                .frame(width: targetW, height: targetH)
                .scaleEffect(x: sx, y: sy, anchor: .center)
                .position(x: cx, y: cy)
            }
            .frame(width: targetW, height: targetH)
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func lerp(_ a: CGFloat, _ b: CGFloat, _ t: Double) -> CGFloat {
        a + (b - a) * CGFloat(t)
    }
}

/// Played over `NativeShell` while the live shelf remains visible beneath the lifting cover.
struct NativeBookOpenOverlay: View {
    let transition: NativeBookOpenTransition
    let paper: Color
    let onReachedFullScreen: () -> Void
    let onFinished: () -> Void

    @State private var progress: Double = 0
    @State private var presented = false

    var body: some View {
        NativeBookFlipScene(
            book: transition.book,
            sourceFrame: transition.sourceFrame,
            paper: paper,
            progress: progress
        )
        .onAppear {
            withAnimation(.timingCurve(0.22, 1.0, 0.36, 1.0, duration: 0.44)) {
                progress = 1
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.38) {
                guard !presented else { return }
                presented = true
                onReachedFullScreen()
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.48) {
                onFinished()
            }
        }
    }
}

/// Reverse cover-shut overlay played over `NativeShell` when returning from the reader.
struct NativeBookCloseOverlay: View {
    let transition: NativeBookOpenTransition
    let paper: Color
    let onFinished: () -> Void

    @State private var progress: Double = 1
    @State private var settlingFade: Double = 1

    var body: some View {
        NativeBookFlipScene(
            book: transition.book,
            sourceFrame: transition.sourceFrame,
            paper: paper,
            progress: progress
        )
        .opacity(settlingFade)
        .onAppear {
            withAnimation(.timingCurve(0.24, 0.94, 0.32, 1.0, duration: 0.36)) {
                progress = 0
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.32) {
                withAnimation(.easeOut(duration: 0.08)) {
                    settlingFade = 0
                }
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.41) {
                onFinished()
            }
        }
    }
}
