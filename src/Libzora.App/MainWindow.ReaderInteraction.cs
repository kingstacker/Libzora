using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Reader interactions that are independent of the native webview control:
/// layout injection, TOC/fragment navigation, in-page search, basic marks and
/// the small productivity tools that make the first reader surface usable.
/// </summary>
public partial class MainWindow
{
    private ReaderLayoutSettings _readerBaseLayout = NormalizeReaderLayoutForPlatform(new ReaderLayoutSettings());
    private ReaderLayoutSettings _readerLayout = BuildEffectiveProseLayout(NormalizeReaderLayoutForPlatform(new ReaderLayoutSettings()));
    private ReaderReadingStats? _readerBookReadingStats;
    private bool _readerVerticalDebugBoxesEnabled =
        Environment.GetEnvironmentVariable("LIBZORA_VERTICAL_DEBUG_BOXES") == "1";

    private bool ShouldShowReaderVerticalDebugBoxes() =>
        _readerLayout.VerticalWriting
        && _readerVerticalDebugBoxesEnabled;

    private void LoadReaderVerticalDebugBoxesSetting()
    {
        _readerVerticalDebugBoxesEnabled =
            _appSettings.ReaderVerticalDebugBoxesEnabled
            ?? Environment.GetEnvironmentVariable("LIBZORA_VERTICAL_DEBUG_BOXES") == "1";
    }

    private int _readerPageAnimation = ReaderAnimationFade;
    private readonly SemaphoreSlim _readerPageTurnGate = new(1, 1);
    private readonly SemaphoreSlim _readerTransitionGate = new(1, 1);
    private readonly SemaphoreSlim _readerLayoutGate = new(1, 1);
    private readonly SemaphoreSlim _readerBundledFontGate = new(1, 1);
    private CancellationTokenSource? _readerRelayoutCancellation;
    private IReaderHost? _readerPendingRelayoutHost;
    private ReaderScrollState? _readerPendingRelayoutState;
    private int _readerPendingKeyboardNavigation;
    private IReadOnlyList<EpubReaderNavigationItem> _readerTocItems = [];
    private ReaderProgressRow? _readerRestoredProgress;
    private ReaderSyncedPositionMarker? _readerActiveSyncedMarker;
    private double _readerSyncedMarkerScrollOrigin;
    private bool _readerSyncedMarkerScrollArmed;

    private static ReaderResumeMarker? ToResumeMarker(ReaderSyncedPositionMarker? marker) =>
        marker is null
            ? null
            : new ReaderResumeMarker(
                marker.ChapterIndex,
                marker.ScrollPosition,
                marker.Fragment,
                ReaderContentPosition.Validate(marker.ContentPosition));

    private void ClearActiveReaderSyncedMarker(bool clearPersisted = true)
    {
        var hadMarker = _readerActiveSyncedMarker is not null;
        _readerActiveSyncedMarker = null;
        _readerSyncedMarkerScrollArmed = false;
        (CurrentReaderHost as IReaderResumeMarkerHost)?.SetResumeMarker(null);
        (HiddenReaderHost as IReaderResumeMarkerHost)?.SetResumeMarker(null);
        if (hadMarker && clearPersisted && _readerBookFile is { } file)
        {
            _ = ObserveReaderTaskAsync(_readerData.ClearSyncedPositionMarkerAsync(file.Id, CancellationToken.None));
        }
    }
    private int _readerSearchSequence;
    private int _readerSearchCount;
    private int _readerSearchIndex = -1;
    private int? _readerPendingChunkOffset;
    private string? _readerPendingSearchQuery;
    private string? _readerPendingSearchContext;
    private int _readerBookmarkIndicatorSequence;
    private int _readerFootnoteHoverSequence;
    private bool _readerFootnotePollRunning;
    private int _readerFootnoteHoverMissCount;
    private string? _readerFootnoteHref;
    private Point? _readerFootnotePlacementPoint;
    private PixelPoint? _readerFootnoteAnchorScreenPoint;
    private DispatcherTimer? _readerFootnoteHoverTimer;
    private DispatcherTimer? _readerSelectionHighlightLongPressTimer;
    private const int ReaderSelectionToolTipDurationMs = 800;
    private const int ReaderSelectionHighlightLongPressMs = 320;
    private const double ReaderSelectionHighlightLongPressMaxDistance = 18;
    private Point _readerSelectionHighlightPressPoint;
    private bool _readerSelectionHighlightPressActive;
    private bool _readerSelectionHighlightLongPressTriggered;
    private bool _readerSelectionHighlightSuppressClick;
    private IDisposable? _readerSelectionHighlightMarkerBinding;
    private bool _readerLinuxTextFallbackUpdating;
    private bool _readerLinuxTextFallbackPointerPressed;
    private bool _readerLinuxTextFallbackSelectionAtPointerPress;
    private bool _readerLinuxTextFallbackSelectionDismissPress;
    private bool _readerLinuxVerticalFootnoteHandledRelease;
    private int _readerLinuxTextFallbackSelectionSyncSequence;
    private Point _readerLinuxTextFallbackPointerStart;
    private Vector _readerLinuxTextFallbackSelectionScrollOffset;
    private bool _readerLinuxTextFallbackSelectionScrollLocked;
    private bool _readerLinuxTextFallbackRestoringSelectionScroll;
    private bool _readerLinuxTextFallbackMoveToChapterEnd;
    private int _readerLinuxTextFallbackWheelDeltaRemainder;
    private int _readerLinuxTextFallbackContinuousWheelDirection;
    private long _readerLinuxTextFallbackContinuousWheelLastTick;
    private string? _readerLinuxTextFallbackTargetTitle;
    private string? _readerLinuxTextFallbackEndFragment;
    private string _readerLinuxTextFallbackText = string.Empty;
    private List<string> _readerLinuxTextFallbackPages = [];
    private List<ReaderLinuxTextFallbackPageItem> _readerLinuxTextFallbackPageItems = [];
    public ObservableCollection<ReaderLinuxTextFallbackBlock> ReaderLinuxTextFallbackBlocks { get; } = [];
    public ObservableCollection<ReaderLinuxTextFallbackImage> ReaderLinuxTextFallbackImages { get; } = [];
    private int _readerLinuxTextFallbackPageIndex;
    private int? _readerLinuxTextFallbackPendingReflowAnchor;
    private int _readerLinuxTextFallbackReflowSequence;
    private string? _readerPendingBookmarkQuote;
    private int? _readerPendingBookmarkPosition;
    private int _readerPendingBookmarkFlowMode;
    private ReaderContentPosition? _readerPendingBookmarkContentPosition;
    private string? _readerCurrentFragment;
    private ReaderAnnotation? _readerPendingAnnotation;
    private bool _suppressReaderTocSelectionNavigation;
    private readonly SemaphoreSlim _readerSearchMutationGate = new(1, 1);
    private string? _readerPendingSelection;
    private int _readerPendingSelectionStartOffset;
    private int _readerPendingSelectionEndOffset;
    private string _readerPendingSelectionPrefix = string.Empty;
    private string _readerPendingSelectionSuffix = string.Empty;
    private (double X, double Y)? _readerPendingPdfPoint;
    private double _readerScrollPosition;
    private double _readerScrollRatio;
    private double _readerScrollWidth;
    private double _readerScrollHeight;
    private double _readerClientWidth;
    private double _readerClientHeight;
    private int[] _readerBookPageCounts = [];
    private CancellationTokenSource? _readerBookPageCountCancellation;
    private int _readerBookPageCountSequence;
    private DateTimeOffset _readerSessionStarted;
    private WindowState _readerWindowStateBeforeZen = WindowState.Normal;
    private Avalonia.Styling.ControlTheme? _readerWindowDecorationsThemeBeforeZen;
    private bool _readerZenMode;
    // Set when the reader is closed straight from zen mode, so the next book
    // opened from the shelf starts in the same distraction-free layout.
    private bool _openReaderInZenMode;
    private bool _readerAssistantVisibleBeforeZen = true;
    private bool _readerTocExpandedBeforeZen = true;
    private bool _readerTocMinimalBeforeZen;
    private long _readerActiveSeconds;
    private long _readerListeningSeconds;
    private DispatcherTimer? _readerStatsTimer;
    private int _readerTransientStatusSequence;
    private bool _readerContinuousLocked;
    private int _readerContinuousDirection;
    private bool _readerLastNearTop;
    private bool _readerLastNearBottom;
    private bool _readerContinuousPositionInitialized;
    private double _readerPreviousScrollPosition;
    private DateTimeOffset _readerLastChapterChange = DateTimeOffset.MinValue;
    private bool _readerScrollPollRunning;
    private int _readerWheelDeltaRemainder;
    private int _readerContinuousSkipDepth;
    private int _readerProgressSaveSequence;
    private bool _readerProgressSliderUpdating;
    private bool _readerAiBusy;
    private bool _suppressAiProviderChange;
    private bool _suppressAiModelChange;
    private bool _suppressAiReasoningDepthChange;
    private string _readerAiReasoningDepth = "auto";
    private readonly List<AiConversationTurn> _readerAiConversation = [];
    private CancellationTokenSource? _readerAiCancellation;
    private AiConnectionSettings _readerAiSettings = new();
    private IReadOnlyList<string> _readerAiAvailableModels = [];
    private IReadOnlyList<PdfPageText> _readerPdfPages = [];
    private int _readerPdfPage = 1;
    private string? _readerPdfSourcePath;
    private bool _readerIsPdf;
    private ReaderAnnotation? _selectedReaderAnnotation;
    private IReadOnlyList<PdfOutlineItem> _readerPdfEmbeddedOutline = [];

    private sealed record ReaderScrollState(
        double Position,
        double Ratio,
        double ScrollWidth,
        double ScrollHeight,
        double ClientWidth,
        double ClientHeight);

    public sealed class ReaderLinuxTextFallbackImage : INotifyPropertyChanged, IDisposable
    {
        private double _maxWidth;
        private double _maxHeight;

        public ReaderLinuxTextFallbackImage(Bitmap source, double maxWidth, double maxHeight)
        {
            Source = source;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
        }

        public Bitmap Source { get; }

        public double MaxWidth
        {
            get => _maxWidth;
            private set
            {
                if (Math.Abs(_maxWidth - value) < 0.1) return;
                _maxWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxWidth)));
            }
        }

        public double MaxHeight
        {
            get => _maxHeight;
            private set
            {
                if (Math.Abs(_maxHeight - value) < 0.1) return;
                _maxHeight = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxHeight)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Resize(double maxWidth, double maxHeight)
        {
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
        }

        public void Dispose() => Source.Dispose();
    }

    public sealed class ReaderLinuxTextFallbackBlock : INotifyPropertyChanged
    {
        private double _width = double.NaN;

        public ReaderLinuxTextFallbackBlock(
            string text,
            int textOffset,
            double fontSize,
            double lineHeight,
            double maxWidth,
            bool isChapterTitle = false)
        {
            Text = text;
            TextOffset = textOffset;
            FontSize = fontSize;
            LineHeight = lineHeight;
            MaxWidth = maxWidth;
            IsChapterTitle = isChapterTitle;
        }

        public ReaderLinuxTextFallbackBlock(ReaderLinuxTextFallbackImage image)
        {
            Image = image;
        }

        internal ReaderLinuxTextFallbackBlock(
            string text,
            int textOffset,
            double fontSize,
            double lineHeight,
            double maxWidth,
            IReadOnlyList<ReaderLinuxTextFallbackFootnote> inlineFootnotes,
            bool isChapterTitle = false)
            : this(text, textOffset, fontSize, lineHeight, maxWidth, isChapterTitle)
        {
            InlineFootnotes = inlineFootnotes;
        }

        public ReaderLinuxTextFallbackBlock(string footnoteLabel, string footnoteHref)
        {
            FootnoteLabel = string.IsNullOrWhiteSpace(footnoteLabel) ? UiText.Get("注") : footnoteLabel.Trim();
            FootnoteHref = footnoteHref;
        }

        public string Text { get; } = string.Empty;
        public int TextOffset { get; }
        public double FontSize { get; }
        public double LineHeight { get; }
        public double MaxWidth { get; }
        public bool IsChapterTitle { get; }
        public FontWeight DisplayFontWeight =>
            IsChapterTitle ? FontWeight.Bold : FontWeight.Normal;
        public TextAlignment DisplayTextAlignment =>
            IsChapterTitle ? TextAlignment.Center : TextAlignment.Left;
        public Thickness DisplayMargin =>
            IsText
                ? IsChapterTitle
                    ? new Thickness(
                        0,
                        LineHeight,
                        0,
                        LineHeight)
                    : new Thickness(0)
                : new Thickness(0, LineHeight, 0, LineHeight);
        public ReaderLinuxTextFallbackImage? Image { get; }
        public string FootnoteLabel { get; } = string.Empty;
        public string FootnoteHref { get; } = string.Empty;
        internal IReadOnlyList<ReaderLinuxTextFallbackFootnote> InlineFootnotes { get; } = [];
        public IReadOnlyList<ReaderLinuxTextFallbackAnnotationRange> AnnotationRanges { get; private set; } = [];
        internal bool HasInlineFootnotes => InlineFootnotes.Count > 0;
        public bool IsText => Image is null && string.IsNullOrWhiteSpace(FootnoteHref);
        public bool IsImage => Image is not null;
        public bool IsFootnote => !string.IsNullOrWhiteSpace(FootnoteHref);

        /// <summary>
        /// Fixed layout width of the selectable paragraph. Wrapping must not
        /// depend on the control's content-derived DesiredSize: SelectableTextBlock
        /// re-measures when a selection starts or ends on a line boundary, and a
        /// content-derived width makes that re-measure re-wrap the paragraph.
        /// </summary>
        public double Width
        {
            get => _width;
            private set
            {
                if (double.IsNaN(_width) && double.IsNaN(value)) return;
                if (!double.IsNaN(_width) && !double.IsNaN(value) && Math.Abs(_width - value) < 0.1) return;
                _width = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
            }
        }

        public void ResizeText(double width) => Width = width;

        internal void SetAnnotationRanges(IReadOnlyList<ReaderLinuxTextFallbackAnnotationRange> ranges)
        {
            AnnotationRanges = ranges;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnnotationRanges)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record ReaderLinuxTextFallbackRawBlock(
        string? Text,
        string? ImagePath,
        string? FootnoteHref = null,
        string? FootnoteLabel = null,
        bool IsChapterTitle = false,
        bool IsParagraphBoundary = false);

    private sealed record ReaderLinuxTextFallbackExtractedContent(
        string Text,
        IReadOnlyList<ReaderLinuxTextFallbackRawBlock> Blocks);

    internal sealed record ReaderLinuxTextFallbackFootnote(
        string Label,
        string Href);

    public sealed record ReaderLinuxTextFallbackAnnotationRange(
        int Start,
        int Length,
        string Style,
        string Color);

    private sealed record ReaderLinuxTextFallbackPageItem(
        string Text,
        int TextOffset,
        ReaderLinuxTextFallbackImage? Image,
        IReadOnlyList<ReaderLinuxTextFallbackFootnote>? Footnotes = null,
        int ChapterTitleStart = -1,
        int ChapterTitleLength = 0,
        int PaginationOffset = 0,
        bool StartsParagraph = false)
    {
        public IReadOnlyList<ReaderLinuxTextFallbackFootnote> InlineFootnotes { get; } =
            Footnotes ?? [];

        public bool IsImage => Image is not null;
        public bool HasInlineFootnotes => InlineFootnotes.Count > 0;
        public bool HasChapterTitle =>
            ChapterTitleStart >= 0 && ChapterTitleLength > 0;
    }

    /// <summary>
    /// Vertical paginated reading runs the application-side page composer:
    /// the chapter is banked once and pages are composed on demand, instead
    /// of the whole chapter flowing as one 100k-element document.
    /// </summary>
    private bool IsReaderPageComposeMode =>
        !_readerIsPdf && _readerLayout.VerticalWriting && _readerLayout.FlowMode == 1;

    /// <summary>
    /// Resolves the character offset the composed page should open at. New
    /// page-mode saves persist the page-start character offset in
    /// ScrollPosition; legacy pixel saves fall back to the chapter start.
    /// </summary>
    private static ReaderLayoutSettings NormalizeReaderLayoutForPlatform(ReaderLayoutSettings settings)
    {
        // Vertical writing is a paginated layout on every platform, including
        // Linux WebKitGTK. The vertical page step, edge masks and glyph-phase
        // probes are calibrated per viewport by VerticalStepExpression, so
        // fonts, DPI and native side-panel resizing re-resolve the geometry
        // instead of being hard platform limitations.
        return ReaderLayoutDefaults.Normalize(settings);
    }

    public ObservableCollection<ReaderBookmark> ReaderBookmarks { get; } = [];
    public ObservableCollection<ReaderAnnotation> ReaderAnnotations { get; } = [];
    public ObservableCollection<ReaderSearchResultViewModel> ReaderSearchResults { get; } = [];
    public ObservableCollection<ReaderAiMessageViewModel> ReaderAiMessages { get; } = [];
    public ObservableCollection<ReaderAiSourceViewModel> ReaderAiSources { get; } = [];
    private async Task SaveCurrentReaderGlobalPreferencesAsync(
        CancellationToken cancellationToken)
    {
        await SaveCurrentReaderPreferencesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------
    // Reading stats: cumulative active reading time plus a progress
    // snapshot. Time only accrues while the window is active and the
    // reader pane is visible, so simply leaving the book open is not
    // counted as reading time. Mirrors the WinUI reference.
    // ------------------------------------------------------------------

    private void StartReaderStatsTimer()
    {
        if (_readerStatsTimer is null)
        {
            _readerStatsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _readerStatsTimer.Tick += ReaderStatsTimer_Tick;
        }
        _readerStatsTimer.Start();
    }

    private void StopReaderStatsTimer()
    {
        _readerStatsTimer?.Stop();
    }

    private void ReaderStatsTimer_Tick(object? sender, EventArgs e)
    {
        if (!ReaderRoot.IsVisible) return;
        var isListening = _readerTts.State == TtsPlaybackState.Playing;
        if (!isListening && !IsActive) return;
        _readerActiveSeconds++;
        if (isListening)
            _readerListeningSeconds++;
        if (_readerActiveSeconds % 30 == 0)
            _ = ObserveReadingTimeFlushAsync();
    }

    private async Task ObserveReadingTimeFlushAsync()
    {
        try { await FlushReaderActiveSecondsAsync(); }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("阅读时长保存失败，请检查存储空间或文件权限：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task FlushReaderActiveSecondsAsync()
    {
        var card = _readerBookCard;
        var file = _readerBookFile;
        if (card is null || file is null) return;
        var seconds = Interlocked.Exchange(ref _readerActiveSeconds, 0);
        var listeningSeconds = Math.Clamp(Interlocked.Exchange(ref _readerListeningSeconds, 0), 0, Math.Max(0, seconds));
        if (seconds <= 0)
        {
            await _readerReadingTimeRecovery.InitializeAsync();
            return;
        }
        await _readerReadingTimeRecovery.SaveAsync(card.Book.Id, file.Id, seconds,
            CalculateReaderProgressPercent(), _readerChapterIndex,
            _readerDocument?.Chapters.Count ?? (_readerIsPdf ? _readerPdfPages.Count : 0),
            listeningSeconds, CancellationToken.None);
        var currentSeconds = (_readerBookReadingStats?.CumulativeSeconds ?? 0) + seconds;
        var currentListening = (_readerBookReadingStats?.ListeningSeconds ?? 0) + listeningSeconds;
        _readerBookReadingStats = new ReaderReadingStats
        {
            BookId = card.Book.Id,
            BookFileId = file.Id,
            CumulativeSeconds = currentSeconds,
            ListeningSeconds = currentListening,
            ProgressPercent = CalculateReaderProgressPercent(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (_appSettings.ReaderProgressDisplay == ReaderProgressDisplayMode.BookTimeRemaining)
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
    }

    private void UpdateReaderZoomLabel()
    {
        if (ReaderZoomText is not null)
            ReaderZoomText.Text = _readerIsPdf && CurrentReaderHost is IPdfReadingHost pdf
                ? Math.Abs(pdf.Zoom - 1) < 0.005 ? pdf.FitMode == PdfReaderFitMode.Width ? T("适宽") : T("整页") : $"{pdf.Zoom:P0}"
                : IsReaderMangaMode && CurrentReaderHost is NativeReaderHost manga
                    ? $"{manga.MangaZoom:P0}"
                    : $"{_readerLayout.FontScale:P0}";
    }

    // Transient reader-header status: auto-clears after a short moment instead
    // of lingering forever. A sequence guard plus an exact-text check ensure an
    // older timer never wipes a newer or longer-lived message.
    private void ShowReaderTransientStatus(string message)
    {
        ReaderStatusText.Text = message;
        var sequence = ++_readerTransientStatusSequence;
        _ = Task.Delay(2500).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (sequence == _readerTransientStatusSequence
                    && string.Equals(ReaderStatusText.Text, message, StringComparison.Ordinal))
                {
                    ReaderStatusText.Text = string.Empty;
                }
            }),
            TaskScheduler.Default);
    }

    private void HandleReaderBridgeShortcut(string key, bool ctrlKey)
    {
        if (string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase))
        {
            HandleReaderEscapeShortcut();
            return;
        }
        if (string.Equals(key, "f11", StringComparison.OrdinalIgnoreCase))
        {
            ToggleReaderZenMode();
            return;
        }
        if (ctrlKey && string.Equals(key, "f", StringComparison.OrdinalIgnoreCase))
        {
            OpenReaderSearchShortcut();
            return;
        }
        if (ctrlKey
            && string.Equals(key, "b", StringComparison.OrdinalIgnoreCase)
            && !IsReaderTextInputFocused())
        {
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
        }
    }
}
