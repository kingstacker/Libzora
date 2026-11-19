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
    @State private var summary: ProfileSummary?
    @State private var membership: AccountMembership?
    @State private var preferenceMenu: NativePreferenceMenuRequest?

    private var account: AccountStatus? { store.account ?? summary?.account }
    private var secondaryColor: Color { store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary }
    private var detailColor: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text(L("我的")).font(NativeTheme.pageTitleFont).accessibilityIdentifier("page.profile")
                    .padding(.bottom, 18)

                accountSection

                groupLabel(L("阅读与外观"))
                profileGroup {
                    themeRow
                    languageRow
                    NavigationLink { NativeLibrarySettings(store: store) } label: {
                        profileRow(title: L("书库设置"))
                    }.accessibilityIdentifier("profile.librarySettings")
                    NavigationLink { NativeDictionary(store: store, initialTerm: "").nativeBackButton() } label: {
                        profileRow(title: L("本地词典"))
                    }.accessibilityIdentifier("profile.dictionary")
                }

                groupLabel(L("服务与扩展"))
                profileGroup {
                    NavigationLink { NativeAiSettings(store: store) } label: {
                        profileRow(title: L("AI 助手"), value: summary?.ai ?? "")
                    }.accessibilityIdentifier("profile.ai")
                    NavigationLink { NativeEmailSettings(store: store) } label: {
                        profileRow(title: L("发送到 Kindle"), value: summary?.kindle ?? "")
                    }.accessibilityIdentifier("profile.kindle")
                }

                groupLabel(L("数据与存储"))
                profileGroup {
                    profileRow(title: L("书籍占用"), value: summary?.storage ?? "—", chevron: false)
                    NavigationLink { NativeBackupPage(store: store) } label: {
                        profileRow(title: L("备份与恢复"))
                    }.accessibilityIdentifier("profile.backup")
                    NavigationLink { NativeTrash(store: store) } label: {
                        profileRow(title: L("回收站"))
                    }.accessibilityIdentifier("profile.trash")
                }

                groupLabel(L("支持与关于"))
                profileGroup {
                    NavigationLink { NativeFeedbackPage(store: store) } label: {
                        profileRow(title: L("问题反馈"))
                    }.accessibilityIdentifier("profile.feedback")
                    Button {
                        UIPasteboard.general.string = "1109898894"
                        store.showToast(L("群号已复制到剪贴板"))
                    } label: {
                        profileRow(title: L("QQ 交流群"), value: "1109898894", chevron: false)
                    }
                    NavigationLink { NativeAboutPage(store: store, version: summary?.version ?? "") } label: {
                        profileRow(title: L("关于 Libzora"), value: summary?.version ?? "")
                    }.accessibilityIdentifier("profile.about")
                }
            }.padding(.horizontal, 24).padding(.top, 20).padding(.bottom, 120).buttonStyle(.plain)
        }.background(NativeAmbientBackground()).navigationBarHidden(true)
            .overlay(alignment: .top) { NativeStatusBarGlass() }
            .overlay {
                if let preferenceMenu {
                    NativePreferenceMenuOverlay(request: preferenceMenu) { self.preferenceMenu = nil }
                        .zIndex(10)
                }
            }
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

    /// Identity and cloud sync stay together without a surrounding card. The
    /// avatar and text hierarchy distinguish the account from the settings.
    @ViewBuilder private var accountSection: some View {
        let signedIn = account?.isLoggedIn == true
        let name = signedIn ? (account?.user?.username ?? L("已登录")) : L("登录 / 注册")
        VStack(spacing: 0) {
            NavigationLink { NativeAccountPage(store: store) } label: {
                HStack(spacing: 14) {
                    avatar(signedIn: signedIn, name: name)
                    VStack(alignment: .leading, spacing: 5) {
                        HStack(spacing: 8) {
                            Text(name).font(NativeTheme.font(17, relativeTo: .headline).weight(.semibold))
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
                    Image(systemName: "chevron.right").font(.system(size: 12, weight: .regular))
                        .foregroundStyle(detailColor)
                }.padding(.vertical, 16).contentShape(Rectangle())
            }.accessibilityIdentifier("profile.account")
            NavigationLink { NativeSyncSettings(store: store) } label: {
                profileRow(title: L("云同步"), value: summary?.sync ?? "")
            }.accessibilityIdentifier("profile.sync")
        }
    }

    private var themeRow: some View {
        let follow = store.uiLanguage == "en-US" ? L("系统") : L("跟随系统")
        return preferenceMenuRow(title: L("外观"), identifier: "profile.theme",
                                 selection: store.preferences.themeMode,
                                 options: [(0, follow), (1, L("浅色")), (2, L("深色"))]) { mode in
            var value = store.preferences
            value.themeMode = mode
            store.setPreferences(value)
        }
    }

    private var languageRow: some View {
        preferenceMenuRow(title: L("语言"), identifier: "profile.language",
                          selection: store.preferences.uiLanguage,
                          options: [("auto", L("跟随系统")), ("zh-CN", "简体中文"), ("en-US", "English")]) { language in
            var value = store.preferences
            value.uiLanguage = language
            store.setPreferences(value)
        }
    }

    private func preferenceMenuRow<Selection: Equatable>(
        title: String, identifier: String, selection: Selection,
        options: [(id: Selection, title: String)], select: @escaping (Selection) -> Void
    ) -> some View {
        let value = options.first { $0.id == selection }?.title ?? options.first?.title ?? ""
        let menuOptions = options.map { option in
            NativePreferenceMenuOption(
                id: String(describing: option.id),
                identifier: "\(identifier).option.\(option.id)",
                title: option.title,
                selected: option.id == selection,
                select: { if option.id != selection { select(option.id) } })
        }
        return profileRow(title: title, value: value)
            .accessibilityHidden(true)
            .overlay {
                NativeTrailingPreferenceMenu(title: title, value: value, identifier: identifier) { frame in
                    preferenceMenu = NativePreferenceMenuRequest(identifier: identifier, sourceFrame: frame, options: menuOptions)
                }
            }
    }

    private func groupLabel(_ title: String) -> some View {
        Text(title).font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(secondaryColor)
            .padding(.top, 26).padding(.bottom, 6)
    }

    private func profileGroup<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) { content() }.frame(maxWidth: .infinity)
    }

    private func profileRow(title: String, value: String = "", chevron: Bool = true) -> some View {
        NativeSettingsRow(title: title, value: value, chevron: chevron, detailColor: detailColor)
    }

    private func avatar(signedIn: Bool, name: String) -> some View {
        ZStack {
            Circle().fill(NativeTheme.strong)
            if signedIn {
                Text(String(name.prefix(1))).font(NativeTheme.font(19, relativeTo: .headline))
                    .foregroundStyle(NativeTheme.secondary)
            } else {
                Image(systemName: "person").font(.system(size: 18, weight: .regular))
                    .foregroundStyle(NativeTheme.secondary)
            }
        }.frame(width: 46, height: 46).accessibilityHidden(true)
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

private struct NativePreferenceMenuOption: Identifiable {
    let id: String
    let identifier: String
    let title: String
    let selected: Bool
    let select: () -> Void
}

private struct NativePreferenceMenuRequest {
    let identifier: String
    let sourceFrame: CGRect
    let options: [NativePreferenceMenuOption]
}

/// Keep the full row tappable while letting the profile page draw its own glass menu.
private struct NativeTrailingPreferenceMenu: UIViewRepresentable {
    var title: String
    var value: String
    var identifier: String
    var present: (CGRect) -> Void

    final class Coordinator: NSObject {
        var present: ((CGRect) -> Void)?

        @objc func open(_ sender: UIButton) {
            guard let window = sender.window else { return }
            present?(sender.convert(sender.bounds, to: window))
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> UIButton {
        let button = UIButton(type: .custom)
        button.backgroundColor = .clear
        button.addTarget(context.coordinator, action: #selector(Coordinator.open(_:)), for: .touchUpInside)
        return button
    }

    func updateUIView(_ button: UIButton, context: Context) {
        context.coordinator.present = present
        button.accessibilityLabel = title
        button.accessibilityValue = value
        button.accessibilityIdentifier = identifier
    }
}

private struct NativePreferenceMenuOverlay: View {
    let request: NativePreferenceMenuRequest
    let dismiss: () -> Void

    private static let menuWidth: CGFloat = 250
    private static let rowHeight: CGFloat = 46
    private static let tabBarClearance: CGFloat = 96

    var body: some View {
        GeometryReader { safeArea in
            menu(in: safeArea.frame(in: .global)).ignoresSafeArea()
        }
        .transition(.opacity)
    }

    private func menu(in safeArea: CGRect) -> some View {
        GeometryReader { proxy in
            let origin = proxy.frame(in: .global).origin
            let anchor = CGRect(x: request.sourceFrame.minX - origin.x,
                                y: request.sourceFrame.minY - origin.y,
                                width: request.sourceFrame.width,
                                height: request.sourceFrame.height)
            let width = min(Self.menuWidth, max(0, proxy.size.width - 24))
            let naturalHeight = CGFloat(request.options.count) * Self.rowHeight + 10
            let topLimit = max(12, safeArea.minY - origin.y + 8)
            let bottomLimit = max(topLimit, proxy.size.height - Self.tabBarClearance)
            let height = min(naturalHeight, max(0, bottomLimit - topLimit))
            let selectedIndex = request.options.firstIndex(where: \.selected) ?? 0
            let preferredTop = anchor.midY - CGFloat(selectedIndex) * Self.rowHeight - Self.rowHeight / 2 - 5
            let top = min(max(preferredTop, topLimit), max(topLimit, bottomLimit - height))
            let centerX = min(max(anchor.maxX - width / 2, width / 2 + 12),
                              proxy.size.width - width / 2 - 12)

            ZStack(alignment: .topLeading) {
                Color.black.opacity(0.001)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture(perform: dismiss)
                    .accessibilityHidden(true)

                NativePreferenceMenuPanel(options: request.options) { option in
                    dismiss()
                    option.select()
                }
                .frame(width: width, height: height)
                .accessibilityIdentifier(request.identifier + ".menu")
                .position(x: centerX, y: top + height / 2)
            }
        }
    }
}

private struct NativePreferenceMenuPanel: View {
    let options: [NativePreferenceMenuOption]
    let choose: (NativePreferenceMenuOption) -> Void

    private var rows: some View {
        VStack(spacing: 0) {
            ForEach(options) { option in
                Button { choose(option) } label: {
                    HStack(spacing: 12) {
                        Group {
                            if option.selected {
                                Image(systemName: "checkmark").font(.system(size: 13, weight: .semibold))
                            } else {
                                Color.clear
                            }
                        }
                        .frame(width: 16, height: 16)
                        Text(option.title)
                            .font(NativeTheme.font(16))
                            .foregroundStyle(NativeTheme.ink)
                            .lineLimit(1)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxWidth: .infinity, minHeight: 46, alignment: .leading)
                    .padding(.horizontal, 20)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(option.title)
                .accessibilityIdentifier(option.identifier)
            }
        }
        .padding(.vertical, 5)
    }

    @ViewBuilder var body: some View {
        if #available(iOS 26.0, *) {
            rows
                .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 26, style: .continuous))
                .shadow(color: .black.opacity(0.10), radius: 18, y: 8)
        } else {
            rows
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 22, style: .continuous))
                .shadow(color: .black.opacity(0.16), radius: 22, y: 9)
        }
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
                Button(L("导出备份")) { run(false) }
                    .disabled(busy).accessibilityIdentifier("profile.backup.export")
                Button(L("恢复备份")) { run(true) }
                    .disabled(busy).accessibilityIdentifier("profile.backup.restore")
                if busy { HStack(spacing: 10) { ProgressView().controlSize(.small); Text(L("正在处理…")).foregroundStyle(NativeTheme.secondary) } }
            }
            Section {
                Text(L("备份包含书库、阅读进度、书签与批注。导出与恢复都需要同步密钥；密钥不会写入备份。"))
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }
        }
        .navigationTitle(L("备份与恢复")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeHideTabBar())
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
                    NativeSettingsRow(title: L("版本"), value: version, chevron: false)
                }.buttonStyle(.plain).accessibilityIdentifier("profile.about.version")
                NativeSettingsRow(title: L("开发者"), value: "kingstacker", chevron: false)
            }
        }
        .navigationTitle(L("关于")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeHideTabBar())
        .sheet(isPresented: $developer) {
            NativeNameSheet(store: store, title: L("开发者模式"), initial: "", button: L("进入"), isSecure: true) { password in
                try await store.command("settings.developer.toggle", ["password": password])
            }
        }
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
            settingToggle(L("画廊视图"), identifier: "library.settings.gallery", isOn: Binding(get: { store.preferences.galleryMode }, set: { enabled in
                var value = store.preferences; value.galleryMode = enabled
                store.setPreferences(value)
            }))
            settingToggle(L("炫彩模式"), identifier: "library.settings.colorful", isOn: Binding(get: { store.preferences.colorfulMode }, set: { enabled in
                var value = store.preferences; value.colorfulMode = enabled
                store.setPreferences(value)
            }))
            settingToggle(L("按系列叠放"), identifier: "library.settings.stackBookSeries", isOn: Binding(get: { store.preferences.stackBookSeries }, set: { enabled in
                var value = store.preferences; value.stackBookSeries = enabled
                store.setPreferences(value)
            }))
            settingToggle(L("显示书籍评分"), identifier: "library.settings.rating", isOn: Binding(get: { store.preferences.showBookRating }, set: { enabled in
                var value = store.preferences; value.showBookRating = enabled
                store.setPreferences(value)
            }))
            settingToggle(L("显示书籍同步状态"), identifier: "library.settings.syncStatus", isOn: Binding(get: { store.preferences.showBookSyncStatus }, set: { enabled in
                var value = store.preferences; value.showBookSyncStatus = enabled
                store.setPreferences(value)
            }))
        }
        .navigationTitle(L("书库设置")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeHideTabBar())
    }

    private func settingToggle(_ title: String, identifier: String, isOn: Binding<Bool>) -> some View {
        HStack(spacing: 12) {
            Text(title).font(NativeTheme.font()).accessibilityHidden(true)
            Spacer(minLength: 12)
            NativeSwitch(title: title, isOn: isOn, identifier: identifier).fixedSize()
        }.frame(minHeight: 44).accessibilityElement(children: .contain)
    }
}

struct NativeSyncSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = SyncForm()
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
                Picker(L("同步方式"), selection: $form.provider) { Text("S3").tag(0); Text("WebDAV").tag(1) }
                    .pickerStyle(.segmented).accessibilityIdentifier("sync.provider")
                Toggle(L("启用同步"), isOn: $form.enabled).tint(NativeTheme.systemSwitchTint)
                Toggle(L("自动同步"), isOn: $form.automaticSyncEnabled).tint(NativeTheme.systemSwitchTint)
                caption(L("仅支持自备 S3 / WebDAV，不提供官方云同步。试用与买断同步功能相同。"))
            }
            customStorageSections
            Section {
                VStack(alignment: .leading, spacing: 10) {
                    if !status.isEmpty {
                        Text(status).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            .textSelection(.enabled).accessibilityIdentifier("sync.status")
                    }
                    if busy { ProgressView() }
                    HStack(spacing: 12) {
                        Button(L("测试连接")) { run("test") }
                        Button(L("保存")) { run("save") }.accessibilityIdentifier("sync.save")
                    }.buttonStyle(NativePrimaryButtonStyle()).disabled(busy || loading)
                }
            }
        }.disabled(busy || loading)
            .navigationTitle(L("同步")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .modifier(NativeKeyboardDone())
            .modifier(NativeHideTabBar())
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
        Section(L("连接信息")) {
            if form.provider == 1 {
                field(L("服务地址"), text: $form.webDavEndpoint)
                field(L("用户名"), text: $form.webDavUsername)
                secret(L("密码"), text: $form.webDavPassword)
            } else {
                field(L("Endpoint（AWS 可留空）"), text: $form.endpoint)
                field("Bucket", text: $form.bucket)
                field("Region", text: $form.region)
                field("Access Key", text: $form.accessKey)
                secret("Secret Key", text: $form.secretKey)
            }
            field(L("目录前缀"), text: $form.prefix)
        }
        Section {
            DisclosureGroup(L("高级选项"), isExpanded: $advanced) {
                if form.provider == 0 { Toggle("Path Style", isOn: $form.pathStyle).tint(NativeTheme.systemSwitchTint) }
                Toggle(L("同步时下载书籍文件"), isOn: $form.downloadBookFilesOnSync).tint(NativeTheme.systemSwitchTint)
                Toggle(L("跳过 TLS 验证"), isOn: $form.skipTlsVerify).tint(NativeTheme.systemSwitchTint)
            }
            syncKey
            Button(L("导入连接参数")) { run("import") }
            Button(L("导出连接参数")) { run("export") }
        }
        Section {
            caption(summary)
            Button(L("立即同步")) { run("run") }.accessibilityIdentifier("sync.run")
        }
    }

    @ViewBuilder private var syncKey: some View {
        // This user-owned key must not be offered to password AutoFill.
        NativeTextField(title: L("同步密钥（备份必填，同步建议加密）"), text: $form.encryptionKey, secure: true,
                        identifier: "sync.encryptionKey")
            .textContentType(.oneTimeCode).textInputAutocapitalization(.never).disableAutocorrection(true)
        NativeTextField(title: L("再次输入同步密钥"), text: $confirmEncryptionKey, secure: true,
                        identifier: "sync.encryptionKeyConfirm")
            .textContentType(.oneTimeCode).textInputAutocapitalization(.never).disableAutocorrection(true)
        caption(L("设置或更改密钥时需要输入两次，并在弹窗中确认。密钥由你自行牢记，不会上传或写入备份。遗忘后无法解密已有数据，设置新密钥也无法恢复旧数据。"))
    }

    private func caption(_ text: String, color: Color = NativeTheme.secondary) -> some View {
        Text(text).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(color)
    }
    private func field(_ title: String, text: Binding<String>) -> some View {
        NativeTextField(title: title, text: text).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func secret(_ title: String, text: Binding<String>) -> some View {
        NativeTextField(title: title, text: text, secure: true).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func loadSnapshot() async throws -> SyncForm {
        let value: SyncFormSnapshot = try await store.request("settings.sync.get")
        summary = value.summary
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
                        status = L("已保存"); _ = try? await loadSnapshot()
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
    @State private var providerTask: Task<Void, Never>?

    var body: some View {
        NativeForm {
            Section {
                Picker(L("服务"), selection: $form.provider) { Text("DeepSeek").tag("deepseek"); Text("OpenAI").tag("openai"); Text(L("自定义")).tag("custom") }
                    .pickerStyle(.segmented)
                NativeTextField(title: L("接口地址"), text: $form.baseUrl).textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeTextField(title: L("模型"), text: $form.model).textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeTextField(title: "API Key", text: $form.apiKey, secure: true).textInputAutocapitalization(.never).disableAutocorrection(true)
            }
            Section {
                Button(L("测试连接")) { run(test: true) }
                if busy { ProgressView() }
                if !status.isEmpty { Text(status).foregroundStyle(NativeTheme.secondary).textSelection(.enabled) }
            }
        }.disabled(loading || busy)
            .navigationTitle(L("AI 接口")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
            .nativeBackButton()
            .toolbar { Button(L("保存")) { run(test: false) }.disabled(busy || loading) }
            .task {
                defer { loading = false }
                do { form = try await store.request("settings.ai.get") }
                catch { if !Task.isCancelled { status = error.localizedDescription } }
            }
            .onChange(of: form.provider) { provider in
                guard !loading else { return }
                providerTask?.cancel()
                providerTask = Task {
                    do {
                        let defaults: AiDefaults = try await store.request("settings.ai.defaults", ["provider": provider])
                        guard !Task.isCancelled, form.provider == provider else { return }
                        form.baseUrl = defaults.baseUrl; form.model = defaults.model
                    } catch { }
                }
            }
            .onDisappear { providerTask?.cancel() }
    }
    private func run(test: Bool) {
        busy = true; status = test ? L("正在测试连接…") : ""
        store.perform {
            defer { busy = false }
            let args = ["settings": try requestObject(form)]
            if test { let value: MessageResult = try await store.request("settings.ai.test", args); status = value.message }
            else { try await store.command("settings.ai.save", args); status = L("已保存") }
        }
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
            Section(L("收件人")) { NativeTextField(title: L("Kindle 邮箱"), text: $form.kindleEmailAddress).keyboardType(.emailAddress) }
            Section(L("发件邮箱")) {
                NativeTextField(title: L("发件人邮箱"), text: $form.senderEmailAddress).keyboardType(.emailAddress)
                NativeTextField(title: L("SMTP 服务器"), text: $form.smtpHost)
                HStack { Text(L("端口")); TextField("587", value: $form.smtpPort, formatter: NumberFormatter()).keyboardType(.numberPad).multilineTextAlignment(.trailing) }
                NativeTextField(title: L("SMTP 用户名"), text: $form.smtpUsername)
                NativeTextField(title: L("密码或应用专用密码"), text: $form.smtpPassword, secure: true)
                Toggle(L("使用 SSL / TLS"), isOn: $form.enableSsl).tint(NativeTheme.systemSwitchTint)
            }
            if !status.isEmpty { Section { Text(status).foregroundStyle(NativeTheme.secondary) } }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .navigationTitle(L("发送到 Kindle")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
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
                if items.isEmpty { Text(L("回收站是空的")).foregroundStyle(NativeTheme.secondary) }
                ForEach(items) { item in
                    VStack(alignment: .leading, spacing: 10) {
                        Text(item.title).font(NativeTheme.font(17, relativeTo: .headline))
                        Text(item.detail).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        HStack {
                            Button(L("恢复")) { action("restore", id: item.id) }.buttonStyle(.bordered)
                            Button(L("永久删除"), role: .destructive) { action("purge", id: item.id) }.buttonStyle(.bordered)
                        }
                    }.padding(.vertical, 8)
                }
            }.listRowBackground(store.preferences.colorfulMode ? Color.clear : NativeTheme.page)
        }.modifier(NativeListBackground()).disabled(busy).navigationTitle(L("回收站")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
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
