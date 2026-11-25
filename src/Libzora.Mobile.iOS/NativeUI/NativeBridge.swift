import Combine
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
    /// 0 = Bundled KingHwaOldSong (default), 1 = System Default
    var uiFont = 0
    var themeMode = 0
    var listView = false
    var galleryMode = false
    var colorfulMode = false
    var showBookSyncStatus = false
    var showBookRating = false
    var stackBookSeries = true
    var collectionsView = false
    var pdfZoom = 1.0
    var pdfDisplayMode = 0
    var pdfFitMode = 0
    var pdfAutoCrop = false
    var mangaMode = false
    var mangaDirection = 0
    var mangaAutoCrop = false
    var mangaBlackBackground = false
    var sort = 0
    var readerTheme = 0
    var readerThemeFollowsAppearance = true
    var readerFontScale = 1.35
    var readerFontFamily = "京华老宋体"
    var readerLatinFontFamily = "auto"
    var readerLineHeight = 1.75
    var readerParagraphSpacing = 0.0
    var readerBodyPadding = 24.0
    var readerParagraphIndent = false
    var readerHyphenation = true
    var readerAlignLeft = false
    var readerCjkLatinSpacing = true
    var readerAvoidWidowsAndOrphans = true
    var readerVerticalWriting = false
    var readerFlowMode = 1
    var readerTwoPageInLandscape = true
    var readerPageAnimation = 3
    var readerProgressDisplay = 0
    /// 0 = Off, 1 = Follow OS Low Power Mode, 2 = Always On
    var readerPowerSavingMode = 1
    var readerHighlightStyle = "solid"
    var readerHighlightColor = "#000000"
    var onboardingAccepted = false

    func isPowerSaving(systemLowPower: Bool) -> Bool {
        switch readerPowerSavingMode {
        case 2: return true
        case 1: return systemLowPower
        default: return false
        }
    }

    var colorScheme: ColorScheme? {
        themeMode == 1 ? .light : themeMode == 2 ? .dark : nil
    }
    /// The page colours from `ReaderPalette`. SwiftUI fills the margins around
    /// the rendered page, so any difference shows as a band above and below it.
    func paper(for theme: Int, pureBlackOled: Bool = false) -> Color {
        switch theme {
        case 1: return Color(red: 244 / 255, green: 242 / 255, blue: 236 / 255)
        case 2: return Color(red: 238 / 255, green: 243 / 255, blue: 236 / 255)
        case 5: return pureBlackOled ? .black : Color(red: 14 / 255, green: 14 / 255, blue: 14 / 255)
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
    var chromeVisible = false
    var isPdf = false
    var title = ""
    var chapter = ""
    var position = ""
    var progress = 0.0
    var bookmark = false
    var error: String?
    var speechState = 0
    var speechStatus = ""
    var speechSegmentIndex = 0
    var speechSegmentCount = 0
    var speechSpeed = 1.0
    var speechSleepMinutesRemaining = 0
    var speechSleepAfterChapter = false
    var selectedText: String?
    var selectionBounds: NativeSelectionBounds?
    var footnoteReturnVisible = false
    var navigationRevision: Int64?
    var isManga = false
    var isMangaVerticalScroll = false
    var presentationFrameReady = false
    var presentationFrameRevision: Int64 = 0
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
    let reasoning: String?
    let status: String?
    let sources: [AiSource]?
    let enabled: Bool?
    let bookId: String?
    let tab: Int?
}

/// Reader state changes on page turns and speech progress. Only the reader
/// observes it, so the library tabs below are not re-rendered.
@MainActor
final class NativeReaderSession: ObservableObject {
    @Published var state = ReaderSnapshot()
    @Published var dialog: ReaderDialog?
    @Published var media: ReaderMedia?
    @Published var footnote: ReaderFootnote?
    @Published var searchBatch: ReaderSearchBatch?
    /// Delivers every progressive search batch synchronously on the main actor
    /// so back-to-back incremental deltas are never coalesced away.
    let searchBatches = PassthroughSubject<ReaderSearchBatch, Never>()
    private var aiChats: [String: NativeAiChatModel] = [:]
    var appearanceSessionId = ""
    /// Monotonic per reading session. SwiftUI may recreate the reader view
    /// (resetting @State) while the renderer keeps the last revision, so an
    /// older-looking counter would make the renderer ignore appearance changes.
    var appearanceRevision = 0
    var appearanceColorScheme: ColorScheme?

    func aiChat(for bookId: String) -> NativeAiChatModel {
        if let existing = aiChats[bookId] { return existing }
        let created = NativeAiChatModel(bookId: bookId)
        aiChats[bookId] = created
        return created
    }

    func receiveAiStream(
        sessionId: String,
        text: String,
        reasoning: String,
        status: String,
        sources: [AiSource]
    ) {
        for chat in aiChats.values where chat.activeStreamId == sessionId {
            chat.applyStreamUpdate(text: text, reasoning: reasoning, status: status, sources: sources)
        }
    }

    func reset() {
        state = ReaderSnapshot()
        dialog = nil
        media = nil
        footnote = nil
        searchBatch = nil
        for chat in aiChats.values { chat.cancelAndDispose() }
        aiChats.removeAll()
        appearanceSessionId = ""
        appearanceRevision = 0
        appearanceColorScheme = nil
    }
}

@MainActor
final class NativeStore: ObservableObject {
    let handler: ManagedRequest
    let readerController: UIViewController
    /// Only shown on an ordinary cold launch into the library (~1s), never on resume/reader-restore/widget deep-links.
    @Published var showsLaunchAnimation: Bool
    /// Shown during cold widget deep-links so the Libzora icon stays visible while the destination loads concurrently.
    @Published var widgetColdLaunch = false
    @Published var openingBookTransition: NativeBookOpenTransition?
    @Published var openingBookReadyId: UUID?
    @Published var readerEntrySnapshot: UIImage?
    var readerOpenAnimation: NativeBookOpenAnimation = .none
    var readerOpenSource: NativeBookOpenSource = .other
    @Published var closingBookTransition: NativeBookOpenTransition?
    @Published var closingBookBackdrop = false
    @Published var closingBookSnapshot: UIImage?
    var closingTransitionWaiters: [UUID: CheckedContinuation<Void, Never>] = [:]
    var activeTab = 0
    private(set) var viewportRect: CGRect = UIScreen.main.bounds
    var lastBookOpenOrigin: (bookId: String, sourceKey: String)?
    @Published var readerPresentationActive = false
    var widgetTabTransition: NativePageSlideTransition?
    var widgetTabTask: Task<Void, Never>?
    weak var rootViewController: UIViewController?
    var widgetBrandOverlay: UIView?
    var widgetEntrySnapshotCover: UIView?
    var widgetHandoffStartedNs: UInt64?
    var pendingWidgetReaderSnapshot: UIImage?
    @Published var closingReader = false
    var openingReaderBookId: String?
    var openingReaderGeneration: Int?
    var queuedReaderOpen = NativeReaderOpenQueue<NativeReaderOpenRequest>()
    var pendingOpenSource: NativeBookOpenSource = .other
    @Published var library: [LibraryBook] = [] {
        didSet { librarySeriesCache = nil }
    }
    var librarySeriesCache: [NativeBookSeries.ID: NativeBookSeries]?
    static let bookOpenOrderKey = "libzora.reader.bookOpenOrder"
    @Published var bookOpenOrder = UserDefaults.standard.dictionary(forKey: bookOpenOrderKey) as? [String: Int] ?? [:]
    @Published var folders: [LibraryFolder] = []
    @Published private(set) var preparingLibrary = true
    @Published private(set) var libraryLoaded = false
    @Published private(set) var libraryLoadError: String?
    /// Isolated from `NativeStore.objectWillChange` so scroll-driven palette
    /// updates only re-render ambient background layers, never the shelf grid.
    let ambient = NativeAmbientState()
    @Published var preferences = MobilePreferences() {
        didSet {
            applyUiLanguage(NativeLocalization.resolvedLanguage(preferences.uiLanguage))
            applyUiFont(preferences.uiFont)
        }
    }
    @Published private(set) var uiLanguage = NativeLocalization.language
    @Published private(set) var uiFont = NativeTheme.useSystemFont ? 1 : 0
    @Published var continueBookId: String?
    @Published var readerPresentation: NativeReaderPresentation?
    var readerBook: LibraryBook? {
        get { readerPresentation?.book }
        set {
            readerPresentation = newValue.map {
                NativeReaderPresentation(id: session.appearanceSessionId, book: $0, animation: readerOpenAnimation)
            }
        }
    }
    @Published var openingReader = false
    /// Hides the shelf until the launch restore either presents its book or
    /// proves there is nothing to restore, so the library never flashes first.
    @Published var restoringReader = false
    @Published var developerMode = false
    @Published var syncEntitlementsEnforced = false
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
    static let lastReaderKey = "libzora.reader.lastBookId"
    private static let coldLaunchBrandingDurationNs: UInt64 = 1_000_000_000
    var pendingReaderRestore: String?
    var pendingOpenBookId: String?
    /// Bumped whenever a widget link supersedes the reader that is opening.
    var readerOpenGeneration = 0
    private var nextId: Int64 = 0
    private var pending: [Int64: (Data) -> Void] = [:]
    private var toastTask: Task<Void, Never>?
    private var preferenceEdits = NativePreferenceEdits(MobilePreferences())
    private var preferenceRevision: Int { preferenceEdits.revision }
    private var preferenceWrites = 0
    private var preferenceSaveTask: Task<Void, Never>?
    private static let localizedCommands: Set<String> = [
        "settings.profile", "dashboard.get", "materials.get", "reader.toc", "reader.fonts"
    ]
    var libraryRefreshRevision = 0
    var appliedLibraryRevision = 0
    var pendingBookRequiredRevision = 0
    var pendingBookRefreshTask: Task<Void, Never>?
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
        if event.type.hasPrefix("reader"), event.sessionId != session.appearanceSessionId { return }
        switch event.type {
        case "reader": if let reader = event.reader, reader != session.state { session.state = reader }
        case "preferences": if let value = event.preferences { receivePreferences(value) }
        case "language":
            // An older save may still be returning while the user makes a
            // newer choice. Its locale must not undo the optimistic selection.
            if preferenceWrites == 0, let language = event.language { applyUiLanguage(language) }
        case "developer": developerMode = event.enabled ?? false
        case "syncEntitlements": syncEntitlementsEnforced = event.enabled ?? false
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
        case "readerSearch":
            if let search = event.search {
                session.searchBatch = search
                session.searchBatches.send(search)
            }
        case "txtImport": txtReview = event.txtImport
        case "dataBusy": dataBusy = event.busy ?? false; busyMessage = event.message ?? ""
        case "status": if event.channel == "sync" { syncStatus = event.message ?? "" }
        case "aiText":
            if let id = event.sessionId {
                session.receiveAiStream(
                    sessionId: id,
                    text: event.text ?? "",
                    reasoning: event.reasoning ?? "",
                    status: event.status ?? "",
                    sources: event.sources ?? [])
            }
        case "restart": restartRequired = true
        case "toast": showToast(event.message ?? "")
        case "accountInvalidated": account = nil
        case "openBook", "openWidgetBook":
            if let id = event.bookId {
                openRequestedBook(id, source: event.type == "openWidgetBook" ? .widget : .external)
            }
        case "openTab":
            if let tab = event.tab {
                let alreadyVisible = !preparingLibrary && libraryLoaded && activeTab == tab
                    && readerBook == nil && !readerPresentationActive && !closingReader && !openingReader
                guard !alreadyVisible else { return }
                let useBrandCover = NativeWidgetNavigationPolicy.shouldUseLaunchBrandCover(
                    preparingLibrary: preparingLibrary,
                    libraryLoaded: libraryLoaded,
                    restoringReader: restoringReader
                )
                pendingReaderRestore = nil
                pendingOpenBookId = nil
                if useBrandCover {
                    widgetTabTask?.cancel()
                    widgetTabTask = nil
                    beginWidgetBrandCover()
                    requestedTab = tab
                    restoringReader = false
                    dismissReaderForTab()
                } else {
                    widgetTabTask?.cancel()
                    widgetTabTask = Task { @MainActor [weak self] in
                        await self?.waitForWidgetForegroundCommit()
                        guard let self, !Task.isCancelled, self.pendingOpenBookId == nil else { return }
                        let stillVisible = !self.preparingLibrary && self.libraryLoaded && self.activeTab == tab
                            && self.readerBook == nil && !self.readerPresentationActive
                            && !self.closingReader && !self.openingReader
                        guard !stillVisible else { return }
                        self.beginWidgetTabSlideTransition()
                        self.requestedTab = tab
                        self.restoringReader = false
                        self.dismissReaderForTab()
                    }
                }
            }
        case "closeReader": if readerBook != nil { perform { await self.closeReader() } }
        default: break
        }
    }

    func frontBook(for series: NativeBookSeries) -> LibraryBook? {
        let mostRecentlyOpened = series.books.max { (bookOpenOrder[$0.id] ?? 0) < (bookOpenOrder[$1.id] ?? 0) }
        return mostRecentlyOpened.flatMap { bookOpenOrder[$0.id] == nil ? nil : $0 } ?? series.books.first
    }

    func booksInDisplayOrder(for series: NativeBookSeries) -> [LibraryBook] {
        series.booksByPromoting(frontBook(for: series)?.id)
    }

    func seriesSnapshot(for id: NativeBookSeries.ID) -> NativeBookSeries? {
        if librarySeriesCache == nil { librarySeriesCache = NativeBookSeries.groups(library) }
        return librarySeriesCache?[id]
    }

    /// The root owns startup, rather than waiting for a tab's onAppear. Cold
    /// launch plays a 1s branding animation concurrently while the shelf loads;
    /// restoring a reading session or opening a widget link skips it and
    /// enters the target view directly without flashing the library.
    func prepareLaunch(viewport: CGSize) async {
        guard !launchStarted else { return }
        launchStarted = true
        let brandingDelay: Task<Void, Never>? = showsLaunchAnimation
            ? Task { try? await Task.sleep(nanoseconds: Self.coldLaunchBrandingDurationNs) }
            : nil
        do {
            try await refreshLibrary(timeoutNanoseconds: 30_000_000_000)
            try Task.checkCancellation()
        } catch {
            if !libraryLoaded {
                libraryLoadError = Task.isCancelled ? L("书库加载已中断，请重试。") : error.localizedDescription
            }
        }
        let snapshotBooks = library
        let snapshotFolders = folders
        let snapshotPreferences = preferences
        let snapshotContinueBookId = continueBookId
        if libraryLoaded && !snapshotBooks.isEmpty && pendingOpenBookId == nil {
            Task {
                await NativeCoverCache.prepareLibrary(
                    books: snapshotBooks,
                    folders: snapshotFolders,
                    preferences: snapshotPreferences,
                    continueBookId: snapshotContinueBookId,
                    viewport: viewport)
            }
        }
        if showsLaunchAnimation && pendingOpenBookId == nil && requestedTab == nil && !Task.isCancelled {
            await brandingDelay?.value
        } else {
            brandingDelay?.cancel()
            showsLaunchAnimation = false
        }
        if pendingOpenBookId == nil && requestedTab == nil {
            dismissWidgetBrandCover()
        }
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
        if revision == preferenceRevision && preferenceWrites == 0 { receivePreferences(value.preferences) }
        appliedLibraryRevision = refreshRevision
        if continueBookId != value.continueBookId { continueBookId = value.continueBookId }
        libraryLoadError = nil
        if !libraryLoaded { libraryLoaded = true }
        guard !preparingLibrary else { return }
        openPendingReaderIfNeeded()
        restorePendingReaderIfNeeded()
    }

    private func applyUiLanguage(_ language: String) {
        NativeLocalization.setLanguage(language)
        let effective = NativeLocalization.language
        if uiLanguage != effective { uiLanguage = effective }
    }

    private func applyUiFont(_ font: Int) {
        let normalized = font == 1 ? 1 : 0
        if NativeTheme.useSystemFont != (normalized == 1) {
            NativeTheme.setUseSystemFont(normalized == 1)
        }
        if uiFont != normalized { uiFont = normalized }
    }

    private func receivePreferences(_ value: MobilePreferences) {
        do {
            try preferenceEdits.receive(value)
            if preferences != preferenceEdits.value { preferences = preferenceEdits.value }
        } catch { failure = error.localizedDescription }
    }

    /// A Binding setter updates the native control immediately. Persistence and
    /// reader reflow complete asynchronously; older replies cannot undo a tap.
    func setPreferences(_ value: MobilePreferences) {
        guard value != preferences else { return }
        let previous = preferences
        do { try preferenceEdits.select(value) }
        catch { failure = error.localizedDescription; return }
        let revision = preferenceRevision
        preferences = preferenceEdits.value
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
                // Send only user-edited fields; renderer-owned changes such as
                // PDF pinch zoom must not be overwritten by a stale native copy.
                let saved: MobilePreferences = try await self.request("preferences.save", ["preferences": self.preferenceEdits.changes])
                try self.preferenceEdits.saved(saved, revision: revision)
                self.preferences = self.preferenceEdits.value
                if self.preferenceRevision == revision {
                    // A second setting may have superseded the request that enabled badges.
                    if saved.showBookSyncStatus || previous.showBookSyncStatus {
                        do { try await self.refreshLibrary() }
                        catch { if !Task.isCancelled { self.failure = error.localizedDescription } }
                    }
                }
            } catch {
                self.preferenceEdits.failed(revision: revision)
                self.preferences = self.preferenceEdits.value
                if !Task.isCancelled { self.failure = error.localizedDescription }
            }
        }
    }

    func updateViewport(_ rect: CGRect) {
        guard rect.width > 120, rect.height > 160,
              rect.origin.x.isFinite, rect.origin.y.isFinite else { return }
        viewportRect = rect
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

@_cdecl("lz_ios_set_ui_font")
@MainActor
public func setNativeUiFont(_ font: Int32) {
    NativeTheme.setUseSystemFont(font == 1)
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
    store.rootViewController = controller
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
