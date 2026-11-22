using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>Virtualized PDF pages, rasterized from the original PDF at device resolution.</summary>
public sealed partial class NativePdfReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IPdfZoomHost, IPdfAnnotationsHost, IPdfSearchHost, IPdfThumbnailHost, IPdfAnnotationVisualHost, IReaderKeyboardHost, IReaderVisualTransitionHost, IReaderSelectionHost, IReaderResumeMarkerHost
{
    private sealed class CachedPage(PdfPageContent content) : IDisposable
    {
        public PdfPageContent Content { get; } = content;
        private PdfTextSelectionLayout? _selectionLayout;
        public PdfTextSelectionLayout SelectionLayout => _selectionLayout ??= new(Content);
        private IReadOnlyList<PdfFootnoteReference>? _footnotes;
        public IReadOnlyList<PdfFootnoteReference> Footnotes => _footnotes ??= PdfFootnoteLayout.Read(Content);
        public PdfRasterRegion? Raster { get; set; }
        public WriteableBitmap? Bitmap { get; set; }
        public ReaderPalette? Palette { get; set; }
        /// <summary>Full-page low-resolution fallback painted under high-DPI tiles so panning never flashes white.</summary>
        public WriteableBitmap? PreviewBitmap { get; set; }
        public ReaderPalette? PreviewPalette { get; set; }
        public int PreviewRotation { get; set; }
        /// <summary>True once the crop was probed, including pages that need no crop.</summary>
        public bool CropChecked { get; set; }
        public void Dispose()
        {
            Bitmap?.Dispose();
            PreviewBitmap?.Dispose();
        }
    }

    private readonly object _documentGate = new();
    private readonly IPdfDocumentFactory _pdfDocuments;
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _resizeTimer;
    private readonly Dictionary<int, CachedPage> _pages = [];
    private IPdfDocument? _document;
    private string? _documentPath;
    private PdfDocumentInfo? _documentInfo;
    private bool _documentReady;
    private CancellationTokenSource? _documentCancellation;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _viewportCancellation;
    private Task _renderTask = Task.CompletedTask;
    private Size[] _sizes = [];
    private Rect?[] _cropBoxes = [];
    private PdfReaderLayout? _layout;
    private double _layoutDpi = 1;
    private ReaderTheme _appearanceTheme = ReaderTheme.Classic;
    private ReaderPalette _palette = ReaderPalette.For(ReaderTheme.Classic);
    private IReadOnlyList<ReaderAnnotation> _annotations = [];
    private int _version;
    private int _rasterVersion;
    private int _layoutRequest;
    private bool _disposed;
    private bool _syncingBars;
    private bool _allowZoomOverpan;
    private Vector _pan;
    private Point? _pointerStart;
    private int _anchorOffset = -1;
    private int _selectionPage = 1;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _dragging;
    private Guid? _pointerAnnotationHit;
    private int _pointerClickCount;
    private readonly DispatcherTimer _selectionDashTimer;
    private double _selectionDashOffset;
    private Guid? _hoveredAnnotation;
    private readonly List<(int Start, int Length)> _search = [];
    private int _searchPage = 1;
    private int _searchIndex = -1;
    private string _searchQuery = string.Empty;
    private (int Page, int Start, int Length)? _speechHighlight;
    private bool _pointAnnotationMode;
    private bool _handToolMode;
    private bool _isPanning;
    private Point _handDragStart;
    private Vector _handDragOrigin;
    private (Point Anchor, Rect Bounds)? _annotationCallout;
    private ReaderResumeMarker? _resumeMarker;

    public NativePdfReaderHost() : this(new PdfiumDocumentFactory()) { }

    public NativePdfReaderHost(IPdfDocumentFactory pdfDocuments)
    {
        _pdfDocuments = pdfDocuments ?? throw new ArgumentNullException(nameof(pdfDocuments));
        Focusable = true;
        DetachedFromVisualTree += (_, _) => CloseFootnotePreview();
        ClipToBounds = true;
        ScrollBarAutoHide.SetIsEnabled(_vertical, true);
        ScrollBarAutoHide.SetIsEnabled(_horizontal, true);
        VisualChildren.Add(_vertical);
        VisualChildren.Add(_horizontal);
        _vertical.ValueChanged += (_, _) =>
        {
            if (!_syncingBars)
                SetPan(new(_pan.X, _vertical.Value), allowZoomOverpan: HasZoomOverpan(new(_pan.X, _vertical.Value)));
        };
        _horizontal.ValueChanged += (_, _) =>
        {
            if (!_syncingBars)
                SetPan(new(_horizontal.Value, _pan.Y), allowZoomOverpan: HasZoomOverpan(new(_horizontal.Value, _pan.Y)));
        };
        // Marching ants for the picked highlight/underline: the outline dashes
        // walk while an annotation stays selected.
        _selectionDashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _selectionDashTimer.Tick += (_, _) =>
        {
            if (_selectedAnnotationId is null)
            {
                _selectionDashTimer.Stop();
                return;
            }
            _selectionDashOffset = (_selectionDashOffset + 1.6) % 14;
            InvalidateVisual();
        };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _resizeTimer.Tick += async (_, _) =>
        {
            _resizeTimer.Stop();
            RebuildLayout(preservePosition: true);
            await RefreshViewportAsync();
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property != BoundsProperty || _disposed || _sizes.Length == 0) return;
            RebuildLayout(preservePosition: true);
            ScheduleRender();
        };
        PointerCaptureLost += (_, _) =>
        {
            _pointerStart = null;
            _dragging = false;
            _isPanning = false;
            _pointerAnnotationHit = null;
            _pointerLink = null;
        };
    }

    public object View => this;
    public Uri? Source { get; private set; }
    public Task ReadyTask => Task.CompletedTask;
    public Task RenderingTask => _renderTask;
    public int PageNumber { get; private set; } = 1;
    public int PageCount { get; private set; }
    public double Zoom { get; private set; } = 1;
    public PdfReaderDisplayMode DisplayMode { get; private set; } = PdfReaderDisplayMode.Continuous;
    public PdfReaderFitMode FitMode { get; private set; } = PdfReaderFitMode.Width;
    public int Rotation { get; private set; }
    public bool AutoCropMargins { get; private set; }
    /// <summary>Power-saving mode caps render DPI at 2x, skips adjacent prefetch, and uses true OLED black in Ink mode.</summary>
    public bool PowerSavingMode { get; private set; }
    public bool IsPointAnnotationMode => _pointAnnotationMode;
    public bool IsHandToolMode => _handToolMode;

    /// <summary>True when horizontal panning is already at the requested edge (-1 left, +1 right).</summary>
    public bool IsAtHorizontalEdge(int direction)
    {
        if (_layout is null || direction == 0) return true;
        var maxPanX = Math.Max(0, _layout.Extent.Width - Viewport.Width);
        if (maxPanX <= 1) return true;
        return direction < 0 ? _pan.X <= 2 : _pan.X >= maxPanX - 2;
    }
    public string? LastError { get; private set; }
    public PdfPageContent? PageContent => GetPageContent(PageNumber);
    public double VisibleTop => GetVisibleTop(new Rect(Viewport));
    // ScrollToTop aligns destinations inside this margin. Sample the same
    // edge when following the outline, including after zooming or rotating.
    public double NavigationTop => GetVisibleTop(new Rect(Viewport).Deflate(PdfReaderLayout.Margin));
    public double NavigationBottom => Math.Clamp(PageBounds.Intersect(new Rect(Viewport))
        .TransformToAABB(GetPageTransform(PageBounds).Invert()).Bottom / Math.Max(1, GetUnrotatedPageSize(PageBounds).Height), 0, 1);
    public Rect PageBounds => GetPageBounds(PageNumber);
    public bool CanGoPrevious => GetAdjacentPage(-1) != PageNumber;
    public bool CanGoNext => GetAdjacentPage(1) != PageNumber;
    internal int CachedPageCount => _pages.Count;
    internal long CachedRasterBytes => _pages.Values.Sum(page => (long)(page.Raster?.Width ?? 0) * (page.Raster?.Height ?? 0) * 4);
    public IReadOnlyList<int> VisiblePageNumbers => _layout?.VisiblePages(DocumentViewport).ToArray() ?? [];
    private double DeviceScaling
    {
        get
        {
            var raw = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return PowerSavingMode ? Math.Min(2.0, raw) : raw;
        }
    }
    private Size Viewport => new(Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
    private Rect DocumentViewport => new(_pan.X, _pan.Y, Viewport.Width, Viewport.Height);

    private double GetVisibleTop(Rect viewport) => Math.Clamp(PageBounds.Intersect(viewport)
        .TransformToAABB(GetPageTransform(PageBounds).Invert()).Top / Math.Max(1, GetUnrotatedPageSize(PageBounds).Height), 0, 1);

    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>(null);
    public void Navigate(Uri uri) => _ = NavigateAsync(uri);

    public async Task<int> PrepareDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (_documentReady && _documentCancellation is { IsCancellationRequested: false }
            && string.Equals(_documentPath, path, StringComparison.Ordinal)) return PageCount;
        Stop();
        _documentCancellation?.Dispose();
        _documentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _documentCancellation.Token;
        var version = ++_version;
        var count = await Task.Run(() =>
        {
            lock (_documentGate)
            {
                token.ThrowIfCancellationRequested();
                _document?.Dispose();
                _document = null;
                _documentPath = null;
                _documentInfo = null;
                _document = _pdfDocuments.Open(path);
                _documentPath = path;
                // Parse the outline here, on the same thread that opened the
                // document: PDFium calls are serialised process-wide, so doing
                // it later queues the rail behind the first page raster and the
                // table of contents only appears once that page is done.
                _documentInfo = _document.ReadInfo(token);
                return _document.PageCount;
            }
        }, token);
        token.ThrowIfCancellationRequested();
        if (_disposed || version != _version) throw new OperationCanceledException(token);
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
        PageCount = count;
        PageNumber = 1;
        Source = null;
        _backHistory.Clear();
        _forwardHistory.Clear();
        PageNumberOffset = null;
        _pointAnnotationMode = false;
        _handToolMode = false;
        _isPanning = false;
        _sizes = Enumerable.Repeat(new Size(595, 842), count).ToArray();
        _cropBoxes = new Rect?[count];
        _layout = null;
        _pan = default;
        _annotations = [];
        // Publish readiness only after the page count and caches belong to
        // this load. Stop/cancellation must not leave a reusable half-session.
        _documentReady = true;
        return count;
    }

    public async Task<bool> NavigateAsync(Uri uri, CancellationToken cancellationToken = default, bool recordHistory = true,
        bool preserveVerticalPosition = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var starting = new ReaderNavigationStartingEventArgs(uri);
        NavigationStarting?.Invoke(this, starting);
        if (starting.Cancel || !uri.IsFile) return false;
        try
        {
            var previous = Source is not null && string.Equals(_documentPath, uri.LocalPath, StringComparison.OrdinalIgnoreCase)
                ? CaptureNavigationPoint() : null;
            await PrepareDocumentAsync(uri.LocalPath, cancellationToken);
            _navigation?.Cancel();
            _navigation?.Dispose();
            _navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _documentCancellation!.Token);
            var token = _navigation.Token;
            var version = ++_version;
            _viewportCancellation?.Cancel();
            ++_rasterVersion;
            var pageNumber = Math.Clamp(ReadTargetPage(uri), 1, PageCount);
            _pointAnnotationMode = false;
            _handToolMode = false;
            _isPanning = false;
            LastError = null;
            var initialPages = DisplayMode == PdfReaderDisplayMode.TwoPage
                ? Enumerable.Range((pageNumber - 1) / 2 * 2 + 1, Math.Min(2, PageCount - (pageNumber - 1) / 2 * 2)).ToArray()
                : [pageNumber];
            await LoadPageContentsAsync(initialPages, version, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || version != _version) return false;
            Source = uri;
            // The old layout has no geometry for a different spread yet.
            // Publish the destination only after layout and pan are resolved.
            ActivatePage(pageNumber, emitPage: false);
            RebuildLayout(preservePosition: false);
            ClearSearch();
            ClearSelection();
            _speechHighlight = null;
            ScrollToTop(ReadTargetTop(uri), preserveVerticalPosition);
            var targetOffset = ReadTargetOffset(uri);
            if (targetOffset >= 0) ScrollToOffset(targetOffset);
            await RefreshViewportAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || version != _version) return false;
            if (LastError is not null) throw new InvalidOperationException(LastError);
            EmitPage();
            if (recordHistory && previous is not null) AddNavigationPoint(previous);
            NavigationCompleted?.Invoke(this, new(uri, true));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                LastError = exception.Message;
                NavigationCompleted?.Invoke(this, new(uri, false));
            }
            return false;
        }
    }
}
