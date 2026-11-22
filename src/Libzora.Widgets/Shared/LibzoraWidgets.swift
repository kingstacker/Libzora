import SwiftUI
import WidgetKit

struct KkProvider: TimelineProvider {
    func placeholder(in context: Context) -> KkEntry { .placeholder }

    func getSnapshot(in context: Context, completion: @escaping (KkEntry) -> Void) {
        completion(context.isPreview ? .placeholder : entry())
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<KkEntry>) -> Void) {
        let entry = entry()
        let next = Calendar.current.date(byAdding: .minute, value: 20, to: Date())
            ?? Date().addingTimeInterval(1_200)
        completion(Timeline(entries: [entry], policy: .after(next)))
    }

    private func entry() -> KkEntry {
        let snapshot = KkSnapshotStore.load()
        return KkEntry(
            date: Date(),
            snapshot: snapshot,
            hasData: snapshot != nil,
            isPlaceholder: false)
    }
}

struct KkContinueWidget: Widget {
    let kind = "LibzoraContinue"
    private var isChinese: Bool { KkFormat.isChinese }

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkContinueWidgetView(entry: entry)
                .environment(\.kkUseSystemFont, entry.snapshot?.useSystemFont ?? false)
                .lzWidgetBackground()
        }
        .configurationDisplayName(isChinese ? "继续阅读" : "Continue Reading")
        .description(isChinese ? "在读的几本书，点哪一本就从哪一本继续。" : "Jump right back into the books you are reading.")
        .supportedFamilies(families)
        .contentMarginsDisabled()
    }

    private var families: [WidgetFamily] {
        #if os(iOS)
        return [.systemSmall, .systemMedium, .accessoryRectangular, .accessoryInline]
        #else
        return [.systemSmall, .systemMedium]
        #endif
    }
}

struct KkStatsWidget: Widget {
    let kind = "LibzoraStats"
    private var isChinese: Bool { KkFormat.isChinese }

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkStatsWidgetView(entry: entry)
                .environment(\.kkUseSystemFont, entry.snapshot?.useSystemFont ?? false)
                .lzWidgetBackground()
        }
        .configurationDisplayName(isChinese ? "阅读数据" : "Reading Stats")
        .description(isChinese ? "本周阅读时长、连续天数、读完的书与笔记数量。" : "Track daily reading time, streak, finished books, and notes.")
        .supportedFamilies([.systemMedium, .systemLarge])
        .contentMarginsDisabled()
    }
}

struct KkReadingTreeWidget: Widget {
    let kind = "LibzoraTree"
    private var isChinese: Bool { KkFormat.isChinese }

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkReadingTreeWidgetView(entry: entry)
                .environment(\.kkUseSystemFont, entry.snapshot?.useSystemFont ?? false)
                .lzWidgetBackground()
        }
        .configurationDisplayName(isChinese ? "阅读之树" : "Reading Tree")
        .description(isChinese ? "累计阅读浇灌小树苗，枝繁叶茂、开花结果。" : "Watch your reading tree grow, blossom, and bear fruit.")
        .supportedFamilies([.systemSmall, .systemMedium])
        .contentMarginsDisabled()
    }
}

@main
struct LibzoraWidgetBundle: WidgetBundle {
    @WidgetBundleBuilder
    var body: some Widget {
        KkContinueWidget()
        KkStatsWidget()
        KkReadingTreeWidget()
    }
}
