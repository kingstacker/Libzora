import Foundation

/// The on-disk contract written by WidgetSnapshotPublisher. Keep this in sync
/// with the C# records; the extension only consumes the file and never writes
/// app data back.
struct KkSnapshot: Decodable {
    /// Interface font mirrored from the host app (`0` = KingHwaOldSong, `1` = system default).
    /// Optional so snapshots produced by older app versions decode as the bundled font.
    let uiFont: Int?
    let continueReading: KkBook?
    /// In-progress books ordered by the moment they were last read. Newer
    /// publishers always send it; older snapshots fall back to continueReading.
    let continueBooks: [KkBook]?
    let reading: KkReading

    var useSystemFont: Bool { uiFont == 1 }

    static var sample: KkSnapshot { sample(isChinese: KkFormat.isChinese) }

    static func sample(isChinese: Bool) -> KkSnapshot {
        let books = sampleBooks(isChinese: isChinese)
        return KkSnapshot(
            uiFont: 0,
            continueReading: books[0],
            continueBooks: books,
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

    private static func sampleBooks(isChinese: Bool) -> [KkBook] {
        let author = isChinese ? "示例作者" : "Sample Author"
        return [
            KkBook(
                id: "6B1A2D5E-3C4F-4A6B-8C9D-0E1F2A3B4C5D",
                title: isChinese ? "示例书籍一" : "Sample Book 1",
                authors: author,
                progress: 42,
                cover: nil),
            KkBook(
                id: "7C2B3E6F-4D5A-4B7C-9DAE-1F2A3B4C5D6E",
                title: isChinese ? "示例书籍二" : "Sample Book 2",
                authors: author,
                progress: 68,
                cover: nil),
            KkBook(
                id: "8D3C4F7A-5E6B-4C8D-AEBF-2A3B4C5D6E7F",
                title: isChinese ? "示例书籍三" : "Sample Book 3",
                authors: author,
                progress: 12,
                cover: nil),
            KkBook(
                id: "9E4D5A8B-6F7C-4D9E-BFC0-3B4C5D6E7F80",
                title: isChinese ? "示例书籍四" : "Sample Book 4",
                authors: author,
                progress: 87,
                cover: nil)
        ]
    }
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
    static let appGroup = "group.com.kingstacker.libzora"
    static let folder = "LibzoraWidgets"
    static let snapshotFile = "snapshot.json"

    /// Widget extension processes cannot read the app's Documents directory.
    /// The App Group is the only shared location on both iOS and macOS.
    static var rootURL: URL? {
        if let override = ProcessInfo.processInfo.environment["LIBZORA_WIDGET_DATA_ROOT"],
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
    static var isChinese: Bool {
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

    static func compactHours(_ seconds: Int64, isChinese: Bool = KkFormat.isChinese) -> String {
        let clamped = max(0, seconds)
        if clamped == 0 { return isChinese ? "0 分钟" : "0 min" }
        if clamped < 60 { return isChinese ? "<1 分钟" : "<1 min" }
        let hours = Double(clamped) / 3_600
        if hours < 1 {
            let minutes = Int(clamped / 60)
            return isChinese ? "\(minutes) 分钟" : "\(minutes) min"
        }
        let roundedTenths = (hours * 10).rounded() / 10
        let value: String
        if roundedTenths >= 10 || roundedTenths == roundedTenths.rounded() {
            value = String(format: "%.0f", roundedTenths)
        } else {
            value = String(format: "%.1f", roundedTenths)
        }
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
