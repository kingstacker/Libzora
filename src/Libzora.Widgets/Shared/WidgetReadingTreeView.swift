import SwiftUI
import WidgetKit

struct KkReadingTreeWidgetView: View {
    let entry: KkEntry
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme

    private var lang: WidgetLanguage { WidgetLocalization.current }
    private var snapshot: KkSnapshot { entry.snapshot ?? KkSnapshot.sample(language: lang) }
    private var profile: KkReadingTreeProfile {
        KkReadingTreeProfile(snapshot: snapshot, date: entry.date, isPreview: entry.isPlaceholder || !entry.hasData)
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

    private var heading: some View {
        KkWidgetHeading(
            title: WidgetLocalization.treeTitle(lang),
            detail: WidgetLocalization.streakBadge(profile.streakDays, lang))
    }

    private var small: some View {
        VStack(alignment: .leading, spacing: 0) {
            heading

            KkReadingTreeCanvas(profile: profile)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                // Reuse the canvas's sky/ground margins, keeping the tree's
                // proportions intact while giving it more width and height.
                .padding(.horizontal, -4)
                .padding(.vertical, -4)

            let duration = KkFormat.localizedCompactHours(profile.totalSeconds, language: lang)
            let countText = KkFormat.localizedCompactCount(profile.booksFinished, language: lang)
            let books = WidgetLocalization.treeBooksCount(countText, rawCount: profile.booksFinished, lang)
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(duration)
                    .accessibilityLabel("\(WidgetLocalization.totalTime(lang)) \(duration)")
                Spacer(minLength: 0)
                Text(books)
                    .accessibilityLabel("\(WidgetLocalization.finishedLabel(lang)) \(books)")
            }
            .kkLabelFont(10.5, weight: .medium)
            .foregroundStyle(KkTheme.secondaryInk(scheme))
            .lineLimit(1)
            .minimumScaleFactor(0.85)
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 10)
        .accessibilityElement(children: .combine)
    }

    private var medium: some View {
        GeometryReader { proxy in
            let spacing: CGFloat = proxy.size.width >= 310 ? 12 : 10
            let treeWidth = min(168, max(128, (proxy.size.width - spacing) * 0.48))

            HStack(alignment: .center, spacing: spacing) {
                // Span the full widget height on the left and reuse the canvas's
                // internal sky/ground margins so the 4x2 tree is visibly larger
                // and broader than the 2x2 small widget.
                KkReadingTreeCanvas(profile: profile)
                    .padding(.leading, -6)
                    .padding(.vertical, -6)
                    .frame(width: treeWidth, height: proxy.size.height)

                VStack(alignment: .leading, spacing: 0) {
                    heading

                    Spacer(minLength: 4)

                    VStack(alignment: .leading, spacing: 0) {
                        Text(profile.localizedStageTitle(lang))
                            .kkTitleFont(19)
                            .foregroundStyle(KkTheme.ink(scheme))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)

                        Text(profile.localizedNextHint(lang))
                            .kkBodyFont(10.5)
                            .foregroundStyle(KkTheme.secondaryInk(scheme))
                            .lineLimit(2)
                            .minimumScaleFactor(0.82)
                            .padding(.top, 2)

                        KkProgressBar(progress: profile.stageProgress * 100, height: 3)
                            .padding(.top, 6)
                    }

                    Spacer(minLength: 6)

                    let totalParts = KkFormat.localizedCompactHoursParts(profile.totalSeconds, language: lang)
                    HStack(alignment: .top, spacing: 6) {
                        KkStat(
                            value: totalParts.value,
                            label: WidgetLocalization.totalShort(lang),
                            unit: totalParts.unit,
                            alignment: .leading,
                            valueSize: 15,
                            labelSize: 10,
                            stackSpacing: 2,
                            expand: true,
                            subdued: profile.totalSeconds == 0)
                        KkStat(
                            value: KkFormat.localizedCompactCount(profile.booksFinished, language: lang),
                            label: WidgetLocalization.finishedLabel(lang),
                            alignment: .center,
                            valueSize: 15,
                            labelSize: 10,
                            stackSpacing: 2,
                            expand: true,
                            subdued: profile.booksFinished == 0)
                        KkStat(
                            value: KkFormat.localizedCompactCount(profile.annotationCount, language: lang),
                            label: WidgetLocalization.treeNotes(lang),
                            alignment: .trailing,
                            valueSize: 15,
                            labelSize: 10,
                            stackSpacing: 2,
                            expand: true,
                            subdued: profile.annotationCount == 0)
                    }
                }
                .padding(.vertical, 2)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
    }
}
