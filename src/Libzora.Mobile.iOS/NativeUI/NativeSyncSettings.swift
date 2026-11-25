import SwiftUI
import UIKit

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
    @State private var showMissingEncryptionKeyPrompt = false
    @State private var missingEncryptionKeyMessage = ""
    @State private var pendingEncryptionKeyFocus: SyncKeyFieldFocus = .key
    @FocusState private var focusedKeyField: SyncKeyFieldFocus?

    private enum SyncKeyFieldFocus: Hashable {
        case key
        case confirm
    }

    var body: some View {
        ScrollViewReader { proxy in
        NativeForm {
            Section {
                Picker(L("同步方式"), selection: $form.provider) {
                    ForEach(providers) { provider in Text(provider.displayName).tag(provider.provider) }
                }.pickerStyle(.segmented).accessibilityIdentifier("sync.provider")
            }
            .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 2, trailing: 0))
            .listRowBackground(Color.clear)

            Section {
                Toggle(L("启用同步"), isOn: Binding(
                    get: { form.enabled },
                    set: { next in
                        if !next {
                            form.enabled = false
                            return
                        }
                        switch NativeSyncKeyConfirmation.canEnableSync(
                            key: form.encryptionKey,
                            savedKey: savedEncryptionKey,
                            confirmation: confirmEncryptionKey) {
                        case .allow:
                            form.enabled = true
                        case .missingKey:
                            form.enabled = false
                            pendingEncryptionKeyFocus = .key
                            missingEncryptionKeyMessage = L("开启同步前，请先在下方设置同步密钥。")
                            showMissingEncryptionKeyPrompt = true
                        case .unconfirmedKey:
                            form.enabled = false
                            pendingEncryptionKeyFocus = .confirm
                            missingEncryptionKeyMessage = L("请先再次输入并确认一致的同步密钥，再开启同步。")
                            showMissingEncryptionKeyPrompt = true
                        }
                    }
                )).tint(NativeTheme.systemSwitchTint)
                Toggle(L("自动同步"), isOn: $form.automaticSyncEnabled).tint(NativeTheme.systemSwitchTint)
            } footer: {
                footerCaption(L("可使用 iCloud 云盘或自备 S3 / WebDAV 同步书籍、阅读进度和批注。"))
            }
            .alert(Text(L("需要设置同步密钥")), isPresented: $showMissingEncryptionKeyPrompt) {
                Button(L("取消"), role: .cancel) { }
                Button(L("去设置")) {
                    let targetId = pendingEncryptionKeyFocus == .confirm
                        ? "sync.encryptionKeyConfirm"
                        : "sync.encryptionKey"
                    withAnimation { proxy.scrollTo(targetId, anchor: .center) }
                    focusedKeyField = pendingEncryptionKeyFocus
                }
            } message: {
                Text(missingEncryptionKeyMessage)
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
            .focused($focusedKeyField, equals: .key)
            .id("sync.encryptionKey")
        NativeInlineTextField(title: L("再次输入同步密钥"), text: $confirmEncryptionKey, secure: true,
                              identifier: "sync.encryptionKeyConfirm")
            .textContentType(.oneTimeCode).textInputAutocapitalization(.never).disableAutocorrection(true)
            .focused($focusedKeyField, equals: .confirm)
            .id("sync.encryptionKeyConfirm")
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

