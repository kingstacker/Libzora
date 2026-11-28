import Foundation

@MainActor
extension NativeStore {
    func receiveFootnote(_ footnote: ReaderFootnote?) {
        session.footnote = footnote?.visible == true ? footnote : nil
#if DEBUG
        // The isolated fixture deterministically delivers a late footer-label
        // refresh after the first marker tap. No reader navigation takes place,
        // and no backend progress or user-library data is modified.
        guard Bundle.main.bundleIdentifier == "com.kingstacker.libzora.readerfootnotepreview",
              ProcessInfo.processInfo.arguments.contains("--libzora-test-late-footnote-labels"),
              let footnote, footnote.visible, !footnote.loading else { return }
        let sessionId = session.appearanceSessionId
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard let self, !Task.isCancelled,
                  self.session.appearanceSessionId == sessionId,
                  self.session.footnote?.id == footnote.id else { return }
            var refreshed = self.session.state
            refreshed.position += " ·"
            refreshed.chapter += " "
            self.session.state = refreshed
        }
#endif
    }
}
