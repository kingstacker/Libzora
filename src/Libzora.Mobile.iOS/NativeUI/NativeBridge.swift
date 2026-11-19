import Foundation
import SwiftUI
import UIKit

public typealias ManagedRequest = @convention(c) (Int64, UnsafePointer<CChar>?) -> Void

struct NativeFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private struct Reply<Value: Decodable>: Decodable {
    let ok: Bool
    let result: Value?
    let error: String?
}

struct Done: Decodable {}

struct MobilePreferences: Codable, Equatable {
    var uiLanguage = "auto"
    var themeMode = 0
    var listView = false
    var galleryMode = false
    var colorfulMode = false
    var showBookSyncStatus = false
    var showBookRating = false
    var stackBookSeries = false
    var collectionsView = false
    var pdfZoom = 1.0
    var sort = 0
    var readerTheme = 0
    var readerThemeFollowsAppearance = true
    var readerFontScale = 1.35
    var readerFontFamily = "京华老宋体"
    var readerLineHeight = 1.75
    var readerParagraphSpacing = 0.0
    var readerBodyPadding = 24.0
    var readerParagraphIndent = false
    var readerVerticalWriting = false
    var readerPageAnimation = 1
    var readerProgressDisplay = 0

    var colorScheme: ColorScheme? {
        themeMode == 1 ? .light : themeMode == 2 ? .dark : nil
    }
    /// The page colours from `ReaderPalette`. SwiftUI fills the margins around
    /// the rendered page, so any difference shows as a band above and below it.
    func paper(for theme: Int) -> Color {
        switch theme {
        case 1: return Color(red: 244 / 255, green: 242 / 255, blue: 236 / 255)
        case 2: return Color(red: 238 / 255, green: 243 / 255, blue: 236 / 255)
        case 5: return Color(red: 14 / 255, green: 14 / 255, blue: 14 / 255)
        default: return .white
        }
    }
}

struct LibraryFolder: Decodable, Identifiable, Equatable {
    let id: String
    let name: String
    let count: Int
    let automatic: Bool
}

struct LibrarySnapshot: Decodable {
    let books: [LibraryBook]
    let collections: [LibraryFolder]
    let preferences: MobilePreferences
    let continueBookId: String?
}

struct NativePromptChoice: Decodable {
    let id: String
    let title: String
    let cancel: Bool?
}

struct NativePrompt: Decodable, Identifiable {
    let id: Int64
    let title: String
    let message: String
    let confirm: String
    let choices: [NativePromptChoice]?
}

struct ReaderSnapshot: Decodable, Equatable {
    var ready = false
    var chromeVisible = true
    var isPdf = false
    var title = ""
    var chapter = ""
    var position = ""
    var progress = 0.0
    var bookmark = false
    var error: String?
    var speechState = 0
    var speechStatus = ""
    var selectedText: String?
    var selectionBounds: NativeSelectionBounds?
    var footnoteReturnVisible = false
    var navigationRevision: Int64?
}

struct ReaderFootnoteAnchor: Decodable, Equatable {
    let x: Double
    let y: Double
    let width: Double
    let height: Double
    let viewportWidth: Double
    let viewportHeight: Double
}

/// One in-page note card. The anchor is in the rendered reader surface's
/// coordinates, which is the same space the selection toolbar already uses.
struct ReaderFootnote: Decodable, Equatable, Identifiable {
    let id: Int64
    let visible: Bool
    let title: String
    let text: String?
    let loading: Bool
    let canJump: Bool
    let anchor: ReaderFootnoteAnchor
    let error: String?
}

private struct NativeEvent: Decodable {
    let type: String
    let message: String?
    let reader: ReaderSnapshot?
    let prompt: NativePrompt?
    let preferences: MobilePreferences?
    let language: String?
    let id: Int64?
    let dialog: ReaderDialog?
    let media: ReaderMedia?
    let footnote: ReaderFootnote?
    let search: ReaderSearchBatch?
    let txtImport: TxtReview?
    let busy: Bool?
    let channel: String?
    let sessionId: String?
    let text: String?
    let enabled: Bool?
    let bookId: String?
    let tab: Int?
}

/// Reader state changes on page turns, speech progress and every AI token.
/// Only the reader observes it, so the library tabs below are not re-rendered.
@MainActor
final class NativeReaderSession: ObservableObject {
    @Published var state = ReaderSnapshot()
    @Published var dialog: ReaderDialog?
    @Published var media: ReaderMedia?
    @Published var footnote: ReaderFootnote?
    @Published var searchBatch: ReaderSearchBatch?
    @Published var aiStreams: [String: String] = [:]
    var appearanceSessionId = ""
    /// Monotonic per reading session. SwiftUI may recreate the reader view
    /// (resetting @State) while the renderer keeps the last revision, so an
    /// older-looking counter would make the renderer ignore appearance changes.
    var appearanceRevision = 0
    var appearanceColorScheme: ColorScheme?
}

@MainActor
final class NativeStore: ObservableObject {
    let handler: ManagedRequest
    let readerController: UIViewController
    let showsLaunchAnimation: Bool
    @Published var library: [LibraryBook] = [] {
        didSet { librarySeriesCache = nil }
    }
    private var librarySeriesCache: [NativeBookSeries.ID: NativeBookSeries]?
    private static let bookOpenOrderKey = "libzora.reader.bookOpenOrder"
    @Published private var bookOpenOrder = UserDefaults.standard.dictionary(forKey: bookOpenOrderKey) as? [String: Int] ?? [:]
    @Published var folders: [LibraryFolder] = []
    @Published private(set) var preparingLibrary = true
    @Published private(set) var libraryLoaded = false
    @Published private(set) var libraryLoadError: String?
    @Published var visibleAmbientColors: [NativeAmbientColor] = []
    @Published var fallbackAmbientColors: [NativeAmbientColor] = []
    var ambientColors: [NativeAmbientColor] {
        visibleAmbientColors.isEmpty ? fallbackAmbientColors : visibleAmbientColors
    }
    @Published var preferences = MobilePreferences() {
        didSet { applyUiLanguage(NativeLocalization.resolvedLanguage(preferences.uiLanguage)) }
    }
    @Published private(set) var uiLanguage = NativeLocalization.language
    @Published var continueBookId: String?
    @Published var readerBook: LibraryBook?
    @Published var openingReader = false
    /// Hides the shelf until the launch restore either presents its book or
    /// proves there is nothing to restore, so the library never flashes first.
    @Published var restoringReader = false
    @Published var developerMode = false
    @Published var account: AccountStatus?
    let session = NativeReaderSession()
    @Published var prompt: NativePrompt?
    @Published var toast: String?
    @Published var failure: String? {
        didSet { if let failure { NativeAlerts.error(failure) { [weak self] in self?.failure = nil } } }
    }
    @Published var restartRequired = false
    @Published var dataBusy = false
    @Published var busyMessage = ""
    @Published var txtReview: TxtReview?
    @Published var syncStatus = ""
    @Published var requestedTab: Int?
    private static let lastReaderKey = "libzora.reader.lastBookId"
    private var pendingReaderRestore: String?
    private var pendingOpenBookId: String?
    /// Bumped whenever a widget link supersedes the reader that is opening.
    private var readerOpenGeneration = 0
    private var nextId: Int64 = 0
    private var pending: [Int64: (Data) -> Void] = [:]
    private var toastTask: Task<Void, Never>?
    private var preferenceRevision = 0
    private var preferenceWrites = 0
    private var preferenceSaveTask: Task<Void, Never>?
    private static let localizedCommands: Set<String> = [
        "settings.profile", "dashboard.get", "materials.get", "reader.toc", "reader.fonts"
    ]
    private var libraryRefreshRevision = 0
    private var launchStarted = false
    private var libraryChangedDuringLaunch = false

    init(handler: @escaping ManagedRequest, readerController: UIViewController) {
        self.handler = handler
        self.readerController = readerController
        let savedReader = ProcessInfo.processInfo.arguments.contains("--libzora-disable-restore")
            ? nil : UserDefaults.standard.string(forKey: Self.lastReaderKey)
        showsLaunchAnimation = savedReader == nil
        pendingReaderRestore = savedReader
        restoringReader = savedReader != nil
    }

    func request<Value: Decodable>(_ command: String, _ args: [String: Any] = [:],
                                   timeoutNanoseconds: UInt64? = nil) async throws -> Value {
        // SwiftUI starts locale-dependent tasks as soon as the optimistic
        // selection changes. Wait until managed preferences have caught up.
        // The save command itself and its library refresh must never wait here.
        if Self.localizedCommands.contains(command) {
            while let saving = preferenceSaveTask {
                await saving.value
                try Task.checkCancellation()
            }
        }
        nextId += 1
        let id = nextId
        let data = try JSONSerialization.data(withJSONObject: ["command": command, "args": args])
        let json = String(decoding: data, as: UTF8.self)
        var timeoutTask: Task<Void, Never>?
        defer { timeoutTask?.cancel() }
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                pending[id] = { data in
                    do {
                        let reply = try JSONDecoder().decode(Reply<Value>.self, from: data)
                        if reply.ok, let value = reply.result { continuation.resume(returning: value) }
                        else { continuation.resume(throwing: NativeFailure(message: reply.error ?? L("操作失败"))) }
                    } catch { continuation.resume(throwing: error) }
                }
                if Task.isCancelled { cancel(id); return }
                if let timeoutNanoseconds {
                    timeoutTask = Task { [weak self] in
                        do { try await Task.sleep(nanoseconds: timeoutNanoseconds) }
                        catch { return }
                        self?.cancel(id, timedOut: true)
                    }
                }
                json.withCString { handler(id, $0) }
            }
        }, onCancel: {
            Task { @MainActor in self.cancel(id) }
        })
    }

    private func cancel(_ id: Int64, timedOut: Bool = false) {
        guard let completion = pending.removeValue(forKey: id) else { return }
        let message = timedOut ? L("书库加载超时，请重试。") : L("操作已取消")
        let data = Data("{\"ok\":false,\"error\":\"\(message)\"}".utf8)
        completion(data)
        "{\"command\":\"cancel\"}".withCString { handler(id, $0) }
    }

    func resolve(_ id: Int64, json: String) {
        pending.removeValue(forKey: id)?(Data(json.utf8))
    }

    func receive(_ json: String) {
        guard let event = try? JSONDecoder().decode(NativeEvent.self, from: Data(json.utf8)) else { return }
        switch event.type {
        case "reader": if let reader = event.reader, reader != session.state { session.state = reader }
        case "preferences": if let value = event.preferences, value != preferences { preferences = value }
        case "language":
            // An older save may still be returning while the user makes a
            // newer choice. Its locale must not undo the optimistic selection.
            if preferenceWrites == 0, let language = event.language { applyUiLanguage(language) }
        case "developer": developerMode = event.enabled ?? false
        case "libraryChanged":
            // Keep a single owner for the first snapshot. A sync notification
            // must not supersede it and release the splash before data arrives.
            if preparingLibrary { libraryChangedDuringLaunch = true }
            else { perform { try await self.refreshLibrary() } }
        case "confirm": if let prompt = event.prompt { self.prompt = prompt; NativeAlerts.confirm(prompt, store: self) }
        case "confirmDismissed": if let id = event.id { NativeAlerts.dismissPrompt(id); if prompt?.id == id { prompt = nil } }
        case "readerDialog": session.dialog = event.dialog
        case "readerMedia": session.media = event.media
        case "readerFootnote":
            session.footnote = event.footnote?.visible == true ? event.footnote : nil
        case "readerSearch": session.searchBatch = event.search
        case "txtImport": txtReview = event.txtImport
        case "dataBusy": dataBusy = event.busy ?? false; busyMessage = event.message ?? ""
        case "status": if event.channel == "sync" { syncStatus = event.message ?? "" }
        case "aiText": if let id = event.sessionId { session.aiStreams[id] = event.text ?? "" }
        case "restart": restartRequired = true
        case "toast": showToast(event.message ?? "")
        case "accountInvalidated": account = nil
        case "openBook": if let id = event.bookId { openRequestedBook(id) }
        case "openTab":
            if let tab = event.tab {
                // A tab link asks for the tab itself, so it must also cancel
                // the saved reading session and any reader that is still
                // opening. Otherwise the last book reopens over the tab.
                pendingReaderRestore = nil
                pendingOpenBookId = nil
                requestedTab = tab
                restoringReader = false
                dismissReaderForTab()
            }
        case "closeReader": if readerBook != nil { perform { await self.closeReader() } }
        default: break
        }
    }

    func booksInDisplayOrder(for series: NativeBookSeries) -> [LibraryBook] {
        let mostRecentlyOpened = series.books.max { (bookOpenOrder[$0.id] ?? 0) < (bookOpenOrder[$1.id] ?? 0) }
        let leadingID = mostRecentlyOpened.flatMap { bookOpenOrder[$0.id] == nil ? nil : $0.id }
        return series.booksByPromoting(leadingID)
    }

    func seriesSnapshot(for id: NativeBookSeries.ID) -> NativeBookSeries? {
        if librarySeriesCache == nil { librarySeriesCache = NativeBookSeries.groups(library) }
        return librarySeriesCache?[id]
    }

    /// The root owns startup, rather than waiting for a tab's onAppear. The
    /// branded splash stays for two seconds while the shelf is prepared in
    /// parallel. Restoring the last reader skips the animation and its delay.
    func prepareLaunch(viewport: CGSize) async {
        guard !launchStarted else { return }
        launchStarted = true
        async let launchAnimation: Void = Task.sleep(nanoseconds: showsLaunchAnimation ? 2_000_000_000 : 0)
        do {
            try await refreshLibrary(timeoutNanoseconds: 30_000_000_000)
            await NativeCoverCache.prepareLibrary(books: library, folders: folders, preferences: preferences,
                                                 continueBookId: continueBookId, viewport: viewport)
            try Task.checkCancellation()
        } catch {
            if !libraryLoaded {
                libraryLoadError = Task.isCancelled ? L("书库加载已中断，请重试。") : error.localizedDescription
            }
        }
        try? await launchAnimation
        preparingLibrary = false
        if libraryLoaded && !Task.isCancelled {
            openPendingReaderIfNeeded()
            restorePendingReaderIfNeeded()
        } else {
            releaseLaunchRestore()
        }
        if libraryChangedDuringLaunch {
            libraryChangedDuringLaunch = false
            perform { try await self.refreshLibrary() }
        }
    }

    func refreshLibrary(timeoutNanoseconds: UInt64? = nil) async throws {
        let revision = preferenceRevision
        libraryRefreshRevision += 1
        let refreshRevision = libraryRefreshRevision
        let value: LibrarySnapshot
        do {
            value = try await request("library.get", timeoutNanoseconds: timeoutNanoseconds)
        } catch {
            if refreshRevision == libraryRefreshRevision && !Task.isCancelled {
                libraryLoadError = error.localizedDescription
            }
            throw error
        }
        guard refreshRevision == libraryRefreshRevision else { return }
        // Every assignment re-renders all tabs, even with an equal value.
        if library != value.books { library = value.books }
        if folders != value.collections { folders = value.collections }
        if revision == preferenceRevision && preferenceWrites == 0 && preferences != value.preferences {
            preferences = value.preferences
        }
        if continueBookId != value.continueBookId { continueBookId = value.continueBookId }
        libraryLoadError = nil
        if !libraryLoaded { libraryLoaded = true }
        guard !preparingLibrary else { return }
        openPendingReaderIfNeeded()
        restorePendingReaderIfNeeded()
    }

    /// A widget can deep-link into a book before the shelf has loaded, or
    /// while another book is open. Keep the request until a fresh library
    /// snapshot can resolve it and the reader is free.
    func openRequestedBook(_ bookId: String) {
        pendingOpenBookId = bookId
        pendingReaderRestore = nil
        guard !preparingLibrary else { return }
        if readerBook != nil {
            perform { await self.closeReader() }
        } else if openingReader {
            // The reader that is opening came from the saved-session restore
            // or an older link; the newest widget tap wins.
            readerOpenGeneration += 1
        } else if !library.isEmpty {
            openPendingReaderIfNeeded()
        } else {
            perform { try await self.refreshLibrary() }
        }
    }

    /// Invalidates an in-flight reader open and dismisses a visible cover.
    private func dismissReaderForTab() {
        readerOpenGeneration += 1
        restoringReader = false
        if readerBook != nil { perform { await self.closeReader() } }
    }

    private func openPendingReaderIfNeeded() {
        guard let bookId = pendingOpenBookId, !library.isEmpty,
              readerBook == nil, !openingReader, !restartRequired else { return }
        pendingOpenBookId = nil
        guard let book = library.first(where: { $0.id == bookId }) else {
            restoringReader = false
            return
        }
        // An explicit widget tap wins over restoring the last reader session.
        Task { try? await self.openReader(book) }
    }

    /// The shelf's first successful load is the safe moment to reopen the
    /// book that was on screen when the app was last quit.
    private func restorePendingReaderIfNeeded() {
        guard let bookId = pendingReaderRestore,
              readerBook == nil, !openingReader, !restartRequired,
              pendingOpenBookId == nil else { return }
        guard !library.isEmpty else {
            // Keep waiting for a populated snapshot, but never trap the user
            // behind the launch cover on a shelf that is still loading.
            restoringReader = false
            return
        }
        pendingReaderRestore = nil
        guard let book = library.first(where: { $0.id == bookId }) else {
            UserDefaults.standard.removeObject(forKey: Self.lastReaderKey)
            restoringReader = false
            return
        }
        // An unstructured task outlives the refresh that triggered it, so a
        // cancelled pull-to-refresh cannot eat the reader restoration.
        Task { try? await self.openReader(book) }
    }

    /// A failed first shelf load must not leave the launch cover on screen.
    func releaseLaunchRestore() {
        if readerBook == nil && !openingReader { restoringReader = false }
    }

    private func applyUiLanguage(_ language: String) {
        NativeLocalization.setLanguage(language)
        let effective = NativeLocalization.language
        if uiLanguage != effective { uiLanguage = effective }
    }

    /// A Binding setter updates the native control immediately. Persistence and
    /// reader reflow complete asynchronously; older replies cannot undo a tap.
    func setPreferences(_ value: MobilePreferences) {
        guard value != preferences else { return }
        let previous = preferences
        preferenceRevision += 1
        let revision = preferenceRevision
        preferences = value
        preferenceWrites += 1
        let precedingSave = preferenceSaveTask
        preferenceSaveTask = Task { [weak self] in
            await precedingSave?.value
            guard let self else { return }
            defer {
                self.preferenceWrites -= 1
                if self.preferenceWrites == 0 { self.preferenceSaveTask = nil }
            }
            // Preserve write order and skip taps superseded before their save
            // starts, so an older request cannot become the persisted choice.
            guard self.preferenceRevision == revision else { return }
            do {
                let object = try JSONSerialization.jsonObject(with: JSONEncoder().encode(value))
                let saved: MobilePreferences = try await self.request("preferences.save", ["preferences": object])
                if self.preferenceRevision == revision {
                    self.preferences = saved
                    // A second setting may have superseded the request that enabled badges.
                    if saved.showBookSyncStatus || previous.showBookSyncStatus {
                        do { try await self.refreshLibrary() }
                        catch { if !Task.isCancelled { self.failure = error.localizedDescription } }
                    }
                }
            } catch {
                if self.preferenceRevision == revision { self.preferences = previous }
                if !Task.isCancelled { self.failure = error.localizedDescription }
            }
        }
    }

    func openReader(_ book: LibraryBook, location: [String: Any]? = nil) async throws {
        guard !openingReader, readerBook == nil else { return }
        openingReader = true
        defer { openingReader = false }
        let generation = readerOpenGeneration
        session.state = ReaderSnapshot()
        session.dialog = nil
        session.appearanceSessionId = UUID().uuidString
        session.appearanceRevision = 0
        session.appearanceColorScheme = nil
        var args: [String: Any] = ["bookId": book.id, "sessionId": session.appearanceSessionId]
        if let location { args["location"] = location }
        // Present the cover before the reader reports ready. Otherwise the
        // shelf stays visible while the chapter engine opens the book.
        readerBook = book
        restoringReader = false
        do {
            let _: Done = try await request("reader.open", args)
            guard generation == readerOpenGeneration else {
                // A widget link arrived while the reader was opening. A tab link
                // must not be covered by this reader, and a newer book link
                // should replace it, so close the stale reader first.
                openingReader = false
                let _: Done? = try? await request("reader.close")
                openPendingReaderIfNeeded()
                return
            }
            UserDefaults.standard.set(book.id, forKey: Self.lastReaderKey)
            let nextOrder = (bookOpenOrder.values.max() ?? 0) + 1
            bookOpenOrder[book.id] = nextOrder
            UserDefaults.standard.set(bookOpenOrder, forKey: Self.bookOpenOrderKey)
        } catch {
            if generation == readerOpenGeneration {
                readerBook = nil
                restoringReader = false
                let _: Done? = try? await request("reader.close")
                openPendingReaderIfNeeded()
            }
            throw error
        }
    }

    func closeReader() async {
        restoringReader = false
        do { let _: Done = try await request("reader.close") }
        catch { if !Task.isCancelled { failure = error.localizedDescription }; return }
        readerBook = nil
        UserDefaults.standard.removeObject(forKey: Self.lastReaderKey)
        do { try await refreshLibrary() }
        catch { if !Task.isCancelled { failure = error.localizedDescription } }
    }

    func perform(_ work: @escaping @MainActor () async throws -> Void) {
        Task {
            do { try await work() }
            catch { if !Task.isCancelled { failure = error.localizedDescription } }
        }
    }

    func command(_ command: String, _ args: [String: Any] = [:], message: String? = nil) async throws {
        let _: Done = try await request(command, args)
        if let message { showToast(message) }
    }

    func showToast(_ text: String) {
        toastTask?.cancel()
        toast = text
        toastTask = Task {
            do { try await Task.sleep(nanoseconds: 3_000_000_000) }
            catch { return }
            toast = nil
        }
    }

    func answerPrompt(_ value: NativePrompt, choice: String) {
        prompt = nil
        perform { try await self.command("ui.confirm", ["id": value.id, "choice": choice]) }
    }
}

@MainActor
private enum NativeRuntime {
    static var store: NativeStore?
}

@_cdecl("lz_ios_set_ui_language")
public func setNativeUiLanguage(_ pointer: UnsafePointer<CChar>?) {
    guard let pointer else { return }
    NativeLocalization.setLanguage(String(cString: pointer))
}

@_cdecl("lz_ios_create_shell")
@MainActor
public func createNativeShell(_ handler: ManagedRequest?, _ readerPointer: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let handler, let readerPointer else { return nil }
    NativeTheme.configureControls()
    let readerController = Unmanaged<UIViewController>.fromOpaque(readerPointer).takeUnretainedValue()
    let store = NativeStore(handler: handler, readerController: readerController)
    NativeRuntime.store = store
    let controller = UIHostingController(rootView: NativeShell(store: store))
    controller.view.backgroundColor = .systemBackground
    controller.view.tintColor = .label
    return Unmanaged.passRetained(controller).toOpaque()
}

@_cdecl("lz_ios_release_shell")
@MainActor
public func releaseNativeShell(_ pointer: UnsafeMutableRawPointer?) {
    if let pointer { Unmanaged<UIViewController>.fromOpaque(pointer).release() }
}

@_cdecl("lz_ios_resolve_request")
@MainActor
public func resolveNativeRequest(_ id: Int64, _ json: UnsafePointer<CChar>?) {
    if let json { NativeRuntime.store?.resolve(id, json: String(cString: json)) }
}

@_cdecl("lz_ios_publish_event")
@MainActor
public func publishNativeEvent(_ json: UnsafePointer<CChar>?) {
    if let json { NativeRuntime.store?.receive(String(cString: json)) }
}
