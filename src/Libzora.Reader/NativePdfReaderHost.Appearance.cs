using Avalonia;
using Libzora.Core;
using Libzora.Layout;

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
        return ImageCropDetector.DetectNormalizedCropBox(pixels, width, height) is { } crop
            ? new Rect(crop.Left, crop.Top, crop.Width, crop.Height)
            : null;
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
