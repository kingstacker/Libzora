import SwiftUI
import WidgetKit

struct KkReadingTreeWidgetView: View {
    let entry: KkEntry
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme
    @Environment(\.widgetRenderingMode) private var renderingMode

    private var snapshot: KkSnapshot { entry.snapshot ?? KkSnapshot.sample(isChinese: isChinese) }
    private var profile: KkReadingTreeProfile {
        KkReadingTreeProfile(snapshot: snapshot, date: entry.date, isPreview: entry.isPlaceholder || !entry.hasData)
    }
    private var isFullColor: Bool { renderingMode == .fullColor }
    private var isChinese: Bool { KkFormat.isChinese }

    var body: some View {
        Group {
            if !entry.hasData {
                KkEmptyState(
                    title: isChinese ? "阅读之树" : "Reading Tree",
                    message: isChinese ? "打开 Libzora，种下一棵书树" : "Open Libzora to plant your tree",
                    systemImage: "leaf")
            } else if family == .systemMedium {
                medium
            } else {
                small
            }
        }
        .lzWidgetURL(URL(string: "libzora://dashboard"))
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: 4) {
                Text(isChinese ? "阅读之树" : "Reading Tree")
                    .kkLabelFont(11, weight: .medium)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                Spacer(minLength: 4)
                if profile.streakDays > 0 {
                    HStack(spacing: 3) {
                        Circle()
                            .fill(isFullColor ? KkTheme.treeLeafAccent(scheme) : Color.primary.opacity(0.7))
                            .frame(width: 5, height: 5)
                        Text(isChinese ? "\(profile.streakDays)天" : "\(profile.streakDays)d")
                            .kkLabelFont(9.5, weight: .medium)
                            .foregroundStyle(KkTheme.secondaryInk(scheme))
                            .monospacedDigit()
                            .lineLimit(1)
                    }
                    .fixedSize(horizontal: true, vertical: false)
                } else if profile.hasReadToday {
                    Circle()
                        .fill(isFullColor ? KkTheme.treeLeafAccent(scheme) : Color.primary.opacity(0.7))
                        .frame(width: 5, height: 5)
                }
            }

            KkReadingTreeCanvas(profile: profile)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.vertical, -3)

            HStack(alignment: .center, spacing: 4) {
                Text(KkFormat.compactHours(profile.totalSeconds, isChinese: isChinese))
                    .kkLabelFont(9.5, weight: .medium)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .monospacedDigit()
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: 4)
                HStack(spacing: 3) {
                    Circle()
                        .fill(isFullColor ? KkTheme.treeYellowAccent(scheme) : Color.primary.opacity(0.7))
                        .frame(width: 5, height: 5)
                    let countText = KkFormat.compactCount(profile.booksFinished, isChinese: isChinese)
                    Text(isChinese
                        ? "\(countText) 本"
                        : (profile.booksFinished == 1 ? "1 book" : "\(countText) books"))
                        .kkLabelFont(9.5, weight: .medium)
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .monospacedDigit()
                        .lineLimit(1)
                }
                .fixedSize(horizontal: true, vertical: false)
            }
        }
        .padding(.horizontal, 13)
        .padding(.vertical, 12)
        .accessibilityElement(children: .combine)
    }

    private var medium: some View {
        HStack(alignment: .center, spacing: 10) {
            KkReadingTreeCanvas(profile: profile)
                .frame(width: 134)
                .frame(maxHeight: .infinity)

            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .center, spacing: 6) {
                    Text(isChinese ? "阅读之树" : "Reading Tree")
                        .kkLabelFont(11, weight: .medium)
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    vitalityBadge
                }

                Spacer(minLength: 4)

                Text(profile.stageTitle(isChinese: isChinese))
                    .kkTitleFont(19)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)

                Text(profile.nextHint(isChinese: isChinese))
                    .kkBodyFont(10)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
                    .padding(.top, 2)

                KkProgressBar(progress: profile.stageProgress * 100, height: 3)
                    .padding(.top, 6)

                Spacer(minLength: 6)

                Rectangle()
                    .fill(KkTheme.hairline(scheme))
                    .frame(height: 0.5)
                    .accessibilityHidden(true)

                let totalParts = KkFormat.compactHoursParts(profile.totalSeconds, isChinese: isChinese)
                HStack(alignment: .top, spacing: 0) {
                    KkStat(
                        value: totalParts.value,
                        label: isChinese ? "累计" : "Total",
                        unit: totalParts.unit,
                        alignment: .leading,
                        valueSize: 13.5,
                        labelSize: 9.5,
                        stackSpacing: 2)
                    Spacer(minLength: 4)
                    KkStat(
                        value: KkFormat.compactCount(profile.booksFinished, isChinese: isChinese),
                        label: isChinese ? "已读完" : "Books",
                        alignment: .center,
                        valueSize: 13.5,
                        labelSize: 9.5,
                        stackSpacing: 2)
                    Spacer(minLength: 4)
                    KkStat(
                        value: KkFormat.compactCount(profile.annotationCount, isChinese: isChinese),
                        label: isChinese ? "批注" : "Notes",
                        alignment: .trailing,
                        valueSize: 13.5,
                        labelSize: 9.5,
                        stackSpacing: 2)
                }
                .padding(.top, 8)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        }
        .padding(.leading, 10)
        .padding(.trailing, 14)
        .padding(.vertical, 13)
    }

    @ViewBuilder
    private var vitalityBadge: some View {
        let labelText: String? = if profile.streakDays > 0 {
            isChinese ? "连续 \(profile.streakDays) 天" : "\(profile.streakDays)d streak"
        } else if profile.hasReadToday {
            isChinese ? "今日已读" : "Read today"
        } else {
            nil
        }

        if let labelText {
            HStack(spacing: 4) {
                Circle()
                    .fill(isFullColor ? KkTheme.treeLeafAccent(scheme) : Color.primary.opacity(0.75))
                    .frame(width: 5, height: 5)
                Text(labelText)
                    .kkLabelFont(9.5, weight: .medium)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
            }
            .fixedSize(horizontal: true, vertical: false)
            .padding(.horizontal, 7)
            .padding(.vertical, 3)
            .background(KkTheme.track(scheme), in: Capsule())
        }
    }
}
