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

    static var sample: KkSnapshot { sample(language: WidgetLocalization.current) }

    static func sample(isChinese: Bool) -> KkSnapshot {
        sample(language: isChinese ? .zhCN : .enUS)
    }

    static func sample(language: WidgetLanguage) -> KkSnapshot {
        let books = sampleBooks(language: language)
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

    private static func sampleBooks(language: WidgetLanguage) -> [KkBook] {
        let author = WidgetLocalization.sampleAuthor(language)
        return [
            KkBook(
                id: "6B1A2D5E-3C4F-4A6B-8C9D-0E1F2A3B4C5D",
                title: WidgetLocalization.sampleBookTitle(1, language),
                authors: author,
                progress: 42,
                cover: nil),
            KkBook(
                id: "7C2B3E6F-4D5A-4B7C-9DAE-1F2A3B4C5D6E",
                title: WidgetLocalization.sampleBookTitle(2, language),
                authors: author,
                progress: 68,
                cover: nil),
            KkBook(
                id: "8D3C4F7A-5E6B-4C8D-AEBF-2A3B4C5D6E7F",
                title: WidgetLocalization.sampleBookTitle(3, language),
                authors: author,
                progress: 12,
                cover: nil),
            KkBook(
                id: "9E4D5A8B-6F7C-4D9E-BFC0-3B4C5D6E7F80",
                title: WidgetLocalization.sampleBookTitle(4, language),
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

enum WidgetLanguage: String {
    case zhCN = "zh-CN"
    case zhHant = "zh-Hant"
    case enUS = "en-US"
    case jaJP = "ja-JP"
    case deDE = "de-DE"
    case frFR = "fr-FR"
    case koKR = "ko-KR"
    case esES = "es-ES"
    case itIT = "it-IT"
    case ptBR = "pt-BR"

    var isChinese: Bool { self == .zhCN || self == .zhHant }

    var localeIdentifier: String {
        switch self {
        case .zhCN: return "zh_Hans_CN"
        case .zhHant: return "zh_Hant_TW"
        case .enUS: return "en_US_POSIX"
        case .jaJP: return "ja_JP"
        case .deDE: return "de_DE"
        case .frFR: return "fr_FR"
        case .koKR: return "ko_KR"
        case .esES: return "es_ES"
        case .itIT: return "it_IT"
        case .ptBR: return "pt_BR"
        }
    }
}

enum WidgetLocalization {
    static var current: WidgetLanguage {
        resolve(Locale.preferredLanguages)
    }

    static func resolve(_ preferred: [String]) -> WidgetLanguage {
        for raw in preferred {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty { continue }
            let lower = trimmed.replacingOccurrences(of: "_", with: "-").lowercased()
            if lower.hasPrefix("zh") {
                if lower.contains("hant") || lower.contains("-tw") || lower.contains("-hk") || lower.contains("-mo") {
                    return .zhHant
                }
                return .zhCN
            }
            if lower.hasPrefix("ja") { return .jaJP }
            if lower.hasPrefix("de") { return .deDE }
            if lower.hasPrefix("fr") { return .frFR }
            if lower.hasPrefix("ko") { return .koKR }
            if lower.hasPrefix("es") { return .esES }
            if lower.hasPrefix("it") { return .itIT }
            if lower.hasPrefix("pt") { return .ptBR }
            if lower.hasPrefix("en") { return .enUS }
        }
        return .enUS
    }

    static func pick(_ lang: WidgetLanguage = current, _ table: [WidgetLanguage: String]) -> String {
        table[lang] ?? table[.enUS] ?? ""
    }

    static func sampleAuthor(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "示例作者", .zhHant: "範例作者", .enUS: "Sample Author", .jaJP: "サンプル著者",
            .deDE: "Beispielautor", .frFR: "Auteur exemple", .koKR: "샘플 저자",
            .esES: "Autor de ejemplo", .itIT: "Autore esempio", .ptBR: "Autor de exemplo"
        ])
    }

    static func sampleBookTitle(_ index: Int, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN:
            let names = ["示例书籍一", "示例书籍二", "示例书籍三", "示例书籍四"]
            return names[max(0, min(names.count - 1, index - 1))]
        case .zhHant:
            let names = ["範例書籍一", "範例書籍二", "範例書籍三", "範例書籍四"]
            return names[max(0, min(names.count - 1, index - 1))]
        case .jaJP: return "サンプル本 \(index)"
        case .deDE: return "Beispielbuch \(index)"
        case .frFR: return "Livre exemple \(index)"
        case .koKR: return "샘플 도서 \(index)"
        case .esES: return "Libro ejemplo \(index)"
        case .itIT: return "Libro esempio \(index)"
        case .ptBR: return "Livro exemplo \(index)"
        case .enUS: return "Sample Book \(index)"
        }
    }

    static func continueTitle(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "继续阅读", .zhHant: "繼續閱讀", .enUS: "Continue Reading", .jaJP: "続きを読む",
            .deDE: "Weiterlesen", .frFR: "Continuer", .koKR: "계속 읽기",
            .esES: "Seguir leyendo", .itIT: "Continua", .ptBR: "Continuar"
        ])
    }

    static func continueShort(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "继续阅读", .zhHant: "繼續閱讀", .enUS: "Continue", .jaJP: "続きを読む",
            .deDE: "Weiterlesen", .frFR: "Continuer", .koKR: "계속 읽기",
            .esES: "Seguir", .itIT: "Continua", .ptBR: "Continuar"
        ])
    }

    static func continueDescription(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "在读的几本书，点哪一本就从哪一本继续。",
            .zhHant: "正在閱讀的書籍，點擊即可繼續閱讀。",
            .enUS: "Jump right back into the books you are reading.",
            .jaJP: "読書中の本にすぐ戻って続きから読めます。",
            .deDE: "Direkt dort weiterlesen, wo Sie aufgehört haben.",
            .frFR: "Reprenez directement les livres que vous lisez.",
            .koKR: "읽고 있던 책으로 바로 돌아가 이어 읽습니다.",
            .esES: "Vuelve al instante a los libros que estás leyendo.",
            .itIT: "Riprendi subito i libri che stai leggendo.",
            .ptBR: "Volte direto para os livros que você está lendo."
        ])
    }

    static func statsTitle(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "阅读数据", .zhHant: "閱讀數據", .enUS: "Reading Stats", .jaJP: "読書統計",
            .deDE: "Lesestatistik", .frFR: "Statistiques", .koKR: "독서 통계",
            .esES: "Estadísticas", .itIT: "Statistiche", .ptBR: "Estatísticas"
        ])
    }

    static func statsDescription(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "本周阅读时长、连续天数、读完的书与笔记数量。",
            .zhHant: "本週閱讀時長、連續天數、讀完的書與筆記數量。",
            .enUS: "Track daily reading time, streak, finished books, and notes.",
            .jaJP: "毎日の読書時間、連続日数、読了冊数、メモ数を表示します。",
            .deDE: "Lesezeit, Serie, beendete Bücher und Notizen im Blick.",
            .frFR: "Suivez temps de lecture, série, livres lus et notes.",
            .koKR: "일일 독서 시간, 연속 기록, 완독 수와 메모를 확인합니다.",
            .esES: "Sigue tu tiempo diario, racha, libros terminados y notas.",
            .itIT: "Segui tempo giornaliero, serie, libri letti e note.",
            .ptBR: "Acompanhe tempo diário, sequência, livros lidos e notas."
        ])
    }

    static func treeTitle(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "阅读之树", .zhHant: "閱讀之樹", .enUS: "Reading Tree", .jaJP: "読書の木",
            .deDE: "Lesebaum", .frFR: "Arbre de lecture", .koKR: "독서 나무",
            .esES: "Árbol lector", .itIT: "Albero lettura", .ptBR: "Árvore leitura"
        ])
    }

    static func treeDescription(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "累计阅读浇灌小树苗，枝繁叶茂、开花结果。",
            .zhHant: "累計閱讀澆灌小樹苗，枝繁葉茂、開花結果。",
            .enUS: "Watch your reading tree grow, blossom, and bear fruit.",
            .jaJP: "累積読書時間で木が育ち、花が咲き実を結びます。",
            .deDE: "Sieh deinem Lesebaum beim Wachsen, Blühen und Fruchten zu.",
            .frFR: "Regardez votre arbre grandir, fleurir et porter des fruits.",
            .koKR: "누적 독서로 나무가 자라고 꽃피며 열매를 맺습니다.",
            .esES: "Ve crecer, florecer y dar fruto a tu árbol de lectura.",
            .itIT: "Guarda il tuo albero crescere, fiorire e dare frutti.",
            .ptBR: "Veja sua árvore de leitura crescer, florescer e dar frutos."
        ])
    }

    static func continueBook(_ title: String, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN: return "继续阅读《\(title)》"
        case .zhHant: return "繼續閱讀《\(title)》"
        case .jaJP: return "続きを読む: \(title)"
        case .deDE: return "Weiterlesen: \(title)"
        case .frFR: return "Continuer \(title)"
        case .koKR: return "계속 읽기: \(title)"
        case .esES: return "Seguir con \(title)"
        case .itIT: return "Continua \(title)"
        case .ptBR: return "Continuar \(title)"
        case .enUS: return "Continue \(title)"
        }
    }

    static func openToReadShort(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "打开 Libzora 开始阅读", .zhHant: "打開 Libzora 開始閱讀", .enUS: "Open Libzora to read",
            .jaJP: "Libzoraを開いて読書", .deDE: "Libzora öffnen", .frFR: "Ouvrir Libzora",
            .koKR: "Libzora 열어 읽기", .esES: "Abre Libzora para leer",
            .itIT: "Apri Libzora per leggere", .ptBR: "Abra o Libzora para ler"
        ])
    }

    static func continueEmptyMessage(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "打开 Libzora，开始阅读", .zhHant: "打開 Libzora，開始閱讀", .enUS: "Open Libzora to start reading",
            .jaJP: "Libzoraを開いて読書を始めましょう", .deDE: "Libzora öffnen und loslesen",
            .frFR: "Ouvrez Libzora pour commencer", .koKR: "Libzora를 열어 독서를 시작하세요",
            .esES: "Abre Libzora para empezar a leer", .itIT: "Apri Libzora per iniziare",
            .ptBR: "Abra o Libzora para começar"
        ])
    }

    static func emptyShelfTitle(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "书架还空着", .zhHant: "書架還空著", .enUS: "Empty Shelf",
            .jaJP: "本棚が空です", .deDE: "Regal leer", .frFR: "Étagère vide",
            .koKR: "책장이 비어 있습니다", .esES: "Estante vacío",
            .itIT: "Libreria vuota", .ptBR: "Estante vazia"
        ])
    }

    static func emptyShelfMessage(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "导入一本书，开始阅读", .zhHant: "匯入一本書，開始閱讀", .enUS: "Import a book to start reading",
            .jaJP: "本を取り込んで読書を始めましょう", .deDE: "Buch importieren und loslesen",
            .frFR: "Importez un livre pour lire", .koKR: "책을 가져와 독서를 시작하세요",
            .esES: "Importa un libro para leer", .itIT: "Importa un libro per leggere",
            .ptBR: "Importe um livro para ler"
        ])
    }

    static func readPercent(_ pct: String, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN: return "已读 \(pct)"
        case .zhHant: return "已讀 \(pct)"
        case .jaJP: return "\(pct) 読了"
        case .deDE: return "\(pct) gelesen"
        case .frFR: return "\(pct) lu"
        case .koKR: return "\(pct) 읽음"
        case .esES: return "\(pct) leído"
        case .itIT: return "\(pct) letto"
        case .ptBR: return "\(pct) lido"
        case .enUS: return "\(pct) read"
        }
    }

    static func readLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "已读", .zhHant: "已讀", .enUS: "Read", .jaJP: "読了",
            .deDE: "Gelesen", .frFR: "Lu", .koKR: "읽음",
            .esES: "Leído", .itIT: "Letto", .ptBR: "Lido"
        ])
    }

    static func statsEmptyMessage(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "打开 Libzora，记录阅读时光", .zhHant: "打開 Libzora，記錄閱讀時光",
            .enUS: "Open Libzora to track your reading", .jaJP: "Libzoraを開いて読書を記録",
            .deDE: "Libzora öffnen, um Lesezeit zu erfassen", .frFR: "Ouvrez Libzora pour suivre vos lectures",
            .koKR: "Libzora를 열어 독서 시간을 기록하세요", .esES: "Abre Libzora para registrar tu lectura",
            .itIT: "Apri Libzora per tracciare la lettura", .ptBR: "Abra o Libzora para acompanhar"
        ])
    }

    static func weekDuration(_ formatted: String, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN: return "本周 \(formatted)"
        case .zhHant: return "本週 \(formatted)"
        case .jaJP: return "今週 \(formatted)"
        case .deDE: return "Woche \(formatted)"
        case .frFR: return "Sem. \(formatted)"
        case .koKR: return "이번 주 \(formatted)"
        case .esES: return "Semana \(formatted)"
        case .itIT: return "Sett. \(formatted)"
        case .ptBR: return "Semana \(formatted)"
        case .enUS: return "Week \(formatted)"
        }
    }

    static func finishedLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "已读完", .zhHant: "已讀完", .enUS: "Finished", .jaJP: "読了",
            .deDE: "Beendet", .frFR: "Terminés", .koKR: "완독",
            .esES: "Leídos", .itIT: "Finiti", .ptBR: "Concluídos"
        ])
    }

    static func bookmarksLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "书签", .zhHant: "書籤", .enUS: "Bookmarks", .jaJP: "しおり",
            .deDE: "Lesezeichen", .frFR: "Signets", .koKR: "북마크",
            .esES: "Marcadores", .itIT: "Segnalibri", .ptBR: "Marcadores"
        ])
    }

    static func notesLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "划线", .zhHant: "劃線", .enUS: "Notes", .jaJP: "メモ",
            .deDE: "Notizen", .frFR: "Notes", .koKR: "메모",
            .esES: "Notas", .itIT: "Note", .ptBR: "Notas"
        ])
    }

    static func notesDetailedLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "划线笔记", .zhHant: "劃線筆記", .enUS: "Notes", .jaJP: "メモ・ハイライト",
            .deDE: "Notizen", .frFR: "Notes", .koKR: "하이라이트",
            .esES: "Notas", .itIT: "Note", .ptBR: "Notas"
        ])
    }

    static func thisWeekReading(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "本周阅读", .zhHant: "本週閱讀", .enUS: "This Week", .jaJP: "今週の読書",
            .deDE: "Diese Woche", .frFR: "Cette semaine", .koKR: "이번 주",
            .esES: "Esta semana", .itIT: "Questa sett.", .ptBR: "Esta semana"
        ])
    }

    static func totalTime(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "累计阅读", .zhHant: "累計閱讀", .enUS: "Total Time", .jaJP: "累計読書",
            .deDE: "Gesamtzeit", .frFR: "Temps total", .koKR: "누적 시간",
            .esES: "Tiempo total", .itIT: "Tempo totale", .ptBR: "Tempo total"
        ])
    }

    static func thisWeekDaily(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "本周每日", .zhHant: "本週每日", .enUS: "This Week", .jaJP: "今週の推移",
            .deDE: "Diese Woche", .frFR: "Cette semaine", .koKR: "이번 주",
            .esES: "Esta semana", .itIT: "Questa sett.", .ptBR: "Esta semana"
        ])
    }

    static func startedLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "开始阅读", .zhHant: "開始閱讀", .enUS: "Started", .jaJP: "読書中",
            .deDE: "Begonnen", .frFR: "Commencés", .koKR: "시작함",
            .esES: "Empezados", .itIT: "Iniziati", .ptBR: "Iniciados"
        ])
    }

    static func todayLabel(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "今日阅读", .zhHant: "今日閱讀", .enUS: "Today", .jaJP: "今日",
            .deDE: "Heute", .frFR: "Aujourd'hui", .koKR: "오늘",
            .esES: "Hoy", .itIT: "Oggi", .ptBR: "Hoje"
        ])
    }

    static func streakBadge(_ days: Int, _ lang: WidgetLanguage = current) -> String {
        let count = max(0, days)
        switch lang {
        case .zhCN: return "连续 \(count) 天"
        case .zhHant: return "連續 \(count) 天"
        case .jaJP: return "\(count)日連続"
        case .deDE: return "\(count) T. Serie"
        case .frFR: return "Série \(count)j"
        case .koKR: return "\(count)일 연속"
        case .esES: return "Racha \(count)d"
        case .itIT: return "Serie \(count)g"
        case .ptBR: return "Série \(count)d"
        case .enUS: return "\(count)d streak"
        }
    }

    static func shortDays(_ days: Int, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN, .zhHant: return "\(days)天"
        case .jaJP: return "\(days)日"
        case .deDE: return "\(days)T"
        case .frFR: return "\(days)j"
        case .koKR: return "\(days)일"
        case .itIT: return "\(days)g"
        case .enUS, .esES, .ptBR: return "\(days)d"
        }
    }

    static func treeEmptyMessage(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "打开 Libzora，种下一棵书树", .zhHant: "打開 Libzora，種下一棵書樹",
            .enUS: "Open Libzora to plant your tree", .jaJP: "Libzoraを開いて木を植えましょう",
            .deDE: "Libzora öffnen und Baum pflanzen", .frFR: "Ouvrez Libzora pour planter l'arbre",
            .koKR: "Libzora를 열어 나무를 심어보세요", .esES: "Abre Libzora para plantar tu árbol",
            .itIT: "Apri Libzora per piantare l'albero", .ptBR: "Abra o Libzora para plantar sua árvore"
        ])
    }

    static func treeBooksCount(_ countText: String, rawCount: Int, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN, .zhHant: return "\(countText) 本"
        case .jaJP: return "\(countText) 冊"
        case .deDE: return rawCount == 1 ? "1 Buch" : "\(countText) Bücher"
        case .frFR: return rawCount == 1 ? "1 livre" : "\(countText) livres"
        case .koKR: return "\(countText)권"
        case .esES: return rawCount == 1 ? "1 libro" : "\(countText) libros"
        case .itIT: return rawCount == 1 ? "1 libro" : "\(countText) libri"
        case .ptBR: return rawCount == 1 ? "1 livro" : "\(countText) livros"
        case .enUS: return rawCount == 1 ? "1 book" : "\(countText) books"
        }
    }

    static func totalShort(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "累计", .zhHant: "累計", .enUS: "Total", .jaJP: "累計",
            .deDE: "Gesamt", .frFR: "Total", .koKR: "누적",
            .esES: "Total", .itIT: "Totale", .ptBR: "Total"
        ])
    }

    static func treeNotes(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "批注", .zhHant: "批注", .enUS: "Notes", .jaJP: "メモ",
            .deDE: "Notizen", .frFR: "Notes", .koKR: "메모",
            .esES: "Notas", .itIT: "Note", .ptBR: "Notas"
        ])
    }

    static func readToday(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "今日已读", .zhHant: "今日已讀", .enUS: "Read today", .jaJP: "今日読了",
            .deDE: "Heute gelesen", .frFR: "Lu auj.", .koKR: "오늘 읽음",
            .esES: "Leído hoy", .itIT: "Letto oggi", .ptBR: "Lido hoje"
        ])
    }

    static func evergreen(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "长青不息", .zhHant: "長青不息", .enUS: "Evergreen", .jaJP: "常緑",
            .deDE: "Immergrün", .frFR: "Persistant", .koKR: "상록수",
            .esES: "Perenne", .itIT: "Sempreverde", .ptBR: "Sempre-verde"
        ])
    }

    static func nextStageHint(_ formatted: String, target: String, _ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN: return "距「\(target)」还差 \(formatted)"
        case .zhHant: return "距「\(target)」還差 \(formatted)"
        case .jaJP: return "「\(target)」まであと \(formatted)"
        case .deDE: return "Noch \(formatted) bis \(target)"
        case .frFR: return "\(formatted) avant \(target)"
        case .koKR: return "'\(target)'까지 \(formatted)"
        case .esES: return "\(formatted) para \(target)"
        case .itIT: return "\(formatted) a \(target)"
        case .ptBR: return "\(formatted) para \(target)"
        case .enUS: return "\(formatted) to \(target)"
        }
    }

    static func minuteUnit(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "分钟", .zhHant: "分鐘", .enUS: "min", .jaJP: "分",
            .deDE: "Min", .frFR: "min", .koKR: "분",
            .esES: "min", .itIT: "min", .ptBR: "min"
        ])
    }

    static func hourUnit(_ lang: WidgetLanguage = current) -> String {
        pick(lang, [
            .zhCN: "小时", .zhHant: "小時", .enUS: "hr", .jaJP: "時間",
            .deDE: "Std", .frFR: "h", .koKR: "시간",
            .esES: "h", .itIT: "h", .ptBR: "h"
        ])
    }

    static func zeroMinutes(_ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN: return "0 分钟"
        case .zhHant: return "0 分鐘"
        case .jaJP: return "0分"
        case .deDE: return "0 Min"
        case .koKR: return "0분"
        default: return "0 min"
        }
    }

    static func lessThanMinute(_ lang: WidgetLanguage = current) -> String {
        switch lang {
        case .zhCN: return "不到 1 分钟"
        case .zhHant: return "不到 1 分鐘"
        case .jaJP: return "1分未満"
        case .deDE: return "<1 Min"
        case .koKR: return "1분 미만"
        default: return "<1 min"
        }
    }
}

enum KkFormat {
    static var isChinese: Bool {
        Locale.preferredLanguages.first?.hasPrefix("zh") == true
    }

    static func duration(_ seconds: Int64) -> String {
        duration(seconds, language: WidgetLocalization.current)
    }

    static func duration(_ seconds: Int64, language: WidgetLanguage) -> String {
        let seconds = max(0, seconds)
        if seconds == 0 { return WidgetLocalization.zeroMinutes(language) }
        let hours = Int(seconds / 3_600)
        let minutes = Int((seconds % 3_600) / 60)
        let minUnit = WidgetLocalization.minuteUnit(language)
        let hrUnit = WidgetLocalization.hourUnit(language)
        if hours > 0 && minutes > 0 {
            switch language {
            case .zhCN: return "\(hours) 小时 \(minutes) 分"
            case .zhHant: return "\(hours) 小時 \(minutes) 分"
            case .jaJP: return "\(hours)時間\(minutes)分"
            case .koKR: return "\(hours)시간 \(minutes)분"
            default: return "\(hours) \(hrUnit) \(minutes) \(minUnit)"
            }
        }
        if hours > 0 {
            return language == .jaJP || language == .koKR ? "\(hours)\(hrUnit)" : "\(hours) \(hrUnit)"
        }
        if minutes > 0 {
            return language == .jaJP || language == .koKR ? "\(minutes)\(minUnit)" : "\(minutes) \(minUnit)"
        }
        return WidgetLocalization.lessThanMinute(language)
    }

    static func compactHoursParts(_ seconds: Int64, isChinese: Bool = KkFormat.isChinese) -> (value: String, unit: String) {
        compactHoursParts(seconds, language: isChinese ? .zhCN : .enUS)
    }

    static func localizedCompactHoursParts(_ seconds: Int64, language: WidgetLanguage = WidgetLocalization.current) -> (value: String, unit: String) {
        compactHoursParts(seconds, language: language)
    }

    static func compactHoursParts(_ seconds: Int64, language: WidgetLanguage) -> (value: String, unit: String) {
        let clamped = max(0, seconds)
        let minUnit = WidgetLocalization.minuteUnit(language)
        let hrUnit = WidgetLocalization.hourUnit(language)
        if clamped == 0 { return ("0", minUnit) }
        if clamped < 60 { return ("<1", minUnit) }
        let hours = Double(clamped) / 3_600
        if hours < 1 {
            let minutes = Int(clamped / 60)
            return ("\(minutes)", minUnit)
        }
        let roundedTenths = (hours * 10).rounded() / 10
        let value: String
        if roundedTenths >= 10 || roundedTenths == roundedTenths.rounded() {
            value = String(format: "%.0f", roundedTenths)
        } else {
            value = String(format: "%.1f", roundedTenths)
        }
        return (value, hrUnit)
    }

    static func compactHours(_ seconds: Int64, isChinese: Bool = KkFormat.isChinese) -> String {
        let parts = compactHoursParts(seconds, isChinese: isChinese)
        return "\(parts.value) \(parts.unit)"
    }

    static func localizedCompactHours(_ seconds: Int64, language: WidgetLanguage = WidgetLocalization.current) -> String {
        let parts = compactHoursParts(seconds, language: language)
        return language == .jaJP || language == .koKR ? "\(parts.value)\(parts.unit)" : "\(parts.value) \(parts.unit)"
    }

    static func compactCount(_ count: Int, isChinese: Bool = KkFormat.isChinese) -> String {
        compactCount(count, language: isChinese ? .zhCN : .enUS)
    }

    static func localizedCompactCount(_ count: Int, language: WidgetLanguage = WidgetLocalization.current) -> String {
        compactCount(count, language: language)
    }

    static func compactCount(_ count: Int, language: WidgetLanguage) -> String {
        let safe = max(0, count)
        if safe < 10_000 { return "\(safe)" }
        func formatScaled(_ value: Double, unit: String) -> String {
            let roundedTenths = (value * 10).rounded() / 10
            let text = roundedTenths >= 100 || roundedTenths == roundedTenths.rounded()
                ? String(format: "%.0f", roundedTenths)
                : String(format: "%.1f", roundedTenths)
            return "\(text)\(unit)"
        }
        switch language {
        case .zhCN, .zhHant: return formatScaled(Double(safe) / 10_000, unit: "万")
        case .jaJP: return formatScaled(Double(safe) / 10_000, unit: "万")
        case .koKR: return formatScaled(Double(safe) / 10_000, unit: "만")
        default:
            if safe >= 1_000_000 { return formatScaled(Double(safe) / 1_000_000, unit: "M") }
            return formatScaled(Double(safe) / 1_000, unit: "k")
        }
    }

    static func progress(_ value: Double) -> Int {
        Int(max(0, min(100, value)).rounded())
    }

    static func weekday(_ isoDate: String) -> String {
        guard let date = date(from: isoDate) else { return "" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: WidgetLocalization.current.localeIdentifier)
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
