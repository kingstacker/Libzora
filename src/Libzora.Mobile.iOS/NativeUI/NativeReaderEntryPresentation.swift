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
    let navigationRevision: Int
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
    @State private var revealNavigationRevision: Int?

    func body(content: Content) -> some View {
        let state = session.state
        let key = NativeReaderEntryKey(ready: state.ready, frameReady: state.presentationFrameReady,
            revision: state.presentationFrameRevision, acknowledgedRevision: acknowledgedRevision,
            hasError: state.error != nil, navigationRevision: store.widgetNavigationIntent.revision)
        content
            .overlay {
                if !prepared && animation != .cover {
                    Group {
                        if store.launchBrandActive {
                            NativeLaunchBrandView(showProgress: loadingFeedback)
                        } else {
                            ZStack {
                                paper
                                if !loadingFeedback, store.widgetEntrySnapshotCover == nil,
                                   let snapshot = store.readerEntrySnapshot {
                                    Image(uiImage: snapshot)
                                        .resizable()
                                        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
                if store.session.appearanceSessionId == sessionId {
                    if store.launchBrandActive {
                        store.setLaunchBrandProgressVisible(true)
                    } else {
                        store.dismissWidgetEntrySnapshotCover()
                        store.restoringReader = false
                    }
                }
            }
            .task(id: key) {
                guard animation != .cover, !prepared,
                      key.hasError || key.ready && key.frameReady && key.revision > 0
                        && key.acknowledgedRevision == key.revision else { return }
                if revealNavigationRevision != key.navigationRevision {
                    revealTask?.cancel()
                    policy = NativeReaderPresentationPolicy()
                    revealNavigationRevision = key.navigationRevision
                }
                guard !Task.isCancelled,
                      session.state.presentationFrameRevision == key.revision,
                      policy.begin(ready: key.ready, frameReady: key.frameReady, frameRevision: key.revision,
                        acknowledgedRevision: key.acknowledgedRevision, hasError: key.hasError,
                        sessionIsCurrent: store.session.appearanceSessionId == sessionId && store.readerBook != nil
                            && (store.readerOpenSource != .widget
                                || store.widgetNavigationIntent.destination == store.readerBook.map { .book($0.id) })) else { return }
                // Later reader/footer updates must not cancel an in-progress
                // handoff by changing the readiness task's identity.
                revealTask = Task { @MainActor in await revealReader() }
            }
            .onDisappear {
                revealTask?.cancel()
                transition?.finish()
                if store.readerEntryTransition === transition { store.readerEntryTransition = nil }
            }
    }

    private func revealReader() async {
        let navigationRevision = store.widgetNavigationIntent.revision
        guard !Task.isCancelled, store.session.appearanceSessionId == sessionId,
              store.widgetNavigationIntent.isCurrent(navigationRevision) else { return }
        if store.launchBrandActive {
            if animation.isPageAnimation {
                await store.waitForMinimumLaunchBrandHold()
            }
            await store.waitForInitialAmbientColorsIfNeeded()
            guard !Task.isCancelled, store.session.appearanceSessionId == sessionId,
              store.widgetNavigationIntent.isCurrent(navigationRevision) else { return }
        }
        let window = store.navigationWindow
        let handoff: NativePageSlideTransition?
        if store.launchBrandActive {
            let snapshot = store.captureLaunchBrandSnapshot() ?? store.readerEntrySnapshot
            let transitionPaper = animation.isPageAnimation ? (store.launchBrandPageColor ?? UIColor(paper)) : UIColor(paper)
            handoff = NativePageSlideTransition(window: window, snapshot: snapshot, paper: transitionPaper)
        } else if !loadingFeedback, let oldView = store.takeWidgetEntrySnapshotCover() {
            handoff = NativePageSlideTransition(window: window, oldPageView: oldView, paper: UIColor(paper))
        } else {
            let snapshot = loadingFeedback ? store.captureNavigationSnapshot() : store.readerEntrySnapshot
            handoff = NativePageSlideTransition(window: window, snapshot: snapshot, paper: UIColor(paper))
        }
        transition = handoff
        store.readerEntryTransition = handoff
        defer {
            handoff?.finish()
            if store.readerEntryTransition === handoff { store.readerEntryTransition = nil }
        }
        store.dismissLaunchBrandCover()
        store.dismissWidgetEntrySnapshotCover()
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) { prepared = true }
        await NativeDisplayFrame.waitForCommit()
        guard !Task.isCancelled, store.session.appearanceSessionId == sessionId,
              store.widgetNavigationIntent.isCurrent(navigationRevision) else { handoff?.finish(); return }
        let duration = store.readerOpenSource == .widget || store.readerOpenSource == .restore
            ? NativeReaderPresentationPolicy.widgetTransitionDuration
            : NativeBookOpenTransitionPolicy.pageTransitionDuration
        let snapshot = await store.captureReaderPresentationSnapshot(sessionId: sessionId)
        guard !Task.isCancelled, store.session.appearanceSessionId == sessionId,
              store.widgetNavigationIntent.isCurrent(navigationRevision) else { handoff?.finish(); return }
        await handoff?.show(store.readerPresentationView, snapshot: snapshot, animation: animation, duration: duration)
        if transition === handoff { transition = nil }
        if !Task.isCancelled, store.widgetNavigationIntent.isCurrent(navigationRevision) {
            store.completeReaderEntry(sessionId: sessionId)
        }
    }
}
