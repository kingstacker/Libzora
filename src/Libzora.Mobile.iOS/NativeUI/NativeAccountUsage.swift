import SwiftUI

/// The account summary shares the same Free / Trial / Lifetime feature state.
struct NativeAccountHeader: View {
    @Environment(\.locale) private var uiLocale
    let account: AccountStatus?
    let membership: AccountMembership?
    let busy: Bool
    let signingOut: Bool
    let status: String
    let accountStatus: String
    let refresh: () -> Void
    let signOut: (() -> Void)?
    let syncEntitlementsEnforced: Bool

    private var plan: String { membership?.plan ?? "free" }
    private var accent: Color { NativeTheme.planAccent(plan) }
    private var summary: String {
        if !syncEntitlementsEnforced { return L("S3 / WebDAV 多端同步免费使用，无需注册。") }
        guard let membership else { return L("正在读取会员信息…") }
        switch membership.plan {
        case "trial": return L("试用中 · 自备存储同步已解锁")
        case "lifetime": return L("永久买断 · 自备存储同步已解锁")
        default: return L("Free · 畅享本地阅读")
        }
    }
    private var symbol: String {
        switch plan {
        case "trial": return "clock"
        case "lifetime": return "person.crop.circle.badge.checkmark"
        default: return "person.crop.circle"
        }
    }

    var body: some View {
        let _ = uiLocale

        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 14) {
                ZStack {
                    Circle().fill(accent.opacity(0.12))
                    Image(systemName: symbol).font(.system(size: 21, weight: .medium))
                        .foregroundStyle(accent)
                }.frame(width: 44, height: 44).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 5) {
                    Text(account?.user?.username ?? "").font(NativeTheme.font(20, relativeTo: .title3).weight(.semibold))
                        .lineLimit(1).minimumScaleFactor(0.75).truncationMode(.tail)
                        .accessibilityIdentifier("account.username")
                    HStack(spacing: 6) {
                        Circle().fill(NativeTheme.success).frame(width: 6, height: 6)
                        Text(L("已登录")).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.success)
                        Text("·").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                        Text(account?.regionName ?? "").font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.muted)
                    }
                    Text(summary).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                        .lineLimit(2).accessibilityIdentifier("membership.sync")
                }
                Spacer(minLength: 8)
                VStack(alignment: .trailing, spacing: 6) {
                    if let membership {
                        Text(membership.planName)
                            .font(NativeTheme.font(12, relativeTo: .caption).weight(.semibold))
                            .foregroundStyle(accent)
                            .padding(.horizontal, 8).padding(.vertical, 3)
                            .background(accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 6))
                            .accessibilityIdentifier("membership.tier")
                        Text(membership.planExpiryText).font(NativeTheme.font(11, relativeTo: .caption2))
                            .foregroundStyle(NativeTheme.muted).multilineTextAlignment(.trailing)
                    } else {
                        ProgressView().controlSize(.small)
                    }
                }
            }
            NativeTheme.divider.frame(height: 1)
            HStack(spacing: 14) {
                Button {
                    refresh()
                } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "arrow.clockwise").font(.system(size: 12, weight: .medium))
                        Text(L("刷新"))
                    }.font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                }.buttonStyle(.plain).disabled(busy || signingOut)
                    .accessibilityIdentifier("membership.refresh")
                if busy || signingOut { ProgressView().controlSize(.small) }
                Spacer(minLength: 0)
                if let signOut {
                    Button(L("退出登录")) { signOut() }.buttonStyle(.plain)
                        .font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                        .disabled(busy || signingOut)
                        .accessibilityIdentifier("account.logout")
                }
            }
            if !status.isEmpty {
                Text(status).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("membership.status")
            }
            if !accountStatus.isEmpty {
                Text(accountStatus).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("account.status")
            }
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
}

struct NativeExpiryNotice: View {
    let text: String
    var identifier = "membership.expired"
    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "clock.badge.exclamationmark").font(.system(size: 16, weight: .medium))
                .foregroundStyle(NativeTheme.warning).padding(.top, 2).accessibilityHidden(true)
            Text(text).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(NativeTheme.warning.opacity(0.10), in: RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(NativeTheme.warning.opacity(0.25), lineWidth: 1))
            .accessibilityIdentifier(identifier)
    }
}

/// One device row: platform avatar, name, current-device badge and sign-out action.
struct NativeDeviceRow: View {
    @Environment(\.locale) private var uiLocale
    let device: AccountDevice
    let current: Bool
    let onKick: () -> Void

    private var symbol: String {
        switch device.platform.lowercased() {
        case "ios", "android": return "iphone"
        case "ipados", "ipad": return "ipad"
        default: return "desktopcomputer"
        }
    }
    private var activity: String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var seen = formatter.date(from: device.lastSeenAt)
        if seen == nil {
            formatter.formatOptions = [.withInternetDateTime]
            seen = formatter.date(from: device.lastSeenAt)
        }
        guard let seen else { return device.lastSeenAt }
        let elapsed = max(0, Date().timeIntervalSince(seen))
        if elapsed < 60 { return L("刚刚") }
        if elapsed < 3600 { return L("{0} 分钟前", Int(elapsed / 60)) }
        if elapsed < 86_400 { return L("{0} 小时前", Int(elapsed / 3600)) }
        if elapsed < 604_800 { return L("{0} 天前", Int(elapsed / 86_400)) }
        let display = DateFormatter()
        display.dateFormat = "MM/dd HH:mm"
        return display.string(from: seen)
    }

    var body: some View {
        let _ = uiLocale

        HStack(alignment: .center, spacing: 12) {
            Image(systemName: symbol).font(.system(size: 16, weight: .medium))
                .foregroundStyle(current ? NativeTheme.success : NativeTheme.secondary)
                .frame(width: 36, height: 36)
                .background(current ? NativeTheme.success.opacity(0.12) : NativeTheme.strong,
                            in: RoundedRectangle(cornerRadius: 10))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(device.name).font(NativeTheme.font(13, relativeTo: .subheadline)).lineLimit(1)
                    .truncationMode(.tail)
                    .accessibilityIdentifier("membership.device.\(device.deviceId)")
                    .accessibilityLabel(device.name + (current ? L("（当前设备）") : ""))
                Text(L("{0} · 最近活跃 {1}", device.platform, activity))
                    .font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.secondary)
            }
            Spacer(minLength: 8)
            if current {
                Text(L("当前设备")).font(NativeTheme.font(10, relativeTo: .caption2)).foregroundStyle(NativeTheme.success)
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(NativeTheme.success.opacity(0.12), in: RoundedRectangle(cornerRadius: 4))
            }
            Button(current ? L("退出此设备") : L("退出设备")) { onKick() }.buttonStyle(.plain)
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                .accessibilityIdentifier("membership.kick.\(device.deviceId)")
        }.padding(.vertical, 4)
    }
}

struct NativeSyncEntitlements: View {
    @Environment(\.locale) private var uiLocale
    let status: DeviceFeatureStatus
    let enforced: Bool
    var body: some View {
        let _ = uiLocale

        Section(L("同步功能")) {
            Text(!enforced ? L("S3 / WebDAV 多端同步免费使用，无需注册。")
                 : status.canUseFeatures ? L("自备 S3 / WebDAV 多端同步已解锁")
                 : L("领取试用或购买买断订阅，即可使用自备 S3 / WebDAV 多端同步。"))
                .font(NativeTheme.font(13)).accessibilityIdentifier("membership.sync.features")
            Text(L("所有方案均不提供官方云存储或官方云同步；存储与流量费用由用户承担。"))
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                .accessibilityIdentifier("membership.no-official-cloud")
        }
    }
}
