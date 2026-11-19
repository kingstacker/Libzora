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
    private readonly DispatcherTimer _longPress = new() { Interval = LongPressDelay };
    private IReaderHost? _readerHost;
    private EpubReaderDocument? _epubDocument;
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
    private long _unsavedSeconds;
    private ReaderProgressRow? _lastSavedProgress;
    private Point? _pressPoint;
    private bool _pressMoved;
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
    public ReaderPage(Book book, ApplicationModules modules, MobileShell shell, ReaderProgressRow? jump = null)
    {
        _book = book;
        _jump = jump;
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
        Loaded += OnLoaded;
    }

    public bool IsChromeVisible => _usesNativeFrontend ? _nativeChromeVisible : TopChrome.Classes.Contains("visible");

    public string? PositionText => FooterPosition.Text;

    private static ReaderLayoutSettings BuildLayout(MobilePreferences preferences)
    {
        var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
            FontScale: preferences.ReaderFontScale,
            LineHeight: preferences.ReaderLineHeight,
            MaxWidth: 900,
            BodyPadding: ReaderLayoutDefaults.DefaultBodyPadding,
            FontFamily: preferences.ReaderFontFamily,
            FlowMode: 1,
            VerticalWriting: preferences.ReaderVerticalWriting,
            TwoPageMode: false) { ParagraphSpacing = preferences.ReaderParagraphSpacing });
        // Applied after Normalize: a phone reads better with a narrower column
        // than the desktop's 24-point floor allows.
        return normalized with
        {
            // Compact spacing makes paragraph lines follow the normal line pitch.
            ParagraphSpacingMatchesLineHeight = normalized.ParagraphSpacing <= 0,
            OverridePublisherLineHeight = true,
            OverridePublisherParagraphSpacing = true,
            NaturalLatinTypography = true,
            HyphenateEnglish = preferences.ReaderHyphenation,
            AvoidWidowsAndOrphans = false,
            ParagraphIndent = preferences.ReaderParagraphIndent,
            BodyPadding = Math.Clamp(preferences.ReaderBodyPadding, 16, 64),
            // The footer shares the normal bottom paper margin. When hidden,
            // let text use that strip, retaining a small inset inside the safe area.
            BodyBottomPadding = preferences.ReaderProgressDisplay == MobileReaderProgressDisplay.Off ? 8 : null
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
            _bookFile = ChooseReadableFile(_book, _jump?.BookFileId);
            if (_bookFile is null)
            {
                ShowError(() => UiText.Get("这本书没有可读取的 EPUB 或 PDF 文件。"));
                return;
            }

            var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile);
            await _shell.EnsureReaderDataAsync();
            // Rebuild the absolute-path index for downloaded/imported fonts.
            // The app container path changes across iOS updates, so the
            // persisted preference stores only the catalog key.
            try { await _modules.Library.Fonts.ListAsync(_lifetime.Token); }
            catch (OperationCanceledException) { throw; }
            catch { /* A damaged font manifest must not block opening a book. */ }
            if (!File.Exists(path) && await TryDownloadAsync(_bookFile) is { } downloaded) path = downloaded;
            if (!File.Exists(path))
            {
                ShowError(() => UiText.Get("书籍文件不在本机，请重新导入，或在“我的”里开启同步后重试。"));
                return;
            }

            _bookmarks.AddRange(await _modules.Reader.Data.GetBookmarksAsync(_bookFile.Id, _lifetime.Token));
            var format = _bookFile.Format.Trim().TrimStart('.').ToLowerInvariant();
            if (format == "pdf")
            {
                await OpenPdfAsync(path, _lifetime.Token);
                return;
            }

            if (format is "epub" or "azw3" or "mobi")
            {
                await OpenEpubAsync(path, format, _lifetime.Token);
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
