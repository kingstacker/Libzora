using Avalonia;
using Libzora.Core;
using Xunit;

namespace Libzora.Tests;

public sealed class PdfViewerRefinementsTests
{
    [Fact]
    public void DetectContentCropBoxTrimsWideWhiteMarginsWithPadding()
    {
        const int w = 200;
        const int h = 200;
        var pixels = new byte[w * h * 4];
        Array.Fill(pixels, (byte)255); // pure white page
        // Draw dark text block inside x:[40..160], y:[50..150] (normalized 0.2..0.8, 0.25..0.75)
        for (var y = 50; y < 150; y++)
        {
            for (var x = 40; x < 160; x++)
            {
                var idx = (y * w + x) * 4;
                pixels[idx] = 20;
                pixels[idx + 1] = 20;
                pixels[idx + 2] = 20;
                pixels[idx + 3] = 255;
            }
        }

        var crop = NativePdfReaderHost.DetectContentCropBox(pixels, w, h);
        Assert.NotNull(crop);
        Assert.InRange(crop!.Value.X, 0.15, 0.20);
        Assert.InRange(crop.Value.Y, 0.20, 0.25);
        Assert.InRange(crop.Value.Right, 0.80, 0.85);
        Assert.InRange(crop.Value.Bottom, 0.75, 0.80);
    }

    [Fact]
    public void DetectContentCropBoxReturnsNullForBlankPage()
    {
        var pixels = new byte[100 * 100 * 4];
        Array.Fill(pixels, (byte)255);
        Assert.Null(NativePdfReaderHost.DetectContentCropBox(pixels, 100, 100));
    }

    [Fact]
    public void PdfReaderLayoutWithCropBoxEnlargesContentWithoutAddingHorizontalOverflow()
    {
        Size[] sizes = [new(600, 800)];
        var viewport = new Size(390, 844);
        var crop = new Rect(0.15, 0.10, 0.70, 0.80);

        var uncropped = new PdfReaderLayout(sizes, viewport, 1, PdfReaderDisplayMode.SinglePage, PdfReaderFitMode.Page, 1.0, 2.0);
        var cropped = new PdfReaderLayout(sizes, viewport, 1, PdfReaderDisplayMode.SinglePage, PdfReaderFitMode.Page, 1.0, 2.0, 0, [crop]);

        // Full page rect grows larger (content appears zoomed in), while the visible
        // cropped frame fits cleanly inside the viewport width.
        Assert.True(cropped.Pages[0].Width > uncropped.Pages[0].Width * 1.2);
        Assert.True(cropped.Frames[0].Width <= viewport.Width - PdfReaderLayout.Margin * 2 + 1);
        Assert.Equal(viewport.Width, cropped.Extent.Width, 1);
    }

    [Fact]
    public void DarkThemePageColoringInvertsWhiteToPaperAndPreservesSmoothChroma()
    {
        var inkPalette = ReaderPalette.For(ReaderTheme.Ink);
        // 3 pixels: white background, black text, pale blue grid line (R=215, G=230, B=245)
        byte[] pixels =
        [
            255, 255, 255, 255, // BGRA white
            0, 0, 0, 255,       // BGRA black
            245, 230, 215, 255, // BGRA pale blue
        ];

        NativePdfReaderHost.ApplyPageColors(pixels, inkPalette);

        // White maps to dark paper
        Assert.Equal(inkPalette.Page.B, pixels[0]);
        Assert.Equal(inkPalette.Page.G, pixels[1]);
        Assert.Equal(inkPalette.Page.R, pixels[2]);
        // Black maps to light ink
        Assert.Equal(inkPalette.Ink.B, pixels[4]);
        Assert.Equal(inkPalette.Ink.G, pixels[5]);
        Assert.Equal(inkPalette.Ink.R, pixels[6]);
        // Pale blue becomes dark blue (not jarring bright cyan) while keeping B > R hue
        Assert.True(pixels[8] > pixels[10], $"Expected blue hue preserved: B={pixels[8]}, R={pixels[10]}");
        Assert.True(pixels[8] < 80, $"Expected pale blue background inverted to dark tone, got B={pixels[8]}");
    }

    [Fact]
    public void PureBlackOledPaletteUsesZeroLuminanceBackgroundForInkTheme()
    {
        var normalInk = ReaderPalette.For(ReaderTheme.Ink, pureBlackOled: false);
        var oledInk = ReaderPalette.For(ReaderTheme.Ink, pureBlackOled: true);
        Assert.True(normalInk.Page.R > 0);
        Assert.Equal((byte)0, oledInk.Page.R);
        Assert.Equal((byte)0, oledInk.Page.G);
        Assert.Equal((byte)0, oledInk.Page.B);
        // Light themes remain unchanged under power saving
        Assert.Equal(ReaderPalette.For(ReaderTheme.Classic), ReaderPalette.For(ReaderTheme.Classic, pureBlackOled: true));
    }
}
