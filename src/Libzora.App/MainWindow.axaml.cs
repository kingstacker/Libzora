using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Infrastructure.Plugins;

namespace Libzora;

/// <summary>
/// Window construction, shared fields and constants.
/// </summary>
public partial class MainWindow : Window
{
    private byte[]? _pendingDetailCover;
    private Bitmap? _pendingDetailCoverPreview;

    private const string MaximizeGlyphData = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z";
    private const string SidebarChevronDownData = "M 4,6 L 8,10 L 12,6";
    private const string SidebarChevronRightData = "M 6,4 L 10,8 L 6,12";
    private const string LibraryGridGlyphData = "M 3,3 H 9 V 9 H 3 Z M 15,3 H 21 V 9 H 15 Z M 3,15 H 9 V 21 H 3 Z M 15,15 H 21 V 21 H 15 Z";
    private const string LibraryListGlyphData = "M 4,6 H 6 M 10,6 H 20 M 4,12 H 6 M 10,12 H 20 M 4,18 H 6 M 10,18 H 20";
    private const string LibraryCollectionsGlyphData = "M 3,7 H 9 L 11,9 H 21 V 20 H 3 Z";
    private const double LibraryDetailWidth = 320;
    private const double BookGridSlotWidth = 166;
    private const int PerFileImportFormatSelectionLimit = 1_000;
    private const double RubberBandDragThreshold = 8;
    private const double CollectionDragThreshold = 8;
    // Gallery mode trims the card to its cover, so the wrap-panel slot shrinks
    // with it (cover 214 + card margin 12) instead of leaving a blank strip.
    private double BookGridSlotHeight => _appSettings.GridGalleryDisplay ? 226 : 304;
    private const string InstantMenuHoverClass = "instantMenuHover";
    private const int LibraryDetailSlideDurationMs = 520;
    private const int WindowStateAnimationDurationMs = 160;
    // Supersedes in-flight detail-pane animations whenever a newer show/hide
    // command arrives, so a close that was interrupted by a re-select can
    // never collapse a freshly re-opened pane.
    private int _detailPaneAnimationVersion;
    private DispatcherTimer? _dropOverlayHideTimer;
    private CancellationTokenSource? _librarySearchDebounce;
    private BookCollectionFolderViewModel? _pendingCollectionDragFolder;
    private Control? _pendingCollectionDragControl;
    private IPointer? _pendingCollectionDragPointer;
    private Point _pendingCollectionDragStartPoint;
    private bool _collectionDragActive;
    private bool _suppressCollectionTap;
    private BookCollectionFolderViewModel? _collectionDragTargetFolder;
    private Control? _collectionDragTargetControl;
    private CancellationTokenSource? _detailPaneAnimationCancellation;
    private CancellationTokenSource? _windowStateAnimationCancellation;
    private bool _windowMinimizeAnimationInProgress;
    private int _windowStateAnimationVersion;
    private FileSystemWatcher? _databaseChangeWatcher;
    private DispatcherTimer? _crossProcessDataRefreshTimer;
    private DateTime _lastInProcessDataChangeUtc = DateTime.MinValue;
    private bool _crossProcessRefreshBusy;
    private bool _crossProcessRefreshNeedsSync;
    private readonly SemaphoreSlim _bookMetadataSaveGate = new(1, 1);

    private readonly ApplicationModules _modules;
    private readonly AppPaths _paths;
    private readonly PluginManager _pluginManager;
    private readonly string _rootConfigurationDirectory;
    private readonly IBookLibraryService _library;
    private readonly IBookFormatConverter _formatConverter;
    private readonly ReaderFormatCacheService _readerFormatCache;
    private readonly IKindleDeviceService? _kindle;
    private IDeviceChangeNotifier? _deviceChangeNotifier;
    private readonly DeviceModelStore _deviceModelStore;
    private readonly KindleDeviceAuxiliaryCacheStore _kindleAuxiliaryCacheStore;
    private readonly ISecretProtector _secretProtector;
    private readonly AppBackupService _backupService;
    private readonly PlatformDiagnosticsService _platformDiagnostics;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly FontLibraryService _fontLibrary;
    private readonly DictionaryService _dictionaryService;
    private readonly ReaderDataService _readerData;
    private readonly ReaderProgressRecoveryService _readerProgressRecovery;
    private readonly ReaderReadingTimeRecoveryService _readerReadingTimeRecovery;
    private readonly EpubBookContentService _bookContent;
    private readonly KindleAnnotationSyncService _kindleAnnotationSync;
    private readonly EmbeddingModelDownloadService _embeddingModelDownloader;
    private readonly G2PWModelDownloadService _g2pwModelDownloader;
    private readonly LocalEmbeddingService _embeddingService;
    private readonly ReaderEmbeddingIndexService _readerEmbeddingIndex;
    private readonly IReaderRetriever _readerRetriever;
    private readonly ReaderAiContextBuilder _readerAiContextBuilder;
    private readonly EpubFootnoteResolver _footnotes;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatClient _aiChatClient;
    private readonly ReaderWebSearchService _readerWebSearch;
    private readonly TranslationService _translationService;
    private readonly IEpubTranslationService _epubTranslationService;
    private readonly QueryRewriteService _queryRewriteService;
    private readonly TtsSettingsStore _ttsSettingsStore;
    private readonly TtsCacheManager _ttsCacheManager;
    private readonly TtsService _readerTts;
    private readonly EpubReaderPreparationService _epubReader;
    private readonly IReaderHostFactory _readerHostFactory;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly KindleEmailSender _kindleEmailSender;
    private readonly ISyncService _s3SyncService;
    // Refreshing the library rebuilds BookCardViewModel instances. Keep the
    // last per-book result so a transient refresh failure cannot turn every
    // existing card back into the default NotSynced state.
    private readonly ConcurrentDictionary<Guid, BookSyncStatus> _bookSyncStatusCache = new();
    private long _bookSyncStatusRefreshVersion;
    private int _bookSyncStatusRefreshBusy;
    private int _collectionSearchVersion;
    private CancellationTokenSource? _collectionSearchCancellation;
    private readonly UpdateService? _updateService;
    private AppSettings _appSettings = new();
    private KindleEmailSettings _kindleEmailSettings = new();
    private S3SyncStoredSettings _s3SyncStoredSettings = new(
        Guid.NewGuid().ToString("N"),
        new S3SyncSettings());
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _embeddingModelDownloadCancellation;
    private Task? _embeddingModelDownloadTask;
    private bool _embeddingModelDownloadBusy;
    private string? _deviceDisplayName;
    private Button? _activeNavigationSectionButton;
    private IReaderHost? _readerActiveHost;
    private IReaderHost? _readerPreloadHost;
    private bool _filterControlsReady;
    private bool _updatingFilterControls;
    private bool _updatingDetails;
    private LibraryViewMode _libraryViewMode = LibraryViewMode.Grid;
    private LibraryViewMode _lastNonCollectionLibraryViewMode = LibraryViewMode.Grid;
    private VirtualizingWrapPanel? _bookGridPanel;
    private BookCardViewModel? _selectedCard;
    private BookCardViewModel? _multiSelectAnchor;
    private readonly HashSet<Guid> _selectedBookIds = [];
    private readonly HashSet<BookCardViewModel> _visibleBookCards = [];
    private readonly Dictionary<Control, BookCardViewModel> _bookCardControls = [];
    private readonly HashSet<BookCardViewModel> _pdfCoverRequests = [];
    private TaskCompletionSource<bool>? _confirmationCompletion;
    private TaskCompletionSource<BookRemovalChoice>? _bookRemovalCompletion;
    private TaskCompletionSource<DeviceDuplicateChoice>? _deviceDuplicateCompletion;
    private enum DeviceDuplicateChoice { Cancel, Skip, SendAll }
    private enum BookRemovalChoice { Cancel, MoveToTrash }
    private TaskCompletionSource<string?>? _collectionNameCompletion;
    private TaskCompletionSource<bool>? _messageCompletion;
    private bool _conversionInProgress;
    private bool _automaticReaderFormatGenerationInProgress;
    private CancellationTokenSource? _conversionCancellation;
    private TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>? _importFormatSelectionCompletion;
    private TaskCompletionSource<ImportConflictResolution>? _importConflictCompletion;
    private readonly List<(string FilePath, ToggleSwitch Toggle)> _importFormatSelectionRows = [];
    private bool _rubberBandSelecting;
    private bool _rubberBandPointerSequenceHandled;
    private Point _rubberBandStart;
    private Point _rubberBandStartContent;
    private Point _rubberBandCurrent;
    private Point _rubberBandPointerPosition;
    private bool _rubberBandPressedOnCard;
    private bool _rubberBandGestureActive;
    private readonly DispatcherTimer _rubberBandAutoScrollTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private double _rubberBandAutoScrollDelta;
    private ScrollViewer? _bookGridScrollViewer;
    private readonly DispatcherTimer _bookDetailsDelayTimer = new();
    private BookCardViewModel? _pendingBookDetailsCard;
    private BookCardViewModel? _pressedBookCard;
    private Guid? _detailPaneBookId;

    public MainWindow()
        : this(DesktopApplicationBootstrap.Create())
    {
    }

    public MainWindow(
        AppPaths paths,
        IBookLibraryService library,
        IBookFormatConverter? formatConverter = null,
        AppServices? services = null,
        AppSettings? startupSettings = null)
        : this(DesktopApplicationBootstrap.Create(
            paths, library, formatConverter, services, startupSettings), services)
    {
    }

    /// <summary>Transfers ownership of a per-shell service graph to this window.</summary>
    public MainWindow(ApplicationModules modules, AppServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ObjectDisposedException.ThrowIf(modules.IsDisposed, modules);
        _modules = modules;
        _paths = modules.Paths;
        _pluginManager = modules.Plugins;
        // Clear the previous process's task records as the window starts, even
        // if the user does not open the task-history flyout during this run.
        _ = BackgroundTasks;
        _rootConfigurationDirectory = services?.RootConfigurationDirectory ?? AppContext.BaseDirectory;
        _library = modules.Library.Books;
        _appSettings = modules.StartupSettings;
        _formatConverter = modules.Library.FormatConverter;
        _readerFormatCache = modules.Reader.FormatCache;
        _kindle = services?.KindleDeviceService;
        _deviceModelStore = modules.Integrations.DeviceModels;
        _kindleAuxiliaryCacheStore = modules.Integrations.DeviceCache;
        _secretProtector = modules.SecretProtector;
        _backupService = modules.Library.Backups;
        _platformDiagnostics = modules.Integrations.Diagnostics;
        _appSettingsStore = modules.Library.Settings;
        _fontLibrary = modules.Library.Fonts;
        _dictionaryService = modules.Library.Dictionary;
        _readerData = modules.Reader.Data;
        _readerProgressRecovery = modules.Reader.ProgressRecovery;
        _readerReadingTimeRecovery = modules.Reader.ReadingTimeRecovery;
        _bookContent = modules.Reader.Content;
        _kindleAnnotationSync = new KindleAnnotationSyncService(
            _paths,
            _library,
            _readerData,
            _bookContent);
        _embeddingModelDownloader = modules.Ai.EmbeddingModels;
        _g2pwModelDownloader = modules.Ai.PinyinModels;
        _embeddingService = modules.Ai.Embedding;
        _readerEmbeddingIndex = modules.Ai.EmbeddingIndex;
        _readerRetriever = modules.Ai.Retriever;
        _readerAiContextBuilder = modules.Ai.Context;
        _footnotes = modules.Reader.Footnotes;
        _aiSettingsStore = modules.Ai.Settings;
        _aiChatClient = modules.Ai.ChatClient;
        _readerWebSearch = modules.Ai.WebSearch;
        _translationService = modules.Ai.Translation;
        _epubTranslationService = modules.Ai.EpubTranslation;
        _queryRewriteService = modules.Ai.QueryRewrite;
        _ttsSettingsStore = modules.Reader.TtsSettings;
        _ttsCacheManager = modules.Reader.TtsCache;
        _readerTts = new TtsService(
            services?.TtsEngine,
            _ttsCacheManager,
            services?.TtsAudioPlayer,
            GetCurrentReaderTtsDocument,
            AdvanceReaderForTtsAsync,
            services?.TtsEnvironmentSetup,
            GetNextReaderTtsDocumentAsync);
        _readerTts.StateChanged += ReaderTts_StateChanged;
        _readerTts.EnvironmentChanged += ReaderTts_EnvironmentChanged;
        _epubReader = modules.Reader.Preparation;
        _readerHostFactory = services?.FormatReaderHostFactory ?? new DesktopReaderHostFactory(services?.PdfDocumentFactory);
        _kindleWebFileInput = services?.KindleWebFileInput;
        _kindleEmailSettingsStore = modules.Integrations.KindleEmailSettings;
        _kindleEmailSender = modules.Integrations.KindleEmail;
        _s3SyncService = modules.Sync.Service;
        _s3SyncService.RemoteSettingsApplied += S3RemoteSettingsApplied;
        _updateService = modules.Integrations.Updates;
        ViewModel = new LibraryViewModel(_library, _paths.Data);
        ViewModel.SortMode = ParseLibrarySortMode(_appSettings.LibrarySortMode);
        ViewModel.ViewChanged += ViewModel_ViewChanged;

        InitializeComponent();
        ConfigureMacWindowChrome();
        PromoteModalOverlays();
        InitializeReaderNavigation();
        InitializeKindleEmailClipboardSupport();
        ApplyMainAppearance();
        InitializeWidgetSnapshots();
        InitializeReaderAppearance();
        InitializeReaderToolbarAutoHide();
        InitializeReaderSelectionToolTips();
        // Keep the reader's nested menu on the same hover branch and chevron
        // state as the book-library context menu.
        if (ReaderMoreButton.Flyout is MenuFlyout readerMoreMenu)
            AttachInstantMenuHover(readerMoreMenu);
        InitializeReaderEmbeddingModelSelectors();
        InitializeReaderAiTurnNavigation();
        // AcceptsReturn lets the TextBox handle Enter before a normal bubbled
        // handler can see it. Observe the tunneling phase, including an event
        // already marked handled, so Ctrl+Enter always reaches the AI sender
        // while plain Enter remains a newline.
        ReaderAiQuestionBox.AddHandler(
            InputElement.KeyDownEvent,
            ReaderAiQuestionBox_KeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _library.DataChanged += LocalLibraryDataChanged;
        _readerData.DataChanged += LocalReaderDataChanged;
        InitializeS3SyncIndicator();
        _readerAiThinkingAnimationTimer.Tick += (_, _) => TickReaderAiThinkingAnimation();
        if (modules.HasStartupSettings && !_appSettings.OnboardingCompleted)
        {
            // Choose the first-run surface before the window is presented. The
            // async library/database initialization can continue behind it.
            LibraryRoot.IsVisible = false;
            ShowOnboardingIfNeeded();
        }
        UiText.LanguageChanged += MainWindowLanguageChanged;
        ApplyApplicationIcon();
        InitializeTrayIcon();
        // Linux file managers can mark external drag events handled on the
        // first child under the pointer. Observe the bubbled event even then,
        // at the top-level window that owns the native XDND target.
        AddHandler(
            DragDrop.DragEnterEvent,
            LibraryPane_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            DragDrop.DragOverEvent,
            LibraryPane_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            DragDrop.DragLeaveEvent,
            LibraryPane_DragLeave,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            DragDrop.DropEvent,
            LibraryPane_Drop,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // The device resource page contains a ListBox/ScrollViewer which may
        // handle drag events before they reach the page. Register here with
        // handledEventsToo so a resource dropped on any part of that page still
        // reaches the Kindle transfer path. The XAML attached handlers were
        // removed to avoid running the Drop handler twice.
        DeviceResourcePage.AddHandler(
            DragDrop.DragEnterEvent,
            DeviceResourcePage_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceResourcePage.AddHandler(
            DragDrop.DragOverEvent,
            DeviceResourcePage_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceResourcePage.AddHandler(
            DragDrop.DropEvent,
            DeviceResourcePage_Drop,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // The local dictionary list contains interactive children (ListBox and
        // TextBox), so listen after handled events to make the whole section a
        // drop target instead of only its unused whitespace.
        DictionaryManagementSection.AddHandler(
            DragDrop.DragEnterEvent,
            DictionaryManagementSection_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DictionaryManagementSection.AddHandler(
            DragDrop.DragOverEvent,
            DictionaryManagementSection_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DictionaryManagementSection.AddHandler(
            DragDrop.DropEvent,
            DictionaryManagementSection_Drop,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // SelectableTextBlock handles PointerPressed while beginning a text
        // selection. Listen after handled events so the reader can freeze its
        // scroll offset before Avalonia brings the selection into view.
        ReaderLinuxTextFallbackOverlay.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderLinuxTextFallback_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        ReaderLinuxTextFallbackOverlay.AddHandler(
            InputElement.PointerReleasedEvent,
            ReaderLinuxTextFallback_PointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        ReaderRoot.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderRoot_SelectionDismissPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        // The vertical fallback page manages its own drag selection and
        // footnote taps; surface them through the shared reader popups.
        ReaderLinuxTextFallbackPageVertical.SelectionCommitted += (_, _) =>
            SyncLinuxReaderTextFallbackSelectionState();
        ReaderLinuxTextFallbackPageVertical.FootnoteActivated += (_, payload) =>
        {
            _readerLinuxVerticalFootnoteHandledRelease = true;
            _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(
                payload.Href,
                isFootnote: true,
                ReaderLinuxTextFallbackPageVertical.TranslatePoint(
                    payload.Position,
                    ReaderWebViewHost)));
        };
        ReaderLinuxTextFallbackPageVertical.FootnoteDismissed += (_, _) =>
            HideReaderFootnotePopup();
        BookGrid.ItemsPanel = new FuncTemplate<Panel?>(() =>
        {
            _bookGridPanel = new VirtualizingWrapPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                ItemWidth = BookGridSlotWidth,
                ItemHeight = BookGridSlotHeight,
                CacheLength = 0.5
            };
            return _bookGridPanel;
        });
        BookGrid.SizeChanged += (_, _) => UpdateBookGridLayout();
        BookGrid.LayoutUpdated += (_, _) => UpdateBookGridLayout();
        LibraryContentHost.SizeChanged += (_, _) => UpdateBookGridLayout();
        LibraryWorkspace.SizeChanged += (_, _) => UpdateBookGridLayout();
        SizeChanged += (_, _) => UpdateBookGridLayout();
        _rubberBandAutoScrollTimer.Tick += (_, _) => TickRubberBandAutoScroll();
        _bookDetailsDelayTimer.Tick += (_, _) => CompletePendingBookDetails();
        LibraryRoot.AddHandler(
            InputElement.PointerPressedEvent,
            LibraryRoot_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(KeyDownEvent, MainWindow_GlobalKeyDown, RoutingStrategies.Tunnel);
        // ListBoxItem and its ScrollViewer handle pointer movement/release while
        // dragging. Listen after handled events as well, otherwise a drag that
        // crosses cards can lose the rubber-band update in one direction.
        BookGrid.AddHandler(
            InputElement.PointerPressedEvent,
            BookGrid_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        BookGrid.AddHandler(
            InputElement.PointerMovedEvent,
            BookGrid_PointerMoved,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        BookGrid.AddHandler(
            InputElement.PointerReleasedEvent,
            BookGrid_PointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        BookGrid.AddHandler(
            InputElement.PointerCaptureLostEvent,
            BookGrid_PointerCaptureLost,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceBookGridScroll.AddHandler(
            InputElement.PointerMovedEvent,
            DeviceBookGrid_PointerMoved,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceBookGridScroll.AddHandler(
            InputElement.PointerReleasedEvent,
            DeviceBookGrid_PointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceBookGridScroll.AddHandler(
            InputElement.PointerCaptureLostEvent,
            DeviceBookGrid_PointerCaptureLost,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _deviceRubberBandAutoScrollTimer.Tick += (_, _) => TickDeviceRubberBandAutoScroll();
        DataContext = this;
        Closed += MainWindow_Closed;
        Closing += MainWindow_Closing;
        UpdateMaximizeGlyph();
        UpdateWindowShadowMargin();
        SetSidebarActive(AllBooksButton);
        SetLibraryViewMode(ParseLibraryViewMode(_appSettings.LibraryViewMode), persist: false);
        InitializeDeviceShelfControls();
        UpdateLibraryUi();
        ConfigureStage3Timer();
        Opened += (_, _) => InitializeDeviceChangeNotifier(services?.CreateDeviceChangeNotifier);
        SetEjectButtonsEnabled(false);
        Opened += (_, _) => EnsureInteractiveControlToolTips();
        Opened += (_, _) => InitializePlugins();
        Opened += async (_, _) => await RefreshAccountSummaryAsync();
        Opened += (_, _) => StartDeviceChecks();
        BookGrid.TemplateApplied += (_, _) => AttachBookGridAutoHideScrollbar();
        BookList.TemplateApplied += (_, _) => AttachBookListAutoHideScrollbar();
        Dispatcher.UIThread.Post(UpdateBookGridLayout, DispatcherPriority.Loaded);
        AttachSettingsAutoHideScrollbar();
        AttachBookGridAutoHideScrollbar();
        AttachBookListAutoHideScrollbar();
        AttachReadingDashboardAutoHideScrollbars();
    }
}
