using System.Runtime.InteropServices;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Detects tight non-white content bounds on scanned pages and comic images
/// so outer scan borders can be trimmed without re-encoding image files.
/// </summary>
public static class ImageCropDetector
{
    private const int MaxCachedEntries = 512;
    private const int PreviewDecodeMaxDimension = 400;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (long LastWriteTicks, float Padding, SKRect? Crop)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Detects the tight non-white content bounds of a BGRA8888 pixel buffer
    /// as a normalized 0..1 rectangle with comfortable breathing padding.
    /// Returns null when margins are negligible or the page is blank/full-bleed.
    /// </summary>
    public static SKRect? DetectNormalizedCropBox(byte[] pixels, int width, int height, double padding = 0.025)
    {
        if (width < 16 || height < 16 || pixels.Length < width * height * 4) return null;
        var step = width >= 400 ? 2 : 1;
        var rows = new int[(height + step - 1) / step];
        const int inkThreshold = 242;
        long totalInk = 0;
        for (var y = 0; y < height; y += step)
        {
            var rowOffset = y * width * 4;
            var rowIndex = y / step;
            for (var x = 0; x < width; x += step)
            {
                var idx = rowOffset + x * 4;
                if (pixels[idx] >= inkThreshold && pixels[idx + 1] >= inkThreshold && pixels[idx + 2] >= inkThreshold)
                    continue;
                rows[rowIndex]++;
                totalInk++;
            }
        }
        if (totalInk == 0) return null;
        var (firstRow, lastRow) = TrimOuterIslands(rows, totalInk);
        if (firstRow < 0) return null;

        var columns = new int[(width + step - 1) / step];
        long bandInk = 0;
        var inset = Math.Max(1, (int)Math.Round(rows.Length * 0.02));
        var bandFirst = Math.Max(firstRow, inset);
        var bandLast = Math.Min(lastRow, rows.Length - 1 - inset);
        if (bandFirst > bandLast) { bandFirst = firstRow; bandLast = lastRow; }
        for (var y = bandFirst * step; y <= bandLast * step && y < height; y += step)
        {
            var rowOffset = y * width * 4;
            for (var x = 0; x < width; x += step)
            {
                var idx = rowOffset + x * 4;
                if (pixels[idx] < inkThreshold || pixels[idx + 1] < inkThreshold || pixels[idx + 2] < inkThreshold)
                {
                    columns[x / step]++;
                    bandInk++;
                }
            }
        }
        var (firstColumn, lastColumn) = TrimOuterIslands(columns, bandInk);
        if (firstColumn < 0) return null;

        var left = firstColumn * step / (double)width;
        var top = firstRow * step / (double)height;
        var right = Math.Min(1, (lastColumn * step + step) / (double)width);
        var bottom = Math.Min(1, (lastRow * step + step) / (double)height);

        var pad = Math.Clamp(padding, 0, 0.05);
        left = Math.Clamp(left - pad, 0, 0.30);
        top = Math.Clamp(top - pad, 0, 0.30);
        right = Math.Clamp(right + pad, 0.70, 1.0);
        bottom = Math.Clamp(bottom + pad, 0.70, 1.0);

        var cropW = right - left;
        var cropH = bottom - top;
        if (cropW >= 0.96 && cropH >= 0.96) return null;
        if (cropW < 0.40 || cropH < 0.40) return null;
        return new SKRect((float)left, (float)top, (float)right, (float)bottom);
    }

    /// <summary>
    /// Computes and caches the normalized content crop rectangle for an on-disk
    /// image by decoding a small preview bitmap.
    /// </summary>
    public static SKRect? GetCachedImageCropBox(string path, float padding = 0.008f)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        long ticks;
        try { ticks = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return null; }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var entry)
                && entry.LastWriteTicks == ticks
                && Math.Abs(entry.Padding - padding) < 0.0001f)
            {
                return entry.Crop;
            }
        }

        var crop = ComputeImageCropBox(path, padding);
        lock (CacheLock)
        {
            if (Cache.Count >= MaxCachedEntries) Cache.Clear();
            Cache[path] = (ticks, padding, crop);
        }
        return crop;
    }

    private static SKRect? ComputeImageCropBox(string path, float padding)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var source = codec.Info;
            if (source.Width < 16 || source.Height < 16) return null;

            var scale = Math.Min(1f, PreviewDecodeMaxDimension / (float)Math.Max(source.Width, source.Height));
            var supported = codec.GetScaledDimensions(scale);
            var target = new SKImageInfo(
                Math.Max(16, supported.Width),
                Math.Max(16, supported.Height),
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using var bitmap = new SKBitmap(target);
            if (codec.GetPixels(target, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;

            var byteCount = target.Width * target.Height * 4;
            var pixels = new byte[byteCount];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, byteCount);
            return DetectNormalizedCropBox(pixels, target.Width, target.Height, padding);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static (int First, int Last) TrimOuterIslands(int[] counts, long totalInk)
    {
        var first = 0;
        var last = counts.Length - 1;
        while (first < last && counts[first] == 0) first++;
        while (last > first && counts[last] == 0) last--;
        if (counts[first] == 0) return (-1, -1);

        var gapMin = Math.Max(3, (int)Math.Round(counts.Length * 0.03));
        var islandLimit = Math.Max(4, totalInk / 100);
        while (first < last)
        {
            var groupEnd = first;
            while (groupEnd <= last && counts[groupEnd] > 0) groupEnd++;
            var gapEnd = groupEnd;
            while (gapEnd <= last && counts[gapEnd] == 0) gapEnd++;
            if (gapEnd > last) break;
            long islandInk = 0;
            for (var index = first; index < groupEnd; index++) islandInk += counts[index];
            if (gapEnd - groupEnd < gapMin || islandInk > islandLimit) break;
            first = gapEnd;
        }
        while (last > first)
        {
            var groupStart = last;
            while (groupStart >= first && counts[groupStart] > 0) groupStart--;
            var gapStart = groupStart;
            while (gapStart >= first && counts[gapStart] == 0) gapStart--;
            if (gapStart < first) break;
            long islandInk = 0;
            for (var index = groupStart + 1; index <= last; index++) islandInk += counts[index];
            if (groupStart - gapStart < gapMin || islandInk > islandLimit) break;
            last = gapStart;
        }
        return (first, last);
    }
}
