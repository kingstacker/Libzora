import Foundation

/// The managed UI and SwiftUI share source keys, translations and language codes.
/// Only interface text is passed here, never book text or user-authored content.
enum NativeLocalization {
    static let supportedLanguages: [(id: String, title: String)] = [
        ("zh-CN", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("en-US", "English"),
        ("ja-JP", "日本語"),
        ("de-DE", "Deutsch"),
        ("fr-FR", "Français"),
        ("ko-KR", "한국어"),
        ("es-ES", "Español"),
        ("it-IT", "Italiano"),
        ("pt-BR", "Português (Brasil)")
    ]

    private static let lock = NSLock()
    private static var currentLanguage = resolvedLanguage("auto")
    private static let catalogs = loadCatalogs()
    private static let placeholders = try! NSRegularExpression(pattern: #"\{([0-9]+)\}"#)

    static var language: String {
        lock.lock(); defer { lock.unlock() }
        return currentLanguage
    }
    static var locale: Locale { Locale(identifier: language) }
    static var isCjk: Bool { ["zh-CN", "zh-Hant", "ja-JP", "ko-KR"].contains(language) }
    static var usesVerticalColumns: Bool { ["zh-CN", "zh-Hant", "ja-JP"].contains(language) }

    static func displayName(for id: String) -> String {
        if id == "auto" { return L("跟随系统") }
        return supportedLanguages.first { $0.id == id }?.title ?? L("跟随系统")
    }

    static func setLanguage(_ language: String) {
        let normalized = resolvedLanguage(language)
        lock.lock(); defer { lock.unlock() }
        currentLanguage = normalized
    }

    static func resolvedLanguage(_ preference: String, preferredLanguages: [String] = Locale.preferredLanguages) -> String {
        func supported(_ value: String) -> String? {
            let code = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                .replacingOccurrences(of: "_", with: "-")
            if code == "zh-hant" || code.hasPrefix("zh-hant-")
                || code == "zh-tw" || code.hasPrefix("zh-tw-")
                || code == "zh-hk" || code.hasPrefix("zh-hk-")
                || code == "zh-mo" || code.hasPrefix("zh-mo-") { return "zh-Hant" }
            if code == "zh" || code.hasPrefix("zh-") { return "zh-CN" }
            if code == "en" || code.hasPrefix("en-") { return "en-US" }
            if code == "ja" || code.hasPrefix("ja-") { return "ja-JP" }
            if code == "de" || code.hasPrefix("de-") { return "de-DE" }
            if code == "fr" || code.hasPrefix("fr-") { return "fr-FR" }
            if code == "ko" || code.hasPrefix("ko-") { return "ko-KR" }
            if code == "es" || code.hasPrefix("es-") { return "es-ES" }
            if code == "it" || code.hasPrefix("it-") { return "it-IT" }
            if code == "pt" || code.hasPrefix("pt-") { return "pt-BR" }
            return nil
        }
        if let manual = supported(preference) { return manual }
        return preferredLanguages.compactMap(supported).first ?? "en-US"
    }

    static func text(_ source: String, arguments: [Any] = []) -> String {
        let active = language
        let template: String
        if active == "zh-CN" {
            template = source
        } else {
            template = catalogs[active]?[source] ?? catalogs["en-US"]?[source] ?? source
        }
        guard !arguments.isEmpty else { return template }
        let matches = placeholders.matches(in: template, range: NSRange(template.startIndex..., in: template))
        var result = template
        // Replace template tokens once, backwards. Braces in a book title or
        // other argument must never become another formatting instruction.
        for match in matches.reversed() {
            guard let numberRange = Range(match.range(at: 1), in: template),
                  let index = Int(template[numberRange]), arguments.indices.contains(index),
                  let range = Range(match.range, in: result) else { continue }
            result.replaceSubrange(range, with: String(describing: arguments[index]))
        }
        return result
    }

    private static func loadCatalogs() -> [String: [String: String]] {
        func shared(_ language: String) -> [String: String] {
            guard let url = Bundle.main.url(forResource: "Shared." + language, withExtension: "axaml", subdirectory: "Localization"),
                  let parser = XMLParser(contentsOf: url) else { return [:] }
            let collector = SharedStringCollector()
            parser.delegate = collector
            parser.shouldResolveExternalEntities = false
            return parser.parse() ? collector.values : [:]
        }
        let chinese = shared("zh-CN"), english = shared("en-US")
        var sharedEnglish: [String: String] = [:]
        // Stable order mirrors the managed catalog's resource order.
        for key in chinese.keys.sorted() {
            if let source = chinese[key], let translation = english[key], sharedEnglish[source] == nil {
                sharedEnglish[source] = translation
            }
        }
        var result: [String: [String: String]] = ["en-US": sharedEnglish]
        let files = (Bundle.main.urls(forResourcesWithExtension: "json", subdirectory: "Localization") ?? [])
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        for lang in supportedLanguages where lang.id != "zh-CN" {
            var values = result[lang.id] ?? [:]
            for url in files where url.lastPathComponent.hasSuffix(".\(lang.id).json") {
                guard let data = try? Data(contentsOf: url),
                      let translations = try? JSONSerialization.jsonObject(with: data) as? [String: String] else { continue }
                values.merge(translations) { _, value in value }
            }
            result[lang.id] = values
        }
        return result
    }
}

private final class SharedStringCollector: NSObject, XMLParserDelegate {
    var values: [String: String] = [:]
    private var key: String?
    private var text = ""
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes attributeDict: [String: String]) {
        if elementName == "x:String" { key = attributeDict["x:Key"]; text = "" }
    }
    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if key != nil { text += string }
    }
    func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) {
        if key != nil { text += String(decoding: CDATABlock, as: UTF8.self) }
    }
    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        if elementName == "x:String", let key { values[key] = text; self.key = nil }
    }
}

func L(_ source: String, _ arguments: Any...) -> String {
    NativeLocalization.text(source, arguments: arguments)
}
