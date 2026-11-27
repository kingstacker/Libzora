import SwiftUI
import UIKit

struct NativeAiMessage: Identifiable, Equatable {
    let id: UUID
    let role: String
    var content: String
    var reasoning: String
    var status: String
    var sources: [AiSource]
    var requestKind: String
    var pageOnly: Bool
    var selection: String
    var failed: Bool
    var isStreaming: Bool

    init(
        id: UUID = UUID(),
        role: String,
        content: String = "",
        reasoning: String = "",
        status: String = "",
        sources: [AiSource] = [],
        requestKind: String = "question",
        pageOnly: Bool = false,
        selection: String = "",
        failed: Bool = false,
        isStreaming: Bool = false
    ) {
        self.id = id
        self.role = role
        self.content = content
        self.reasoning = reasoning
        self.status = status
        self.sources = sources
        self.requestKind = requestKind
        self.pageOnly = pageOnly
        self.selection = selection
        self.failed = failed
        self.isStreaming = isStreaming
    }
}

private struct ConversationTurnPayload: Codable {
    let role: String
    let content: String
}

@MainActor
final class NativeAiChatModel: ObservableObject {
    let bookId: String
    @Published var messages: [NativeAiMessage] = []
    @Published var draftQuestion = ""
    @Published var busy = false
    @Published var status = ""
    @Published var reasoningDepth = "auto"
    @Published var aiInfo: AiInfo?
    private(set) var activeStreamId = ""
    private var requestTask: Task<Void, Never>?
    private var requestNonce = 0

    init(bookId: String) {
        self.bookId = bookId
    }

    var reasoningOptions: [(id: String, label: String)] {
        if aiInfo?.provider.lowercased() == "deepseek" {
            return [("auto", L("自动")), ("high", L("深入")), ("max", L("极致"))]
        }
        return [("auto", L("自动")), ("low", L("快速")), ("medium", L("平衡")), ("high", L("深入"))]
    }

    var activeReasoningLabel: String {
        reasoningOptions.first(where: { $0.id == reasoningDepth })?.label ?? L("自动")
    }

    func loadInfo(store: NativeStore) async {
        if let info: AiInfo = try? await store.request("reader.aiInfo") {
            aiInfo = info
            if !reasoningOptions.contains(where: { $0.id == reasoningDepth }) {
                reasoningDepth = "auto"
            }
        }
    }

    func ask(
        question: String,
        requestKind: String,
        pageOnly: Bool,
        selection: String,
        store: NativeStore,
        replacingFromIndex: Int? = nil
    ) {
        let trimmed = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !busy, !trimmed.isEmpty else { return }

        if let cutIndex = replacingFromIndex, messages.indices.contains(cutIndex) {
            messages.removeSubrange(cutIndex..<messages.count)
        }

        let history = messages
            .filter { !$0.failed && !$0.isStreaming && !$0.content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map { ConversationTurnPayload(role: $0.role, content: $0.content) }

        let streamId = UUID().uuidString
        activeStreamId = streamId
        requestNonce += 1
        let nonce = requestNonce
        busy = true
        status = requestKind == "chapterSummary"
            ? L("正在整理当前章节内容…")
            : requestKind == "bookSummary"
                ? L("正在抽样全书内容…")
                : pageOnly ? L("本次使用当前页。") : L("正在进行全书关键词检索…")

        messages.append(NativeAiMessage(
            role: "user",
            content: trimmed,
            requestKind: requestKind,
            pageOnly: pageOnly,
            selection: selection))
        messages.append(NativeAiMessage(
            role: "assistant",
            requestKind: requestKind,
            pageOnly: pageOnly,
            selection: selection,
            isStreaming: true))

        let depth = reasoningDepth
        requestTask = Task { [weak self] in
            guard let self else { return }
            defer {
                if self.requestNonce == nonce {
                    self.busy = false
                    self.activeStreamId = ""
                }
            }
            do {
                let args: [String: Any] = [
                    "bookId": bookId,
                    "question": trimmed,
                    "history": try requestObject(history),
                    "pageOnly": pageOnly,
                    "requestKind": requestKind,
                    "reasoningDepth": depth,
                    "selection": selection,
                    "sessionId": streamId
                ]
                let result: AiResult = try await store.request("reader.ai", args)
                guard self.requestNonce == nonce, let index = self.messages.indices.last else { return }
                self.messages[index].content = result.text
                self.messages[index].reasoning = result.reasoning
                self.messages[index].status = result.status
                self.messages[index].sources = result.sources
                self.messages[index].failed = false
                self.messages[index].isStreaming = false
                self.status = ""
            } catch {
                guard self.requestNonce == nonce, let index = self.messages.indices.last else { return }
                self.status = ""
                self.messages[index].isStreaming = false
                if Task.isCancelled {
                    let partial = self.messages[index].content.trimmingCharacters(in: .whitespacesAndNewlines)
                    if partial.isEmpty {
                        self.messages[index].content = L("已停止")
                    }
                    self.messages[index].status = L("已停止生成")
                } else {
                    self.messages[index].failed = true
                    if self.messages[index].content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        self.messages[index].content = error.localizedDescription
                    } else {
                        self.messages[index].status = error.localizedDescription
                    }
                }
            }
        }
    }

    func applyStreamUpdate(text: String, reasoning: String, status: String, sources: [AiSource]) {
        guard let index = messages.indices.last, messages[index].isStreaming else { return }
        messages[index].content = text
        messages[index].reasoning = reasoning
        if !status.isEmpty {
            self.status = status
            messages[index].status = status
        }
        if !sources.isEmpty {
            messages[index].sources = sources
        }
    }

    func retry(assistantId: UUID, store: NativeStore) {
        guard !busy, let assistantIndex = messages.firstIndex(where: { $0.id == assistantId }), assistantIndex > 0 else {
            return
        }
        let userMessage = messages[assistantIndex - 1]
        guard userMessage.role == "user" else { return }
        ask(
            question: userMessage.content,
            requestKind: userMessage.requestKind,
            pageOnly: userMessage.pageOnly,
            selection: userMessage.selection,
            store: store,
            replacingFromIndex: assistantIndex - 1)
    }

    func stop() {
        requestTask?.cancel()
        requestTask = nil
    }

    func clear() {
        guard !busy else { return }
        messages.removeAll()
        status = ""
        activeStreamId = ""
    }

    func cancelAndDispose() {
        requestNonce += 1
        requestTask?.cancel()
        requestTask = nil
        busy = false
        activeStreamId = ""
        status = ""
    }
}

struct NativeAssistant: View {
    @ObservedObject var store: NativeStore
    @ObservedObject var chat: NativeAiChatModel
    let book: LibraryBook
    let initialSelection: String
    var isSidebar = false
    var backgroundColor: Color = NativeTheme.page
    var onJumpToSource: (AiSource) -> Void
    var onClose: () -> Void
    var onInputFocusChanged: (Bool) -> Void = { _ in }

    @State private var activeSelection: String
    @State private var expandedReasoningIds: Set<UUID> = []
    @State private var expandedSourceIds: Set<UUID> = []
    @State private var previewSource: AiSource?
    @State private var showingSettings = false
    @State private var sharePayload: NativeSharePayload?
    @State private var copiedMessageId: UUID?
    @State private var nearBottom = true
    @State private var containerWidth: CGFloat = 360
    @FocusState private var inputFocused: Bool

    init(
        store: NativeStore,
        chat: NativeAiChatModel,
        book: LibraryBook,
        initialSelection: String,
        isSidebar: Bool = false,
        backgroundColor: Color = NativeTheme.page,
        onJumpToSource: @escaping (AiSource) -> Void,
        onClose: @escaping () -> Void,
        onInputFocusChanged: @escaping (Bool) -> Void = { _ in }
    ) {
        self.store = store
        self.chat = chat
        self.book = book
        self.initialSelection = initialSelection
        self.isSidebar = isSidebar
        self.backgroundColor = backgroundColor
        self.onJumpToSource = onJumpToSource
        self.onClose = onClose
        self.onInputFocusChanged = onInputFocusChanged
        _activeSelection = State(initialValue: initialSelection)
    }

    private var canSend: Bool {
        !chat.busy && !chat.draftQuestion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        let bubbleMaxWidth = min(600, max(220, containerWidth - (isSidebar ? 28 : 40)))
        let pageBackground = isSidebar ? backgroundColor : NativeTheme.page
        VStack(spacing: 0) {
            if isSidebar {
                sidebarHeader
                Divider()
            }
            if let info = chat.aiInfo, !info.configured {
                unconfiguredBanner
                Divider()
            }
            ScrollViewReader { proxy in
                ZStack(alignment: .bottom) {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 14) {
                            if chat.messages.isEmpty { emptyState }
                            ForEach(chat.messages) { message in
                                bubble(message: message, maxWidth: bubbleMaxWidth)
                            }
                            if !chat.status.isEmpty { statusRow }
                            Color.clear
                                .frame(height: 1)
                                .id("ai-bottom-anchor")
                                .onAppear { nearBottom = true }
                                .onDisappear { nearBottom = false }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 16)
                    }
                    .modifier(NativeInteractiveKeyboardDismiss())
                    .onChange(of: chat.messages.count) { _ in
                        scrollToBottom(proxy)
                    }
                    .onChange(of: inputFocused) { focused in
                        if focused && nearBottom && !chat.messages.isEmpty {
                            scrollToBottom(proxy)
                        }
                    }
                    if !nearBottom && !chat.messages.isEmpty {
                        Button {
                            withAnimation(.easeOut(duration: 0.2)) {
                                scrollToBottom(proxy)
                            }
                        } label: {
                            HStack(spacing: 5) {
                                Image(systemName: "arrow.down")
                                    .font(.system(size: 11, weight: .semibold))
                                Text(chat.busy ? L("新内容") : L("回到底部"))
                                    .font(NativeTheme.font(12, relativeTo: .caption))
                            }
                            .foregroundStyle(NativeTheme.ink)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 7)
                            .background(NativeTheme.surface, in: Capsule())
                            .overlay(Capsule().stroke(NativeTheme.divider, lineWidth: 0.8))
                            .shadow(color: Color.black.opacity(0.08), radius: 6, y: 2)
                        }
                        .buttonStyle(.plain)
                        .padding(.bottom, 10)
                        .accessibilityIdentifier("reader.assistant.scrollBottom")
                    }
                }
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(spacing: 0) {
                Divider()
                composer
            }
            .background(pageBackground)
        }
        .background(pageBackground)
        .background(
            GeometryReader { proxy in
                Color.clear
                    .onAppear { containerWidth = proxy.size.width }
                    .onChange(of: proxy.size.width) { containerWidth = $0 }
            }
        )
        .modifier(NativeAssistantToolbarModifier(enabled: !isSidebar, content: {
            ToolbarItemGroup(placement: .navigationBarTrailing) {
                reasoningMenuButton
                if !chat.messages.isEmpty {
                    Button { chat.clear() } label: {
                        Image(systemName: "trash")
                    }
                    .disabled(chat.busy)
                    .accessibilityLabel(L("清空对话"))
                    .accessibilityIdentifier("reader.assistant.clear")
                }
            }
        }))
        .task { await chat.loadInfo(store: store) }
        .onChange(of: initialSelection) { updated in
            if !updated.isEmpty { activeSelection = updated }
        }
        .onChange(of: inputFocused) { onInputFocusChanged($0) }
        .onDisappear { onInputFocusChanged(false) }
        .sheet(item: $previewSource) { source in
            NativeAiSourceSheet(source: source) {
                previewSource = nil
                onJumpToSource(source)
            }
            .preferredColorScheme(nil)
        }
        .sheet(isPresented: $showingSettings, onDismiss: {
            Task { await chat.loadInfo(store: store) }
        }) {
            NavigationView {
                NativeAiSettings(store: store)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button(L("完成")) { showingSettings = false }
                        }
                    }
            }
            .navigationViewStyle(.stack)
        }
        .sheet(item: $sharePayload) { payload in
            NativeActivityShareSheet(items: [payload.text])
        }
    }

    private var sidebarHeader: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(L("AI 助手"))
                    .font(NativeTheme.font(16, relativeTo: .headline))
                    .fontWeight(.semibold)
                    .foregroundStyle(NativeTheme.ink)
                if let model = chat.aiInfo?.model, !model.isEmpty {
                    Text(model)
                        .font(NativeTheme.font(11, relativeTo: .caption2))
                        .foregroundStyle(NativeTheme.muted)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            reasoningMenuButton
            if !chat.messages.isEmpty {
                Button { chat.clear() } label: {
                    Image(systemName: "trash")
                        .font(.system(size: 14, weight: .regular))
                        .foregroundStyle(chat.busy ? NativeTheme.faint : NativeTheme.secondary)
                        .frame(width: 36, height: 36)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(chat.busy)
                .accessibilityLabel(L("清空对话"))
                .accessibilityIdentifier("reader.assistant.clear")
            }
            Button(action: onClose) {
                Image(systemName: "xmark")
                    .font(.system(size: 14, weight: .medium))
                    .foregroundStyle(NativeTheme.secondary)
                    .frame(width: 36, height: 36)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(L("关闭"))
            .accessibilityIdentifier("reader.assistant.close")
        }
        .padding(.leading, 14)
        .padding(.trailing, 8)
        .padding(.vertical, 6)
    }

    private var reasoningMenuButton: some View {
        Menu {
            ForEach(chat.reasoningOptions, id: \.id) { option in
                Button {
                    chat.reasoningDepth = option.id
                } label: {
                    if chat.reasoningDepth == option.id {
                        Label(option.label, systemImage: "checkmark")
                    } else {
                        Text(option.label)
                    }
                }
            }
            Divider()
            Button {
                showingSettings = true
            } label: {
                Label(L("AI 接口设置"), systemImage: "slider.horizontal.3")
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "brain")
                    .font(.system(size: 12, weight: .regular))
                Text(chat.activeReasoningLabel)
                    .font(NativeTheme.font(12, relativeTo: .caption))
            }
            .foregroundStyle(NativeTheme.secondary)
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .background(NativeTheme.sunk, in: Capsule())
        }
        .accessibilityLabel(L("思考深度"))
        .accessibilityIdentifier("reader.assistant.options")
    }

    private var unconfiguredBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "exclamationmark.circle")
                .font(.system(size: 15))
                .foregroundStyle(NativeTheme.warning)
            Text(L("尚未配置 AI 接口，请先填写服务地址与 API Key。"))
                .font(NativeTheme.font(13, relativeTo: .footnote))
                .foregroundStyle(NativeTheme.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            Button {
                showingSettings = true
            } label: {
                Text(L("去设置"))
                    .font(NativeTheme.font(13, relativeTo: .footnote))
                    .fontWeight(.semibold)
                    .foregroundStyle(Color(uiColor: .systemBlue))
                    .padding(.horizontal, 4)
                    .padding(.vertical, 4)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("reader.assistant.openSettings")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(NativeTheme.sunk)
    }

    @ViewBuilder
    private func bubble(message: NativeAiMessage, maxWidth: CGFloat) -> some View {
        let isUser = message.role == "user"
        let hasContent = !message.content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let hasReasoning = !message.reasoning.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let thinking = message.isStreaming && !hasContent && !hasReasoning
        let reasoningExpanded = expandedReasoningIds.contains(message.id)
        let sourcesExpanded = expandedSourceIds.contains(message.id)

        HStack(alignment: .bottom, spacing: 0) {
            if isUser { Spacer(minLength: 16) }
            VStack(alignment: .leading, spacing: 8) {
                if isUser {
                    if !message.selection.isEmpty {
                        Text("「\(message.selection)」")
                            .font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.page.opacity(0.8))
                            .lineLimit(2)
                    }
                    Text(message.content)
                        .font(NativeTheme.font(16))
                        .foregroundStyle(NativeTheme.page)
                } else {
                    if thinking {
                        NativeTypingIndicator()
                    }
                    if hasReasoning || (message.isStreaming && !hasContent) {
                        if hasReasoning {
                            Button {
                                if reasoningExpanded { expandedReasoningIds.remove(message.id) }
                                else { expandedReasoningIds.insert(message.id) }
                            } label: {
                                HStack(spacing: 5) {
                                    Image(systemName: reasoningExpanded ? "chevron.down" : "chevron.right")
                                        .font(.system(size: 10, weight: .semibold))
                                    Text(reasoningExpanded ? L("收起思考过程") : L("展开思考过程"))
                                        .font(NativeTheme.font(12, relativeTo: .caption))
                                    if message.isStreaming && !hasContent {
                                        ProgressView().scaleEffect(0.6).frame(width: 12, height: 12)
                                    }
                                }
                                .foregroundStyle(NativeTheme.secondary)
                            }
                            .buttonStyle(.plain)

                            if reasoningExpanded {
                                NativeMarkdownView(
                                    message.reasoning,
                                    fontSize: 13.5,
                                    muted: true,
                                    sources: message.sources,
                                    onCitationTap: { previewSource = $0 })
                                .padding(.leading, 8)
                                .overlay(alignment: .leading) {
                                    Rectangle()
                                        .fill(NativeTheme.strong)
                                        .frame(width: 2)
                                }
                            }
                        } else if message.isStreaming && !hasContent {
                            HStack(spacing: 6) {
                                Text(L("思考中…"))
                                    .font(NativeTheme.font(12, relativeTo: .caption))
                                    .foregroundStyle(NativeTheme.muted)
                            }
                        }
                    }
                    if hasContent {
                        NativeMarkdownView(
                            message.content,
                            fontSize: 16,
                            sources: message.sources,
                            onCitationTap: { previewSource = $0 })
                    }
                    if !message.sources.isEmpty && !message.isStreaming {
                        Button {
                            if sourcesExpanded { expandedSourceIds.remove(message.id) }
                            else { expandedSourceIds.insert(message.id) }
                        } label: {
                            HStack(spacing: 5) {
                                Image(systemName: sourcesExpanded ? "chevron.down" : "chevron.right")
                                    .font(.system(size: 10, weight: .semibold))
                                Text(L("引用来源 ({0})", message.sources.count))
                                    .font(NativeTheme.font(12, relativeTo: .caption))
                            }
                            .foregroundStyle(NativeTheme.secondary)
                        }
                        .buttonStyle(.plain)

                        if sourcesExpanded {
                            VStack(alignment: .leading, spacing: 6) {
                                ForEach(message.sources) { source in
                                    Button {
                                        previewSource = source
                                    } label: {
                                        HStack(spacing: 6) {
                                            Text("[\(source.id)]")
                                                .font(.system(size: 11, weight: .semibold, design: .monospaced))
                                                .foregroundStyle(Color(uiColor: .systemBlue))
                                            Text(source.label)
                                                .font(NativeTheme.font(12, relativeTo: .caption))
                                                .foregroundStyle(NativeTheme.secondary)
                                                .lineLimit(1)
                                            Spacer(minLength: 4)
                                            Image(systemName: "arrow.up.right")
                                                .font(.system(size: 10))
                                                .foregroundStyle(NativeTheme.muted)
                                        }
                                        .padding(.horizontal, 10)
                                        .padding(.vertical, 6)
                                        .background(NativeTheme.surface, in: RoundedRectangle(cornerRadius: 8))
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                    }
                    if !message.status.isEmpty && !message.isStreaming {
                        Text(message.status)
                            .font(NativeTheme.font(11.5, relativeTo: .caption2))
                            .foregroundStyle(message.failed ? NativeTheme.warning : NativeTheme.muted)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    if !message.isStreaming && (hasContent || message.failed) {
                        assistantActionRow(message: message, hasContent: hasContent)
                    }
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, thinking ? 12 : 10)
            .background(isUser ? NativeTheme.ink : NativeTheme.bubble,
                        in: NativeChatBubbleShape(tail: isUser ? .bottomTrailing : .bottomLeading))
            .frame(maxWidth: maxWidth, alignment: isUser ? .trailing : .leading)
            .contextMenu {
                if !isUser && hasContent {
                    Button { copyAnswer(message) } label: {
                        Label(L("复制回答"), systemImage: "doc.on.doc")
                    }
                    Button { shareAnswer(message) } label: {
                        Label(L("导出 Markdown"), systemImage: "square.and.arrow.up")
                    }
                }
                if !isUser && !chat.busy {
                    Button { chat.retry(assistantId: message.id, store: store) } label: {
                        Label(L("重试"), systemImage: "arrow.clockwise")
                    }
                }
            }
            if !isUser { Spacer(minLength: 16) }
        }
        .frame(maxWidth: .infinity, alignment: isUser ? .trailing : .leading)
        .id(message.id)
    }

    private func assistantActionRow(message: NativeAiMessage, hasContent: Bool) -> some View {
        HStack(spacing: 14) {
            if hasContent {
                Button { copyAnswer(message) } label: {
                    HStack(spacing: 4) {
                        Image(systemName: copiedMessageId == message.id ? "checkmark" : "doc.on.doc")
                            .font(.system(size: 11))
                        Text(copiedMessageId == message.id ? L("已复制") : L("复制"))
                            .font(NativeTheme.font(12, relativeTo: .caption))
                    }
                    .foregroundStyle(NativeTheme.secondary)
                }
                .buttonStyle(.plain)

                Button { shareAnswer(message) } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "square.and.arrow.up")
                            .font(.system(size: 11))
                        Text(L("导出"))
                            .font(NativeTheme.font(12, relativeTo: .caption))
                    }
                    .foregroundStyle(NativeTheme.secondary)
                }
                .buttonStyle(.plain)
            }
            if !chat.busy {
                Button { chat.retry(assistantId: message.id, store: store) } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "arrow.clockwise")
                            .font(.system(size: 11))
                        Text(L("重试"))
                            .font(NativeTheme.font(12, relativeTo: .caption))
                    }
                    .foregroundStyle(NativeTheme.secondary)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.top, 2)
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 10) {
            Image(systemName: "sparkles")
                .font(.system(size: 24, weight: .regular))
                .foregroundStyle(NativeTheme.muted)
            Text(L("阅读助手")).font(NativeTheme.font(20, relativeTo: .title3)).foregroundStyle(NativeTheme.ink)
            Text(L("可以概括当前页、总结本章、概览全书，或结合原文检索回答问题。"))
                .font(NativeTheme.font(14)).foregroundStyle(NativeTheme.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.top, 8)
        .padding(.bottom, 6)
    }

    private var statusRow: some View {
        HStack(spacing: 7) {
            if chat.busy { ProgressView().scaleEffect(0.65).frame(width: 14, height: 14) }
            Text(chat.status).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
        }
        .padding(.leading, 4)
    }

    private var composer: some View {
        VStack(spacing: 10) {
            if !activeSelection.isEmpty {
                HStack(spacing: 8) {
                    Image(systemName: "text.quote")
                        .font(.system(size: 12))
                        .foregroundStyle(NativeTheme.secondary)
                    Text(activeSelection)
                        .font(NativeTheme.font(12.5, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.secondary)
                        .lineLimit(2)
                    Spacer(minLength: 4)
                    Button { activeSelection = "" } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.system(size: 14))
                            .foregroundStyle(NativeTheme.muted)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(L("清除引用"))
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
                .padding(.horizontal, 16)
            }

            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    if !activeSelection.isEmpty {
                        promptChip(L("解释原文"), symbol: "text.quote") {
                            let quote = activeSelection
                            activeSelection = ""
                            chat.ask(
                                question: L("解释这段话：「{0}」", quote),
                                requestKind: "question",
                                pageOnly: true,
                                selection: quote,
                                store: store)
                        }
                    }
                    promptChip(L("概括本页"), symbol: "doc.text.magnifyingglass") {
                        chat.ask(
                            question: L("用几句话概括这一页的要点。"),
                            requestKind: "question",
                            pageOnly: true,
                            selection: "",
                            store: store)
                    }
                    promptChip(L("总结本章"), symbol: "book.pages") {
                        chat.ask(
                            question: L("请总结当前章节的核心观点、关键人物或概念，以及值得回看的段落。"),
                            requestKind: "chapterSummary",
                            pageOnly: false,
                            selection: "",
                            store: store)
                    }
                    promptChip(L("全书概览"), symbol: "books.vertical") {
                        chat.ask(
                            question: L("请根据抽样原文快速概览这本书的主题、结构与主要论点，并说明覆盖范围。"),
                            requestKind: "bookSummary",
                            pageOnly: false,
                            selection: "",
                            store: store)
                    }
                }
                .padding(.horizontal, 16)
            }
            HStack(alignment: .bottom, spacing: 10) {
                questionInputField
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 20))
                    .accessibilityIdentifier("reader.assistant.input")
                if chat.busy {
                    Button { chat.stop() } label: {
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

    @ViewBuilder
    private var questionInputField: some View {
        Group {
            if #available(iOS 16.0, *) {
                TextField(L("输入问题"), text: $chat.draftQuestion, axis: .vertical)
                    .font(NativeTheme.font(16))
                    .lineLimit(1...5)
                    .submitLabel(.send)
                    .onSubmit(send)
            } else {
                TextField(L("输入问题"), text: $chat.draftQuestion)
                    .font(NativeTheme.font(16))
                    .submitLabel(.send)
                    .onSubmit(send)
            }
        }
        .focused($inputFocused)
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
        .disabled(chat.busy)
    }

    private func scrollToBottom(_ proxy: ScrollViewProxy) {
        proxy.scrollTo("ai-bottom-anchor", anchor: .bottom)
    }

    private func send() {
        let value = chat.draftQuestion.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, !chat.busy else { return }
        let quote = activeSelection
        chat.draftQuestion = ""
        activeSelection = ""
        chat.ask(
            question: value,
            requestKind: "question",
            pageOnly: false,
            selection: quote,
            store: store)
    }

    private func copyAnswer(_ message: NativeAiMessage) {
        UIPasteboard.general.string = message.content
        copiedMessageId = message.id
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            if copiedMessageId == message.id { copiedMessageId = nil }
        }
    }

    private func shareAnswer(_ message: NativeAiMessage) {
        var markdown = message.content
        if !message.sources.isEmpty {
            let refs = message.sources.map { source in
                "- [\(source.id)] \(source.label)\n\n  \(source.content.replacingOccurrences(of: "\n", with: " "))"
            }.joined(separator: "\n\n")
            markdown += "\n\n---\n\n" + refs
        }
        sharePayload = NativeSharePayload(text: markdown)
    }
}

private struct NativeInteractiveKeyboardDismiss: ViewModifier {
    func body(content: Content) -> some View {
        if #available(iOS 16.0, *) {
            content.scrollDismissesKeyboard(.interactively)
        } else {
            content
        }
    }
}

private struct NativeAssistantToolbarModifier<Items: ToolbarContent>: ViewModifier {
    let enabled: Bool
    @ToolbarContentBuilder let content: () -> Items

    func body(content base: Content) -> some View {
        if enabled {
            base.toolbar(content: content)
        } else {
            base
        }
    }
}

private struct NativeSharePayload: Identifiable {
    let id = UUID()
    let text: String
}

private struct NativeAiSourceSheet: View {
    @Environment(\.dismiss) private var dismiss
    let source: AiSource
    let onJump: () -> Void

    var body: some View {
        NavigationView {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text("[\(source.id)] · \(source.label)")
                        .font(NativeTheme.font(13, relativeTo: .footnote))
                        .fontWeight(.semibold)
                        .foregroundStyle(NativeTheme.secondary)
                    Text(source.content)
                        .font(NativeTheme.font(16))
                        .lineSpacing(5)
                        .foregroundStyle(NativeTheme.ink)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(20)
            }
            .background(NativeTheme.page)
            .navigationTitle(L("原文来源"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("关闭")) { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(L("跳转到原文")) {
                        dismiss()
                        onJump()
                    }
                }
            }
        }
        .navigationViewStyle(.stack)
    }
}

private struct NativeActivityShareSheet: UIViewControllerRepresentable {
    let items: [Any]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: items, applicationActivities: nil)
    }

    func updateUIViewController(_ uiViewController: UIActivityViewController, context: Context) {}
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
