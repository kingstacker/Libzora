import Foundation

/// The on-disk contract written by WidgetSnapshotPublisher. Keep this in sync
/// with the C# records; the extension only consumes the file and never writes
/// app data back.
struct KkSnapshot: Decodable {
    /// Interface font mirrored from the host app (`0` = KingHwaOldSong, `1` = system default).
    /// Optional so snapshots produced by older app versions decode as the bundled font.
    let uiFont: Int?
    let generatedAt: String?
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
            generatedAt: nil,
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
                    KkDay(date: "2026-11-06", seconds: 1_200, listeningSeconds: 300),
                    KkDay(date: "2026-11-07", seconds: 2_820, listeningSeconds: 600),
                    KkDay(date: "2026-11-08", seconds: 900, listeningSeconds: 0),
                    KkDay(date: "2026-11-09", seconds: 3_480, listeningSeconds: 900),
                    KkDay(date: "2026-11-10", seconds: 1_980, listeningSeconds: 420),
                    KkDay(date: "2026-11-11", seconds: 3_900, listeningSeconds: 1_200),
                    KkDay(date: "2026-11-12", seconds: 2_460, listeningSeconds: 720)
                ],
                todayListeningSeconds: 720,
                weekListeningSeconds: 4_140,
                totalListeningSeconds: 32_400))
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
    let todayListeningSeconds: Int64?
    let weekListeningSeconds: Int64?
    let totalListeningSeconds: Int64?

    var safeTodayListeningSeconds: Int64 { max(0, todayListeningSeconds ?? 0) }
    var safeWeekListeningSeconds: Int64 { max(0, weekListeningSeconds ?? 0) }
    var safeTotalListeningSeconds: Int64 { max(0, totalListeningSeconds ?? 0) }
}

struct KkDay: Decodable, Identifiable {
    let date: String
    let seconds: Int64
    let listeningSeconds: Int64?
    var id: String { date }

    init(date: String, seconds: Int64, listeningSeconds: Int64? = nil) {
        self.date = date
        self.seconds = seconds
        self.listeningSeconds = listeningSeconds
    }
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

    static func compactHoursParts(_ seconds: Int64, isChinese: Bool = KkFormat.isChinese) -> (value: String, unit: String) {
        let clamped = max(0, seconds)
        if clamped == 0 { return ("0", isChinese ? "分钟" : "min") }
        if clamped < 60 { return ("<1", isChinese ? "分钟" : "min") }
        let hours = Double(clamped) / 3_600
        if hours < 1 {
            let minutes = Int(clamped / 60)
            return ("\(minutes)", isChinese ? "分钟" : "min")
        }
        let roundedTenths = (hours * 10).rounded() / 10
        let value: String
        if roundedTenths >= 10 || roundedTenths == roundedTenths.rounded() {
            value = String(format: "%.0f", roundedTenths)
        } else {
            value = String(format: "%.1f", roundedTenths)
        }
        return (value, isChinese ? "小时" : "hr")
    }

    static func compactHours(_ seconds: Int64, isChinese: Bool = KkFormat.isChinese) -> String {
        let parts = compactHoursParts(seconds, isChinese: isChinese)
        return "\(parts.value) \(parts.unit)"
    }

    static func compactCount(_ count: Int, isChinese: Bool = KkFormat.isChinese) -> String {
        let safe = max(0, count)
        if safe < 10_000 { return "\(safe)" }
        func formatScaled(_ value: Double, unit: String) -> String {
            let roundedTenths = (value * 10).rounded() / 10
            let text = roundedTenths >= 100 || roundedTenths == roundedTenths.rounded()
                ? String(format: "%.0f", roundedTenths)
                : String(format: "%.1f", roundedTenths)
            return "\(text)\(unit)"
        }
        if isChinese { return formatScaled(Double(safe) / 10_000, unit: "万") }
        if safe >= 1_000_000 { return formatScaled(Double(safe) / 1_000_000, unit: "M") }
        return formatScaled(Double(safe) / 1_000, unit: "k")
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
        formatter.timeZone = Calendar.current.timeZone
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.date(from: isoDate)
    }

    static func isoString(from date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = Calendar.current.timeZone
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from: date)
    }

    static func isoTimestamp(from isoString: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let parsed = fractional.date(from: isoString) { return parsed }
        let standard = ISO8601DateFormatter()
        standard.formatOptions = [.withInternetDateTime]
        return standard.date(from: isoString)
    }

    static func resolvedAnchorDay(generatedAt: String?, days: [KkDay], calendar: Calendar) -> Date? {
        if let generatedAt, let parsed = isoTimestamp(from: generatedAt) {
            return calendar.startOfDay(for: parsed)
        }
        if let latest = days.compactMap({ date(from: $0.date) }).max() {
            return calendar.startOfDay(for: latest)
        }
        return nil
    }

    static func effectiveTodaySeconds(reading: KkReading, generatedAt: String?, on date: Date = Date()) -> Int64 {
        let cal = Calendar.current
        let targetIso = isoString(from: date)
        let recordedToday = reading.days.first(where: { $0.date == targetIso })?.seconds
        guard let anchorDay = resolvedAnchorDay(generatedAt: generatedAt, days: reading.days, calendar: cal) else {
            return max(0, recordedToday ?? reading.todaySeconds)
        }
        let targetDay = cal.startOfDay(for: date)
        let gap = cal.dateComponents([.day], from: anchorDay, to: targetDay).day ?? 0
        if gap <= 0 {
            return max(0, max(recordedToday ?? 0, reading.todaySeconds))
        }
        return max(0, recordedToday ?? 0)
    }

    static func effectiveWeekSeconds(reading: KkReading, generatedAt: String?, on date: Date = Date()) -> Int64 {
        let cal = Calendar.current
        guard let anchorDay = resolvedAnchorDay(generatedAt: generatedAt, days: reading.days, calendar: cal) else {
            return max(0, reading.weekSeconds)
        }
        let targetDay = cal.startOfDay(for: date)
        let gap = cal.dateComponents([.day], from: anchorDay, to: targetDay).day ?? 0
        if gap <= 0 {
            return max(0, reading.weekSeconds)
        }
        let windowStart = cal.date(byAdding: .day, value: -6, to: targetDay) ?? targetDay
        return reading.days.reduce(0) { sum, day in
            guard let dayDate = self.date(from: day.date) else { return sum }
            let dayStart = cal.startOfDay(for: dayDate)
            guard dayStart >= windowStart && dayStart <= targetDay else { return sum }
            return sum + max(0, day.seconds)
        }
    }

    static func effectiveStreakDays(reading: KkReading, generatedAt: String?, on date: Date = Date()) -> Int {
        guard reading.streakDays > 0 else { return 0 }
        let cal = Calendar.current
        guard let anchorDay = resolvedAnchorDay(generatedAt: generatedAt, days: reading.days, calendar: cal) else {
            return max(0, reading.streakDays)
        }
        let targetDay = cal.startOfDay(for: date)
        let gap = cal.dateComponents([.day], from: anchorDay, to: targetDay).day ?? 0
        if gap <= 0 {
            return max(0, reading.streakDays)
        }
        if gap == 1 {
            let anchorIso = isoString(from: anchorDay)
            let anchorSeconds = reading.days.first(where: { $0.date == anchorIso })?.seconds ?? reading.todaySeconds
            return anchorSeconds > 0 ? max(0, reading.streakDays) : 0
        }
        return 0
    }

    static func mondayStartOfWeek(for date: Date, calendar: Calendar = Calendar(identifier: .gregorian)) -> Date {
        var cal = calendar
        cal.timeZone = Calendar.current.timeZone
        let dayStart = cal.startOfDay(for: date)
        let weekday = cal.component(.weekday, from: dayStart) // 1 = Sun, 2 = Mon, ..., 7 = Sat
        let daysFromMonday = (weekday + 5) % 7
        return cal.date(byAdding: .day, value: -daysFromMonday, to: dayStart) ?? dayStart
    }

    static func mondayFirstWeekDays(_ days: [KkDay], referenceDate: Date = Date()) -> [KkDay] {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = Calendar.current.timeZone
        let currentMonday = mondayStartOfWeek(for: referenceDate, calendar: cal)
        let latestDataDate = days.compactMap { date(from: $0.date) }.max()
        let weekStart: Date
        if let latest = latestDataDate, cal.startOfDay(for: latest) < currentMonday {
            weekStart = mondayStartOfWeek(for: latest, calendar: cal)
        } else {
            weekStart = currentMonday
        }
        let byDate = Dictionary(days.map { ($0.date, $0) }, uniquingKeysWith: { first, _ in first })
        return (0..<7).map { offset in
            let dayDate = cal.date(byAdding: .day, value: offset, to: weekStart) ?? weekStart
            let iso = isoString(from: dayDate)
            return byDate[iso] ?? KkDay(date: iso, seconds: 0)
        }
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
