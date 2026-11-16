import SwiftUI

struct NativeMembershipSections: View {
    @ObservedObject var store: NativeStore
    var onSignOut: (() -> Void)?
    var signingOut = false
    var accountStatus = ""
    @State private var details: DeviceFeatureStatus?
    @State private var status = ""
    @State private var busy = false
    @State private var task: Task<Void, Never>?
    @State private var kickTarget: AccountDevice?

    var body: some View {
        Group {
            Section {
                NativeAccountHeader(account: store.account, membership: details?.membership,
                                    busy: busy, signingOut: signingOut,
                                    status: status, accountStatus: accountStatus,
                                    refresh: { perform { await refresh() } }, signOut: onSignOut)
            }
            if let member = details?.membership {
                if member.isTrialExpired, let notice = member.trialExpiryMessage {
                    NativeExpiryNotice(text: notice, identifier: "membership.expired")
                } else if member.isTrialExpiringSoon, let notice = member.trialRenewalMessage {
                    NativeExpiryNotice(text: notice, identifier: "membership.expiring")
                }
            }
            if let member = details?.membership, member.canClaimTrial || member.isTrialActive {
                Section("免费试用") {
                    Toggle(isOn: Binding(get: { member.trialClaimed == true },
                                         set: { value in if value { claimTrial() } })) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text("领取 7 天试用订阅")
                                .font(NativeTheme.font(15, relativeTo: .subheadline))
                            Text("体验自备 S3 / WebDAV 多端同步")
                                .font(NativeTheme.font(12, relativeTo: .caption))
                                .foregroundStyle(NativeTheme.secondary)
                        }
                    }
                    .disabled(member.trialClaimed == true || !member.canClaimTrial || busy)
                    .accessibilityIdentifier("membership.trial.toggle")
                    Text(member.trialStatusMessage)
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("membership.trial.status")
                }
            }
            if let details { NativeSyncEntitlements(status: details) }
            Section("已登录设备") {
                HStack(alignment: .center, spacing: 12) {
                    Text(deviceCapacity).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("membership.devices.capacity")
                    Spacer(minLength: 8)
                    VStack(alignment: .trailing, spacing: 5) {
                        Text("\(details?.membership.loggedInDeviceCount ?? 0) / \(details?.membership.deviceLimit ?? 6) 台")
                            .font(NativeTheme.font(15, relativeTo: .subheadline)).monospacedDigit()
                        HStack(spacing: 4) {
                            ForEach(0..<max(0, details?.membership.deviceLimit ?? 6), id: \.self) { index in
                                RoundedRectangle(cornerRadius: 2)
                                    .fill(index < (details?.membership.loggedInDeviceCount ?? 0) ? NativeTheme.success : NativeTheme.strong)
                                    .frame(width: 16, height: 4)
                            }
                        }.accessibilityHidden(true)
                    }
                }
                ForEach(activeDevices) { device in
                    NativeDeviceRow(device: device, current: device.deviceId == details?.currentDeviceId) {
                        kickTarget = device
                    }
                }
            }
            NativeMembershipPlans(membership: details?.membership, account: store.account,
                                  claimTrial: { claimTrial() }) { perform { await refresh() } }
        }.disabled(busy)
            .task(id: store.account?.user?.id) { busy = true; defer { busy = false }; await refresh() }
            .confirmationDialog("踢出设备？", isPresented: Binding(get: { kickTarget != nil }, set: { if !$0 { kickTarget = nil } }), titleVisibility: .visible) {
                if let device = kickTarget {
                    Button("踢出 \(device.name)", role: .destructive) { kick(device) }
                }
                Button("取消", role: .cancel) { kickTarget = nil }
            } message: { Text("此设备会退出登录并释放名额。") }
            .onDisappear { task?.cancel() }
    }

    private var deviceCapacity: String {
        guard let member = details?.membership else { return "" }
        let remaining = max(0, member.deviceLimit - member.loggedInDeviceCount)
        return remaining > 0 ? "还可登录 \(remaining) 台设备" : "设备名额已满，可退出旧设备后登录新设备。"
    }

    /// The desktop lists the current device first, then the most recently seen.
    private var activeDevices: [AccountDevice] {
        (details?.devices ?? []).filter { $0.state == "active" }.sorted {
            let left = $0.deviceId == details?.currentDeviceId
            let right = $1.deviceId == details?.currentDeviceId
            if left != right { return left }
            return $0.lastSeenAt > $1.lastSeenAt
        }
    }

    private func refresh() async {
        do {
            let value: DeviceFeatureStatus = try await store.request("settings.membership.get")
            guard !Task.isCancelled else { return }
            details = value; status = ""
        } catch { if !Task.isCancelled { status = error.localizedDescription } }
    }

    private func perform(_ operation: @escaping () async throws -> Void) {
        guard !busy else { return }
        busy = true
        task = Task {
            defer { busy = false }
            do { try await operation() }
            catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func claimTrial() {
        perform {
            let _: DeviceFeatureStatus = try await store.request("settings.membership.trialClaim")
            await refresh()
            status = "7 天试用已开通，可使用自备 S3 / WebDAV 同步书籍、阅读进度、书签和批注。"
        }
    }
    private func kick(_ device: AccountDevice) {
        kickTarget = nil
        perform {
            let _: MessageResult = try await store.request("settings.membership.kick", ["deviceId": device.deviceId])
            let account: AccountStatus = try await store.request("settings.account.get")
            store.account = account
            if account.isLoggedIn { await refresh() }
        }
    }
}

/// Feature tiers match the desktop. Rights are bound to the account; this view only refreshes them.
struct NativeMembershipPlans: View {
    static let anchor = "membership.plans"
    let membership: AccountMembership?
    let account: AccountStatus?
    let claimTrial: () -> Void
    let refreshRights: () -> Void
    @State private var selection = 2

    private var signedIn: Bool { account?.isLoggedIn == true }
    private var plan: String { membership?.plan ?? "" }
    private var hasLifetime: Bool { membership?.tier == "lifetime" }
    private var regionName: String { account?.regionName ?? "中国区" }
    private var hint: String {
        signedIn ? "买断权益直接绑定账号，开通后刷新即可使用。服务端仅验证功能权益，不处理支付或订单。"
            : "登录后可领取试用或刷新账号的买断权益。"
    }

    var body: some View {
        Section {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    Text("功能方案").font(NativeTheme.font(17, relativeTo: .headline).weight(.semibold))
                    Text(regionName)
                        .font(NativeTheme.font(11, relativeTo: .caption2))
                        .foregroundStyle(NativeTheme.secondary)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(NativeTheme.strong, in: RoundedRectangle(cornerRadius: 6))
                        .accessibilityIdentifier("membership.plans.region")
                }
                Text("Free 免费本地阅读；试用与买断解锁相同的自备存储同步功能。")
                    .font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }.id(Self.anchor)
            Group {
                Text("买断订阅为一次性购买，无自动续费。购买与退款适用购买渠道规则及当地法律。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("membership.no-refund-policy")
                Picker("会员方案", selection: $selection) {
                    Text("Free").tag(0)
                    Text("试用订阅").tag(1)
                    Text("买断订阅").tag(2)
                }.pickerStyle(.segmented)
                    .accessibilityIdentifier("membership.plans.tabs")
                planCard(selection)
                    .id(selection)
                    .simultaneousGesture(
                        DragGesture(minimumDistance: 24).onEnded { value in
                            guard abs(value.translation.width) > abs(value.translation.height) else { return }
                            if value.translation.width < -40 { select(selection + 1) }
                            else if value.translation.width > 40 { select(selection - 1) }
                        }
                    )
                    .accessibilityHint("左右轻扫可切换会员方案")
                Text(hint).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("membership.plans.hint")
            }
        }.onChange(of: membership?.plan) { newPlan in
            if newPlan == "trial" { select(1) }
            else if newPlan != nil { select(2) }
        }
    }

    private func select(_ index: Int) {
        selection = min(2, max(0, index))
    }

    @ViewBuilder private func planCard(_ index: Int) -> some View {
        switch index {
        case 0:
            NativePlanCard(name: "Free", tagline: "让阅读自由开始", price: "¥0", period: "永久免费",
                           accent: NativeTheme.planFree, isCurrent: plan == "free",
                           benefits: ["本地书库管理与阅读", "USB Kindle 书籍与批注双向同步", "插件功能", "自定义 AI 接口", "加密备份"],
                           footer: "完整本地体验永久免费，不含自备存储多端同步。") {
                Button(plan == "free" ? "当前方案" : "永久免费") {}
                    .buttonStyle(NativePlanButtonStyle(accent: NativeTheme.planFree, filled: false)).disabled(true)
            }
        case 1:
            NativePlanCard(name: "试用订阅", tagline: "完整体验买断功能", price: "¥0", period: "每账号主动领取一次，连续 7 天",
                           accent: NativeTheme.planTrial, isCurrent: plan == "trial",
                           benefits: ["包含全部 Free 权益", "自备 S3 / WebDAV 多端同步", "书籍、进度、书签与批注同步", "最多 6 台设备登录"],
                           footer: "到期只停用同步，不删除本地或自备存储数据。所有方案均无官方云同步。") {
                Button(plan == "trial" ? "试用中" : membership?.trialClaimed == true ? "已领取过试用" : "领取 7 天试用") { claimTrial() }
                    .buttonStyle(NativePlanButtonStyle(accent: NativeTheme.planTrial, filled: false))
                    .disabled(!signedIn || membership?.canClaimTrial != true)
                    .accessibilityIdentifier("membership.trial.claim")
            }
        default:
            NativePlanCard(name: "买断订阅", tagline: "你的书库，由你掌控", price: "¥88", period: "一次购买，永久使用",
                           badge: "推荐", accent: NativeTheme.planLifetime, isCurrent: plan == "lifetime",
                           benefits: ["包含全部 Free 权益", "自备 S3 / WebDAV 多端同步", "书籍、进度、书签与批注同步", "最多 6 台设备登录"],
                           footer: "需自行配置 S3 / WebDAV；存储与流量费用自行承担。试用与买断同步功能相同。") {
                Button(hasLifetime ? "已永久买断" : "刷新权益") { refreshRights() }
                    .buttonStyle(NativePlanButtonStyle(accent: NativeTheme.planLifetime, filled: true))
                    .disabled(!signedIn || hasLifetime)
                    .accessibilityIdentifier("membership.rights.refresh")
            }
        }
    }
}

struct NativePlanBadge: View {
    let text: String
    let accent: Color
    var body: some View {
        Text(text).font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(accent)
            .padding(.horizontal, 8).padding(.vertical, 3).background(accent.opacity(0.12), in: Capsule())
    }
}

private struct NativePlanButtonStyle: ButtonStyle {
    let accent: Color
    let filled: Bool
    @Environment(\.isEnabled) private var enabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(NativeTheme.font())
            .foregroundStyle(!enabled ? NativeTheme.muted : filled ? NativeTheme.page : accent)
            .frame(maxWidth: .infinity).padding(.vertical, 12)
            .background(!enabled ? NativeTheme.sunk : filled ? accent : accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
            .opacity(configuration.isPressed ? 0.75 : 1)
    }
}

private struct NativePlanCard<Action: View>: View {
    let name: String
    let tagline: String
    /// nil means the price needs a signed-in session.
    let price: String?
    var suffix = ""
    let period: String
    var badge = ""
    let accent: Color
    let isCurrent: Bool
    let benefits: [String]
    let footer: String
    @ViewBuilder let action: Action

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text(name).font(NativeTheme.font(19, relativeTo: .title3)).foregroundStyle(accent)
                    if !badge.isEmpty { NativePlanBadge(text: badge, accent: accent) }
                }
                caption(tagline)
            }
            VStack(alignment: .leading, spacing: 2) {
                if let price {
                    HStack(alignment: .firstTextBaseline, spacing: 3) {
                        Text(price).font(NativeTheme.font(28, relativeTo: .title))
                        if !suffix.isEmpty { caption(suffix) }
                    }
                } else {
                    Text("登录后查看价格").font(NativeTheme.font(15)).foregroundStyle(NativeTheme.muted)
                }
                caption(period)
            }
            NativeTheme.divider.frame(height: 1)
            VStack(alignment: .leading, spacing: 8) {
                ForEach(benefits, id: \.self) { benefit in
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "checkmark").font(.system(size: 11, weight: .semibold)).foregroundStyle(accent).padding(.top, 4)
                        Text(benefit).font(NativeTheme.font(14))
                    }
                }
            }
            NativeTheme.divider.frame(height: 1)
            caption(footer)
            action
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(NativeTheme.surface, in: RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(isCurrent ? accent : NativeTheme.strong, lineWidth: isCurrent ? 1.5 : 1))
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
    }
}
