# Code map

A file-level index for people and AI agents. Read this first, then open only
the two or three files a task actually needs. Design rationale lives in
[modular-architecture.md](modular-architecture.md); platform notes live in
[cross-platform.md](cross-platform.md) and [ios-native-frontend.md](ios-native-frontend.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Libzora.Core/` | Domain models and platform contracts. No UI, no I/O. |
| `src/Libzora.Layout/` | EPUB parsing (`IO/`), CSS hints (`MicroCss`), pagination and text composition. |
| `src/Libzora.Infrastructure/` | SQLite library, S3/WebDAV sync, backups, device scanning, networking. |
| `src/Libzora.Application/` | Application services and TTS coordination (`Libzora.ApplicationServices`). |
| `src/Libzora.Reader/` | Reader host adapters (`NativeReaderHost`, `NativePdfReaderHost`). |
| `src/Libzora.App/` | Avalonia desktop UI. `MainWindow` is split into partial files (below). |
| `src/Libzora.Desktop.{Windows,MacOS,Linux}/` | Desktop heads: composition root, platform wiring, packaging. |
| `src/Libzora.Mobile/` | Shared mobile logic used by every mobile head. |
| `src/Libzora.Mobile.iOS/` | SwiftUI native frontend plus the C# bridge (`NativeUI/`, `IOSNativeFrontend.*.cs`). |
| `src/Libzora.Mobile.Android/`, `src/Libzora.Mobile.Desktop/` | Other mobile heads. |
| `src/Libzora.Widgets/` | Home-screen / desktop widget rendering. |
| `src/Libzora.Platform.{Common,Windows,MacOS,Linux}/` | Platform services behind Core contracts. |
| `src/Libzora.AccountServer/`, `src/Libzora.McpServer/` | Account service and MCP server. |
| `src/Libzora.Tts.Sherpa/` | Local TTS engine. |
| `tests/` | xUnit suites, iOS logic/UI tests, script tests. |
| `docs/` | Design notes and operational guides. |
| `plugins/` | Sandboxed plugin sources shipped with the desktop app. |
| `scripts/`, `installer/`, `website/` | Build tooling, packaging, public site. |

Dependency direction: `Core → Layout → Infrastructure → Application → Reader → App → Desktop.*`.
`Application` must never reference Avalonia, `App`, or a desktop platform
assembly; a regression test enforces this.

## Desktop `MainWindow`

`MainWindow` is one partial class spread across files grouped by feature. Name
pattern: `MainWindow.<Area><Aspect>.cs`.

### Reader

| File | Responsibility |
| --- | --- |
| `MainWindow.Reader.cs`, `MainWindow.Reader.*.cs` | Reader window assembly, chapters, hosts and shared lifecycle. |
| `MainWindow.ReaderInteraction.cs` | Reader state, records, small shared helpers. |
| `MainWindow.ReaderScripts*.cs` | JavaScript injected into the reader document (core, vertical, debug). |
| `MainWindow.ReaderHostSetup.cs` | Host/surface configuration, bundled fonts. |
| `MainWindow.ReaderContentExtraction.cs` | Static XHTML walking, footnotes, plain text. |
| `MainWindow.ReaderLayout.cs` | Layout application, scroll state, diagnostics. |
| `MainWindow.ReaderTocNavigation*.cs` | TOC navigation, locations, annotations, in-page search. |
| `MainWindow.ReaderToc.cs`, `MainWindow.ReaderToc.*.cs` | TOC panel (layout, rows, compact mode, markers, scroll bar). |
| `MainWindow.ReaderPaging.cs` | Page turns, continuous scroll, transitions. |
| `MainWindow.ReaderOpenTransition.cs` | Cover lift + paper crossfade reader open/close transitions. |
| `MainWindow.ReaderPageStats.cs` | Page counts, position, progress. |
| `MainWindow.ReaderToolbar.cs` | Toolbar menus (flow, animation, bookmarks). |
| `MainWindow.ReaderZenMode.cs` | Zen mode and window chrome. |
| `MainWindow.ReaderSelectionPopup.cs` | Selection action bar. |
| `MainWindow.ReaderHighlightPalette.cs` | Annotation inline underline style, color swatch palette, and card converters. |
| `MainWindow.ReaderImageZoom.cs` | Double-click illustration lightbox and zoom/pan overlay. |
| `MainWindow.ReaderFootnotes.cs`, `ReaderAnnotationCardLayout.cs` | Footnote/annotation popovers and hover card layout math. |
| `MainWindow.ReaderBridge.cs` | WebView bridge message dispatch. |
| `MainWindow.ReaderFeatures*.cs` | Shortcuts, PDF navigation, tabs, bookmarks, whole-book search, progress slider and layout panel. |
| `MainWindow.ReaderManga.cs` | Desktop manga mode detection, per-book preferences, direction/spread/crop/black-canvas controls. |
| `MainWindow.ReaderAi*.cs` | Embedding models, assistant panel, provider settings, chat and context building. |
| `MainWindow.ReaderTts*.cs` | TTS navigation, controls, floating panel, model management and playback. |
| `MainWindow.KreaderValidation*.cs` | Debug-only Kreader validation and layout diagnostics. |
| `MainWindow.ReaderLinuxTextFallback*.cs`, `ReaderLinuxTextFallbackVerticalPage*.cs` | Linux plain-text fallback surface (render, paging, content, vertical page, input). |
| `MainWindow.ReaderNavigation.cs` | Return-to-library navigation and native menus. |

### Stage 3 (devices, dashboard, settings)

| File | Responsibility |
| --- | --- |
| `MainWindow.Stage3.cs` | Page switching, task spinners, toasts. |
| `MainWindow.Stage3Devices.cs` | Device discovery, connection state, caches. |
| `MainWindow.Stage3DeviceBooks.cs` | Device book list, selection, filtering, paging. |
| `MainWindow.Stage3DeviceTransfer*.cs` | Send books to Kindle over USB or email. |
| `MainWindow.Stage3DeviceExport.cs` | Export and delete device books. |
| `MainWindow.KindleAnnotationSync.cs` | Kindle USB highlights, notes and bookmarks sync UI. |
| `MainWindow.KindleFullSync.cs` | Unified Kindle USB two-way sync (books, bookmarks, highlights, notes). |
| `MainWindow.Stage3ReadingMaterials.cs` | Collected highlights and notes. |
| `MainWindow.Stage3DeviceResources.cs` | Device fonts and dictionaries. |
| `MainWindow.Stage3Dashboard.cs` | Reading dashboard, diagnostics, library trash. |
| `MainWindow.Stage3ReaderAiSettings.cs` | Reader AI settings. |
| `MainWindow.Stage3SettingsS3*.cs` | S3 settings, connection profiles, local change tracking, sync runs. |
| `MainWindow.Stage3SettingsApp.cs` | Language, Calibre detection, autosave. |
| `MainWindow.Stage3SettingsResources.cs` | Local font/dictionary management; About opens the built-in legal viewer. |
| `MainWindow.Stage3SettingsPersist.cs` | Settings read/save and data-root migration. |
| `MainWindow.Stage3Backup.cs` | Encrypted backup export/import. |
| `Stage3*ViewModels.cs`, `KindleBookCardViewModel.cs` | Device-side view models. |

### Library and window shell

| File | Responsibility |
| --- | --- |
| `MainWindow.axaml.cs` | Window construction, shared fields and constants. |
| `MainWindow.LibraryChrome.cs` | Icon, auto-hide scrollbars, chrome helpers. |
| `MainWindow.LibraryLifecycle.cs` | Initialisation, window events, cross-process DB watcher. |
| `MainWindow.LibraryView.cs` | Filters, UI refresh, book-card visual lifetime. |
| `MainWindow.LibrarySelection.cs` | Selection, detail-pane animation, refresh. |
| `MainWindow.LibraryImport.cs` | File/folder import, format selection, conflicts. |
| `MainWindow.LibraryActions.cs` | Drag and drop, open, delete, rename. |
| `MainWindow.BookContextMenu.cs`, `MainWindow.BookContextMenu.Series.cs` | Context menu construction for single books and stacked series. |
| `MainWindow.BookMutations.cs`, `MainWindow.BookMutations.Series.cs` | Favourite, reading status, collections, conversion and series batch mutations. |
| `MainWindow.LibraryDialogs.cs` | Confirmation, message and collection overlays. |
| `MainWindow.LibraryFiltering.cs` | Search, filters, view mode, pagination. |
| `MainWindow.LibrarySeries.cs`, `MainWindow.LibrarySeries.Scroll.cs` | Series stack expansion, horizontal drag/overscroll bounce, and front-volume promotion on the computer and device shelves. |
| `MainWindow.BookInteractions*.cs` | Card/grid pointer handling, rubber band, collection drag, detail actions. |
| `MainWindow.WindowChrome.cs` | Title bar, window-state animation, accessibility tooltips. |
| `MainWindow.Settings*.cs`, `MainWindow.Plugins.cs` | Settings page shell (expanders, search, persistence, Kindle email) and plugin UI. |
| `LibraryViewModel.cs`, `BookCardViewModel.cs`, `SeriesShelfProjection.cs`, `BulkObservableCollection.cs`, `BookCollectionFolderViewModel.cs` | Library view models and series shelf projection. |

### Reflection editor

`BookReflectionEditorSurface` is a partial class split by responsibility:

| File | Responsibility |
| --- | --- |
| `BookReflectionEditorSurface.cs` | State, markdown projection and loading. |
| `.Toolbar.cs` | Formatting toolbar and image action buttons. |
| `.Blocks.cs` | Block view construction. |
| `.Images.cs` | Image selection, resizing and replacement. |
| `.Editing.cs` | Text edits, keyboard handling, paste and block splitting. |
| `.BlockUpdate.cs` | Block refresh and selection toolbar state. |
| `.Formatting.cs` | Bold, links and citation insertion. |
| `.Dialogs.cs` | Owner window lookup and dialog chrome. |
| `.Media.cs` | Image insertion and link/alt prompts. |
| `.TextModel.cs` | Inline styles and markdown parsing. |
| `.Types.cs` | Nested view and block-state types. |

## Service layer partials

Large services are split by responsibility so one change opens one file:

| Service | Files |
| --- | --- |
| `S3SyncService.Database*.cs` | connection, snapshot, apply-remote (sync core), merge, dedupe, blobs, SQL, tombstones |
| `SqliteBookLibraryService.*.cs` | queries, import, mutations, trash, collections, file paths, readers, schema |
| `ReaderDataService.*.cs` | annotations, layout, stats, index, embeddings, search |
| `EpubReaderPreparationService.*.cs` | toc, navigation, chapter titles, duplicate cover normalization, index cache, extraction, XML, sanitizer, footnote-container detection |
| `PluginBridge.*.cs` | dispatch, content, AI, network, decode |
| `EpubTranslationService.*.cs` | translate, retry, render, documents, text tools, cache, types |
| `PinyinBookService.*.cs` | generate, annotate, AI review, documents, cache, types |
| `TtsService.*.cs` | startup, control, run, pipeline, lifecycle |
| `MtpKindleDeviceService.*.cs` | books, resources, annotations, scan, session |
| `LibzoraMcpTools.*.cs` + `McpResults.cs` | library, collections, content, devices, validation, projection, result records |
| `NativePdfReaderHost.*.cs` + `PdfReaderLayout.cs` | interaction, annotation callouts, search, rendering, pointer, layout, view, auto-crop, dark coloring |
| `NativeReaderHost.*.cs` | navigation, configuration, scrolling, annotations, search, content, page counts, layout, rendering, images, pointer, selection, capture |
| `XhtmlChapterLoader.*.cs` | flow, inline, blocks, footnotes, plain footnotes, external plain footnotes, plain figures, targets, links |
| `S3SyncService.*.cs` | settings, sync entry, versions, state, snapshots, cleanup, download, encryption |
| `WpdKindleAccess.*.cs` | devices, resources, annotations, books, shell items |
| `KindleDeviceService.*.cs` | detection, books, transfer, resources, annotations, files, native |
| `HorizontalComposer.*.cs` | line breaking, Knuth-Plass, images, tables, table layout |
| `GlyphShaper.cs` + `GlyphShaper.Variants.cs` | HarfBuzz shaping, system font loading, paired Latin/monospace and true Bold/Italic variants |
| `ReaderPage.*.cs` (Mobile) | navigation (request generations and exact chapter matching), position, input, selection, footnote/figure detours and durable body progress, chrome, lifecycle, native (actions, search), native presentation (revision-safe first drawn page) |
| `BookReflectionService.cs` + `BookReflectionFormatting.cs` | book-level reflection persistence, Markdown normalization, preview and citation helpers |
| `BookReflectionPage.axaml.cs` + `BookReflectionCitationSheet.axaml.cs` (Mobile) | mobile reflection editor, Markdown preview and annotation citation picker |
| `MobileShell.*.cs` | shell state, sheets, bars |
| `Libzora.Infrastructure/LegalDocuments.cs` | Offline legal text loaded from the bundled bilingual articles in `website/`. |
| `AppDataMigrationService.*.cs` | journal, journal I/O, payload, verification, types |
| `AppBackupService.*.cs` | export, snapshot/import, files |
| `TtsEnvironmentSetupService.*.cs` | edge-tts, install, python, process, types |
| `AiServices.cs` + `AiSettingsStore.cs` / `AiChatClient.cs` / `AiConversationModels.cs` | AI settings, conversation models and chat client |
| `MassStorageKindleDeviceService.*.cs` | detection, books, resources, annotations, device, files |
| `ComposerCore.cs` + `CellFactory*.cs` | layout core and cell factory |
| `ImageCropDetector.cs` + `EpubMangaDetector.cs` | bitmap border auto-crop and EPUB fixed/image manga detection |
| `MicroCss*.cs` | hints, borders, parsing, lengths |
| `VerticalComposer.*.cs` | text, glyphs, debug, cells, images |

## iOS native frontend

- `src/Libzora.Mobile.iOS/NativeUI/*.swift` — SwiftUI screens: bookshelf,
  library, reader (`NativeReader.swift`, `NativeReaderAssistant.swift`,
  `NativeBookReflection.swift`, `NativeBookReflectionPolicy.swift`,
  `NativeMarkdownParser.swift`, `NativeMarkdown.swift`,
  `NativeBookOpenTransition.swift`,
  `NativeBookOpenTransitionPolicy.swift`, `NativeSwipeDeletePolicy.swift`,
  `NativeLibraryBatchPolicy.swift`, `NativeLibraryBatchSelection.swift`,
  `NativeReaderHighlightPalette.swift`,
  `NativeReaderAdvancedTypography.swift`, `NativeReaderLatinFontPicker.swift`),
  settings, account, membership, theme, widgets.
- `NativeUI/NativeBridge.swift` — store, bridge requests/events, preferences and library refresh;
  `NativeBridge.Reader.swift` — session-identified reader presentations (including same-book reopens), restore, widget links, queued opens and teardown;
  `NativeReaderOpenPolicy.swift` — revision-safe latest-intent queue and active-open deduplication.
- `NativeReaderEntryPresentation.swift`, `NativeReaderPresentationPolicy.swift` — first-drawn-frame and viewport acknowledgement gating, stable loading cover and cancellation.
- `NativePageSlideTransition.swift`, `NativeDisplayFrame.swift` — native display commits and GPU-composited page snapshots; only library and reading-dashboard sessions use 3D book animations.
- `NativeBridge.Navigation.swift`, `NativeWidgetNavigationPolicy.swift` — fast widget tab handoffs without resetting the live tab/navigation trees.
- `Libzora.Reader/Native{Pdf,}ReaderHost.Presentation.cs` — optional Core presentation-frame capability, notifying only after the current page was drawn.
- `EpubReaderPreparationService.Auxiliary.cs` — marks footnote/endnote-only
  spine documents so they stay resolvable but never count as a page, a chapter
  position, or a page-turn destination.
- `src/Libzora.Mobile.iOS/IOSNativeFrontend.*.cs` — C# side of the bridge,
  split by area (`Library`, `Reader`, `Settings`, `Fonts`).
- `src/Libzora.Mobile.iOS/IOSMobileHost.cs` — host wiring.
- `src/Libzora.Mobile.iOS/IOSSecretProtector.cs`, `IOSSecretKeyMigration.cs` —
  device-only Keychain protection and verified migration of legacy sandbox keys.
- `src/Libzora.Mobile.iOS/NativeUI/NativePreferenceEdits.swift` — field-level
  optimistic preference writes, rollback baselines, widget-link and filter policies.
- `NativeUI/NativeLegalDocument.swift` — settings navigation to the bundled user agreement and privacy policy.
- `NativeLibraryChrome.swift`, `NativeLibraryScrollChromePolicy.swift`, `NativeShelfCards.swift`, `NativeBookSeriesShelf.swift` — bookshelf scroll chrome, interactive masthead/mask scroll policy, shared cards/navigation, and series panels split from the top-level library page.
- `NativeTrash.swift`, `NativeSwipeDeleteCard.swift`, `NativeDataSafetyPolicy.swift` — independent restore hit targets, acknowledged/confirmed deletion, and portable editing/onboarding policies.
- `NativeBookActions.swift`, `IOSNativeFrontend.BookFiles.cs`, `MobileBookFileExport.cs` — explicit reading statuses, exact/recent file selection, and byte-preserving temporary book exports.
- `NativeSyncSettings.swift`, `IOSNativeFrontend.Import.cs` — native sync form and serialized book/TXT imports, including optional rule previews.
- `NativeUI/NativeWelcomeView.swift`, `src/Libzora.Mobile/Views/WelcomeOverlay.axaml` — first-launch mobile welcome guide requiring explicit acceptance of the bundled User Agreement and Privacy Policy.
- `NativeUI/NativeAppleSignIn.swift` — native Sign in with Apple coordinator (SHA-256 nonce) and HIG button used by `NativeAccount.swift` for sign-in and Apple ID linking.
- `tests/Libzora.Mobile.iOS.UITests/` and `.LogicTests/` — behaviour contracts; `NativeDataSafetyUITests` / `prepare-ios-safety-preview.py` use an independent covered-book/PDF/TXT fixture bundle for deletion, onboarding, editing and file-action regressions.

## Where to look for common tasks

| Task | Start here |
| --- | --- |
| Reader UI or page-turn behaviour | `MainWindow.ReaderPaging.cs`, `MainWindow.ReaderInteraction.cs` |
| EPUB parsing or pagination | `src/Libzora.Layout/IO/`, `src/Libzora.Layout/Layout/` |
| Manga mode (EPUB detection, crop, Webtoon) | `EpubMangaDetector.cs`, `ImageCropDetector.cs`, `src/Libzora.Reader/NativeReaderHost*.cs`, `src/Libzora.Mobile.iOS/NativeUI/NativeReader.swift` |
| PDF viewing, zoom, auto-crop | `src/Libzora.Reader/NativePdfReaderHost*.cs`, `PdfReaderLayout.cs` |
| Book library list, grid, selection | `MainWindow.Library*.cs` |
| Series stacking and volume promotion | `src/Libzora.Core/BookSeriesGrouping.cs`, `src/Libzora.App/SeriesShelfProjection.cs`, `MainWindow.LibrarySeries.cs` |
| Book import pipeline | `MainWindow.LibraryImport.cs`, `src/Libzora.Application/` |
| Kindle device transfer | `MainWindow.Stage3Device*.cs`, `src/Libzora.Infrastructure/` |
| S3 / WebDAV / iCloud sync | `S3SyncService*.cs`, `MainWindow.Stage3SettingsS3*.cs`, `docs/icloud-sync.md` |
| Backup and encryption | `MainWindow.Stage3Backup.cs`, `src/Libzora.Infrastructure/` |
| SQLite schema or queries | `src/Libzora.Infrastructure/SqliteBookLibraryService*.cs` |
| iOS screen | `src/Libzora.Mobile.iOS/NativeUI/`, `IOSNativeFrontend.*.cs` |
| Widgets | `src/Libzora.Widgets/`, `NativeWidgets.swift` |
| Plugin API | `docs/plugins.md`, `src/Libzora.Core/` plugin contracts |

## Build and test

```bash
# Desktop (macOS example)
dotnet build src/Libzora.Desktop.MacOS/Libzora.Desktop.MacOS.csproj

# Fast UI-layer check while iterating
dotnet build src/Libzora.App/Libzora.App.csproj

# Core suites
dotnet test tests/Libzora.Tests/Libzora.Tests.csproj
dotnet test tests/Libzora.App.Tests/Libzora.App.Tests.csproj
```

- iOS is built with `scripts/` helpers or Xcode; see
  [ios-development.md](ios-development.md) and [ios-native-frontend.md](ios-native-frontend.md).
- General AI/maintainer rules are in `AGENTS.md`.

## Code organisation rules

1. Keep a source file under ~800 lines. When a `MainWindow` partial or service
   grows past that, split it along an existing feature boundary rather than
   adding a new `#region`.
2. Name new partials `MainWindow.<Area><Aspect>.cs` so the file list stays
   sortable by feature.
3. Put business orchestration in `Libzora.Application`, not in the window.
   The window should translate UI intent into service calls.
4. Put platform-specific behaviour behind a `Libzora.Core` interface and
   implement it in `Libzora.Platform.*`.
5. Every behavioural change needs a test in `tests/`; UI-layer changes should
   also be run in the simulator or desktop app.
