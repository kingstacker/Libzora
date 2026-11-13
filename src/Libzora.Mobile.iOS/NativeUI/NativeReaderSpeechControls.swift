import SwiftUI
import UIKit

/// Keep playback reachable without leaving a full toolbar over the page.
/// Only interactions restart the delay; speech progress can update freely.
struct NativeReaderSpeechControls: View {
    let state: Int
    let status: String
    let suspended: Bool
    @Binding var expanded: Bool
    let settings: () -> Void
    let pause: () -> Void
    let stop: () -> Void
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    // This SwiftUI tree is embedded in UIKit. Its scenePhase can remain
    // inactive even while the reader is visible, so use UIKit's lifecycle.
    @State private var appActive = UIApplication.shared.applicationState == .active
    @State private var interaction = 0

    private struct DockRequest: Equatable {
        let interaction: Int
        let armed: Bool
    }

    private var dockRequest: DockRequest {
        DockRequest(interaction: interaction,
                    armed: expanded && !suspended && appActive)
    }

    var body: some View {
        ZStack(alignment: .trailing) {
            if expanded {
                toolbar
                    .padding(.horizontal, 12)
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

    private var toolbar: some View {
        HStack(spacing: 6) {
            Image(systemName: "headphones")
                .frame(width: 26)
                .accessibilityHidden(true)
            Text(status)
                .font(NativeTheme.font(12, relativeTo: .caption))
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityIdentifier("reader.speech.status")
            Button {
                interaction += 1
                settings()
            } label: {
                Text("设置").font(NativeTheme.font(12, relativeTo: .caption))
                    .frame(width: 44, height: 44)
            }
            .accessibilityLabel("朗读设置")
            .accessibilityIdentifier("reader.speech.settings")
            Button {
                interaction += 1
                pause()
            } label: {
                Image(systemName: state == 3 ? "play.fill" : "pause.fill")
                    .frame(width: 44, height: 44)
            }
            .accessibilityLabel(state == 3 ? "继续朗读" : "暂停朗读")
            .accessibilityIdentifier("reader.speech.pause")
            Button {
                interaction += 1
                stop()
            } label: {
                Image(systemName: "xmark").frame(width: 44, height: 44)
            }
            .accessibilityLabel("停止朗读")
            .accessibilityIdentifier("reader.speech.stop")
        }
        .font(.system(size: 17, weight: .regular))
        .buttonStyle(.plain)
        .padding(.leading, 12)
        .padding(.trailing, 4)
        .frame(height: 48)
        .modifier(NativeFloatingSurface())
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
        .buttonStyle(.plain)
        .background(NativeSpeechDockShape().fill(NativeTheme.surface.opacity(0.97)))
        .overlay(NativeSpeechDockShape().stroke(NativeTheme.divider, lineWidth: 0.5))
        .shadow(color: .black.opacity(scheme == .dark ? 0.25 : 0.10), radius: 10, y: 4)
        .accessibilityLabel("展开朗读控制")
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
