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
            hasData: snapshot != nil)
    }
}

struct KkContinueWidget: Widget {
    let kind = "KkindleContinue"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkContinueWidgetView(entry: entry)
                .kkWidgetBackground()
        }
        .configurationDisplayName("继续阅读")
        .description("封面、书名和进度，轻点回到上次翻到的地方。")
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
    let kind = "KkindleStats"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkStatsWidgetView(entry: entry)
                .kkWidgetBackground()
        }
        .configurationDisplayName("阅读数据")
        .description("本周阅读时长、连续天数、读完的书与笔记数量。")
        .supportedFamilies([.systemMedium, .systemLarge])
        .contentMarginsDisabled()
    }
}

@main
struct KkindleWidgetBundle: WidgetBundle {
    @WidgetBundleBuilder
    var body: some Widget {
        KkContinueWidget()
        KkStatsWidget()
    }
}
