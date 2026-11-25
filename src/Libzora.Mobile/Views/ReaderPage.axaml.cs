using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Immersive reader. EPUB uses the shared self-drawn surface in paginated mode
/// and PDF the shared PDFium surface; both retain progress in the existing
/// reader database so desktop and mobile resume the same place. Touch input is
/// classified here: side taps and swipes turn pages, a center tap toggles the
/// chrome. The hosts stay unfocusable because a focused host swallows Android's
/// back key as a reader shortcut, leaving the page impossible to leave.
/// </summary>

public partial class ReaderPage : UserControl
{
    private const double SwipeDistance = 44;
    private const double TapSlop = 12;
    private const double SwipeDominance = 1.3;
    private const string PdfPageChapterPrefix = "pdf:page:";
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(450);

    private readonly Book _book;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TocEntry> _tocEntries = [];
    private readonly List<ReaderBookmark> _bookmarks = [];
    private readonly List<ReaderAnnotation> _annotations = [];
    private readonly ReaderProgressRow? _jump;
    private readonly Guid? _preferredFileId;
    private readonly DispatcherTimer _longPress = new() { Interval = LongPressDelay };
    private IReaderHost? _readerHost;
    private EpubReaderDocument? _epubDocument;
    private EpubProgressiveSession? _epubPreparationSession;
    private BookFile? _bookFile;
    private string? _epubPath;
    private int _chapterIndex;
    private int _pdfPage = 1;
    private int _pdfPageCount;
    private double _epubPosition;
    private double _overallProgress;
    private string _positionTitle = string.Empty;
    private string _positionPages = string.Empty;
    private ReaderLayoutSettings _layout;
    private DateTimeOffset _lastProgressSave = DateTimeOffset.MinValue;
    private readonly ReadingActivityClock _activityClock = new(DateTimeOffset.UtcNow);
    private readonly ListeningActivityClock _listeningClock = new();
    private bool _isReaderForeground = true;
    private long _unsavedSeconds;
    private long _unsavedListeningSeconds;

    public string BookTitle => _book.Title;
    public string BookAuthors => _book.Authors;
    public string? BookCoverPath => _book.CoverPath;
    private ReaderProgressRow? _lastSavedProgress;
    private ReaderSyncedPositionMarker? _activeSyncedMarker;
    private double _syncedMarkerScrollOrigin;

    private static ReaderResumeMarker? ToResumeMarker(ReaderSyncedPositionMarker? marker) =>
        marker is null
            ? null
            : new ReaderResumeMarker(
                marker.ChapterIndex,
                marker.ScrollPosition,
                marker.Fragment,
                ReaderContentPosition.Validate(marker.ContentPosition));

    private void ClearActiveSyncedMarker(bool clearPersisted = true)
    {
        var hadMarker = _activeSyncedMarker is not null;
        _activeSyncedMarker = null;
        (_readerHost as IReaderResumeMarkerHost)?.SetResumeMarker(null);
        if (hadMarker && clearPersisted && _bookFile is { } file)
        {
            _ = _modules.Reader.Data.ClearSyncedPositionMarkerAsync(file.Id, CancellationToken.None);
        }
    }
    private Point? _pressPoint;
    private Point _lastPanPoint;
    private bool _pressMoved;
    private bool _panning;
    private bool _mangaSettingsInitialized;
    private bool _selectionArmed;
    private bool _expectSelection;
    private ReaderSelection? _selection;
    private bool _tapConsumed;
    private bool _sliderActive;
    private bool _turning;
    private bool _started;
    private bool _disposed;
    // Set once the book is on screen. Before that (or after a failed open) the
    // position fields hold defaults, and saving them would overwrite the real
    // progress here and, through sync, on every other device.
    private bool _ready;
    private bool _isPdf;

    /// <summary>Design-time only.</summary>
    public ReaderPage()
    {
        _book = null!;
        _modules = null!;
        _shell = null!;
        _layout = null!;
        InitializeComponent();
    }

    /// <param name="jump">Opens at this location instead of the saved progress.</param>
    public ReaderPage(Book book, ApplicationModules modules, MobileShell shell, ReaderProgressRow? jump = null,
        Guid? preferredFileId = null)
    {
        _book = book;
        _jump = jump;
        _preferredFileId = preferredFileId;
        _modules = modules;
        _shell = shell;
        _layout = BuildLayout(shell.Preferences.Current);
        InitializeComponent();
        ReaderFooter.IsVisible = shell.Preferences.Current.ReaderProgressDisplay != MobileReaderProgressDisplay.Off;
        TitleText.Text = book.Title;
        LoadingTitleText.Text = book.Title;
        if (shell.Services.CreateTtsEngine is null || shell.Services.CreateTtsPlayer is null)
        {
            ListenButton.IsVisible = false;
            ReaderActions.ColumnDefinitions = ColumnDefinitions.Parse("*,*,*");
            Grid.SetColumn(AiButton, 1);
            Grid.SetColumn(TypographyButton, 2);
        }

        const RoutingStrategies tunnel = RoutingStrategies.Tunnel;
        ReaderHostSlot.AddHandler(PointerPressedEvent, OnSurfacePressed, tunnel, handledEventsToo: true);
        ReaderHostSlot.AddHandler(PointerMovedEvent, OnSurfaceMoved, tunnel, handledEventsToo: true);
        ReaderHostSlot.AddHandler(PointerReleasedEvent, OnSurfaceReleased, tunnel, handledEventsToo: true);
        ReaderHostSlot.AddHandler(PointerCaptureLostEvent, OnSurfaceCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        _longPress.Tick += OnLongPress;
        ProgressSlider.AddHandler(PointerPressedEvent, OnSliderPressed, tunnel, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnSliderReleased, tunnel, handledEventsToo: true);
        ProgressSliderGroup.SetHostVisible(false);
        ProgressSliderGroup.DragStarted += () => _sliderActive = true;
        ProgressSliderGroup.DragCompleted += canceled =>
        {
            _sliderActive = false;
            if (!canceled) _ = SeekAsync(ProgressSlider.Value);
        };
        ReaderHostSlot.SizeChanged += OnReaderSurfaceSizeChanged;
        Loaded += OnLoaded;
    }

    public bool IsChromeVisible => _usesNativeFrontend ? _nativeChromeVisible : TopChrome.Classes.Contains("visible");

    public string? PositionText => FooterPosition.Text;

    private bool ResolveViewportLandscape()
    {
        if (_usesNativeFrontend && _nativeViewportHint is { Width: > 0, Height: > 0 } hint)
            return hint.Width > hint.Height && hint.Width >= 760;
        var slot = ReaderHostSlot.Bounds;
        if (slot.Width > 0 && slot.Height > 0) return slot.Width > slot.Height;
        var page = Bounds;
        if (page.Width > 0 && page.Height > 0) return page.Width > page.Height;
        return false;
    }

    private void OnReaderSurfaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_disposed || !_started || !_ready || _isPdf) return;
        var desired = BuildLayoutForViewport(_shell.Preferences.Current, ResolveViewportLandscape());
        if (desired.FlowMode != _layout.FlowMode || desired.TwoPageMode != _layout.TwoPageMode)
        {
            _ = ApplyReaderSettingsAsync();
        }
    }

    private static ReaderLayoutSettings BuildLayout(MobilePreferences preferences) =>
        BuildLayoutForViewport(preferences, isLandscape: false);

    private static ReaderLayoutSettings BuildLayoutForViewport(MobilePreferences preferences, bool isLandscape)
    {
        var mangaMode = preferences.MangaMode;
        var verticalScrollManga = mangaMode && preferences.MangaDirection == (int)MobileMangaDirection.VerticalScroll;
        var verticalWriting = !mangaMode && preferences.ReaderVerticalWriting;
        var twoPageMode = !mangaMode && !verticalWriting && isLandscape && preferences.ReaderTwoPageInLandscape;
        var flowMode = mangaMode
            ? (verticalScrollManga ? 0 : 1)
            : (twoPageMode ? 1 : (preferences.ReaderFlowMode == 0 ? 0 : 1));
        var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
            FontScale: preferences.ReaderFontScale,
            LineHeight: preferences.ReaderLineHeight,
            MaxWidth: 900,
            BodyPadding: ReaderLayoutDefaults.DefaultBodyPadding,
            FontFamily: preferences.ReaderFontFamily,
            FlowMode: flowMode,
            VerticalWriting: verticalWriting,
            TwoPageMode: twoPageMode)
        {
            ParagraphSpacing = preferences.ReaderParagraphSpacing,
            MangaMode = mangaMode,
            MangaAutoCrop = mangaMode && preferences.MangaAutoCrop
        });
        // Applied after Normalize: a phone reads better with a narrower column
        // than the desktop's 24-point floor allows, and manga fills edge to edge.
        return normalized with
        {
            // Compact spacing makes paragraph lines follow the normal line pitch.
            ParagraphSpacingMatchesLineHeight = normalized.ParagraphSpacing <= 0,
            OverridePublisherLineHeight = true,
            OverridePublisherParagraphSpacing = true,
            NaturalLatinTypography = true,
            HyphenateEnglish = preferences.ReaderHyphenation,
            AvoidWidowsAndOrphans = preferences.ReaderAvoidWidowsAndOrphans,
            AlignLeft = preferences.ReaderAlignLeft,
            CjkLatinSpacing = preferences.ReaderCjkLatinSpacing,
            LatinFontFamily = string.IsNullOrWhiteSpace(preferences.ReaderLatinFontFamily)
                || string.Equals(preferences.ReaderLatinFontFamily, "auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : preferences.ReaderLatinFontFamily,
            ParagraphIndent = preferences.ReaderParagraphIndent,
            BodyPadding = mangaMode ? 0 : Math.Clamp(preferences.ReaderBodyPadding, 16, 64),
            // The footer shares the normal bottom paper margin. When hidden,
            // let text use that strip, retaining a small inset inside the safe area.
            BodyBottomPadding = mangaMode ? 0 : (preferences.ReaderProgressDisplay == MobileReaderProgressDisplay.Off ? 8 : null)
        };
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        _shell.ActualThemeVariantChanged += OnAppThemeVariantChanged;
        ApplyTheme();
        _shell.Services.SystemBars?.SetEdgeGesturesCaptured(true);
        await OpenAsync();
    }

    private async Task OpenAsync()
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            LoadingPanel.IsVisible = !_usesNativeFrontend;
            ErrorPanel.IsVisible = false;
            LoadingDetailText.Text = UiText.Get("正在读取书籍信息");

            if (_jump is { } jump && jump.BookId != _book.Id)
            {
                ShowError(() => UiText.Get("这条阅读位置不属于当前书籍。"));
                return;
            }
            _bookFile = ChooseReadableFile(_book, _jump?.BookFileId ?? _preferredFileId);
            if (_bookFile is null)
            {
                ShowError(() => UiText.Get("这本书没有可读取的 EPUB 或 PDF 文件。"));
                return;
            }

            var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile);
            // Rebuild the absolute-path index for downloaded/imported fonts in
            // parallel with reader data initialization. A damaged font manifest
            // must not block opening a book.
            var fontsTask = WarmFontIndexAsync(_lifetime.Token);
            await _shell.EnsureReaderDataAsync();
            await fontsTask;
            if (!File.Exists(path) && await TryDownloadAsync(_bookFile) is { } downloaded) path = downloaded;
            if (!File.Exists(path))
            {
                ShowError(() => UiText.Get("书籍文件不在本机，请重新导入，或在“我的”里开启同步后重试。"));
                return;
            }

            var bookmarksTask = _modules.Reader.Data.GetBookmarksAsync(_bookFile.Id, _lifetime.Token);
            var format = _bookFile.Format.Trim().TrimStart('.').ToLowerInvariant();
            if (format == "pdf")
            {
                await OpenPdfAsync(path, bookmarksTask, _lifetime.Token);
                return;
            }

            if (format is "epub" or "azw3" or "mobi" or "txt")
            {
                await OpenEpubAsync(path, format, bookmarksTask, _lifetime.Token);
                return;
            }

            ShowError(() => UiText.Get("暂不支持在安卓阅读 {0} 格式。", format.ToUpperInvariant()));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError(() => UiText.Localize(exception.Message));
        }
    }

    private async Task WarmFontIndexAsync(CancellationToken cancellationToken)
    {
        try { await _modules.Library.Fonts.ListAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { /* A damaged font manifest must not block opening a book. */ }
    }

    /// <summary>Synced books arrive as metadata; their files download when first opened.</summary>
    private async Task<string?> TryDownloadAsync(BookFile file)
    {
        var stored = await _modules.Sync.Service.LoadSettingsAsync(_lifetime.Token);
        if (!stored.Settings.Enabled || !stored.Settings.IsConfigured || !stored.Settings.HasChosenCustomProvider) return null;
        LoadingDetailText.Text = UiText.Get("正在从云端下载…");
        var progress = new Progress<string>(message => LoadingDetailText.Text = UiText.Localize(message));
        return await _modules.Sync.Service.EnsureBookFileDownloadedAsync(
            stored.DeviceId, stored.Settings, file, progress, _lifetime.Token);
    }
}
