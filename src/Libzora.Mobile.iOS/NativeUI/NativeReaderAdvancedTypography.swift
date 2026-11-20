import SwiftUI

struct NativeReaderAdvancedTypography: View {
    @ObservedObject var store: NativeStore
    let fontOptions: [ReaderFontOption]
    let close: () -> Void

    @State private var latinFontPicker = false
    @State private var loadedOptions: [ReaderFontOption] = []

    private var effectiveOptions: [ReaderFontOption] {
        loadedOptions.isEmpty ? fontOptions : loadedOptions
    }

    private var currentLatinFontName: String {
        let key = store.preferences.readerLatinFontFamily
        if key.isEmpty || key == "auto" { return L("跟随正文") }
        if let option = effectiveOptions.first(where: { $0.kind == "latin" && $0.key == key }) {
            return option.name
        }
        if key.hasPrefix("system:") {
            return String(key.dropFirst("system:".count))
        }
        return L("跟随正文")
    }

    private func preference<Value>(_ key: WritableKeyPath<MobilePreferences, Value>) -> Binding<Value> {
        Binding(get: { store.preferences[keyPath: key] }, set: { newValue in
            var value = store.preferences
            value[keyPath: key] = newValue
            store.setPreferences(value)
        })
    }

    var body: some View {
        NavigationView {
            List {
                Section(header: sectionHeader(L("西文与英文"))) {
                    Button { latinFontPicker = true } label: {
                        HStack(spacing: 12) {
                            VStack(alignment: .leading, spacing: 3) {
                                Text(L("英文字体"))
                                    .font(NativeTheme.font(15))
                                Text(L("为英文正文及混排字母单独指定西文字体"))
                                    .font(NativeTheme.font(11, relativeTo: .caption2))
                                    .foregroundStyle(NativeTheme.muted)
                            }
                            Spacer(minLength: 8)
                            Text(currentLatinFontName)
                                .font(NativeTheme.font(13))
                                .foregroundStyle(NativeTheme.secondary)
                                .lineLimit(1)
                            Image(systemName: "chevron.right")
                                .font(.system(size: 12, weight: .regular))
                                .foregroundStyle(NativeTheme.muted)
                        }
                        .frame(minHeight: 48)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("typography.latinFontFamily")

                    optionRow(
                        title: L("对齐方式"),
                        detail: L("长篇英文在窄屏上选择左对齐可避免词距过松")
                    ) {
                        Picker(L("对齐方式"), selection: preference(\.readerAlignLeft)) {
                            Text(L("两端")).tag(false)
                            Text(L("左对齐")).tag(true)
                        }
                        .pickerStyle(.segmented)
                        .frame(width: 140)
                        .accessibilityIdentifier("typography.alignment")
                    }

                    optionRow(
                        title: L("英文断词"),
                        detail: L("按英文音节自动连字符换行，减少行尾留白")
                    ) {
                        Picker(L("英文断词"), selection: preference(\.readerHyphenation)) {
                            Text(L("关")).tag(false)
                            Text(L("开")).tag(true)
                        }
                        .pickerStyle(.segmented)
                        .frame(width: 112)
                        .accessibilityIdentifier("typography.hyphenation")
                    }
                }

                Section(header: sectionHeader(L("混排与分页"))) {
                    optionRow(
                        title: L("中西间距"),
                        detail: L("在汉字与英文或数字之间自动留出呼吸空隙")
                    ) {
                        Picker(L("中西间距"), selection: preference(\.readerCjkLatinSpacing)) {
                            Text(L("关")).tag(false)
                            Text(L("开")).tag(true)
                        }
                        .pickerStyle(.segmented)
                        .frame(width: 112)
                        .accessibilityIdentifier("typography.cjkLatinSpacing")
                    }

                    optionRow(
                        title: L("孤行保护"),
                        detail: L("避免段首或段尾单行孤悬在页面边缘")
                    ) {
                        Picker(L("孤行保护"), selection: preference(\.readerAvoidWidowsAndOrphans)) {
                            Text(L("关")).tag(false)
                            Text(L("开")).tag(true)
                        }
                        .pickerStyle(.segmented)
                        .frame(width: 112)
                        .accessibilityIdentifier("typography.widowsOrphans")
                    }
                }
            }
            .listStyle(.plain)
            .accessibilityIdentifier("advancedTypography.list")
            .navigationTitle(L("高级排版"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("完成"), action: close)
                        .accessibilityIdentifier("advancedTypography.done")
                }
            }
        }
        .navigationViewStyle(.stack)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .sheet(isPresented: $latinFontPicker, onDismiss: { Task { await refreshFonts() } }) {
            NativeReaderLatinFontPicker(store: store, close: { latinFontPicker = false })
        }
    }

    private func sectionHeader(_ title: String) -> some View {
        Text(title)
            .font(NativeTheme.font(12, relativeTo: .caption))
            .foregroundStyle(NativeTheme.muted)
    }

    private func optionRow<Control: View>(
        title: String,
        detail: String,
        @ViewBuilder control: () -> Control
    ) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(NativeTheme.font(15))
                Text(detail)
                    .font(NativeTheme.font(11, relativeTo: .caption2))
                    .foregroundStyle(NativeTheme.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            control()
        }
        .padding(.vertical, 4)
    }

    private func refreshFonts() async {
        if let list: ReaderFontList = try? await store.request("reader.fonts") {
            loadedOptions = list.fonts
        }
    }
}
