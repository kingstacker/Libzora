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

/// Narrow observable state observed only by the lightweight header/glass overlays
/// so per-frame scroll tracking never re-evaluates the library grid.
final class NativeLibraryChromeState: ObservableObject {
    @Published fileprivate(set) var glassOpacity: Double = 0
    @Published fileprivate(set) var mastheadProgress: CGFloat = 0
}

/// Tracks scroll offset so the floating masthead and its fade mask follow
/// finger movement 1:1 when appearing and hiding.
final class NativeLibraryScrollState: ObservableObject {
    static let space = "library.scroll"
    let chrome = NativeLibraryChromeState()
    var offset: CGFloat { policy.offset }
    var onScrollBucketChanged: ((CGFloat) -> Void)?
    private var policy = NativeLibraryScrollChromePolicy()
    private var anchor: CGFloat?
    private var lastAmbientBucket = -1

    func updateMastheadHeight(_ height: CGFloat) {
        policy.updateMastheadHeight(height)
        publishChrome(animated: false)
    }

    func updateBounds(contentHeight: CGFloat, viewportHeight: CGFloat) {
        policy.updateBounds(contentHeight: contentHeight, viewportHeight: viewportHeight)
        publishChrome(animated: false)
    }

    func update(top: CGFloat) {
        guard top.isFinite else { return }
        if anchor == nil { anchor = top }
        guard let anchor else { return }
        // Rubber-band motion beyond either edge is not a scroll reversal.
        // In particular, returning from bottom overscroll must not reveal the header.
        let value = anchor - top
        let previousOffset = policy.offset
        policy.update(clampedOffset: value)
        publishChrome(animated: false)

        if abs(policy.offset - previousOffset) > 0.25 {
            let bucket = Int(max(0, policy.offset) / 64)
            if bucket != lastAmbientBucket {
                lastAmbientBucket = bucket
                onScrollBucketChanged?(policy.offset)
            }
        }
    }

    /// Gently settles a partially revealed header once scrolling comes to a complete stop.
    func settleIfIdle() {
        guard policy.settleTarget() != nil else { return }
        publishChrome(animated: true)
    }

    private func publishChrome(animated: Bool) {
        let opacityChanged = abs(policy.glassOpacity - chrome.glassOpacity) > 0.001
            || ((policy.glassOpacity == 0 || policy.glassOpacity == 1) && policy.glassOpacity != chrome.glassOpacity)
        let progressChanged = abs(policy.mastheadProgress - chrome.mastheadProgress) > 0.001
            || ((policy.mastheadProgress == 0 || policy.mastheadProgress == 1) && policy.mastheadProgress != chrome.mastheadProgress)
        guard opacityChanged || progressChanged else { return }

        let apply = {
            if opacityChanged { self.chrome.glassOpacity = self.policy.glassOpacity }
            if progressChanged { self.chrome.mastheadProgress = self.policy.mastheadProgress }
        }
        if animated {
            withAnimation(.easeOut(duration: 0.16)) { apply() }
        } else {
            apply()
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

struct NativeLibraryScrollPhaseObserver: ViewModifier {
    let state: NativeLibraryScrollState
    func body(content: Content) -> some View {
        if #available(iOS 18.0, *) {
            content.onScrollPhaseChange { _, newPhase in
                if newPhase == .idle {
                    state.settleIfIdle()
                }
            }
        } else {
            content
        }
    }
}

struct NativeLibraryInlineMastheadHost<Content: View>: View {
    @ObservedObject var chrome: NativeLibraryChromeState
    @ViewBuilder let content: () -> Content

    var body: some View {
        let floatingActive = chrome.mastheadProgress > 0
        content()
            .opacity(floatingActive ? 0 : 1)
            .allowsHitTesting(!floatingActive)
            .accessibilityHidden(floatingActive)
    }
}

struct NativeLibraryFloatingMastheadHost<Content: View>: View {
    @ObservedObject var chrome: NativeLibraryChromeState
    let slideDistance: CGFloat
    @ViewBuilder let content: () -> Content

    var body: some View {
        let progress = chrome.mastheadProgress
        let travel = max(1, slideDistance)
        content()
            .opacity(Double(progress))
            .offset(y: -(1 - progress) * travel)
            .allowsHitTesting(progress > 0.35)
            .accessibilityHidden(progress <= 0)
    }
}

struct NativeMastheadGlass: View {
    @ObservedObject var chrome: NativeLibraryChromeState
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
            .opacity(chrome.glassOpacity)
    }
}

struct NativeLibraryStatusBarGlass: View {
    @ObservedObject var chrome: NativeLibraryChromeState
    var body: some View {
        NativeStatusBarGlass()
            .opacity(chrome.glassOpacity * (1 - Double(chrome.mastheadProgress)))
    }
}
