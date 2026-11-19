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

        switch (_presentation)
        {
            case ReaderPresentation.Spread:
            {
                // Two half-width composed pages fill the viewport side by side.
                var half = (float)(Math.Max(1, Bounds.Width) / 2);
                PaintPage(canvas, _pageIndex, 0f, 0f);
                PaintPage(canvas, _pageIndex + 1, half, 0f);
                break;
            }
            case ReaderPresentation.Scroll:
            {
                // Composed pages stack vertically; paint the two slices that
                // intersect the viewport at the scroll offset.
                var pageHeight = (float)Math.Max(1, Bounds.Height);
                var first = (int)(_scrollOffset / pageHeight);
                var slice = (float)(_scrollOffset - first * pageHeight);
                PaintPage(canvas, first, 0f, -slice);
                PaintPage(canvas, first + 1, 0f, pageHeight - slice);
                break;
            }
            default:
                PaintPage(canvas, _pageIndex, 0f, 0f);
                break;
        }

        canvas.Restore();
        canvas.Flush();
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

        var page = _layout.Pages[pageIndex];
        using var imagePins = PinImages(page.Images.Select(image => image.Path));
        canvas.Save();
        canvas.Translate(offsetX, offsetY);
        var selectionBands = SelectionBandsFor(page);
        var annotationOverlays = AnnotationOverlaysFor(page);
        var searchBands = SearchBandsFor(page);
        var focusedSearchBands = FocusedSearchBandsFor(page);
        lock (_engineGate)
        {
            var painter = new TypesetPainter(Engine.Fonts, _paintTheme, ResolveImage);
            painter.Paint(
                canvas,
                page,
                selectionBands,
                highlightBands: SpeechBandsFor(page),
                searchBands: searchBands,
                annotationOverlays: annotationOverlays,
                showVerticalDebugBoxes: _showVerticalDebugBoxes,
                focusedSearchBands: focusedSearchBands,
                paintBackground: false);
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
    /// Decode at the size the page can actually show. A 4000px scan used to be
    /// decoded in full on the UI thread and then scaled down per frame.
    /// </summary>
    private static SKImage? DecodeImageScaled(string path)
    {
        SKCodecResult scaledResult = default;
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0) return null;

            var target = ScaledImageInfo(codec, source);
            using var bitmap = new SKBitmap(target);
            scaledResult = codec.GetPixels(target, bitmap.GetPixels());
            if (scaledResult is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                return SKImage.FromPixelCopy(target, bitmap.GetPixels(), bitmap.RowBytes);
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

            var target = FitImageInfo(source);
            using var scaled = bitmap.Resize(
                target,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (scaled is null) return null;
            return SKImage.FromPixelCopy(target, scaled.GetPixels(), scaled.RowBytes);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"[Libzora] image decode failed {path}: {exception.Message}");
            return null;
        }
    }

    private static SKImageInfo ScaledImageInfo(SKCodec codec, SKImageInfo source)
    {
        var requested = FitImageInfo(source);
        var supported = codec.GetScaledDimensions(
            Math.Min(requested.Width / (float)source.Width, requested.Height / (float)source.Height));
        return new SKImageInfo(
            Math.Max(1, supported.Width),
            Math.Max(1, supported.Height),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
    }

    private static SKImageInfo FitImageInfo(SKImageInfo source)
    {
        var scale = Math.Min(1d, ImageDecodeMaxDimension / (double)Math.Max(source.Width, source.Height));
        return new SKImageInfo(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
    }
}
