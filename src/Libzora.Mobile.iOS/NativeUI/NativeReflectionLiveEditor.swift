import SwiftUI
import UIKit

/// Live-rendered Markdown editor for book reflections. Renders headings,
/// blockquotes, lists, rules, inline formatting, links and embedded images
/// directly in the editing area while keeping canonical Markdown lossless.
struct NativeReflectionTextView: UIViewRepresentable {
    @Environment(\.nativeUiFont) private var uiFont
    @Environment(\.sizeCategory) private var sizeCategory
    @Binding var text: String
    @Binding var selection: NSRange
    let useSystemFont: Bool
    let editable: Bool

    func makeCoordinator() -> Coordinator { Coordinator(parent: self) }

    func makeUIView(context: Context) -> LiveMarkdownTextView {
        let view = LiveMarkdownTextView.makeTextKit1()
        view.delegate = context.coordinator
        view.backgroundColor = .clear
        view.textContainerInset = UIEdgeInsets(top: 14, left: 16, bottom: 28, right: 16)
        view.keyboardDismissMode = .interactive
        view.alwaysBounceVertical = true
        view.adjustsFontForContentSizeCategory = true
        view.font = NativeTheme.uiKitFont(15, useSystem: useSystemFont)
        view.textColor = LiveMarkdownStyle.inkColor
        return view
    }

    func updateUIView(_ view: LiveMarkdownTextView, context: Context) {
        let _ = uiFont
        let _ = sizeCategory
        context.coordinator.parent = self
        view.isEditable = editable
        if !editable, view.isFirstResponder { view.resignFirstResponder() }

        let currentMarkdown = context.coordinator.serializeMarkdown(from: view.attributedText)
        let styleChanged = context.coordinator.lastUseSystemFont != useSystemFont
            || context.coordinator.lastSizeCategory != sizeCategory
        context.coordinator.lastUseSystemFont = useSystemFont
        context.coordinator.lastSizeCategory = sizeCategory

        if currentMarkdown != text {
            context.coordinator.loadMarkdown(text, rawSelection: selection, into: view)
        } else if styleChanged {
            context.coordinator.applyStyling(to: view)
        } else if !view.isFirstResponder {
            let desired = context.coordinator.displayRange(forRawRange: selection, in: view)
            if view.selectedRange != desired {
                context.coordinator.suppressUpdates = true
                view.selectedRange = desired
                context.coordinator.suppressUpdates = false
                context.coordinator.applyStyling(to: view)
            }
        }
    }

    final class Coordinator: NSObject, UITextViewDelegate {
        var parent: NativeReflectionTextView
        var suppressUpdates = false
        var lastUseSystemFont: Bool?
        var lastSizeCategory: ContentSizeCategory?
        private var lastActiveSelectionSignature: (Int, Int)?

        init(parent: NativeReflectionTextView) {
            self.parent = parent
        }

        func loadMarkdown(_ markdown: String, rawSelection: NSRange, into view: LiveMarkdownTextView) {
            suppressUpdates = true
            defer { suppressUpdates = false }

            let parsed = NativeReflectionDocumentCodec.parseImages(in: markdown)
            let storage = view.textStorage
            storage.beginEditing()
            let attributed = NSMutableAttributedString(string: parsed.displayText)
            for token in parsed.tokens {
                let attachment = NativeReflectionImageAttachment(
                    markdownToken: token.rawToken,
                    alt: token.alt,
                    imageUrl: token.url
                )
                attributed.addAttribute(
                    .attachment,
                    value: attachment,
                    range: NSRange(location: token.displayLocation, length: 1)
                )
            }
            storage.setAttributedString(attributed)
            storage.endEditing()

            let displaySel = NativeReflectionDocumentCodec.displayRange(
                fromRawRange: rawSelection,
                tokens: parsed.tokens,
                displayLength: storage.length
            )
            view.selectedRange = displaySel
            lastActiveSelectionSignature = (displaySel.location, displaySel.length)
            applyStylingLocked(to: view)
        }

        func serializeMarkdown(from attributed: NSAttributedString) -> String {
            let full = NSRange(location: 0, length: attributed.length)
            guard full.length > 0 else { return "" }
            let nsString = attributed.string as NSString
            var output = ""
            attributed.enumerateAttribute(.attachment, in: full, options: []) { value, range, _ in
                if let imageAttachment = value as? NativeReflectionImageAttachment {
                    output.append(imageAttachment.markdownToken)
                } else {
                    output.append(nsString.substring(with: range))
                }
            }
            return output
        }

        private func currentTokens(in view: UITextView) -> ([NativeReflectionImageToken], Int) {
            let markdown = serializeMarkdown(from: view.attributedText)
            let parsed = NativeReflectionDocumentCodec.parseImages(in: markdown)
            return (parsed.tokens, (markdown as NSString).length)
        }

        func displayRange(forRawRange rawRange: NSRange, in view: UITextView) -> NSRange {
            let (tokens, _) = currentTokens(in: view)
            return NativeReflectionDocumentCodec.displayRange(
                fromRawRange: rawRange,
                tokens: tokens,
                displayLength: view.textStorage.length
            )
        }

        func textViewDidChange(_ textView: UITextView) {
            guard !suppressUpdates, let view = textView as? LiveMarkdownTextView else { return }
            let markdown = serializeMarkdown(from: view.attributedText)
            let parsed = NativeReflectionDocumentCodec.parseImages(in: markdown)
            let rawSel = NativeReflectionDocumentCodec.rawRange(
                fromDisplayRange: view.selectedRange,
                tokens: parsed.tokens,
                rawLength: (markdown as NSString).length
            )
            parent.text = markdown
            parent.selection = rawSel

            guard view.markedTextRange == nil else { return }
            lastActiveSelectionSignature = (view.selectedRange.location, view.selectedRange.length)
            applyStyling(to: view)
        }

        func textViewDidChangeSelection(_ textView: UITextView) {
            guard !suppressUpdates, let view = textView as? LiveMarkdownTextView else { return }
            let (tokens, rawLen) = currentTokens(in: view)
            let rawSel = NativeReflectionDocumentCodec.rawRange(
                fromDisplayRange: view.selectedRange,
                tokens: tokens,
                rawLength: rawLen
            )
            if parent.selection != rawSel {
                parent.selection = rawSel
            }
            guard view.markedTextRange == nil else { return }
            let signature = (view.selectedRange.location, view.selectedRange.length)
            guard lastActiveSelectionSignature?.0 != signature.0
                    || lastActiveSelectionSignature?.1 != signature.1 else { return }
            lastActiveSelectionSignature = signature
            applyStyling(to: view)
        }

        func applyStyling(to view: LiveMarkdownTextView) {
            guard view.markedTextRange == nil else { return }
            suppressUpdates = true
            defer { suppressUpdates = false }
            applyStylingLocked(to: view)
        }

        private func applyStylingLocked(to view: LiveMarkdownTextView) {
            let storage = view.textStorage
            let fullRange = NSRange(location: 0, length: storage.length)
            let savedSelection = view.selectedRange
            let style = LiveMarkdownStyle(useSystemFont: parent.useSystemFont)

            guard fullRange.length > 0 else {
                view.quoteRanges = []
                view.ruleRanges = []
                view.typingAttributes = style.baseTypingAttributes
                view.updateDecorations()
                return
            }

            let text = storage.string
            let lines = NativeReflectionMarkdownRanges.parseLines(in: text)
            let activeLineRange = Self.expandedLineRange(for: savedSelection, in: text as NSString)

            var quoteBlocks: [NSRange] = []
            var activeQuoteBlock: NSRange?
            var ruleRanges: [NSRange] = []

            storage.beginEditing()
            storage.addAttributes(style.baseAttributes, range: fullRange)
            storage.removeAttribute(.backgroundColor, range: fullRange)
            storage.removeAttribute(.underlineStyle, range: fullRange)
            storage.removeAttribute(.strikethroughStyle, range: fullRange)
            storage.removeAttribute(.strokeWidth, range: fullRange)
            storage.removeAttribute(.obliqueness, range: fullRange)

            for line in lines {
                let lineActive = Self.selectionIntersects(savedSelection, lineRange: line.lineRange, activeLineRange: activeLineRange)
                switch line.kind {
                case let .heading(level, prefixRange, contentRange):
                    flushQuote(&activeQuoteBlock, into: &quoteBlocks)
                    let headingFont = style.headingFont(level: level)
                    let paragraph = style.headingParagraphStyle(level: level)
                    storage.addAttribute(.paragraphStyle, value: paragraph, range: line.lineRange)
                    if contentRange.length > 0 {
                        storage.addAttributes(style.boldAttributes(font: headingFont, color: LiveMarkdownStyle.inkColor), range: contentRange)
                    }
                    styleSyntax(prefixRange, active: lineActive, storage: storage, style: style)
                    applyInlineSpans(in: text, range: contentRange, baseFont: headingFont, baseColor: LiveMarkdownStyle.inkColor, selection: savedSelection, storage: storage, style: style)

                case let .quote(prefixRange, contentRange):
                    if let current = activeQuoteBlock {
                        activeQuoteBlock = NSUnionRange(current, line.lineRange)
                    } else {
                        activeQuoteBlock = line.lineRange
                    }
                    storage.addAttribute(.paragraphStyle, value: style.quoteParagraphStyle, range: line.lineRange)
                    if contentRange.length > 0 {
                        storage.addAttribute(.foregroundColor, value: LiveMarkdownStyle.secondaryColor, range: contentRange)
                    }
                    styleSyntax(prefixRange, active: lineActive, storage: storage, style: style)
                    applyInlineSpans(in: text, range: contentRange, baseFont: style.bodyFont, baseColor: LiveMarkdownStyle.secondaryColor, selection: savedSelection, storage: storage, style: style)

                case let .unorderedList(prefixRange, contentRange):
                    flushQuote(&activeQuoteBlock, into: &quoteBlocks)
                    storage.addAttribute(.paragraphStyle, value: style.listParagraphStyle, range: line.lineRange)
                    storage.addAttribute(.foregroundColor, value: LiveMarkdownStyle.syntaxColor, range: prefixRange)
                    applyInlineSpans(in: text, range: contentRange, baseFont: style.bodyFont, baseColor: LiveMarkdownStyle.inkColor, selection: savedSelection, storage: storage, style: style)

                case let .orderedList(prefixRange, contentRange):
                    flushQuote(&activeQuoteBlock, into: &quoteBlocks)
                    storage.addAttribute(.paragraphStyle, value: style.listParagraphStyle, range: line.lineRange)
                    storage.addAttribute(.foregroundColor, value: LiveMarkdownStyle.syntaxColor, range: prefixRange)
                    applyInlineSpans(in: text, range: contentRange, baseFont: style.bodyFont, baseColor: LiveMarkdownStyle.inkColor, selection: savedSelection, storage: storage, style: style)

                case .rule:
                    flushQuote(&activeQuoteBlock, into: &quoteBlocks)
                    storage.addAttribute(.paragraphStyle, value: style.ruleParagraphStyle, range: line.lineRange)
                    if lineActive {
                        storage.addAttribute(.foregroundColor, value: LiveMarkdownStyle.syntaxColor, range: line.contentRange)
                    } else {
                        storage.addAttribute(.foregroundColor, value: UIColor.clear, range: line.contentRange)
                        ruleRanges.append(line.contentRange)
                    }

                case .paragraph:
                    flushQuote(&activeQuoteBlock, into: &quoteBlocks)
                    applyInlineSpans(in: text, range: line.contentRange, baseFont: style.bodyFont, baseColor: LiveMarkdownStyle.inkColor, selection: savedSelection, storage: storage, style: style)
                }
            }
            flushQuote(&activeQuoteBlock, into: &quoteBlocks)
            storage.endEditing()

            if view.selectedRange != savedSelection {
                let len = storage.length
                let loc = min(savedSelection.location, len)
                view.selectedRange = NSRange(location: loc, length: min(savedSelection.length, max(0, len - loc)))
            }
            view.typingAttributes = style.typingAttributesForActiveLine(lines: lines, selection: view.selectedRange)
            view.quoteRanges = quoteBlocks
            view.ruleRanges = ruleRanges
            view.updateDecorations()
        }

        private func flushQuote(_ current: inout NSRange?, into list: inout [NSRange]) {
            if let range = current {
                list.append(range)
                current = nil
            }
        }

        private func styleSyntax(_ range: NSRange, active: Bool, storage: NSTextStorage, style: LiveMarkdownStyle) {
            guard range.length > 0 else { return }
            if active {
                storage.addAttributes([
                    .font: style.syntaxFont,
                    .foregroundColor: LiveMarkdownStyle.syntaxColor,
                ], range: range)
            } else {
                storage.addAttributes([
                    .font: LiveMarkdownStyle.hiddenSyntaxFont,
                    .foregroundColor: UIColor.clear,
                ], range: range)
            }
        }

        private func applyInlineSpans(
            in text: String,
            range: NSRange,
            baseFont: UIFont,
            baseColor: UIColor,
            selection: NSRange,
            storage: NSTextStorage,
            style: LiveMarkdownStyle
        ) {
            let spans = NativeReflectionMarkdownRanges.parseInlineSpans(in: text, within: range)
            for span in spans {
                let active = Self.selectionTouchesSpan(selection, spanRange: span.fullRange)
                switch span {
                case let .bold(_, open, content, close):
                    storage.addAttributes(style.boldAttributes(font: baseFont, color: baseColor), range: content)
                    styleSyntax(open, active: active, storage: storage, style: style)
                    styleSyntax(close, active: active, storage: storage, style: style)

                case let .italic(_, open, content, close):
                    storage.addAttributes(style.italicAttributes(font: baseFont, color: baseColor), range: content)
                    styleSyntax(open, active: active, storage: storage, style: style)
                    styleSyntax(close, active: active, storage: storage, style: style)

                case let .strikethrough(_, open, content, close):
                    storage.addAttributes([
                        .strikethroughStyle: NSUnderlineStyle.single.rawValue,
                        .foregroundColor: LiveMarkdownStyle.secondaryColor,
                    ], range: content)
                    styleSyntax(open, active: active, storage: storage, style: style)
                    styleSyntax(close, active: active, storage: storage, style: style)

                case let .code(_, open, content, close):
                    storage.addAttributes([
                        .font: style.codeFont,
                        .backgroundColor: LiveMarkdownStyle.codeBackgroundColor,
                    ], range: content)
                    styleSyntax(open, active: active, storage: storage, style: style)
                    styleSyntax(close, active: active, storage: storage, style: style)

                case let .link(_, open, label, close, _):
                    storage.addAttributes([
                        .foregroundColor: UIColor.systemBlue,
                        .underlineStyle: NSUnderlineStyle.single.rawValue,
                    ], range: label)
                    styleSyntax(open, active: active, storage: storage, style: style)
                    styleSyntax(close, active: active, storage: storage, style: style)
                }
            }
        }

        private static func expandedLineRange(for selection: NSRange, in text: NSString) -> NSRange {
            let len = text.length
            guard len > 0 else { return NSRange(location: 0, length: 0) }
            let loc = min(max(0, selection.location), len)
            if loc == len && selection.length == 0 {
                // Caret at end of document: if the text ends with a newline, the
                // caret is on the new empty line after all existing lines.
                if text.character(at: len - 1) == 0x0A {
                    return NSRange(location: len, length: 0)
                }
                return text.lineRange(for: NSRange(location: len - 1, length: 0))
            }
            let clamped = NSRange(location: loc, length: min(selection.length, max(0, len - loc)))
            return text.lineRange(for: clamped)
        }

        private static func selectionIntersects(_ selection: NSRange, lineRange: NSRange, activeLineRange: NSRange) -> Bool {
            if activeLineRange.length == 0 { return false }
            return NSIntersectionRange(lineRange, activeLineRange).length > 0
        }

        private static func selectionTouchesSpan(_ selection: NSRange, spanRange: NSRange) -> Bool {
            let selStart = selection.location
            let selEnd = selection.location + selection.length
            let spanStart = spanRange.location
            let spanEnd = spanRange.location + spanRange.length
            return selStart <= spanEnd && selEnd >= spanStart
        }
    }
}

final class LiveMarkdownTextView: UITextView {
    var quoteRanges: [NSRange] = []
    var ruleRanges: [NSRange] = []

    private let quoteLayer = CAShapeLayer()
    private let ruleLayer = CAShapeLayer()
    private var lastWidth: CGFloat = 0

    static func makeTextKit1() -> LiveMarkdownTextView {
        if #available(iOS 16.0, *) {
            let view = LiveMarkdownTextView(usingTextLayoutManager: false)
            view.configureDecorationLayers()
            return view
        }
        let view = LiveMarkdownTextView()
        view.configureDecorationLayers()
        return view
    }

    private func configureDecorationLayers() {
        quoteLayer.fillColor = LiveMarkdownStyle.quoteBarColor.cgColor
        ruleLayer.fillColor = LiveMarkdownStyle.ruleColor.cgColor
        layer.addSublayer(quoteLayer)
        layer.addSublayer(ruleLayer)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        if abs(bounds.width - lastWidth) > 0.5 {
            lastWidth = bounds.width
            // Re-layout embedded image attachments when width changes (e.g. rotation).
            let full = NSRange(location: 0, length: textStorage.length)
            if full.length > 0 {
                layoutManager.invalidateLayout(forCharacterRange: full, actualCharacterRange: nil)
            }
        }
        updateDecorations()
    }

    override func traitCollectionDidChange(_ previousTraitCollection: UITraitCollection?) {
        super.traitCollectionDidChange(previousTraitCollection)
        quoteLayer.fillColor = LiveMarkdownStyle.quoteBarColor.resolvedColor(with: traitCollection).cgColor
        ruleLayer.fillColor = LiveMarkdownStyle.ruleColor.resolvedColor(with: traitCollection).cgColor
    }

    func updateDecorations() {
        quoteLayer.fillColor = LiveMarkdownStyle.quoteBarColor.resolvedColor(with: traitCollection).cgColor
        ruleLayer.fillColor = LiveMarkdownStyle.ruleColor.resolvedColor(with: traitCollection).cgColor

        let total = textStorage.length
        guard total > 0 else {
            quoteLayer.path = nil
            ruleLayer.path = nil
            return
        }

        layoutManager.ensureLayout(for: textContainer)
        let quotePath = CGMutablePath()
        for range in quoteRanges {
            let clamped = NSIntersectionRange(range, NSRange(location: 0, length: total))
            guard clamped.length > 0 else { continue }
            let glyphs = layoutManager.glyphRange(forCharacterRange: clamped, actualCharacterRange: nil)
            guard glyphs.length > 0 else { continue }
            var rect = layoutManager.boundingRect(forGlyphRange: glyphs, in: textContainer)
            rect.origin.y += textContainerInset.top
            let barRect = CGRect(
                x: textContainerInset.left + 2,
                y: rect.minY + 3,
                width: 3,
                height: max(14, rect.height - 6)
            )
            quotePath.addRoundedRect(in: barRect, cornerWidth: 1.5, cornerHeight: 1.5)
        }
        quoteLayer.path = quotePath

        let rulePath = CGMutablePath()
        let contentWidth = max(40, bounds.width - textContainerInset.left - textContainerInset.right)
        for range in ruleRanges {
            let clamped = NSIntersectionRange(range, NSRange(location: 0, length: total))
            guard clamped.length > 0 else { continue }
            let glyphs = layoutManager.glyphRange(forCharacterRange: clamped, actualCharacterRange: nil)
            guard glyphs.length > 0 else { continue }
            var rect = layoutManager.boundingRect(forGlyphRange: glyphs, in: textContainer)
            rect.origin.y += textContainerInset.top
            let lineRect = CGRect(
                x: textContainerInset.left,
                y: rect.midY,
                width: contentWidth,
                height: 1
            )
            rulePath.addRect(lineRect)
        }
        ruleLayer.path = rulePath
    }
}

private final class NativeReflectionImageAttachment: NSTextAttachment {
    private static let cache = NSCache<NSString, UIImage>()

    let markdownToken: String
    let alt: String
    let imageUrl: String

    init(markdownToken: String, alt: String, imageUrl: String) {
        self.markdownToken = markdownToken
        self.alt = alt
        self.imageUrl = imageUrl
        super.init(data: nil, ofType: nil)
        self.image = Self.decodeRoundedImage(from: imageUrl)
    }

    required init?(coder: NSCoder) {
        self.markdownToken = ""
        self.alt = ""
        self.imageUrl = ""
        super.init(coder: coder)
    }

    override func attachmentBounds(
        for textContainer: NSTextContainer?,
        proposedLineFragment lineFrag: CGRect,
        glyphPosition position: CGPoint,
        characterIndex charIndex: Int
    ) -> CGRect {
        guard let image else { return CGRect(x: 0, y: 0, width: 120, height: 36) }
        let availableWidth = max(120, (textContainer?.size.width ?? lineFrag.width) - 8)
        let maxHeight: CGFloat = 280
        let natural = image.size
        guard natural.width > 0, natural.height > 0 else {
            return CGRect(x: 0, y: 0, width: availableWidth, height: 120)
        }
        let scale = min(1, min(availableWidth / natural.width, maxHeight / natural.height))
        return CGRect(x: 0, y: 0, width: floor(natural.width * scale), height: floor(natural.height * scale))
    }

    private static func decodeRoundedImage(from url: String) -> UIImage? {
        let key = "\(url.count):\(url.prefix(48)):\(url.suffix(32))" as NSString
        if let cached = cache.object(forKey: key) { return cached }
        guard url.lowercased().hasPrefix("data:image/"),
              let comma = url.firstIndex(of: ",") else { return nil }
        let header = String(url[..<comma]).lowercased()
        guard header.contains(";base64") else { return nil }
        let payload = String(url[url.index(after: comma)...])
        guard let data = Data(base64Encoded: payload, options: .ignoreUnknownCharacters),
              let raw = UIImage(data: data) else { return nil }
        let rounded = renderRounded(raw, radius: 10)
        cache.setObject(rounded, forKey: key)
        return rounded
    }

    private static func renderRounded(_ image: UIImage, radius: CGFloat) -> UIImage {
        let maxSide: CGFloat = 960
        let size = image.size
        let longest = max(size.width, size.height)
        let scale = longest > maxSide && longest > 0 ? maxSide / longest : 1
        let target = CGSize(width: max(1, floor(size.width * scale)), height: max(1, floor(size.height * scale)))
        let renderer = UIGraphicsImageRenderer(size: target)
        return renderer.image { _ in
            let rect = CGRect(origin: .zero, size: target)
            UIBezierPath(roundedRect: rect, cornerRadius: radius).addClip()
            image.draw(in: rect)
        }
    }
}

private struct LiveMarkdownStyle {
    static let hiddenSyntaxFont = UIFont.systemFont(ofSize: 0.01)

    static let inkColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0xed / 255.0, green: 0xed / 255.0, blue: 0xed / 255.0, alpha: 1)
            : UIColor(red: 0x1c / 255.0, green: 0x1c / 255.0, blue: 0x1c / 255.0, alpha: 1)
    }

    static let secondaryColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0xb0 / 255.0, green: 0xb0 / 255.0, blue: 0xb0 / 255.0, alpha: 1)
            : UIColor(red: 0x5e / 255.0, green: 0x5e / 255.0, blue: 0x5e / 255.0, alpha: 1)
    }

    static let syntaxColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(white: 1, alpha: 0.36)
            : UIColor(white: 0, alpha: 0.32)
    }

    static let quoteBarColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0x4a / 255.0, green: 0x4a / 255.0, blue: 0x4a / 255.0, alpha: 1)
            : UIColor(red: 0xd0 / 255.0, green: 0xd0 / 255.0, blue: 0xd0 / 255.0, alpha: 1)
    }

    static let ruleColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(white: 1, alpha: 0.14)
            : UIColor(white: 0, alpha: 0.10)
    }

    static let codeBackgroundColor = UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(white: 1, alpha: 0.12)
            : UIColor(white: 0, alpha: 0.06)
    }

    let useSystemFont: Bool
    let bodyFont: UIFont
    let syntaxFont: UIFont
    let codeFont: UIFont
    let baseParagraphStyle: NSParagraphStyle
    let quoteParagraphStyle: NSParagraphStyle
    let listParagraphStyle: NSParagraphStyle
    let ruleParagraphStyle: NSParagraphStyle

    init(useSystemFont: Bool) {
        self.useSystemFont = useSystemFont
        let metrics = UIFontMetrics(forTextStyle: .body)
        self.bodyFont = metrics.scaledFont(for: NativeTheme.uiKitFont(15.5, useSystem: useSystemFont))
        self.syntaxFont = metrics.scaledFont(for: NativeTheme.uiKitFont(14, useSystem: useSystemFont))
        self.codeFont = metrics.scaledFont(for: UIFont.monospacedSystemFont(ofSize: 13.5, weight: .regular))

        let base = NSMutableParagraphStyle()
        base.lineSpacing = 5
        base.paragraphSpacing = 6
        self.baseParagraphStyle = base

        let quote = NSMutableParagraphStyle()
        quote.lineSpacing = 4.5
        quote.paragraphSpacing = 4
        quote.firstLineHeadIndent = 14
        quote.headIndent = 14
        self.quoteParagraphStyle = quote

        let list = NSMutableParagraphStyle()
        list.lineSpacing = 4.5
        list.paragraphSpacing = 4
        list.firstLineHeadIndent = 4
        list.headIndent = 18
        self.listParagraphStyle = list

        let rule = NSMutableParagraphStyle()
        rule.paragraphSpacingBefore = 6
        rule.paragraphSpacing = 6
        self.ruleParagraphStyle = rule
    }

    var baseAttributes: [NSAttributedString.Key: Any] {
        [
            .font: bodyFont,
            .foregroundColor: Self.inkColor,
            .paragraphStyle: baseParagraphStyle,
        ]
    }

    var baseTypingAttributes: [NSAttributedString.Key: Any] { baseAttributes }

    func headingFont(level: Int) -> UIFont {
        let size: CGFloat
        switch level {
        case 1: size = 22
        case 2: size = 19.5
        case 3: size = 17.5
        default: size = 16
        }
        let metrics = UIFontMetrics(forTextStyle: level <= 2 ? .title3 : .headline)
        if useSystemFont {
            return metrics.scaledFont(for: UIFont.systemFont(ofSize: size, weight: .semibold))
        }
        return metrics.scaledFont(for: NativeTheme.uiKitFont(size, useSystem: false))
    }

    func headingParagraphStyle(level: Int) -> NSParagraphStyle {
        let style = NSMutableParagraphStyle()
        style.lineSpacing = 3
        style.paragraphSpacingBefore = level == 1 ? 10 : 7
        style.paragraphSpacing = 5
        return style
    }

    func boldAttributes(font: UIFont, color: UIColor) -> [NSAttributedString.Key: Any] {
        if useSystemFont {
            let bold = UIFont.systemFont(ofSize: font.pointSize, weight: .semibold)
            return [.font: bold, .foregroundColor: color]
        }
        // KingHwaOldSong ships a single regular face; negative strokeWidth adds
        // crisp fill+stroke weight while preserving the serif typeface.
        return [.font: font, .foregroundColor: color, .strokeWidth: -2.6, .strokeColor: color]
    }

    func italicAttributes(font: UIFont, color: UIColor) -> [NSAttributedString.Key: Any] {
        if useSystemFont,
           let descriptor = font.fontDescriptor.withSymbolicTraits(.traitItalic) {
            return [.font: UIFont(descriptor: descriptor, size: font.pointSize), .foregroundColor: color]
        }
        return [.font: font, .foregroundColor: color, .obliqueness: 0.18]
    }

    func typingAttributesForActiveLine(lines: [NativeReflectionParsedLine], selection: NSRange) -> [NSAttributedString.Key: Any] {
        let caret = selection.location
        if let line = lines.first(where: { caret >= $0.lineRange.location && caret <= $0.lineRange.location + $0.lineRange.length }) {
            switch line.kind {
            case let .heading(level, prefix, _):
                if caret >= prefix.location + prefix.length {
                    var attrs = boldAttributes(font: headingFont(level: level), color: Self.inkColor)
                    attrs[.paragraphStyle] = headingParagraphStyle(level: level)
                    return attrs
                }
            case let .quote(prefix, _):
                if caret >= prefix.location + prefix.length {
                    return [
                        .font: bodyFont,
                        .foregroundColor: Self.secondaryColor,
                        .paragraphStyle: quoteParagraphStyle,
                    ]
                }
            default:
                break
            }
        }
        return baseTypingAttributes
    }
}
