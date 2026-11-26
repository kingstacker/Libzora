import SwiftUI
import WidgetKit

struct KkReadingTreeWidgetView: View {
    let entry: KkEntry
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme
    @Environment(\.widgetRenderingMode) private var renderingMode

    private var lang: WidgetLanguage { WidgetLocalization.current }
    private var snapshot: KkSnapshot { entry.snapshot ?? KkSnapshot.sample(language: lang) }
    private var profile: KkReadingTreeProfile {
        KkReadingTreeProfile(snapshot: snapshot, date: entry.date, isPreview: entry.isPlaceholder || !entry.hasData)
    }
    private var isFullColor: Bool { renderingMode == .fullColor }
    private var streakDotColor: Color {
        let active = profile.streakDays > 0 || profile.hasReadToday
        if isFullColor {
            return active ? KkTheme.treeLeafAccent(scheme) : KkTheme.faintInk(scheme)
        }
        return Color.primary.opacity(active ? 0.75 : 0.4)
    }

    var body: some View {
        Group {
            if !entry.hasData {
                KkEmptyState(
                    title: WidgetLocalization.treeTitle(lang),
                    message: WidgetLocalization.treeEmptyMessage(lang),
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
            HStack(alignment: .center, spacing: 0) {
                Text(WidgetLocalization.treeTitle(lang))
                    .kkLabelFont(11, weight: .medium)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.68)
                Spacer(minLength: 6)
                HStack(spacing: 3) {
                    Circle()
                        .fill(streakDotColor)
                        .frame(width: 5, height: 5)
                    Text(WidgetLocalization.shortDays(profile.streakDays, lang))
                        .kkLabelFont(9.5, weight: .medium)
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .monospacedDigit()
                        .lineLimit(1)
                        .minimumScaleFactor(0.72)
                }
                .layoutPriority(1)
            }

            KkReadingTreeCanvas(profile: profile)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.vertical, -3)

            HStack(alignment: .center, spacing: 4) {
                Text(KkFormat.localizedCompactHours(profile.totalSeconds, language: lang))
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
                    let countText = KkFormat.localizedCompactCount(profile.booksFinished, language: lang)
                    Text(WidgetLocalization.treeBooksCount(countText, rawCount: profile.booksFinished, lang))
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
                HStack(alignment: .center, spacing: 0) {
                    Text(WidgetLocalization.treeTitle(lang))
                        .kkLabelFont(11, weight: .medium)
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .lineLimit(1)
                        .minimumScaleFactor(0.68)
                    Spacer(minLength: 6)
                    vitalityBadge
                }

                Spacer(minLength: 4)

                Text(profile.localizedStageTitle(lang))
                    .kkTitleFont(19)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)

                Text(profile.localizedNextHint(lang))
                    .kkBodyFont(10)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.72)
                    .padding(.top, 2)

                KkProgressBar(progress: profile.stageProgress * 100, height: 3)
                    .padding(.top, 6)

                Spacer(minLength: 6)

                Rectangle()
                    .fill(KkTheme.hairline(scheme))
                    .frame(height: 0.5)
                    .accessibilityHidden(true)

                let totalParts = KkFormat.localizedCompactHoursParts(profile.totalSeconds, language: lang)
                HStack(alignment: .top, spacing: 4) {
                    KkStat(
                        value: totalParts.value,
                        label: WidgetLocalization.totalShort(lang),
                        unit: totalParts.unit,
                        alignment: .leading,
                        valueSize: 13.5,
                        labelSize: 9.5,
                        stackSpacing: 2)
                    Spacer(minLength: 4)
                    KkStat(
                        value: KkFormat.localizedCompactCount(profile.booksFinished, language: lang),
                        label: WidgetLocalization.finishedLabel(lang),
                        alignment: .center,
                        valueSize: 13.5,
                        labelSize: 9.5,
                        stackSpacing: 2)
                    Spacer(minLength: 4)
                    KkStat(
                        value: KkFormat.localizedCompactCount(profile.annotationCount, language: lang),
                        label: WidgetLocalization.treeNotes(lang),
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

    private var vitalityBadge: some View {
        HStack(spacing: 3.5) {
            Circle()
                .fill(streakDotColor)
                .frame(width: 5, height: 5)
            Text(WidgetLocalization.streakBadge(profile.streakDays, lang))
                .kkLabelFont(9.5, weight: .medium)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.72)
        }
        .layoutPriority(1)
        .padding(.horizontal, 6)
        .padding(.vertical, 2.5)
        .background(KkTheme.track(scheme), in: Capsule())
    }
}
