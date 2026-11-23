import SwiftUI
import UIKit

struct NativeKeyboardDone: ViewModifier {
    func body(content: Content) -> some View {
        content.toolbar {
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button(L("完成")) { UIApplication.shared.sendAction(#selector(UIResponder.resignFirstResponder), to: nil, from: nil, for: nil) }
            }
        }
    }
}

struct NativeProfilePage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.colorScheme) private var scheme
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var summary: ProfileSummary?
    @State private var membership: AccountMembership?

    private var account: AccountStatus? { store.account ?? summary?.account }
    private var cardOuterInset: CGFloat { sizeClass == .regular ? 20 : 16 }
    private var secondaryColor: Color { store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary }
    private var detailColor: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    private var cardDividerColor: Color {
        if store.preferences.colorfulMode {
            return scheme == .dark ? Color.white.opacity(0.10) : Color.black.opacity(0.08)
        }
        return scheme == .dark ? Color.white.opacity(0.08) : Color.black.opacity(0.07)
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 0) {
                HStack(spacing: 0) {
                    NativePageTitle(title: L("我的"), identifier: "page.profile")
                    Spacer(minLength: 0)
                }.modifier(NativePageMasthead())
                profileContent
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .contain).accessibilityIdentifier("profile.content")
        }
        .background { profileBackground }
        .navigationBarHidden(true)
        .overlay(alignment: .top) { NativeStatusBarGlass() }
        .task(id: store.uiLanguage) {
            do {
                let result: ProfileSummary = try await store.request("settings.profile")
                guard !Task.isCancelled else { return }
                summary = result; store.account = result.account
            }
            catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
            if !Task.isCancelled { await loadMembership() }
        }
    }

    @ViewBuilder private var profileBackground: some View {
        if store.preferences.colorfulMode {
            NativeAmbientBackground()
        } else {
            Color(uiColor: .systemGroupedBackground).ignoresSafeArea()
        }
    }

    private var profileContent: some View {
        VStack(alignment: .leading, spacing: 0) {
            accountSection

            groupLabel(L("阅读与外观"))
            profileCard {
                themeRow
                cardDivider()
                uiFontRow
                cardDivider()
                languageRow
                cardDivider()
                NavigationLink { NativeLibrarySettings(store: store) } label: {
                    profileRow(title: L("书库设置"))
                }.accessibilityIdentifier("profile.librarySettings")
                cardDivider()
                NavigationLink { NativeDictionary(store: store, initialTerm: "").nativeBackButton() } label: {
                    profileRow(title: L("本地词典"))
                }.accessibilityIdentifier("profile.dictionary")
            }

            groupLabel(L("服务与扩展"))
            profileCard {
                NavigationLink { NativeAiSettings(store: store) } label: {
                    profileRow(title: L("AI 助手"), value: summary?.ai ?? "")
                }.accessibilityIdentifier("profile.ai")
                cardDivider()
                NavigationLink { NativeEmailSettings(store: store) } label: {
                    profileRow(title: L("发送到 Kindle"), value: summary?.kindle ?? "")
                }.accessibilityIdentifier("profile.kindle")
            }

            groupLabel(L("数据与存储"))
            profileCard {
                profileRow(title: L("书籍占用"), value: summary?.storage ?? "—", chevron: false)
                cardDivider()
                NavigationLink { NativeBackupPage(store: store) } label: {
                    profileRow(title: L("备份与恢复"))
                }.accessibilityIdentifier("profile.backup")
                cardDivider()
                NavigationLink { NativeTrash(store: store) } label: {
                    profileRow(title: L("回收站"))
                }.accessibilityIdentifier("profile.trash")
            }

            groupLabel(L("支持与关于"))
            profileCard {
                NavigationLink { NativeFeedbackPage(store: store) } label: {
                    profileRow(title: L("问题反馈"))
                }.accessibilityIdentifier("profile.feedback")
                cardDivider()
                NavigationLink { NativeAboutPage(store: store, version: summary?.version ?? "") } label: {
                    profileRow(title: L("关于 Libzora"), value: summary?.version ?? "")
                }.accessibilityIdentifier("profile.about")
            }
        }
        .padding(.horizontal, cardOuterInset)
        .padding(.top, 8)
        .padding(.bottom, 120)
        .buttonStyle(NativeProfileCardRowButtonStyle())
    }

    /// Account identity and cloud sync share the lead card, mirroring iOS Settings'
    /// Apple Account + iCloud grouping.
    @ViewBuilder private var accountSection: some View {
        let signedIn = account?.isLoggedIn == true
        let name = signedIn ? (account?.user?.username ?? L("已登录")) : L("登录 / 注册")
        profileCard {
            NavigationLink { NativeAccountPage(store: store) } label: {
                HStack(spacing: 14) {
                    avatar(signedIn: signedIn, name: name)
                    VStack(alignment: .leading, spacing: 5) {
                        HStack(spacing: 8) {
                            Text(name).font(NativeTheme.font(18, relativeTo: .headline).weight(.semibold))
                                .foregroundStyle(NativeTheme.ink)
                                .lineLimit(1).minimumScaleFactor(0.8)
                            if signedIn, let membership, membership.plan != "free" {
                                NativePlanBadge(text: membership.planName,
                                                accent: NativeTheme.planAccent(membership.plan))
                            }
                        }
                        if let subtitle = accountSubtitle {
                            Text(subtitle)
                                .font(NativeTheme.font(12, relativeTo: .caption))
                                .foregroundStyle(detailColor).lineLimit(2)
                        }
                    }
                    Spacer(minLength: 8)
                    Image(systemName: "chevron.right").font(.system(size: 12, weight: .medium))
                        .foregroundStyle(detailColor.opacity(0.85))
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 16)
                .contentShape(Rectangle())
            }.accessibilityIdentifier("profile.account")
            cardDivider()
            NavigationLink { NativeSyncSettings(store: store) } label: {
                profileRow(title: L("云同步"), value: summary?.sync ?? "")
            }.accessibilityIdentifier("profile.sync")
        }
    }

    private var themeRow: some View {
        let follow = store.uiLanguage == "en-US" ? L("系统") : L("跟随系统")
        let options: [(id: Int, title: String)] = [(0, follow), (1, L("浅色")), (2, L("深色"))]
        let current = options.first { $0.id == store.preferences.themeMode }?.title ?? follow
        return NavigationLink {
            NativePreferenceOptionPage(
                store: store,
                title: { L("外观") },
                identifierPrefix: "profile.theme",
                current: { $0.themeMode },
                options: { currentStore in
                    let systemLabel = currentStore.uiLanguage == "en-US" ? L("系统") : L("跟随系统")
                    return [(0, systemLabel), (1, L("浅色")), (2, L("深色"))]
                },
                apply: { $0.themeMode = $1 }
            )
        } label: {
            profileRow(title: L("外观"), value: current)
        }
        .accessibilityLabel(L("外观"))
        .accessibilityValue(current)
        .accessibilityIdentifier("profile.theme")
    }

    private var uiFontRow: some View {
        let current = store.preferences.uiFont == 1 ? L("系统默认") : L("京华老宋体")
        return NavigationLink {
            NativePreferenceOptionPage(
                store: store,
                title: { L("界面字体") },
                identifierPrefix: "profile.uiFont",
                current: { $0.uiFont },
                options: { _ in [(0, L("京华老宋体（默认）")), (1, L("系统默认"))] },
                optionFont: { id in NativeTheme.font(useSystem: id == 1) },
                footer: { L("书库、导航栏与设置的显示字体；阅读正文可在阅读器排版设置中单独调整。") },
                apply: { $0.uiFont = $1 }
            )
        } label: {
            profileRow(title: L("界面字体"), value: current)
        }
        .accessibilityLabel(L("界面字体"))
        .accessibilityValue(current)
        .accessibilityIdentifier("profile.uiFont")
    }

    private var languageRow: some View {
        let options: [(id: String, title: String)] = [("auto", L("跟随系统")), ("zh-CN", "简体中文"), ("en-US", "English")]
        let current = options.first { $0.id == store.preferences.uiLanguage }?.title ?? L("跟随系统")
        return NavigationLink {
            NativePreferenceOptionPage(
                store: store,
                title: { L("语言") },
                identifierPrefix: "profile.language",
                current: { $0.uiLanguage },
                options: { _ in [("auto", L("跟随系统")), ("zh-CN", "简体中文"), ("en-US", "English")] },
                apply: { $0.uiLanguage = $1 }
            )
        } label: {
            profileRow(title: L("语言"), value: current)
        }
        .accessibilityLabel(L("语言"))
        .accessibilityValue(current)
        .accessibilityIdentifier("profile.language")
    }

    private func groupLabel(_ title: String) -> some View {
        Text(title)
            .font(NativeTheme.font(13, relativeTo: .caption).weight(.medium))
            .foregroundStyle(secondaryColor)
            .padding(.leading, 16)
            .padding(.top, 22)
            .padding(.bottom, 7)
    }

    private func profileCard<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 24, style: .continuous)
        return VStack(alignment: .leading, spacing: 0) { content() }
            .frame(maxWidth: .infinity)
            .background {
                if store.preferences.colorfulMode {
                    shape.fill(.regularMaterial)
                } else {
                    shape.fill(Color(uiColor: .secondarySystemGroupedBackground))
                }
            }
            .clipShape(shape)
            .overlay {
                shape.stroke(
                    store.preferences.colorfulMode
                        ? Color.white.opacity(scheme == .dark ? 0.12 : 0.45)
                        : (scheme == .dark ? Color.white.opacity(0.06) : Color.black.opacity(0.03)),
                    lineWidth: 0.5
                )
                .allowsHitTesting(false)
            }
            .shadow(color: Color.black.opacity(scheme == .dark ? 0.14 : 0.025), radius: 6, y: 1.5)
    }

    private func cardDivider(leading: CGFloat = 16) -> some View {
        Rectangle()
            .fill(cardDividerColor)
            .frame(height: 0.5)
            .padding(.leading, leading)
            .accessibilityHidden(true)
    }

    private func profileRow(title: String, value: String = "", chevron: Bool = true) -> some View {
        NativeSettingsRow(title: title, value: value, chevron: chevron, detailColor: detailColor)
            .padding(.horizontal, 16)
    }

    private func avatar(signedIn: Bool, name: String) -> some View {
        ZStack {
            Circle()
                .fill(NativeTheme.strong)
            Circle()
                .stroke(Color.primary.opacity(scheme == .dark ? 0.12 : 0.06), lineWidth: 0.5)
            if signedIn {
                Text(String(name.prefix(1)))
                    .font(NativeTheme.font(21, relativeTo: .headline).weight(.semibold))
                    .foregroundStyle(NativeTheme.secondary)
            } else {
                Image(systemName: "person")
                    .font(.system(size: 20, weight: .regular))
                    .foregroundStyle(NativeTheme.secondary)
            }
        }.frame(width: 52, height: 52).accessibilityHidden(true)
    }

    private var accountSubtitle: String? {
        guard account?.isLoggedIn == true else { return nil }
        let region = account?.regionName ?? L("中国区")
        guard let membership else { return region }
        switch membership.plan {
        case "trial": return region + L(" · 试用订阅 · ") + membership.planExpiryText
        case "lifetime": return region + L(" · 买断订阅 · 永久有效")
        default: return membership.isTrialExpired ? region + L(" · 试用订阅已到期") : region + L(" · 免费账户")
        }
    }

    private func loadMembership() async {
        guard store.account?.isLoggedIn == true else { membership = nil; return }
        let value: DeviceFeatureStatus? = try? await store.request("settings.membership.get")
        if !Task.isCancelled { membership = value?.membership }
    }
}

private struct NativeProfileCardRowButtonStyle: ButtonStyle {
    @Environment(\.colorScheme) private var scheme

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(
                Color.primary.opacity(configuration.isPressed ? (scheme == .dark ? 0.10 : 0.055) : 0)
            )
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

private struct NativePreferenceOptionPage<Selection: Hashable>: View {
    @ObservedObject var store: NativeStore
    let title: () -> String
    let identifierPrefix: String
    let current: (MobilePreferences) -> Selection
    let options: (NativeStore) -> [(id: Selection, title: String)]
    var optionFont: ((Selection) -> Font)? = nil
    var footer: (() -> String)? = nil
    let apply: (inout MobilePreferences, Selection) -> Void

    var body: some View {
        let selectedValue = current(store.preferences)
        NativeForm {
            Section {
                ForEach(options(store), id: \.id) { option in
                    let isSelected = option.id == selectedValue
                    Button {
                        guard !isSelected else { return }
                        UISelectionFeedbackGenerator().selectionChanged()
                        var value = store.preferences
                        apply(&value, option.id)
                        store.setPreferences(value)
                    } label: {
                        HStack(spacing: 12) {
                            Text(option.title)
                                .font(optionFont?(option.id) ?? NativeTheme.font())
                                .foregroundStyle(NativeTheme.ink)
                            Spacer(minLength: 12)
                            if isSelected {
                                Image(systemName: "checkmark")
                                    .font(.system(size: 14, weight: .semibold))
                                    .foregroundStyle(Color(uiColor: .systemBlue))
                            }
                        }
                        .frame(minHeight: 26)
                        .contentShape(Rectangle())
                    }
                    .accessibilityLabel(option.title)
                    .accessibilityAddTraits(isSelected ? .isSelected : [])
                    .accessibilityIdentifier("\(identifierPrefix).option.\(option.id)")
                }
            } footer: {
                if let footerText = footer?(), !footerText.isEmpty {
                    Text(footerText)
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.muted)
                }
            }
        }
        .navigationTitle(title())
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarHidden(false)
        .nativeBackButton()
    }
}

/// Rare, consequential actions live one level down so the profile page stays a
/// directory of destinations instead of a list of buttons that act immediately.
struct NativeBackupPage: View {
    @ObservedObject var store: NativeStore
    @State private var busy = false

    var body: some View {
        NativeForm {
            Section {
                Button { run(false) } label: {
                    HStack {
                        Text(L("导出备份")).foregroundStyle(busy ? NativeTheme.muted : Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }.disabled(busy).accessibilityIdentifier("profile.backup.export")
                Button { run(true) } label: {
                    HStack {
                        Text(L("恢复备份")).foregroundStyle(busy ? NativeTheme.muted : Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }.disabled(busy).accessibilityIdentifier("profile.backup.restore")
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    if busy {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text(L("正在处理…"))
                        }
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
                    }
                    Text(L("备份包含书库、阅读进度、书签与批注。导出与恢复都需要同步密钥；密钥不会写入备份。"))
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
                }
            }
        }
        .navigationTitle(L("备份与恢复")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
    }

    private func run(_ restore: Bool) {
        busy = true
        store.perform { defer { busy = false }; try await store.command(restore ? "backup.restore" : "backup.export") }
    }
}

struct NativeAboutPage: View {
    @ObservedObject var store: NativeStore
    let version: String
    @State private var developer = false
    @State private var versionTaps = 0
    @State private var firstVersionTap = Date.distantPast

    var body: some View {
        NativeForm {
            Section {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Libzora").font(NativeTheme.font(22, relativeTo: .title2).weight(.semibold))
                    Text(L("本地优先的电子书阅读器")).font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.secondary)
                }
            }
            Section {
                Button { tapVersion() } label: {
                    NativeSettingsRow(title: L("版本"), value: version, chevron: false, verticalPadding: 2)
                }.buttonStyle(.plain).accessibilityIdentifier("profile.about.version")
                NativeSettingsRow(title: L("开发者"), value: "kingstacker", chevron: false, verticalPadding: 2)
            }
            Section {
                Link(destination: legalURL("terms")) {
                    NativeSettingsRow(title: L("用户协议"), chevron: true, verticalPadding: 2)
                }.accessibilityIdentifier("profile.about.terms")
                Link(destination: legalURL("privacy")) {
                    NativeSettingsRow(title: L("隐私政策"), chevron: true, verticalPadding: 2)
                }.accessibilityIdentifier("profile.about.privacy")
            }
        }
        .navigationTitle(L("关于")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .sheet(isPresented: $developer) {
            NativeNameSheet(store: store, title: L("开发者模式"), initial: "", button: L("进入"), isSecure: true) { password in
                try await store.command("settings.developer.toggle", ["password": password])
            }
        }
    }

    /// Follows the app language (not the system one) so the page matches the UI.
    private func legalURL(_ page: String) -> URL {
        let language = NativeLocalization.language == "en-US" ? "en" : "zh"
        return URL(string: "https://libzora.stacker.beauty/\(page).html?lang=\(language)")!
    }

    private func tapVersion() {
        let now = Date()
        if versionTaps == 0 || now.timeIntervalSince(firstVersionTap) > 3 { versionTaps = 0; firstVersionTap = now }
        versionTaps += 1
        guard versionTaps >= 5 else { return }; versionTaps = 0
        if store.developerMode { store.perform { try await store.command("settings.developer.toggle") } }
        else { developer = true }
    }
}

struct NativeLibrarySettings: View {
    @ObservedObject var store: NativeStore

    var body: some View {
        NativeForm {
            Section {
                settingToggle(L("画廊视图"), identifier: "library.settings.gallery", isOn: Binding(get: { store.preferences.galleryMode }, set: { enabled in
                    var value = store.preferences; value.galleryMode = enabled
                    store.setPreferences(value)
                }))
                settingToggle(L("炫彩模式"), identifier: "library.settings.colorful", isOn: Binding(get: { store.preferences.colorfulMode }, set: { enabled in
                    var value = store.preferences; value.colorfulMode = enabled
                    store.setPreferences(value)
                }))
            }
            Section {
                settingToggle(L("按系列叠放"), identifier: "library.settings.stackBookSeries", isOn: Binding(get: { store.preferences.stackBookSeries }, set: { enabled in
                    var value = store.preferences; value.stackBookSeries = enabled
                    store.setPreferences(value)
                }))
            } footer: {
                Text(L("同一系列的多本书在书架中叠放显示；关闭后每本书单独显示。"))
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
            }
            Section {
                settingToggle(L("显示书籍评分"), identifier: "library.settings.rating", isOn: Binding(get: { store.preferences.showBookRating }, set: { enabled in
                    var value = store.preferences; value.showBookRating = enabled
                    store.setPreferences(value)
                }))
                settingToggle(L("显示书籍同步状态"), identifier: "library.settings.syncStatus", isOn: Binding(get: { store.preferences.showBookSyncStatus }, set: { enabled in
                    var value = store.preferences; value.showBookSyncStatus = enabled
                    store.setPreferences(value)
                }))
            }
        }
        .navigationTitle(L("书库设置")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
    }

    private func settingToggle(_ title: String, identifier: String, isOn: Binding<Bool>) -> some View {
        HStack(spacing: 12) {
            Text(title).font(NativeTheme.font()).accessibilityHidden(true)
            Spacer(minLength: 12)
            NativeSwitch(title: title, isOn: isOn, identifier: identifier).fixedSize()
        }.frame(minHeight: 28).accessibilityElement(children: .contain)
    }
}

struct NativeSyncSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = SyncForm()
    @State private var providers = [NativeSyncProviderOption(provider: 0, displayName: "S3"),
                                    NativeSyncProviderOption(provider: 1, displayName: "WebDAV")]
    @State private var formLoaded = false
    @State private var summary = ""
    @State private var status = ""
    @State private var busy = false
    @State private var loading = true
    @State private var advanced = false
    @State private var actionTask: Task<Void, Never>?
    @State private var confirmEncryptionKey = ""
    @State private var savedEncryptionKey = ""
    @State private var showEncryptionKeyConfirmation = false
    @State private var encryptionKeyOperation = "save"

    var body: some View {
        NativeForm {
            Section {
                Picker(L("同步方式"), selection: $form.provider) {
                    ForEach(providers) { provider in Text(provider.displayName).tag(provider.provider) }
                }.pickerStyle(.segmented).accessibilityIdentifier("sync.provider")
            }
            .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 2, trailing: 0))
            .listRowBackground(Color.clear)

            Section {
                Toggle(L("启用同步"), isOn: $form.enabled).tint(NativeTheme.systemSwitchTint)
                Toggle(L("自动同步"), isOn: $form.automaticSyncEnabled).tint(NativeTheme.systemSwitchTint)
            } footer: {
                footerCaption(L("可使用 iCloud 云盘或自备 S3 / WebDAV 同步书籍、阅读进度和批注。"))
            }
            customStorageSections
            Section {
                Button { run("test") } label: {
                    HStack {
                        Text(L("测试连接")).foregroundStyle(busy || loading ? NativeTheme.muted : Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }
                Button { run("save") } label: {
                    HStack {
                        Spacer()
                        Text(L("保存")).font(NativeTheme.font().weight(.semibold))
                            .foregroundStyle(busy || loading ? NativeTheme.muted : Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }.accessibilityIdentifier("sync.save")
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    if busy { ProgressView().controlSize(.small) }
                    if !status.isEmpty {
                        Text(status)
                            .font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
                            .textSelection(.enabled)
                            .accessibilityIdentifier("sync.status")
                    }
                }
            }
        }.disabled(busy || loading)
            .navigationTitle(L("同步")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .modifier(NativeKeyboardDone())
            .task { await load() }
            .onChange(of: store.syncStatus) { status = $0 }
            .onDisappear { actionTask?.cancel() }
            .alert(Text(form.encryptionKey.isEmpty ? L("清除同步密钥") : L("确认同步密钥")),
                   isPresented: $showEncryptionKeyConfirmation) {
                Button(L("取消"), role: .cancel) { }
                Button(form.encryptionKey.isEmpty ? L("清除密钥") : L("使用此密钥")) {
                    perform(encryptionKeyOperation, encryptionKeyConfirmed: true)
                }
            } message: {
                Text(form.encryptionKey.isEmpty
                     ? L("清除本机保存的同步密钥后，将无法再解密已有加密备份或云端同步数据。确定清除吗？")
                     : L("同步密钥用于加密备份和自备存储中的同步数据。请确认两次输入一致，并已妥善保存。密钥遗忘后无法找回，已有加密数据也无法恢复。"))
            }
    }

    @ViewBuilder private var customStorageSections: some View {
        Section {
            if form.provider == 1 {
                field(L("服务地址"), text: $form.webDavEndpoint)
                field(L("用户名"), text: $form.webDavUsername)
                secret(L("密码"), text: $form.webDavPassword)
            } else if form.provider == 0 {
                field(L("Endpoint（AWS 可留空）"), text: $form.endpoint)
                field("Bucket", text: $form.bucket)
                field("Region", text: $form.region)
                field("Access Key", text: $form.accessKey)
                secret("Secret Key", text: $form.secretKey)
            } else if form.provider == 3 {
                Label(L("iCloud 云盘"), systemImage: "icloud")
                    .font(NativeTheme.font()).accessibilityIdentifier("sync.icloud.info")
            }
            field(L("目录前缀"), text: $form.prefix)
        } header: {
            Text(L("连接信息"))
        } footer: {
            if form.provider == 3 {
                VStack(alignment: .leading, spacing: 4) {
                    footerCaption(L("在系统设置中开启 iCloud 云盘。iPhone、iPad 和 Mac 使用同一 Apple 账户、相同区域和目录前缀即可同步，无需填写服务器或密码。"))
                    footerCaption(L("数据占用你的 iCloud 存储空间，其他设备可能需要稍等片刻才能收到更新。"))
                }
            }
        }
        Section {
            DisclosureGroup(L("高级选项"), isExpanded: $advanced) {
                if form.provider == 0 { Toggle("Path Style", isOn: $form.pathStyle).tint(NativeTheme.systemSwitchTint) }
                Toggle(L("同步时下载书籍文件"), isOn: $form.downloadBookFilesOnSync).tint(NativeTheme.systemSwitchTint)
                if form.provider != 3 { Toggle(L("跳过 TLS 验证"), isOn: $form.skipTlsVerify).tint(NativeTheme.systemSwitchTint) }
            }
        }
        Section {
            syncKey
        } footer: {
            footerCaption(L("设置或更改密钥时需要输入两次，并在弹窗中确认。密钥由你自行牢记，不会上传或写入备份。遗忘后无法解密已有数据，设置新密钥也无法恢复旧数据。"))
        }
        if form.provider != 3 {
            Section {
                Button { run("import") } label: {
                    HStack { Text(L("导入连接参数")).foregroundStyle(Color(uiColor: .systemBlue)); Spacer() }
                        .frame(minHeight: 26).contentShape(Rectangle())
                }
                Button { run("export") } label: {
                    HStack { Text(L("导出连接参数")).foregroundStyle(Color(uiColor: .systemBlue)); Spacer() }
                        .frame(minHeight: 26).contentShape(Rectangle())
                }
            }
        }
        Section {
            Button { run("run") } label: {
                HStack {
                    Spacer()
                    Text(L("立即同步")).font(NativeTheme.font().weight(.semibold))
                        .foregroundStyle(Color(uiColor: .systemBlue))
                    Spacer()
                }.frame(minHeight: 26).contentShape(Rectangle())
            }.accessibilityIdentifier("sync.run")
        } footer: {
            if !summary.isEmpty { footerCaption(summary) }
        }
    }

    @ViewBuilder private var syncKey: some View {
        // This user-owned key must not be offered to password AutoFill.
        NativeInlineTextField(title: L("同步密钥（云同步与备份必填）"), text: $form.encryptionKey, secure: true,
                              identifier: "sync.encryptionKey")
            .textContentType(.oneTimeCode).textInputAutocapitalization(.never).disableAutocorrection(true)
        NativeInlineTextField(title: L("再次输入同步密钥"), text: $confirmEncryptionKey, secure: true,
                              identifier: "sync.encryptionKeyConfirm")
            .textContentType(.oneTimeCode).textInputAutocapitalization(.never).disableAutocorrection(true)
    }

    private func footerCaption(_ text: String) -> some View {
        Text(text)
            .font(NativeTheme.font(12, relativeTo: .caption))
            .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
    }
    private func field(_ title: String, text: Binding<String>) -> some View {
        NativeInlineTextField(title: title, text: text).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func secret(_ title: String, text: Binding<String>) -> some View {
        NativeInlineTextField(title: title, text: text, secure: true).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func loadSnapshot() async throws -> SyncForm {
        let value: SyncFormSnapshot = try await store.request("settings.sync.get")
        summary = value.summary
        if let available = value.providers { providers = available }
        return value.settings
    }
    private func load() async {
        defer { loading = false }
        do {
            let saved = try await loadSnapshot()
            // This reruns on return from the account page; keep unsaved edits.
            if !formLoaded { form = saved; savedEncryptionKey = saved.encryptionKey; formLoaded = true }
            let account: AccountStatus = try await store.request("settings.account.get"); store.account = account
        }
        catch { if !Task.isCancelled { status = error.localizedDescription } }
    }
    private func run(_ operation: String) {
        let keyEmpty = form.encryptionKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        // Clearing the key cannot succeed while sync is enabled, testing, or running;
        // let validation report the missing-key requirement directly.
        if keyEmpty && (operation == "run" || operation == "test" || (operation == "save" && form.enabled)) {
            perform(operation)
            return
        }
        switch NativeSyncKeyConfirmation.decision(operation: operation, key: form.encryptionKey,
                                                  savedKey: savedEncryptionKey, confirmation: confirmEncryptionKey) {
        case .mismatch:
            status = L("两次输入的同步密钥不一致，请重新输入。")
        case .confirm:
            encryptionKeyOperation = operation
            showEncryptionKeyConfirmation = true
        case .proceed:
            perform(operation)
        }
    }
    private func perform(_ operation: String, encryptionKeyConfirmed: Bool = false) {
        busy = true; status = operation == "run" ? L("正在同步…") : operation == "test" ? L("正在测试连接…") : ""
        actionTask = Task {
            defer { busy = false }
            do {
                let args: [String: Any] = ["settings": try requestObject(form),
                                           "encryptionKeyConfirmed": encryptionKeyConfirmed]
                if operation == "import" {
                    let imported: SyncForm = try await store.request("settings.sync.import", args)
                    form = imported; status = L("参数已导入，请检查后点击保存。同步加密密钥保持本机原值。")
                } else if operation == "test" || operation == "run" {
                    let result: MessageResult = try await store.request("settings.sync.\(operation)", args)
                    status = result.message + (result.warning.map { " " + $0 } ?? "")
                    if operation == "run" {
                        form.enabled = true
                        savedEncryptionKey = form.encryptionKey
                        confirmEncryptionKey = ""
                        if let saved = try? await loadSnapshot() {
                            savedEncryptionKey = saved.encryptionKey
                            form.encryptionKey = saved.encryptionKey
                        }
                        try await store.refreshLibrary()
                    }
                } else {
                    try await store.command("settings.sync.\(operation)", args)
                    if operation == "save" {
                        savedEncryptionKey = form.encryptionKey
                        confirmEncryptionKey = ""
                        status = L("已保存")
                        if let saved = try? await loadSnapshot() { form = saved }
                        if store.preferences.showBookSyncStatus { try await store.refreshLibrary() }
                    }
                }
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }
}

struct NativeAiSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = AiForm()
    @State private var busy = false
    @State private var loading = true
    @State private var status = ""
    @State private var fetchedModels: [String] = []
    @State private var showModelPicker = false
    @State private var providerTask: Task<Void, Never>?
    @State private var actionTask: Task<Void, Never>?

    var body: some View {
        NativeForm {
            Section {
                Picker(L("服务"), selection: Binding(get: { form.provider }, set: selectProvider)) { Text("DeepSeek").tag("deepseek"); Text("OpenAI").tag("openai"); Text(L("自定义")).tag("custom") }
                    .pickerStyle(.segmented)
            }
            .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 2, trailing: 0))
            .listRowBackground(Color.clear)

            Section {
                NativeInlineTextField(title: L("接口地址"), text: $form.baseUrl, identifier: "ai.baseUrl").textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeInlineTextField(title: L("模型"), text: $form.model, identifier: "ai.model").textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeInlineTextField(title: "API Key", text: $form.apiKey, secure: true, identifier: "ai.apiKey").textInputAutocapitalization(.never).disableAutocorrection(true)
            }
            Section {
                Button { fetchModels() } label: {
                    HStack {
                        Text(L("获取模型")).font(NativeTheme.font()).foregroundStyle(Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }.accessibilityIdentifier("ai.fetchModels")
                if !fetchedModels.isEmpty {
                    Button { showModelPicker = true } label: {
                        NativeSettingsRow(title: L("选择模型"),
                                          value: form.model.isEmpty ? L("{0} 个可用", fetchedModels.count) : form.model,
                                          verticalPadding: 2)
                    }.buttonStyle(.plain).accessibilityIdentifier("ai.selectModel")
                }
            }
            Section {
                Button { run(test: true) } label: {
                    HStack {
                        Spacer()
                        Text(L("测试连接")).foregroundStyle(Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }.accessibilityIdentifier("ai.test")
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    if busy { ProgressView().controlSize(.small) }
                    if !status.isEmpty {
                        Text(status)
                            .font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
                            .textSelection(.enabled)
                            .accessibilityIdentifier("ai.status")
                    }
                }
            }
        }.disabled(loading || busy)
            .navigationTitle(L("AI 接口")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone())
            .nativeBackButton()
            .toolbar { Button(L("保存")) { run(test: false) }.disabled(busy || loading) }
            .task {
                loading = true
                defer { loading = false }
                do { form = try await store.request("settings.ai.get") }
                catch { if !Task.isCancelled { status = error.localizedDescription } }
            }
            .onChange(of: form.baseUrl) { _ in
                if !loading { fetchedModels = [] }
            }
            .onDisappear { providerTask?.cancel(); actionTask?.cancel() }
            .sheet(isPresented: $showModelPicker) {
                NativeAiModelPickerSheet(models: fetchedModels, selectedModel: $form.model)
            }
    }
    private func selectProvider(_ provider: String) {
        guard !loading, !busy, provider != form.provider else { return }
        form.provider = provider
        fetchedModels = []
        providerTask?.cancel()
        let previousUrl = form.baseUrl
        let previousModel = form.model
        providerTask = Task {
            do {
                let defaults: AiDefaults = try await store.request("settings.ai.defaults", ["provider": provider])
                guard !Task.isCancelled, form.provider == provider else { return }
                if form.baseUrl == previousUrl { form.baseUrl = defaults.baseUrl }
                if form.model == previousModel { form.model = defaults.model }
            } catch { }
        }
    }
    private func fetchModels() {
        busy = true; status = L("正在获取模型列表…")
        actionTask?.cancel()
        actionTask = Task {
            defer { busy = false }
            await providerTask?.value
            do {
                let result: AiModelListResult = try await store.request("settings.ai.models", ["settings": try requestObject(form)])
                fetchedModels = result.models
                if result.models.isEmpty {
                    status = L("未获取到可用模型，请手动填写模型名称。")
                } else {
                    if form.model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        form.model = result.models[0]
                    }
                    status = L("已获取 {0} 个模型，请选择。", result.models.count)
                    showModelPicker = true
                }
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }
    private func run(test: Bool) {
        busy = true; status = test ? L("正在测试连接…") : ""
        actionTask?.cancel()
        actionTask = Task {
            defer { busy = false }
            await providerTask?.value
            do {
                let args = ["settings": try requestObject(form)]
                if test { let value: MessageResult = try await store.request("settings.ai.test", args); status = value.message }
                else { try await store.command("settings.ai.save", args); status = L("已保存") }
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }
}

private struct NativeAiModelPickerSheet: View {
    let models: [String]
    @Binding var selectedModel: String
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""

    private var filtered: [String] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return models }
        return models.filter { $0.localizedCaseInsensitiveContains(trimmed) }
    }

    var body: some View {
        NavigationView {
            List {
                if models.count > 8 {
                    TextField(L("搜索模型"), text: $query)
                        .font(NativeTheme.font(15))
                        .textInputAutocapitalization(.never)
                        .disableAutocorrection(true)
                        .accessibilityIdentifier("ai.models.search")
                }
                if filtered.isEmpty {
                    Text(L("没有匹配的模型")).foregroundStyle(NativeTheme.secondary).font(NativeTheme.font())
                } else {
                    ForEach(filtered, id: \.self) { model in
                        Button {
                            selectedModel = model
                            dismiss()
                        } label: {
                            HStack {
                                Text(model).font(NativeTheme.font()).foregroundStyle(NativeTheme.ink)
                                Spacer()
                                if model.caseInsensitiveCompare(selectedModel.trimmingCharacters(in: .whitespacesAndNewlines)) == .orderedSame {
                                    Image(systemName: "checkmark").foregroundStyle(NativeTheme.ink)
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle(L("选择模型"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("取消")) { dismiss() }.font(NativeTheme.font())
                }
            }
        }.navigationViewStyle(.stack)
    }
}

struct NativeEmailSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = EmailForm()
    @State private var busy = false
    @State private var loading = true
    @State private var status = ""
    var body: some View {
        NativeForm {
            Section(L("收件人")) {
                NativeInlineTextField(title: L("Kindle 邮箱"), text: $form.kindleEmailAddress).keyboardType(.emailAddress)
            }
            Section {
                NativeInlineTextField(title: L("发件人邮箱"), text: $form.senderEmailAddress).keyboardType(.emailAddress)
                NativeInlineTextField(title: L("SMTP 服务器"), text: $form.smtpHost)
                HStack {
                    Text(L("端口")).font(NativeTheme.font())
                    TextField("587", value: $form.smtpPort, formatter: NumberFormatter())
                        .font(NativeTheme.font())
                        .keyboardType(.numberPad)
                        .multilineTextAlignment(.trailing)
                }
                .frame(minHeight: 26)
                NativeInlineTextField(title: L("SMTP 用户名"), text: $form.smtpUsername)
                NativeInlineTextField(title: L("密码或应用专用密码"), text: $form.smtpPassword, secure: true)
                Toggle(L("使用 SSL / TLS"), isOn: $form.enableSsl).tint(NativeTheme.systemSwitchTint)
            } header: {
                Text(L("发件邮箱"))
            } footer: {
                if !status.isEmpty {
                    Text(status)
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
                }
            }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .navigationTitle(L("发送到 Kindle")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone())
            .nativeBackButton()
            .toolbar { Button(L("保存")) {
                busy = true
                store.perform { defer { busy = false }; try await store.command("settings.kindle.save", ["settings": try requestObject(form)]); status = L("已保存") }
            }.disabled(loading || busy) }
            .task {
                defer { loading = false }
                do { form = try await store.request("settings.kindle.get") }
                catch { if !Task.isCancelled { status = error.localizedDescription } }
            }
    }
}

struct NativeTrash: View {
    @ObservedObject var store: NativeStore
    @State private var items: [TrashRecord] = []
    @State private var busy = false
    var body: some View {
        List {
            Group {
                if items.isEmpty {
                    Text(L("回收站是空的"))
                        .foregroundStyle(NativeTheme.secondary)
                        .frame(maxWidth: .infinity, alignment: .center)
                        .padding(.vertical, 6)
                }
                ForEach(items) { item in
                    VStack(alignment: .leading, spacing: 10) {
                        Text(item.title).font(NativeTheme.font(17, relativeTo: .headline))
                        Text(item.detail).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        HStack {
                            Button(L("恢复")) { action("restore", id: item.id) }.buttonStyle(.bordered)
                            Button(L("永久删除"), role: .destructive) { action("purge", id: item.id) }.buttonStyle(.bordered)
                        }
                    }.padding(.vertical, 6)
                }
            }
            .listRowInsets(EdgeInsets(top: 10, leading: 16, bottom: 10, trailing: 16))
            .listRowBackground(NativeGroupedRowBackground(colorfulMode: store.preferences.colorfulMode))
        }.listStyle(.insetGrouped).textCase(nil).font(NativeTheme.font())
            .modifier(NativeGroupedListBackground()).disabled(busy)
            .navigationTitle(L("回收站")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .task { do { items = try await store.request("trash.get") } catch { if !Task.isCancelled { store.failure = error.localizedDescription } } }
    }
    private func action(_ operation: String, id: String) {
        busy = true
        store.perform {
            defer { busy = false }
            try await store.command("trash.\(operation)", ["id": id])
            items = try await store.request("trash.get"); try await store.refreshLibrary()
        }
    }
}
