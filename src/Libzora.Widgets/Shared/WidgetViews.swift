import SwiftUI
import WidgetKit

struct KkEntry: TimelineEntry {
    let date: Date
    let snapshot: KkSnapshot?
    let hasData: Bool

    static let placeholder = KkEntry(
        date: Date(),
        snapshot: .sample,
        hasData: true)
}

struct KkContinueWidgetView: View {
    let entry: KkEntry
    @Environment(\.widgetFamily) private var family
    @Environment(\.colorScheme) private var scheme

    private var book: KkBook? { entry.snapshot?.continueReading }
    /// In-progress books, newest reading first. Newer snapshots list several;
    /// older ones fall back to the single continueReading book.
    private var books: [KkBook] {
        if let listed = entry.snapshot?.continueBooks, !listed.isEmpty { return listed }
        return book.map { [$0] } ?? []
    }
    private var url: URL? {
        guard let id = book?.id else { return nil }
        return URL(string: "libzora://read/\(id)")
    }

    var body: some View {
        Group {
            #if os(iOS)
            switch family {
            case .accessoryRectangular:
                accessoryRectangular
            case .accessoryInline:
                Label(book.map { "继续阅读《\($0.title)》" } ?? "打开 Libzora 开始阅读",
                      systemImage: "book")
                    .font(KkTheme.body(12))
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
            KkEmptyState(title: "继续阅读", message: "打开 Libzora，开始阅读")
        } else if !books.isEmpty {
            if family == .systemMedium {
                medium(books)
            } else {
                small(books[0])
            }
        } else {
            KkEmptyState(title: "书架还空着", message: "导入一本书，开始阅读")
        }
    }

    private func small(_ book: KkBook) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            heading
            Spacer(minLength: 8)
            HStack(alignment: .center, spacing: 10) {
                KkCover(book: book, width: 52, height: 74)
                VStack(alignment: .leading, spacing: 5) {
                    Text(book.title)
                        .font(KkTheme.title(14))
                        .foregroundStyle(KkTheme.ink(scheme))
                        .lineLimit(3)
                        .minimumScaleFactor(0.85)
                    authors(book, size: 10)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 10)
            progress(book, compact: true)
        }
        .padding(16)
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
                let slotWidth = floor((availableWidth - spacing * 3) / 4)
                let availableHeight = max(80, geometry.size.height - verticalPadding * 2)
                let maxCoverHeight = max(44, availableHeight - 52)
                let coverHeight = min(maxCoverHeight, floor(slotWidth / 0.70))
                let coverWidth = floor(coverHeight * 0.70)

                VStack(alignment: .leading, spacing: 0) {
                    heading
                    Spacer(minLength: 6)
                    HStack(alignment: .top, spacing: spacing) {
                        ForEach(items) { item in
                            bookColumn(item, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
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
            KkCover(book: book, width: 86, height: 124)
            VStack(alignment: .leading, spacing: 0) {
                heading
                Text(book.title)
                    .font(KkTheme.title(20))
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
    private func bookColumn(_ book: KkBook, slotWidth: CGFloat, coverWidth: CGFloat, coverHeight: CGFloat) -> some View {
        if let destination = URL(string: "libzora://read/\(book.id)") {
            Link(destination: destination) {
                bookColumnContent(book, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
            }
        } else {
            bookColumnContent(book, slotWidth: slotWidth, coverWidth: coverWidth, coverHeight: coverHeight)
        }
    }

    private func bookColumnContent(_ book: KkBook, slotWidth: CGFloat, coverWidth: CGFloat, coverHeight: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            KkCover(book: book, width: coverWidth, height: coverHeight)
                .frame(maxWidth: .infinity, alignment: .center)
            Text(book.title)
                .font(KkTheme.title(10.5))
                .foregroundStyle(KkTheme.ink(scheme))
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 4) {
                KkProgressBar(progress: book.progress, height: 2)
                Text(KkFormat.percent(book.progress))
                    .font(KkTheme.label(9, weight: .medium))
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .monospacedDigit()
                    .fixedSize()
            }
        }
        .frame(width: slotWidth, alignment: .top)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    private var heading: some View {
        Text("继续阅读")
            .font(KkTheme.label(11, weight: .medium))
            .foregroundStyle(KkTheme.secondaryInk(scheme))
    }

    @ViewBuilder
    private func authors(_ book: KkBook, size: CGFloat) -> some View {
        if !book.authors.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            Text(book.authors)
                .font(KkTheme.body(size))
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(1)
        }
    }

    private func progress(_ book: KkBook, compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .center, spacing: 4) {
                Text("已读")
                    .font(KkTheme.label(10, weight: .regular))
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                Text(KkFormat.percent(book.progress))
                    .font(KkTheme.label(11, weight: .semibold))
                    .foregroundStyle(KkTheme.ink(scheme))
                    .monospacedDigit()
                Spacer(minLength: 4)
                Image(systemName: "arrow.right")
                    .font(.system(size: compact ? 10 : 11, weight: .medium))
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                    .accessibilityHidden(true)
            }
            KkProgressBar(progress: book.progress, height: 3)
        }
        .accessibilityElement(children: .combine)
    }

    #if os(iOS)
    private var accessoryRectangular: some View {
        VStack(alignment: .leading, spacing: 3) {
            Label("继续阅读", systemImage: "book")
                .font(KkTheme.label(11))
            Text(book?.title ?? "打开 Libzora，开始阅读")
                .font(KkTheme.title(12))
                .lineLimit(1)
            if let book {
                Text("已读 \(KkFormat.percent(book.progress))")
                    .font(KkTheme.body(10))
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

    private var reading: KkReading { entry.snapshot?.reading ?? .sample }

    var body: some View {
        Group {
            if !entry.hasData {
                KkEmptyState(title: "阅读数据", message: "打开 Libzora，记录阅读时光",
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
                Text("阅读数据")
                    .font(KkTheme.title(14))
                    .foregroundStyle(KkTheme.ink(scheme))
                Spacer(minLength: 6)
                streak
            }
            Spacer(minLength: 8)
            HStack(alignment: .bottom, spacing: 20) {
                VStack(alignment: .leading, spacing: 3) {
                    today(size: 36)
                    Text("本周 \(KkFormat.duration(reading.weekSeconds))")
                        .font(KkTheme.label(9, weight: .regular))
                        .foregroundStyle(KkTheme.secondaryInk(scheme))
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                }
                .frame(width: 106, alignment: .leading)
                KkWeekBars(days: reading.days, maxHeight: 46, barWidth: 12)
                    .frame(maxWidth: .infinity)
            }
            Spacer(minLength: 8)
            divider
            HStack(spacing: 12) {
                KkStat(value: "\(reading.booksFinished)", label: "读完", inline: true)
                KkStat(value: "\(reading.bookmarkCount)", label: "书签", inline: true)
                KkStat(value: "\(reading.annotationCount)", label: "划线", inline: true)
            }
            .padding(.top, 9)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 15)
    }

    private var large: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text("阅读数据")
                    .font(KkTheme.title(18))
                    .foregroundStyle(KkTheme.ink(scheme))
                Spacer(minLength: 6)
                streak
            }
            Spacer(minLength: 16)
            HStack(alignment: .center, spacing: 24) {
                today(size: 52)
                    .frame(maxWidth: .infinity, alignment: .leading)
                VStack(alignment: .leading, spacing: 12) {
                    durationStat("本周阅读", value: KkFormat.duration(reading.weekSeconds))
                    durationStat("累计阅读", value: KkFormat.compactHours(reading.totalSeconds))
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 18)
            VStack(alignment: .leading, spacing: 10) {
                Text("近 7 天")
                    .font(KkTheme.label(10, weight: .medium))
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
                KkWeekBars(days: reading.days, maxHeight: 92, barWidth: 20)
            }
            Spacer(minLength: 14)
            divider
            HStack(spacing: 12) {
                KkStat(value: "\(reading.booksStarted)", label: "开始阅读")
                KkStat(value: "\(reading.booksFinished)", label: "已读完")
                KkStat(value: "\(reading.bookmarkCount)", label: "书签")
                KkStat(value: "\(reading.annotationCount)", label: "划线笔记")
            }
            .padding(.top, 12)
        }
        .padding(20)
    }

    private func today(size: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("今日阅读")
                .font(KkTheme.label(10, weight: .regular))
                .foregroundStyle(KkTheme.secondaryInk(scheme))
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text("\(max(0, reading.todaySeconds / 60))")
                    .font(KkTheme.number(size))
                    .foregroundStyle(KkTheme.ink(scheme))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
                Text("分钟")
                    .font(KkTheme.label(10, weight: .regular))
                    .foregroundStyle(KkTheme.secondaryInk(scheme))
            }
        }
        .accessibilityElement(children: .combine)
    }

    private var streak: some View {
        Text("连续 \(max(0, reading.streakDays)) 天")
            .font(KkTheme.label(10, weight: .medium))
            .foregroundStyle(KkTheme.secondaryInk(scheme))
            .lineLimit(1)
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
                .font(KkTheme.label(10, weight: .regular))
                .foregroundStyle(KkTheme.secondaryInk(scheme))
            Text(value)
                .font(KkTheme.label(14, weight: .medium))
                .foregroundStyle(KkTheme.ink(scheme))
                .lineLimit(1)
                .minimumScaleFactor(0.75)
        }
        .accessibilityElement(children: .combine)
    }
}

struct KkWeekBars: View {
    let days: [KkDay]
    var maxHeight: CGFloat
    var barWidth: CGFloat
    @Environment(\.colorScheme) private var scheme

    private var recent: [KkDay] { KkFormat.lastSevenDays(days) }
    private var peak: Int64 { KkFormat.maximumSeconds(recent) }

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
            HStack(alignment: .bottom, spacing: 6) {
                ForEach(recent) { day in
                    VStack(spacing: 7) {
                        RoundedRectangle(cornerRadius: 3, style: .continuous)
                            .fill(KkTheme.bar(scheme, emphasized: day.date == recent.last?.date))
                            .frame(width: barWidth, height: barHeight(day))
                            .frame(height: maxHeight, alignment: .bottom)
                        Text(KkFormat.weekday(day.date))
                            .font(KkTheme.label(9, weight: day.date == recent.last?.date ? .semibold : .regular))
                            .foregroundStyle(day.date == recent.last?.date
                                             ? KkTheme.ink(scheme) : KkTheme.faintInk(scheme))
                            .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel("\(KkFormat.weekday(day.date))，\(day.seconds > 0 ? KkFormat.duration(day.seconds) : "0 分钟")")
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
                .font(KkTheme.title(14))
                .foregroundStyle(KkTheme.ink(scheme))
            Text(message)
                .font(KkTheme.body(11))
                .foregroundStyle(KkTheme.secondaryInk(scheme))
                .lineLimit(2)
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(16)
    }
}

private extension KkReading {
    static let sample = KkSnapshot.sample.reading
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
        if #available(iOS 17.0, macOS 14.0, *) {
            self.containerBackground(for: .widget) {
                KkBackdrop()
            }
        } else {
            self.background(KkBackdrop())
        }
    }
}
