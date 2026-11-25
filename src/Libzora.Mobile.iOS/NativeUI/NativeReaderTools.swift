import SwiftUI
import UIKit
import WebKit

private func dictionaryHtmlPalette(colorScheme: ColorScheme, isCard: Bool) -> [String: String] {
    if colorScheme == .dark {
        return [
            "paper": isCard ? "#1e1e1e" : "#1c1c1e",
            "ink": "#ededed",
            "muted": "#8c8c8c",
            "soft": "#2a2a2a",
            "border": "#262626"
        ]
    }
    return [
        "paper": "#ffffff",
        "ink": "#1c1c1c",
        "muted": "#767676",
        "soft": "#f5f5f5",
        "border": "#efefef"
    ]
}

private func dictionaryWebBackground(colorScheme: ColorScheme, isCard: Bool) -> UIColor {
    if colorScheme == .dark {
        return isCard
            ? UIColor(red: 0x1e / 255.0, green: 0x1e / 255.0, blue: 0x1e / 255.0, alpha: 1)
            : .secondarySystemGroupedBackground
    }
    return .white
}

private struct NativeDictionaryWebView: UIViewRepresentable {
    let html: String
    let backgroundColor: UIColor
    let onLookupTerm: (String) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(onLookupTerm: onLookupTerm)
    }

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.defaultWebpagePreferences.allowsContentJavaScript = true
        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = context.coordinator
        webView.isOpaque = false
        webView.backgroundColor = backgroundColor
        webView.scrollView.backgroundColor = backgroundColor
        webView.scrollView.contentInsetAdjustmentBehavior = .never
        return webView
    }

    func updateUIView(_ webView: WKWebView, context: Context) {
        context.coordinator.onLookupTerm = onLookupTerm
        webView.backgroundColor = backgroundColor
        webView.scrollView.backgroundColor = backgroundColor
        if context.coordinator.lastHtml != html {
            context.coordinator.lastHtml = html
            webView.loadHTMLString(html, baseURL: URL(string: "https://libzora.invalid/dictionary/"))
        }
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        var onLookupTerm: (String) -> Void
        var lastHtml = ""

        init(onLookupTerm: @escaping (String) -> Void) {
            self.onLookupTerm = onLookupTerm
        }

        func webView(_ webView: WKWebView,
                     decidePolicyFor navigationAction: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard let url = navigationAction.request.url else {
                decisionHandler(.cancel)
                return
            }
            if url.scheme == "libzora-dictionary" {
                if let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
                   let raw = components.percentEncodedQuery?.removingPercentEncoding,
                   !raw.isEmpty, raw.count <= 256 {
                    DispatchQueue.main.async { self.onLookupTerm(raw) }
                }
                decisionHandler(.cancel)
                return
            }
            if url.absoluteString == "https://libzora.invalid/dictionary/"
                || url.scheme == "about"
                || url.absoluteString.lowercased().hasPrefix("data:text/html") {
                decisionHandler(.allow)
                return
            }
            decisionHandler(.cancel)
        }
    }
}

/// Justified, selectable text view for Wikipedia extracts so both leading and
/// trailing edges align cleanly with the floating card boundaries.
private struct NativeJustifiedText: UIViewRepresentable {
    @Environment(\.nativeUiFont) private var uiFont
    let text: String
    let useSystemFont: Bool

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> SelfSizingTextView {
        let view = SelfSizingTextView()
        view.isEditable = false
        view.isSelectable = true
        view.isScrollEnabled = false
        view.backgroundColor = .clear
        view.textContainerInset = .zero
        view.textContainer.lineFragmentPadding = 0
        view.adjustsFontForContentSizeCategory = true
        view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        view.setContentHuggingPriority(.required, for: .vertical)
        view.setContentCompressionResistancePriority(.required, for: .vertical)
        return view
    }

    func updateUIView(_ view: SelfSizingTextView, context: Context) {
        let _ = uiFont
        if context.coordinator.lastText == text && context.coordinator.lastUseSystemFont == useSystemFont {
            return
        }
        context.coordinator.lastText = text
        context.coordinator.lastUseSystemFont = useSystemFont
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .justified
        paragraph.lineSpacing = 5
        paragraph.paragraphSpacing = 6
        paragraph.lineBreakMode = .byWordWrapping
        let font = UIFontMetrics(forTextStyle: .body)
            .scaledFont(for: NativeTheme.uiKitFont(15, useSystem: useSystemFont))
        let ink = UIColor { traits in
            traits.userInterfaceStyle == .dark
                ? UIColor(red: 0xed / 255.0, green: 0xed / 255.0, blue: 0xed / 255.0, alpha: 1)
                : UIColor(red: 0x1c / 255.0, green: 0x1c / 255.0, blue: 0x1c / 255.0, alpha: 1)
        }
        view.attributedText = NSAttributedString(
            string: text,
            attributes: [.font: font, .foregroundColor: ink, .paragraphStyle: paragraph]
        )
        view.invalidateIntrinsicContentSize()
    }

    @available(iOS 16.0, *)
    func sizeThatFits(_ proposal: ProposedViewSize, uiView: SelfSizingTextView, context: Context) -> CGSize? {
        let width = proposal.width ?? uiView.bounds.width
        guard width > 0 else { return nil }
        let fitted = uiView.sizeThatFits(CGSize(width: width, height: .greatestFiniteMagnitude))
        return CGSize(width: width, height: ceil(fitted.height))
    }

    final class Coordinator {
        var lastText = ""
        var lastUseSystemFont: Bool?
    }

    final class SelfSizingTextView: UITextView {
        private var lastLayoutWidth: CGFloat = 0

        override var intrinsicContentSize: CGSize {
            let width = bounds.width > 0 ? bounds.width : UIScreen.main.bounds.width
            let fitted = sizeThatFits(CGSize(width: width, height: .greatestFiniteMagnitude))
            return CGSize(width: UIView.noIntrinsicMetric, height: ceil(fitted.height))
        }

        override func layoutSubviews() {
            super.layoutSubviews()
            if bounds.width > 0 && abs(bounds.width - lastLayoutWidth) > 0.5 {
                lastLayoutWidth = bounds.width
                invalidateIntrinsicContentSize()
            }
        }
    }
}

struct NativeDictionary: View {
    @ObservedObject var store: NativeStore
    @Environment(\.colorScheme) private var colorScheme
    @State private var term: String
    @State private var entries: [DictionaryEntry] = []
    @State private var renderedHtml: String?
    @State private var dictionaries: [DictionaryRecord] = []
    @State private var localBusy = false
    @State private var managementBusy = false
    @State private var localStatus = ""
    @State private var lookupRevision = 0
    init(store: NativeStore, initialTerm: String) { self.store = store; _term = State(initialValue: initialTerm) }
    var body: some View {
        NativeForm {
            Section {
                HStack {
                    TextField(L("查询词语"), text: $term)
                        .onSubmit { lookup() }
                        .accessibilityIdentifier("reader.dictionary.query")
                    if !term.isEmpty {
                        Button(action: clearLookup) {
                            Image(systemName: "xmark.circle.fill")
                                .foregroundStyle(NativeTheme.muted)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(L("清空查询"))
                        .accessibilityIdentifier("reader.dictionary.clear")
                    }
                    Button(L("查询")) { lookup() }
                        .foregroundStyle(Color(uiColor: .systemBlue))
                        .buttonStyle(.borderless)
                }
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    if localBusy { ProgressView(L("正在查本地词典…")) }
                    if !localStatus.isEmpty {
                        Text(localStatus)
                            .font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
                    }
                }
            }
            if let renderedHtml {
                Section(L("词典释义")) {
                    NativeDictionaryWebView(
                        html: renderedHtml,
                        backgroundColor: dictionaryWebBackground(colorScheme: colorScheme, isCard: false)
                    ) { linkedTerm in
                        term = linkedTerm
                        lookup()
                    }
                    .frame(height: 360)
                    .listRowInsets(EdgeInsets(top: 4, leading: 6, bottom: 4, trailing: 6))
                }
            } else {
                ForEach(Array(entries.enumerated()), id: \.offset) { _, entry in
                    Section(entry.term + " · " + entry.dictionaryName) { Text(entry.definition).textSelection(.enabled) }
                }
            }
            Section {
                ForEach(dictionaries) { dictionary in
                    VStack(alignment: .leading, spacing: 8) {
                        let title = [dictionary.name, dictionary.sourceFormat]
                            .compactMap { $0 }
                            .joined(separator: " · ")
                        Toggle(title + L(" · {0} 条", dictionary.entryCount), isOn: Binding(get: { dictionary.enabled }, set: { enabled in action("toggle", ["id": dictionary.id, "enabled": enabled]) })).tint(NativeTheme.systemSwitchTint)
                            .accessibilityIdentifier("dictionary.enabled.\(dictionary.id)")
                        Button(L("移除"), role: .destructive) { action("remove", ["id": dictionary.id]) }.buttonStyle(.borderless)
                    }
                }
                if dictionaries.isEmpty { Text(L("尚未导入词典")).foregroundStyle(NativeTheme.secondary) }
                Button { action("import") } label: {
                    HStack {
                        Text(L("导入词典")).foregroundStyle(Color(uiColor: .systemBlue))
                        Spacer()
                    }.frame(minHeight: 26).contentShape(Rectangle())
                }
            } header: {
                Text(L("词典管理"))
            } footer: {
                VStack(alignment: .leading, spacing: 6) {
                    if managementBusy {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text(L("正在处理词典…"))
                        }
                    }
                    Text(L("支持 MDX（可同时选择配套 CSS）、TXT、TSV、CSV 等词典格式。"))
                }
                .font(NativeTheme.font(12, relativeTo: .caption))
                .foregroundStyle(store.preferences.colorfulMode ? NativeTheme.secondaryOnColorful : NativeTheme.secondary)
            }
            .disabled(managementBusy)
        }.navigationTitle(L("本地词典")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone())
            .task {
                do { dictionaries = try await store.request("dictionary.get") }
                catch { if !Task.isCancelled { localStatus = error.localizedDescription } }
                if !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { lookup() }
            }
            .onChange(of: colorScheme) { _ in
                if renderedHtml != nil && !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    lookup()
                }
            }
    }
    private func lookup() {
        let query = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty, !managementBusy else { return }
        lookupRevision += 1
        let revision = lookupRevision
        entries = []
        renderedHtml = nil
        localBusy = true
        localStatus = L("正在查本地词典…")
        let palette = dictionaryHtmlPalette(colorScheme: colorScheme, isCard: false)
        Task { @MainActor in
            do {
                let payload: DictionaryLookupPayload = try await store.request(
                    "dictionary.lookupRendered",
                    ["term": query, "palette": palette]
                )
                guard revision == lookupRevision else { return }
                entries = payload.entries
                renderedHtml = payload.renderedHtml
                let count = payload.entryCount ?? payload.entries.count
                localStatus = count == 0 ? L("没有找到本地释义。请先在词典管理中导入词典。") : ""
            } catch {
                guard !Task.isCancelled, revision == lookupRevision else { return }
                localStatus = L("本地词典查询失败：{0}", error.localizedDescription)
            }
            if revision == lookupRevision { localBusy = false }
        }
    }
    private func clearLookup() {
        lookupRevision += 1
        term = ""
        entries = []
        renderedHtml = nil
        localBusy = false
        localStatus = ""
    }
    private func action(_ command: String, _ args: [String: Any] = [:]) {
        guard !managementBusy else { return }
        managementBusy = true
        store.perform {
            defer { managementBusy = false }
            try await store.command("dictionary.\(command)", args)
            dictionaries = try await store.request("dictionary.get")
            managementBusy = false
            if !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { lookup() }
        }
    }
}

/// Compact floating lookup card inside the reader matching the desktop popup:
/// switch cleanly between local dictionaries and Wikipedia without leaving the page.
struct NativeFloatingDictionaryCard: View {
    @ObservedObject var store: NativeStore
    let close: () -> Void
    @Environment(\.colorScheme) private var colorScheme
    @State private var term: String
    @State private var activeTab = 0
    @State private var entries: [DictionaryEntry] = []
    @State private var renderedHtml: String?
    @State private var wikipedia: WikipediaArticle?
    @State private var localBusy = false
    @State private var wikipediaBusy = false
    @State private var localStatus = ""
    @State private var wikipediaStatus = ""
    @State private var lookupRevision = 0

    init(store: NativeStore, initialTerm: String, close: @escaping () -> Void) {
        self.store = store
        self.close = close
        _term = State(initialValue: initialTerm)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass")
                        .font(.system(size: 14, weight: .regular))
                        .foregroundStyle(NativeTheme.muted)
                    TextField(L("查询词语"), text: $term)
                        .font(NativeTheme.font(15, relativeTo: .body))
                        .submitLabel(.search)
                        .onSubmit { lookup() }
                        .accessibilityIdentifier("reader.dictionary.query")
                    if !term.isEmpty {
                        Button(action: clearLookup) {
                            Image(systemName: "xmark.circle.fill")
                                .font(.system(size: 15))
                                .foregroundStyle(NativeTheme.muted)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(L("清空查询"))
                        .accessibilityIdentifier("reader.dictionary.clear")
                    }
                }
                .padding(.horizontal, 10)
                .frame(height: 36)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(NativeTheme.divider, lineWidth: 0.5))

                Button(L("查询")) { lookup() }
                    .font(NativeTheme.font(14, relativeTo: .subheadline))
                    .foregroundStyle(NativeTheme.ink)
                    .buttonStyle(.plain)
                    .padding(.horizontal, 6)
                    .frame(height: 36)

                Button(action: close) {
                    Image(systemName: "xmark")
                        .font(.system(size: 16, weight: .regular))
                        .foregroundStyle(NativeTheme.secondary)
                        .frame(width: 28, height: 28, alignment: .center)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(L("关闭"))
                .accessibilityIdentifier("reader.dictionary.close")
            }

            Picker("", selection: $activeTab) {
                Text(L("本地词典")).tag(0)
                Text(L("维基百科")).tag(1)
            }
            .pickerStyle(.segmented)
            .accessibilityIdentifier("reader.dictionary.tabs")

            Group {
                if activeTab == 0, let renderedHtml {
                    NativeDictionaryWebView(
                        html: renderedHtml,
                        backgroundColor: dictionaryWebBackground(colorScheme: colorScheme, isCard: true)
                    ) { linkedTerm in
                        term = linkedTerm
                        lookup()
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 12) {
                            if activeTab == 0 {
                                localContent
                            } else {
                                wikipediaContent
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.leading, 4)
                        .padding(.trailing, 8)
                        .padding(.vertical, 4)
                    }
                }
            }
            .frame(minHeight: 150, maxHeight: 240)
            .accessibilityIdentifier(activeTab == 1 ? "reader.dictionary.wikipedia" : "reader.dictionary.local")
        }
        .padding(16)
        .modifier(NativeFloatingSurface())
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("reader.dictionary.card")
        .task {
            if !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { lookup() }
        }
        .onChange(of: colorScheme) { _ in
            if renderedHtml != nil && !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                lookup()
            }
        }
    }

    @ViewBuilder private var localContent: some View {
        if localBusy {
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text(L("正在查本地词典…"))
                    .font(NativeTheme.font(13, relativeTo: .footnote))
                    .foregroundStyle(NativeTheme.secondary)
            }
        } else if !entries.isEmpty {
            ForEach(Array(entries.enumerated()), id: \.offset) { index, entry in
                if index > 0 { Divider() }
                VStack(alignment: .leading, spacing: 4) {
                    Text(entry.term + " · " + entry.dictionaryName)
                        .font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.secondary)
                    Text(entry.definition)
                        .font(NativeTheme.font(15, relativeTo: .body))
                        .foregroundStyle(NativeTheme.ink)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        } else if !localStatus.isEmpty {
            Text(localStatus)
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder private var wikipediaContent: some View {
        if wikipediaBusy {
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text(L("正在查询维基百科…"))
                    .font(NativeTheme.font(13, relativeTo: .footnote))
                    .foregroundStyle(NativeTheme.secondary)
            }
        } else if let wikipedia {
            VStack(alignment: .leading, spacing: 10) {
                Text(wikipedia.title)
                    .font(NativeTheme.font(17, relativeTo: .headline))
                    .foregroundStyle(NativeTheme.ink)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                NativeJustifiedText(
                    text: wikipedia.extract,
                    useSystemFont: store.preferences.uiFont == 1
                )
                .frame(maxWidth: .infinity, alignment: .leading)
                HStack(alignment: .center, spacing: 8) {
                    Text(L("来源：维基百科 · CC BY-SA 4.0"))
                        .font(NativeTheme.font(11, relativeTo: .caption2))
                        .foregroundStyle(NativeTheme.muted)
                        .lineLimit(2)
                    Spacer(minLength: 8)
                    Link(destination: wikipedia.url) {
                        HStack(spacing: 4) {
                            Text(L("在维基百科中打开"))
                            Image(systemName: "arrow.up.right.square")
                        }
                        .font(NativeTheme.font(13, relativeTo: .footnote))
                    }
                    .accessibilityIdentifier("reader.dictionary.wikipedia.open")
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("reader.dictionary.wikipedia.result")
        } else if !wikipediaStatus.isEmpty {
            Text(wikipediaStatus)
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func lookup() {
        let query = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return }
        lookupRevision += 1
        let revision = lookupRevision
        entries = []
        renderedHtml = nil
        wikipedia = nil
        localBusy = true
        wikipediaBusy = true
        localStatus = L("正在查本地词典…")
        wikipediaStatus = L("正在查询维基百科…")
        let palette = dictionaryHtmlPalette(colorScheme: colorScheme, isCard: true)
        Task { @MainActor in
            do {
                let payload: DictionaryLookupPayload = try await store.request(
                    "dictionary.lookupRendered",
                    ["term": query, "palette": palette]
                )
                guard revision == lookupRevision else { return }
                entries = payload.entries
                renderedHtml = payload.renderedHtml
                let count = payload.entryCount ?? payload.entries.count
                localStatus = count == 0 ? L("没有找到本地释义。请先在词典管理中导入词典。") : ""
            } catch {
                guard !Task.isCancelled, revision == lookupRevision else { return }
                localStatus = L("本地词典查询失败：{0}", error.localizedDescription)
            }
            if revision == lookupRevision { localBusy = false }
        }
        Task { @MainActor in
            do {
                let value: [WikipediaArticle] = try await store.request("dictionary.wikipedia", ["term": query])
                guard revision == lookupRevision else { return }
                wikipedia = value.first
                wikipediaStatus = value.isEmpty ? L("没有找到同名维基百科条目。普通词义请查本地词典。") : ""
            } catch {
                guard !Task.isCancelled, revision == lookupRevision else { return }
                wikipediaStatus = L("维基百科查询失败：{0}", error.localizedDescription)
            }
            if revision == lookupRevision { wikipediaBusy = false }
        }
    }

    private func clearLookup() {
        lookupRevision += 1
        term = ""
        entries = []
        renderedHtml = nil
        wikipedia = nil
        localBusy = false
        wikipediaBusy = false
        localStatus = ""
        wikipediaStatus = ""
    }
}

struct NativeTxtReview: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let review: TxtReview
    @State private var rules = TxtRules()
    @State private var page = 0
    @State private var preview: TxtPreview?
    @State private var error: String?
    @State private var busy = false
    @State private var finished = false
    private var revision: String { "\(rules.encoding):\(rules.headingPreset):\(rules.customHeadingPattern):\(rules.recognizeVolumes):\(page)" }
    var body: some View {
        NavigationView {
            NativeForm {
                Section { Text(review.name).font(NativeTheme.font(17, relativeTo: .headline))
                    Picker(L("编码"), selection: $rules.encoding) { Text(L("自动识别")).tag(0); Text("UTF-8").tag(1); Text("UTF-16 LE").tag(2); Text("UTF-16 BE").tag(3); Text("GB18030").tag(4) }
                    Picker(L("章节规则"), selection: $rules.headingPreset) { Text(L("自动识别")).tag(0); Text(L("中文章节")).tag(1); Text(L("英文章节")).tag(2); Text(L("序言和尾声")).tag(3); Text(L("不识别章节")).tag(4); Text(L("自定义正则")).tag(5) }
                    if rules.headingPreset == 5 { TextField(L("章节正则"), text: $rules.customHeadingPattern).textInputAutocapitalization(.never).disableAutocorrection(true) }
                    Toggle(L("识别分卷"), isOn: $rules.recognizeVolumes).tint(NativeTheme.systemSwitchTint)
                }
                Section(L("目录预览")) {
                    if let preview {
                        Text(L("{0} 个目录项 · {1}", preview.chapterCount, preview.detectedEncoding)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        ForEach(preview.chapters) { chapter in Text((chapter.isVolume ? L("卷 · ") : "") + chapter.title) }
                        HStack { Button(L("上一页")) { page -= 1 }.disabled(!preview.hasPreviousPage); Spacer(); Button(L("下一页")) { page += 1 }.disabled(!preview.hasNextPage) }.buttonStyle(.borderless)
                    } else if let error { Text(error).foregroundStyle(NativeTheme.secondary) } else { ProgressView(L("正在识别章节…")) }
                }
            }.navigationTitle(L("TXT 导入")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).disabled(busy)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button(L("取消")) { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button(L("导入")) {
                        busy = true
                        store.perform {
                            defer { busy = false }
                            try await store.command("txt.complete", ["id": review.id, "rules": try requestObject(rules)])
                            finished = true
                        }
                    }.disabled(busy || preview == nil) }
                }
                .task(id: revision) {
                    preview = nil; error = nil
                    do { let value: TxtPreview = try await store.request("txt.preview", ["id": review.id, "rules": try requestObject(rules), "page": page]); if !Task.isCancelled { preview = value } }
                    catch { if !Task.isCancelled { self.error = error.localizedDescription } }
                }
                .onChange(of: rules) { _ in page = 0 }
                .onDisappear { if !finished { store.perform { try await store.command("txt.cancel", ["id": review.id]) } } }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
    }
}
