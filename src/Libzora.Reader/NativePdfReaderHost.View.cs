using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Measure, scroll bars, panning, emission and capture.
/// </summary>
public sealed partial class NativePdfReaderHost
{

    private async Task RefreshViewportCoreAsync(int version, int rasterVersion, CancellationToken token)
    {
        var rotation = Rotation;
        var palette = _palette;
        try
        {
            // Unknown page dimensions are estimated until that page enters the viewport.
            // Reflow keeps the current page and its local reading position anchored.
            for (var pass = 0; pass < 4; pass++)
            {
                var needed = VisiblePageNumbers.Append(PageNumber).Distinct().ToArray();
                if (needed.All(_pages.ContainsKey)) break;
                await LoadPageContentsAsync(needed, version, token);
                token.ThrowIfCancellationRequested();
                if (version != _version || rasterVersion != _rasterVersion) return;
                RebuildLayout(preservePosition: true);
            }
            var requests = new List<(int Page, int Width, int Height, int X, int Y, int W, int H)>();
            var visible = VisiblePageNumbers;
            foreach (var page in visible)
            {
                if (!_pages.TryGetValue(page, out var cached)) continue;
                var bounds = GetPageBounds(page);
                var fullSize = _sizes[page - 1];
                if (Rotation % 180 != 0) fullSize = new(fullSize.Height, fullSize.Width);
                var scale = Math.Max(
                    bounds.Width / Math.Max(1, fullSize.Width),
                    bounds.Height / Math.Max(1, fullSize.Height));
                var width = Math.Max(1, (int)Math.Round(fullSize.Width * scale * _layoutDpi));
                var height = Math.Max(1, (int)Math.Round(fullSize.Height * scale * _layoutDpi));
                var intersection = bounds.Intersect(new Rect(Viewport));
                if (intersection.Width <= 0 || intersection.Height <= 0) continue;
                var left = Math.Clamp(
                    (int)Math.Floor((intersection.X - bounds.X) * _layoutDpi),
                    0,
                    width - 1);
                var top = Math.Clamp(
                    (int)Math.Floor((intersection.Y - bounds.Y) * _layoutDpi),
                    0,
                    height - 1);
                var right = Math.Clamp(
                    (int)Math.Ceiling((intersection.Right - bounds.X) * _layoutDpi),
                    left + 1,
                    width);
                var bottom = Math.Clamp(
                    (int)Math.Ceiling((intersection.Bottom - bounds.Y) * _layoutDpi),
                    top + 1,
                    height);
                if (cached.Raster is { } raster && raster.Rotation == rotation && cached.Palette == palette
                    && raster.PagePixelWidth == width && raster.PagePixelHeight == height
                    && raster.Left <= left && raster.Top <= top && raster.Left + raster.Width >= right && raster.Top + raster.Height >= bottom) continue;
                // A small overscan region absorbs several wheel ticks without repainting.
                var padding = Math.Min(160, (int)(96 * _layoutDpi));
                left = Math.Max(0, left - padding); top = Math.Max(0, top - padding);
                right = Math.Min(width, right + padding); bottom = Math.Min(height, bottom + padding);
                requests.Add((page, width, height, left, top, right - left, bottom - top));
            }
            foreach (var request in requests)
            {
                var raster = await Task.Run(() =>
                {
                    lock (_documentGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_disposed || version != _version || rasterVersion != _rasterVersion || _document is null) throw new OperationCanceledException(token);
                        return _document.RenderRegion(request.Page, request.Width, request.Height, request.X, request.Y, request.W, request.H, token, rotation);
                    }
                }, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || version != _version || rasterVersion != _rasterVersion) return;
                ReplaceBitmap(_pages[request.Page], raster, palette);
            }
            var keep = visible.Append(PageNumber).ToHashSet();
            foreach (var page in _pages.Keys.Where(page => !keep.Contains(page)).OrderByDescending(page => Math.Abs(page - PageNumber)).ToArray())
            {
                if (_pages.Count <= Math.Max(6, keep.Count) && CachedRasterBytes < 96_000_000) break;
                _pages[page].Dispose();
                _pages.Remove(page);
            }
            if (!string.IsNullOrEmpty(_searchQuery)) RebuildSearch(_searchQuery);
            EmitPage();
            InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_disposed && version == _version && rasterVersion == _rasterVersion)
            {
                LastError = exception.Message;
                Emit(new { type = "pdfError", message = LastError });
            }
        }
    }

    private static void ReplaceBitmap(CachedPage page, PdfRasterRegion raster, ReaderPalette palette)
    {
        ApplyPageColors(raster.Pixels, palette);
        var bitmap = new WriteableBitmap(new PixelSize(raster.Width, raster.Height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            if (frame.RowBytes == raster.Width * 4) Marshal.Copy(raster.Pixels, 0, frame.Address, raster.Pixels.Length);
            else for (var row = 0; row < raster.Height; row++)
                Marshal.Copy(raster.Pixels, row * raster.Width * 4, IntPtr.Add(frame.Address, row * frame.RowBytes), raster.Width * 4);
        }
        page.Bitmap?.Dispose();
        page.Bitmap = bitmap;
        page.Palette = palette;
        // Keep geometry for cache reuse, not a second copy of the pixel buffer.
        page.Raster = raster with { Pixels = [] };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _vertical.Measure(new Size(12, Math.Max(0, availableSize.Height)));
        _horizontal.Measure(new Size(Math.Max(0, availableSize.Width), 12));
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        SyncScrollBars();
        _vertical.Arrange(new Rect(Math.Max(0, finalSize.Width - 12), 0, 12, Math.Max(0, finalSize.Height - 12)));
        _horizontal.Arrange(new Rect(0, Math.Max(0, finalSize.Height - 12), Math.Max(0, finalSize.Width - 12), 12));
        UpdateAnnotationCallout();
        return finalSize;
    }

    private void SyncScrollBars()
    {
        _syncingBars = true;
        try
        {
            _vertical.Maximum = Math.Max(0, (_layout?.Extent.Height ?? 0) - Viewport.Height);
            _horizontal.Maximum = Math.Max(0, (_layout?.Extent.Width ?? 0) - Viewport.Width);
            _vertical.ViewportSize = Viewport.Height;
            _horizontal.ViewportSize = Viewport.Width;
            var dpi = DeviceScaling;
            _pan = _allowZoomOverpan
                ? new(Math.Round(_pan.X * dpi) / dpi, Math.Round(_pan.Y * dpi) / dpi)
                : new(Math.Round(Math.Clamp(_pan.X, 0, _horizontal.Maximum) * dpi) / dpi,
                    Math.Round(Math.Clamp(_pan.Y, 0, _vertical.Maximum) * dpi) / dpi);
            _vertical.Value = Math.Clamp(_pan.Y, 0, _vertical.Maximum);
            _horizontal.Value = Math.Clamp(_pan.X, 0, _horizontal.Maximum);
            _vertical.IsVisible = _vertical.Maximum > 1;
            _horizontal.IsVisible = _horizontal.Maximum > 1;
        }
        finally { _syncingBars = false; }
    }

    private void SetPan(Vector pan, bool trackPage = true, bool render = true, bool allowZoomOverpan = false)
    {
        CloseFootnotePreview();
        _pan = pan;
        _allowZoomOverpan = allowZoomOverpan;
        SyncScrollBars();
        UpdateAnnotationCallout();
        if (trackPage && DisplayMode == PdfReaderDisplayMode.Continuous && _layout is not null)
            ActivatePage(_layout.PageAt(_pan.Y + PdfReaderLayout.Margin + 1));
        InvalidateArrange();
        if (_annotationCallout is null) ClearHover();
        InvalidateVisual();
        EmitPage();
        if (render) ScheduleRender();
    }

    private void ActivatePage(int page, bool emitPage = true)
    {
        page = Math.Clamp(page, 1, Math.Max(1, PageCount));
        if (PageNumber == page) return;
        PageNumber = page;
        if (_documentPath is not null) Source = new Uri(new Uri(_documentPath).AbsoluteUri + $"#page={page}");
        ClearSelection();
        _speechHighlight = null;
        RebuildSearch(_searchQuery);
        if (emitPage) EmitPage();
    }

    public void ScrollBy(double pixels)
    {
        ScrollBarAutoHide.Show(_vertical);
        ScrollPanBy(new(0, pixels));
    }

    private void ScrollPanBy(Vector delta)
    {
        var maxX = _horizontal.Maximum;
        var maxY = _vertical.Maximum;
        var pan = new Vector(
            ApplyScrollDelta(_pan.X, delta.X, maxX),
            ApplyScrollDelta(_pan.Y, delta.Y, maxY));
        SetPan(pan, allowZoomOverpan: HasZoomOverpan(pan));
    }

    private bool HasZoomOverpan(Vector pan) =>
        pan.X < 0 || pan.Y < 0 || pan.X > _horizontal.Maximum || pan.Y > _vertical.Maximum;

    private static double ApplyScrollDelta(double current, double delta, double maximum)
    {
        if (current < 0)
        {
            if (delta <= 0) return current;
            var next = current + delta;
            return next < 0 ? next : Math.Clamp(next, 0, maximum);
        }
        if (current > maximum)
        {
            if (delta >= 0) return current;
            var next = current + delta;
            return next > maximum ? next : Math.Clamp(next, 0, maximum);
        }
        return Math.Clamp(current + delta, 0, maximum);
    }

    public void ScrollToTop(double top, bool preserveVerticalPosition = false)
    {
        if (_layout is null) return;
        var page = _layout.Pages[PageNumber - 1];
        top = Math.Clamp(top, 0, 1);
        var margin = PdfReaderLayout.Margin;
        var x = _pan.X;
        if (DisplayMode == PdfReaderDisplayMode.TwoPage)
        {
            // A destination on the other sheet must enter the viewport even
            // when zoom has made the spread wider than the reader surface.
            var start = page.Left - margin;
            var end = page.Right + margin - Viewport.Width;
            x = start <= end ? start : Math.Clamp(x, end, start);
        }
        Vector pan = Rotation switch
        {
            90 => new(page.Right + margin - top * page.Width - Viewport.Width, page.Y - margin),
            180 => new(x, page.Bottom + margin - top * page.Height - Viewport.Height),
            270 => new(page.X - margin + top * page.Width, page.Y - margin),
            _ => new(x, page.Y - margin + top * page.Height)
        };
        // Ordinary spread turns keep the sheet at the same vertical position.
        // In particular, a restored/zoom-anchored offset must not snap back to
        // the centered fit position when the next equal-sized spread appears.
        if (preserveVerticalPosition) pan = new(Math.Clamp(pan.X, 0, _horizontal.Maximum), _pan.Y);
        SetPan(pan, trackPage: false, allowZoomOverpan: preserveVerticalPosition && _allowZoomOverpan);
    }

    private void EmitPage()
    {
        if (_layout is null) return;
        Emit(new { type = "pdfPage", page = PageNumber, top = PagePanY, left = _pan.X,
            scrollWidth = _layout.Extent.Width, scrollHeight = PageBounds.Height + PdfReaderLayout.Margin * 2,
            clientWidth = Viewport.Width, clientHeight = Viewport.Height });
    }

    private void Emit(object message)
    {
        if (!_disposed) WebMessageReceived?.Invoke(this, new ReaderWebMessageReceivedEventArgs(JsonSerializer.Serialize(message)));
    }

    public Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pages.Count == 0 || Bounds.Width < 1 || Bounds.Height < 1) return Task.FromResult<byte[]?>(null);
        var dpi = DeviceScaling;
        using var capture = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width * dpi), (int)Math.Ceiling(Bounds.Height * dpi)), new Vector(96 * dpi, 96 * dpi));
        capture.Render(this);
        using var stream = new MemoryStream();
        capture.Save(stream, PngBitmapEncoderOptions.Default);
        return Task.FromResult<byte[]?>(stream.ToArray());
    }

    public void Stop()
    {
        CloseFootnotePreview();
        _documentReady = false;
        _navigation?.Cancel();
        _viewportCancellation?.Cancel();
        _documentCancellation?.Cancel();
        ++_version; ++_rasterVersion;
        _resizeTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _navigation?.Dispose();
        _viewportCancellation?.Dispose();
        _documentCancellation?.Dispose();
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
        lock (_documentGate) { _document?.Dispose(); _document = null; _documentInfo = null; }
    }
}
