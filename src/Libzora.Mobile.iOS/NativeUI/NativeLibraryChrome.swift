import SwiftUI
import UIKit

struct NativeVisibleCover: Hashable {
    let path: String
    let version: String
}

struct NativeAmbientCoverRegion: Equatable {
    let cover: NativeVisibleCover
    let column: Int
    let row: Int
    let visibility: Double
}

final class NativeLibraryGlassState: ObservableObject {
    @Published fileprivate(set) var opacity: Double = 0
}

/// Tracks the scroll direction to reveal the shelf actions on a downward drag.
final class NativeLibraryScrollState: ObservableObject {
    static let space = "library.scroll"
    let glass = NativeLibraryGlassState()
    @Published private(set) var showMasthead = false
    private(set) var offset: CGFloat = 0
    var onScrollBucketChanged: ((CGFloat) -> Void)?
    private var anchor: CGFloat?
    private var directionAnchor: CGFloat = 0
    private var maximumOffset: CGFloat = 0
    private var lastValue: CGFloat?
    private var lastSample = ProcessInfo.processInfo.systemUptime
    private var scrollSpeed: CGFloat = 0
    private var lastAmbientBucket = -1

    /// Finish the masthead transition in the time it takes the shelf to cover
    /// its height, so a flick snaps in and a slow drag eases with the finger.
    private var mastheadDuration: TimeInterval {
        min(0.26, max(0.05, 64 / Double(max(abs(scrollSpeed), 160))))
    }

    func updateBounds(contentHeight: CGFloat, viewportHeight: CGFloat) {
        guard contentHeight.isFinite, viewportHeight.isFinite, viewportHeight > 0 else { return }
        maximumOffset = max(0, contentHeight - viewportHeight)
        directionAnchor = min(directionAnchor, maximumOffset)
        if offset > maximumOffset {
            offset = maximumOffset
            showMasthead = false
            updateGlassOpacity()
        }
    }

    private func updateGlassOpacity() {
        let clamped = min(1, max(0, offset / 48))
        let quantized = (clamped * 25).rounded() / 25
        if abs(quantized - glass.opacity) >= 0.03 {
            glass.opacity = quantized
        }
    }

    func update(top: CGFloat) {
        guard top.isFinite else { return }
        if anchor == nil { anchor = top }
        guard let anchor else { return }
        // Rubber-band motion beyond either edge is not a scroll reversal.
        // In particular, returning from bottom overscroll must not reveal the header.
        let value = min(maximumOffset, max(0, anchor - top))
        let now = ProcessInfo.processInfo.systemUptime
        if let lastValue, now > lastSample {
            let elapsed = now - lastSample
            let delta = value - lastValue
            if elapsed < 0.12 {
                let instantaneous = delta / CGFloat(elapsed)
                scrollSpeed = scrollSpeed == 0 ? instantaneous : (scrollSpeed * 0.35 + instantaneous * 0.65)
            } else if abs(delta) > 8 {
                // A pause ends the old gesture, but a large initial jump already indicates a fast flick.
                scrollSpeed = delta / 0.016
            } else {
                scrollSpeed = 0
            }
        }
        lastValue = value
        lastSample = now
        if abs(value - offset) > 0.5 {
            if value <= 0.5 {
                // Both headers have identical geometry here, so handing back to
                // the inline header does not make its controls jump or vanish.
                if showMasthead { showMasthead = false }
                directionAnchor = value
            } else if value > directionAnchor + 12 {
                if showMasthead { withAnimation(.easeOut(duration: mastheadDuration)) { showMasthead = false } }
                directionAnchor = value
            } else if value < directionAnchor - 12 {
                if value >= 64 && !showMasthead { withAnimation(.easeOut(duration: mastheadDuration)) { showMasthead = true } }
                directionAnchor = value
            }
            offset = value
            updateGlassOpacity()
            let bucket = Int(max(0, value) / 64)
            if bucket != lastAmbientBucket {
                lastAmbientBucket = bucket
                onScrollBucketChanged?(value)
            }
        }
    }
}

struct NativeLibraryScrollReader: View {
    @ObservedObject var state: NativeLibraryScrollState
    var body: some View {
        GeometryReader { proxy in
            Color.clear
                .onAppear { state.update(top: proxy.frame(in: .named(NativeLibraryScrollState.space)).minY) }
                .onChange(of: proxy.frame(in: .named(NativeLibraryScrollState.space)).minY) { top in
                    state.update(top: top)
                }
        }.frame(height: 0)
    }
}

struct NativeMastheadGlass: View {
    @ObservedObject var glass: NativeLibraryGlassState
    let colors: [NativeAmbientColor]?
    @EnvironmentObject private var store: NativeStore
    var body: some View {
        ZStack {
            Rectangle().fill(.regularMaterial)
            NativeTheme.page.opacity(0.15)
            if store.preferences.colorfulMode {
                NativeAmbientWash(colorsOverride: colors).opacity(0.22)
            }
        }
            // Let the blur dissolve into the shelf instead of ending on a
            // hard line, like the system's soft scroll-edge treatment.
            .mask {
                LinearGradient(stops: [
                    .init(color: .black, location: 0),
                    .init(color: .black, location: 0.46),
                    .init(color: .black.opacity(0.88), location: 0.60),
                    .init(color: .black.opacity(0.5), location: 0.76),
                    .init(color: .black.opacity(0.14), location: 0.91),
                    .init(color: .clear, location: 1)
                ], startPoint: .top, endPoint: .bottom)
            }
            .opacity(glass.opacity)
    }
}

struct NativeLibraryStatusBarGlass: View {
    @ObservedObject var scroll: NativeLibraryScrollState
    var body: some View {
        NativeLibraryStatusBarGlassBody(glass: scroll.glass)
    }
}

struct NativeLibraryStatusBarGlassBody: View {
    @ObservedObject var glass: NativeLibraryGlassState
    var body: some View {
        NativeStatusBarGlass().opacity(glass.opacity)
    }
}

