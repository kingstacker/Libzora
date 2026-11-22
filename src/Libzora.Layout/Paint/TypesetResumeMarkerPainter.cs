using SkiaSharp;

namespace Libzora.Layout;

internal static class TypesetResumeMarkerPainter
{
    public static SKColor TintColor(SKColor background) =>
        IsDark(background)
            ? new SKColor(0xF5, 0x9E, 0x0B, 42)
            : new SKColor(0xD9, 0x77, 0x06, 36);

    public static SKColor AccentColor(SKColor background) =>
        IsDark(background)
            ? new SKColor(0xF5, 0x9E, 0x0B, 225)
            : new SKColor(0xD9, 0x77, 0x06, 225);

    public static void PaintTint(SKCanvas canvas, TypesetResumeMarker marker, SKColor background)
    {
        if (marker.HighlightBands is not { Count: > 0 } bands)
        {
            return;
        }

        using var tint = new SKPaint
        {
            Color = TintColor(background),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        foreach (var band in bands)
        {
            if (band.Width > 0 && band.Height > 0)
            {
                canvas.DrawRoundRect(band, 2.5f, 2.5f, tint);
            }
        }
    }

    public static void PaintIndicator(
        SKCanvas canvas,
        LayoutPage page,
        TypesetResumeMarker marker,
        SKColor background)
    {
        var accent = AccentColor(background);
        using var fill = new SKPaint
        {
            Color = accent,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

        if (marker.IsImage)
        {
            PaintRibbon(canvas, page, marker.AnchorRect, fill, background);
            return;
        }

        var anchor = marker.AnchorRect;
        if (anchor.Width <= 0 || anchor.Height <= 0)
        {
            return;
        }

        if (page.WritingMode == TypesetWritingMode.VerticalRl)
        {
            var barY = Math.Clamp(anchor.Top - 4f, 2f, Math.Max(2f, page.Height - 6f));
            var barLeft = Math.Max(2f, anchor.Left - 1f);
            var barRight = Math.Min(page.Width - 2f, anchor.Right + 1f);
            if (barRight <= barLeft) return;

            canvas.DrawRoundRect(new SKRect(barLeft, barY, barRight, barY + 2.2f), 1.1f, 1.1f, fill);
            using var nib = new SKPath();
            nib.MoveTo(barRight, barY - 2f);
            nib.LineTo(barRight + 4f, barY + 1.1f);
            nib.LineTo(barRight, barY + 4.2f);
            nib.Close();
            canvas.DrawPath(nib, fill);
            return;
        }

        var barX = Math.Clamp(anchor.Left - 4f, 2f, Math.Max(2f, page.Width - 6f));
        var barTop = Math.Max(2f, anchor.Top - 1f);
        var barBottom = Math.Min(page.Height - 2f, anchor.Bottom + 1f);
        if (barBottom <= barTop) return;

        canvas.DrawRoundRect(new SKRect(barX, barTop, barX + 2.2f, barBottom), 1.1f, 1.1f, fill);
        using var head = new SKPath();
        head.MoveTo(barX - 2f, barTop);
        head.LineTo(barX + 4.2f, barTop);
        head.LineTo(barX + 1.1f, barTop + 4f);
        head.Close();
        canvas.DrawPath(head, fill);
    }

    private static void PaintRibbon(
        SKCanvas canvas,
        LayoutPage page,
        SKRect imageRect,
        SKPaint fill,
        SKColor background)
    {
        const float width = 14f;
        const float height = 22f;
        var x = page.WritingMode == TypesetWritingMode.VerticalRl
            ? Math.Clamp(imageRect.Right - width - 10f, 4f, Math.Max(4f, page.Width - width - 4f))
            : Math.Clamp(imageRect.Left + 10f, 4f, Math.Max(4f, page.Width - width - 4f));
        var y = Math.Clamp(imageRect.Top, 2f, Math.Max(2f, page.Height - height - 2f));
        using var path = new SKPath();
        path.MoveTo(x, y);
        path.LineTo(x + width, y);
        path.LineTo(x + width, y + height);
        path.LineTo(x + width * 0.5f, y + height - 5f);
        path.LineTo(x, y + height);
        path.Close();
        canvas.DrawPath(path, fill);

        using var border = new SKPaint
        {
            Color = background.WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        };
        canvas.DrawPath(path, border);
    }

    private static bool IsDark(SKColor color) =>
        (0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue) < 128f;
}
