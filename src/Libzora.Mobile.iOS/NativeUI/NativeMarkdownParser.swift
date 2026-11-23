import Foundation

// MARK: - Block Model

enum NativeMarkdownAlignment: Equatable {
    case leading, center, trailing
}

struct NativeMarkdownTable: Equatable {
    let headers: [String]
    let alignments: [NativeMarkdownAlignment]
    let rows: [[String]]
}

struct NativeMarkdownListItem: Equatable {
    let level: Int
    let marker: String
    let checked: Bool?
    let blocks: [NativeMarkdownBlock]

    var text: String {
        blocks.compactMap { block in
            if case let .paragraph(value) = block.kind { return value }
            return nil
        }.joined(separator: "\n")
    }
}

struct NativeMarkdownBlock: Identifiable, Equatable {
    enum Kind: Equatable {
        case heading(level: Int, text: String)
        case paragraph(String)
        case list([NativeMarkdownListItem])
        case code(language: String?, text: String)
        case math(String)
        case quote([NativeMarkdownBlock])
        case rule
        case table(NativeMarkdownTable)
        case image(alt: String, url: String)
    }

    let id: Int
    let kind: Kind
}

// MARK: - Inline Model

struct NativeMarkdownInlineStyle: OptionSet, Equatable {
    let rawValue: Int
    static let bold = NativeMarkdownInlineStyle(rawValue: 1 << 0)
    static let italic = NativeMarkdownInlineStyle(rawValue: 1 << 1)
    static let strikethrough = NativeMarkdownInlineStyle(rawValue: 1 << 2)
    static let code = NativeMarkdownInlineStyle(rawValue: 1 << 3)
}

enum NativeMarkdownInlineToken: Equatable {
    case text(String, style: NativeMarkdownInlineStyle, link: URL?)
    case citation(String)
    case math(latex: String, raw: String)
    case image(alt: String, url: String)
    case lineBreak
}

enum NativeMathScriptKind: Equatable {
    case normal
    case superscript
    case `subscript`
}

struct NativeMathRun: Equatable {
    let text: String
    let script: NativeMathScriptKind
}

// MARK: - Block Parser

struct NativeMarkdownParser {
    private var lines: [String]
    private var index = 0
    private var nextId = 0

    static func parse(_ source: String) -> [NativeMarkdownBlock] {
        let normalized = source
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        var parser = NativeMarkdownParser(lines: normalized.components(separatedBy: "\n"))
        return parser.parseBlocks()
    }

    private mutating func parseBlocks() -> [NativeMarkdownBlock] {
        var blocks: [NativeMarkdownBlock] = []
        while index < lines.count {
            let raw = lines[index]
            let trimmed = raw.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty {
                index += 1
                continue
            }
            if let opening = Self.fence(raw) {
                blocks.append(make(.code(language: opening.language, text: parseFence(opening.marker))))
                continue
            }
            if let math = parseDisplayMathBlock() {
                blocks.append(make(.math(math)))
                continue
            }
            if let heading = Self.heading(raw) {
                blocks.append(make(.heading(level: heading.level, text: heading.text)))
                index += 1
                continue
            }
            if Self.isRule(trimmed) {
                blocks.append(make(.rule))
                index += 1
                continue
            }
            if trimmed.contains("|"), index + 1 < lines.count, Self.isTableSeparator(lines[index + 1]) {
                blocks.append(make(.table(parseTable())))
                continue
            }
            if trimmed.hasPrefix(">") {
                blocks.append(make(.quote(parseQuote())))
                continue
            }
            if Self.listItem(raw) != nil {
                blocks.append(make(.list(parseList())))
                continue
            }
            if let image = Self.standaloneImage(trimmed) {
                blocks.append(make(.image(alt: image.alt, url: image.url)))
                index += 1
                continue
            }
            blocks.append(make(.paragraph(parseParagraph())))
        }
        return blocks
    }

    private mutating func make(_ kind: NativeMarkdownBlock.Kind) -> NativeMarkdownBlock {
        defer { nextId += 1 }
        return NativeMarkdownBlock(id: nextId, kind: kind)
    }

    private mutating func parseFence(_ marker: String) -> String {
        index += 1
        var body: [String] = []
        while index < lines.count {
            let candidate = lines[index].trimmingCharacters(in: .whitespaces)
            if candidate.hasPrefix(marker) && candidate.dropFirst(marker.count).trimmingCharacters(in: .whitespaces).isEmpty {
                index += 1
                break
            }
            body.append(lines[index])
            index += 1
        }
        return body.joined(separator: "\n")
    }

    private mutating func parseDisplayMathBlock() -> String? {
        let trimmed = lines[index].trimmingCharacters(in: .whitespaces)
        let delimiters: [(open: String, close: String)] = [("$$", "$$"), ("\\[", "\\]")]
        for (open, close) in delimiters where trimmed.hasPrefix(open) {
            let afterOpen = String(trimmed.dropFirst(open.count))
            if let closeRange = afterOpen.range(of: close),
               afterOpen[closeRange.upperBound...].trimmingCharacters(in: .whitespaces).isEmpty {
                let inner = String(afterOpen[..<closeRange.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
                index += 1
                return inner.isEmpty ? nil : inner
            }
            if afterOpen.trimmingCharacters(in: .whitespaces).isEmpty || !afterOpen.contains(close) {
                var body: [String] = []
                if !afterOpen.trimmingCharacters(in: .whitespaces).isEmpty {
                    body.append(afterOpen)
                }
                var look = index + 1
                var foundClose = false
                while look < lines.count {
                    let line = lines[look]
                    if let closeRange = line.range(of: close),
                       line[closeRange.upperBound...].trimmingCharacters(in: .whitespaces).isEmpty {
                        let beforeClose = String(line[..<closeRange.lowerBound])
                        if !beforeClose.trimmingCharacters(in: .whitespaces).isEmpty {
                            body.append(beforeClose)
                        }
                        foundClose = true
                        look += 1
                        break
                    }
                    body.append(line)
                    look += 1
                }
                if foundClose {
                    index = look
                    let joined = body.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
                    return joined.isEmpty ? nil : joined
                }
            }
        }
        return nil
    }

    private mutating func parseQuote() -> [NativeMarkdownBlock] {
        var inner: [String] = []
        while index < lines.count {
            let trimmed = lines[index].trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix(">") else { break }
            var content = String(trimmed.dropFirst())
            if content.hasPrefix(" ") { content.removeFirst() }
            inner.append(content)
            index += 1
        }
        return NativeMarkdownParser.parse(inner.joined(separator: "\n"))
    }

    private mutating func parseList() -> [NativeMarkdownListItem] {
        var items: [NativeMarkdownListItem] = []
        var baseIndent: Int?
        while index < lines.count {
            let raw = lines[index]
            let trimmed = raw.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty {
                var look = index + 1
                while look < lines.count && lines[look].trimmingCharacters(in: .whitespaces).isEmpty {
                    look += 1
                }
                if let baseIndent, look < lines.count {
                    if let next = Self.listItem(lines[look]), next.indent >= baseIndent {
                        index = look
                        continue
                    }
                    if Self.indent(of: lines[look]) > baseIndent {
                        index = look
                        continue
                    }
                }
                break
            }

            guard let item = Self.listItem(raw) else {
                // Indented continuation blocks (code fences, nested quotes, extra paragraphs)
                if let baseIndent, Self.indent(of: raw) > baseIndent, !items.isEmpty {
                    var extraLines: [String] = []
                    let stripWidth = baseIndent + 2
                    while index < lines.count {
                        let contRaw = lines[index]
                        let contTrimmed = contRaw.trimmingCharacters(in: .whitespaces)
                        if contTrimmed.isEmpty {
                            var look = index + 1
                            while look < lines.count && lines[look].trimmingCharacters(in: .whitespaces).isEmpty {
                                look += 1
                            }
                            if look < lines.count, Self.listItem(lines[look]) == nil, Self.indent(of: lines[look]) > baseIndent {
                                extraLines.append("")
                                index += 1
                                continue
                            }
                            break
                        }
                        if Self.listItem(contRaw) != nil || Self.indent(of: contRaw) <= baseIndent {
                            break
                        }
                        extraLines.append(Self.stripIndent(contRaw, upTo: stripWidth))
                        index += 1
                    }
                    if !extraLines.isEmpty {
                        let last = items[items.count - 1]
                        let mergedBlocks: [NativeMarkdownBlock]
                        let nested = NativeMarkdownParser.parse(extraLines.joined(separator: "\n"))
                        if case let .paragraph(existing) = last.blocks.last?.kind,
                           nested.count == 1,
                           case let .paragraph(addition) = nested[0].kind,
                           last.blocks.count == 1 {
                            mergedBlocks = [NativeMarkdownBlock(id: last.blocks[0].id, kind: .paragraph(existing + "\n" + addition))]
                        } else {
                            mergedBlocks = reindex(last.blocks + nested)
                        }
                        items[items.count - 1] = NativeMarkdownListItem(
                            level: last.level,
                            marker: last.marker,
                            checked: last.checked,
                            blocks: mergedBlocks)
                        continue
                    }
                }
                break
            }

            if baseIndent == nil { baseIndent = item.indent }
            guard let baseIndent, item.indent >= baseIndent else { break }
            let level = max(0, min(3, (item.indent - baseIndent) / 2))
            var text = item.text
            var checked: Bool?
            if !item.ordered {
                if text.hasPrefix("[ ]") {
                    checked = false
                    text = String(text.dropFirst(3)).trimmingCharacters(in: .whitespaces)
                } else if text.lowercased().hasPrefix("[x]") {
                    checked = true
                    text = String(text.dropFirst(3)).trimmingCharacters(in: .whitespaces)
                }
            }
            let itemBlocks = text.isEmpty ? [] : NativeMarkdownParser.parse(text)
            items.append(NativeMarkdownListItem(
                level: level,
                marker: item.ordered ? "\(item.number)." : "•",
                checked: checked,
                blocks: itemBlocks.isEmpty ? [NativeMarkdownBlock(id: 0, kind: .paragraph(text))] : itemBlocks))
            index += 1
        }
        return items
    }

    private func reindex(_ blocks: [NativeMarkdownBlock]) -> [NativeMarkdownBlock] {
        blocks.enumerated().map { NativeMarkdownBlock(id: $0.offset, kind: $0.element.kind) }
    }

    private static func stripIndent(_ line: String, upTo width: Int) -> String {
        var consumed = 0
        var idx = line.startIndex
        while idx < line.endIndex && consumed < width {
            if line[idx] == " " { consumed += 1 }
            else if line[idx] == "\t" { consumed += 4 }
            else { break }
            idx = line.index(after: idx)
        }
        return String(line[idx...])
    }

    private mutating func parseParagraph() -> String {
        var parts: [String] = []
        while index < lines.count {
            let raw = lines[index]
            let trimmed = raw.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty { break }
            if !parts.isEmpty, Self.startsBlock(raw) { break }
            if !parts.isEmpty, trimmed.contains("|"), index + 1 < lines.count,
               Self.isTableSeparator(lines[index + 1]) { break }
            parts.append(trimmed)
            index += 1
        }
        return parts.joined(separator: "\n")
    }

    private mutating func parseTable() -> NativeMarkdownTable {
        let headers = Self.tableCells(lines[index])
        let alignments = Self.tableCells(lines[index + 1]).map(Self.alignment(of:))
        index += 2
        var rows: [[String]] = []
        while index < lines.count {
            let raw = lines[index]
            let trimmed = raw.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty || !trimmed.contains("|") { break }
            rows.append(Self.tableCells(raw))
            index += 1
        }
        return NativeMarkdownTable(headers: headers, alignments: alignments, rows: rows)
    }

    // MARK: Line classification

    private static func fence(_ line: String) -> (marker: String, language: String?)? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard let first = trimmed.first, first == "`" || first == "~" else { return nil }
        let count = trimmed.prefix { $0 == first }.count
        guard count >= 3 else { return nil }
        let info = trimmed.dropFirst(count).trimmingCharacters(in: .whitespaces)
        if first == "`" && info.contains("`") { return nil }
        let language = info.split(separator: " ").first.map(String.init)
        return (String(repeating: String(first), count: count), language?.isEmpty == true ? nil : language)
    }

    private static func heading(_ line: String) -> (level: Int, text: String)? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        let level = trimmed.prefix { $0 == "#" }.count
        guard (1...6).contains(level) else { return nil }
        let rest = trimmed.dropFirst(level)
        guard rest.first == " " || rest.first == "\t" else { return nil }
        var text = rest.trimmingCharacters(in: .whitespaces)
        if let lastSpace = text.lastIndex(where: { $0 == " " || $0 == "\t" }) {
            let suffix = text[text.index(after: lastSpace)...]
            if !suffix.isEmpty && suffix.allSatisfy({ $0 == "#" }) {
                text = String(text[..<lastSpace]).trimmingCharacters(in: .whitespaces)
            }
        }
        return text.isEmpty ? nil : (level, text)
    }

    private static func isRule(_ line: String) -> Bool {
        let compact = line.replacingOccurrences(of: " ", with: "")
        guard compact.count >= 3, let first = compact.first, "-*_".contains(first) else { return false }
        return compact.allSatisfy { $0 == first }
    }

    private static func startsBlock(_ line: String) -> Bool {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        return fence(line) != nil
            || isStandaloneDisplayMathStart(trimmed)
            || heading(line) != nil
            || isRule(trimmed)
            || trimmed.hasPrefix(">")
            || listItem(line) != nil
            || standaloneImage(trimmed) != nil
    }

    private static func standaloneImage(_ trimmed: String) -> (alt: String, url: String)? {
        guard trimmed.hasPrefix("![") else { return nil }
        let chars = Array(trimmed)
        guard let parsed = NativeMarkdownInlineParser.parseImageBracketLink(chars, start: 0, end: chars.count),
              parsed.nextIndex == chars.count else {
            return nil
        }
        return (parsed.alt, parsed.url)
    }

    private static func isStandaloneDisplayMathStart(_ trimmed: String) -> Bool {
        trimmed.hasPrefix("$$") || trimmed.hasPrefix("\\[")
    }

    private static func indent(of line: String) -> Int {
        var width = 0
        for character in line {
            if character == " " { width += 1 }
            else if character == "\t" { width += 4 }
            else { break }
        }
        return width
    }

    private static func listItem(_ line: String) -> (indent: Int, ordered: Bool, number: Int, text: String)? {
        let text = line as NSString
        guard let match = listPattern.firstMatch(in: line, range: NSRange(location: 0, length: text.length)) else { return nil }
        let marker = text.substring(with: match.range(at: 2))
        let ordered = marker.first?.isNumber == true
        let number = ordered ? Int(marker.dropLast()) ?? 1 : 1
        return (
            indent(of: text.substring(with: match.range(at: 1))),
            ordered,
            number,
            text.substring(with: match.range(at: 3))
        )
    }

    private static let listPattern = try! NSRegularExpression(
        pattern: #"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$"#)

    private static func isTableSeparator(_ line: String) -> Bool {
        let cells = tableCells(line)
        guard !cells.isEmpty, line.contains("-") else { return false }
        return cells.allSatisfy {
            $0.range(of: #"^:?-{3,}:?$"#, options: .regularExpression) != nil
        }
    }

    private static func alignment(of cell: String) -> NativeMarkdownAlignment {
        let value = cell.trimmingCharacters(in: .whitespaces)
        let leading = value.hasPrefix(":")
        let trailing = value.hasSuffix(":")
        if leading && trailing { return .center }
        return trailing ? .trailing : .leading
    }

    private static func tableCells(_ line: String) -> [String] {
        var value = line.trimmingCharacters(in: .whitespaces)
        if value.hasPrefix("|") { value.removeFirst() }
        if value.hasSuffix("|"), !value.hasSuffix("\\|") { value.removeLast() }
        var cells: [String] = []
        var current = ""
        var escaped = false
        for character in value {
            if escaped {
                current.append(character)
                escaped = false
            } else if character == "\\" {
                current.append(character)
                escaped = true
            } else if character == "|" {
                cells.append(current.trimmingCharacters(in: .whitespaces))
                current = ""
            } else {
                current.append(character)
            }
        }
        cells.append(current.trimmingCharacters(in: .whitespaces))
        return cells
    }
}

// MARK: - Inline Parser (CJK-friendly emphasis, math protection, citations, safe links)

enum NativeMarkdownInlineParser {
    static func parse(_ source: String) -> [NativeMarkdownInlineToken] {
        let chars = Array(source)
        return parseRange(chars, start: 0, end: chars.count, style: [], link: nil)
    }

    private static func parseRange(
        _ chars: [Character],
        start: Int,
        end: Int,
        style: NativeMarkdownInlineStyle,
        link: URL?
    ) -> [NativeMarkdownInlineToken] {
        var tokens: [NativeMarkdownInlineToken] = []
        var buffer = ""
        var i = start

        func flush() {
            guard !buffer.isEmpty else { return }
            if case let .text(prev, prevStyle, prevLink) = tokens.last,
               prevStyle == style, prevLink == link {
                tokens[tokens.count - 1] = .text(prev + buffer, style: style, link: link)
            } else {
                tokens.append(.text(buffer, style: style, link: link))
            }
            buffer = ""
        }

        while i < end {
            let ch = chars[i]

            if ch == "\n" {
                flush()
                tokens.append(.lineBreak)
                i += 1
                continue
            }

            // Escape backslash
            if ch == "\\", i + 1 < end {
                let next = chars[i + 1]
                // Inline math \( ... \)
                if next == "(", let close = findInlineBracketMathEnd(chars, from: i + 2, end: end) {
                    flush()
                    let latex = String(chars[(i + 2)..<close]).trimmingCharacters(in: .whitespaces)
                    let raw = String(chars[i..<(close + 2)])
                    if !latex.isEmpty {
                        tokens.append(.math(latex: latex, raw: raw))
                    } else {
                        buffer.append(contentsOf: raw)
                    }
                    i = close + 2
                    continue
                }
                if isEscapablePunctuation(next) {
                    buffer.append(next)
                    i += 2
                    continue
                }
            }

            // Inline code: `...` or ``...``
            if ch == "`" {
                let tickCount = countRun(chars, from: i, end: end, char: "`")
                if let closeStart = findClosingTicks(chars, from: i + tickCount, end: end, count: tickCount) {
                    flush()
                    var content = String(chars[(i + tickCount)..<closeStart])
                    if content.count >= 2, content.hasPrefix(" "), content.hasSuffix(" "),
                       content.contains(where: { $0 != " " }) {
                        content = String(content.dropFirst().dropLast())
                    }
                    var codeStyle = style
                    codeStyle.insert(.code)
                    tokens.append(.text(content, style: codeStyle, link: link))
                    i = closeStart + tickCount
                    continue
                }
                buffer.append(String(repeating: "`", count: tickCount))
                i += tickCount
                continue
            }

            // Inline math $...$
            if ch == "$", (i + 1 >= end || chars[i + 1] != "$") {
                if let close = findInlineDollarMathEnd(chars, from: i + 1, end: end) {
                    flush()
                    let latex = String(chars[(i + 1)..<close]).trimmingCharacters(in: .whitespaces)
                    let raw = String(chars[i...close])
                    tokens.append(.math(latex: latex, raw: raw))
                    i = close + 1
                    continue
                }
            }

            // Image ![alt](url)
            if ch == "!", i + 1 < end, chars[i + 1] == "[",
               let parsed = parseBracketLink(chars, bracketStart: i + 1, end: end) {
                flush()
                tokens.append(.image(alt: parsed.label, url: parsed.url))
                i = parsed.nextIndex
                continue
            }

            // Link [label](url) or Citation [S1]
            if ch == "[" {
                if let citation = parseCitation(chars, start: i, end: end) {
                    flush()
                    tokens.append(.citation(citation.id))
                    i = citation.nextIndex
                    continue
                }
                if link == nil, let parsed = parseBracketLink(chars, bracketStart: i, end: end) {
                    flush()
                    let validURL = safeWebURL(parsed.url)
                    if let validURL {
                        let inner = parseRange(chars, start: parsed.labelStart, end: parsed.labelEnd, style: style, link: validURL)
                        tokens.append(contentsOf: inner)
                    } else {
                        let inner = parseRange(chars, start: parsed.labelStart, end: parsed.labelEnd, style: style, link: nil)
                        tokens.append(contentsOf: inner)
                    }
                    i = parsed.nextIndex
                    continue
                }
            }

            // Autolink <https://...>
            if ch == "<", link == nil, let autolink = parseAngleAutolink(chars, start: i, end: end) {
                flush()
                tokens.append(.text(autolink.text, style: style, link: autolink.url))
                i = autolink.nextIndex
                continue
            }

            // Bare URL https://... or http://...
            if link == nil, ch == "h", let bare = parseBareWebURL(chars, start: i, end: end) {
                flush()
                tokens.append(.text(bare.text, style: style, link: bare.url))
                i = bare.nextIndex
                continue
            }

            // Strikethrough ~~...~~
            if ch == "~", i + 1 < end, chars[i + 1] == "~",
               let close = findClosingDelimiter(chars, from: i + 2, end: end, delimiter: "~", count: 2, allowAdjacentWord: true) {
                flush()
                var nextStyle = style
                nextStyle.insert(.strikethrough)
                tokens.append(contentsOf: parseRange(chars, start: i + 2, end: close, style: nextStyle, link: link))
                i = close + 2
                continue
            }

            // Emphasis: ***, **, *, __, _
            if ch == "*" || ch == "_" {
                let run = countRun(chars, from: i, end: end, char: ch)
                let allowWordBoundary = (ch == "*")
                let canOpen = allowWordBoundary || isUnderscoreOpenBoundary(chars, at: i, start: start)
                if canOpen {
                    if run >= 3,
                       let close = findClosingDelimiter(chars, from: i + 3, end: end, delimiter: ch, count: 3, allowAdjacentWord: allowWordBoundary) {
                        flush()
                        var nextStyle = style
                        nextStyle.insert(.bold)
                        nextStyle.insert(.italic)
                        tokens.append(contentsOf: parseRange(chars, start: i + 3, end: close, style: nextStyle, link: link))
                        i = close + 3
                        continue
                    }
                    if run >= 2,
                       let close = findClosingDelimiter(chars, from: i + 2, end: end, delimiter: ch, count: 2, allowAdjacentWord: allowWordBoundary) {
                        flush()
                        var nextStyle = style
                        nextStyle.insert(.bold)
                        tokens.append(contentsOf: parseRange(chars, start: i + 2, end: close, style: nextStyle, link: link))
                        i = close + 2
                        continue
                    }
                    if run >= 1,
                       let close = findClosingDelimiter(chars, from: i + 1, end: end, delimiter: ch, count: 1, allowAdjacentWord: allowWordBoundary) {
                        flush()
                        var nextStyle = style
                        nextStyle.insert(.italic)
                        tokens.append(contentsOf: parseRange(chars, start: i + 1, end: close, style: nextStyle, link: link))
                        i = close + 1
                        continue
                    }
                }
                buffer.append(String(repeating: String(ch), count: run))
                i += run
                continue
            }

            buffer.append(ch)
            i += 1
        }

        flush()
        return tokens
    }

    private static func isEscapablePunctuation(_ ch: Character) -> Bool {
        "\\`*_{}[]()#+-.!~|$<>".contains(ch)
    }

    private static func countRun(_ chars: [Character], from start: Int, end: Int, char: Character) -> Int {
        var idx = start
        while idx < end && chars[idx] == char { idx += 1 }
        return idx - start
    }

    private static func findClosingTicks(_ chars: [Character], from start: Int, end: Int, count: Int) -> Int? {
        var i = start
        while i < end {
            if chars[i] == "`" {
                let run = countRun(chars, from: i, end: end, char: "`")
                if run == count { return i }
                i += run
            } else {
                i += 1
            }
        }
        return nil
    }

    private static func findInlineBracketMathEnd(_ chars: [Character], from start: Int, end: Int) -> Int? {
        var i = start
        while i + 1 < end {
            if chars[i] == "\\", chars[i + 1] == ")" {
                return i
            }
            i += 1
        }
        return nil
    }

    private static func findInlineDollarMathEnd(_ chars: [Character], from start: Int, end: Int) -> Int? {
        guard start < end else { return nil }
        // Opening '$' cannot be followed by whitespace or another '$'
        if chars[start].isWhitespace || chars[start] == "$" { return nil }
        // Opening '$' preceded by alphanumeric word character (like foo$bar$) is ignored
        if start >= 2, let prev = chars[start - 2].unicodeScalars.first, CharacterSet.alphanumerics.contains(prev), !isCJK(chars[start - 2]) {
            return nil
        }
        var i = start
        while i < end {
            if chars[i] == "\n" { return nil }
            if chars[i] == "\\", i + 1 < end {
                i += 2
                continue
            }
            if chars[i] == "$" {
                if i > start && !chars[i - 1].isWhitespace {
                    if i + 1 < end && chars[i + 1].isNumber { return nil }
                    return i
                }
                return nil
            }
            i += 1
        }
        return nil
    }

    private static func isCJK(_ ch: Character) -> Bool {
        guard let scalar = ch.unicodeScalars.first?.value else { return false }
        return (0x4E00...0x9FFF).contains(scalar) || (0x3400...0x4DBF).contains(scalar) || (0x3000...0x303F).contains(scalar) || (0xFF00...0xFFEF).contains(scalar)
    }

    private static func isUnderscoreOpenBoundary(_ chars: [Character], at index: Int, start: Int) -> Bool {
        guard index > start else { return true }
        let prev = chars[index - 1]
        return prev.isWhitespace || prev.isPunctuation || isCJK(prev)
    }

    private static func isUnderscoreCloseBoundary(_ chars: [Character], after index: Int, end: Int) -> Bool {
        guard index < end else { return true }
        let next = chars[index]
        return next.isWhitespace || next.isPunctuation || isCJK(next)
    }

    private static func findClosingDelimiter(
        _ chars: [Character],
        from start: Int,
        end: Int,
        delimiter: Character,
        count: Int,
        allowAdjacentWord: Bool
    ) -> Int? {
        guard start < end else { return nil }
        // Do not open if followed immediately by whitespace for single '*' / '_' / '~~'
        if chars[start].isWhitespace { return nil }
        var i = start
        while i < end {
            let ch = chars[i]
            if ch == "\n" {
                i += 1
                continue
            }
            if ch == "\\", i + 1 < end {
                i += 2
                continue
            }
            if ch == "`" {
                let ticks = countRun(chars, from: i, end: end, char: "`")
                if let close = findClosingTicks(chars, from: i + ticks, end: end, count: ticks) {
                    i = close + ticks
                    continue
                }
            }
            if ch == delimiter {
                let run = countRun(chars, from: i, end: end, char: delimiter)
                if run >= count && i > start && !chars[i - 1].isWhitespace {
                    if allowAdjacentWord || isUnderscoreCloseBoundary(chars, after: i + count, end: end) {
                        // Prefer exact match when looking for single '*' inside '**' etc.
                        if run == count || (count == 1 && run > 2) || (count == 2 && run >= 2) {
                            return i + (run - count)
                        }
                    }
                }
                i += run
                continue
            }
            i += 1
        }
        return nil
    }

    private static func parseCitation(_ chars: [Character], start: Int, end: Int) -> (id: String, nextIndex: Int)? {
        guard start + 3 < end, chars[start] == "[" else { return nil }
        let prefix = chars[start + 1]
        guard prefix == "S" || prefix == "s" else { return nil }
        var i = start + 2
        var digits = ""
        while i < end && chars[i].isNumber && digits.count < 4 {
            digits.append(chars[i])
            i += 1
        }
        guard !digits.isEmpty, i < end, chars[i] == "]" else { return nil }
        if i + 1 < end, chars[i + 1] == "(" { return nil }
        return ("S" + digits, i + 1)
    }

    static func parseImageBracketLink(
        _ chars: [Character],
        start: Int,
        end: Int
    ) -> (alt: String, url: String, nextIndex: Int)? {
        guard start + 1 < end, chars[start] == "!", chars[start + 1] == "[" else { return nil }
        guard let parsed = parseBracketLink(chars, bracketStart: start + 1, end: end) else { return nil }
        return (parsed.label, parsed.url, parsed.nextIndex)
    }

    private static func parseBracketLink(
        _ chars: [Character],
        bracketStart: Int,
        end: Int
    ) -> (label: String, labelStart: Int, labelEnd: Int, url: String, nextIndex: Int)? {
        guard bracketStart < end, chars[bracketStart] == "[" else { return nil }
        var depth = 1
        var i = bracketStart + 1
        while i < end && depth > 0 {
            if chars[i] == "\\", i + 1 < end {
                i += 2
                continue
            }
            if chars[i] == "[" { depth += 1 }
            else if chars[i] == "]" { depth -= 1; if depth == 0 { break } }
            i += 1
        }
        guard depth == 0, i + 1 < end, chars[i + 1] == "(" else { return nil }
        let labelEnd = i
        var j = i + 2
        var urlDepth = 1
        while j < end && urlDepth > 0 {
            if chars[j] == "\\", j + 1 < end {
                j += 2
                continue
            }
            if chars[j] == "(" { urlDepth += 1 }
            else if chars[j] == ")" { urlDepth -= 1; if urlDepth == 0 { break } }
            else if chars[j] == "\n" { return nil }
            j += 1
        }
        guard urlDepth == 0 else { return nil }
        let label = String(chars[(bracketStart + 1)..<labelEnd])
        var rawURL = String(chars[(i + 2)..<j]).trimmingCharacters(in: .whitespaces)
        // Strip optional title `"title"` or `'title'`
        if let spaceIdx = rawURL.firstIndex(where: { $0.isWhitespace }) {
            let suffix = rawURL[spaceIdx...].trimmingCharacters(in: .whitespaces)
            if suffix.hasPrefix("\"") || suffix.hasPrefix("'") || suffix.hasPrefix("=") {
                rawURL = String(rawURL[..<spaceIdx])
            }
        }
        if rawURL.hasPrefix("<"), rawURL.hasSuffix(">"), rawURL.count >= 2 {
            rawURL = String(rawURL.dropFirst().dropLast())
        }
        return (label, bracketStart + 1, labelEnd, rawURL, j + 1)
    }

    private static func parseAngleAutolink(_ chars: [Character], start: Int, end: Int) -> (text: String, url: URL, nextIndex: Int)? {
        var i = start + 1
        while i < end {
            if chars[i] == ">" {
                let candidate = String(chars[(start + 1)..<i])
                if let url = safeWebURL(candidate) {
                    return (candidate, url, i + 1)
                }
                return nil
            }
            if chars[i].isWhitespace || chars[i] == "<" { return nil }
            i += 1
        }
        return nil
    }

    private static func parseBareWebURL(_ chars: [Character], start: Int, end: Int) -> (text: String, url: URL, nextIndex: Int)? {
        if start > 0 {
            let prev = chars[start - 1]
            if prev.isLetter || prev.isNumber || prev == "/" { return nil }
        }
        let slice = String(chars[start..<min(end, start + 8)])
        guard slice.hasPrefix("https://") || slice.hasPrefix("http://") else { return nil }
        var i = start
        while i < end && !chars[i].isWhitespace && !isCJK(chars[i]) {
            i += 1
        }
        // Trim trailing sentence punctuation
        while i > start && ".,;:!?)」。，、；：！？".contains(chars[i - 1]) {
            if chars[i - 1] == ")" {
                let candidate = String(chars[start..<i])
                if candidate.filter({ $0 == "(" }).count >= candidate.filter({ $0 == ")" }).count {
                    break
                }
            }
            i -= 1
        }
        let raw = String(chars[start..<i])
        guard let url = safeWebURL(raw) else { return nil }
        return (raw, url, i)
    }

    static func safeWebURL(_ raw: String) -> URL? {
        guard let url = URL(string: raw),
              let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "http",
              url.host != nil,
              url.user == nil,
              url.password == nil else {
            return nil
        }
        return url
    }
}

// MARK: - LaTeX Math Formatter

enum NativeMarkdownMathFormatter {
    static func format(_ latex: String) -> [NativeMathRun] {
        let chars = Array(latex.trimmingCharacters(in: .whitespacesAndNewlines))
        return parseMath(chars, start: 0, end: chars.count, script: .normal)
    }

    static func plainText(_ latex: String) -> String {
        format(latex).map(\.text).joined()
    }

    private static func parseMath(
        _ chars: [Character],
        start: Int,
        end: Int,
        script: NativeMathScriptKind
    ) -> [NativeMathRun] {
        var runs: [NativeMathRun] = []
        var buffer = ""
        var i = start

        func append(_ text: String, _ runScript: NativeMathScriptKind = script) {
            guard !text.isEmpty else { return }
            if let last = runs.last, last.script == runScript {
                runs[runs.count - 1] = NativeMathRun(text: last.text + text, script: runScript)
            } else {
                runs.append(NativeMathRun(text: text, script: runScript))
            }
        }

        func flush() {
            append(buffer, script)
            buffer = ""
        }

        while i < end {
            let ch = chars[i]
            if ch == "{" {
                if let close = findBraceEnd(chars, open: i, end: end) {
                    flush()
                    runs.append(contentsOf: parseMath(chars, start: i + 1, end: close, script: script))
                    i = close + 1
                    continue
                }
            }

            if ch == "^" || ch == "_" {
                let nextScript: NativeMathScriptKind = ch == "^" ? .superscript : .subscript
                if i + 1 < end {
                    flush()
                    if chars[i + 1] == "{", let close = findBraceEnd(chars, open: i + 1, end: end) {
                        runs.append(contentsOf: parseMath(chars, start: i + 2, end: close, script: nextScript))
                        i = close + 1
                        continue
                    } else if chars[i + 1] == "\\" {
                        let cmd = readCommand(chars, from: i + 1, end: end)
                        let symbol = symbols[cmd.name] ?? cmd.raw
                        append(symbol, nextScript)
                        i = cmd.nextIndex
                        continue
                    } else {
                        append(String(chars[i + 1]), nextScript)
                        i += 2
                        continue
                    }
                }
            }

            if ch == "\\", i + 1 < end {
                let next = chars[i + 1]
                if next == "," || next == ";" || next == ":" || next == " " {
                    buffer.append(" ")
                    i += 2
                    continue
                }
                if next == "!" {
                    i += 2
                    continue
                }
                if next == "\\" {
                    buffer.append("\n")
                    i += 2
                    continue
                }
                if "{}$%&_#".contains(next) {
                    buffer.append(next)
                    i += 2
                    continue
                }
                let cmd = readCommand(chars, from: i, end: end)
                if cmd.name == "left" || cmd.name == "right" || cmd.name == "big" || cmd.name == "Big" {
                    i = cmd.nextIndex
                    if i < end && chars[i] == "." { i += 1 }
                    continue
                }
                if cmd.name == "frac" || cmd.name == "dfrac" || cmd.name == "tfrac" {
                    if let arg1 = readGroup(chars, from: cmd.nextIndex, end: end),
                       let arg2 = readGroup(chars, from: arg1.nextIndex, end: end) {
                        flush()
                        let num = parseMath(chars, start: arg1.contentStart, end: arg1.contentEnd, script: script)
                        let den = parseMath(chars, start: arg2.contentStart, end: arg2.contentEnd, script: script)
                        let numText = num.map(\.text).joined()
                        let denText = den.map(\.text).joined()
                        let wrapNum = numText.contains(" ") || numText.contains("+") || numText.contains("-")
                        let wrapDen = denText.contains(" ") || denText.contains("+") || denText.contains("-")
                        if wrapNum { append("(") }
                        runs.append(contentsOf: num)
                        if wrapNum { append(")") }
                        append(" / ")
                        if wrapDen { append("(") }
                        runs.append(contentsOf: den)
                        if wrapDen { append(")") }
                        i = arg2.nextIndex
                        continue
                    }
                }
                if cmd.name == "sqrt" {
                    var cursor = cmd.nextIndex
                    var rootIndex: [NativeMathRun]?
                    if cursor < end && chars[cursor] == "[",
                       let bracketEnd = chars[cursor..<end].firstIndex(of: "]") {
                        rootIndex = parseMath(chars, start: cursor + 1, end: bracketEnd, script: .superscript)
                        cursor = bracketEnd + 1
                    }
                    if let arg = readGroup(chars, from: cursor, end: end) {
                        flush()
                        if let rootIndex { runs.append(contentsOf: rootIndex) }
                        append("√(")
                        runs.append(contentsOf: parseMath(chars, start: arg.contentStart, end: arg.contentEnd, script: script))
                        append(")")
                        i = arg.nextIndex
                        continue
                    }
                }
                if ["text", "mathrm", "mathbf", "mathit", "mathsf", "mathcal", "operatorname"].contains(cmd.name),
                   let arg = readGroup(chars, from: cmd.nextIndex, end: end) {
                    flush()
                    runs.append(contentsOf: parseMath(chars, start: arg.contentStart, end: arg.contentEnd, script: script))
                    i = arg.nextIndex
                    continue
                }
                if let symbol = symbols[cmd.name] {
                    buffer.append(symbol)
                    i = cmd.nextIndex
                    continue
                }
                buffer.append(cmd.raw)
                i = cmd.nextIndex
                continue
            }

            if ch == "*" {
                buffer.append("·")
                i += 1
                continue
            }

            buffer.append(ch)
            i += 1
        }
        flush()
        return mergeRuns(runs)
    }

    private static func mergeRuns(_ runs: [NativeMathRun]) -> [NativeMathRun] {
        var merged: [NativeMathRun] = []
        for run in runs where !run.text.isEmpty {
            if let last = merged.last, last.script == run.script {
                merged[merged.count - 1] = NativeMathRun(text: last.text + run.text, script: run.script)
            } else {
                merged.append(run)
            }
        }
        return merged
    }

    private static func findBraceEnd(_ chars: [Character], open: Int, end: Int) -> Int? {
        guard open < end, chars[open] == "{" else { return nil }
        var depth = 1
        var i = open + 1
        while i < end {
            if chars[i] == "\\", i + 1 < end {
                i += 2
                continue
            }
            if chars[i] == "{" { depth += 1 }
            else if chars[i] == "}" {
                depth -= 1
                if depth == 0 { return i }
            }
            i += 1
        }
        return nil
    }

    private static func readCommand(_ chars: [Character], from backslash: Int, end: Int) -> (name: String, raw: String, nextIndex: Int) {
        var i = backslash + 1
        var name = ""
        while i < end && chars[i].isLetter {
            name.append(chars[i])
            i += 1
        }
        if name.isEmpty && i < end {
            name = String(chars[i])
            i += 1
        }
        return (name, String(chars[backslash..<i]), i)
    }

    private static func readGroup(_ chars: [Character], from start: Int, end: Int) -> (contentStart: Int, contentEnd: Int, nextIndex: Int)? {
        var i = start
        while i < end && chars[i].isWhitespace { i += 1 }
        guard i < end else { return nil }
        if chars[i] == "{", let close = findBraceEnd(chars, open: i, end: end) {
            return (i + 1, close, close + 1)
        }
        return (i, i + 1, i + 1)
    }

    private static let symbols: [String: String] = [
        "alpha": "α", "beta": "β", "gamma": "γ", "delta": "δ", "epsilon": "ϵ", "varepsilon": "ε",
        "zeta": "ζ", "eta": "η", "theta": "θ", "vartheta": "ϑ", "iota": "ι", "kappa": "κ",
        "lambda": "λ", "mu": "μ", "nu": "ν", "xi": "ξ", "pi": "π", "varpi": "ϖ",
        "rho": "ρ", "varrho": "ϱ", "sigma": "σ", "varsigma": "ς", "tau": "τ", "upsilon": "υ",
        "phi": "φ", "varphi": "ϕ", "chi": "χ", "psi": "ψ", "omega": "ω",
        "Gamma": "Γ", "Delta": "Δ", "Theta": "Θ", "Lambda": "Λ", "Xi": "Ξ", "Pi": "Π",
        "Sigma": "Σ", "Upsilon": "Υ", "Phi": "Φ", "Psi": "Ψ", "Omega": "Ω",
        "times": "×", "div": "÷", "pm": "±", "mp": "∓", "cdot": "·", "ast": "∗", "star": "⋆",
        "neq": "≠", "ne": "≠", "leq": "≤", "le": "≤", "geq": "≥", "ge": "≥",
        "approx": "≈", "equiv": "≡", "cong": "≅", "sim": "∼", "simeq": "≃", "propto": "∝",
        "ll": "≪", "gg": "≫", "infty": "∞", "partial": "∂", "nabla": "∇",
        "sum": "∑", "prod": "∏", "coprod": "∐", "int": "∫", "iint": "∬", "iiint": "∭", "oint": "∮",
        "in": "∈", "notin": "∉", "ni": "∋", "subset": "⊂", "supset": "⊃",
        "subseteq": "⊆", "supseteq": "⊇", "cup": "∪", "cap": "∩", "emptyset": "∅", "varnothing": "∅",
        "forall": "∀", "exists": "∃", "nexists": "∄", "neg": "¬", "lnot": "¬",
        "land": "∧", "wedge": "∧", "lor": "∨", "vee": "∨", "oplus": "⊕", "otimes": "⊗",
        "to": "→", "rightarrow": "→", "leftarrow": "←", "leftrightarrow": "↔",
        "Rightarrow": "⇒", "Leftarrow": "⇐", "Leftrightarrow": "⇔", "iff": "⇔", "implies": "⇒",
        "mapsto": "↦", "uparrow": "↑", "downarrow": "↓",
        "dots": "…", "ldots": "…", "cdots": "⋯", "vdots": "⋮", "ddots": "⋱",
        "angle": "∠", "perp": "⊥", "parallel": "∥", "triangle": "△",
        "degree": "°", "circ": "∘", "prime": "′",
        "quad": "  ", "qquad": "    ",
        "mathbbR": "ℝ", "mathbbN": "ℕ", "mathbbZ": "ℤ", "mathbbQ": "ℚ", "mathbbC": "ℂ"
    ]
}
