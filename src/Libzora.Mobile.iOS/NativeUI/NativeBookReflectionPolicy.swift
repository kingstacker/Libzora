import Foundation

/// Foundation-only Markdown formatting, citation quoting, preview stripping and
/// character counting helpers for mobile book reflections.
enum NativeBookReflectionPolicy {
    static func countCharacters(in markdown: String) -> Int {
        let stripped = stripImageLinesAndTokens(markdown)
        guard !stripped.isEmpty else { return 0 }
        var total = 0
        for rawLine in stripped.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            var plain = rawLine.trimmingCharacters(in: .whitespaces)
            if let range = plain.range(of: #"^#{1,6}\s+"#, options: .regularExpression) {
                plain.removeSubrange(range)
            } else if let range = plain.range(of: #"^>\s?"#, options: .regularExpression) {
                plain.removeSubrange(range)
            } else if let range = plain.range(of: #"^[-*+]\s+"#, options: .regularExpression) {
                plain.removeSubrange(range)
            } else if let range = plain.range(of: #"^\d+[.)]\s+"#, options: .regularExpression) {
                plain.removeSubrange(range)
            }
            plain = plain.replacingOccurrences(of: #"\*\*([^*\r\n]+)\*\*"#, with: "$1", options: .regularExpression)
            plain = plain.replacingOccurrences(of: #"\[([^\]\r\n]+)\]\([^)\r\n]+\)"#, with: "$1", options: .regularExpression)
            total += plain.count
        }
        return total
    }

    static func plainPreviewText(from markdown: String) -> String {
        let stripped = stripImageLinesAndTokens(markdown)
        guard !stripped.isEmpty else { return "" }
        let lines = stripped
            .replacingOccurrences(of: "\r\n", with: "\n")
            .components(separatedBy: "\n")
            .map { raw -> String in
                var text = raw.trimmingCharacters(in: .whitespaces)
                text = text.replacingOccurrences(of: #"^#{1,6}\s*"#, with: "", options: .regularExpression)
                text = text.replacingOccurrences(of: #"^>\s?"#, with: "", options: .regularExpression)
                text = text.replacingOccurrences(of: #"^[-*+]\s+"#, with: "", options: .regularExpression)
                text = text.replacingOccurrences(of: #"^\d+[.)]\s+"#, with: "", options: .regularExpression)
                text = text.replacingOccurrences(of: #"(\*\*|__|~~|`)"#, with: "", options: .regularExpression)
                text = text.replacingOccurrences(of: #"\[([^\]]+)\]\([^)]*\)"#, with: "$1", options: .regularExpression)
                return text
            }
        return lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func stripImageLinesAndTokens(_ markdown: String) -> String {
        let normalized = markdown.replacingOccurrences(of: "\r\n", with: "\n")
        let lines = normalized.components(separatedBy: "\n").compactMap { line -> String? in
            if line.range(of: #"^\s*!\[[^\]]*\]\([^)\s\r\n]+(?:\s*=\s*\d+(?:x\d+)?)?\)\s*$"#, options: .regularExpression) != nil {
                return nil
            }
            return line.replacingOccurrences(of: #"!\[[^\]\r\n]*\]\([^\)\r\n]*\)"#, with: "", options: .regularExpression)
        }
        return lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func formatCitationBlock(text: String, bookTitle: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "\r\n", with: "\n")
        guard !trimmed.isEmpty else { return "" }
        let quoted = trimmed.components(separatedBy: "\n").map { "> \($0)" }.joined(separator: "\n")
        let cleanTitle = bookTitle.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
        guard !cleanTitle.isEmpty else { return quoted }
        let decorated = (cleanTitle.hasPrefix("《") && cleanTitle.hasSuffix("》"))
            ? cleanTitle
            : "《\(cleanTitle)》"
        return "\(quoted)\n>\n> --\(decorated)"
    }

    static func toggleLinePrefix(
        in text: String,
        selection: NSRange,
        prefix: String,
        isHeading: Bool
    ) -> (text: String, selection: NSRange) {
        let nsText = text as NSString
        let safeLoc = min(max(0, selection.location), nsText.length)
        let lineRange = nsText.lineRange(for: NSRange(location: safeLoc, length: 0))
        let hasTrailingNewline = lineRange.length > 0 && nsText.substring(with: NSRange(location: lineRange.location + lineRange.length - 1, length: 1)) == "\n"
        let contentRange = NSRange(location: lineRange.location, length: hasTrailingNewline ? lineRange.length - 1 : lineRange.length)
        let line = nsText.substring(with: contentRange)

        let updated: String
        if isHeading {
            let stripped = line.replacingOccurrences(of: #"^#{1,6}\s+"#, with: "", options: .regularExpression)
            updated = line.hasPrefix(prefix) ? stripped : prefix + stripped
        } else if line.hasPrefix(prefix) {
            updated = String(line.dropFirst(prefix.count))
        } else {
            updated = prefix + line
        }

        let replaced = nsText.replacingCharacters(in: contentRange, with: updated)
        let caret = lineRange.location + (updated as NSString).length
        return (replaced, NSRange(location: caret, length: 0))
    }

    static func toggleBold(
        in text: String,
        selection: NSRange
    ) -> (text: String, selection: NSRange) {
        let nsText = text as NSString
        let start = min(max(0, selection.location), nsText.length)
        let length = min(max(0, selection.length), nsText.length - start)
        let range = NSRange(location: start, length: length)
        if length == 0 {
            let replaced = nsText.replacingCharacters(in: range, with: "****")
            return (replaced, NSRange(location: start + 2, length: 0))
        }
        let selected = nsText.substring(with: range)
        if selected.count >= 4, selected.hasPrefix("**"), selected.hasSuffix("**") {
            let inner = String(selected.dropFirst(2).dropLast(2))
            let replaced = nsText.replacingCharacters(in: range, with: inner)
            return (replaced, NSRange(location: start + (inner as NSString).length, length: 0))
        }
        let wrapped = "**\(selected)**"
        let replaced = nsText.replacingCharacters(in: range, with: wrapped)
        return (replaced, NSRange(location: start + (wrapped as NSString).length, length: 0))
    }

    static func insertBlock(
        _ block: String,
        into text: String,
        selection: NSRange
    ) -> (text: String, selection: NSRange) {
        let nsText = text as NSString
        let start = min(max(0, selection.location), nsText.length)
        let end = min(nsText.length, start + max(0, selection.length))
        let range = NSRange(location: start, length: end - start)
        let needsLeading = start > 0 && nsText.substring(with: NSRange(location: start - 1, length: 1)) != "\n"
        let needsTrailing = end < nsText.length && nsText.substring(with: NSRange(location: end, length: 1)) != "\n"
        let insertion = (needsLeading ? "\n\n" : "") + block + (needsTrailing ? "\n\n" : "\n")
        let replaced = nsText.replacingCharacters(in: range, with: insertion)
        let caret = start + (insertion as NSString).length
        return (replaced, NSRange(location: caret, length: 0))
    }

    static func formatUpdatedTimestamp(_ isoString: String?) -> String? {
        guard let isoString, !isoString.isEmpty else { return nil }
        let parser = ISO8601DateFormatter()
        parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var date = parser.date(from: isoString)
        if date == nil {
            parser.formatOptions = [.withInternetDateTime]
            date = parser.date(from: isoString)
        }
        guard let date else { return nil }
        let out = DateFormatter()
        out.locale = Locale(identifier: "en_US_POSIX")
        out.dateFormat = "yyyy-MM-dd HH:mm"
        return out.string(from: date)
    }
}

struct NativeReflectionImageToken: Equatable {
    let rawRange: NSRange
    let displayLocation: Int
    let alt: String
    let url: String
    let rawToken: String
}

/// Pure-Foundation codec mapping between raw Markdown (with inline data-URI
/// image tokens) and the editor's display string (where each image is a single
/// U+FFFC attachment character), keeping toolbar selection offsets exact.
enum NativeReflectionDocumentCodec {
    static let attachmentScalar = "\u{FFFC}"
    private static let imagePattern = try! NSRegularExpression(
        pattern: #"!\[([^\]\r\n]*)\]\((data:image/[^)\s\r\n]+)(?:\s*=\s*\d+(?:x\d+)?)?\)"#
    )

    static func parseImages(in markdown: String) -> (displayText: String, tokens: [NativeReflectionImageToken]) {
        let nsText = markdown as NSString
        let matches = imagePattern.matches(in: markdown, range: NSRange(location: 0, length: nsText.length))
        guard !matches.isEmpty else { return (markdown, []) }

        var output = ""
        var tokens: [NativeReflectionImageToken] = []
        var cursor = 0
        var displayLength = 0

        for match in matches {
            let full = match.range
            if full.location > cursor {
                let prefix = nsText.substring(with: NSRange(location: cursor, length: full.location - cursor))
                output.append(prefix)
                displayLength += (prefix as NSString).length
            }
            let alt = nsText.substring(with: match.range(at: 1))
            let url = nsText.substring(with: match.range(at: 2))
            let rawToken = nsText.substring(with: full)
            tokens.append(NativeReflectionImageToken(
                rawRange: full,
                displayLocation: displayLength,
                alt: alt,
                url: url,
                rawToken: rawToken
            ))
            output.append(attachmentScalar)
            displayLength += 1
            cursor = full.location + full.length
        }

        if cursor < nsText.length {
            output.append(nsText.substring(from: cursor))
        }
        return (output, tokens)
    }

    static func rawLocation(fromDisplayLocation location: Int, tokens: [NativeReflectionImageToken], rawLength: Int) -> Int {
        let clamped = max(0, location)
        var delta = 0
        for token in tokens {
            if clamped <= token.displayLocation { break }
            delta += token.rawRange.length - 1
        }
        return min(max(0, clamped + delta), rawLength)
    }

    static func rawRange(fromDisplayRange range: NSRange, tokens: [NativeReflectionImageToken], rawLength: Int) -> NSRange {
        let start = rawLocation(fromDisplayLocation: range.location, tokens: tokens, rawLength: rawLength)
        let end = rawLocation(fromDisplayLocation: range.location + range.length, tokens: tokens, rawLength: rawLength)
        return NSRange(location: start, length: max(0, end - start))
    }

    static func displayLocation(fromRawLocation location: Int, tokens: [NativeReflectionImageToken], displayLength: Int) -> Int {
        let clamped = max(0, location)
        var delta = 0
        for token in tokens {
            let tokenEnd = token.rawRange.location + token.rawRange.length
            if clamped <= token.rawRange.location { break }
            if clamped < tokenEnd {
                return min(token.displayLocation + 1, displayLength)
            }
            delta += token.rawRange.length - 1
        }
        return min(max(0, clamped - delta), displayLength)
    }

    static func displayRange(fromRawRange range: NSRange, tokens: [NativeReflectionImageToken], displayLength: Int) -> NSRange {
        let start = displayLocation(fromRawLocation: range.location, tokens: tokens, displayLength: displayLength)
        let end = displayLocation(fromRawLocation: range.location + range.length, tokens: tokens, displayLength: displayLength)
        return NSRange(location: start, length: max(0, end - start))
    }
}

enum NativeReflectionLineKind: Equatable {
    case heading(level: Int, prefixRange: NSRange, contentRange: NSRange)
    case quote(prefixRange: NSRange, contentRange: NSRange)
    case unorderedList(prefixRange: NSRange, contentRange: NSRange)
    case orderedList(prefixRange: NSRange, contentRange: NSRange)
    case rule
    case paragraph
}

struct NativeReflectionParsedLine: Equatable {
    let lineRange: NSRange
    let contentRange: NSRange
    let kind: NativeReflectionLineKind
}

enum NativeReflectionInlineSpan: Equatable {
    case bold(full: NSRange, open: NSRange, content: NSRange, close: NSRange)
    case italic(full: NSRange, open: NSRange, content: NSRange, close: NSRange)
    case strikethrough(full: NSRange, open: NSRange, content: NSRange, close: NSRange)
    case code(full: NSRange, open: NSRange, content: NSRange, close: NSRange)
    case link(full: NSRange, open: NSRange, label: NSRange, close: NSRange, url: String)

    var fullRange: NSRange {
        switch self {
        case let .bold(full, _, _, _),
             let .italic(full, _, _, _),
             let .strikethrough(full, _, _, _),
             let .code(full, _, _, _),
             let .link(full, _, _, _, _):
            return full
        }
    }
}

/// UTF-16 range parser for live in-editor Markdown rendering. Keeps all ranges
/// strictly in NSString coordinates so UITextView attribute styling never drifts.
enum NativeReflectionMarkdownRanges {
    private static let headingRegex = try! NSRegularExpression(pattern: #"^(#{1,6})(\s+)"#)
    private static let quoteRegex = try! NSRegularExpression(pattern: #"^(>\s?)"#)
    private static let unorderedRegex = try! NSRegularExpression(pattern: #"^(\s*[-*+]\s+)"#)
    private static let orderedRegex = try! NSRegularExpression(pattern: #"^(\s*\d+[.)]\s+)"#)
    private static let ruleRegex = try! NSRegularExpression(pattern: #"^\s*(?:---+|\*\*\*+|___+)\s*$"#)
    private static let codeRegex = try! NSRegularExpression(pattern: #"(`)([^`\r\n]+)(`)"#)
    private static let linkRegex = try! NSRegularExpression(pattern: #"(\[)([^\]\r\n]+)(\]\(([^)\s\r\n]+)\))"#)
    private static let boldRegex = try! NSRegularExpression(pattern: #"(\*\*)([^*\r\n]+)(\*\*)"#)
    private static let strikeRegex = try! NSRegularExpression(pattern: #"(~~)([^~\r\n]+)(~~)"#)
    private static let italicRegex = try! NSRegularExpression(pattern: #"(?<!\*)(\*)([^*\r\n]+)(\*)(?!\*)"#)

    static func parseLines(in text: String) -> [NativeReflectionParsedLine] {
        let nsText = text as NSString
        let total = nsText.length
        guard total > 0 else { return [] }

        var result: [NativeReflectionParsedLine] = []
        var location = 0
        while location < total {
            let lineRange = nsText.lineRange(for: NSRange(location: location, length: 0))
            let hasNewline = lineRange.length > 0
                && nsText.character(at: lineRange.location + lineRange.length - 1) == 0x0A
            let bodyLength = hasNewline ? lineRange.length - 1 : lineRange.length
            let bodyRange = NSRange(location: lineRange.location, length: bodyLength)
            let lineText = nsText.substring(with: bodyRange)
            let localFull = NSRange(location: 0, length: bodyLength)

            let kind: NativeReflectionLineKind
            if let match = headingRegex.firstMatch(in: lineText, range: localFull) {
                let level = match.range(at: 1).length
                let prefixLen = match.range.length
                kind = .heading(
                    level: level,
                    prefixRange: NSRange(location: bodyRange.location, length: prefixLen),
                    contentRange: NSRange(location: bodyRange.location + prefixLen, length: max(0, bodyLength - prefixLen))
                )
            } else if let match = quoteRegex.firstMatch(in: lineText, range: localFull) {
                let prefixLen = match.range.length
                kind = .quote(
                    prefixRange: NSRange(location: bodyRange.location, length: prefixLen),
                    contentRange: NSRange(location: bodyRange.location + prefixLen, length: max(0, bodyLength - prefixLen))
                )
            } else if ruleRegex.firstMatch(in: lineText, range: localFull) != nil {
                kind = .rule
            } else if let match = unorderedRegex.firstMatch(in: lineText, range: localFull) {
                let prefixLen = match.range.length
                kind = .unorderedList(
                    prefixRange: NSRange(location: bodyRange.location, length: prefixLen),
                    contentRange: NSRange(location: bodyRange.location + prefixLen, length: max(0, bodyLength - prefixLen))
                )
            } else if let match = orderedRegex.firstMatch(in: lineText, range: localFull) {
                let prefixLen = match.range.length
                kind = .orderedList(
                    prefixRange: NSRange(location: bodyRange.location, length: prefixLen),
                    contentRange: NSRange(location: bodyRange.location + prefixLen, length: max(0, bodyLength - prefixLen))
                )
            } else {
                kind = .paragraph
            }

            result.append(NativeReflectionParsedLine(lineRange: lineRange, contentRange: bodyRange, kind: kind))
            location = lineRange.location + lineRange.length
        }
        return result
    }

    static func parseInlineSpans(in text: String, within range: NSRange) -> [NativeReflectionInlineSpan] {
        guard range.length > 0 else { return [] }
        let nsText = text as NSString
        var spans: [NativeReflectionInlineSpan] = []
        var occupied: [NSRange] = []

        func overlaps(_ candidate: NSRange) -> Bool {
            occupied.contains { NSIntersectionRange($0, candidate).length > 0 }
        }

        for match in codeRegex.matches(in: text, range: range) {
            guard !overlaps(match.range) else { continue }
            occupied.append(match.range)
            spans.append(.code(full: match.range, open: match.range(at: 1), content: match.range(at: 2), close: match.range(at: 3)))
        }
        for match in linkRegex.matches(in: text, range: range) {
            guard !overlaps(match.range) else { continue }
            occupied.append(match.range)
            let url = nsText.substring(with: match.range(at: 4))
            spans.append(.link(full: match.range, open: match.range(at: 1), label: match.range(at: 2), close: match.range(at: 3), url: url))
        }
        for match in boldRegex.matches(in: text, range: range) {
            guard !overlaps(match.range) else { continue }
            occupied.append(match.range)
            spans.append(.bold(full: match.range, open: match.range(at: 1), content: match.range(at: 2), close: match.range(at: 3)))
        }
        for match in strikeRegex.matches(in: text, range: range) {
            guard !overlaps(match.range) else { continue }
            occupied.append(match.range)
            spans.append(.strikethrough(full: match.range, open: match.range(at: 1), content: match.range(at: 2), close: match.range(at: 3)))
        }
        for match in italicRegex.matches(in: text, range: range) {
            guard !overlaps(match.range) else { continue }
            occupied.append(match.range)
            spans.append(.italic(full: match.range, open: match.range(at: 1), content: match.range(at: 2), close: match.range(at: 3)))
        }

        return spans.sorted { $0.fullRange.location < $1.fullRange.location }
    }
}
