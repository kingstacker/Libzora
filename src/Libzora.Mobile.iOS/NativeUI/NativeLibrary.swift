import SwiftUI
import UIKit

struct NativeNameSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var store: NativeStore
    let title: String
    let button: String
    let save: (String) async throws -> Void
    let isSecure: Bool
    @State private var value: String
    @State private var busy = false
    @State private var error: String?
    @FocusState private var focused: Bool
    init(store: NativeStore, title: String, initial: String, button: String, isSecure: Bool = false, save: @escaping (String) async throws -> Void) {
        self.store = store; self.title = title; self.button = button; self.isSecure = isSecure; self.save = save; _value = State(initialValue: initial)
    }
    var body: some View {
        NavigationView {
            VStack(alignment: .leading, spacing: 12) {
                Group {
                    if isSecure { SecureField(L("请输入密码"), text: $value) }
                    else { TextField(title, text: $value) }
                }.font(NativeTheme.font()).focused($focused).padding(14)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                    .accessibilityIdentifier("name.input")
                if let error { Text(error).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted) }
                Spacer()
            }.padding(24).background(NativeAmbientBackground())
                .navigationTitle(title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                .modifier(NativeKeyboardDone())
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button(L("取消")) { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button(button) {
                        busy = true
                        Task { defer { busy = false }; do { try await save(value.trimmingCharacters(in: .whitespacesAndNewlines)); dismiss() } catch { self.error = error.localizedDescription } }
                    }.disabled(busy || value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty).accessibilityIdentifier("name.save") }
                }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            .onAppear { focused = true }
    }
}

struct NativeBookDetailPage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    @State private var editor = false
    @State private var folders = false
    @State private var rename = false
    @State private var busy = false
    @State private var expanded = false
    private var book: LibraryBook? { store.library.first { $0.id == bookId } }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Button { dismiss() } label: { Image(systemName: "chevron.left").frame(width: 44, height: 44) }
                    .buttonStyle(.plain).accessibilityLabel(L("返回书库")).accessibilityIdentifier("book.back")
                Spacer()
                if let book {
                    Button { action("favorite", ["favorite": !book.favorite]) } label: {
                        Image(systemName: book.favorite ? "star.fill" : "star").frame(width: 44, height: 44)
                    }.buttonStyle(.plain).disabled(busy).accessibilityLabel(book.favorite ? L("取消收藏") : L("收藏"))
                    Menu {
                        Button(L("编辑书籍信息")) { editor = true }; Button(L("加入收藏夹")) { folders = true }
                        Button(L("重命名")) { rename = true }; Button(L("发送到 Kindle")) { action("kindle") }
                        Button(L("移到回收站"), role: .destructive) { action("delete") }
                    } label: { Image(systemName: "ellipsis").frame(width: 44, height: 44) }
                        .buttonStyle(.plain).disabled(busy).accessibilityLabel(L("书籍选项"))
                }
            }.font(.system(size: 20, weight: .regular)).padding(.horizontal, 8).padding(.top, 6)
            ScrollView {
                if let book {
                    VStack(alignment: .leading, spacing: 0) {
                        BookCover(book: book, width: 148, height: 214).frame(maxWidth: .infinity)
                        Text(book.title).font(NativeTheme.font(21, relativeTo: .title2)).lineLimit(3).lineSpacing(5)
                            .multilineTextAlignment(.center).frame(maxWidth: .infinity).padding(.top, 28)
                        Text(book.authors).font(NativeTheme.font(14)).foregroundStyle(NativeTheme.secondary)
                            .multilineTextAlignment(.center).frame(maxWidth: .infinity).padding(.top, 6)
                        Text(L("{0} · {1} · 已读 {2}%", book.formats, book.size, String(format: "%.0f", locale: NativeLocalization.locale, book.progress)))
                            .font(NativeTheme.font(12.5, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                            .frame(maxWidth: .infinity).padding(.top, 8)
                        if let description = book.description, !description.isEmpty {
                            VStack(alignment: .leading, spacing: 8) {
                                Text(description).font(NativeTheme.font(14)).foregroundStyle(NativeTheme.secondary)
                                    .lineLimit(expanded ? nil : 5).lineSpacing(7).textSelection(.enabled)
                                Button(expanded ? L("收起") : L("展开全部")) { expanded.toggle() }
                                    .font(NativeTheme.font(14)).foregroundStyle(NativeTheme.muted).buttonStyle(.plain)
                            }.padding(.top, 32)
                        }
                        HStack {
                            Text(L("书籍信息")).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            Spacer()
                            Button(L("编辑")) { editor = true }.font(NativeTheme.font(14)).foregroundStyle(NativeTheme.muted).buttonStyle(.plain)
                        }.padding(.top, 24).padding(.bottom, 4)
                        VStack(spacing: 14) {
                            fact(L("评分"), book.onlineRating.map { String(format: "%.1f", $0) })
                            fact(L("丛书"), book.series); fact(L("出版社"), book.publisher); fact(L("出版日期"), book.publishDate)
                            fact("ISBN", book.isbn); fact(L("页数"), book.pageCount); fact(L("装帧"), book.binding)
                            fact(L("标签"), book.tags); fact(L("分类"), book.category); fact(L("文件"), book.formats)
                            fact(L("添加时间"), String(book.createdAt.prefix(10)).replacingOccurrences(of: "-", with: "/"))
                        }.font(NativeTheme.font(14)).padding(.top, 7)
                    }.padding(.horizontal, 28).padding(.top, 12).padding(.bottom, 24)
                }
            }
            if let book {
                Button { store.perform { try await store.openReader(book) } } label: {
                    Text(book.progress > 0 ? L("继续阅读 · {0}%", String(format: "%.0f", locale: NativeLocalization.locale, book.progress)) : L("开始阅读"))
                }.buttonStyle(NativeLiquidGlassButtonStyle()).disabled(store.openingReader).accessibilityIdentifier("book.read")
                    .padding(.horizontal, 28).padding(.top, 8).padding(.bottom, 14)
            }
        }.background(NativeAmbientBackground()).navigationBarHidden(true).modifier(NativeHideTabBar())
            .sheet(isPresented: $editor) { if let book { NativeBookEditor(store: store, book: book) } }
            .sheet(isPresented: $folders) { NativeBookCollections(store: store, bookId: bookId) }
            .sheet(isPresented: $rename) {
                NativeNameSheet(store: store, title: L("修改书名"), initial: book?.title ?? "", button: L("保存")) { title in
                    try await store.command("book.rename", ["bookId": bookId, "title": title]); try await store.refreshLibrary()
                }
            }
    }
    @ViewBuilder private func fact(_ name: String, _ value: String?) -> some View {
        if let value, !value.isEmpty {
            HStack(alignment: .top, spacing: 8) {
                Text(name).foregroundStyle(NativeTheme.muted).frame(width: 88, alignment: .leading)
                Text(value).foregroundStyle(NativeTheme.secondary).frame(maxWidth: .infinity, alignment: .trailing).multilineTextAlignment(.trailing)
            }
        }
    }
    private func action(_ operation: String, _ args: [String: Any] = [:]) {
        busy = true; var payload = args; payload["bookId"] = bookId
        store.perform {
            defer { busy = false }; try await store.command("book.\(operation)", payload)
            try await store.refreshLibrary(); if book == nil { dismiss() }
        }
    }
}

struct NativeBookEditor: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let book: LibraryBook
    @State private var fields: [String: String]
    @State private var busy = false
    @State private var error: String?
    init(store: NativeStore, book: LibraryBook) {
        self.store = store; self.book = book
        _fields = State(initialValue: ["title": book.title, "authors": book.authors, "series": book.series ?? "",
            "seriesIndex": book.seriesIndex.flatMap { $0.isFinite ? String($0) : nil } ?? "",
            "publisher": book.publisher ?? "", "publishDate": book.publishDate ?? "", "isbn": book.isbn ?? "",
            "pageCount": book.pageCount ?? "", "binding": book.binding ?? "", "tags": book.tags, "category": book.category,
            "description": book.description ?? "", "onlineRating": book.onlineRating.map { String($0) } ?? ""])
    }
    private func field(_ key: String) -> Binding<String> { Binding(get: { fields[key] ?? "" }, set: { fields[key] = $0 }) }
    private var coverImage: UIImage? {
        guard let encoded = fields["cover"], let data = Data(base64Encoded: encoded) else { return nil }
        return UIImage(data: data)
    }
    private var draftTitle: String {
        let value = (fields["title"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? book.title : value
    }
    private var draftAuthors: String {
        let value = (fields["authors"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? book.authors : value
    }
    var body: some View {
        NavigationView {
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    coverPanel
                    editorSection(L("简介")) { descriptionEditor }
                    editorSection(L("书籍信息")) {
                        VStack(spacing: 16) {
                            NativeTextField(title: L("书名"), text: field("title")); NativeTextField(title: L("作者"), text: field("authors"))
                            NativeTextField(title: L("评分（0–10）"), text: field("onlineRating")).keyboardType(.decimalPad); NativeTextField(title: L("标签"), text: field("tags"))
                            NativeTextField(title: L("分类"), text: field("category")); NativeTextField(title: L("丛书"), text: field("series"))
                            NativeTextField(title: L("系列序号"), text: field("seriesIndex")).keyboardType(.decimalPad)
                            NativeTextField(title: L("出版社"), text: field("publisher")); NativeTextField(title: L("出版日期"), text: field("publishDate"))
                            NativeTextField(title: "ISBN", text: field("isbn")); NativeTextField(title: L("页数"), text: field("pageCount"))
                            NativeTextField(title: L("装帧"), text: field("binding"))
                        }
                    }
                    if let error {
                        Text(error).font(NativeTheme.font(13, relativeTo: .footnote)).foregroundStyle(NativeTheme.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 4)
                    }
                }.padding(.horizontal, 24).padding(.top, 12).padding(.bottom, 32).disabled(busy)
            }
            .modifier(NativeScrollDismissesKeyboard())
            .background(NativeAmbientBackground())
            .navigationTitle(L("编辑书籍")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone())
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button(L("取消")) { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button(L("保存")) {
                        busy = true
                        Task { defer { busy = false }; do { try await store.command("book.save", ["bookId": book.id, "fields": fields]); try await store.refreshLibrary(); dismiss() } catch { self.error = error.localizedDescription } }
                    }.disabled(busy || (fields["title"] ?? "").isEmpty) }
                }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
            .onChange(of: fields["series"]) { value in
                let name = (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                let original = (book.series ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                if name.caseInsensitiveCompare(original) != .orderedSame { fields["seriesIndex"] = "" }
            }
    }

    /// The cover and the live title/author preview fill the header so the sheet
    /// does not open on a small cover floating in empty space.
    private var coverPanel: some View {
        HStack(alignment: .top, spacing: 18) {
            coverThumbnail
            VStack(alignment: .leading, spacing: 4) {
                Text(draftTitle).font(NativeTheme.font(17, relativeTo: .headline)).lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
                Text(draftAuthors).font(NativeTheme.font(13, relativeTo: .footnote))
                    .foregroundStyle(NativeTheme.secondary).lineLimit(2)
                Text(L("{0} · {1} · 已读 {2}%", book.formats, book.size, String(format: "%.0f", locale: NativeLocalization.locale, book.progress)))
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                    .padding(.top, 2)
                Spacer(minLength: 12)
                Button {
                    busy = true
                    Task { defer { busy = false }; do { let value: CoverResult = try await store.request("book.cover"); if let cover = value.cover { fields["cover"] = cover } } catch { self.error = error.localizedDescription } }
                } label: {
                    Label(L("更换封面"), systemImage: "photo.on.rectangle.angled")
                }.buttonStyle(NativeLiquidGlassButtonStyle(compact: true)).disabled(busy)
                    .accessibilityIdentifier("book.cover.change")
            }
            Spacer(minLength: 0)
        }.padding(.vertical, 4)
    }

    @ViewBuilder private var coverThumbnail: some View {
        if let image = coverImage {
            Image(uiImage: image).resizable().scaledToFill()
                .frame(width: 104, height: 151)
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .shadow(color: .black.opacity(0.12), radius: 5, y: 2)
        } else {
            BookCover(book: book, width: 104)
        }
    }

    private func editorSection<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(NativeTheme.font(13, relativeTo: .footnote)).foregroundStyle(NativeTheme.muted)
                .padding(.leading, 4)
            content()
        }
    }

    private var descriptionEditor: some View {
        ZStack(alignment: .topLeading) {
            if (fields["description"] ?? "").isEmpty {
                Text(L("写点什么…")).font(NativeTheme.font()).foregroundStyle(NativeTheme.faint)
                    .padding(.horizontal, 12).padding(.vertical, 11).allowsHitTesting(false)
            }
            TextEditor(text: field("description")).font(NativeTheme.font()).frame(minHeight: 150)
                .padding(.horizontal, 7).padding(.vertical, 3)
                .modifier(NativeScrollContentBackgroundHidden())
        }.background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
    }
}

struct NativeBookCollections: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    @State private var name = ""
    @State private var busy = false
    var body: some View {
        NavigationView {
            NativeForm {
                ForEach(store.folders.filter { !$0.automatic }) { folder in
                    Toggle(folder.name, isOn: Binding(get: { store.library.first { $0.id == bookId }?.collectionIds.contains(folder.id) ?? false }, set: { member in
                        busy = true
                        store.perform { defer { busy = false }; try await store.command("collection.membership", ["bookId": bookId, "collectionId": folder.id, "member": member]); try await store.refreshLibrary() }
                    })).tint(NativeTheme.systemSwitchTint)
                }
                Section(L("新建收藏夹")) {
                    TextField(L("名称"), text: $name)
                    Button(L("创建并加入")) {
                        busy = true
                        store.perform {
                            defer { busy = false }
                            let folder: CreatedFolder = try await store.request("collection.create", ["name": name])
                            try await store.command("collection.membership", ["bookId": bookId, "collectionId": folder.id, "member": true])
                            name = ""; try await store.refreshLibrary()
                        }
                    }.disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }.disabled(busy).navigationTitle(L("加入收藏夹")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                .toolbar { Button(L("完成")) { dismiss() }.disabled(busy) }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
    }
}

struct NativeCollectionPage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let folderId: String
    @State private var rename = false
    @State private var addBooks = false
    @State private var busy = false
    private var folder: LibraryFolder? { store.folders.first { $0.id == folderId } }
    private var books: [LibraryBook] {
        NativeBookOrder.sorted(store.library.filter { $0.collectionIds.contains(folderId) }, sort: store.preferences.sort)
    }
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Button { dismiss() } label: { Image(systemName: "chevron.left").frame(width: 44, height: 44) }
                    .accessibilityLabel(L("返回收藏夹")).accessibilityIdentifier("collection.back")
                Text(folder?.name ?? L("收藏夹")).font(NativeTheme.font(21, relativeTo: .title2)).lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 4)
                if folder?.automatic == false {
                    Button { addBooks = true } label: { Image(systemName: "plus").frame(width: 44, height: 44) }
                        .accessibilityLabel(L("添加书籍")).accessibilityIdentifier("collection.add")
                    Menu {
                        Button(L("重命名")) { rename = true }; Button(L("清空书籍")) { run("clear") }
                        Menu(L("合并到")) { ForEach(store.folders.filter { !$0.automatic && $0.id != folderId }) { target in Button(target.name) { run("merge", ["targetId": target.id]) } } }
                        Button(L("解散收藏夹"), role: .destructive) { run("dissolve") }
                    } label: { Image(systemName: "ellipsis").frame(width: 44, height: 44) }
                        .accessibilityLabel(L("收藏夹选项"))
                }
            }.font(.system(size: 20, weight: .regular)).buttonStyle(.plain).padding(.horizontal, 8).padding(.top, 16)
            GeometryReader { geometry in
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack(spacing: 0) {
                            Text(L("{0} 本", books.count)).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                            Spacer()
                            NativeBookSortMenu(store: store)
                            NativeBookLayoutButton(store: store, identifier: "collection.layout")
                        }.padding(.trailing, -12).padding(.bottom, 14)
                        if books.isEmpty {
                            VStack(spacing: 12) {
                                Image(systemName: "book").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(NativeTheme.muted)
                                Text(L("收藏夹还是空的")).font(NativeTheme.font(17, relativeTo: .headline))
                                if folder?.automatic == false {
                                    Text(L("点右上角 + 添加书籍")).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                                }
                            }.frame(maxWidth: .infinity).padding(.top, 96)
                        } else {
                            NativeBookShelf(store: store, books: books, pageWidth: geometry.size.width) { book in
                                var items = [
                                    NativeBookMenuItem(L("阅读")) { store.perform { try await store.openReader(book) } },
                                    NativeBookMenuItem(book.favorite ? L("取消收藏") : L("收藏")) {
                                        store.perform { try await store.command("book.favorite", ["bookId": book.id, "favorite": !book.favorite]); try await store.refreshLibrary() }
                                    }
                                ]
                                if folder?.automatic == false {
                                    items.append(NativeBookMenuItem(L("移出收藏夹"), destructive: true) {
                                        run("membership", ["bookId": book.id, "member": false])
                                    })
                                }
                                return items
                            }
                        }
                    }.padding(.horizontal, 24).padding(.top, 8).padding(.bottom, 28)
                }.refreshable {
                    do { try await store.refreshLibrary() }
                    catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
                }
            }
        }.background(NativeAmbientBackground()).disabled(busy).navigationBarHidden(true)
            .modifier(NativeHideTabBar())
            .sheet(isPresented: $rename) {
                NativeNameSheet(store: store, title: L("重命名收藏夹"), initial: folder?.name ?? "", button: L("保存")) { name in
                    try await store.command("collection.rename", ["collectionId": folderId, "name": name]); try await store.refreshLibrary()
                }
            }
            .sheet(isPresented: $addBooks) {
                NavigationView {
                    List(store.library.filter { !$0.collectionIds.contains(folderId) }) { book in
                        Button { run("membership", ["bookId": book.id, "member": true]) } label: { BookRow(book: book) }
                            .accessibilityIdentifier("collection.pick.\(book.id)")
                    }.disabled(busy).navigationTitle(L("添加书籍")).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).toolbar { Button(L("完成")) { addBooks = false }.disabled(busy) }
                }.navigationViewStyle(.stack)
            }
    }
    private func run(_ operation: String, _ args: [String: Any] = [:]) {
        var payload = args; payload["collectionId"] = folderId; busy = true
        store.perform { defer { busy = false }; try await store.command("collection.\(operation)", payload); try await store.refreshLibrary(); if folder == nil { dismiss() } }
    }
}
