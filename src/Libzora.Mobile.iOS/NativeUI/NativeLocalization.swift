import Foundation

/// The managed UI and SwiftUI share source keys, translations and language codes.
/// Only interface text is passed here, never book text or user-authored content.
enum NativeLocalization {
    private static let lock = NSLock()
    private static var currentLanguage = resolvedLanguage("auto")
    private static let english = loadCatalog()
    private static let placeholders = try! NSRegularExpression(pattern: #"\{([0-9]+)\}"#)

    static var language: String {
        lock.lock(); defer { lock.unlock() }
        return currentLanguage
    }
    static var locale: Locale { Locale(identifier: language) }

    static func setLanguage(_ language: String) {
        let normalized = resolvedLanguage(language)
        lock.lock(); defer { lock.unlock() }
        currentLanguage = normalized
    }

    static func resolvedLanguage(_ preference: String, preferredLanguages: [String] = Locale.preferredLanguages) -> String {
        func supported(_ value: String) -> String? {
            let code = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                .replacingOccurrences(of: "_", with: "-")
            if code == "zh" || code.hasPrefix("zh-") { return "zh-CN" }
            if code == "en" || code.hasPrefix("en-") { return "en-US" }
            return nil
        }
        if let manual = supported(preference) { return manual }
        return preferredLanguages.compactMap(supported).first ?? "en-US"
    }

    static func text(_ source: String, arguments: [Any] = []) -> String {
        let template = language == "en-US" ? english[source] ?? source : source
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

    private static func loadCatalog() -> [String: String] {
        func shared(_ language: String) -> [String: String] {
            guard let url = Bundle.main.url(forResource: "Shared." + language, withExtension: "axaml", subdirectory: "Localization"),
                  let parser = XMLParser(contentsOf: url) else { return [:] }
            let collector = SharedStringCollector()
            parser.delegate = collector
            parser.shouldResolveExternalEntities = false
            return parser.parse() ? collector.values : [:]
        }
        let chinese = shared("zh-CN"), english = shared("en-US")
        var values: [String: String] = [:]
        // Stable order mirrors the managed catalog's resource order.
        for key in chinese.keys.sorted() {
            if let source = chinese[key], let translation = english[key], values[source] == nil {
                values[source] = translation
            }
        }
        let overrides = (Bundle.main.urls(forResourcesWithExtension: "json", subdirectory: "Localization") ?? [])
            .filter { $0.lastPathComponent.hasSuffix(".en-US.json") }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        for url in overrides {
            guard let data = try? Data(contentsOf: url),
                  let translations = try? JSONSerialization.jsonObject(with: data) as? [String: String] else { continue }
            values.merge(translations) { _, value in value }
        }
        return values
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
