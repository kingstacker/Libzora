using Avalonia;
using Libzora.Core;

namespace Libzora;

public sealed partial class NativePdfReaderHost
{
    /// <summary>
    /// Detects the tight non-white content bounds of a raw PDFium BGRA raster
    /// (before theme recoloring) as a normalized 0..1 rectangle with comfortable
    /// breathing padding. Returns null when margins are negligible or the page
    /// is blank/full-bleed.
    /// </summary>
    internal static Rect? DetectContentCropBox(byte[] pixels, int width, int height)
    {
        if (width < 16 || height < 16 || pixels.Length < width * height * 4) return null;
        // Sample every 2nd pixel on larger rasters for sub-millisecond scans.
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

        // Measure the horizontal bounds only across the main content rows: a
        // page number or scanner mark far below the text block must not widen
        // the crop back to the page edge. The outermost sliver is ignored even
        // when it is too close to the text for the gap test above, because
        // printed page numbers live there.
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

        // Add gentle breathing padding around detected ink and cap maximum trim
        // so sparse pages (e.g. a centered chapter title) never zoom excessively.
        const double pad = 0.025;
        left = Math.Clamp(left - pad, 0, 0.30);
        top = Math.Clamp(top - pad, 0, 0.30);
        right = Math.Clamp(right + pad, 0.70, 1.0);
        bottom = Math.Clamp(bottom + pad, 0.70, 1.0);

        var cropW = right - left;
        var cropH = bottom - top;
        if (cropW >= 0.96 && cropH >= 0.96) return null;
        if (cropW < 0.40 || cropH < 0.40) return null;
        return new Rect(left, top, cropW, cropH);
    }

    /// <summary>
    /// Returns the first and last rows/columns that belong to the main content
    /// block. A tiny ink island behind a wide blank gap at the page edge (a page
    /// number in the outer margin, a scanner edge or a stray rule) must not pin
    /// that side while the opposite margin is trimmed: that asymmetry pushed the
    /// text block against the other edge of the screen.
    /// </summary>
    private static (int First, int Last) TrimOuterIslands(int[] counts, long totalInk)
    {
        var first = 0;
        var last = counts.Length - 1;
        while (first < last && counts[first] == 0) first++;
        while (last > first && counts[last] == 0) last--;
        if (counts[first] == 0) return (-1, -1);

        // Ignore only a genuinely tiny island (under 1% of the page ink) whose
        // gap is wide enough not to be ordinary word or line spacing.
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

    internal static void ApplyPageColors(byte[] pixels, ReaderPalette palette)
    {
        if (palette == ReaderPalette.For(ReaderTheme.Classic)) return;
        var isDark = (palette.Page.R * 77 + palette.Page.G * 150 + palette.Page.B * 29) < 64 * 256;
        if (isDark)
        {
            ApplyDarkPageColors(pixels, palette);
            return;
        }

        // Light/warm themes (Beige, Green, etc.): remap white paper and neutral
        // ink to the paper palette while preserving saturated color figures intact.
        var pageR = palette.Page.R;
        var pageG = palette.Page.G;
        var pageB = palette.Page.B;
        var inkR = palette.Ink.R;
        var inkG = palette.Ink.G;
        var inkB = palette.Ink.B;
        var deltaR = pageR - inkR;
        var deltaG = pageG - inkG;
        var deltaB = pageB - inkB;
        const int colorThreshold = 48;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            if (r == g && g == b)
            {
                pixels[index] = (byte)(inkB + ((deltaB * b + 127) / 255));
                pixels[index + 1] = (byte)(inkG + ((deltaG * g + 127) / 255));
                pixels[index + 2] = (byte)(inkR + ((deltaR * r + 127) / 255));
                continue;
            }
            var max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            var min = r < g ? (r < b ? r : b) : (g < b ? g : b);
            var chroma = max - min;
            if (chroma >= colorThreshold) continue;
            var gray = (r * 77 + g * 150 + b * 29 + 128) >> 8;
            var weight = colorThreshold - chroma;
            var mapB = inkB + ((deltaB * gray + 127) / 255);
            var mapG = inkG + ((deltaG * gray + 127) / 255);
            var mapR = inkR + ((deltaR * gray + 127) / 255);
            pixels[index] = (byte)((b * chroma + mapB * weight + colorThreshold / 2) / colorThreshold);
            pixels[index + 1] = (byte)((g * chroma + mapG * weight + colorThreshold / 2) / colorThreshold);
            pixels[index + 2] = (byte)((r * chroma + mapR * weight + colorThreshold / 2) / colorThreshold);
        }
    }

    /// <summary>
    /// Smooth luminance-preserving inversion for dark themes (Ink): inverts
    /// perceptual brightness while retaining chromaticity (hue/saturation), so
    /// white paper becomes <c>palette.Page</c>, black ink becomes <c>palette.Ink</c>,
    /// and light-tinted/colored charts transition cleanly without blotchy halos.
    /// </summary>
    private static void ApplyDarkPageColors(byte[] pixels, ReaderPalette palette)
    {
        var pageR = palette.Page.R;
        var pageG = palette.Page.G;
        var pageB = palette.Page.B;
        var inkR = palette.Ink.R;
        var inkG = palette.Ink.G;
        var inkB = palette.Ink.B;
        var spanR = inkR - pageR;
        var spanG = inkG - pageG;
        var spanB = inkB - pageB;

        for (var index = 0; index < pixels.Length; index += 4)
        {
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            if (r == g && g == b)
            {
                var inv = 255 - r;
                pixels[index] = (byte)(pageB + ((spanB * inv + 127) / 255));
                pixels[index + 1] = (byte)(pageG + ((spanG * inv + 127) / 255));
                pixels[index + 2] = (byte)(pageR + ((spanR * inv + 127) / 255));
                continue;
            }

            // Compute perceptual luminance L in 0..255 and map (255 - L) into
            // the dark theme range [pageLum..inkLum], preserving RGB chroma offsets.
            var lum = (r * 77 + g * 150 + b * 29 + 128) >> 8;
            var invLum = 255 - lum;
            var targetR = pageR + ((spanR * invLum + 127) / 255);
            var targetG = pageG + ((spanG * invLum + 127) / 255);
            var targetB = pageB + ((spanB * invLum + 127) / 255);

            // Slightly attenuate chroma (0.85x) so neon colors stay calm on dark paper.
            var outR = targetR + (((r - lum) * 218) >> 8);
            var outG = targetG + (((g - lum) * 218) >> 8);
            var outB = targetB + (((b - lum) * 218) >> 8);

            pixels[index] = (byte)Math.Clamp(outB, 0, 255);
            pixels[index + 1] = (byte)Math.Clamp(outG, 0, 255);
            pixels[index + 2] = (byte)Math.Clamp(outR, 0, 255);
        }
    }
}
