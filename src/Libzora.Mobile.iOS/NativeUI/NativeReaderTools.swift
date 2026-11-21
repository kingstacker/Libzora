import SwiftUI
import UIKit

struct NativeDictionary: View {
    @ObservedObject var store: NativeStore
    @State private var term: String
    @State private var entries: [DictionaryEntry] = []
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
            ForEach(Array(entries.enumerated()), id: \.offset) { _, entry in
                Section(entry.term + " · " + entry.dictionaryName) { Text(entry.definition).textSelection(.enabled) }
            }
            Section(L("词典管理")) {
                ForEach(dictionaries) { dictionary in
                    VStack(alignment: .leading, spacing: 8) {
                        Toggle(dictionary.name + L(" · {0} 条", dictionary.entryCount), isOn: Binding(get: { dictionary.enabled }, set: { enabled in action("toggle", ["id": dictionary.id, "enabled": enabled]) })).tint(NativeTheme.systemSwitchTint)
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
            }
            .disabled(managementBusy)
        }.navigationTitle(L("本地词典")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
            .task {
                do { dictionaries = try await store.request("dictionary.get") }
                catch { if !Task.isCancelled { localStatus = error.localizedDescription } }
                if !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { lookup() }
            }
    }
    private func lookup() {
        let query = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty, !managementBusy else { return }
        lookupRevision += 1
        let revision = lookupRevision
        entries = []
        localBusy = true
        localStatus = L("正在查本地词典…")
        Task { @MainActor in
            do {
                let value: [DictionaryEntry] = try await store.request("dictionary.lookup", ["term": query])
                guard revision == lookupRevision else { return }
                entries = value
                localStatus = value.isEmpty ? L("没有找到本地释义。请先在词典管理中导入词典。") : ""
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
    @State private var term: String
    @State private var activeTab = 0
    @State private var entries: [DictionaryEntry] = []
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

            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    if activeTab == 0 {
                        localContent
                    } else {
                        wikipediaContent
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 2)
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
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        } else if !localStatus.isEmpty {
            Text(localStatus)
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
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
                Text(wikipedia.extract)
                    .font(NativeTheme.font(15, relativeTo: .body))
                    .foregroundStyle(NativeTheme.ink)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                Link(destination: wikipedia.url) {
                    Label(L("在维基百科中打开"), systemImage: "arrow.up.right.square")
                        .font(NativeTheme.font(14, relativeTo: .subheadline))
                }
                .accessibilityIdentifier("reader.dictionary.wikipedia.open")
                Text(L("来源：维基百科 · CC BY-SA 4.0"))
                    .font(NativeTheme.font(11, relativeTo: .caption2))
                    .foregroundStyle(NativeTheme.muted)
            }
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("reader.dictionary.wikipedia.result")
        } else if !wikipediaStatus.isEmpty {
            Text(wikipediaStatus)
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
        }
    }

    private func lookup() {
        let query = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return }
        lookupRevision += 1
        let revision = lookupRevision
        entries = []
        wikipedia = nil
        localBusy = true
        wikipediaBusy = true
        localStatus = L("正在查本地词典…")
        wikipediaStatus = L("正在查询维基百科…")
        Task { @MainActor in
            do {
                let value: [DictionaryEntry] = try await store.request("dictionary.lookup", ["term": query])
                guard revision == lookupRevision else { return }
                entries = value
                localStatus = value.isEmpty ? L("没有找到本地释义。请先在词典管理中导入词典。") : ""
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
        wikipedia = nil
        localBusy = false
        wikipediaBusy = false
        localStatus = ""
        wikipediaStatus = ""
    }
}

private struct ConversationTurn: Codable { let role: String; var content: String }

struct NativeAssistant: View {
    @ObservedObject var store: NativeStore
    @ObservedObject var session: NativeReaderSession
    let book: LibraryBook
    let selection: String
    @State private var question = ""
    @State private var history: [ConversationTurn] = []
    @State private var busy = false
    @State private var sessionId = UUID().uuidString
    @State private var requestTask: Task<Void, Never>?
    @State private var status = ""

    private var canSend: Bool {
        !busy && !question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        GeometryReader { geometry in
            VStack(spacing: 0) {
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 12) {
                            if history.isEmpty { emptyState }
                            ForEach(Array(history.enumerated()), id: \.offset) { index, turn in
                                bubble(index: index, turn: turn,
                                       maxWidth: min(600, max(220, geometry.size.width - 40)))
                            }
                            if !status.isEmpty { statusRow }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 20)
                    }
                    // Only follow a newly submitted question. Once the answer
                    // starts streaming the position stays put so the reader can
                    // read from the top and scroll down at their own pace.
                    .onChange(of: history.count) { _ in scrollToBottom(proxy) }
                }
                Divider()
                composer
            }
            .background(NativeTheme.page)
        }
        .modifier(NativeKeyboardDone())
        .onDisappear { requestTask?.cancel(); session.aiStreams.removeValue(forKey: sessionId) }
    }

    @ViewBuilder
    private func bubble(index: Int, turn: ConversationTurn, maxWidth: CGFloat) -> some View {
        let isUser = turn.role == "user"
        let streaming = busy && index == history.count - 1 && !isUser
        let content = streaming ? session.aiStreams[sessionId] ?? turn.content : turn.content
        let thinking = streaming && content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        HStack(alignment: .bottom, spacing: 0) {
            if isUser { Spacer(minLength: 16) }
            Group {
                if thinking { NativeTypingIndicator() }
                else if isUser { Text(content).font(NativeTheme.font(16)).foregroundStyle(NativeTheme.page) }
                else { NativeMarkdownView(content).font(NativeTheme.font(16)) }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, thinking ? 12 : 10)
            .background(isUser ? NativeTheme.ink : NativeTheme.bubble,
                        in: NativeChatBubbleShape(tail: isUser ? .bottomTrailing : .bottomLeading))
            .frame(maxWidth: maxWidth, alignment: isUser ? .trailing : .leading)
            .contextMenu {
                if !isUser && !thinking {
                    Button { UIPasteboard.general.string = content } label: {
                        Label(L("复制回答"), systemImage: "doc.on.doc")
                    }
                }
            }
            if !isUser { Spacer(minLength: 16) }
        }
        .frame(maxWidth: .infinity, alignment: isUser ? .trailing : .leading)
        .id(index)
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 10) {
            Image(systemName: "sparkles")
                .font(.system(size: 24, weight: .regular))
                .foregroundStyle(NativeTheme.muted)
            Text(L("阅读助手")).font(NativeTheme.font(20, relativeTo: .title3)).foregroundStyle(NativeTheme.ink)
            Text(L("可以概括当前页、解释所选文字，或结合全书关键词检索回答问题。"))
                .font(NativeTheme.font(14)).foregroundStyle(NativeTheme.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.top, 8)
        .padding(.bottom, 6)
    }

    private var statusRow: some View {
        HStack(spacing: 7) {
            if busy { ProgressView().scaleEffect(0.65).frame(width: 14, height: 14) }
            Text(status).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
        }
        .padding(.leading, 4)
    }

    private var composer: some View {
        VStack(spacing: 10) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    promptChip(L("概括本页"), symbol: "doc.text.magnifyingglass") {
                        ask(L("用几句话概括这一页的要点。"), pageOnly: true)
                    }
                    if !selection.isEmpty {
                        promptChip(L("解释原文"), symbol: "text.quote") {
                            ask(L("解释这段话：「{0}」", selection), pageOnly: true)
                        }
                    }
                }
                .padding(.horizontal, 16)
            }
            HStack(alignment: .bottom, spacing: 10) {
                TextField(L("输入问题"), text: $question)
                    .font(NativeTheme.font(16))
                    .submitLabel(.send)
                    .onSubmit(send)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 20))
                    .accessibilityIdentifier("reader.assistant.input")
                if busy {
                    Button { requestTask?.cancel() } label: {
                        Image(systemName: "stop.circle.fill")
                            .font(.system(size: 30, weight: .regular))
                            .foregroundStyle(NativeTheme.ink)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(L("停止"))
                } else {
                    Button(action: send) {
                        Image(systemName: "arrow.up.circle.fill")
                            .font(.system(size: 30, weight: .regular))
                            .foregroundStyle(canSend ? NativeTheme.ink : NativeTheme.faint)
                    }
                    .buttonStyle(.plain)
                    .disabled(!canSend)
                    .accessibilityLabel(L("发送"))
                    .accessibilityIdentifier("reader.assistant.send")
                }
            }
            .padding(.horizontal, 16)
        }
        .padding(.top, 10)
        .padding(.bottom, 12)
    }

    private func promptChip(_ title: String, symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Image(systemName: symbol).font(.system(size: 12, weight: .regular))
                Text(title).font(NativeTheme.font(13))
            }
            .foregroundStyle(NativeTheme.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(NativeTheme.sunk, in: Capsule())
        }
        .buttonStyle(.plain)
        .disabled(busy)
    }

    private func scrollToBottom(_ proxy: ScrollViewProxy) {
        guard let last = history.indices.last else { return }
        proxy.scrollTo(last, anchor: .bottom)
    }

    private func send() {
        let value = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return }
        question = ""
        ask(value, pageOnly: false)
    }

    private func ask(_ question: String, pageOnly: Bool) {
        guard !busy else { return }
        busy = true
        status = pageOnly ? L("本次使用当前页。") : L("正在进行全书关键词检索…")
        let previous = history
        history.append(ConversationTurn(role: "user", content: question))
        // Empty streaming content is a state, not a translated-text sentinel.
        history.append(ConversationTurn(role: "assistant", content: ""))
        session.aiStreams[sessionId] = ""
        requestTask = Task {
            defer { busy = false }
            do {
                let answer: TextResult = try await store.request("reader.ai", ["bookId": book.id, "question": question, "history": try requestObject(previous), "pageOnly": pageOnly, "sessionId": sessionId])
                history[history.count - 1].content = answer.text
                status = ""
            } catch {
                status = ""
                if !Task.isCancelled { history[history.count - 1].content = error.localizedDescription }
                else {
                    let partial = session.aiStreams[sessionId] ?? ""
                    history[history.count - 1].content = partial.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                        ? L("已停止") : partial
                }
            }
        }
    }
}

/// Three pulsing dots keep the first assistant bubble alive while the request
/// is still resolving and no markdown has streamed in yet.
private struct NativeTypingIndicator: View {
    @Environment(\.locale) private var uiLocale
    var body: some View {
        let _ = uiLocale

        TimelineView(.periodic(from: .now, by: 0.35)) { context in
            let step = Int(context.date.timeIntervalSinceReferenceDate / 0.35) % 3
            HStack(spacing: 5) {
                ForEach(0..<3, id: \.self) { index in
                    Circle().fill(NativeTheme.muted).frame(width: 6, height: 6)
                        .opacity(index == step ? 1 : 0.35)
                }
            }
            .frame(height: 20)
        }
        .accessibilityLabel(L("正在思考"))
    }
}

/// Rounded chat bubble with one tighter corner on the sender's side, the same
/// silhouette iOS uses for messages without drawing an attached tail.
private struct NativeChatBubbleShape: Shape {
    enum Tail { case none, bottomLeading, bottomTrailing }
    let tail: Tail

    func path(in rect: CGRect) -> Path {
        let radius = min(18, min(rect.width, rect.height) / 2)
        let tailRadius = min(6, radius)
        let topLeft = radius
        let topRight = radius
        let bottomLeft = tail == .bottomLeading ? tailRadius : radius
        let bottomRight = tail == .bottomTrailing ? tailRadius : radius
        var path = Path()
        path.move(to: CGPoint(x: rect.minX + topLeft, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX - topRight, y: rect.minY))
        path.addArc(center: CGPoint(x: rect.maxX - topRight, y: rect.minY + topRight),
                    radius: topRight, startAngle: .degrees(-90), endAngle: .degrees(0), clockwise: false)
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - bottomRight))
        path.addArc(center: CGPoint(x: rect.maxX - bottomRight, y: rect.maxY - bottomRight),
                    radius: bottomRight, startAngle: .degrees(0), endAngle: .degrees(90), clockwise: false)
        path.addLine(to: CGPoint(x: rect.minX + bottomLeft, y: rect.maxY))
        path.addArc(center: CGPoint(x: rect.minX + bottomLeft, y: rect.maxY - bottomLeft),
                    radius: bottomLeft, startAngle: .degrees(90), endAngle: .degrees(180), clockwise: false)
        path.addLine(to: CGPoint(x: rect.minX, y: rect.minY + topLeft))
        path.addArc(center: CGPoint(x: rect.minX + topLeft, y: rect.minY + topLeft),
                    radius: topLeft, startAngle: .degrees(180), endAngle: .degrees(270), clockwise: false)
        path.closeSubpath()
        return path
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
                        store.perform { defer { busy = false }; try await store.command("txt.complete", ["id": review.id, "rules": try requestObject(rules)]); finished = true; store.txtReview = nil; dismiss() }
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
