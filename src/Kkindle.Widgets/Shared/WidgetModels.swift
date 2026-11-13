import Foundation

/// The on-disk contract written by WidgetSnapshotPublisher. Keep this in sync
/// with the C# records; the extension only consumes the file and never writes
/// app data back.
struct KkSnapshot: Decodable {
    let continueReading: KkBook?
    let reading: KkReading

    static let sample = KkSnapshot(
        continueReading: KkBook(
            id: "6B1A2D5E-3C4F-4A6B-8C9D-0E1F2A3B4C5D",
            title: "夜航西飞",
            authors: "柏瑞尔·马卡姆",
            progress: 42,
            cover: nil),
        reading: KkReading(
            todaySeconds: 2_460,
            streakDays: 7,
            weekSeconds: 14_760,
            totalSeconds: 186_480,
            booksStarted: 18,
            booksFinished: 6,
            bookmarkCount: 24,
            annotationCount: 81,
            days: [
                KkDay(date: "2026-11-06", seconds: 1_200),
                KkDay(date: "2026-11-07", seconds: 2_820),
                KkDay(date: "2026-11-08", seconds: 900),
                KkDay(date: "2026-11-09", seconds: 3_480),
                KkDay(date: "2026-11-10", seconds: 1_980),
                KkDay(date: "2026-11-11", seconds: 3_900),
                KkDay(date: "2026-11-12", seconds: 2_460)
            ]))
}

struct KkBook: Decodable, Identifiable {
    let id: String
    let title: String
    let authors: String
    let progress: Double
    let cover: String?
}

struct KkReading: Decodable {
    let todaySeconds: Int64
    let streakDays: Int
    let weekSeconds: Int64
    let totalSeconds: Int64
    let booksStarted: Int
    let booksFinished: Int
    let bookmarkCount: Int
    let annotationCount: Int
    let days: [KkDay]
}

struct KkDay: Decodable, Identifiable {
    let date: String
    let seconds: Int64
    var id: String { date }
}

enum KkSnapshotStore {
    static let appGroup = "group.com.kingstacker.kkindle"
    static let folder = "KkindleWidgets"
    static let snapshotFile = "snapshot.json"

    /// Widget extension processes cannot read the app's Documents directory.
    /// The App Group is the only shared location on both iOS and macOS.
    static var rootURL: URL? {
        if let override = ProcessInfo.processInfo.environment["KKINDLE_WIDGET_DATA_ROOT"],
           !override.isEmpty {
            return URL(fileURLWithPath: override, isDirectory: true)
        }
        guard let container = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: appGroup) else { return nil }
        return container.appendingPathComponent(folder, isDirectory: true)
    }

    static func load() -> KkSnapshot? {
        guard let root = rootURL else { return nil }
        let url = root.appendingPathComponent(snapshotFile)
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(KkSnapshot.self, from: data)
    }

    static func coverURL(_ relative: String?) -> URL? {
        guard let relative, !relative.isEmpty, let root = rootURL else { return nil }
        let url = root.appendingPathComponent(relative)
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }
}

enum KkFormat {
    private static var isChinese: Bool {
        Locale.preferredLanguages.first?.hasPrefix("zh") == true
    }

    static func duration(_ seconds: Int64) -> String {
        let seconds = max(0, seconds)
        if seconds == 0 { return isChinese ? "0 分钟" : "0 min" }
        let hours = Int(seconds / 3_600)
        let minutes = Int((seconds % 3_600) / 60)
        if hours > 0 && minutes > 0 { return isChinese ? "\(hours) 小时 \(minutes) 分" : "\(hours) hr \(minutes) min" }
        if hours > 0 { return isChinese ? "\(hours) 小时" : "\(hours) hr" }
        if minutes > 0 { return isChinese ? "\(minutes) 分钟" : "\(minutes) min" }
        return isChinese ? "不到 1 分钟" : "<1 min"
    }

    static func compactHours(_ seconds: Int64) -> String {
        let hours = Double(max(0, seconds)) / 3_600
        if hours < 1 { return duration(seconds) }
        let value = hours >= 10 ? String(format: "%.0f", hours) : String(format: "%.1f", hours)
        return isChinese ? "\(value) 小时" : "\(value) hr"
    }

    static func progress(_ value: Double) -> Int {
        Int(max(0, min(100, value)).rounded())
    }

    static func weekday(_ isoDate: String) -> String {
        guard let date = date(from: isoDate) else { return "" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: isChinese ? "zh_Hans_CN" : "en_US_POSIX")
        formatter.dateFormat = "EEEEE"
        return formatter.string(from: date)
    }

    static func date(from isoDate: String) -> Date? {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.date(from: isoDate)
    }

    static func lastSevenDays(_ days: [KkDay]) -> [KkDay] {
        Array(days.suffix(7))
    }

    static func maximumSeconds(_ days: [KkDay]) -> Int64 {
        max(1, days.map { max(0, $0.seconds) }.max() ?? 0)
    }

    static func percent(_ value: Double) -> String {
        "\(progress(value))%"
    }
}
