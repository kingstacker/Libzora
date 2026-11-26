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
    /// In-progress books, newest reading first. Older snapshots fall back to
    /// the single continueReading book.
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
        GeometryReader { proxy in
            let innerH = max(90, proxy.size.height - 24)
            let titleReserve: CGFloat = innerH >= 130 ? 38 : 34
            let coverHeight = min(100, max(72, innerH - titleReserve - 6))
            let coverWidth = (coverHeight / 1.43).rounded(.down)

            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top, spacing: 10) {
                    KkCover(book: book, width: coverWidth, height: coverHeight, blankPlaceholder: entry.isPlaceholder)

                    VStack(alignment: .leading, spacing: 0) {
                        Text(WidgetLocalization.continueTitle(lang))
                            .kkTitleFont(11.5)
                            .foregroundStyle(KkTheme.secondaryInk(scheme))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)

                        Spacer(minLength: 4)

                        KkStat(
                            value: KkFormat.percent(book.progress),
                            label: WidgetLocalization.readLabel(lang),
                            alignment: .leading,
                            valueSize: 16,
                            labelSize: 10,
                            stackSpacing: 2)
                    }
                    .frame(maxWidth: .infinity, maxHeight: coverHeight, alignment: .leading)
                }

                Spacer(minLength: 6)

                Text(book.title)
                    .kkTitleFont(13.5)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(2)
                    .minimumScaleFactor(0.85)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.horizontal, 13)
            .padding(.vertical, 12)
        }
    }

    /// Use the actual book count, not four permanent slots, while keeping each
    /// cover's independent destination and a comfortable, bounded cover size.
    @ViewBuilder
    private func medium(_ books: [KkBook]) -> some View {
        if books.count == 1, let only = books.first {
            mediumBook(only)
        } else {
            let items = Array(books.prefix(4))
            GeometryReader { geometry in
                let layout = KkContinueBooksLayout(size: geometry.size, bookCount: items.count)
                VStack(alignment: .leading, spacing: 0) {
                    heading
                    Spacer(minLength: 6)
                    HStack(alignment: .top, spacing: KkContinueBooksLayout.spacing) {
                        ForEach(items) { item in
                            bookColumn(item, slotWidth: layout.slotWidth, coverWidth: layout.coverWidth,
                                       coverHeight: layout.coverHeight)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, KkContinueBooksLayout.horizontalPadding)
                .padding(.vertical, KkContinueBooksLayout.verticalPadding)
            }
        }
    }

    private func mediumBook(_ book: KkBook) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            heading
            HStack(alignment: .center, spacing: 16) {
                KkCover(book: book, width: 72, height: 103, blankPlaceholder: entry.isPlaceholder)
                VStack(alignment: .leading, spacing: 0) {
                    Text(book.title)
                        .kkTitleFont(20)
                        .foregroundStyle(KkTheme.ink(scheme))
                        .lineLimit(2)
                        .minimumScaleFactor(0.82)
                    authors(book, size: 11)
                        .padding(.top, 5)
                    Spacer(minLength: 8)
                    progress(book)
                }
                .frame(maxWidth: .infinity, maxHeight: 103, alignment: .leading)
            }
            .frame(maxHeight: .infinity)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
    }

    @ViewBuilder
    private func bookColumn(_ book: KkBook, slotWidth: CGFloat, coverWidth: CGFloat,
                            coverHeight: CGFloat) -> some View {
        if !entry.isPlaceholder, let destination = URL(string: "libzora://read/\(book.id)") {
            Link(destination: destination) {
                bookColumnContent(book, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
            }
        } else {
            bookColumnContent(book, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
        }
    }

    private func bookColumnContent(_ book: KkBook, slotWidth: CGFloat, coverWidth: CGFloat, coverHeight: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            KkCover(book: book, width: coverWidth, height: coverHeight, blankPlaceholder: entry.isPlaceholder)
                .frame(maxWidth: .infinity, alignment: .center)
            Text(book.title)
                .kkTitleFont(11.5)
                .foregroundStyle(KkTheme.ink(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.85)
                .padding(.top, 5)
            Text(WidgetLocalization.readPercent(KkFormat.percent(book.progress), lang))
                .kkBodyFont(10)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.82)
                .padding(.top, 2)
        }
        .frame(width: slotWidth, alignment: .leading)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    private var heading: some View {
        KkWidgetHeading(title: WidgetLocalization.continueTitle(lang))
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

    private func progress(_ book: KkBook) -> some View {
        KkStat(
            value: KkFormat.percent(book.progress),
            label: WidgetLocalization.readLabel(lang),
            inline: true,
            valueSize: 13,
            labelSize: 10.5)
    }

    #if os(iOS)
    private var accessoryRectangular: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(WidgetLocalization.continueShort(lang))
                .kkBodyFont(11)
                .opacity(0.8)
                .lineLimit(1)
            Text(book?.title ?? WidgetLocalization.continueEmptyMessage(lang))
                .kkTitleFont(13)
                .lineLimit(1)
            if let book {
                Text(WidgetLocalization.readPercent(KkFormat.percent(book.progress), lang))
                    .kkBodyFont(11)
                    .opacity(0.8)
                    .lineLimit(1)
            }
        }
        .accessibilityElement(children: .combine)
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
            heading()
            Spacer(minLength: 8)
            HStack(alignment: .bottom, spacing: 14) {
                VStack(alignment: .leading, spacing: 4) {
                    today(size: 34)
                    Text(WidgetLocalization.weekDuration(KkFormat.duration(effectiveWeekSeconds), lang))
                        .kkBodyFont(10.5)
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                }
                .frame(minWidth: 84, maxWidth: 110, alignment: .leading)
                KkWeekBars(days: reading.days, referenceDate: isLiveSnapshot ? entry.date : nil, maxHeight: 48, barWidth: 10)
                    .frame(maxWidth: .infinity)
            }
            Spacer(minLength: 10)
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                countStat(reading.booksFinished, label: WidgetLocalization.finishedLabel(lang), inline: true)
                countStat(reading.bookmarkCount, label: WidgetLocalization.bookmarksLabel(lang), inline: true, alignment: .center)
                countStat(reading.annotationCount, label: WidgetLocalization.notesLabel(lang), inline: true, alignment: .trailing)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
    }

    private var large: some View {
        VStack(alignment: .leading, spacing: 0) {
            heading(size: 14)
            Spacer(minLength: 12)
            HStack(alignment: .center, spacing: 20) {
                today(size: 48)
                    .frame(maxWidth: .infinity, alignment: .leading)
                VStack(alignment: .leading, spacing: 12) {
                    durationStat(WidgetLocalization.thisWeekReading(lang), seconds: effectiveWeekSeconds)
                    durationStat(WidgetLocalization.totalTime(lang), seconds: reading.totalSeconds)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 12)
            VStack(alignment: .leading, spacing: 8) {
                Text(WidgetLocalization.thisWeekDaily(lang))
                    .kkBodyFont(11)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                KkWeekBars(days: reading.days, referenceDate: isLiveSnapshot ? entry.date : nil, maxHeight: 80, barWidth: 18)
            }
            Spacer(minLength: 12)
            HStack(alignment: .top, spacing: 10) {
                countStat(reading.booksStarted, label: WidgetLocalization.startedLabel(lang), alignment: .center)
                countStat(reading.booksFinished, label: WidgetLocalization.finishedLabel(lang), alignment: .center)
                countStat(reading.bookmarkCount, label: WidgetLocalization.bookmarksLabel(lang), alignment: .center)
                countStat(reading.annotationCount, label: WidgetLocalization.notesDetailedLabel(lang), alignment: .center)
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 18)
    }

    private func heading(size: CGFloat = 12) -> some View {
        KkWidgetHeading(
            title: WidgetLocalization.statsTitle(lang),
            detail: WidgetLocalization.streakBadge(effectiveStreakDays, lang),
            size: size)
    }

    private func countStat(_ count: Int, label: String, inline: Bool = false,
                           alignment: HorizontalAlignment = .leading) -> some View {
        KkStat(
            value: KkFormat.localizedCompactCount(count, language: lang),
            label: label,
            inline: inline,
            alignment: alignment,
            valueSize: inline ? 14 : 20,
            labelSize: 10.5,
            stackSpacing: 4,
            expand: true,
            subdued: count == 0)
    }

    private func today(size: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(WidgetLocalization.todayLabel(lang))
                .kkBodyFont(10.5)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text("\(max(0, effectiveTodaySeconds / 60))")
                    .kkNumberFont(size)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
                Text(WidgetLocalization.minuteUnit(lang))
                    .kkBodyFont(11)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private func durationStat(_ label: String, seconds: Int64) -> some View {
        let parts = KkFormat.localizedCompactHoursParts(seconds, language: lang)
        return VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .kkBodyFont(10.5)
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            HStack(alignment: .firstTextBaseline, spacing: 3) {
                Text(parts.value)
                    .kkNumberFont(17)
                    .foregroundStyle(KkTheme.ink(scheme))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Text(parts.unit)
                    .kkBodyFont(10.5)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(1)
            }
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
                                .kkLabelFont(10, weight: day.date == highlightedDate ? .semibold : .regular)
                                .foregroundStyle(day.date == highlightedDate
                                                 ? KkTheme.ink(scheme) : KkTheme.secondaryInk(scheme))
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
    @Environment(\.widgetFamily) private var family

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            KkWidgetHeading(title: title, size: family == .systemLarge ? 14 : 12)
            Spacer(minLength: 8)
            VStack(spacing: 10) {
                Image(systemName: systemImage)
                    .font(.system(size: 24, weight: .light))
                    .foregroundStyle(KkTheme.faintInk(scheme))
                    .accessibilityHidden(true)
                Text(message)
                    .kkBodyFont(11)
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .lineLimit(2)
                    .multilineTextAlignment(.center)
            }
            .frame(maxWidth: .infinity)
            Spacer(minLength: 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.horizontal, family == .systemSmall ? 14 : (family == .systemLarge ? 20 : 16))
        .padding(.vertical, family == .systemLarge ? 18 : 14)
    }
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
