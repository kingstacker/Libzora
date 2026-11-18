import UIKit

/// System alerts are presented from the current native controller, including
/// an active form/sheet. Their lifetime never depends on Avalonia overlays.
@MainActor
enum NativeAlerts {
    private struct Pending {
        let id: Int64
        let title: String
        let message: String
        let choices: [NativePromptChoice]
        let completion: (String) -> Void
    }
    private static var queue: [Pending] = []
    private static var active: Pending?
    private static var controller: UIAlertController?
    private static var errorId: Int64 = -1
    private static var retry: Task<Void, Never>?

    static func confirm(_ prompt: NativePrompt, store: NativeStore) {
        let defaults = [NativePromptChoice(id: "confirm", title: prompt.confirm, cancel: false),
                        NativePromptChoice(id: "cancel", title: L("取消"), cancel: true)]
        queue.append(Pending(id: prompt.id, title: prompt.title, message: prompt.message,
            choices: prompt.choices.flatMap { $0.isEmpty ? nil : $0 } ?? defaults,
            completion: { [weak store] choice in store?.answerPrompt(prompt, choice: choice) }))
        presentNext()
    }
    static func error(_ text: String, dismissed: @escaping () -> Void) {
        errorId -= 1
        queue.append(Pending(id: errorId, title: L("操作失败"), message: text,
            choices: [NativePromptChoice(id: "confirm", title: L("好"), cancel: false)],
            completion: { _ in dismissed() }))
        presentNext()
    }
    static func dismissPrompt(_ id: Int64) {
        queue.removeAll { $0.id == id }
        guard active?.id == id else { return }
        active = nil
        let previous = controller; controller = nil
        previous?.dismiss(animated: true) { presentNext() }
    }
    private static func presentNext() {
        guard active == nil, !queue.isEmpty else { return }
        guard let presenter = topController(), !(presenter is UIAlertController), !presenter.isBeingDismissed else {
            scheduleNext(); return
        }
        let value = queue.removeFirst(); active = value
        let alert = UIAlertController(title: value.title, message: value.message, preferredStyle: .alert)
        for choice in value.choices {
            alert.addAction(UIAlertAction(title: choice.title, style: choice.cancel == true ? .cancel : .default) { _ in
                finish(value, choice: choice.id)
            })
        }
        controller = alert
        presenter.present(alert, animated: true)
    }
    private static func finish(_ value: Pending, choice: String) {
        guard active?.id == value.id else { return }
        active = nil; controller = nil; value.completion(choice); scheduleNext()
    }
    private static func scheduleNext() {
        retry?.cancel()
        retry = Task {
            do { try await Task.sleep(nanoseconds: 250_000_000) } catch { return }
            presentNext()
        }
    }
    private static func topController() -> UIViewController? {
        let windows = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap(\.windows)
        guard var view = (windows.first { $0.isKeyWindow } ?? windows.first { !$0.isHidden })?.rootViewController else { return nil }
        while true {
            if let presented = view.presentedViewController { view = presented }
            else if let navigation = view as? UINavigationController, let visible = navigation.visibleViewController { view = visible }
            else if let tabs = view as? UITabBarController, let selected = tabs.selectedViewController { view = selected }
            else { return view }
        }
    }
}
