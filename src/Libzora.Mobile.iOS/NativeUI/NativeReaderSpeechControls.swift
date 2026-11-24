import SwiftUI
import UIKit

/// Keep playback reachable without leaving a full toolbar over the page.
/// Only interactions restart the delay; speech progress can update freely.
struct NativeReaderSpeechControls: View {
    @Environment(\.locale) private var uiLocale
    let state: Int
    let status: String
    let speed: Double
    let sleepMinutesRemaining: Int
    let sleepAfterChapter: Bool
    let suspended: Bool
    @Binding var expanded: Bool
    let setSpeed: (Double) -> Void
    let setSleepTimer: (Int, Bool) -> Void
    let settings: () -> Void
    let pause: () -> Void
    let stop: () -> Void
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    // This SwiftUI tree is embedded in UIKit. Its scenePhase can remain
    // inactive even while the reader is visible, so use UIKit's lifecycle.
    @State private var appActive = UIApplication.shared.applicationState == .active
    @State private var interaction = 0

    private static let quickSpeeds: [Double] = [0.8, 1.0, 1.2, 1.5, 1.8, 2.0]

    private struct DockRequest: Equatable {
        let interaction: Int
        let armed: Bool
    }

    private var dockRequest: DockRequest {
        DockRequest(interaction: interaction,
                    armed: expanded && !suspended && appActive)
    }

    var body: some View {
        let _ = uiLocale

        ZStack(alignment: .trailing) {
            if expanded {
                toolbar
                    .padding(.horizontal, 8)
                    .transition(transition)
            } else {
                dock.transition(transition)
            }
        }
        .frame(maxWidth: .infinity, alignment: .trailing)
        .frame(height: 48)
        .padding(.bottom, 8)
        .animation(reduceMotion ? nil : .easeInOut(duration: 0.25), value: expanded)
        .onAppear {
            appActive = UIApplication.shared.applicationState == .active
            expanded = true
        }
        .onReceive(NotificationCenter.default.publisher(for: UIApplication.didBecomeActiveNotification)) { _ in
            appActive = true
        }
        .onReceive(NotificationCenter.default.publisher(for: UIApplication.willResignActiveNotification)) { _ in
            appActive = false
        }
        .task(id: dockRequest) {
            guard dockRequest.armed else { return }
            do { try await Task.sleep(nanoseconds: 4_000_000_000) }
            catch { return }
            guard !Task.isCancelled, dockRequest.armed else { return }
            expanded = false
        }
    }

    private var transition: AnyTransition {
        reduceMotion ? .opacity : .move(edge: .trailing).combined(with: .opacity)
    }

    private func cycleNextSpeed() -> Double {
        let current = Self.quickSpeeds.firstIndex { abs($0 - speed) < 0.08 }
            ?? Self.quickSpeeds.firstIndex(of: 1.0)
            ?? 1
        return Self.quickSpeeds[(current + 1) % Self.quickSpeeds.count]
    }

    private var sleepBadge: String? {
        if sleepAfterChapter { return L("本章") }
        if sleepMinutesRemaining > 0 { return "\(sleepMinutesRemaining)m" }
        return nil
    }

    private var toolbar: some View {
        HStack(spacing: 4) {
            Image(systemName: "headphones")
                .frame(width: 24)
                .accessibilityHidden(true)
            Text(status)
                .font(NativeTheme.font(12, relativeTo: .caption))
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityIdentifier("reader.speech.status")
            Button {
                interaction += 1
                setSpeed(cycleNextSpeed())
            } label: {
                Text("\(speed, specifier: "%.1f")×")
                    .font(NativeTheme.font(12, relativeTo: .caption).monospacedDigit())
                    .padding(.horizontal, 6)
                    .frame(minWidth: 40, minHeight: 44)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel(L("切换朗读倍速"))
            .accessibilityIdentifier("reader.speech.speed")
            Menu {
                Button(L("15 分钟")) { interaction += 1; setSleepTimer(15, false) }
                Button(L("30 分钟")) { interaction += 1; setSleepTimer(30, false) }
                Button(L("60 分钟")) { interaction += 1; setSleepTimer(60, false) }
                Button(L("播完本章")) { interaction += 1; setSleepTimer(0, true) }
                if sleepAfterChapter || sleepMinutesRemaining > 0 {
                    Divider()
                    Button(L("关闭定时"), role: .destructive) { interaction += 1; setSleepTimer(0, false) }
                }
            } label: {
                HStack(spacing: 2) {
                    Image(systemName: "moon.zzz")
                        .font(.system(size: 14, weight: .regular))
                    if let badge = sleepBadge {
                        Text(badge)
                            .font(NativeTheme.font(11, relativeTo: .caption2).monospacedDigit())
                    }
                }
                .foregroundStyle(sleepBadge == nil ? NativeTheme.ink : Color.accentColor)
                .padding(.horizontal, 4)
                .frame(minWidth: 36, minHeight: 44)
                .contentShape(Rectangle())
            }
            .accessibilityLabel(L("睡眠定时"))
            .accessibilityIdentifier("reader.speech.sleep")
            Button {
                interaction += 1
                settings()
            } label: {
                Image(systemName: "slider.horizontal.3")
                    .font(.system(size: 15, weight: .regular))
                    .frame(width: 38, height: 44)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel(L("朗读设置"))
            .accessibilityIdentifier("reader.speech.settings")
            Button {
                interaction += 1
                pause()
            } label: {
                Image(systemName: state == 3 ? "play.fill" : "pause.fill")
                    .frame(width: 40, height: 44)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel(state == 3 ? L("继续朗读") : L("暂停朗读"))
            .accessibilityIdentifier("reader.speech.pause")
            Button {
                interaction += 1
                stop()
            } label: {
                Image(systemName: "xmark").frame(width: 38, height: 44)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel(L("停止朗读"))
            .accessibilityIdentifier("reader.speech.stop")
        }
        .font(.system(size: 17, weight: .regular))
        .buttonStyle(NativeGlassToolbarButtonStyle())
        .padding(.leading, 14)
        .padding(.trailing, 6)
        .frame(height: 48)
        .background {
            Color.clear
                .contentShape(Capsule())
                .onTapGesture { interaction += 1 }
                .accessibilityHidden(true)
        }
        .modifier(NativeLiquidGlassSurface(shape: Capsule()))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reader.speech.bar")
    }

    private var dock: some View {
        Button {
            interaction += 1
            expanded = true
        } label: {
            Image(systemName: state == 3 ? "pause.fill" : "headphones")
                .font(.system(size: 18, weight: .regular))
                .frame(width: 44, height: 48)
                .contentShape(Rectangle())
        }
        .buttonStyle(NativeGlassToolbarButtonStyle())
        .modifier(NativeLiquidGlassSurface(shape: NativeSpeechDockShape()))
        .accessibilityLabel(L("展开朗读控制"))
        .accessibilityValue(status)
        .accessibilityIdentifier("reader.speech.dock")
    }
}

private struct NativeSpeechDockShape: Shape {
    func path(in rect: CGRect) -> Path {
        Path(UIBezierPath(roundedRect: rect, byRoundingCorners: [.topLeft, .bottomLeft],
                          cornerRadii: CGSize(width: 18, height: 18)).cgPath)
    }
}
