import SwiftUI

struct StorageCategoryItem: Decodable, Identifiable, Equatable {
    let key: String
    let bytes: Int64
    let sizeText: String
    let fileCount: Int

    var id: String { key }
}

struct StorageBreakdown: Decodable, Equatable {
    let totalBytes: Int64
    let totalText: String
    let clearableCacheBytes: Int64
    let clearableCacheText: String
    let categories: [StorageCategoryItem]
}

struct NativeStoragePage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.colorScheme) private var scheme
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var breakdown: StorageBreakdown?
    @State private var loading = true
    @State private var clearing = false
    @State private var message: String?

    private var cardOuterInset: CGFloat { sizeClass == .regular ? NativeAdaptiveLayout.pageInset : 16 }
    private var detailColor: Color { store.preferences.colorfulMode ? NativeTheme.mutedOnColorful : NativeTheme.muted }
    private var secondaryColor: Color { store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary }

    private var cardDividerColor: Color {
        if store.preferences.colorfulMode {
            return scheme == .dark ? Color.white.opacity(0.10) : Color.black.opacity(0.08)
        }
        return scheme == .dark ? Color.white.opacity(0.08) : Color.black.opacity(0.07)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                if loading && breakdown == nil {
                    HStack {
                        Spacer()
                        ProgressView()
                        Spacer()
                    }
                    .padding(.vertical, 48)
                } else if let breakdown {
                    overviewCard(breakdown)

                    sectionLabel(L("存储明细"))
                    breakdownCard(breakdown)

                    sectionLabel(L("可清理缓存"))
                    cacheCard(breakdown)

                    if let message {
                        Text(message)
                            .font(NativeTheme.font(13, relativeTo: .footnote))
                            .foregroundStyle(secondaryColor)
                            .padding(.horizontal, 12)
                            .padding(.top, 10)
                    }
                } else {
                    Text(L("暂无存储数据"))
                        .font(NativeTheme.font(15))
                        .foregroundStyle(secondaryColor)
                        .frame(maxWidth: .infinity, alignment: .center)
                        .padding(.vertical, 48)
                }
            }
            .padding(.horizontal, cardOuterInset)
            .padding(.top, 12)
            .padding(.bottom, 28)
        }
        .background {
            if store.preferences.colorfulMode {
                NativeAmbientBackground()
            } else {
                Color(uiColor: .systemGroupedBackground).ignoresSafeArea()
            }
        }
        .navigationTitle(L("存储占用"))
        .navigationBarTitleDisplayMode(.inline)
        .nativeBackButton()
        .task { await loadBreakdown() }
        .refreshable { await loadBreakdown() }
    }

    private func overviewCard(_ data: StorageBreakdown) -> some View {
        let nonZero = data.categories.filter { $0.bytes > 0 }
        return card {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    Text(L("总占用"))
                        .font(NativeTheme.font(13, relativeTo: .subheadline))
                        .foregroundStyle(secondaryColor)
                    Spacer()
                    Text(data.totalText)
                        .font(NativeTheme.font(20, relativeTo: .title3).weight(.semibold))
                        .foregroundStyle(NativeTheme.ink)
                        .monospacedDigit()
                }

                StorageSegmentedBar(categories: nonZero, totalBytes: data.totalBytes, colorForKey: color(for:))
                    .frame(height: 18)

                if !nonZero.isEmpty {
                    LazyVGrid(
                        columns: [GridItem(.adaptive(minimum: 92), alignment: .leading)],
                        alignment: .leading,
                        spacing: 8
                    ) {
                        ForEach(nonZero) { item in
                            HStack(spacing: 5) {
                                Circle()
                                    .fill(color(for: item.key))
                                    .frame(width: 7, height: 7)
                                Text(label(for: item.key))
                                    .font(NativeTheme.font(11.5, relativeTo: .caption2))
                                    .foregroundStyle(secondaryColor)
                                    .lineLimit(1)
                            }
                        }
                    }
                }
            }
            .padding(14)
        }
    }

    private func breakdownCard(_ data: StorageBreakdown) -> some View {
        let sorted = data.categories.sorted { lhs, rhs in
            if lhs.bytes != rhs.bytes { return lhs.bytes > rhs.bytes }
            return lhs.key < rhs.key
        }
        return card {
            VStack(spacing: 0) {
                ForEach(Array(sorted.enumerated()), id: \.element.id) { index, item in
                    if index > 0 {
                        Rectangle()
                            .fill(cardDividerColor)
                            .frame(height: 0.5)
                            .padding(.leading, 38)
                    }
                    HStack(spacing: 11) {
                        RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                            .fill(color(for: item.key))
                            .frame(width: 10, height: 10)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(label(for: item.key))
                                .font(NativeTheme.font(14))
                                .foregroundStyle(NativeTheme.ink)
                            Text(subtitle(for: item, totalBytes: data.totalBytes))
                                .font(NativeTheme.font(11.5, relativeTo: .caption))
                                .foregroundStyle(detailColor)
                                .monospacedDigit()
                        }
                        Spacer(minLength: 8)
                        Text(item.sizeText)
                            .font(NativeTheme.font(13.5))
                            .foregroundStyle(item.bytes > 0 ? NativeTheme.ink : detailColor)
                            .monospacedDigit()
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                }
            }
        }
    }

    private func cacheCard(_ data: StorageBreakdown) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            card {
                HStack(spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(L("可清理缓存"))
                            .font(NativeTheme.font(14))
                            .foregroundStyle(NativeTheme.ink)
                        Text(data.clearableCacheText)
                            .font(NativeTheme.font(11.5, relativeTo: .caption))
                            .foregroundStyle(detailColor)
                            .monospacedDigit()
                    }
                    Spacer()
                    Button {
                        Task { await clearCache() }
                    } label: {
                        if clearing {
                            ProgressView()
                                .frame(minWidth: 64)
                        } else {
                            Text(L("清理缓存"))
                                .font(NativeTheme.font(13).weight(.medium))
                                .foregroundStyle(data.clearableCacheBytes > 0 ? NativeTheme.page : detailColor)
                                .padding(.horizontal, 13)
                                .padding(.vertical, 6)
                                .background(
                                    data.clearableCacheBytes > 0 ? NativeTheme.ink : NativeTheme.sunk,
                                    in: Capsule()
                                )
                        }
                    }
                    .buttonStyle(.plain)
                    .disabled(clearing || data.clearableCacheBytes == 0)
                    .accessibilityIdentifier("storage.clearCache")
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
            }
            Text(L("清理阅读排版缓存与日志，不会影响书籍、批注、词典和封面。"))
                .font(NativeTheme.font(11.5, relativeTo: .caption))
                .foregroundStyle(detailColor)
                .padding(.horizontal, 12)
        }
    }

    private func sectionLabel(_ title: String) -> some View {
        Text(title)
            .font(NativeTheme.font(12, relativeTo: .caption))
            .foregroundStyle(detailColor)
            .padding(.leading, 12)
            .padding(.top, 20)
            .padding(.bottom, 6)
    }

    private func card<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 20, style: .continuous)
        return VStack(spacing: 0) { content() }
            .frame(maxWidth: .infinity)
            .background {
                if store.preferences.colorfulMode {
                    shape.fill(.regularMaterial)
                } else {
                    shape.fill(Color(uiColor: .secondarySystemGroupedBackground))
                }
            }
            .clipShape(shape)
            .overlay {
                shape.stroke(
                    store.preferences.colorfulMode
                        ? Color.white.opacity(scheme == .dark ? 0.12 : 0.45)
                        : (scheme == .dark ? Color.white.opacity(0.06) : Color.black.opacity(0.03)),
                    lineWidth: 0.5
                )
                .allowsHitTesting(false)
            }
            .shadow(color: Color.black.opacity(scheme == .dark ? 0.14 : 0.025), radius: 6, y: 1.5)
    }

    private func label(for key: String) -> String {
        switch key {
        case "books": return L("书籍")
        case "dictionaries": return L("词典")
        case "fonts": return L("字体")
        case "cache": return L("缓存与封面")
        case "backupsTrash": return L("备份与回收站")
        default: return L("数据库与其他")
        }
    }

    private func subtitle(for item: StorageCategoryItem, totalBytes: Int64) -> String {
        let pct = totalBytes > 0 ? Double(item.bytes) * 100.0 / Double(totalBytes) : 0.0
        let pctText = item.bytes == 0 ? "0%" : (pct < 0.1 ? "<0.1%" : String(format: "%.1f%%", pct))
        return "\(L("{0} 个文件", item.fileCount)) · \(pctText)"
    }

    private func color(for key: String) -> Color {
        let dark = scheme == .dark
        switch key {
        case "books":
            return dark ? Color(red: 0.38, green: 0.65, blue: 0.98) : Color(red: 0.23, green: 0.51, blue: 0.96)
        case "dictionaries":
            return dark ? Color(red: 0.65, green: 0.55, blue: 0.98) : Color(red: 0.55, green: 0.36, blue: 0.96)
        case "fonts":
            return dark ? Color(red: 0.18, green: 0.83, blue: 0.75) : Color(red: 0.08, green: 0.72, blue: 0.65)
        case "cache":
            return dark ? Color(red: 0.98, green: 0.75, blue: 0.14) : Color(red: 0.96, green: 0.62, blue: 0.04)
        case "backupsTrash":
            return dark ? Color(red: 0.98, green: 0.44, blue: 0.52) : Color(red: 0.96, green: 0.25, blue: 0.37)
        default:
            return dark ? Color(red: 0.52, green: 0.55, blue: 0.60) : Color(red: 0.61, green: 0.64, blue: 0.69)
        }
    }

    private func loadBreakdown() async {
        loading = true
        defer { loading = false }
        if let result: StorageBreakdown = try? await store.request("settings.storage.get") {
            breakdown = result
        }
    }

    private func clearCache() async {
        clearing = true
        defer { clearing = false }
        if let result: StorageBreakdown = try? await store.request("settings.storage.clearCache") {
            withAnimation(.easeInOut(duration: 0.25)) {
                breakdown = result
            }
            message = L("已清理缓存")
        }
    }
}

private struct StorageSegmentedBar: View {
    let categories: [StorageCategoryItem]
    let totalBytes: Int64
    let colorForKey: (String) -> Color

    private let gap: CGFloat = 2
    private let minSegmentWidth: CGFloat = 5

    var body: some View {
        GeometryReader { proxy in
            let totalWidth = max(0, proxy.size.width)
            let shape = RoundedRectangle(cornerRadius: 7, style: .continuous)
            ZStack(alignment: .leading) {
                shape.fill(NativeTheme.sunk)
                if totalBytes > 0 && !categories.isEmpty && totalWidth > 0 {
                    let widths = segmentWidths(totalWidth: totalWidth)
                    HStack(spacing: gap) {
                        ForEach(Array(categories.enumerated()), id: \.element.id) { index, item in
                            Rectangle()
                                .fill(colorForKey(item.key))
                                .frame(width: widths[index])
                        }
                    }
                    .clipShape(shape)
                }
            }
        }
    }

    private func segmentWidths(totalWidth: CGFloat) -> [CGFloat] {
        let count = categories.count
        let totalGap = gap * CGFloat(max(0, count - 1))
        let usable = max(0, totalWidth - totalGap)
        guard usable > 0, totalBytes > 0 else { return Array(repeating: 0, count: count) }

        var raw = categories.map { usable * CGFloat($0.bytes) / CGFloat(totalBytes) }
        let floorWidth = min(minSegmentWidth, usable / CGFloat(count))
        var deficit: CGFloat = 0
        var flexibleTotal: CGFloat = 0

        for i in 0..<count {
            if raw[i] < floorWidth {
                deficit += floorWidth - raw[i]
                raw[i] = floorWidth
            } else {
                flexibleTotal += raw[i] - floorWidth
            }
        }

        if deficit > 0 && flexibleTotal > 0 {
            for i in 0..<count where raw[i] > floorWidth {
                let share = (raw[i] - floorWidth) / flexibleTotal
                raw[i] = max(floorWidth, raw[i] - deficit * share)
            }
        }

        return raw
    }
}
