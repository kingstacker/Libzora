import SwiftUI

/// Places the note card next to the tapped reference. The anchor arrives in
/// the rendered reader surface's coordinate space, the same space the
/// selection toolbar maps from, so a single scale accounts for any difference
/// between the Avalonia viewport and the SwiftUI overlay.
enum NativeFootnoteCardLayout {
    static func width(in size: CGSize) -> CGFloat {
        max(180, min(360, size.width - 32))
    }

    static func center(anchor: ReaderFootnoteAnchor, cardHeight: CGFloat, in size: CGSize,
                       bottomInset: CGFloat, chromeVisible: Bool) -> CGPoint {
        let width = width(in: size)
        let surfaceHeight = max(1, size.height - bottomInset)
        let scaleX = anchor.viewportWidth > 1 ? size.width / CGFloat(anchor.viewportWidth) : 1
        let scaleY = anchor.viewportHeight > 1 ? surfaceHeight / CGFloat(anchor.viewportHeight) : 1
        var x = CGFloat(anchor.x + anchor.width / 2) * scaleX
        if anchor.width <= 0 || anchor.viewportWidth <= 1 { x = size.width / 2 }
        let anchorTop = CGFloat(anchor.y) * scaleY
        let anchorBottom = CGFloat(anchor.y + anchor.height) * scaleY
        let minTop: CGFloat = chromeVisible ? 76 : 8
        let maxBottom = max(minTop + cardHeight, size.height - bottomInset - 8)
        let above = anchorTop - 10 - cardHeight
        let below = anchorBottom + 10
        var top: CGFloat
        if above >= minTop { top = above }
        else if below + cardHeight <= maxBottom { top = below }
        else { top = min(max(minTop, below), max(minTop, maxBottom - cardHeight)) }
        top = min(max(minTop, top), max(minTop, maxBottom - cardHeight))
        let half = width / 2
        x = min(max(half + 8, x), max(half + 8, size.width - half - 8))
        return CGPoint(x: x, y: top + cardHeight / 2)
    }
}

struct NativeFootnoteCardHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

private struct NativeFootnoteNoteHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

struct NativeReaderFootnoteCard: View {
    @Environment(\.locale) private var uiLocale
    let footnote: ReaderFootnote
    let cardWidth: CGFloat
    let close: () -> Void
    let jump: () -> Void
    @State private var dragOffset: CGFloat = 0
    @State private var noteHeight: CGFloat = 0
    private static let maximumNoteHeight: CGFloat = 220
    private var noteWidth: CGFloat { max(1, cardWidth - 28) }

    var body: some View {
        let _ = uiLocale

        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text(footnote.title)
                    .font(NativeTheme.font(12, relativeTo: .footnote))
                    .fontWeight(.semibold)
                    .foregroundStyle(NativeTheme.muted)
                Spacer(minLength: 8)
                if footnote.canJump {
                    Button(action: jump) {
                        Image(systemName: "arrow.up.right")
                            .font(.system(size: 13, weight: .semibold))
                            .frame(width: 30, height: 30)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(L("定位原注"))
                    .accessibilityIdentifier("reader.footnote.jump")
                }
                Button(action: close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 13, weight: .semibold))
                        .frame(width: 30, height: 30)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(L("关闭注释"))
                .accessibilityIdentifier("reader.footnote.close")
            }
            if footnote.loading {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(L("正在查找注释…")).font(NativeTheme.font(14))
                        .foregroundStyle(NativeTheme.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 6)
            } else if let text = footnote.text, !text.isEmpty {
                // Short notes get a card that hugs the text; only a note
                // taller than the cap falls back to an internal scroll view.
                if noteHeight > Self.maximumNoteHeight {
                    ScrollView { noteText(text) }
                        .frame(height: Self.maximumNoteHeight)
                } else {
                    noteText(text)
                }
            } else {
                Text(footnote.error ?? L("未找到注释内容"))
                    .font(NativeTheme.font(14))
                    .foregroundStyle(NativeTheme.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, 4)
            }
        }
        .padding(14)
        .background(alignment: .topLeading) {
            if let text = footnote.text, !text.isEmpty {
                noteText(text)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(width: noteWidth, alignment: .leading)
                    .background(GeometryReader { proxy in
                        Color.clear.preference(
                            key: NativeFootnoteNoteHeightKey.self,
                            value: proxy.size.height)
                    })
                    .hidden()
                    .accessibilityHidden(true)
            }
        }
        .onPreferenceChange(NativeFootnoteNoteHeightKey.self) { height in
            if height > 0 { noteHeight = height }
        }
        .onChange(of: footnote.id) { _ in noteHeight = 0 }
        .modifier(NativeFloatingSurface())
        .offset(y: dragOffset)
        .gesture(
            DragGesture(minimumDistance: 8)
                .onChanged { value in dragOffset = max(0, value.translation.height) }
                .onEnded { value in
                    if value.translation.height > 48 { close() }
                    else { withAnimation(.easeOut(duration: 0.15)) { dragOffset = 0 } }
                }
        )
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reader.footnote.card")
    }

    private func noteText(_ text: String) -> some View {
        Text(text)
            .font(NativeTheme.font(15))
            .lineSpacing(5)
            .textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}
