import SwiftUI

/// Lists the two external feedback channels only. Opening this page never
/// requests account data, captures logs, reads photos or submits anything to
/// the account server.
struct NativeFeedbackPage: View {
    @ObservedObject var store: NativeStore

    var body: some View {
        NativeForm {
            Section {
                Link(destination: URL(string: "https://github.com/kingstacker/Libzora/issues")!) {
                    NativeSettingsRow(title: L("GitHub Issues"), chevron: true, verticalPadding: 2)
                }
                .accessibilityIdentifier("feedback.issues")
                Button {
                    UIPasteboard.general.string = "1109898894"
                    store.showToast(L("群号已复制到剪贴板"))
                } label: {
                    NativeSettingsRow(title: L("QQ 交流群"), value: "1109898894", chevron: false, verticalPadding: 2)
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("feedback.qqGroup")
            }
        }
        .navigationTitle(L("问题反馈"))
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeHideTabBar())
    }
}
