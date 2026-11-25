import AuthenticationServices
import SwiftUI

struct NativeAccountPage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.colorScheme) private var colorScheme
    @State private var serverUrl = ""
    @State private var username = ""
    @State private var email = ""
    @State private var password = ""
    @State private var confirmation = ""
    @State private var register = false
    @State private var resetMode = false
    @State private var resetCode = ""
    @State private var resetPassword = ""
    @State private var resetConfirmation = ""
    @State private var emailCode = ""
    @State private var emailCodeSent = false
    @State private var confirmUnbindApple = false
    @State private var showingCloseAccount = false
    @State private var closePassword = ""
    @State private var closeConfirmation = ""
    @State private var closeError = ""
    @State private var loading = true
    @State private var busy = false
    @State private var status = ""
    @State private var actionTask: Task<Void, Never>?

    /// Mirrors Libzora.Core.AccountAvailability.RegistrationAvailable. UI tests
    /// opt in with --libzora-enable-registration; distributed builds stay closed.
    private var registrationAvailable: Bool {
        ProcessInfo.processInfo.arguments.contains("--libzora-enable-registration")
    }

    var body: some View {
        NativeForm {
            if let account = store.account, account.isLoggedIn {
                restartNotice
                emailVerificationSection
                appleLinkSection
                NativeMembershipSections(store: store, onSignOut: { signOut() },
                                         signingOut: busy, accountStatus: status)
                closeAccountSection
            } else {
                Section {
                    Picker(L("账号操作"), selection: $register) { Text(L("登录")).tag(false); Text(L("注册")).tag(true) }
                        .pickerStyle(.segmented).accessibilityIdentifier("account.mode")
                        .disabled(resetMode)
                } footer: {
                    Text(resetMode ? L("输入发送到注册邮箱的验证码，然后设置新密码。")
                        : register ? (registrationAvailable ? L("注册后可在多台设备同步书籍和阅读进度。")
                            : L("注册暂未上线。当前版本无需注册即可免费使用全部功能。"))
                        : L("使用用户名或邮箱登录同一账号。"))
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(register && !registrationAvailable ? NativeTheme.warning
                                         : (store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary))
                        .accessibilityIdentifier("account.registrationUnavailable")
                }
                .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 2, trailing: 0))
                .listRowBackground(Color.clear)

                Section {
                    if register {
                        accountField(L("邮箱"), text: $email, identifier: "account.emailField")
                            .textContentType(.emailAddress).keyboardType(.emailAddress)
                    }
                    accountField(register ? L("用户名") : L("用户名或邮箱"), text: $username,
                                 identifier: "account.usernameField")
                        .textContentType(.username)
                    if resetMode {
                        accountField(L("邮箱验证码（8 位）"), text: $resetCode, identifier: "account.reset.code")
                            .textContentType(.oneTimeCode)
                        accountField(L("新密码（8–128 位）"), text: $resetPassword, secure: true,
                                     identifier: "account.reset.password")
                            .textContentType(.newPassword)
                        accountField(L("确认新密码"), text: $resetConfirmation, secure: true,
                                     identifier: "account.reset.confirmation")
                            .textContentType(.newPassword)
                    } else if register {
                        accountField(L("登录密码（8–128 位）"), text: $password, secure: true,
                                     identifier: "account.password")
                            .textContentType(.newPassword)
                        accountField(L("确认登录密码"), text: $confirmation, secure: true,
                                     identifier: "account.confirmation")
                            .textContentType(.newPassword)
                    } else {
                        accountField(L("登录密码（8–128 位）"), text: $password, secure: true,
                                     identifier: "account.password")
                            .textContentType(.password)
                    }
                } header: {
                    Text(resetMode ? L("重置密码") : register ? L("注册信息") : L("登录信息"))
                } footer: {
                    if register {
                        VStack(alignment: .leading, spacing: 3) {
                            accountFooterText(L("注册邮箱不可修改。"))
                            accountFooterText(L("3–32 位字母、数字、点、下划线或连字符"))
                        }
                    } else if resetMode {
                        accountFooterText(L("验证码 15 分钟内有效。"))
                    }
                }
                regionSection
                actionSection
                if !resetMode {
                    appleSignInSection
                }
            }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .id(store.account?.isLoggedIn == true ? "account.signed-in" : "account.signed-out")
            .navigationTitle(L("用户账号")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .modifier(NativeKeyboardDone())
            .confirmationDialog(L("解绑 Apple ID？"), isPresented: $confirmUnbindApple, titleVisibility: .visible) {
                Button(L("解绑 Apple ID"), role: .destructive) { unbindAppleId() }
                Button(L("取消"), role: .cancel) {}
            } message: {
                Text(L("解绑后将无法再使用此 Apple ID 直接登录当前账号。"))
            }
            .sheet(isPresented: $showingCloseAccount, onDismiss: {
                closePassword = ""; closeConfirmation = ""; closeError = ""
            }) {
                closeAccountSheet
            }
            .task { await load() }
            .onChange(of: register) { _ in resetMode = false; clearPasswords(); status = "" }
            .onDisappear { actionTask?.cancel(); clearPasswords() }
    }

    /// A region switch reloads the region's account automatically, so the
    /// restart warning must also stay visible on the signed-in page.
    @ViewBuilder private var restartNotice: some View {
        if store.account?.requiresDataRestart == true {
            Text(L("区域已切换，请重新打开应用后使用该区域的独立书库和同步。"))
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.warning)
                .accessibilityIdentifier("account.restart")
        }
    }

    /// Region belongs to the signed-out form only, right before the sign-in
    /// action; a signed-in page keeps the region chosen at login.
    @ViewBuilder private var regionSection: some View {
        Section {
            HStack(spacing: 12) {
                Text(L("服务器区域"))
                    .font(NativeTheme.font())
                    .foregroundStyle(NativeTheme.ink)
                Spacer(minLength: 12)
                Picker(L("服务器区域"), selection: Binding(get: { store.account?.region ?? 0 }, set: { selectRegion($0) })) {
                    Text(L("中国区")).tag(0); Text(L("国际区")).tag(1)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .accessibilityIdentifier("account.region")
            }
            .frame(minHeight: 28)
        } footer: {
            VStack(alignment: .leading, spacing: 4) {
                accountFooterText(L("两区账号和同步数据独立，不互通。"))
                if store.account?.requiresDataRestart == true {
                    accountFooterText(L("区域已切换，请重新打开应用后使用该区域的独立书库和同步。"))
                        .accessibilityIdentifier("account.restart")
                }
            }
        }
    }

    /// Form actions stay in the scrolling list so they do not jump above the
    /// keyboard or hover over the fields while the page moves.
    @ViewBuilder private var actionSection: some View {
        Section {
            if resetMode {
                Button { submitReset() } label: {
                    centeredActionLabel(L("重置密码"), tint: Color(uiColor: .systemBlue))
                }.accessibilityIdentifier("account.reset.submit")
            } else {
                let disabled = register && !registrationAvailable
                Button { signIn() } label: {
                    centeredActionLabel(register ? (registrationAvailable ? L("注册并登录") : L("注册暂未上线")) : L("登录"),
                                        tint: disabled ? NativeTheme.muted : Color(uiColor: .systemBlue))
                }
                .disabled(disabled)
                .accessibilityIdentifier("account.submit")
            }
        } footer: {
            VStack(alignment: .leading, spacing: 6) {
                if resetMode || !register {
                    Button {
                        if resetMode { cancelReset() } else { beginReset() }
                    } label: {
                        Text(resetMode ? L("返回登录") : L("忘记密码？"))
                            .font(NativeTheme.font(12.5, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.secondary)
                            .padding(.vertical, 2)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier(resetMode ? "account.reset.cancel" : "account.forgot")
                }
                if loading || busy {
                    ProgressView().controlSize(.small)
                }
                if !status.isEmpty {
                    accountFooterText(status)
                        .accessibilityIdentifier("account.status")
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func accountField(_ title: String, text: Binding<String>, secure: Bool = false, identifier: String) -> some View {
        Group {
            if secure { SecureField(title, text: text) }
            else { TextField(title, text: text) }
        }
        .font(NativeTheme.font())
        .frame(minHeight: 26)
        .accessibilityLabel(title)
        .accessibilityIdentifier(identifier)
    }

    private func centeredActionLabel(_ title: String, tint: Color, weight: Font.Weight = .semibold) -> some View {
        HStack {
            Spacer(minLength: 0)
            Text(title).font(NativeTheme.font().weight(weight)).foregroundStyle(tint)
            Spacer(minLength: 0)
        }
        .frame(minHeight: 26)
        .contentShape(Rectangle())
    }

    private func accountFooterText(_ text: String) -> some View {
        Text(text)
            .font(NativeTheme.font(12, relativeTo: .caption))
            .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
    }

    /// The registered email can only be verified while signed in; recovery and
    /// verification share the same 8-digit, 15-minute code.
    @ViewBuilder private var emailVerificationSection: some View {
        Section {
            if let user = store.account?.user {
                if user.emailVerified == true {
                    Text(L("注册邮箱已验证。")).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("account.email.verified")
                } else if user.email == nil {
                    Text(L("此账号未设置注册邮箱，无法验证或找回密码。"))
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("account.email.missing")
                } else if emailCodeSent {
                    accountField(L("邮箱验证码（8 位）"), text: $emailCode, identifier: "account.email.code")
                        .textContentType(.oneTimeCode)
                    Button { verifyEmail() } label: {
                        centeredActionLabel(L("确认验证"), tint: Color(uiColor: .systemBlue))
                    }.accessibilityIdentifier("account.email.verify")
                } else {
                    Button { sendEmailCode() } label: {
                        HStack { Text(L("发送验证码")).foregroundStyle(Color(uiColor: .systemBlue)); Spacer() }
                            .contentShape(Rectangle())
                    }.accessibilityIdentifier("account.email.send")
                }
            }
        } header: {
            Text(L("邮箱验证"))
        } footer: {
            if let user = store.account?.user, user.emailVerified != true, user.email != nil {
                Text(emailCodeSent ? L("验证码已发送至注册邮箱（15 分钟内有效）。") : L("注册邮箱尚未验证。验证后可找回密码。"))
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
            }
        }
    }

    private func beginReset() {
        guard !busy, !register else { return }
        let identifier = username.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !identifier.isEmpty else { status = L("请输入有效的用户名或邮箱。"); return }
        busy = true; status = L("正在发送验证码…")
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.reset.request",
                    ["serverUrl": serverUrl, "username": identifier, "region": store.account?.region ?? 0])
                guard !Task.isCancelled else { return }
                resetMode = true; resetCode = ""; resetPassword = ""; resetConfirmation = ""
                status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func submitReset() {
        guard !busy else { return }
        if resetPassword != resetConfirmation { status = L("两次输入的密码不一致。"); return }
        busy = true; status = L("正在重置密码…")
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.reset.confirm",
                    ["serverUrl": serverUrl, "username": username, "code": resetCode,
                     "newPassword": resetPassword, "confirmPassword": resetConfirmation])
                guard !Task.isCancelled else { return }
                resetMode = false; resetCode = ""; resetPassword = ""; resetConfirmation = ""
                password = ""; confirmation = ""; status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func cancelReset() {
        resetMode = false; resetCode = ""; resetPassword = ""; resetConfirmation = ""
        status = L("已返回登录。")
    }

    private func sendEmailCode() {
        guard !busy else { return }
        busy = true; status = L("正在发送邮箱验证码…")
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.email.request")
                guard !Task.isCancelled else { return }
                emailCodeSent = true; emailCode = ""; status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func verifyEmail() {
        guard !busy else { return }
        busy = true; status = L("正在验证邮箱…")
        actionTask = Task {
            defer { busy = false }
            do {
                let account: AccountStatus = try await store.request("settings.account.email.verify", ["code": emailCode])
                guard !Task.isCancelled else { return }
                store.account = account; emailCodeSent = false; emailCode = ""; status = L("邮箱验证成功。")
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func load() async {
        defer { loading = false }
        do {
            let account: AccountStatus = try await store.request("settings.account.get", ["validateSession": true])
            guard !Task.isCancelled else { return }
            store.account = account; serverUrl = account.serverUrl
        } catch {
            guard !Task.isCancelled else { return }
            if let account: AccountStatus = try? await store.request("settings.account.get") {
                store.account = account; serverUrl = account.serverUrl
            }
            status = error.localizedDescription
        }
    }

    private func signIn() {
        guard !busy else { return }
        if register && !registrationAvailable { status = L("注册暂未上线。当前版本无需注册即可免费使用全部功能。"); return }
        if register && password != confirmation { status = L("两次输入的密码不一致。"); return }
        busy = true; status = register ? L("正在注册…") : L("正在登录…")
        var args: [String: Any] = ["serverUrl": serverUrl, "username": username, "password": password,
                                   "region": store.account?.region ?? 0]
        if register { args["email"] = email; args["confirmPassword"] = confirmation }
        let command = register ? "settings.account.register" : "settings.account.login"
        actionTask = Task {
            defer { busy = false; clearPasswords() }
            do {
                let account: AccountStatus = try await store.request(command, args)
                guard !Task.isCancelled else { return }
                store.account = account; status = ""
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func selectRegion(_ region: Int) {
        guard !loading && !busy && region != store.account?.region else { return }
        busy = true; clearPasswords(); username = ""; email = ""; status = ""
        actionTask = Task {
            defer { busy = false }
            do {
                let account: AccountStatus = try await store.request("settings.account.select", ["region": region])
                guard !Task.isCancelled else { return }
                store.account = account; serverUrl = account.serverUrl
                let validated: AccountStatus = try await store.request("settings.account.get", ["validateSession": true])
                guard !Task.isCancelled else { return }
                store.account = validated
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func signOut() {
        guard !busy else { return }
        busy = true; status = L("正在退出…")
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.logout")
                let account: AccountStatus = try await store.request("settings.account.get")
                guard !Task.isCancelled else { return }
                store.account = account; serverUrl = account.serverUrl; status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    @ViewBuilder private var appleSignInSection: some View {
        Section {
            Button {
                signInWithApple()
            } label: {
                HStack(spacing: 7) {
                    Spacer(minLength: 0)
                    Image(systemName: "apple.logo")
                        .font(.system(size: 15, weight: .semibold))
                    Text(L("通过 Apple 登录"))
                        .font(NativeTheme.font().weight(.semibold))
                    Spacer(minLength: 0)
                }
                .foregroundStyle(NativeTheme.ink)
                .frame(minHeight: 26)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(L("通过 Apple 登录"))
            .accessibilityIdentifier("account.apple.signin")
        } footer: {
            accountFooterText(L("使用 Apple ID 登录或自动创建当前区域账号，也可在登录后与已有账号绑定。"))
        }
    }

    @ViewBuilder private var appleLinkSection: some View {
        Section {
            if let user = store.account?.user {
                if user.appleLinked == true {
                    HStack {
                        Text(L("Apple ID"))
                            .font(NativeTheme.font())
                        Spacer()
                        Text(L("已绑定"))
                            .font(NativeTheme.font(13))
                            .foregroundStyle(NativeTheme.secondary)
                    }
                    .accessibilityIdentifier("account.apple.linked")
                    Button(role: .destructive) {
                        confirmUnbindApple = true
                    } label: {
                        HStack {
                            Text(L("解绑 Apple ID"))
                            Spacer()
                        }
                        .contentShape(Rectangle())
                    }
                    .accessibilityIdentifier("account.apple.unbind")
                } else {
                    Button {
                        bindAppleId()
                    } label: {
                        HStack {
                            Text(L("绑定 Apple ID"))
                                .foregroundStyle(Color(uiColor: .systemBlue))
                            Spacer()
                        }
                        .contentShape(Rectangle())
                    }
                    .accessibilityIdentifier("account.apple.bind")
                }
            }
        } header: {
            Text(L("Apple 登录"))
        } footer: {
            if store.account?.user?.appleLinked == true {
                accountFooterText(L("已绑定 Apple ID，可在支持的设备上直接使用 Apple ID 登录此账号。"))
            } else {
                accountFooterText(L("绑定后可直接使用 Apple ID 登录当前账号。"))
            }
        }
    }

    private func signInWithApple() {
        guard !busy else { return }
        busy = true; status = L("正在通过 Apple 登录…")
        actionTask = Task {
            defer { busy = false; clearPasswords() }
            do {
                guard let credential = try await NativeAppleSignInCoordinator.authorize(in: store.navigationWindow) else {
                    status = ""
                    return
                }
                guard !Task.isCancelled else { return }
                var args = credential.bridgeArguments
                args["serverUrl"] = serverUrl
                args["region"] = store.account?.region ?? 0
                let account: AccountStatus = try await store.request("settings.account.apple.login", args)
                guard !Task.isCancelled else { return }
                store.account = account; status = ""
            } catch {
                if !Task.isCancelled { status = error.localizedDescription }
            }
        }
    }

    private func bindAppleId() {
        guard !busy else { return }
        busy = true; status = L("正在绑定 Apple ID…")
        actionTask = Task {
            defer { busy = false }
            do {
                guard let credential = try await NativeAppleSignInCoordinator.authorize(in: store.navigationWindow) else {
                    status = ""
                    return
                }
                guard !Task.isCancelled else { return }
                let account: AccountStatus = try await store.request(
                    "settings.account.apple.bind",
                    credential.bridgeArguments
                )
                guard !Task.isCancelled else { return }
                store.account = account
                status = L("Apple ID 绑定成功。")
            } catch {
                if !Task.isCancelled { status = error.localizedDescription }
            }
        }
    }

    private func unbindAppleId() {
        guard !busy else { return }
        busy = true; status = L("正在解绑 Apple ID…")
        actionTask = Task {
            defer { busy = false }
            do {
                let account: AccountStatus = try await store.request("settings.account.apple.unbind")
                guard !Task.isCancelled else { return }
                store.account = account
                status = L("已解绑 Apple ID。")
            } catch {
                if !Task.isCancelled { status = error.localizedDescription }
            }
        }
    }

    @ViewBuilder private var closeAccountSection: some View {
        Section {
            Button(role: .destructive) {
                closePassword = ""
                closeConfirmation = ""
                closeError = ""
                showingCloseAccount = true
            } label: {
                HStack {
                    Text(L("注销账号"))
                    Spacer()
                }
                .contentShape(Rectangle())
            }
            .accessibilityIdentifier("account.close.open")
        } header: {
            Text(L("账号安全"))
        } footer: {
            accountFooterText(L("注销后将停用当前账号并下线所有已登录设备，此操作不可撤销；本机已保存的书籍不会被删除。"))
        }
    }

    private var closeConfirmationMatches: Bool {
        let trimmed = closeConfirmation.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed == L("停用账号")
    }

    private var canSubmitCloseAccount: Bool {
        !busy && closePassword.count >= 8 && closeConfirmationMatches
    }

    private var closeAccountSheet: some View {
        NavigationView {
            NativeForm {
                Section {
                    Text(L("注销后将停用此账号，所有已登录设备会立即退出且无法恢复。本机书库中的本地书籍会保留。"))
                        .font(NativeTheme.font(13))
                        .foregroundStyle(NativeTheme.secondary)
                }
                Section {
                    accountField(L("当前登录密码"), text: $closePassword, secure: true,
                                 identifier: "account.close.password")
                        .textContentType(.password)
                    accountField(L("输入“停用账号”确认"), text: $closeConfirmation,
                                 identifier: "account.close.confirmation")
                } header: {
                    Text(L("安全验证"))
                } footer: {
                    VStack(alignment: .leading, spacing: 6) {
                        accountFooterText(L("请输入当前密码并输入“停用账号”以确认注销。"))
                        if !closeError.isEmpty {
                            Text(closeError)
                                .font(NativeTheme.font(12, relativeTo: .caption))
                                .foregroundStyle(Color(uiColor: .systemRed))
                                .accessibilityIdentifier("account.close.error")
                        }
                    }
                }
                Section {
                    Button(role: .destructive) {
                        submitCloseAccount()
                    } label: {
                        centeredActionLabel(
                            busy ? L("正在注销账号…") : L("确认注销账号"),
                            tint: canSubmitCloseAccount ? Color(uiColor: .systemRed) : NativeTheme.muted
                        )
                    }
                    .disabled(!canSubmitCloseAccount)
                    .accessibilityIdentifier("account.close.confirm")
                }
            }
            .textInputAutocapitalization(.never)
            .disableAutocorrection(true)
            .navigationTitle(L("注销账号"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("取消")) { showingCloseAccount = false }
                        .disabled(busy)
                }
            }
        }
        .navigationViewStyle(.stack)
        .font(NativeTheme.font())
        .foregroundStyle(NativeTheme.ink)
        .tint(NativeTheme.ink)
    }

    private func submitCloseAccount() {
        guard canSubmitCloseAccount else { return }
        busy = true; closeError = ""
        let currentPassword = closePassword
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.close", [
                    "currentPassword": currentPassword,
                    "confirmation": closeConfirmation.trimmingCharacters(in: .whitespacesAndNewlines)
                ])
                let account: AccountStatus = try await store.request("settings.account.get")
                guard !Task.isCancelled else { return }
                closePassword = ""; closeConfirmation = ""; closeError = ""
                showingCloseAccount = false
                store.account = account; serverUrl = account.serverUrl
                status = result.message
            } catch {
                if !Task.isCancelled { closeError = error.localizedDescription }
            }
        }
    }

    private func clearPasswords() { password = ""; confirmation = "" }
}
