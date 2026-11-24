import SwiftUI
import UIKit

struct NativeReaderInsetsAcknowledgement: Decodable {
    let accepted: Bool
    let revision: Int64
}

private struct NativeReaderEntryKey: Equatable {
    let ready: Bool
    let frameReady: Bool
    let revision: Int64
    let acknowledgedRevision: Int64?
    let hasError: Bool
}

/// Mount the live surface at its final bounds behind one stable page. Reveal
/// exactly once after both the renderer and the native display are ready.
struct NativeReaderEntryPresentation: ViewModifier {
    @ObservedObject var store: NativeStore
    @ObservedObject var session: NativeReaderSession
    let sessionId: String
    let animation: NativeBookOpenAnimation
    let paper: Color
    let acknowledgedRevision: Int64?
    @State private var policy = NativeReaderPresentationPolicy()
    @Binding var prepared: Bool
    @State private var loadingFeedback = false
    @State private var transition: NativePageSlideTransition?
    @State private var revealTask: Task<Void, Never>?

    func body(content: Content) -> some View {
        let state = session.state
        let key = NativeReaderEntryKey(ready: state.ready, frameReady: state.presentationFrameReady,
            revision: state.presentationFrameRevision, acknowledgedRevision: acknowledgedRevision,
            hasError: state.error != nil)
        content
            .overlay {
                if !prepared && animation != .cover {
                    Group {
                        if store.widgetColdLaunch {
                            NativeWidgetLaunchBrandView(showProgress: loadingFeedback)
                        } else {
                            ZStack {
                                paper
                                if !loadingFeedback, let snapshot = store.readerEntrySnapshot {
                                    Image(uiImage: snapshot).resizable().scaledToFill()
                                }
                                if loadingFeedback {
                                    VStack(spacing: 14) {
                                        ProgressView()
                                        Text(L("正在打开书籍…")).font(NativeTheme.font(14))
                                    }
                                    .accessibilityIdentifier("reader.loading")
                                    VStack {
                                        HStack {
                                            Button { store.perform { await store.closeReader(animated: false) } } label: {
                                                Image(systemName: "chevron.left").frame(width: 44, height: 44)
                                            }
                                            .buttonStyle(.plain).accessibilityLabel(L("返回"))
                                            .accessibilityIdentifier("reader.close")
                                            Spacer()
                                        }
                                        Spacer()
                                    }.padding(.top, NativeTheme.statusBarHeight).padding(.horizontal, 8)
                                }
                            }
                        }
                    }
                    .ignoresSafeArea()
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier("reader.entry.cover")
                }
            }
            .task {
                guard animation != .cover else { return }
                await NativeDisplayFrame.waitForCommit()
                guard !Task.isCancelled else { return }
                try? await Task.sleep(nanoseconds: NativeReaderPresentationPolicy.loadingFeedbackDelay)
                guard !Task.isCancelled, !prepared else { return }
                loadingFeedback = true
                // The modal now supplies its own stable loading state; the
                // startup cover beneath it can safely release without a flash.
                if store.session.appearanceSessionId == sessionId && !store.widgetColdLaunch {
                    store.restoringReader = false
                }
            }
            .task(id: key) {
                guard animation != .cover, !prepared,
                      key.hasError || key.ready && key.frameReady && key.revision > 0
                        && key.acknowledgedRevision == key.revision else { return }
                await NativeDisplayFrame.waitForCommit()
                guard !Task.isCancelled,
                      session.state.presentationFrameRevision == key.revision,
                      policy.begin(ready: key.ready, frameReady: key.frameReady, frameRevision: key.revision,
                        acknowledgedRevision: key.acknowledgedRevision, hasError: key.hasError,
                        sessionIsCurrent: store.session.appearanceSessionId == sessionId && store.readerBook != nil) else { return }
                // Later reader/footer updates must not cancel an in-progress
                // handoff by changing the readiness task's identity.
                revealTask = Task { @MainActor in await revealReader() }
            }
            .onDisappear { revealTask?.cancel(); transition?.finish() }
    }

    private func revealReader() async {
        if store.widgetColdLaunch {
            await store.waitForMinimumWidgetBrandHold()
        }
        guard !Task.isCancelled, store.session.appearanceSessionId == sessionId else { return }
        let window = store.navigationWindow
        let snapshot = store.widgetColdLaunch
            ? (store.captureWidgetBrandSnapshot() ?? store.readerEntrySnapshot)
            : (loadingFeedback ? store.captureNavigationSnapshot() : store.readerEntrySnapshot)
        let handoff = NativePageSlideTransition(window: window, snapshot: snapshot, paper: UIColor(paper))
        transition = handoff
        store.dismissWidgetBrandCover()
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) { prepared = true }
        await NativeDisplayFrame.waitForCommit()
        guard !Task.isCancelled, store.session.appearanceSessionId == sessionId else { handoff?.finish(); return }
        let duration = store.readerOpenSource == .widget
            ? NativeReaderPresentationPolicy.widgetTransitionDuration
            : NativeBookOpenTransitionPolicy.pageTransitionDuration
        await handoff?.show(store.readerPresentationView, animation: animation, duration: duration)
        transition = nil
        if !Task.isCancelled { store.completeReaderEntry(sessionId: sessionId) }
    }
}
