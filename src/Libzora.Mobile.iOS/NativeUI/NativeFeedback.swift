import SwiftUI

/// A support guide only. Opening this page never requests account data,
/// captures logs, reads photos or submits anything to the account server.
struct NativeFeedbackPage: View {
    @ObservedObject var store: NativeStore

    var body: some View {
        NativeForm {
            Section {
                Text(L("账号服务不接收反馈、截图或日志，请使用 GitHub Issues。"))
                    .font(NativeTheme.font(13))
                    .foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("feedback.serviceBoundary")
                Text(L("GitHub Issues 是外部公开渠道，发布的内容可能被缓存或索引。分享前请检查并移除账号、邮箱、密钥、书籍内容、文件路径等敏感信息；应用不会自动附加或上传任何内容。"))
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(NativeTheme.muted)
            }
            Section(L("反馈渠道")) {
                Link(destination: URL(string: "https://github.com/kingstacker/Libzora/issues")!) {
                    NativeSettingsRow(title: L("打开 GitHub Issues"), chevron: false)
                }
                .accessibilityIdentifier("feedback.issues")
                Text(verbatim: "https://github.com/kingstacker/Libzora/issues")
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(NativeTheme.muted)
                    .textSelection(.enabled)
            }
        }
        .navigationTitle(L("问题反馈"))
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeHideTabBar())
    }
}
