using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// View state, layout rebuilding and viewport refresh.
/// </summary>
public sealed partial class NativePdfReaderHost
{
    public PdfDocumentInfo? CachedDocumentInfo => Volatile.Read(ref _documentInfo);

    public Task<PdfDocumentInfo> ReadDocumentInfoAsync(CancellationToken token) => Task.Run(() =>
    {
        lock (_documentGate)
        {
            token.ThrowIfCancellationRequested();
            if (_documentInfo is { } cached) return cached;
            return (_document ?? throw new ObjectDisposedException(nameof(NativePdfReaderHost))).ReadInfo(token);
        }
    }, token);

    public Task<IReadOnlyList<PdfPageText>> BuildTextIndexAsync(CancellationToken token)
    {
        var count = PageCount;
        var path = _documentPath;
        return Task.Run<IReadOnlyList<PdfPageText>>(async () =>
        {
            var result = new PdfPageText[count];
            long characters = 0;
            for (var page = 1; page <= count; page++)
            {
                token.ThrowIfCancellationRequested();
                var text = string.Empty;
                if (page <= 10_000 && characters < 20_000_000)
                {
                    lock (_documentGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_disposed || _documentPath != path || _document is null) throw new OperationCanceledException(token);
                        text = _document.ReadPageText(page, token);
                    }
                }
                result[page - 1] = new(page, text);
                characters += text.Length;
                if (page % 16 != 0) continue;
                // Visible-page work takes priority without marshaling every
                // indexed page onto the UI thread or introducing timer delays.
                try { await _renderTask.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Yield();
            }
            return result;
        }, token);
    }

    public static int ReadTargetPage(Uri uri) => PdfNavigationTarget.ReadPage(uri);
    public static double ReadTargetTop(Uri uri) => PdfNavigationTarget.ReadTop(uri);
    public static int ReadTargetOffset(Uri uri) => PdfNavigationTarget.ReadOffset(uri);

    public PdfPageContent? GetPageContent(int page) => _pages.GetValueOrDefault(page)?.Content;
    public Rect GetPageBounds(int page) => _layout is not null && page >= 1 && page <= _layout.Pages.Length
        ? _layout.Pages[page - 1].Translate(-_pan) : new Rect(Viewport);
    internal PdfRasterRegion? GetRenderedRegion(int page) => _pages.GetValueOrDefault(page)?.Raster;

    public int GetAdjacentPage(int direction)
    {
        if (PageCount == 0 || direction == 0) return PageNumber;
        var first = DisplayMode == PdfReaderDisplayMode.TwoPage ? (PageNumber - 1) / 2 * 2 + 1 : PageNumber;
        var target = first + Math.Sign(direction) * (DisplayMode == PdfReaderDisplayMode.TwoPage ? 2 : 1);
        return target < 1 || target > PageCount ? PageNumber : target;
    }

    public void SetAppearance(ReaderAppearanceSettings appearance)
    {
        var palette = ReaderPalette.For(appearance.Theme);
        if (_palette != palette)
        {
            _palette = palette;
            ++_rasterVersion;
            ScheduleRender();
        }
        ReaderAppearanceResources.PopulateControls(_vertical.Resources, appearance);
        ReaderAppearanceResources.PopulateControls(_horizontal.Resources, appearance);
        InvalidateVisual();
    }

    public async Task SetDisplayModeAsync(PdfReaderDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        var request = ++_layoutRequest;
        if (mode == PdfReaderDisplayMode.TwoPage && PageCount > 0)
        {
            var first = (PageNumber - 1) / 2 * 2 + 1;
            var count = Math.Min(2, PageCount - first + 1);
            await LoadPageContentsAsync(Enumerable.Range(first, count), _version, _documentCancellation!.Token);
            if (_disposed || request != _layoutRequest) return;
        }
        ClearSelection();
        _pointAnnotationMode = false;
        DisplayMode = mode;
        RebuildLayout(preservePosition: true);
        await RefreshViewportAsync();
        Emit(new { type = "pdfLayout" });
    }

    public async Task SetFitModeAsync(PdfReaderFitMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        ++_layoutRequest;
        FitMode = mode;
        Zoom = 1;
        RebuildLayout(preservePosition: true);
        await RefreshViewportAsync();
        Emit(new { type = "pdfLayout" });
    }

    public async Task SetZoomAsync(double zoom, Point? anchor = null)
    {
        if (!double.IsFinite(zoom)) return;
        ++_layoutRequest;
        Zoom = Math.Clamp(zoom, 0.25, 8);
        if (_layout is { Pages.Length: > 0 })
        {
            var anchorPoint = anchor ?? new Point(Viewport.Width / 2, Viewport.Height / 2);
            var anchorPage = VisiblePageNumbers
                .OrderBy(pageNumber =>
                {
                    var bounds = GetPageBounds(pageNumber);
                    var dx = Math.Max(0, Math.Max(bounds.Left - anchorPoint.X, anchorPoint.X - bounds.Right));
                    var dy = Math.Max(0, Math.Max(bounds.Top - anchorPoint.Y, anchorPoint.Y - bounds.Bottom));
                    return dx * dx + dy * dy;
                })
                .FirstOrDefault();
            if (anchorPage == 0) anchorPage = Math.Clamp(PageNumber, 1, _layout.Pages.Length);

            var oldPage = GetPageBounds(anchorPage);
            var oldSize = GetUnrotatedPageSize(oldPage);
            var local = GetPageTransform(oldPage).Invert().Transform(anchorPoint);
            var fraction = new Point(
                Math.Clamp(local.X / Math.Max(1, oldSize.Width), 0, 1),
                Math.Clamp(local.Y / Math.Max(1, oldSize.Height), 0, 1));

            RebuildLayout(preservePosition: false);
            var newPage = _layout.Pages[anchorPage - 1];
            var newSize = GetUnrotatedPageSize(newPage);
            var anchoredPoint = GetPageTransform(newPage).Transform(new Point(
                fraction.X * newSize.Width,
                fraction.Y * newSize.Height));
            var anchoredPan = new Vector(anchoredPoint.X - anchorPoint.X, anchoredPoint.Y - anchorPoint.Y);
            var maxPan = new Vector(
                Math.Max(0, _layout.Extent.Width - Viewport.Width),
                Math.Max(0, _layout.Extent.Height - Viewport.Height));
            var needsOverpan = anchoredPan.X < 0 || anchoredPan.Y < 0
                || anchoredPan.X > maxPan.X || anchoredPan.Y > maxPan.Y;
            SetPan(anchoredPan, render: false, allowZoomOverpan: needsOverpan);
        }
        else
        {
            RebuildLayout(preservePosition: false);
        }
        Emit(new { type = "pdfZoom", zoom = Zoom });
        await RefreshViewportAsync();
    }

    public async Task RotateClockwiseAsync()
    {
        if (_disposed || _layout is null) return;
        ++_layoutRequest;
        var page = PageBounds;
        var size = GetUnrotatedPageSize(page);
        var center = GetPageTransform(page).Invert().Transform(new Point(Viewport.Width / 2, Viewport.Height / 2));
        var fraction = new Point(Math.Clamp(center.X / size.Width, 0, 1), Math.Clamp(center.Y / size.Height, 0, 1));
        Rotation = (Rotation + 90) % 360;
        ClearSelection();
        RebuildLayout(preservePosition: false);
        page = _layout.Pages[PageNumber - 1];
        size = GetUnrotatedPageSize(page);
        center = GetPageTransform(page).Transform(new Point(fraction.X * size.Width, fraction.Y * size.Height));
        SetPan(new(center.X - Viewport.Width / 2, center.Y - Viewport.Height / 2), trackPage: false, render: false);
        await RefreshViewportAsync();
        Emit(new { type = "pdfLayout" });
    }

    private double PagePanY => _layout is null ? 0 : _pan.Y - _layout.Pages[PageNumber - 1].Y + PdfReaderLayout.Margin;
    public string CaptureViewState() => string.Create(CultureInfo.InvariantCulture,
        $"pdf-view:{Zoom:R};{_pan.X / Math.Max(1, PageBounds.Width):R};{PagePanY / Math.Max(1, PageBounds.Height):R};{(int)DisplayMode};{(int)FitMode};{Rotation};{PageNumberOffset ?? 0};{(PageNumberOffset.HasValue ? 1 : 0)}");

    public void RestoreViewSettings(string? state)
    {
        var values = ParseViewState(state);
        if (values is null) return;
        Zoom = Math.Clamp(values[0], 0.25, 8);
        DisplayMode = values.Length == 3 ? PdfReaderDisplayMode.SinglePage : (PdfReaderDisplayMode)(int)values[3];
        FitMode = values.Length == 3 ? PdfReaderFitMode.Width : (PdfReaderFitMode)(int)values[4];
        Rotation = values.Length > 5 ? (int)values[5] : 0;
        PageNumberOffset = values.Length >= 8 && values[7] == 1 ? (int)values[6] : null;
    }

    public async Task RestoreViewStateAsync(string? state, int legacyScrollPosition = 0)
    {
        var values = ParseViewState(state);
        RestoreViewSettings(state);
        RebuildLayout(preservePosition: false);
        if (_layout is not null)
        {
            var page = _layout.Pages[PageNumber - 1];
            var pan = values is null ? new Vector(0, page.Y - PdfReaderLayout.Margin + Math.Max(0, legacyScrollPosition))
                : new Vector(Math.Clamp(values[1], -2, 3) * page.Width,
                    page.Y - PdfReaderLayout.Margin + Math.Clamp(values[2], -2, 3) * page.Height);
            SetPan(pan, trackPage: false, render: false, allowZoomOverpan: HasZoomOverpan(pan));
        }
        await RefreshViewportAsync();
    }

    private static double[]? ParseViewState(string? state)
    {
        var values = state?.StartsWith("pdf-view:", StringComparison.Ordinal) == true ? state[9..].Split(';') : [];
        if (values.Length is not (3 or 5 or 6 or 7 or 8)) return null;
        var parsed = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
            if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index]) || !double.IsFinite(parsed[index])) return null;
        // Version 1 stored the removed paper-column mode as value 3. Treat
        // those saved sessions as ordinary single-page PDFs instead of
        // allowing an unavailable mode to leak into the new reader.
        if (values.Length >= 5 && parsed[3] == 3) parsed[3] = (int)PdfReaderDisplayMode.SinglePage;
        if (values.Length >= 5 && (parsed[3] != Math.Truncate(parsed[3]) || parsed[4] != Math.Truncate(parsed[4])
            || !Enum.IsDefined((PdfReaderDisplayMode)(int)parsed[3]) || !Enum.IsDefined((PdfReaderFitMode)(int)parsed[4]))) return null;
        if (values.Length >= 6 && parsed[5] is not (0 or 90 or 180 or 270)) return null;
        if (values.Length >= 8 && (parsed[6] != Math.Truncate(parsed[6]) || Math.Abs(parsed[6]) > 1_000_000 || parsed[7] is not (0 or 1))) return null;
        return parsed;
    }

    private void RebuildLayout(bool preservePosition)
    {
        CloseFootnotePreview();
        if (_sizes.Length == 0 || _disposed) return;
        var fractionX = _pan.X / Math.Max(1, PageBounds.Width);
        var fractionY = PagePanY / Math.Max(1, PageBounds.Height);
        _layoutDpi = DeviceScaling;
        _layout = new(
            _sizes,
            Viewport,
            PageNumber,
            DisplayMode,
            FitMode,
            Zoom,
            _layoutDpi,
            Rotation);
        if (preservePosition)
        {
            var page = _layout.Pages[PageNumber - 1];
            _pan = new(fractionX * page.Width, page.Y - PdfReaderLayout.Margin + fractionY * page.Height);
        }
        SyncScrollBars();
        InvalidateArrange();
        InvalidateVisual();
        UpdateAnnotationCallout();
    }

    private async Task LoadPageContentsAsync(IEnumerable<int> pages, int version, CancellationToken token)
    {
        var missing = pages.Where(page => page >= 1 && page <= PageCount && !_pages.ContainsKey(page)).Distinct().ToArray();
        if (missing.Length == 0) return;
        var loaded = await Task.Run(() =>
        {
            var result = new List<(int Page, PdfPageContent Content)>();
            foreach (var page in missing)
                lock (_documentGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_disposed || version != _version || _document is null) throw new OperationCanceledException(token);
                    result.Add((page, _document.ReadPage(page, token)));
                }
            return result;
        }, token);
        token.ThrowIfCancellationRequested();
        if (_disposed || version != _version) return;
        foreach (var (page, content) in loaded)
        {
            if (!_pages.ContainsKey(page)) _pages.Add(page, new(content));
            _sizes[page - 1] = new(content.Width, content.Height);
        }
    }

    private void ScheduleRender()
    {
        if (_disposed || _sizes.Length == 0 || _resizeTimer.IsEnabled) return;
        // Throttle, rather than debounce: a continuous trackpad gesture must
        // keep producing sharp frames while input is still arriving.
        _resizeTimer.Start();
    }

    public Task RefreshViewportAsync(CancellationToken token = default)
    {
        if (_disposed || _document is null || _layout is null) return Task.CompletedTask;
        _resizeTimer.Stop();
        _viewportCancellation?.Cancel();
        _viewportCancellation?.Dispose();
        _viewportCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _documentCancellation!.Token);
        _renderTask = RefreshViewportCoreAsync(_version, ++_rasterVersion, _viewportCancellation.Token);
        return _renderTask;
    }
}
