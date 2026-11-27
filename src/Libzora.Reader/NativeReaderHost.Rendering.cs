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
/// Page painting and bitmap rendering.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private void PaintPageIntoBitmap(int pixelWidth, int pixelHeight, double scaling)
    {
        if (_bitmap is null)
        {
            return;
        }

        using var frame = _bitmap.Lock();
        var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, frame.Address, frame.RowBytes);
        if (surface is null)
        {
            return;
        }

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale((float)scaling);
        if (_mangaZoom > 1.01)
        {
            canvas.Translate(-(float)_mangaPan.X, -(float)_mangaPan.Y);
            canvas.Scale((float)_mangaZoom);
        }

        switch (_presentation)
        {
            case ReaderPresentation.Spread:
            {
                // Two half-width composed pages fill the viewport side by side.
                // Right-to-left manga places the earlier page on the right half.
                var half = (float)(Math.Max(1, Bounds.Width) / 2);
                var firstOffset = _settings.MangaMode && _settings.MangaRightToLeft ? half : 0f;
                var secondOffset = _settings.MangaMode && _settings.MangaRightToLeft ? 0f : half;
                PaintPage(canvas, _pageIndex, firstOffset, 0f);
                if (_pageIndex + 1 < (_layout?.Pages.Count ?? 0))
                {
                    PaintPage(canvas, _pageIndex + 1, secondOffset, 0f);
                }
                else if (IsShowingCompanionSpread && _spreadCompanionLayout is { Pages.Count: > 0 } companion)
                {
                    PaintLayoutPage(canvas, companion.Pages[0], secondOffset, 0f, includeTextOverlays: false);
                }
                break;
            }
            case ReaderPresentation.Scroll:
            {
                // Composed pages stack vertically; paint the slices that
                // intersect the viewport at the scroll offset, including the
                // preceding tile so lines or images straddling a tile boundary
                // render seamlessly without inter-page gaps.
                var pageHeight = (float)ScrollPageHeight;
                var first = (int)(_scrollOffset / pageHeight);
                var slice = (float)(_scrollOffset - first * pageHeight);
                if (first > 0) PaintPage(canvas, first - 1, 0f, -pageHeight - slice);
                PaintPage(canvas, first, 0f, -slice);
                PaintPage(canvas, first + 1, 0f, pageHeight - slice);
                if (IsContinuousProseScroll && _scrollNextChapterLayout is { Pages.Count: > 0 } nextChapter)
                {
                    var nextOriginY = (float)(ContinuousNextChapterStartY - _scrollOffset);
                    if (nextOriginY < pageHeight)
                    {
                        for (var p = 0; p < nextChapter.Pages.Count; p++)
                        {
                            var pageY = nextOriginY + p * pageHeight;
                            if (pageY >= pageHeight) break;
                            if (pageY + pageHeight <= -pageHeight) continue;
                            PaintLayoutPage(canvas, nextChapter.Pages[p], 0f, pageY, includeTextOverlays: false);
                        }
                    }
                }
                break;
            }
            default:
                PaintPage(canvas, _pageIndex, 0f, 0f);
                break;
        }

        canvas.Restore();
        canvas.Flush();
    }

    public void SetMangaZoom(double zoom, Point? anchorInView = null)
    {
        var clamped = double.IsFinite(zoom) ? Math.Clamp(zoom, 1.0, 4.0) : 1.0;
        if (clamped <= 1.01)
        {
            ResetMangaZoom();
            return;
        }

        var w = Math.Max(1, Bounds.Width);
        var h = Math.Max(1, Bounds.Height);
        var anchor = anchorInView ?? new Point(w / 2.0, h / 2.0);
        var prevZoom = Math.Max(1.0, _mangaZoom);
        var contentX = (anchor.X + _mangaPan.X) / prevZoom;
        var contentY = (anchor.Y + _mangaPan.Y) / prevZoom;
        _mangaZoom = clamped;
        _mangaPan = ClampMangaPan(
            contentX * clamped - anchor.X,
            contentY * clamped - anchor.Y,
            clamped);
        _bitmapDirty = true;
        InvalidateVisual();
        Emit(new { type = "mangaZoom", zoom = _mangaZoom });
    }

    public void PanMangaBy(Vector delta)
    {
        if (_mangaZoom <= 1.01) return;
        var next = ClampMangaPan(_mangaPan.X + delta.X, _mangaPan.Y + delta.Y, _mangaZoom);
        if (Math.Abs(next.X - _mangaPan.X) < 0.1 && Math.Abs(next.Y - _mangaPan.Y) < 0.1) return;
        _mangaPan = next;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    public void ResetMangaZoom()
    {
        if (_mangaZoom <= 1.01 && Math.Abs(_mangaPan.X) < 0.01 && Math.Abs(_mangaPan.Y) < 0.01) return;
        _mangaZoom = 1.0;
        _mangaPan = default;
        _bitmapDirty = true;
        InvalidateVisual();
        Emit(new { type = "mangaZoom", zoom = _mangaZoom });
    }

    private Point ClampMangaPan(double x, double y, double zoom)
    {
        var maxX = Math.Max(0, Bounds.Width * (zoom - 1.0));
        var maxY = Math.Max(0, Bounds.Height * (zoom - 1.0));
        return new Point(Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }

    private void PaintPage(SKCanvas canvas, int pageIndex, float offsetX, float offsetY)
    {
        if (_layout is null
            || _options is null
            || pageIndex < 0
            || pageIndex >= _layout.Pages.Count)
        {
            return;
        }

        PaintLayoutPage(canvas, _layout.Pages[pageIndex], offsetX, offsetY, includeTextOverlays: true);
    }

    private void PaintLayoutPage(SKCanvas canvas, LayoutPage page, float offsetX, float offsetY, bool includeTextOverlays)
    {
        using var imagePins = PinImages(page.Images.Select(image => image.Path));
        canvas.Save();
        canvas.Translate(offsetX, offsetY);
        var selectionBands = includeTextOverlays ? SelectionBandsFor(page) : null;
        var annotationOverlays = includeTextOverlays ? AnnotationOverlaysFor(page) : null;
        var searchBands = includeTextOverlays ? SearchBandsFor(page) : null;
        var focusedSearchBands = includeTextOverlays ? FocusedSearchBandsFor(page) : null;
        var highlightBands = includeTextOverlays ? SpeechBandsFor(page) : null;
        var resumeMarker = includeTextOverlays ? ResumeMarkerFor(page) : null;
        lock (_engineGate)
        {
            var painter = new TypesetPainter(Engine.Fonts, _paintTheme, ResolveImage);
            painter.Paint(
                canvas,
                page,
                selectionBands,
                highlightBands: highlightBands,
                searchBands: searchBands,
                annotationOverlays: annotationOverlays,
                showVerticalDebugBoxes: _showVerticalDebugBoxes,
                focusedSearchBands: focusedSearchBands,
                paintBackground: false,
                resumeMarker: resumeMarker);
        }

        canvas.Restore();
    }

    private TypesetFontLibrary FontsForPainting => Engine.Fonts;

    private static SKImage? ResolveImage(string path)
    {
        lock (ImageCacheLock)
        {
            if (ImageCache.TryGetValue(path, out var cached))
            {
                ImageCache[path] = (cached.Image, ++_imageCacheTick);
                return cached.Image;
            }
        }

        SKImage? image;
        try
        {
            image = DecodeImageScaled(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"[Libzora] image decode failed {path}: {exception.Message}");
            return null;
        }
        if (image is null) return null;

        lock (ImageCacheLock)
        {
            if (ImageCache.TryGetValue(path, out var raced))
            {
                // Another reader finished the same decode while we were busy.
                image.Dispose();
                ImageCache[path] = (raced.Image, ++_imageCacheTick);
                return raced.Image;
            }

            ImageCache[path] = (image, ++_imageCacheTick);
            TrimImageCacheLocked();
        }
        return image;
    }

    private static IDisposable PinImages(IEnumerable<string> paths)
    {
        var pinned = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pinned.Length == 0) return EmptyImagePin.Instance;

        lock (ImageCacheLock)
        {
            foreach (var path in pinned)
            {
                ImagePins.TryGetValue(path, out var count);
                ImagePins[path] = count + 1;
            }
        }
        return new ImagePin(pinned);
    }

    private static void TrimImageCacheLocked()
    {
        while (ImageCache.Count > ImageCacheCapacity)
        {
            string? oldestKey = null;
            var oldestUse = long.MaxValue;
            foreach (var entry in ImageCache)
            {
                if (ImagePins.ContainsKey(entry.Key) || entry.Value.UsedAt >= oldestUse) continue;
                oldestUse = entry.Value.UsedAt;
                oldestKey = entry.Key;
            }
            if (oldestKey is null || !ImageCache.Remove(oldestKey, out var stale)) break;
            stale.Image.Dispose();
        }
    }

    private sealed class ImagePin(string[] paths) : IDisposable
    {
        private string[]? _paths = paths;

        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _paths, null);
            if (released is null) return;
            lock (ImageCacheLock)
            {
                foreach (var path in released)
                {
                    if (!ImagePins.TryGetValue(path, out var count)) continue;
                    if (count <= 1) ImagePins.Remove(path);
                    else ImagePins[path] = count - 1;
                }
                TrimImageCacheLocked();
            }
        }
    }

    private sealed class EmptyImagePin : IDisposable
    {
        public static readonly EmptyImagePin Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Decodes an illustration into an Avalonia <see cref="WriteableBitmap"/>
    /// for the desktop lightbox viewer, bounded to <paramref name="maxDimension"/>.
    /// </summary>
    public static WriteableBitmap? DecodePreviewBitmap(string path, int maxDimension = 4096)
    {
        using var bitmap = DecodeSkBitmap(path, maxDimension);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var output = new WriteableBitmap(
            new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var frame = output.Lock();
        using var pixmap = bitmap.PeekPixels();
        if (pixmap is null || !pixmap.ReadPixels(info, frame.Address, frame.RowBytes, 0, 0))
        {
            output.Dispose();
            return null;
        }
        return output;
    }

    /// <summary>
    /// Decode at the size the page can actually show. A 4000px scan used to be
    /// decoded in full on the UI thread and then scaled down per frame.
    /// </summary>
    private static SKImage? DecodeImageScaled(string path)
    {
        using var bitmap = DecodeSkBitmap(path, ImageDecodeMaxDimension);
        return bitmap is null
            ? null
            : SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
    }

    private static SKBitmap? DecodeSkBitmap(string path, int maxDimension)
    {
        SKCodecResult scaledResult = default;
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0) return null;

            var target = ScaledImageInfo(codec, source, maxDimension);
            var bitmap = new SKBitmap(target);
            scaledResult = codec.GetPixels(target, bitmap.GetPixels());
            if (scaledResult is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                return bitmap;
            bitmap.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"[Libzora] scaled image decode failed {path}: {exception.Message}");
            return null;
        }

        // Progressive JPEGs (and several other codecs) only support a limited
        // set of subsampled sizes. Decode at full size as a fallback, then scale
        // once in Skia rather than leaving a blank frame.
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0) return null;
            if ((long)source.Width * source.Height > 80_000_000)
            {
                Console.WriteLine($"[Libzora] image too large to decode {path}: {source.Width}x{source.Height} ({scaledResult})");
                return null;
            }

            var sourceInfo = new SKImageInfo(
                source.Width,
                source.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using var bitmap = new SKBitmap(sourceInfo);
            var result = codec.GetPixels(sourceInfo, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                Console.WriteLine($"[Libzora] image decode failed {path}: {result} (scaled: {scaledResult})");
                return null;
            }

            var target = FitImageInfo(source, maxDimension);
            return bitmap.Resize(
                target,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"[Libzora] image decode failed {path}: {exception.Message}");
            return null;
        }
    }

    private static SKImageInfo ScaledImageInfo(SKCodec codec, SKImageInfo source, int maxDimension)
    {
        var requested = FitImageInfo(source, maxDimension);
        var supported = codec.GetScaledDimensions(
            Math.Min(requested.Width / (float)source.Width, requested.Height / (float)source.Height));
        return new SKImageInfo(
            Math.Max(1, supported.Width),
            Math.Max(1, supported.Height),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
    }

    private static SKImageInfo FitImageInfo(SKImageInfo source, int maxDimension)
    {
        var scale = Math.Min(1d, maxDimension / (double)Math.Max(source.Width, source.Height));
        return new SKImageInfo(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
    }
}
