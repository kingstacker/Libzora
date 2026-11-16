import Foundation

enum JSONValue: Codable {
    case null, bool(Bool), number(Double), string(String), array([JSONValue]), object([String: JSONValue])
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else if let value = try? container.decode([JSONValue].self) { self = .array(value) }
        else { self = .object(try container.decode([String: JSONValue].self)) }
    }
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }
    var foundationValue: Any {
        switch self {
        case .null: return NSNull()
        case .bool(let value): return value
        case .number(let value): return value
        case .string(let value): return value
        case .array(let value): return value.map(\.foundationValue)
        case .object(let value): return value.mapValues(\.foundationValue)
        }
    }
}

func requestObject<Value: Encodable>(_ value: Value) throws -> Any {
    try JSONSerialization.jsonObject(with: JSONEncoder().encode(value))
}

struct CreatedFolder: Decodable { let id: String; let name: String }
struct CoverResult: Decodable { let cover: String? }
struct MessageResult: Decodable { let message: String; let warning: String? }
struct TextResult: Decodable { let text: String }
struct ReaderFontOption: Decodable, Identifiable, Equatable {
    let key: String
    let name: String
    let detail: String
    /// builtin, system or download.
    let kind: String
    let installed: Bool
    let bytes: Int64
    let license: String?
    let previewName: String
    let path: String?
    var id: String { key }
}
struct ReaderFontList: Decodable { let current: String; let fonts: [ReaderFontOption] }
struct ReaderFontInstall: Decodable { let key: String; let path: String; let previewName: String }
struct ProfileSummary: Decodable { let version: String; let sync: String; let ai: String; let kindle: String; let storage: String; let account: AccountStatus }
struct AccountUser: Decodable { let id: String; let username: String; let email: String?; let emailVerified: Bool? }
struct AccountStatus: Decodable {
    let serverUrl: String; let user: AccountUser?; let expiresAt: String?; let isLoggedIn: Bool
    let region: Int; let requiresDataRestart: Bool
    var regionName: String { region == 1 ? L("国际区") : L("中国区") }
}

struct AccountDevice: Decodable, Identifiable {
    let deviceId: String; let name: String; let platform: String; let state: String
    let registeredAt: String; let lastSeenAt: String
    var id: String { deviceId }
}
struct AccountMembership: Decodable {
    let accountId: String; let region: Int; let tier: String; let hasLifetimeFeatures: Bool
    let proExpiresAt: String?; let deviceLimit: Int; let loggedInDeviceCount: Int; let activatedDeviceCount: Int
    let uploadBytes: Int64; let uploadQuota: Int64; let downloadBytes: Int64; let downloadRemainingBytes: Int64
    let downloadCreditBytes: Int64; let storageBytes: Int64; let storageQuota: Int64; let storageAddonNextExpiresAt: String?
    let plusRenewalLimitAt: String?; let pendingPlusDays: Int?
    let plusDownloadQuotaBytes: Int64?; let plusDownloadUsedBytes: Int64?; let plusDownloadExpiresAt: String?
    let plusEarlyDownloadQuotaBytes: Int64?; let plusEarlyDownloadUsedBytes: Int64?
    let plusEarlyDownloadEndsAt: String?
    let metadataDownloadQuotaBytes: Int64?; let metadataDownloadUsedBytes: Int64?
    let metadataDownloadResetsAt: String?
    let trialAvailable: Bool?
    let trialClaimed: Bool?
    let trialClaimedAt: String?
    let trialExpiresAt: String?
    let featureExpiresAt: String?
    var featureExpiresAtDate: Date? { Self.date(featureExpiresAt ?? proExpiresAt ?? trialExpiresAt) }
    var tierName: String { planName }
    /// Historical paid periods retain their expiry, never a permanent buyout.
    var plan: String {
        if tier == "lifetime" { return "lifetime" }
        if (tier == "trial" || tier == "pro"), let expiry = featureExpiresAtDate, expiry > Date() { return "trial" }
        return "free"
    }
    var planName: String { plan == "trial" ? L("试用订阅") : plan == "lifetime" ? L("买断订阅") : "Free" }
    var isTrialExpired: Bool { plan != "lifetime" && (featureExpiresAtDate.map { $0 <= Date() } ?? false) }
    var trialRenewalMessage: String? {
        guard plan == "trial", let expiry = featureExpiresAtDate else { return nil }
        let remaining = expiry.timeIntervalSinceNow
        guard remaining > 0 && remaining <= 30 * 86_400 else { return nil }
        let days = max(1, Int(ceil(remaining / 86_400)))
        let duration = days <= 1 ? L("不足 1 天") : L("剩余 {0} 天", days)
        return L("试用订阅将于 {0} 到期（{1}）。到期后 S3 / WebDAV 多端同步将停用，本地书库及自备存储中的数据不会删除。购买买断订阅可永久使用。", Self.day(expiry), duration)
    }
    var isTrialExpiringSoon: Bool { trialRenewalMessage != nil }
    var trialExpiresAtDate: Date? { Self.date(trialExpiresAt) }
    var isTrialActive: Bool { plan == "trial" && trialClaimed == true && (trialExpiresAtDate.map { $0 > Date() } ?? false) }
    var canClaimTrial: Bool { trialAvailable == true && trialClaimed != true && plan == "free" }
    var trialStatusMessage: String {
        guard isTrialActive, let end = trialExpiresAtDate else {
            return L("每账号主动领取一次，连续 7 天。到期只停用同步，不删除本地或自备存储数据。")
        }
        return L("试用期至 {0}，到期只停用同步，不删除本地或自备存储数据。可购买买断订阅继续使用。", Self.day(end))
    }
    var planExpiryText: String {
        if plan == "trial", let expiry = featureExpiresAtDate { return L("有效至 ") + Self.day(expiry) }
        return plan == "lifetime" ? L("永久有效") : isTrialExpired ? L("试用订阅已到期") : L("永久免费")
    }
    var trialExpiryMessage: String? {
        guard isTrialExpired, let expiry = featureExpiresAtDate else { return nil }
        return L("试用订阅已于 {0} 到期，S3 / WebDAV 多端同步已停用。本地书库及自备存储中的数据不会删除；购买买断订阅可永久恢复同步功能。", Self.day(expiry))
    }
    private static func date(_ text: String?) -> Date? {
        guard let text else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: text) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: text)
    }
    private static func day(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyy/MM/dd"
        return formatter.string(from: date)
    }
}
struct DeviceFeatureStatus: Decodable {
    let membership: AccountMembership; let devices: [AccountDevice]; let currentDeviceId: String
    let isCurrentDeviceActive: Bool; let canUseFeatures: Bool
    let canUseAccountCloudSync: Bool?
}
struct SyncForm: Codable {
    var provider = 0
    var providerChosenByUser = false
    var enabled = false
    var automaticSyncEnabled = true
    var downloadBookFilesOnSync = false
    var endpoint = ""
    var accessKey = ""
    var secretKey = ""
    var webDavEndpoint = ""
    var webDavUsername = ""
    var webDavPassword = ""
    var bucket = ""
    var region = "us-east-1"
    var pathStyle = false
    var skipTlsVerify = false
    var prefix = "libzora"
    var encryptionKey = ""
    var accountRegion = 0
    var accountServerUrl = ""
    var accountUserId = "00000000-0000-0000-0000-000000000000"
}
struct SyncFormSnapshot: Decodable { let settings: SyncForm; let summary: String }

struct AiForm: Codable {
    var provider = "deepseek"
    var baseUrl = "https://api.deepseek.com"
    var model = ""
    var apiKey = ""
}
struct AiDefaults: Decodable { let baseUrl: String; let model: String }

struct EmailForm: Codable {
    var kindleEmailAddress = ""
    var senderEmailAddress = ""
    var smtpHost = ""
    var smtpPort = 587
    var smtpUsername = ""
    var smtpPassword = ""
    var enableSsl = true
}

struct MaterialRecord: Decodable, Identifiable {
    let id: String
    let bookId: String
    let bookTitle: String
    let kind: String
    let quote: String
    let note: String
    let locationText: String
    let updatedAt: String
    let location: JSONValue
    var kindLabel: String { kind == "note" ? L("笔记") : kind == "bookmark" ? L("书签") : L("划线") }
}

struct DashboardBook: Decodable, Identifiable {
    let bookId: String
    let title: String
    let progressPercent: Double
    let cumulativeSeconds: Int64
    let isInLibrary: Bool
    var id: String { bookId }
}
struct ReadingDay: Decodable, Identifiable { let date: String; let activeSeconds: Int64; var id: String { date } }
struct ReadingDashboard: Decodable {
    let booksStarted: Int
    let booksFinished: Int
    let totalSeconds: Int64
    let averageProgress: Double
    let bookmarkCount: Int
    let annotationCount: Int
    let recentBooks: [DashboardBook]
    let dailyReading: [ReadingDay]
    let books: [DashboardBook]
    let mostReadBooks: [DashboardBook]
}
struct Medal: Decodable, Identifiable {
    let name: String
    let rule: String
    let icon: String
    let isUnlocked: Bool
    let fraction: Double
    var id: String { name }
}
struct ReadingPeriodBucket: Decodable, Identifiable {
    let startDate: String; let endDate: String; let activeSeconds: Int64
    var id: String { startDate }
    func contains(_ date: String) -> Bool { date >= startDate && date <= endDate }
}
struct DashboardSnapshot: Decodable {
    let dashboard: ReadingDashboard; let medals: [Medal]; let streak: Int
    let period: Int; let today: String; let buckets: [ReadingPeriodBucket]
}
struct TrashRecord: Decodable, Identifiable { let id: String; let title: String; let detail: String; let deletedAt: String }

struct ReaderToc: Decodable, Identifiable { let id: Int; let title: String; let level: Int; let selected: Bool }
struct ReaderSearchHit: Decodable, Identifiable { let id: Int; let title: String; let excerpt: String }
struct ReaderDialog: Decodable, Equatable { let kind: String; let title: String; let text: String; let annotationId: String?; let note: String? }
struct ReaderMedia: Decodable, Identifiable, Equatable { let id: String; let kind: String; let title: String; let path: String }
struct SpeechVoice: Decodable, Identifiable { let id: String; let name: String; let culture: String? }
struct SpeechForm: Decodable { var voice: String; var speed: Double; var autoAdvance: Bool }
struct SpeechSnapshot: Decodable { let settings: SpeechForm; let voices: [SpeechVoice] }
struct DictionaryRecord: Decodable, Identifiable { let id: String; let name: String; let entryCount: Int; let enabled: Bool }
struct DictionaryEntry: Decodable { let term: String; let definition: String; let dictionaryName: String }
struct WikipediaArticle: Decodable { let title: String; let extract: String; let url: URL; let language: String }
struct TxtReview: Decodable, Identifiable { let id: Int64; let name: String }
struct TxtRules: Codable, Equatable { var encoding = 0; var headingPreset = 0; var customHeadingPattern = ""; var recognizeVolumes = true }
struct TxtChapter: Decodable, Identifiable { let number: Int; let title: String; let isVolume: Bool; var id: Int { number } }
struct TxtPreview: Decodable { let chapterCount: Int; let detectedEncoding: String; let chapters: [TxtChapter]; let hasPreviousPage: Bool; let hasNextPage: Bool }

/// Account storage and transfer sizes in the desktop's format, such as "1.5 GB".
func memberSize(_ bytes: Int64) -> String {
    let value = max(0, bytes)
    let (amount, unit): (Double, String) = value >= 1_073_741_824 ? (Double(value) / 1_073_741_824, "GB")
        : value >= 1_048_576 ? (Double(value) / 1_048_576, "MB")
        : value >= 1024 ? (Double(value) / 1024, "KB") : (Double(value), "B")
    let formatter = NumberFormatter()
    formatter.locale = Locale(identifier: "en_US_POSIX")
    formatter.numberStyle = .decimal
    formatter.minimumFractionDigits = 0; formatter.maximumFractionDigits = 2
    formatter.usesGroupingSeparator = false
    return (formatter.string(from: NSNumber(value: amount)) ?? String(amount)) + " " + unit
}

/// The desktop usage ring's percentage, including its "<0.1%" floor.
func memberPercent(_ ratio: Double) -> String {
    let percent = max(0, ratio) * 100
    if percent > 0 && percent < 0.1 { return "<0.1%" }
    let text = String(format: "%.1f", percent)
    return (text.hasSuffix(".0") ? String(text.dropLast(2)) : text) + "%"
}

func readingDuration(_ seconds: Int64) -> String {
    seconds < 3600 ? String(format: L("%.0f 分钟"), Double(seconds) / 60) : String(format: L("%.1f 小时"), Double(seconds) / 3600)
}
