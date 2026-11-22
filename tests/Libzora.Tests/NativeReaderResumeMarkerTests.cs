using Libzora.Layout;
using SkiaSharp;

namespace Libzora.Tests;

public sealed class NativeReaderResumeMarkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypesetPainter_RendersResumeMarker_ForTextAndImagesAndChangesPixels(bool vertical)
    {
        using var fonts = new TypesetFontLibrary(FindBundledFont());
        var painter = new TypesetPainter(fonts, TypesetPaintTheme.Paper);
        var page = new LayoutPage
        {
            Index = 0,
            Width = 600,
            Height = 800,
            InsetHorizontal = 48,
            InsetVertical = 48,
            WritingMode = vertical ? TypesetWritingMode.VerticalRl : TypesetWritingMode.HorizontalTb,
            TextStartOffset = 0,
            TextEndOffset = 50,
            Runs = [],
            Images = [],
            Decorations = [],
            HotZones = [],
        };

        byte[] Render(TypesetResumeMarker? marker)
        {
            using var bitmap = new SKBitmap(600, 800);
            using var canvas = new SKCanvas(bitmap);
            painter.Paint(canvas, page, resumeMarker: marker);
            return bitmap.Bytes;
        }

        var baseline = Render(null);
        var withTextMarker = Render(new TypesetResumeMarker(
            new SKRect(120, 200, 138, 220),
            [new SKRect(120, 200, 260, 220)],
            IsImage: false));
        var withImageMarker = Render(new TypesetResumeMarker(
            new SKRect(60, 80, 540, 720),
            IsImage: true));

        Assert.False(baseline.AsSpan().SequenceEqual(withTextMarker));
        Assert.False(baseline.AsSpan().SequenceEqual(withImageMarker));
    }

    private static string FindBundledFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src", "Libzora.App", "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Bundled KingHwaOldSong font not found.");
    }
}
