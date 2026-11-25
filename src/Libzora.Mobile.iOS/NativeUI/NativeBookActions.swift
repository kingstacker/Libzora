import SwiftUI

struct NativeBookFileRecord: Decodable, Identifiable {
    let id: String
    let name: String
    let format: String
    let size: String
    let readable: Bool
    let local: Bool
}
struct NativeBookFilesSnapshot: Decodable {
    let files: [NativeBookFileRecord]
    let preferredFileId: String?
}
enum NativeBookFileAction: String, Identifiable {
    case read, export
    var id: String { rawValue }
    var title: String { self == .read ? L("选择阅读格式") : L("导出原书") }
}

struct NativeBookFilePicker: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    let action: NativeBookFileAction
    let onRead: (String) -> Void
    @State private var snapshot: NativeBookFilesSnapshot?
    @State private var loading = true
    @State private var busy = false
    @State private var error: String?
    private struct ExportResult: Decodable { let shared: Bool }

    var body: some View {
        NavigationView {
            NativeForm {
                if loading { ProgressView() }
                if let snapshot {
                    ForEach(snapshot.files.filter { action == .export || $0.readable }) { file in
                        Button {
                            if action == .read { onRead(file.id); dismiss() }
                            else { export(file) }
                        } label: {
                            HStack(spacing: 12) {
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(file.name).font(NativeTheme.font(15)).lineLimit(2)
                                    Text("\(file.format) · \(file.size)").font(NativeTheme.font(12))
                                        .foregroundStyle(NativeTheme.secondary)
                                    if !file.local { Text(L("未下载")).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.muted) }
                                }
                                Spacer()
                                if action == .read && file.id == snapshot.preferredFileId {
                                    Image(systemName: "checkmark").foregroundStyle(NativeTheme.ink)
                                }
                                if action == .export { Image(systemName: "square.and.arrow.up") }
                            }.padding(.vertical, 4).contentShape(Rectangle())
                        }.accessibilityIdentifier("book.file.\(file.id)")
                    }
                }
                if busy { ProgressView(L("正在导出…")) }
                if let error {
                    Text(error).foregroundStyle(NativeTheme.secondary)
                    if snapshot == nil { Button(L("重试")) { Task { await load() } } }
                }
            }
            .disabled(busy || loading)
            .navigationTitle(action.title).navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(L("取消")) { dismiss() }.disabled(busy) } }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
            .task { await load() }
    }
    private func load() async {
        loading = true; error = nil
        defer { loading = false }
        do { snapshot = try await store.request("book.files", ["bookId": bookId]) }
        catch { if !Task.isCancelled { self.error = error.localizedDescription } }
    }
    private func export(_ file: NativeBookFileRecord) {
        guard !busy else { return }
        busy = true; error = nil
        Task {
            defer { busy = false }
            do {
                let result: ExportResult = try await store.request("book.export", ["bookId": bookId, "bookFileId": file.id])
                if result.shared { store.showToast(L("原书已导出。")); dismiss() }
            } catch { self.error = error.localizedDescription }
        }
    }
}

struct NativeBookReadingStatusPicker: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    @State private var busy = false
    @State private var error: String?
    private let names = [L("待读"), L("在读"), L("读完")]
    private var status: Int { store.library.first(where: { $0.id == bookId }).map(NativeBookReadingStatus.value) ?? 0 }
    var body: some View {
        NavigationView {
            NativeForm {
                ForEach(0..<3) { value in
                    Button {
                        busy = true
                        Task {
                            defer { busy = false }
                            do {
                                try await store.command("book.readingStatus", ["bookId": bookId, "status": value])
                                try await store.refreshLibrary()
                                dismiss()
                            } catch { self.error = error.localizedDescription }
                        }
                    } label: {
                        HStack { Text(names[value]); Spacer(); if value == status { Image(systemName: "checkmark") } }
                            .font(NativeTheme.font()).contentShape(Rectangle())
                    }.accessibilityIdentifier("book.status.\(value)")
                }
                if let error { Text(error).foregroundStyle(NativeTheme.secondary) }
            }.disabled(busy).navigationTitle(L("阅读状态")).navigationBarTitleDisplayMode(.inline)
                .toolbar { ToolbarItem(placement: .cancellationAction) { Button(L("取消")) { dismiss() }.disabled(busy) } }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
    }
}
