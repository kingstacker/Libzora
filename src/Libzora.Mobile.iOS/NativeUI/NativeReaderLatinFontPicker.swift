import SwiftUI

struct NativeReaderLatinFontPicker: View {
    @ObservedObject var store: NativeStore
    let close: () -> Void
    @State private var options: [ReaderFontOption] = []
    @State private var loading = true
    @State private var error: String?

    private var latinOptions: [ReaderFontOption] {
        options.filter { $0.kind == "latin" }
    }

    private var selectedKey: String {
        let key = store.preferences.readerLatinFontFamily
        return key.isEmpty ? "auto" : key
    }

    var body: some View {
        NavigationView {
            List {
                if loading {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text(L("正在读取字体…")).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                    }
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
                } else {
                    Section {
                        Button { select("auto") } label: {
                            HStack(spacing: 12) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(L("跟随正文"))
                                        .font(NativeTheme.font(17))
                                        .lineLimit(1)
                                    Text(L("与当前正文字体保持一致"))
                                        .font(NativeTheme.font(11, relativeTo: .caption2))
                                        .foregroundStyle(NativeTheme.muted)
                                }
                                Spacer(minLength: 8)
                                if selectedKey == "auto" {
                                    Image(systemName: "checkmark").font(.system(size: 14, weight: .semibold))
                                }
                            }
                            .contentShape(Rectangle())
                            .frame(minHeight: 48)
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("latinFontPicker.option.auto")
                    }

                    if !latinOptions.isEmpty {
                        Section {
                            Text(L("系统西文字体"))
                                .font(NativeTheme.font(12, relativeTo: .caption))
                                .foregroundStyle(NativeTheme.muted)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .listRowInsets(EdgeInsets(top: 14, leading: 20, bottom: 4, trailing: 20))
                                .listRowBackground(Color.clear)
                                .listRowSeparator(.hidden)
                            ForEach(latinOptions) { option in
                                Button { select(option.key) } label: {
                                    HStack(spacing: 12) {
                                        VStack(alignment: .leading, spacing: 3) {
                                            Text(option.name)
                                                .font(previewFont(option, size: 17))
                                                .lineLimit(1)
                                            Text("The quick brown fox jumps over the lazy dog.")
                                                .font(previewFont(option, size: 13))
                                                .foregroundStyle(NativeTheme.secondary)
                                                .lineLimit(1)
                                        }
                                        Spacer(minLength: 8)
                                        if selectedKey == option.key {
                                            Image(systemName: "checkmark").font(.system(size: 14, weight: .semibold))
                                        }
                                    }
                                    .contentShape(Rectangle())
                                    .frame(minHeight: 52)
                                }
                                .buttonStyle(.plain)
                                .accessibilityIdentifier("latinFontPicker.option.\(option.key)")
                            }
                        }
                    }
                }

                if let error {
                    Text(error)
                        .font(NativeTheme.font(12))
                        .foregroundStyle(NativeTheme.warning)
                        .listRowBackground(Color.clear)
                }
            }
            .listStyle(.plain)
            .accessibilityIdentifier("latinFontPicker.list")
            .navigationTitle(L("英文字体"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("完成"), action: close).accessibilityIdentifier("latinFontPicker.done")
                }
            }
        }
        .navigationViewStyle(.stack)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .task(id: store.uiLanguage) { await load() }
    }

    private func previewFont(_ option: ReaderFontOption, size: CGFloat) -> Font {
        guard !option.previewName.isEmpty else { return NativeTheme.font(size) }
        return .custom(option.previewName, size: size)
    }

    private func select(_ key: String) {
        var prefs = store.preferences
        prefs.readerLatinFontFamily = key
        store.setPreferences(prefs)
        close()
    }

    private func load() async {
        do {
            let list: ReaderFontList = try await store.request("reader.fonts")
            options = list.fonts
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
        loading = false
    }
}
