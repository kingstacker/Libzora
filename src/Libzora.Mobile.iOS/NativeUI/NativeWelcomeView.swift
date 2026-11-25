import SwiftUI
import UIKit

/// Multi-step interactive first-launch onboarding allowing quick appearance and
/// reading-paper setup before requiring explicit acceptance of the bundled
/// User Agreement and Privacy Policy.
struct NativeWelcomeView: View {
    @ObservedObject var store: NativeStore
    @Environment(\.locale) private var uiLocale
    @Environment(\.nativeUiFont) private var uiFont
    @Environment(\.colorScheme) private var systemColorScheme
    @State private var currentStep = 0
    @State private var agreed = false
    @State private var activeLegal: NativeLegalDocumentPage.Kind?
    @State private var lastOpenedLegal: NativeLegalDocumentPage.Kind?
    @State private var loadedActiveLegal = false
    @State private var viewedTerms = false
    @State private var viewedPrivacy = false

    private let totalSteps = 4

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

    private var effectiveColorScheme: ColorScheme {
        switch store.preferences.themeMode {
        case 1: return .light
        case 2: return .dark
        default: return systemColorScheme
        }
    }

    var body: some View {
        let _ = uiLocale
        let _ = uiFont

        VStack(spacing: 0) {
            topNavigationBar
                .frame(maxWidth: 500)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 24)
                .padding(.top, 14)
                .padding(.bottom, 8)

            TabView(selection: $currentStep) {
                stepContainer { welcomeAppearanceStep }
                    .tag(0)
                stepContainer { readingPreviewStep }
                    .tag(1)
                stepContainer { libraryAndSyncStep }
                    .tag(2)
                stepContainer { consentStep }
                    .tag(3)
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            .animation(.easeInOut(duration: 0.24), value: currentStep)

            bottomFooter
                .frame(maxWidth: 500)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 24)
                .padding(.top, 12)
                .padding(.bottom, 20)
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
        .sheet(item: $activeLegal, onDismiss: {
            if loadedActiveLegal, let kind = lastOpenedLegal {
                if kind == .terms { viewedTerms = true } else { viewedPrivacy = true }
            }
            lastOpenedLegal = nil
            loadedActiveLegal = false
        }) { kind in
            NavigationView {
                NativeLegalDocumentPage(store: store, kind: kind) {
                    loadedActiveLegal = true
                }
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

    private func stepContainer<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        ScrollView(showsIndicators: false) {
            content()
                .frame(maxWidth: 500)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 24)
                .padding(.top, 12)
                .padding(.bottom, 24)
        }
    }

    // MARK: - Top Navigation & Progress

    private var topNavigationBar: some View {
        VStack(spacing: 12) {
            HStack {
                Text("\(currentStep + 1) / \(totalSteps)")
                    .font(NativeTheme.font(12.5, relativeTo: .caption).monospacedDigit())
                    .foregroundStyle(NativeTheme.muted)

                Spacer()

                Button {
                    withAnimation(.easeInOut(duration: 0.24)) {
                        currentStep = totalSteps - 1
                    }
                } label: {
                    Text(L("跳过介绍"))
                        .font(NativeTheme.font(13, relativeTo: .caption).weight(.medium))
                        .foregroundStyle(NativeTheme.secondary)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(NativeTheme.sunk, in: Capsule())
                }
                .buttonStyle(.plain)
                .opacity(currentStep < totalSteps - 1 ? 1 : 0)
                .allowsHitTesting(currentStep < totalSteps - 1)
                .accessibilityHidden(currentStep >= totalSteps - 1)
                .accessibilityIdentifier("welcome.skip")
            }

            HStack(spacing: 6) {
                ForEach(0..<totalSteps, id: \.self) { index in
                    Capsule()
                        .fill(index <= currentStep ? NativeTheme.ink : NativeTheme.strong)
                        .frame(height: 3.5)
                        .contentShape(Rectangle())
                        .onTapGesture {
                            withAnimation(.easeInOut(duration: 0.22)) {
                                currentStep = index
                            }
                        }
                }
            }
        }
    }

    // MARK: - Step 1: Welcome & Style Personalization

    private var welcomeAppearanceStep: some View {
        VStack(spacing: 24) {
            VStack(spacing: 12) {
                if let icon = Self.appIconImage {
                    Image(uiImage: icon)
                        .resizable()
                        .scaledToFit()
                        .frame(width: 68, height: 68)
                        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                        .overlay(
                            RoundedRectangle(cornerRadius: 16, style: .continuous)
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

                Text(L("为学日益，为道日损。"))
                    .font(NativeTheme.font(12.5, relativeTo: .caption))
                    .foregroundStyle(NativeTheme.muted)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 5)
                    .background(NativeTheme.sunk, in: Capsule())
            }
            .padding(.top, 4)

            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(L("先定下你习惯的界面风格"))
                        .font(NativeTheme.font(15.5, relativeTo: .headline).weight(.semibold))
                        .foregroundStyle(NativeTheme.ink)
                    Text(L("选择后会实时预览，之后也可随时在「我的」里调整。"))
                        .font(NativeTheme.font(12.5, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.muted)
                }

                VStack(alignment: .leading, spacing: 8) {
                    Text(L("语言"))
                        .font(NativeTheme.font(13, relativeTo: .caption).weight(.medium))
                        .foregroundStyle(NativeTheme.secondary)

                    Menu {
                        let allOptions = [("auto", L("跟随系统"))] + NativeLocalization.supportedLanguages
                        ForEach(allOptions, id: \.0) { option in
                            Button {
                                updatePreference { $0.uiLanguage = option.0 }
                            } label: {
                                HStack {
                                    Text(option.1)
                                    if store.preferences.uiLanguage == option.0 {
                                        Image(systemName: "checkmark")
                                    }
                                }
                            }
                        }
                    } label: {
                        HStack(spacing: 8) {
                            Text(NativeLocalization.displayName(for: store.preferences.uiLanguage))
                                .font(NativeTheme.font(14, relativeTo: .subheadline).weight(.medium))
                                .foregroundStyle(NativeTheme.ink)
                                .lineLimit(1)
                            Spacer(minLength: 8)
                            Image(systemName: "chevron.up.chevron.down")
                                .font(.system(size: 11, weight: .semibold))
                                .foregroundStyle(NativeTheme.secondary)
                        }
                        .padding(.horizontal, 12)
                        .padding(.vertical, 10)
                        .background(NativeTheme.page, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                    }
                    .accessibilityIdentifier("welcome.language")
                }

                preferenceSegmentRow(
                    title: L("外观"),
                    options: [
                        (0, L("跟随系统")),
                        (1, L("浅色")),
                        (2, L("深色"))
                    ],
                    selection: store.preferences.themeMode
                ) { value in
                    updatePreference { $0.themeMode = value }
                }

                VStack(alignment: .leading, spacing: 8) {
                    Text(L("界面字体"))
                        .font(NativeTheme.font(13, relativeTo: .caption).weight(.medium))
                        .foregroundStyle(NativeTheme.secondary)

                    HStack(spacing: 10) {
                        fontPreviewTile(
                            title: L("京华老宋体"),
                            sample: L("温故而知新"),
                            useSystem: false,
                            selected: store.preferences.uiFont == 0
                        ) {
                            updatePreference { $0.uiFont = 0 }
                        }
                        fontPreviewTile(
                            title: L("系统默认"),
                            sample: L("温故而知新"),
                            useSystem: true,
                            selected: store.preferences.uiFont == 1
                        ) {
                            updatePreference { $0.uiFont = 1 }
                        }
                    }
                }
            }
            .padding(18)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        }
    }

    // MARK: - Step 2: Refined Reading & Live Page Preview

    private var readingPreviewStep: some View {
        VStack(alignment: .leading, spacing: 18) {
            stepHeader(
                title: L("精排阅读，赏心悦目"),
                subtitle: L("支持 EPUB、PDF、漫画与 TXT，针对中文直排与中西混排深度优化。")
            )

            liveReaderPreviewCard

            VStack(alignment: .leading, spacing: 14) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(L("纸张"))
                        .font(NativeTheme.font(13, relativeTo: .caption).weight(.medium))
                        .foregroundStyle(NativeTheme.secondary)
                    HStack(spacing: 8) {
                        paperChip(label: L("跟随"), tag: -1, swatch: effectiveColorScheme == .dark ? Color(red: 14 / 255, green: 14 / 255, blue: 14 / 255) : .white)
                        paperChip(label: L("白"), tag: 0, swatch: .white)
                        paperChip(label: L("米"), tag: 1, swatch: Color(red: 244 / 255, green: 242 / 255, blue: 236 / 255))
                        paperChip(label: L("绿"), tag: 2, swatch: Color(red: 238 / 255, green: 243 / 255, blue: 236 / 255))
                        paperChip(label: L("黑"), tag: 5, swatch: Color(red: 14 / 255, green: 14 / 255, blue: 14 / 255))
                    }
                }

                HStack(spacing: 12) {
                    preferenceSegmentRow(
                        title: L("排版方向"),
                        options: [(false, L("横排")), (true, L("竖排"))],
                        selection: store.preferences.readerVerticalWriting
                    ) { value in
                        updatePreference { $0.readerVerticalWriting = value }
                    }

                    preferenceSegmentRow(
                        title: L("首行缩进"),
                        options: [(false, L("关")), (true, L("开"))],
                        selection: store.preferences.readerParagraphIndent
                    ) { value in
                        updatePreference { $0.readerParagraphIndent = value }
                    }
                }
            }
            .padding(16)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        }
    }

    private var liveReaderPreviewCard: some View {
        let effectivePaperTheme = store.preferences.readerThemeFollowsAppearance
            ? (effectiveColorScheme == .dark ? 5 : 0)
            : store.preferences.readerTheme
        let paperBg = store.preferences.paper(for: effectivePaperTheme)
        let isDarkPaper = effectivePaperTheme == 5
        let textColor = isDarkPaper
            ? Color(red: 232 / 255, green: 232 / 255, blue: 232 / 255)
            : Color(red: 28 / 255, green: 28 / 255, blue: 28 / 255)
        let subTextColor = isDarkPaper
            ? Color(red: 160 / 255, green: 160 / 255, blue: 160 / 255)
            : Color(red: 110 / 255, green: 110 / 255, blue: 110 / 255)
        let indentPrefix = store.preferences.readerParagraphIndent ? "　　" : ""

        return VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(L("第一章 · 序言"))
                    .font(NativeTheme.font(11.5, relativeTo: .caption2))
                    .foregroundStyle(subTextColor)
                Spacer()
                Text(store.preferences.readerVerticalWriting ? L("竖排预览") : L("横排预览"))
                    .font(NativeTheme.font(11, relativeTo: .caption2))
                    .foregroundStyle(subTextColor)
            }

            if store.preferences.readerVerticalWriting {
                verticalPreviewColumns(textColor: textColor, indent: store.preferences.readerParagraphIndent)
                    .frame(maxWidth: .infinity, minHeight: 132, alignment: .trailing)
            } else {
                VStack(alignment: .leading, spacing: 8) {
                    Text(indentPrefix + L("读书之法，在循序而渐进，熟读而精思。字句既明，则义理自见；纸墨静好，方能心无旁骛。"))
                        .font(NativeTheme.font(14.5, relativeTo: .body))
                        .lineSpacing(5)
                        .foregroundStyle(textColor)
                    Text(indentPrefix + L("横排优化中西混排间距，竖排保留传统右起直书，长篇阅读久看不累。"))
                        .font(NativeTheme.font(14.5, relativeTo: .body))
                        .lineSpacing(5)
                        .foregroundStyle(textColor)
                }
                .frame(maxWidth: .infinity, minHeight: 132, alignment: .topLeading)
            }

            HStack {
                Text("12 / 248")
                Spacer()
                Text("5%")
            }
            .font(NativeTheme.font(11, relativeTo: .caption2).monospacedDigit())
            .foregroundStyle(subTextColor)
        }
        .padding(18)
        .background(paperBg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .stroke(NativeTheme.ink.opacity(0.12), lineWidth: 0.75)
        )
        .shadow(color: .black.opacity(0.05), radius: 10, y: 4)
    }

    private func verticalPreviewColumns(textColor: Color, indent: Bool) -> some View {
        let p1 = L("读书之法，在循序而渐进，熟读而精思。字句既明，则义理自见；纸墨静好，方能心无旁骛。")
        let p2 = L("横排优化中西混排间距，竖排保留传统右起直书，长篇阅读久看不累。")
        if !NativeLocalization.usesVerticalColumns {
            return AnyView(
                HStack(alignment: .top, spacing: 14) {
                    ForEach(Array([p2, p1].enumerated()), id: \.offset) { _, paragraph in
                        Text(paragraph)
                            .font(NativeTheme.font(13, relativeTo: .footnote))
                            .lineSpacing(4)
                            .foregroundStyle(textColor)
                            .frame(maxWidth: .infinity, alignment: .topLeading)
                    }
                }
                .padding(.vertical, 4)
            )
        }

        let maxRows = 7
        let indentPrefix = indent ? "　　" : ""
        let columns = buildVerticalColumns(paragraphs: [indentPrefix + p1, indentPrefix + p2], maxRows: maxRows)
        let punctuation: Set<Character> = ["，", "。", "；", "、", "：", "！", "？"]

        return AnyView(
            HStack(alignment: .top, spacing: 0) {
                ForEach(Array(columns.reversed().enumerated()), id: \.offset) { _, col in
                    VStack(spacing: 1) {
                        ForEach(Array(col.enumerated()), id: \.offset) { _, ch in
                            let isPunct = punctuation.contains(ch)
                            Text(String(ch))
                                .font(NativeTheme.font(14, relativeTo: .callout))
                                .foregroundStyle(textColor)
                                .frame(width: 18, height: 18)
                                .offset(x: isPunct ? 4 : 0, y: isPunct ? -4 : 0)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .top)
                }
            }
            .padding(.vertical, 2)
        )
    }

    /// Splits vertical Chinese paragraphs into fixed-height columns while keeping
    /// punctuation attached to the end of a column instead of starting the next one.
    private func buildVerticalColumns(paragraphs: [String], maxRows: Int) -> [[Character]] {
        let forbiddenStart: Set<Character> = ["，", "。", "；", "、", "：", "！", "？", "」", "》", "）"]
        var result: [[Character]] = []
        for paragraph in paragraphs {
            var current: [Character] = []
            for ch in paragraph {
                if current.count >= maxRows && !forbiddenStart.contains(ch) {
                    result.append(current)
                    current = [ch]
                } else {
                    current.append(ch)
                }
            }
            if !current.isEmpty {
                result.append(current)
            }
        }
        return result
    }

    // MARK: - Step 3: Library, Insights & Zero-Knowledge Sync

    private var libraryAndSyncStep: some View {
        VStack(alignment: .leading, spacing: 22) {
            stepHeader(
                title: L("书库有序，数据归己"),
                subtitle: L("从藏书整理、阅读足迹到跨端同步，一切由你掌控。")
            )

            VStack(spacing: 14) {
                showcaseCard(
                    symbol: "books.vertical",
                    title: L("个人书库与批注"),
                    detail: L("系列自动堆叠、收藏夹整理，统一归档划线、笔记与书签。")
                )

                showcaseCard(
                    symbol: "chart.bar.xaxis",
                    title: L("阅读统计与小组件"),
                    detail: L("记录每日阅读时长与里程碑勋章，轻点桌面小组件随时续读。")
                )

                showcaseCard(
                    symbol: "lock.icloud",
                    title: L("iCloud 与加密同步"),
                    detail: L("支持 iCloud 云盘与自备 S3 / WebDAV 同步，密钥仅保存在设备中。")
                )
            }
        }
    }

    // MARK: - Step 4: Trust Summary & Legal Consent

    private var consentStep: some View {
        VStack(alignment: .leading, spacing: 18) {
            stepHeader(
                title: L("开始使用前"),
                subtitle: L("Libzora 以本地隐私为默认原则，请阅读并确认相关协议。")
            )

            VStack(alignment: .leading, spacing: 14) {
                trustRow(
                    title: L("本地优先，离线可用"),
                    detail: L("书籍、阅读进度与批注默认保存在设备本地，无需登录账号即可阅读。")
                )
                trustRow(
                    title: L("密钥只留在你的设备"),
                    detail: L("端到端加密同步密钥受系统钥匙串保护，不上传至服务器，也不设云端找回。")
                )
                trustRow(
                    title: L("无广告，无第三方追踪"),
                    detail: L("没有开屏广告与行为画像，你的阅读记录只属于你自己。")
                )
            }
            .padding(18)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18, style: .continuous))

            VStack(spacing: 1) {
                legalDocumentRow(
                    title: L("用户协议"),
                    viewed: viewedTerms,
                    kind: .terms,
                    identifier: "welcome.terms"
                )
                Divider().background(NativeTheme.divider)
                legalDocumentRow(
                    title: L("隐私政策"),
                    viewed: viewedPrivacy,
                    kind: .privacy,
                    identifier: "welcome.privacy"
                )
            }
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))

            VStack(alignment: .leading, spacing: 8) {
                Button {
                    withAnimation(.easeInOut(duration: 0.15)) { agreed.toggle() }
                } label: {
                    HStack(alignment: .center, spacing: 10) {
                        Image(systemName: agreed ? "checkmark.circle.fill" : "circle")
                            .font(.system(size: 20))
                            .frame(width: 22, height: 22)
                            .foregroundStyle(agreed ? Color(uiColor: .systemBlue) : NativeTheme.muted)
                        Text(L("我已阅读并同意《用户协议》和《隐私政策》"))
                            .font(NativeTheme.font(13.5, relativeTo: .subheadline))
                            .foregroundStyle(NativeTheme.ink)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 14)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(agreed ? .isSelected : [])
                .accessibilityIdentifier("welcome.agree.toggle")

                // Keep hint space reserved so toggling `agreed` never shifts surrounding layout.
                Text(L("需勾选同意用户协议与隐私政策后方可继续使用"))
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(NativeTheme.muted)
                    .padding(.horizontal, 4)
                    .opacity(agreed ? 0 : 1)
                    .accessibilityHidden(agreed)
            }
        }
    }

    // MARK: - Bottom Footer (Consistent Position Across All Steps)

    private var bottomFooter: some View {
        HStack(spacing: 10) {
            if currentStep > 0 {
                backStepButton
            }
            if currentStep == totalSteps - 1 {
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
            } else {
                Button {
                    withAnimation(.easeInOut(duration: 0.24)) {
                        currentStep = min(totalSteps - 1, currentStep + 1)
                    }
                } label: {
                    Text(L("下一步"))
                        .font(NativeTheme.font(16, relativeTo: .headline).weight(.semibold))
                        .foregroundStyle(NativeTheme.page)
                        .frame(maxWidth: .infinity)
                        .frame(height: 50)
                        .background(NativeTheme.ink, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("welcome.next")
            }
        }
    }

    private var backStepButton: some View {
        Button {
            withAnimation(.easeInOut(duration: 0.24)) {
                currentStep = max(0, currentStep - 1)
            }
        } label: {
            Text(L("上一步"))
                .font(NativeTheme.font(15, relativeTo: .subheadline).weight(.medium))
                .foregroundStyle(NativeTheme.ink)
                .padding(.horizontal, 18)
                .frame(height: 50)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("welcome.back")
    }

    // MARK: - Reusable Components

    private func stepHeader(title: String, subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(NativeTheme.font(24, relativeTo: .title2).weight(.semibold))
                .foregroundStyle(NativeTheme.ink)
            Text(subtitle)
                .font(NativeTheme.font(14, relativeTo: .subheadline))
                .foregroundStyle(NativeTheme.secondary)
                .lineSpacing(3)
        }
    }

    private func preferenceSegmentRow<T: Hashable>(
        title: String,
        options: [(T, String)],
        selection: T,
        onSelect: @escaping (T) -> Void
    ) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(NativeTheme.font(13, relativeTo: .caption).weight(.medium))
                .foregroundStyle(NativeTheme.secondary)

            HStack(spacing: 6) {
                ForEach(Array(options.enumerated()), id: \.offset) { _, option in
                    let isSelected = option.0 == selection
                    Button {
                        onSelect(option.0)
                    } label: {
                        Text(option.1)
                            .font(NativeTheme.font(12.5, relativeTo: .caption).weight(isSelected ? .semibold : .regular))
                            .foregroundStyle(isSelected ? NativeTheme.page : NativeTheme.ink)
                            .lineLimit(1)
                            .minimumScaleFactor(0.72)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .padding(.horizontal, 6)
                            .background(
                                isSelected ? NativeTheme.ink : NativeTheme.page,
                                in: RoundedRectangle(cornerRadius: 10, style: .continuous)
                            )
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func fontPreviewTile(
        title: String,
        sample: String,
        useSystem: Bool,
        selected: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 4) {
                Text(sample)
                    .font(NativeTheme.font(15, relativeTo: .body, useSystem: useSystem))
                    .foregroundStyle(NativeTheme.ink)
                Text(title)
                    .font(NativeTheme.font(12, relativeTo: .caption, useSystem: useSystem))
                    .foregroundStyle(NativeTheme.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(NativeTheme.page, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .stroke(selected ? NativeTheme.ink : NativeTheme.ink.opacity(0.08), lineWidth: selected ? 1.5 : 0.75)
            )
        }
        .buttonStyle(.plain)
    }

    private func paperChip(label: String, tag: Int, swatch: Color) -> some View {
        let isSelected = tag == -1
            ? store.preferences.readerThemeFollowsAppearance
            : (!store.preferences.readerThemeFollowsAppearance && store.preferences.readerTheme == tag)
        return Button {
            updatePreference { value in
                if tag == -1 {
                    value.readerThemeFollowsAppearance = true
                } else {
                    value.readerThemeFollowsAppearance = false
                    value.readerTheme = tag
                }
            }
        } label: {
            VStack(spacing: 5) {
                Circle()
                    .fill(swatch)
                    .frame(width: 28, height: 28)
                    .overlay(
                        Circle().stroke(NativeTheme.ink.opacity(0.22), lineWidth: 0.75)
                    )
                    .overlay(
                        Circle()
                            .stroke(NativeTheme.ink, lineWidth: isSelected ? 2 : 0)
                            .padding(-3)
                    )
                Text(label)
                    .font(NativeTheme.font(11.5, relativeTo: .caption2).weight(isSelected ? .semibold : .regular))
                    .foregroundStyle(isSelected ? NativeTheme.ink : NativeTheme.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
            .background(
                isSelected ? NativeTheme.page : Color.clear,
                in: RoundedRectangle(cornerRadius: 10, style: .continuous)
            )
        }
        .buttonStyle(.plain)
    }

    private func showcaseCard(symbol: String, title: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: symbol)
                .font(.system(size: 20, weight: .regular))
                .foregroundStyle(NativeTheme.ink)
                .frame(width: 42, height: 42)
                .background(NativeTheme.page, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 6) {
                Text(title)
                    .font(NativeTheme.font(16, relativeTo: .headline).weight(.semibold))
                    .foregroundStyle(NativeTheme.ink)
                Text(detail)
                    .font(NativeTheme.font(13.5, relativeTo: .footnote))
                    .foregroundStyle(NativeTheme.secondary)
                    .lineSpacing(4)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(18)
        .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
    }

    private func trustRow(title: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "checkmark.shield.fill")
                .font(.system(size: 17))
                .foregroundStyle(NativeTheme.success)
                .padding(.top, 2)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(NativeTheme.font(14.5, relativeTo: .subheadline).weight(.semibold))
                    .foregroundStyle(NativeTheme.ink)
                Text(detail)
                    .font(NativeTheme.font(12.5, relativeTo: .footnote))
                    .foregroundStyle(NativeTheme.secondary)
                    .lineSpacing(2.5)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func legalDocumentRow(
        title: String,
        viewed: Bool,
        kind: NativeLegalDocumentPage.Kind,
        identifier: String
    ) -> some View {
        Button {
            loadedActiveLegal = false
            lastOpenedLegal = kind
            activeLegal = kind
        } label: {
            HStack(spacing: 12) {
                Image(systemName: "doc.text")
                    .font(.system(size: 16))
                    .foregroundStyle(Color(uiColor: .systemBlue))
                Text(title)
                    .font(NativeTheme.font(14.5, relativeTo: .subheadline).weight(.medium))
                    .foregroundStyle(NativeTheme.ink)
                Spacer()
                if viewed {
                    Text(L("已查看"))
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.success)
                }
                Image(systemName: "chevron.right")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(NativeTheme.muted)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier(identifier)
    }

    private func updatePreference(_ mutate: (inout MobilePreferences) -> Void) {
        var updated = store.preferences
        mutate(&updated)
        store.setPreferences(updated)
    }

    private func accept() {
        guard agreed else { return }
        updatePreference { $0.onboardingAccepted = true }
    }
}
