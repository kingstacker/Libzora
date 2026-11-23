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
