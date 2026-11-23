import SwiftUI
import UIKit

/// Native, offline Markdown for AI answers. The reader runs on iOS 15, so this
/// covers block, inline, math, table and citation syntax without third-party
/// packages or newer system-only renderers.
struct NativeMarkdownView: View {
    private let blocks: [NativeMarkdownBlock]
    private let baseSize: CGFloat
    private let muted: Bool
    private let sources: [AiSource]
    private let onCitationTap: ((AiSource) -> Void)?

    init(
        _ source: String,
        fontSize: CGFloat = 16,
        muted: Bool = false,
        sources: [AiSource] = [],
        onCitationTap: ((AiSource) -> Void)? = nil
    ) {
        self.blocks = NativeMarkdownParser.parse(source)
        self.baseSize = fontSize
        self.muted = muted
        self.sources = sources
        self.onCitationTap = onCitationTap
    }

    var body: some View {
        NativeMarkdownBlocks(blocks: blocks, baseSize: baseSize, muted: muted, sources: sources)
            .textSelection(.enabled)
            .environment(\.openURL, OpenURLAction { url in
                if url.scheme == "libzora-citation",
                   let id = url.host?.uppercased(),
                   let matched = sources.first(where: { $0.id.uppercased() == id }) {
                    onCitationTap?(matched)
                    return .handled
                }
                return .systemAction
            })
    }
}

// MARK: - Blocks

private struct NativeMarkdownBlocks: View {
    let blocks: [NativeMarkdownBlock]
    let baseSize: CGFloat
    var muted = false
    var sources: [AiSource] = []

    private var textColor: Color { muted ? NativeTheme.secondary : NativeTheme.ink }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(blocks) { block in
                switch block.kind {
                case let .heading(level, text):
                    Text(NativeMarkdownInlineRenderer.attributed(text, baseSize: headingSize(level), muted: muted))
                        .font(NativeTheme.font(headingSize(level), relativeTo: level <= 2 ? .title3 : .headline))
                        .fontWeight(.semibold)
                        .foregroundStyle(textColor)
                        .fixedSize(horizontal: false, vertical: true)
                case let .paragraph(text):
                    Text(NativeMarkdownInlineRenderer.attributed(text, baseSize: baseSize, muted: muted))
                        .font(NativeTheme.font(baseSize))
                        .lineSpacing(baseSize >= 15 ? 5 : 3)
                        .foregroundStyle(textColor)
                        .fixedSize(horizontal: false, vertical: true)
                case let .list(items):
                    VStack(alignment: .leading, spacing: 7) {
                        ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                if let checked = item.checked {
                                    Image(systemName: checked ? "checkmark.square.fill" : "square")
                                        .font(.system(size: max(13, baseSize - 1), weight: .regular))
                                        .foregroundStyle(checked ? NativeTheme.secondary : NativeTheme.muted)
                                        .frame(width: 18, alignment: .trailing)
                                } else {
                                    Text(item.marker)
                                        .font(NativeTheme.font(max(13, baseSize - 1)))
                                        .foregroundStyle(NativeTheme.secondary)
                                        .frame(minWidth: 18, alignment: .trailing)
                                }
                                NativeMarkdownBlocks(blocks: item.blocks, baseSize: baseSize, muted: muted, sources: sources)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .padding(.leading, CGFloat(item.level) * 20)
                        }
                    }
                case let .code(language, text):
                    NativeMarkdownCodeBlock(language: language, code: text)
                case let .math(latex):
                    NativeMarkdownMathBlock(latex: latex, baseSize: baseSize)
                case let .quote(inner):
                    HStack(alignment: .top, spacing: 10) {
                        RoundedRectangle(cornerRadius: 2)
                            .fill(NativeTheme.strong)
                            .frame(width: 3)
                        NativeMarkdownBlocks(blocks: inner, baseSize: baseSize, muted: true, sources: sources)
                    }
                    .fixedSize(horizontal: false, vertical: true)
                case .rule:
                    Rectangle()
                        .fill(NativeTheme.divider)
                        .frame(height: 1)
                        .padding(.vertical, 2)
                case let .table(table):
                    NativeMarkdownTableBlock(table: table, baseSize: max(13, baseSize - 2), muted: muted)
                case let .image(alt, url):
                    NativeMarkdownImageBlock(alt: alt, url: url)
                }
            }
        }
    }

    private func headingSize(_ level: Int) -> CGFloat {
        let delta: CGFloat
        switch level {
        case 1: delta = 6
        case 2: delta = 4
        case 3: delta = 2
        case 4: delta = 1
        case 5: delta = 0
        default: delta = -1
        }
        return max(13, baseSize + delta)
    }
}

private struct NativeMarkdownCodeBlock: View {
    let language: String?
    let code: String
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 8) {
                Text((language ?? L("代码")).uppercased())
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
                .accessibilityLabel(copied ? L("已复制") : L("复制代码"))
            }
            ScrollView(.horizontal, showsIndicators: false) {
                Text(code)
                    .font(.system(size: 13, design: .monospaced))
                    .foregroundStyle(NativeTheme.ink)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: true, vertical: true)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(NativeTheme.surface, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(NativeTheme.divider, lineWidth: 0.5))
    }
}

private struct NativeMarkdownMathBlock: View {
    let latex: String
    let baseSize: CGFloat
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text("TeX")
                    .font(.system(size: 10, weight: .semibold, design: .monospaced))
                    .foregroundStyle(NativeTheme.muted)
                Spacer(minLength: 8)
                Button {
                    UIPasteboard.general.string = latex
                    copied = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { copied = false }
                } label: {
                    Image(systemName: copied ? "checkmark" : "doc.on.doc")
                        .font(.system(size: 12, weight: .regular))
                        .foregroundStyle(NativeTheme.secondary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(copied ? L("已复制") : L("复制公式"))
            }
            ScrollView(.horizontal, showsIndicators: false) {
                Text(NativeMarkdownInlineRenderer.mathAttributed(latex, baseSize: baseSize + 1))
                    .foregroundStyle(NativeTheme.ink)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: true, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, 2)
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
    let baseSize: CGFloat
    let muted: Bool

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
                ForEach(Array(table.rows.enumerated()), id: \.offset) { _, cells in
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
                Text(NativeMarkdownInlineRenderer.attributed(text, baseSize: baseSize, muted: muted))
                    .font(NativeTheme.font(baseSize, relativeTo: .subheadline))
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

private struct NativeMarkdownImageBlock: View {
    let alt: String
    let url: String

    private var decodedImage: UIImage? {
        guard url.lowercased().hasPrefix("data:image/"),
              let comma = url.firstIndex(of: ",") else {
            return nil
        }
        let header = String(url[..<comma]).lowercased()
        let payload = String(url[url.index(after: comma)...])
        guard header.contains(";base64"),
              let data = Data(base64Encoded: payload, options: .ignoreUnknownCharacters) else {
            return nil
        }
        return UIImage(data: data)
    }

    var body: some View {
        if let image = decodedImage {
            Image(uiImage: image)
                .resizable()
                .scaledToFit()
                .frame(maxWidth: .infinity, maxHeight: 320, alignment: .leading)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        } else {
            let label = alt.isEmpty ? "[\(L("图片"))]" : "[\(L("图片")): \(alt)]"
            Text(label)
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
        }
    }
}

// MARK: - Inline Renderer

private enum NativeMarkdownInlineRenderer {
    static func attributed(_ source: String, baseSize: CGFloat = 16, muted: Bool = false) -> AttributedString {
        let tokens = NativeMarkdownInlineParser.parse(source)
        var result = AttributedString()
        for token in tokens {
            switch token {
            case .lineBreak:
                result.append(AttributedString("\n"))
            case let .text(text, style, link):
                var part = AttributedString(text)
                if style.contains(.code) {
                    part.font = .system(size: max(11, baseSize - 2), design: .monospaced)
                    part.backgroundColor = NativeTheme.strong.opacity(0.55)
                } else {
                    if style.contains(.bold) && style.contains(.italic) {
                        part.inlinePresentationIntent = [.stronglyEmphasized, .emphasized]
                    } else if style.contains(.bold) {
                        part.inlinePresentationIntent = .stronglyEmphasized
                    } else if style.contains(.italic) {
                        part.inlinePresentationIntent = .emphasized
                    }
                    if style.contains(.strikethrough) {
                        part.strikethroughStyle = .single
                    }
                }
                if let link {
                    part.link = link
                    part.foregroundColor = Color(uiColor: .systemBlue)
                    part.underlineStyle = .single
                } else if muted {
                    part.foregroundColor = NativeTheme.secondary
                }
                result.append(part)
            case let .citation(id):
                var part = AttributedString("[\(id)]")
                part.link = URL(string: "libzora-citation://\(id)")
                part.font = .system(size: max(11, baseSize - 3), weight: .semibold, design: .monospaced)
                part.foregroundColor = Color(uiColor: .systemBlue)
                part.backgroundColor = Color(uiColor: .systemBlue).opacity(0.12)
                result.append(part)
            case let .math(latex, _):
                result.append(mathAttributed(latex, baseSize: baseSize))
            case let .image(alt, url):
                let label = alt.isEmpty ? "[\(L("图片"))]" : "[\(L("图片")): \(alt)]"
                var part = AttributedString(label)
                if let validURL = NativeMarkdownInlineParser.safeWebURL(url) {
                    part.link = validURL
                    part.foregroundColor = Color(uiColor: .systemBlue)
                    part.underlineStyle = .single
                } else {
                    part.foregroundColor = NativeTheme.secondary
                }
                result.append(part)
            }
        }
        return result
    }

    static func mathAttributed(_ latex: String, baseSize: CGFloat) -> AttributedString {
        let runs = NativeMarkdownMathFormatter.format(latex)
        guard !runs.isEmpty else { return AttributedString(latex) }
        var result = AttributedString()
        for run in runs {
            var part = AttributedString(run.text)
            switch run.script {
            case .normal:
                part.font = .system(size: baseSize, design: .serif).italic()
            case .superscript:
                part.font = .system(size: max(10, baseSize * 0.72), design: .serif)
                part.baselineOffset = baseSize * 0.34
            case .subscript:
                part.font = .system(size: max(10, baseSize * 0.72), design: .serif)
                part.baselineOffset = -baseSize * 0.22
            }
            result.append(part)
        }
        return result
    }
}
