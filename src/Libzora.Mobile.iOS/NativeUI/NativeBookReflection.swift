import PhotosUI
import SwiftUI
import UIKit

struct NativeBookReflectionEditor: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    let bookTitle: String
    var onSaved: ((BookReflectionRecord?) -> Void)?

    @State private var content = ""
    @State private var selection = NSRange(location: 0, length: 0)
    @State private var loading = true
    @State private var busy = false
    @State private var editState = NativeReflectionEditState()
    @State private var hasExisting = false
    @State private var showingCitations = false
    @State private var showingLinkPrompt = false
    @State private var showingPhotoPicker = false
    @State private var linkInput = "https://"
    @State private var linkError: String?
    @State private var error: String?

    private var characterCount: Int {
        NativeBookReflectionPolicy.countCharacters(in: content)
    }

    var body: some View {
        NavigationView {
            VStack(spacing: 0) {
                if loading {
                    ProgressView(L("正在读取读后思考…"))
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if !editState.loaded {
                    VStack(spacing: 16) {
                        Text(error ?? L("操作失败")).foregroundStyle(NativeTheme.secondary)
                        Button(L("重试")) { Task { await load() } }
                            .buttonStyle(NativeLiquidGlassButtonStyle())
                            .accessibilityIdentifier("reflection.retry")
                    }.padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    VStack(alignment: .leading, spacing: 10) {
                        HStack(alignment: .firstTextBaseline, spacing: 10) {
                            Text("《\(bookTitle)》")
                                .font(NativeTheme.font(18, relativeTo: .headline).weight(.semibold))
                                .lineLimit(2)
                            Spacer(minLength: 8)
                            Text(L("{0} 字", characterCount))
                                .font(NativeTheme.font(12, relativeTo: .caption))
                                .foregroundStyle(NativeTheme.muted)
                                .monospacedDigit()
                        }

                        formattingToolbar.disabled(busy)
                    }
                    .padding(.horizontal, 20)
                    .padding(.top, 12)
                    .padding(.bottom, 10)

                    Divider().overlay(NativeTheme.divider)

                    ZStack(alignment: .topLeading) {
                        if content.isEmpty {
                            Text(L("写下你读完这本书后的理解、感受、疑问或行动计划。"))
                                .font(NativeTheme.font(15))
                                .foregroundStyle(NativeTheme.faint)
                                .padding(.horizontal, 20)
                                .padding(.vertical, 16)
                                .allowsHitTesting(false)
                        }
                        NativeReflectionTextView(
                            text: $content,
                            selection: $selection,
                            useSystemFont: store.preferences.uiFont == 1,
                            editable: editState.canEdit && !busy
                        )
                        .disabled(busy)
                        .accessibilityIdentifier("reflection.editor")
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)

                    if let error {
                        Text(error)
                            .font(NativeTheme.font(13, relativeTo: .footnote))
                            .foregroundStyle(NativeTheme.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 20)
                            .padding(.vertical, 8)
                    }
                }
            }
            .background(NativeAmbientBackground())
            .navigationTitle(L("读后思考"))
            .navigationBarTitleDisplayMode(.inline)
            .navigationBarHidden(false)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("取消")) { dismiss() }
                        .disabled(busy)
                        .accessibilityIdentifier("reflection.cancel")
                }
                ToolbarItemGroup(placement: .confirmationAction) {
                    if hasExisting && !loading {
                        Menu {
                            Button(L("删除读后思考"), role: .destructive) { deleteReflection() }
                        } label: {
                            Image(systemName: "ellipsis.circle")
                        }
                        .disabled(busy)
                        .accessibilityLabel(L("更多操作"))
                    }
                    Button(L("保存")) { saveReflection() }
                        .disabled(busy || loading || !editState.canEdit)
                        .accessibilityIdentifier("reflection.save")
                }
            }
        }
        .navigationViewStyle(.stack)
        .interactiveDismissDisabled(busy)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .task(id: bookId) { await load() }
        .sheet(isPresented: $showingCitations) {
            NativeReflectionCitationPicker(store: store, currentBookTitle: bookTitle) { citation in
                let block = NativeBookReflectionPolicy.formatCitationBlock(
                    text: citation.displayText,
                    bookTitle: citation.bookTitle
                )
                if !block.isEmpty {
                    let result = NativeBookReflectionPolicy.insertBlock(block, into: content, selection: selection)
                    content = result.text
                    selection = result.selection
                }
            }
        }
        .sheet(isPresented: $showingLinkPrompt) { linkSheet }
        .sheet(isPresented: $showingPhotoPicker) {
            NativeReflectionPhotoPicker { image in
                if let image { insertImage(image) }
            }
        }
    }

    private var formattingToolbar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                toolbarButton("H1", label: L("一级标题")) { applyLinePrefix("# ", isHeading: true) }
                toolbarButton("H2", label: L("二级标题")) { applyLinePrefix("## ", isHeading: true) }
                toolbarButton("H3", label: L("三级标题")) { applyLinePrefix("### ", isHeading: true) }
                toolbarButton("B", label: L("粗体"), bold: true) { applyBold() }
                toolbarIconButton("text.quote", label: L("引用")) { applyLinePrefix("> ", isHeading: false) }
                toolbarButton(L("引用批注"), label: L("引用批注")) { showingCitations = true }
                toolbarIconButton("list.bullet", label: L("无序列表")) { applyLinePrefix("- ", isHeading: false) }
                toolbarIconButton("list.number", label: L("有序列表")) { applyLinePrefix("1. ", isHeading: false) }
                toolbarIconButton("link", label: L("链接")) {
                    linkInput = "https://"
                    linkError = nil
                    showingLinkPrompt = true
                }
                Menu {
                    Button { showingPhotoPicker = true } label: {
                        Label(L("从照片选择"), systemImage: "photo.on.rectangle")
                    }
                    Button { pickImageFromFiles() } label: {
                        Label(L("从文件选择"), systemImage: "folder")
                    }
                } label: {
                    Image(systemName: "photo")
                        .font(.system(size: 14, weight: .regular))
                        .frame(minWidth: 36, minHeight: 32)
                        .padding(.horizontal, 6)
                        .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 8))
                }
                .accessibilityLabel(L("插入图片"))
            }
        }
    }

    private func toolbarButton(_ title: String, label: String, bold: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(NativeTheme.font(13, relativeTo: .footnote).weight(bold ? .bold : .medium))
                .frame(minWidth: 36, minHeight: 32)
                .padding(.horizontal, 8)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    private func toolbarIconButton(_ systemName: String, label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 14, weight: .regular))
                .frame(minWidth: 36, minHeight: 32)
                .padding(.horizontal, 6)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    private var linkSheet: some View {
        NavigationView {
            VStack(alignment: .leading, spacing: 12) {
                TextField("https://", text: $linkInput)
                    .font(NativeTheme.font())
                    .keyboardType(.URL)
                    .textInputAutocapitalization(.never)
                    .disableAutocorrection(true)
                    .padding(14)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                if let linkError {
                    Text(linkError)
                        .font(NativeTheme.font(13))
                        .foregroundStyle(NativeTheme.secondary)
                }
                Spacer()
            }
            .padding(24)
            .background(NativeAmbientBackground())
            .navigationTitle(L("链接"))
            .navigationBarTitleDisplayMode(.inline)
            .navigationBarHidden(false)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("取消")) { showingLinkPrompt = false }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(L("确定")) {
                        var candidate = linkInput.trimmingCharacters(in: .whitespacesAndNewlines)
                        guard !candidate.isEmpty else { return }
                        if !candidate.contains(":") { candidate = "https://" + candidate }
                        guard let url = URL(string: candidate),
                              let scheme = url.scheme?.lowercased(),
                              scheme == "http" || scheme == "https" || scheme == "mailto" else {
                            linkError = L("请输入有效链接。")
                            return
                        }
                        insertLink(url.absoluteString)
                        showingLinkPrompt = false
                    }
                }
            }
        }
        .navigationViewStyle(.stack)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
    }

    private func applyLinePrefix(_ prefix: String, isHeading: Bool) {
        let result = NativeBookReflectionPolicy.toggleLinePrefix(
            in: content,
            selection: selection,
            prefix: prefix,
            isHeading: isHeading
        )
        content = result.text
        selection = result.selection
    }

    private func applyBold() {
        let result = NativeBookReflectionPolicy.toggleBold(in: content, selection: selection)
        content = result.text
        selection = result.selection
    }

    private func insertLink(_ url: String) {
        let nsText = content as NSString
        let start = min(max(0, selection.location), nsText.length)
        let length = min(max(0, selection.length), nsText.length - start)
        let range = NSRange(location: start, length: length)
        let selected = length > 0 ? nsText.substring(with: range) : ""
        let label = selected.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? url : selected
        let markdown = "[\(label)](\(url))"
        content = nsText.replacingCharacters(in: range, with: markdown)
        selection = NSRange(location: start + (markdown as NSString).length, length: 0)
    }

    private func insertImage(_ image: UIImage) {
        let resized = resizeImageIfNeeded(image, maxDimension: 1400)
        guard let data = resized.jpegData(compressionQuality: 0.85) else { return }
        let dataUrl = "data:image/jpeg;base64,\(data.base64EncodedString())"
        let block = "![\(L("图片"))](\(dataUrl))"
        let result = NativeBookReflectionPolicy.insertBlock(block, into: content, selection: selection)
        content = result.text
        selection = result.selection
    }

    private func resizeImageIfNeeded(_ image: UIImage, maxDimension: CGFloat) -> UIImage {
        let size = image.size
        let longest = max(size.width, size.height)
        guard longest > maxDimension, longest > 0 else { return image }
        let scale = maxDimension / longest
        let target = CGSize(width: floor(size.width * scale), height: floor(size.height * scale))
        let renderer = UIGraphicsImageRenderer(size: target)
        return renderer.image { _ in
            image.draw(in: CGRect(origin: .zero, size: target))
        }
    }

    private func pickImageFromFiles() {
        busy = true
        Task {
            defer { busy = false }
            do {
                let result: PickedImageData = try await store.request("reflection.pickImage")
                if let dataUrl = result.dataUrl, !dataUrl.isEmpty {
                    let block = "![\(L("图片"))](\(dataUrl))"
                    let updated = NativeBookReflectionPolicy.insertBlock(block, into: content, selection: selection)
                    content = updated.text
                    selection = updated.selection
                }
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private func load() async {
        guard !busy else { return }
        loading = true
        editState.beginLoad()
        error = nil
        defer { loading = false }
        do {
            let record: BookReflectionRecord = try await store.request("reflection.get", ["bookId": bookId, "editor": true])
            try Task.checkCancellation()
            content = record.content
            hasExisting = record.hasContent
            selection = NSRange(location: (content as NSString).length, length: 0)
            editState.finishLoad(succeeded: true)
        } catch {
            editState.finishLoad(succeeded: false)
            if !Task.isCancelled { self.error = error.localizedDescription }
        }
    }

    private func saveReflection() {
        guard !busy, editState.beginWrite() else { return }
        busy = true
        let submittedContent = content
        error = nil
        Task {
            defer { busy = false; editState.finishWrite() }
            do {
                let saved: BookReflectionRecord = try await store.request(
                    "reflection.save",
                    ["bookId": bookId, "content": submittedContent]
                )
                onSaved?(saved)
                hasExisting = saved.hasContent
                // Also protect the small interval before UIKit applies the disabled state.
                if content == submittedContent { dismiss() }
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private func deleteReflection() {
        guard !busy, editState.beginWrite() else { return }
        busy = true
        error = nil
        Task {
            defer { busy = false; editState.finishWrite() }
            do {
                let result: NativeDeletionResult = try await store.request("reflection.delete", ["bookId": bookId])
                guard result.deleted else { return }
                onSaved?(nil)
                dismiss()
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

private struct NativeReflectionCitationPicker: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let currentBookTitle: String
    let onSelect: (ReflectionCitation) -> Void

    @State private var citations: [ReflectionCitation] = []
    @State private var query = ""
    @State private var loading = true
    @State private var error: String?

    private var filtered: [ReflectionCitation] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return citations }
        return citations.filter { citation in
            citation.bookTitle.localizedCaseInsensitiveContains(trimmed)
                || citation.chapterLabel.localizedCaseInsensitiveContains(trimmed)
                || citation.selectedText.localizedCaseInsensitiveContains(trimmed)
                || citation.note.localizedCaseInsensitiveContains(trimmed)
        }
    }

    var body: some View {
        NavigationView {
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass")
                        .foregroundStyle(NativeTheme.muted)
                    TextField(L("搜索书名、章节、划线或批注"), text: $query)
                        .font(NativeTheme.font(15))
                }
                .padding(.horizontal, 12)
                .frame(height: 38)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
                .padding(.horizontal, 20)
                .padding(.vertical, 12)

                if loading {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let error {
                    Text(error)
                        .font(NativeTheme.font(14))
                        .foregroundStyle(NativeTheme.secondary)
                        .padding(24)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if filtered.isEmpty {
                    Text(citations.isEmpty ? L("暂无可引用的批注。") : L("没有找到匹配的批注。"))
                        .font(NativeTheme.font(14))
                        .foregroundStyle(NativeTheme.muted)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(filtered) { item in
                        Button {
                            onSelect(item)
                            dismiss()
                        } label: {
                            VStack(alignment: .leading, spacing: 5) {
                                Text(item.displayText)
                                    .font(NativeTheme.font(14))
                                    .foregroundStyle(NativeTheme.ink)
                                    .lineLimit(3)
                                Text(item.chapterLabel.isEmpty ? item.bookTitle : "\(item.bookTitle) · \(item.chapterLabel)")
                                    .font(NativeTheme.font(12, relativeTo: .caption))
                                    .foregroundStyle(NativeTheme.muted)
                                    .lineLimit(1)
                            }
                            .padding(.vertical, 4)
                        }
                        .listRowBackground(Color.clear)
                    }
                    .listStyle(.plain)
                }
            }
            .background(NativeAmbientBackground())
            .navigationTitle(L("引用批注"))
            .navigationBarTitleDisplayMode(.inline)
            .navigationBarHidden(false)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(L("取消")) { dismiss() }
                }
            }
        }
        .navigationViewStyle(.stack)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .task { await load() }
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            citations = try await store.request("reflection.citations", ["bookTitle": currentBookTitle])
        } catch {
            if !Task.isCancelled { self.error = error.localizedDescription }
        }
    }
}

private struct NativeReflectionPhotoPicker: UIViewControllerRepresentable {
    let onPicked: (UIImage?) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(onPicked: onPicked) }

    func makeUIViewController(context: Context) -> PHPickerViewController {
        var config = PHPickerConfiguration()
        config.filter = .images
        config.selectionLimit = 1
        let picker = PHPickerViewController(configuration: config)
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ uiViewController: PHPickerViewController, context: Context) {}

    final class Coordinator: NSObject, PHPickerViewControllerDelegate {
        let onPicked: (UIImage?) -> Void

        init(onPicked: @escaping (UIImage?) -> Void) {
            self.onPicked = onPicked
        }

        func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
            picker.dismiss(animated: true)
            guard let provider = results.first?.itemProvider,
                  provider.canLoadObject(ofClass: UIImage.self) else {
                onPicked(nil)
                return
            }
            provider.loadObject(ofClass: UIImage.self) { object, _ in
                DispatchQueue.main.async {
                    self.onPicked(object as? UIImage)
                }
            }
        }
    }
}
