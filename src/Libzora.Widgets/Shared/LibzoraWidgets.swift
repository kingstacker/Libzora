import SwiftUI
import WidgetKit

struct KkProvider: TimelineProvider {
    func placeholder(in context: Context) -> KkEntry { .placeholder }

    func getSnapshot(in context: Context, completion: @escaping (KkEntry) -> Void) {
        completion(context.isPreview ? .placeholder : entry())
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<KkEntry>) -> Void) {
        let now = Date()
        let entry = entry(at: now)
        let calendar = Calendar.current
        let twentyMinutes = calendar.date(byAdding: .minute, value: 20, to: now)
            ?? now.addingTimeInterval(1_200)
        // Also schedule an update right after midnight so seasonal/festival
        // decorations and daily counters roll over promptly on date change.
        let tomorrow = calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: now))
        let midnightRollover = tomorrow?.addingTimeInterval(2) ?? twentyMinutes
        let next = min(twentyMinutes, midnightRollover)
        completion(Timeline(entries: [entry], policy: .after(next)))
    }

    private func entry(at date: Date = Date()) -> KkEntry {
        let snapshot = KkSnapshotStore.load()
        return KkEntry(
            date: date,
            snapshot: snapshot,
            hasData: snapshot != nil,
            isPlaceholder: false)
    }
}

struct KkContinueWidget: Widget {
    let kind = "LibzoraContinue"
    private var lang: WidgetLanguage { WidgetLocalization.current }

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkContinueWidgetView(entry: entry)
                .environment(\.kkUseSystemFont, entry.snapshot?.useSystemFont ?? false)
                .lzWidgetBackground()
        }
        .configurationDisplayName(WidgetLocalization.continueTitle(lang))
        .description(WidgetLocalization.continueDescription(lang))
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
    private var lang: WidgetLanguage { WidgetLocalization.current }

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkStatsWidgetView(entry: entry)
                .environment(\.kkUseSystemFont, entry.snapshot?.useSystemFont ?? false)
                .lzWidgetBackground()
        }
        .configurationDisplayName(WidgetLocalization.statsTitle(lang))
        .description(WidgetLocalization.statsDescription(lang))
        .supportedFamilies([.systemMedium, .systemLarge])
        .contentMarginsDisabled()
    }
}

struct KkReadingTreeWidget: Widget {
    let kind = "LibzoraTree"
    private var lang: WidgetLanguage { WidgetLocalization.current }

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: KkProvider()) { entry in
            KkReadingTreeWidgetView(entry: entry)
                .environment(\.kkUseSystemFont, entry.snapshot?.useSystemFont ?? false)
                .lzWidgetBackground()
        }
        .configurationDisplayName(WidgetLocalization.treeTitle(lang))
        .description(WidgetLocalization.treeDescription(lang))
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
