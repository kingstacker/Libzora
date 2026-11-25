import SwiftUI
import WidgetKit

struct KkEntry: TimelineEntry {
    let date: Date
    let snapshot: KkSnapshot?
    let hasData: Bool
    let isPlaceholder: Bool

    init(date: Date, snapshot: KkSnapshot?, hasData: Bool, isPlaceholder: Bool = false) {
        self.date = date
        self.snapshot = snapshot
        self.hasData = hasData
        self.isPlaceholder = isPlaceholder
    }

    static var placeholder: KkEntry {
        KkEntry(
            date: Date(),
            snapshot: .sample,
            hasData: true,
            isPlaceholder: true)
    }
}

struct KkContinueWidgetView: View {
    let entry: KkEntry
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme

    private var lang: WidgetLanguage { WidgetLocalization.current }
    private var book: KkBook? { entry.snapshot?.continueReading }
    /// In-progress books, newest reading first. Newer snapshots list several;
    /// older ones fall back to the single continueReading book.
    private var books: [KkBook] {
        if let listed = entry.snapshot?.continueBooks, !listed.isEmpty { return listed }
        return book.map { [$0] } ?? []
    }
    private var url: URL? {
        guard !entry.isPlaceholder else { return nil }
        if family == .systemMedium && books.count > 1 { return nil }
        let displayedBook = family == .systemSmall || family == .systemMedium ? books.first : book
        guard let id = displayedBook?.id else { return nil }
        return URL(string: "libzora://read/\(id)")
    }

    var body: some View {
        Group {
            #if os(iOS)
            switch family {
            case .accessoryRectangular:
                accessoryRectangular
            case .accessoryInline:
                Label(
                    book.map { WidgetLocalization.continueBook($0.title, lang) }
                        ?? WidgetLocalization.openToReadShort(lang),
                    systemImage: "book")
                    .kkBodyFont(12)
            default:
                homeContent
            }
            #else
            homeContent
            #endif
        }
        .lzWidgetURL(url)
    }

    @ViewBuilder
    private var homeContent: some View {
        if !entry.hasData {
            KkEmptyState(
                title: WidgetLocalization.continueTitle(lang),
                message: WidgetLocalization.continueEmptyMessage(lang))
        } else if !books.isEmpty {
            if family == .systemMedium {
                medium(books)
            } else {
                small(books[0])
            }
        } else {
            KkEmptyState(
                title: WidgetLocalization.emptyShelfTitle(lang),
                message: WidgetLocalization.emptyShelfMessage(lang))
        }
    }

    private func small(_ book: KkBook) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            heading
            Spacer(minLength: 6)
            HStack(alignment: .center, spacing: 10) {
                KkCover(book: book, width: 56, height: 80, blankPlaceholder: entry.isPlaceholder)
                VStack(alignment: .leading, spacing: 5) {
                    Text(book.title)
                        .kkTitleFont(13)
                        .foregroundStyle(KkTheme.ink(scheme))
                        .lineLimit(3)
                        .minimumScaleFactor(0.85)
                    authors(book, size: 10)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 6)
            progress(book, compact: true)
        }
        .padding(14)
    }

    /// The medium widget arranges up to four actively-read books horizontally so
    /// tapping any cover opens that exact book.
    @ViewBuilder
    private func medium(_ books: [KkBook]) -> some View {
        if books.count == 1, let only = books.first {
            mediumBook(only)
        } else {
            let items = Array(books.prefix(4))
            GeometryReader { geometry in
                let horizontalPadding: CGFloat = 14
                let verticalPadding: CGFloat = 12
                let spacing: CGFloat = 10
                let availableWidth = max(120, geometry.size.width - horizontalPadding * 2)
                let slotWidth = (availableWidth - spacing * 3) / 4
                let availableHeight = max(80, geometry.size.height - verticalPadding * 2)
                let maxCoverHeight = max(44, availableHeight - 44)
                let coverHeight = min(maxCoverHeight, floor(slotWidth / 0.70))
                let coverWidth = floor(coverHeight * 0.70)

                VStack(alignment: .leading, spacing: 0) {
                    heading
                    Spacer(minLength: 6)
                    HStack(alignment: .top, spacing: spacing) {
                        ForEach(items) { item in
                            bookColumn(item, slotWidth: slotWidth, coverWidth: coverWidth,
                                       coverHeight: coverHeight, slotHeight: maxCoverHeight + 40)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, horizontalPadding)
                .padding(.vertical, verticalPadding)
            }
        }
    }

    private func mediumBook(_ book: KkBook) -> some View {
        HStack(alignment: .center, spacing: 18) {
            KkCover(book: book, width: 86, height: 124, blankPlaceholder: entry.isPlaceholder)
            VStack(alignment: .leading, spacing: 0) {
                heading
                Text(book.title)
                    .kkTitleFont(20)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(2)
                    .minimumScaleFactor(0.82)
                    .padding(.top, 8)
                authors(book, size: 11)
                    .padding(.top, 5)
                Spacer(minLength: 8)
                progress(book, compact: false)
            }
            .frame(maxWidth: .infinity, maxHeight: 124, alignment: .leading)
        }
        .padding(18)
    }

    @ViewBuilder
    private func bookColumn(_ book: KkBook, slotWidth: CGFloat, coverWidth: CGFloat,
                            coverHeight: CGFloat, slotHeight: CGFloat) -> some View {
        if !entry.isPlaceholder, let destination = URL(string: "libzora://read/\(book.id)") {
            Link(destination: destination) {
                bookColumnContent(book, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
                    .frame(minHeight: slotHeight, alignment: .top)
                    .contentShape(Rectangle())
            }
        } else {
            bookColumnContent(book, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
        }
    }

    private func bookColumnContent(_ book: KkBook, slotWidth: CGFloat, coverWidth: CGFloat, coverHeight: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            KkCover(book: book, width: coverWidth, height: coverHeight, blankPlaceholder: entry.isPlaceholder)
                .frame(maxWidth: .infinity, alignment: .center)
            Text(book.title)
                .kkTitleFont(10.5)
                .foregroundStyle(KkTheme.ink(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.85)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(WidgetLocalization.readPercent(KkFormat.percent(book.progress), lang))
                .kkLabelFont(9, weight: .medium)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.72)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(width: slotWidth, alignment: .top)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    private var heading: some View {
        Text(WidgetLocalization.continueTitle(lang))
            .kkLabelFont(11, weight: .medium)
            .foregroundStyle(KkTheme.secondaryInk(scheme))
            .lineLimit(1)
            .minimumScaleFactor(0.8)
    }

    @ViewBuilder
    private func authors(_ book: KkBook, size: CGFloat) -> some View {
        if !book.authors.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            Text(book.authors)
                .kkBodyFont(size)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
        }
    }

    private func progress(_ book: KkBook, compact: Bool) -> some View {
        HStack(alignment: .center, spacing: 4) {
            Text(WidgetLocalization.readLabel(lang))
                .kkLabelFont(10, weight: .regular)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            Text(KkFormat.percent(book.progress))
                .kkLabelFont(11, weight: .semibold)
                .foregroundStyle(KkTheme.ink(scheme))
                .monospacedDigit()
            Spacer(minLength: 4)
            Image(systemName: "arrow.right")
                .font(.system(size: compact ? 10 : 11, weight: .medium))
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .accessibilityHidden(true)
        }
        .accessibilityElement(children: .combine)
    }

    #if os(iOS)
    private var accessoryRectangular: some View {
        VStack(alignment: .leading, spacing: 3) {
            Label(WidgetLocalization.continueShort(lang), systemImage: "book")
                .kkLabelFont(11)
            Text(book?.title ?? WidgetLocalization.continueEmptyMessage(lang))
                .kkTitleFont(12)
                .lineLimit(1)
            if let book {
                Text(WidgetLocalization.readPercent(KkFormat.percent(book.progress), lang))
                    .kkBodyFont(10)
                    .opacity(0.72)
                    .lineLimit(1)
            }
        }
    }
    #endif
}

struct KkStatsWidgetView: View {
    let entry: KkEntry
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme

    private var lang: WidgetLanguage { WidgetLocalization.current }
    private var snapshot: KkSnapshot { entry.snapshot ?? KkSnapshot.sample }
    private var reading: KkReading { snapshot.reading }
    private var isLiveSnapshot: Bool { entry.hasData && !entry.isPlaceholder }
    private var effectiveTodaySeconds: Int64 {
        isLiveSnapshot ? KkFormat.effectiveTodaySeconds(reading: reading, generatedAt: snapshot.generatedAt, on: entry.date) : reading.todaySeconds
    }
    private var effectiveWeekSeconds: Int64 {
        isLiveSnapshot ? KkFormat.effectiveWeekSeconds(reading: reading, generatedAt: snapshot.generatedAt, on: entry.date) : reading.weekSeconds
    }
    private var effectiveStreakDays: Int {
        isLiveSnapshot ? KkFormat.effectiveStreakDays(reading: reading, generatedAt: snapshot.generatedAt, on: entry.date) : reading.streakDays
    }

    var body: some View {
        Group {
            if !entry.hasData {
                KkEmptyState(
                    title: WidgetLocalization.statsTitle(lang),
                    message: WidgetLocalization.statsEmptyMessage(lang),
                    systemImage: "chart.bar")
            } else if family == .systemLarge {
                large
            } else {
                medium
            }
        }
        .lzWidgetURL(URL(string: "libzora://dashboard"))
    }

    private var medium: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text(WidgetLocalization.statsTitle(lang))
                    .kkTitleFont(14)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Spacer(minLength: 6)
                streak
            }
            Spacer(minLength: 8)
            HStack(alignment: .bottom, spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    today(size: 36)
                    Text(WidgetLocalization.weekDuration(KkFormat.duration(effectiveWeekSeconds), lang))
                        .kkLabelFont(9.5, weight: .regular)
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .lineLimit(1)
                        .minimumScaleFactor(0.68)
                }
                .frame(minWidth: 84, maxWidth: 108, alignment: .leading)
                KkWeekBars(days: reading.days, referenceDate: isLiveSnapshot ? entry.date : nil, maxHeight: 48, barWidth: 10)
                    .frame(maxWidth: .infinity)
            }
            Spacer(minLength: 8)
            divider
            HStack(spacing: 4) {
                KkStat(value: KkFormat.localizedCompactCount(reading.booksFinished, language: lang),
                       label: WidgetLocalization.finishedLabel(lang),
                       inline: true, alignment: .leading, valueSize: 13, labelSize: 10)
                Spacer(minLength: 4)
                KkStat(value: KkFormat.localizedCompactCount(reading.bookmarkCount, language: lang),
                       label: WidgetLocalization.bookmarksLabel(lang),
                       inline: true, alignment: .center, valueSize: 13, labelSize: 10)
                Spacer(minLength: 4)
                KkStat(value: KkFormat.localizedCompactCount(reading.annotationCount, language: lang),
                       label: WidgetLocalization.notesLabel(lang),
                       inline: true, alignment: .trailing, valueSize: 13, labelSize: 10)
            }
            .padding(.top, 9)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
    }

    private var large: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text(WidgetLocalization.statsTitle(lang))
                    .kkTitleFont(18)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Spacer(minLength: 6)
                streak
            }
            Spacer(minLength: 16)
            HStack(alignment: .center, spacing: 24) {
                today(size: 52)
                    .frame(maxWidth: .infinity, alignment: .leading)
                VStack(alignment: .leading, spacing: 12) {
                    durationStat(WidgetLocalization.thisWeekReading(lang), value: KkFormat.duration(effectiveWeekSeconds))
                    durationStat(WidgetLocalization.totalTime(lang), value: KkFormat.localizedCompactHours(reading.totalSeconds, language: lang))
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 18)
            VStack(alignment: .leading, spacing: 10) {
                Text(WidgetLocalization.thisWeekDaily(lang))
                    .kkLabelFont(10, weight: .medium)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                KkWeekBars(days: reading.days, referenceDate: isLiveSnapshot ? entry.date : nil, maxHeight: 92, barWidth: 20)
            }
            Spacer(minLength: 14)
            divider
            HStack(spacing: 4) {
                KkStat(value: KkFormat.localizedCompactCount(reading.booksStarted, language: lang), label: WidgetLocalization.startedLabel(lang), alignment: .center)
                Spacer(minLength: 4)
                KkStat(value: KkFormat.localizedCompactCount(reading.booksFinished, language: lang), label: WidgetLocalization.finishedLabel(lang), alignment: .center)
                Spacer(minLength: 4)
                KkStat(value: KkFormat.localizedCompactCount(reading.bookmarkCount, language: lang), label: WidgetLocalization.bookmarksLabel(lang), alignment: .center)
                Spacer(minLength: 4)
                KkStat(value: KkFormat.localizedCompactCount(reading.annotationCount, language: lang), label: WidgetLocalization.notesDetailedLabel(lang), alignment: .center)
            }
            .padding(.top, 12)
        }
        .padding(20)
    }

    private func today(size: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(WidgetLocalization.todayLabel(lang))
                .kkLabelFont(10, weight: .regular)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text("\(max(0, effectiveTodaySeconds / 60))")
                    .kkNumberFont(size)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
                Text(WidgetLocalization.minuteUnit(lang))
                    .kkLabelFont(10, weight: .regular)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private var streak: some View {
        Text(WidgetLocalization.streakBadge(effectiveStreakDays, lang))
            .kkLabelFont(10, weight: .medium)
            .foregroundStyle(KkTheme.secondaryInk(scheme))
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(KkTheme.track(scheme), in: Capsule())
    }

    private var divider: some View {
        Rectangle()
            .fill(KkTheme.hairline(scheme))
            .frame(height: 0.5)
            .accessibilityHidden(true)
    }

    private func durationStat(_ label: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .kkLabelFont(10, weight: .regular)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
            Text(value)
                .kkLabelFont(14, weight: .medium)
                .foregroundStyle(KkTheme.ink(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.75)
        }
        .accessibilityElement(children: .combine)
    }
}

struct KkWeekBars: View {
    let days: [KkDay]
    var referenceDate: Date? = nil
    var maxHeight: CGFloat
    var barWidth: CGFloat
    @Environment(\.colorScheme) private var scheme

    private var recent: [KkDay] { KkFormat.mondayFirstWeekDays(days, referenceDate: referenceDate ?? Date()) }
    private var peak: Int64 { KkFormat.maximumSeconds(recent) }
    private var highlightedDate: String? {
        guard let referenceDate else {
            return recent.last(where: { $0.seconds > 0 })?.date ?? recent.last?.date
        }
        let todayIso = KkFormat.isoString(from: referenceDate)
        if recent.contains(where: { $0.date == todayIso }) { return todayIso }
        return nil
    }

    var body: some View {
        ZStack(alignment: .top) {
            VStack {
                gridline
                Spacer()
                gridline
                Spacer()
                gridline
            }
            .frame(height: maxHeight)
            .accessibilityHidden(true)
            if !recent.isEmpty {
                HStack(alignment: .bottom, spacing: 4) {
                    ForEach(recent) { day in
                        VStack(spacing: 5) {
                            RoundedRectangle(cornerRadius: 3, style: .continuous)
                                .fill(KkTheme.bar(scheme, emphasized: day.date == highlightedDate))
                                .frame(width: barWidth, height: barHeight(day))
                                .frame(height: maxHeight, alignment: .bottom)
                            Text(KkFormat.weekday(day.date))
                                .kkLabelFont(9, weight: day.date == highlightedDate ? .semibold : .regular)
                                .foregroundStyle(day.date == highlightedDate
                                                 ? KkTheme.ink(scheme) : KkTheme.faintInk(scheme))
                                .lineLimit(1)
                        }
                        .frame(maxWidth: .infinity)
                        .accessibilityElement(children: .ignore)
                        .accessibilityLabel("\(KkFormat.weekday(day.date))，\(day.seconds > 0 ? KkFormat.duration(day.seconds) : WidgetLocalization.zeroMinutes())")
                    }
                }
            }
        }
    }

    private var gridline: some View {
        Rectangle()
            .fill(KkTheme.hairline(scheme))
            .frame(height: 0.5)
    }

    private func barHeight(_ day: KkDay) -> CGFloat {
        guard day.seconds > 0 else { return 2 }
        return min(maxHeight, max(4, maxHeight * CGFloat(day.seconds) / CGFloat(peak)))
    }
}

struct KkEmptyState: View {
    let title: String
    let message: String
    var systemImage = "book.closed"
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: 9) {
            Image(systemName: systemImage)
                .font(.system(size: 24, weight: .light))
                .foregroundStyle(KkTheme.faintInk(scheme))
                .padding(.bottom, 3)
                .accessibilityHidden(true)
            Text(title)
                .kkTitleFont(14)
                .foregroundStyle(KkTheme.ink(scheme))
            Text(message)
                .kkBodyFont(11)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(2)
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(16)
    }
}

private extension KkReading {
    static var sample: KkReading { KkSnapshot.sample.reading }
}

extension View {
    @ViewBuilder
    func lzWidgetURL(_ url: URL?) -> some View {
        #if os(iOS)
        if let url {
            self.widgetURL(url)
        } else {
            self
        }
        #else
        self
        #endif
    }

    @ViewBuilder
    func lzWidgetBackground() -> some View {
        let clamped = self.dynamicTypeSize(.small ... .large)
        if #available(iOS 17.0, macOS 14.0, *) {
            clamped.containerBackground(for: .widget) {
                KkBackdrop()
            }
        } else {
            clamped.background(KkBackdrop())
        }
    }
}
