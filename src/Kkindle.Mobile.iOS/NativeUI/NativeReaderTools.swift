import SwiftUI
import UIKit

struct NativeDictionary: View {
    @ObservedObject var store: NativeStore
    @State private var term: String
    @State private var entries: [DictionaryEntry] = []
    @State private var dictionaries: [DictionaryRecord] = []
    @State private var busy = false
    @State private var status = ""
    init(store: NativeStore, initialTerm: String) { self.store = store; _term = State(initialValue: initialTerm) }
    var body: some View {
        NativeForm {
            Section {
                HStack { TextField("查询词语", text: $term).onSubmit { lookup() }; Button("查询") { lookup() }.buttonStyle(.borderless) }
                if busy { ProgressView() }
                if !status.isEmpty { Text(status).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary) }
            }
            ForEach(Array(entries.enumerated()), id: \.offset) { _, entry in
                Section(entry.term + " · " + entry.dictionaryName) { Text(entry.definition).textSelection(.enabled) }
            }
            Section("词典管理") {
                ForEach(dictionaries) { dictionary in
                    VStack(alignment: .leading, spacing: 8) {
                        Toggle(dictionary.name + " · \(dictionary.entryCount) 条", isOn: Binding(get: { dictionary.enabled }, set: { enabled in action("toggle", ["id": dictionary.id, "enabled": enabled]) })).tint(NativeTheme.systemSwitchTint)
                        Button("移除", role: .destructive) { action("remove", ["id": dictionary.id]) }.buttonStyle(.borderless)
                    }
                }
                if dictionaries.isEmpty { Text("尚未导入词典").foregroundStyle(NativeTheme.secondary) }
                Button("导入词典") { action("import") }
            }
        }.disabled(busy).navigationTitle("本地词典").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
            .task { do { dictionaries = try await store.request("dictionary.get"); if !term.isEmpty { entries = try await store.request("dictionary.lookup", ["term": term]) } } catch { if !Task.isCancelled { status = error.localizedDescription } } }
    }
    private func lookup() {
        guard !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }; busy = true
        store.perform { defer { busy = false }; entries = try await store.request("dictionary.lookup", ["term": term]); status = entries.isEmpty ? "没有找到释义，请检查词典是否已导入并启用。" : "找到 \(entries.count) 条释义" }
    }
    private func action(_ command: String, _ args: [String: Any] = [:]) {
        busy = true
        store.perform { defer { busy = false }; try await store.command("dictionary.\(command)", args); dictionaries = try await store.request("dictionary.get"); if !term.isEmpty { entries = try await store.request("dictionary.lookup", ["term": term]) } }
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
        let thinking = streaming && (content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                     || content == "正在思考…")
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
                        Label("复制回答", systemImage: "doc.on.doc")
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
            Text("阅读助手").font(NativeTheme.font(20, relativeTo: .title3)).foregroundStyle(NativeTheme.ink)
            Text("可以概括当前页、解释所选文字，或结合全书关键词检索回答问题。")
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
                    promptChip("概括本页", symbol: "doc.text.magnifyingglass") {
                        ask("用几句话概括这一页的要点。", pageOnly: true)
                    }
                    if !selection.isEmpty {
                        promptChip("解释原文", symbol: "text.quote") {
                            ask("解释这段话：「\(selection)」", pageOnly: true)
                        }
                    }
                }
                .padding(.horizontal, 16)
            }
            HStack(alignment: .bottom, spacing: 10) {
                TextField("输入问题", text: $question)
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
                    .accessibilityLabel("停止")
                } else {
                    Button(action: send) {
                        Image(systemName: "arrow.up.circle.fill")
                            .font(.system(size: 30, weight: .regular))
                            .foregroundStyle(canSend ? NativeTheme.ink : NativeTheme.faint)
                    }
                    .buttonStyle(.plain)
                    .disabled(!canSend)
                    .accessibilityLabel("发送")
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
        status = pageOnly ? "本次使用当前页。" : "正在进行全书关键词检索…"
        let previous = history
        history.append(ConversationTurn(role: "user", content: question))
        history.append(ConversationTurn(role: "assistant", content: "正在思考…"))
        session.aiStreams[sessionId] = "正在思考…"
        requestTask = Task {
            defer { busy = false }
            do {
                let answer: TextResult = try await store.request("reader.ai", ["bookId": book.id, "question": question, "history": try requestObject(previous), "pageOnly": pageOnly, "sessionId": sessionId])
                history[history.count - 1].content = answer.text
                status = ""
            } catch {
                status = ""
                if !Task.isCancelled { history[history.count - 1].content = error.localizedDescription }
                else { history[history.count - 1].content = session.aiStreams[sessionId] ?? "已停止" }
            }
        }
    }
}

/// Three pulsing dots keep the first assistant bubble alive while the request
/// is still resolving and no markdown has streamed in yet.
private struct NativeTypingIndicator: View {
    var body: some View {
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
        .accessibilityLabel("正在思考")
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
                    Picker("编码", selection: $rules.encoding) { Text("自动识别").tag(0); Text("UTF-8").tag(1); Text("UTF-16 LE").tag(2); Text("UTF-16 BE").tag(3); Text("GB18030").tag(4) }
                    Picker("章节规则", selection: $rules.headingPreset) { Text("自动识别").tag(0); Text("中文章节").tag(1); Text("英文章节").tag(2); Text("序言和尾声").tag(3); Text("不识别章节").tag(4); Text("自定义正则").tag(5) }
                    if rules.headingPreset == 5 { TextField("章节正则", text: $rules.customHeadingPattern).textInputAutocapitalization(.never).disableAutocorrection(true) }
                    Toggle("识别分卷", isOn: $rules.recognizeVolumes).tint(NativeTheme.systemSwitchTint)
                }
                Section("目录预览") {
                    if let preview {
                        Text("\(preview.chapterCount) 个目录项 · \(preview.detectedEncoding)").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        ForEach(preview.chapters) { chapter in Text((chapter.isVolume ? "卷 · " : "") + chapter.title) }
                        HStack { Button("上一页") { page -= 1 }.disabled(!preview.hasPreviousPage); Spacer(); Button("下一页") { page += 1 }.disabled(!preview.hasNextPage) }.buttonStyle(.borderless)
                    } else if let error { Text(error).foregroundStyle(NativeTheme.secondary) } else { ProgressView("正在识别章节…") }
                }
            }.navigationTitle("TXT 导入").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).disabled(busy)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button("导入") {
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
