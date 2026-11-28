using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// The self-drawn EPUB reading surface. The chapter is loaded, shaped, broken,
/// paginated and painted by Libzora.Layout (HarfBuzz + Skia); this control only
/// blits the finished page into an Avalonia <see cref="WriteableBitmap"/> and
/// translates input into the same JSON bridge protocol the WebKit reader used,
/// so the surrounding reader pipeline keeps working unchanged.
/// </summary>

public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost, IReaderResumeMarkerHost, IReaderPresentationFrameHost
{
    public const string BundledFontFileName = "KingHwaOldSong-v3.0.ttf";

    /// <summary>
    /// Enables figure/table tap targets for shells that open their own viewer.
    /// Avalonia shells leave this off so a tap on an illustration keeps its
    /// existing page-turn/chrome behavior.
    /// </summary>
    public bool ReaderObjectsEnabled { get; set; }

    private const int ImageCacheCapacity = 24;
    private const int ImageDecodeMaxDimension = 2048;
    private static readonly object ImageCacheLock = new();
    private static readonly Dictionary<string, (SKImage Image, long UsedAt)> ImageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> ImagePins =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _imageCacheTick;

    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TypesetEngine? _engine;
    private readonly object _engineGate = new();
    private CancellationTokenSource? _relayoutCts;
    private bool _backgroundCompletionActive;
    internal Task RelayoutTask { get; private set; } = Task.CompletedTask;
    private bool _layoutCompletionScheduled;
    private long _layoutCompletionSequence;
    private long _layoutCompletionNavigationVersion;
    private ChapterContent? _layoutCompletionContent;
    private Task? _layoutCompletionTask;
    private Size? _initialViewportSize;
    private const int InitialComposeAheadCharacters = 8000;

    /// <summary>
    /// Supplies the host container size before Avalonia completes the first
    /// layout pass, avoiding a throwaway 1x1 compose and blocking full relayout.
    /// </summary>
    public void SetInitialViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 2 || height < 2)
        {
            return;
        }

        _initialViewportSize = new Size(width, height);
    }

    private (double Width, double Height) ResolveComposeViewportSize()
    {
        if (Bounds.Width >= 2 && Bounds.Height >= 2)
        {
            return (Math.Max(1, ComposePageWidth(Bounds.Width)), Bounds.Height);
        }

        if (_initialViewportSize is { Width: >= 2, Height: >= 2 } hint)
        {
            return (Math.Max(1, ComposePageWidth(hint.Width)), hint.Height);
        }

        return (Math.Max(1, ComposePageWidth(Bounds.Width)), Math.Max(1, Bounds.Height));
    }
    private ChapterContent? _content;
    private readonly List<TableTextRange> _tableTextRanges = new();
    private ChapterLayout? _layout;
    private string? _spreadCompanionChapterPath;
    private ChapterLayout? _spreadCompanionLayout;
    private string? _scrollNextChapterPath;
    private string? _scrollNextChapterLayoutPath;
    private ChapterLayout? _scrollNextChapterLayout;
    private bool _handoffScrollDown;
    private double? _handoffScrollOffset;
    private double? _handoffFromNextChapterDelta;
    private int _activeHandoffDirection;
    private TypesetLayoutOptions? _options;
    private ReaderLayoutSettings _settings = new();
    private ReaderAppearanceSettings _appearance = new();
    private ReaderPalette _palette = ReaderPalette.For(ReaderTheme.Classic);
    private TypesetPaintTheme _paintTheme = ReaderPalette.For(ReaderTheme.Classic).PaintTheme;
    private CancellationTokenSource? _navigationCts;
    private long _navigationVersion;
    private Task<bool> _navigationTask = Task.FromResult(true);
    private long _configurationVersion;
    private bool _disposed;

    private int _pageIndex;
    private bool _composePending;
    private bool _showVerticalDebugBoxes;
    private CancellationTokenSource? _imagePrefetchCancellation;
    private TypesetFontLibrary? _fontLibrary;
    private readonly object _fontLibraryGate = new();
    private DateTime _lastScrollEmitUtc;
    private DispatcherTimer? _scrollEmitTimer;
    private bool _bitmapDirty;
    private WriteableBitmap? _bitmap;
    private int _bitmapPixelWidth;
    private int _bitmapPixelHeight;
    private DispatcherTimer? _relayoutTimer;
    private ViewportReadingAnchor? _viewportReadingAnchor;
    private ViewportReadingState? _viewportReadingState;
    private bool _preserveViewportPosition;
    private DispatcherTimer? _selectionAutoPageTurnTimer;
    private bool _selectionAutoPageTurnArmed;
    private int _selectionAutoPageTurnDirection;

    /// <summary>How composed pages are presented in the viewport.</summary>
    private enum ReaderPresentation
    {
        Single,
        Spread,
        Scroll,
    }

    private ReaderPresentation _presentation = ReaderPresentation.Single;

    // The paragraph-indent flag is baked into the loaded blocks by the
    // chapter loader, so a runtime toggle must re-run the load, not just the
    // layout pass. Tracked to detect that change in Configure.
    private bool _loadedParagraphIndent = false;

    // Scroll-mode content offset in DIP from the chapter top. Composed pages
    // stack vertically and the viewport slices one or two of them.
    private double _scrollOffset;

    private static readonly TimeSpan SelectionAutoPageTurnDelay = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, int> _fragmentPagesByRequest = new(StringComparer.Ordinal);
    private string[] _navigationFragments = [];
    private bool _navigationFragmentsConfigured;
    private (ChapterLayout Layout, int Page, double ScrollOffset, string Fragment)? _navigationTarget;
    private int? _pendingRestoreOffset;
    private int? _pendingRestorePage;
    private double? _pendingRestoreRatio;
    private double? _pendingScrollOffset;
    private string? _pendingFragment;
    private bool _pendingSeekToEnd;
    private ViewportReadingAnchor? _pendingReadingAnchor;

    // Continuous-mode scrollbar overlay. A real ScrollBar visual child so
    // thumb drags and track clicks work; kept out of the layout otherwise.
    private readonly ScrollBar _scrollBar = new()
    {
        Orientation = Avalonia.Layout.Orientation.Vertical,
        Width = 12,
        Margin = new Thickness(0, 4, 2, 4),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        IsVisible = false,
    };
    private bool _suppressScrollBarSync;

    private readonly List<ReaderAnnotation> _annotations = new();
    private int _annotationsVersion;
    private Guid? _hoveredAnnotationId;
    private List<(ReaderAnnotation Annotation, IReadOnlyList<SKRect> Bands)>? _annotationHoverBands;
    private int _annotationHoverBandsPage = -1;
    private ChapterLayout? _annotationHoverBandsLayout;
    private int _annotationHoverBandsVersion = -1;
    private readonly Dictionary<Guid, (int Start, int End, string Signature)>
        _annotationRanges = new();
    private ChapterContent? _annotationRangesContent;
    private int _annotationRangesVersion = -1;
    private List<(int Start, int Length)>? _searchHits;
    private int? _focusSearchHit;
    private (int Start, int Length)? _speechHighlight;
    private ReaderResumeMarker? _resumeMarker;
    private ReaderTtsTextSnapshot? _speechTextSnapshot;
    private ChapterContent? _speechTextSnapshotContent;
    private ChapterLayout? _speechTextSnapshotLayout;

    // Selection state in body-text offsets.
    private bool _selecting;
    private bool _wordSelectionActive;
    private int _selectionAnchor = -1;
    private int _selectionActiveOffset = -1;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private bool _pointerDown;
    private bool _pointerDragStarted;
    private Point _pointerDownPosition;
    private Point _lastPointerPosition;
    private bool _hasLastPointerPosition;
    private int _pointerDownOffset = -1;
    private PlacedHotZone? _pointerHotZone;
    private string? _hoveredFootnoteHref;
    private IDisposable? _pendingPageClick;
    private string? _pendingPageClickSide;
    private int _pointerClickCount;
    private double _mangaZoom = 1.0;
    private Point _mangaPan;
    private bool _pureBlackBackground;
    private int _lastPrefetchedSlice = -1;
    private DateTimeOffset _lastImageTapAt = DateTimeOffset.MinValue;
    private Point _lastImageTapPosition;
    private string? _loadedFootnoteFragment;

    public double MangaZoom => _mangaZoom;
    public bool PureBlackBackground => _pureBlackBackground;

    public NativeReaderHost()
    {
        Focusable = true;
        ClipToBounds = true;
        ScrollBarAutoHide.SetIsEnabled(_scrollBar, true);
        ReadyTask = _readyTcs.Task;
        _readyTcs.TrySetResult();
        VisualChildren.Add(_scrollBar);
        _scrollBar.Scroll += (_, args) =>
        {
            if (_suppressScrollBarSync) return;
            _suppressScrollBarSync = true;
            try
            {
                SetScrollOffset(args.NewValue);
            }
            finally
            {
                _suppressScrollBarSync = false;
            }
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty)
            {
                ScheduleRelayout();
            }
        };
        PointerCaptureLost += (_, _) =>
        {
            if (!_pointerDown)
            {
                return;
            }

            _pointerDown = false;
            _pointerDragStarted = false;
            _selecting = false;
            _hasLastPointerPosition = false;
            StopSelectionAutoPageTurn();
        };
    }

    // ---- IReaderHost -----------------------------------------------------

    public object View => this;

    public Uri? Source { get; private set; }

    public Task ReadyTask { get; }

    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;

    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;

    /// <summary>Raised after a partial opening layout has been completed in
    /// the background, so labels can switch from text position to real pages.</summary>
    public event EventHandler? LayoutCompleted;

    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    public Task<string?> InvokeScriptAsync(string script)
    {
        // The native surface has no script engine. Leftover calls from shared
        // pipelines (reveal scripts and similar no-ops) are swallowed here;
        // everything meaningful reaches the host through its native methods.
        return Task.FromResult<string?>(null);
    }

    public void Stop()
    {
        CancelPendingPageClick();
        ++_configurationVersion;
        _relayoutCts?.Cancel();
        _navigationCts?.Cancel();
        _previousChapterPreviewCts?.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        _previousChapterPreviewCts?.Cancel();
        _scrollPreviousChapterPreview = null;
        _scrollNextChapterPreview = null;
        CancelPendingPageClick();
        _relayoutCts?.Cancel();
        _scrollEmitTimer?.Stop();
        _scrollEmitTimer = null;
        _imagePrefetchCancellation?.Cancel();
        _imagePrefetchCancellation?.Dispose();
        _imagePrefetchCancellation = null;
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        _relayoutTimer?.Stop();
        _viewportReadingAnchor = null;
        _viewportReadingState = null;
        _navigationTarget = null;
        _preserveViewportPosition = false;
        StopSelectionAutoPageTurn();
        lock (_engineGate)
        {
            _engine?.Dispose();
            _engine = null;
        }
        lock (_fontLibraryGate)
        {
            _fontLibrary?.Dispose();
            _fontLibrary = null;
        }
        _layout = null;
        _content = null;
        _tableTextRanges.Clear();
        _speechHighlight = null;
        InvalidateSpeechTextSnapshot();
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
