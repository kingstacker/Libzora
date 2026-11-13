import SwiftUI
import UIKit

/// Native, offline Markdown for AI answers. The reader runs on iOS 15, so this
/// covers the block and inline syntax that chat answers actually use instead of
/// depending on a newer system renderer or a third-party package.
struct NativeMarkdownView: View {
    private let blocks: [NativeMarkdownBlock]

    init(_ source: String) { blocks = NativeMarkdownParser.parse(source) }

    var body: some View {
        NativeMarkdownBlocks(blocks: blocks)
            .textSelection(.enabled)
    }
}

// MARK: - Model

enum NativeMarkdownAlignment {
    case leading, center, trailing
}

struct NativeMarkdownTable {
    let headers: [String]
    let alignments: [NativeMarkdownAlignment]
    let rows: [[String]]
}

struct NativeMarkdownListItem {
    let level: Int
    let marker: String
    let checked: Bool?
    let text: String
}

struct NativeMarkdownBlock: Identifiable {
    enum Kind {
        case heading(level: Int, text: String)
        case paragraph(String)
        case list([NativeMarkdownListItem])
        case code(language: String?, text: String)
        case quote([NativeMarkdownBlock])
        case rule
        case table(NativeMarkdownTable)
    }

    let id: Int
    let kind: Kind
}

// MARK: - Parsing

private struct NativeMarkdownParser {
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
            if candidate.hasPrefix(marker) {
                index += 1
                break
            }
            body.append(lines[index])
            index += 1
        }
        return body.joined(separator: "\n")
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
                if let baseIndent, look < lines.count,
                   let next = Self.listItem(lines[look]), next.indent >= baseIndent {
                    index = look
                    continue
                }
                break
            }
            guard let item = Self.listItem(raw) else {
                // A plain continuation line still belongs to the previous item.
                if let baseIndent, Self.indent(of: raw) > baseIndent, !items.isEmpty {
                    items[items.count - 1] = NativeMarkdownListItem(
                        level: items[items.count - 1].level,
                        marker: items[items.count - 1].marker,
                        checked: items[items.count - 1].checked,
                        text: items[items.count - 1].text + "\n" + trimmed)
                    index += 1
                    continue
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
            items.append(NativeMarkdownListItem(
                level: level,
                marker: item.ordered ? "\(item.number)." : "•",
                checked: checked,
                text: text))
            index += 1
        }
        return items
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
        let language = info.split(separator: " ").first.map(String.init)
        return (String(repeating: String(first), count: count), language?.isEmpty == true ? nil : language)
    }

    private static func heading(_ line: String) -> (level: Int, text: String)? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        let level = trimmed.prefix { $0 == "#" }.count
        guard (1...6).contains(level) else { return nil }
        let rest = trimmed.dropFirst(level)
        guard rest.first == " " || rest.first == "\t" else { return nil }
        let text = rest.trimmingCharacters(in: .whitespaces)
        return text.isEmpty ? nil : (level, text)
    }

    private static func isRule(_ line: String) -> Bool {
        let compact = line.replacingOccurrences(of: " ", with: "")
        guard compact.count >= 3, let first = compact.first, "-*_".contains(first) else { return false }
        return compact.allSatisfy { $0 == first }
    }

    private static func startsBlock(_ line: String) -> Bool {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        return fence(line) != nil || heading(line) != nil || isRule(trimmed)
            || trimmed.hasPrefix(">") || listItem(line) != nil
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

// MARK: - Blocks

private struct NativeMarkdownBlocks: View {
    let blocks: [NativeMarkdownBlock]
    var muted = false

    private var textColor: Color { muted ? NativeTheme.secondary : NativeTheme.ink }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(blocks) { block in
                switch block.kind {
                case let .heading(level, text):
                    Text(NativeMarkdownInline.attributed(text))
                        .font(NativeTheme.font(headingSize(level), relativeTo: level <= 2 ? .title3 : .headline))
                        .fontWeight(.semibold)
                        .foregroundStyle(textColor)
                        .fixedSize(horizontal: false, vertical: true)
                case let .paragraph(text):
                    Text(NativeMarkdownInline.attributed(text))
                        .font(NativeTheme.font(16))
                        .lineSpacing(5)
                        .foregroundStyle(textColor)
                        .fixedSize(horizontal: false, vertical: true)
                case let .list(items):
                    VStack(alignment: .leading, spacing: 7) {
                        ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                if let checked = item.checked {
                                    Image(systemName: checked ? "checkmark.square.fill" : "square")
                                        .font(.system(size: 15, weight: .regular))
                                        .foregroundStyle(checked ? NativeTheme.secondary : NativeTheme.muted)
                                        .frame(width: 18, alignment: .trailing)
                                } else {
                                    Text(item.marker)
                                        .font(NativeTheme.font(15))
                                        .foregroundStyle(NativeTheme.secondary)
                                        .frame(minWidth: 18, alignment: .trailing)
                                }
                                Text(NativeMarkdownInline.attributed(item.text))
                                    .font(NativeTheme.font(16))
                                    .lineSpacing(4)
                                    .foregroundStyle(textColor)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                            .padding(.leading, CGFloat(item.level) * 20)
                        }
                    }
                case let .code(language, text):
                    NativeMarkdownCodeBlock(language: language, code: text)
                case let .quote(inner):
                    HStack(alignment: .top, spacing: 10) {
                        RoundedRectangle(cornerRadius: 2)
                            .fill(NativeTheme.strong)
                            .frame(width: 3)
                        NativeMarkdownBlocks(blocks: inner, muted: true)
                    }
                    .fixedSize(horizontal: false, vertical: true)
                case .rule:
                    Rectangle()
                        .fill(NativeTheme.divider)
                        .frame(height: 1)
                        .padding(.vertical, 2)
                case let .table(table):
                    NativeMarkdownTableBlock(table: table)
                }
            }
        }
    }

    private func headingSize(_ level: Int) -> CGFloat {
        switch level {
        case 1: return 22
        case 2: return 20
        case 3: return 18
        case 4: return 17
        case 5: return 16
        default: return 15
        }
    }
}

private struct NativeMarkdownCodeBlock: View {
    let language: String?
    let code: String
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 8) {
                Text((language ?? "代码").uppercased())
                    .font(.system(size: 10, weight: .semibold, design: .monospaced))
                    .foregroundStyle(NativeTheme.muted)
                Spacer(minLength: 8)
                Button {
                    UIPasteboard.general.string = code
                    copied = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { copied = false }
                } label: {
                    Image(systemName: copied ? "checkmark" : "doc.on.doc")
                        .font(.system(size: 12, weight: .regular))
                        .foregroundStyle(NativeTheme.secondary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(copied ? "已复制" : "复制代码")
            }
            ScrollView(.horizontal, showsIndicators: false) {
                Text(code)
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(NativeTheme.ink)
                    .fixedSize(horizontal: true, vertical: true)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(NativeTheme.surface, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(NativeTheme.divider, lineWidth: 0.5))
    }
}

private struct NativeMarkdownTableBlock: View {
    let table: NativeMarkdownTable

    private var widths: [CGFloat] {
        (0..<table.headers.count).map { column in
            var widest: CGFloat = 0
            for row in [table.headers] + table.rows where column < row.count {
                widest = max(widest, NativeMarkdownTableBlock.estimatedWidth(row[column]))
            }
            return min(240, max(56, widest + 20))
        }
    }

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            VStack(spacing: 0) {
                row(table.headers, header: true)
                ForEach(Array(table.rows.enumerated()), id: \.offset) { index, cells in
                    Rectangle().fill(NativeTheme.divider).frame(height: 0.5)
                    row(cells, header: false)
                }
            }
            .background(NativeTheme.surface)
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(NativeTheme.divider, lineWidth: 0.5))
        }
    }

    private func row(_ cells: [String], header: Bool) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(widths.enumerated()), id: \.offset) { column, width in
                let text = column < cells.count ? cells[column] : ""
                Text(NativeMarkdownInline.attributed(text))
                    .font(NativeTheme.font(14, relativeTo: .subheadline))
                    .fontWeight(header ? .semibold : .regular)
                    .foregroundStyle(NativeTheme.ink)
                    .multilineTextAlignment(alignment(column))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
                    .frame(width: width, alignment: alignmentValue(column))
            }
        }
        .background(header ? NativeTheme.sunk : Color.clear)
    }

    private func alignment(_ column: Int) -> TextAlignment {
        switch column < table.alignments.count ? table.alignments[column] : .leading {
        case .leading: return .leading
        case .center: return .center
        case .trailing: return .trailing
        }
    }

    private func alignmentValue(_ column: Int) -> Alignment {
        switch column < table.alignments.count ? table.alignments[column] : .leading {
        case .leading: return .leading
        case .center: return .center
        case .trailing: return .trailing
        }
    }

    private static func estimatedWidth(_ text: String) -> CGFloat {
        text.unicodeScalars.reduce(CGFloat(0)) { $0 + ($1.isASCII ? 7.4 : 14.5) }
    }
}

// MARK: - Inline

private enum NativeMarkdownInline {
    static func attributed(_ source: String) -> AttributedString {
        let options = AttributedString.MarkdownParsingOptions(
            interpretedSyntax: .inlineOnlyPreservingWhitespace,
            failurePolicy: .returnPartiallyParsedIfPossible)
        guard var value = try? AttributedString(markdown: source, options: options) else {
            return AttributedString(source)
        }
        for run in value.runs {
            if let intent = run.inlinePresentationIntent {
                if intent.contains(.code) {
                    value[run.range].font = .system(size: 14, design: .monospaced)
                    value[run.range].backgroundColor = NativeTheme.strong.opacity(0.55)
                }
                if intent.contains(.strikethrough) {
                    value[run.range].strikethroughStyle = .single
                }
            }
            if run.link != nil {
                value[run.range].foregroundColor = NativeTheme.ink
                value[run.range].underlineStyle = .single
            }
        }
        return value
    }
}
