import SwiftUI
import UIKit

/// First-launch welcome guide requiring explicit acceptance of the bundled
/// User Agreement and Privacy Policy before entering the library.
struct NativeWelcomeView: View {
    @ObservedObject var store: NativeStore
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont
    @State private var agreed = false
    @State private var activeLegal: NativeLegalDocumentPage.Kind?

    private static let appIconImage: UIImage? = {
        if let icons = Bundle.main.infoDictionary?["CFBundleIcons"] as? [String: Any],
           let primary = icons["CFBundlePrimaryIcon"] as? [String: Any],
           let files = primary["CFBundleIconFiles"] as? [String] {
            for name in files.reversed() {
                if let image = UIImage(named: name) { return image }
            }
        }
        return UIImage(named: "Libzora60x60")
    }()

    var body: some View {
        let _ = uiLocale
        let _ = uiFont

        VStack(spacing: 0) {
            ScrollView(showsIndicators: false) {
                VStack(spacing: 28) {
                    header
                    features
                }
                .frame(maxWidth: 460)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 28)
                .padding(.top, 44)
                .padding(.bottom, 24)
            }

            consentFooter
                .frame(maxWidth: 460)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 24)
                .padding(.top, 12)
                .padding(.bottom, 24)
                .background(
                    NativeTheme.page
                        .shadow(color: .black.opacity(0.05), radius: 10, y: -4)
                        .ignoresSafeArea(edges: .bottom)
                )
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(NativeTheme.page.ignoresSafeArea())
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("welcome.screen")
        .sheet(item: $activeLegal) { kind in
            NavigationView {
                NativeLegalDocumentPage(store: store, kind: kind)
                    .toolbar {
                        ToolbarItem(placement: .confirmationAction) {
                            Button(L("完成")) { activeLegal = nil }
                                .accessibilityIdentifier("welcome.legal.done")
                        }
                    }
            }
            .navigationViewStyle(.stack)
            .font(NativeTheme.font())
            .foregroundStyle(NativeTheme.ink)
            .tint(NativeTheme.ink)
            .environment(\.locale, Locale(identifier: store.uiLanguage))
            .environment(\.nativeUiFont, store.uiFont)
        }
    }

    private var header: some View {
        VStack(spacing: 14) {
            if let icon = Self.appIconImage {
                Image(uiImage: icon)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 72, height: 72)
                    .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 18, style: .continuous)
                            .stroke(NativeTheme.ink.opacity(0.08), lineWidth: 0.5)
                    )
                    .shadow(color: .black.opacity(0.08), radius: 12, y: 5)
            }
            Text(L("欢迎使用 Libzora"))
                .font(NativeTheme.font(26, relativeTo: .title).weight(.semibold))
                .multilineTextAlignment(.center)
                .foregroundStyle(NativeTheme.ink)
                .accessibilityIdentifier("welcome.title")
            Text(L("静心阅读，数据归己。"))
                .font(NativeTheme.font(15, relativeTo: .subheadline))
                .foregroundStyle(NativeTheme.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var features: some View {
        VStack(alignment: .leading, spacing: 18) {
            featureRow(
                symbol: "book.pages",
                title: L("精排离线阅读"),
                detail: L("支持 EPUB、PDF、漫画等常见电子书格式，专注舒适排版与沉浸阅读体验。")
            )
            featureRow(
                symbol: "highlighter",
                title: L("批注、统计与同步"),
                detail: L("统一管理划线笔记、读后思考与阅读数据，支持自备 S3 / WebDAV 端到端加密同步。")
            )
            featureRow(
                symbol: "lock.shield",
                title: L("隐私优先"),
                detail: L("书库与同步密钥仅保存在你的设备中，无广告、无第三方追踪，离线即可使用。")
            )
        }
        .padding(20)
        .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }

    private func featureRow(symbol: String, title: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: symbol)
                .font(.system(size: 20, weight: .regular))
                .foregroundStyle(NativeTheme.ink)
                .frame(width: 34, height: 34)
                .background(NativeTheme.page, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(NativeTheme.font(15, relativeTo: .headline).weight(.semibold))
                    .foregroundStyle(NativeTheme.ink)
                Text(detail)
                    .font(NativeTheme.font(13, relativeTo: .footnote))
                    .foregroundStyle(NativeTheme.secondary)
                    .lineSpacing(3)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var consentFooter: some View {
        VStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 10) {
                Button {
                    withAnimation(.easeInOut(duration: 0.15)) { agreed.toggle() }
                } label: {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: agreed ? "checkmark.circle.fill" : "circle")
                            .font(.system(size: 20))
                            .foregroundStyle(agreed ? Color(uiColor: .systemBlue) : NativeTheme.muted)
                            .padding(.top, 1)
                        Text(L("我已阅读并同意《用户协议》和《隐私政策》"))
                            .font(NativeTheme.font(13.5, relativeTo: .subheadline))
                            .foregroundStyle(NativeTheme.ink)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(agreed ? .isSelected : [])
                .accessibilityIdentifier("welcome.agree.toggle")

                HStack(spacing: 12) {
                    legalPillButton(L("阅读用户协议"), kind: .terms, identifier: "welcome.terms")
                    legalPillButton(L("阅读隐私政策"), kind: .privacy, identifier: "welcome.privacy")
                }
                .padding(.leading, 30)
            }

            Button {
                accept()
            } label: {
                Text(L("同意并开始使用"))
                    .font(NativeTheme.font(16, relativeTo: .headline).weight(.semibold))
                    .foregroundStyle(agreed ? NativeTheme.page : NativeTheme.muted)
                    .frame(maxWidth: .infinity)
                    .frame(height: 50)
                    .background(
                        agreed ? NativeTheme.ink : NativeTheme.sunk,
                        in: RoundedRectangle(cornerRadius: 14, style: .continuous)
                    )
            }
            .buttonStyle(.plain)
            .disabled(!agreed)
            .accessibilityIdentifier("welcome.accept")

            if !agreed {
                Text(L("需勾选同意用户协议与隐私政策后方可继续使用"))
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(NativeTheme.muted)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private func legalPillButton(_ title: String, kind: NativeLegalDocumentPage.Kind, identifier: String) -> some View {
        Button {
            activeLegal = kind
        } label: {
            Text(title)
                .font(NativeTheme.font(12.5, relativeTo: .caption).weight(.medium))
                .foregroundStyle(Color(uiColor: .systemBlue))
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(NativeTheme.sunk, in: Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier(identifier)
    }

    private func accept() {
        guard agreed else { return }
        var updated = store.preferences
        updated.onboardingAccepted = true
        store.setPreferences(updated)
    }
}
