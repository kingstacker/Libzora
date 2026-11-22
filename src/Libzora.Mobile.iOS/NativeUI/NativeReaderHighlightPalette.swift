import SwiftUI
import UIKit

enum NativeHighlightStyle: String, CaseIterable, Identifiable {
    case solid
    case double
    case dashed
    case dotted
    case wavy
    case marker

    var id: String { rawValue }

    var label: String {
        switch self {
        case .solid: return L("直线")
        case .double: return L("双线")
        case .dashed: return L("虚线")
        case .dotted: return L("点线")
        case .wavy: return L("波浪线")
        case .marker: return L("荧光标记")
        }
    }

    static func normalize(_ raw: String?) -> NativeHighlightStyle {
        guard let key = raw?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(),
              let style = NativeHighlightStyle(rawValue: key) else {
            return .solid
        }
        return style
    }
}

struct NativeHighlightColorOption: Identifiable, Equatable {
    let hex: String
    let title: String

    var id: String { hex }

    var color: Color {
        NativeHighlightPalette.color(for: hex) ?? .black
    }

    var isBlack: Bool { hex == "#000000" }
}

enum NativeHighlightPalette {
    static let colors: [NativeHighlightColorOption] = [
        NativeHighlightColorOption(hex: "#000000", title: L("黑白反色")),
        NativeHighlightColorOption(hex: "#FFD54F", title: L("黄色")),
        NativeHighlightColorOption(hex: "#81C784", title: L("绿色")),
        NativeHighlightColorOption(hex: "#64B5F6", title: L("蓝色")),
        NativeHighlightColorOption(hex: "#F48FB1", title: L("粉色")),
        NativeHighlightColorOption(hex: "#B39DDB", title: L("紫色")),
        NativeHighlightColorOption(hex: "#FFB74D", title: L("橙色"))
    ]

    static func normalizeHex(_ value: String?) -> String {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              trimmed.count == 7, trimmed.hasPrefix("#"),
              Int(trimmed.dropFirst(), radix: 16) != nil else {
            return "#000000"
        }
        return trimmed.uppercased()
    }

    static func color(for hex: String?) -> Color? {
        let normalized = normalizeHex(hex)
        guard let value = Int(normalized.dropFirst(), radix: 16) else { return nil }
        let red = Double((value >> 16) & 0xFF) / 255
        let green = Double((value >> 8) & 0xFF) / 255
        let blue = Double(value & 0xFF) / 255
        return Color(red: red, green: green, blue: blue)
    }

    static func accentColor(style: String?, hex: String?) -> Color? {
        let normalizedStyle = NativeHighlightStyle.normalize(style)
        let normalizedHex = normalizeHex(hex)
        guard normalizedStyle == .marker || normalizedHex != "#000000" else { return nil }
        return normalizedHex == "#000000" ? NativeTheme.ink : color(for: normalizedHex)
    }
}

struct NativeHighlightColorSwatch: View {
    let option: NativeHighlightColorOption
    let isSelected: Bool
    var size: CGFloat = 18
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            ZStack {
                if option.isBlack {
                    ZStack {
                        Circle().fill(Color.white)
                        Circle().trim(from: 0, to: 0.5).fill(Color.black).rotationEffect(.degrees(-90))
                    }
                    .frame(width: size, height: size)
                    .overlay(Circle().stroke(NativeTheme.divider.opacity(0.9), lineWidth: 0.75))
                } else {
                    Circle()
                        .fill(option.color)
                        .frame(width: size, height: size)
                        .overlay(Circle().stroke(Color.black.opacity(0.14), lineWidth: 0.5))
                }

                if isSelected {
                    Circle()
                        .strokeBorder(NativeTheme.ink, lineWidth: 1.5)
                        .frame(width: size + 5, height: size + 5)
                }
            }
            .frame(width: 28, height: 28, alignment: .center)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(option.title)
        .accessibilityValue(isSelected ? L("已选择") : "")
        .accessibilityIdentifier("reader.highlight.color.\(option.hex)")
    }
}

struct NativeHighlightStyleMenuButton: View {
    let style: NativeHighlightStyle
    let onSelect: (NativeHighlightStyle) -> Void

    var body: some View {
        Menu {
            ForEach(NativeHighlightStyle.allCases.filter { $0 != .marker }) { item in
                Button {
                    onSelect(item)
                } label: {
                    if item == style {
                        Label(item.label, systemImage: "checkmark")
                    } else {
                        Text(item.label)
                    }
                }
            }
            Divider()
            Button {
                onSelect(.marker)
            } label: {
                if style == .marker {
                    Label(NativeHighlightStyle.marker.label, systemImage: "checkmark")
                } else {
                    Text(NativeHighlightStyle.marker.label)
                }
            }
        } label: {
            HStack(spacing: 4) {
                NativeHighlightStylePreview(style: style)
                    .frame(width: 16, height: 12)
                Text(style.label)
                    .font(NativeTheme.font(12, relativeTo: .caption))
                    .lineLimit(1)
                Image(systemName: "chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(NativeTheme.secondary)
            }
            .foregroundStyle(NativeTheme.ink)
            .padding(.horizontal, 8)
            .frame(height: 28)
            .background(NativeTheme.sunk.opacity(0.7), in: Capsule())
            .contentShape(Capsule())
        }
        .accessibilityLabel(L("划线样式"))
        .accessibilityValue(style.label)
        .accessibilityIdentifier("reader.highlight.style")
    }
}

/// Compact Liquid Glass style and highlight color palette paired with the selection toolbar.
struct NativeSelectionHighlightBar: View {
    let currentStyle: NativeHighlightStyle
    let currentColorHex: String
    let onSelectStyle: (NativeHighlightStyle) -> Void
    let onSelectColor: (NativeHighlightColorOption) -> Void

    var body: some View {
        HStack(spacing: 4) {
            NativeHighlightStyleMenuButton(style: currentStyle, onSelect: onSelectStyle)
            Divider()
                .frame(height: 16)
                .padding(.horizontal, 2)
            HStack(spacing: 2) {
                ForEach(NativeHighlightPalette.colors) { option in
                    let isSelected = currentStyle == .marker && option.hex == currentColorHex
                    NativeHighlightColorSwatch(option: option, isSelected: isSelected) {
                        onSelectColor(option)
                    }
                }
            }
        }
        .padding(.horizontal, 10)
        .frame(height: NativeSelectionToolbarLayout.paletteHeight)
        .modifier(NativeLiquidGlassSurface(shape: Capsule()))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reader.highlight.palette")
    }
}

/// Inline highlight style and marker color strip inside the floating annotation editor.
struct NativeHighlightStyleColorPickerRow: View {
    @Binding var style: NativeHighlightStyle
    @Binding var colorHex: String

    var body: some View {
        HStack(spacing: 6) {
            NativeHighlightStyleMenuButton(style: style) { chosen in
                style = chosen
                if chosen != .marker && colorHex == "#000000" {
                    colorHex = "#000000"
                }
            }
            Spacer(minLength: 4)
            ForEach(NativeHighlightPalette.colors) { option in
                let isSelected = style == .marker ? option.hex == colorHex : (option.hex == colorHex && !option.isBlack)
                NativeHighlightColorSwatch(option: option, isSelected: isSelected, size: 17) {
                    colorHex = option.hex
                    if style != .marker {
                        style = .marker
                    }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reader.note.palette")
    }
}

private struct NativeHighlightStylePreview: View {
    let style: NativeHighlightStyle

    var body: some View {
        Canvas { context, size in
            let midY = size.height * 0.55
            switch style {
            case .solid:
                var path = Path()
                path.move(to: CGPoint(x: 1, y: midY))
                path.addLine(to: CGPoint(x: size.width - 1, y: midY))
                context.stroke(path, with: .foreground, lineWidth: 1.5)
            case .double:
                var top = Path()
                top.move(to: CGPoint(x: 1, y: midY - 2))
                top.addLine(to: CGPoint(x: size.width - 1, y: midY - 2))
                var bottom = Path()
                bottom.move(to: CGPoint(x: 1, y: midY + 2))
                bottom.addLine(to: CGPoint(x: size.width - 1, y: midY + 2))
                context.stroke(top, with: .foreground, lineWidth: 1.2)
                context.stroke(bottom, with: .foreground, lineWidth: 1.2)
            case .dashed:
                var path = Path()
                path.move(to: CGPoint(x: 1, y: midY))
                path.addLine(to: CGPoint(x: size.width - 1, y: midY))
                context.stroke(path, with: .foreground, style: StrokeStyle(lineWidth: 1.5, dash: [3.5, 2.5]))
            case .dotted:
                var path = Path()
                path.move(to: CGPoint(x: 1, y: midY))
                path.addLine(to: CGPoint(x: size.width - 1, y: midY))
                context.stroke(path, with: .foreground, style: StrokeStyle(lineWidth: 1.8, lineCap: .round, dash: [0.5, 3.2]))
            case .wavy:
                var path = Path()
                let step: CGFloat = 3.5
                var x: CGFloat = 1
                var up = true
                path.move(to: CGPoint(x: x, y: midY))
                while x < size.width - 1 {
                    let nextX = min(x + step, size.width - 1)
                    path.addQuadCurve(
                        to: CGPoint(x: nextX, y: midY),
                        control: CGPoint(x: (x + nextX) / 2, y: midY + (up ? -2.5 : 2.5))
                    )
                    x = nextX
                    up.toggle()
                }
                context.stroke(path, with: .foreground, lineWidth: 1.4)
            case .marker:
                let rect = CGRect(x: 1, y: 2, width: size.width - 2, height: size.height - 4)
                context.fill(Path(roundedRect: rect, cornerRadius: 2), with: .foreground)
            }
        }
    }
}
