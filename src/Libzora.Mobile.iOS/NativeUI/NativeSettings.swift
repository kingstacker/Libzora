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

    private var account: AccountStatus? { store.account ?? summary?.account }
    private var secondaryColor: Color { store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary }
    private var detailColor: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }

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
        }.background(NativeAmbientBackground()).navigationBarHidden(true)
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

    private var profileContent: some View {
        VStack(alignment: .leading, spacing: 0) {
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
                    NavigationLink { NativeAboutPage(store: store, version: summary?.version ?? "") } label: {
                        profileRow(title: L("关于 Libzora"), value: summary?.version ?? "")
                    }.accessibilityIdentifier("profile.about")
                }
        }.padding(.horizontal, NativeAdaptiveLayout.pageInset).padding(.top, 8)
            .padding(.bottom, 120).buttonStyle(.plain)
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
        return profileRow(title: title, chevron: false)
            .accessibilityHidden(true)
            .overlay {
                NativeTrailingPreferenceMenu(title: title, value: value, detailColor: detailColor,
                                             identifier: identifier, options: menuOptions)
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

/// Keep the full row tappable while letting UIKit own and morph the trailing value + chevron.
private struct NativeTrailingPreferenceMenu: UIViewRepresentable {
    var title: String
    var value: String
    var detailColor: Color
    var identifier: String
    var options: [NativePreferenceMenuOption]

    final class MenuButton: UIButton {
        let trailingView: UIView = {
            let view = UIView()
            view.backgroundColor = .clear
            view.isUserInteractionEnabled = false
            return view
        }()
        let valueLabel: UILabel = {
            let label = UILabel()
            let base = UIFont(name: "KingHwaOldSong", size: 14) ?? .systemFont(ofSize: 14)
            label.font = UIFontMetrics(forTextStyle: .body).scaledFont(for: base)
            label.adjustsFontForContentSizeCategory = true
            label.numberOfLines = 1
            return label
        }()
        let chevronView: UIImageView = {
            let config = UIImage.SymbolConfiguration(pointSize: 12, weight: .regular)
            let image = UIImage(systemName: "chevron.right", withConfiguration: config)
            let imageView = UIImageView(image: image)
            imageView.contentMode = .scaleAspectFit
            return imageView
        }()

        override init(frame: CGRect) {
            super.init(frame: frame)
            trailingView.addSubview(valueLabel)
            trailingView.addSubview(chevronView)
            addSubview(trailingView)
        }

        required init?(coder: NSCoder) {
            fatalError("init(coder:) has not been implemented")
        }

        override func layoutSubviews() {
            super.layoutSubviews()
            let valueSize = valueLabel.sizeThatFits(CGSize(width: bounds.width, height: bounds.height))
            let chevronSize = chevronView.sizeThatFits(CGSize(width: 24, height: bounds.height))
            let spacing: CGFloat = valueSize.width > 0 ? 8 : 0
            let horizontalPadding: CGFloat = 10
            let contentWidth = valueSize.width + spacing + chevronSize.width
            let containerWidth = min(bounds.width, contentWidth + horizontalPadding)
            let containerHeight = min(max(bounds.height - 6, 34), bounds.height)
            // Flush trailing edge of the visible content to bounds.maxX while giving
            // the glass morph preview balanced padding on the left.
            let containerX = bounds.maxX - containerWidth + horizontalPadding / 2
            let clampedX = max(bounds.minX, min(containerX, bounds.maxX - containerWidth))
            trailingView.frame = CGRect(x: clampedX, y: (bounds.height - containerHeight) / 2,
                                        width: containerWidth, height: containerHeight)

            let rightEdge = containerWidth - (bounds.maxX - (clampedX + containerWidth))
            let chevronX = rightEdge - chevronSize.width
            chevronView.frame = CGRect(x: chevronX, y: (containerHeight - chevronSize.height) / 2,
                                       width: chevronSize.width, height: chevronSize.height)
            valueLabel.frame = CGRect(x: max(0, chevronX - spacing - valueSize.width),
                                      y: (containerHeight - valueSize.height) / 2,
                                      width: valueSize.width, height: valueSize.height)
        }

        override func menuAttachmentPoint(for configuration: UIContextMenuConfiguration) -> CGPoint {
            CGPoint(x: max(bounds.minX, bounds.maxX - 18), y: bounds.midY)
        }

        override func contextMenuInteraction(
            _ interaction: UIContextMenuInteraction,
            previewForHighlightingMenuWithConfiguration configuration: UIContextMenuConfiguration
        ) -> UITargetedPreview? {
            targetedPreview()
        }

        override func contextMenuInteraction(
            _ interaction: UIContextMenuInteraction,
            previewForDismissingMenuWithConfiguration configuration: UIContextMenuConfiguration
        ) -> UITargetedPreview? {
            targetedPreview()
        }

        private func targetedPreview() -> UITargetedPreview? {
            guard trailingView.window != nil else { return nil }
            let parameters = UIPreviewParameters()
            parameters.backgroundColor = .clear
            return UITargetedPreview(view: trailingView, parameters: parameters)
        }
    }

    final class Coordinator: NSObject {
        var signature: [String] = []
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> MenuButton {
        let button = MenuButton(type: .custom)
        button.backgroundColor = .clear
        button.showsMenuAsPrimaryAction = true
        return button
    }

    func updateUIView(_ button: MenuButton, context: Context) {
        button.accessibilityLabel = title
        button.accessibilityValue = value
        button.accessibilityIdentifier = identifier
        let tint = UIColor(detailColor)
        button.valueLabel.text = value
        button.valueLabel.textColor = tint
        button.chevronView.tintColor = tint
        button.setNeedsLayout()
        button.layoutIfNeeded()

        let signature = options.map { "\($0.id)|\($0.title)|\($0.selected)" }
        guard context.coordinator.signature != signature else { return }
        context.coordinator.signature = signature

        let actions = options.map { option in
            let action = UIAction(title: option.title, state: option.selected ? .on : .off) { _ in
                option.select()
            }
            action.accessibilityIdentifier = option.identifier
            return action
        }
        button.menu = UIMenu(children: actions)
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
                Toggle(L("启用同步"), isOn: $form.enabled).tint(NativeTheme.systemSwitchTint)
                Toggle(L("自动同步"), isOn: $form.automaticSyncEnabled).tint(NativeTheme.systemSwitchTint)
                caption(L("可使用 iCloud 云盘或自备 S3 / WebDAV 同步书籍、阅读进度和批注。"))
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
            } else if form.provider == 0 {
                field(L("Endpoint（AWS 可留空）"), text: $form.endpoint)
                field("Bucket", text: $form.bucket)
                field("Region", text: $form.region)
                field("Access Key", text: $form.accessKey)
                secret("Secret Key", text: $form.secretKey)
            } else if form.provider == 3 {
                Label(L("iCloud 云盘"), systemImage: "icloud")
                    .font(NativeTheme.font()).accessibilityIdentifier("sync.icloud.info")
                caption(L("在系统设置中开启 iCloud 云盘。iPhone、iPad 和 Mac 使用同一 Apple 账户、相同区域和目录前缀即可同步，无需填写服务器或密码。"))
                caption(L("数据占用你的 iCloud 存储空间，其他设备可能需要稍等片刻才能收到更新。"))
            }
            field(L("目录前缀"), text: $form.prefix)
        }
        Section {
            DisclosureGroup(L("高级选项"), isExpanded: $advanced) {
                if form.provider == 0 { Toggle("Path Style", isOn: $form.pathStyle).tint(NativeTheme.systemSwitchTint) }
                Toggle(L("同步时下载书籍文件"), isOn: $form.downloadBookFilesOnSync).tint(NativeTheme.systemSwitchTint)
                if form.provider != 3 { Toggle(L("跳过 TLS 验证"), isOn: $form.skipTlsVerify).tint(NativeTheme.systemSwitchTint) }
            }
            syncKey
            if form.provider != 3 {
                Button(L("导入连接参数")) { run("import") }
                Button(L("导出连接参数")) { run("export") }
            }
        }
        Section {
            caption(summary)
            Button(L("立即同步")) { run("run") }.accessibilityIdentifier("sync.run")
        }
    }

    @ViewBuilder private var syncKey: some View {
        // This user-owned key must not be offered to password AutoFill.
        NativeTextField(title: L("同步密钥（云同步与备份必填）"), text: $form.encryptionKey, secure: true,
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
                NativeTextField(title: L("接口地址"), text: $form.baseUrl, identifier: "ai.baseUrl").textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeTextField(title: L("模型"), text: $form.model, identifier: "ai.model").textInputAutocapitalization(.never).disableAutocorrection(true)
                Button { fetchModels() } label: {
                    HStack {
                        Text(L("获取模型")).font(NativeTheme.font())
                        Spacer()
                    }
                }.accessibilityIdentifier("ai.fetchModels")
                if !fetchedModels.isEmpty {
                    Button { showModelPicker = true } label: {
                        NativeSettingsRow(title: L("选择模型"),
                                          value: form.model.isEmpty ? L("{0} 个可用", fetchedModels.count) : form.model)
                    }.buttonStyle(.plain).accessibilityIdentifier("ai.selectModel")
                }
                NativeTextField(title: "API Key", text: $form.apiKey, secure: true, identifier: "ai.apiKey").textInputAutocapitalization(.never).disableAutocorrection(true)
            }
            Section {
                Button(L("测试连接")) { run(test: true) }.accessibilityIdentifier("ai.test")
                if busy { ProgressView() }
                if !status.isEmpty {
                    Text(status).foregroundStyle(NativeTheme.secondary).textSelection(.enabled)
                        .accessibilityIdentifier("ai.status")
                }
            }
        }.disabled(loading || busy)
            .navigationTitle(L("AI 接口")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
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
