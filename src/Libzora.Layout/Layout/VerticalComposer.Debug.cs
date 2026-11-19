using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Debug glyph boxes and ink bounds.
/// </summary>
internal sealed partial class VerticalComposer
{
    private void PlaceRubyAnnotation(
        LayoutCell baseCell,
        float columnLeft,
        float paintY,
        float flowAdvance)
    {
        var annotation = baseCell.RubyAnnotation!;
        if (annotation.Glyphs.Length == 0)
        {
            return;
        }

        _pages.AddRun(new PlacedRun
        {
            FontPath = annotation.FontPath,
            FontSize = annotation.FontSize,
            Glyphs = annotation.Glyphs,
            X = annotation.GlyphX,
            Y = annotation.GlyphY,
            // Ruby in vertical writing is a compact sideways run placed on
            // the reading side of the base glyph.
            OriginX = columnLeft + BodyColumnWidth + _context.RubyGap + annotation.Descent,
            OriginY = paintY + Math.Max(0f, (flowAdvance - annotation.Advance) / 2f),
            FlowAdvance = annotation.Advance,
            Sideways = true,
            TextStart = -1,
            TextLength = 0,
            Clusters = Array.Empty<int>(),
            Style = annotation.Style,
        });
    }

    private static bool IsCompatibilityCell(LayoutCell cell)
    {
        if (cell.ImagePath is not null || cell.Sideways || cell.Combined)
        {
            return true;
        }

        return !string.IsNullOrEmpty(cell.Text)
            && (cell.Text.Any(char.IsAsciiLetterOrDigit)
                || TypesetText.ShouldRotateInVertical(cell.Text));
    }

    private void AddDebugGlyphBox(LayoutCell cell, PlacedRun run)
    {
        if (!_context.Options.CollectDebugBoxes) return;
        if (run.Glyphs.Length == 0 || run.X.Length == 0)
        {
            return;
        }

        var scale = run.Scale;
        var ascent = Math.Max(0.5f, cell.Ascent * scale);
        var descent = Math.Max(0.5f, cell.Descent * scale);
        SKRect rect;
        if (run.Sideways)
        {
            // Rotating a horizontal run maps its font metrics to the
            // cross-flow width while its shaped advance becomes the vertical
            // extent. This frame encloses the actual painted run, not the
            // integer grid cell that used to create the visible gap.
            rect = new SKRect(
                run.OriginX - descent,
                run.OriginY,
                run.OriginX + ascent,
                run.OriginY + Math.Max(1f, run.FlowAdvance));
        }
        else
        {
            var left = run.OriginX + run.X.Min();
            var width = Math.Max(1f, cell.Advance * scale);
            rect = new SKRect(
                left,
                run.OriginY - ascent,
                left + width,
                run.OriginY + descent);
        }

        _pages.AddDebugBox(new TypesetDebugBox(rect, TypesetDebugBoxKind.Glyph));
    }

    /// <summary>
    /// Returns the painted ink bounds relative to the cell's glyph origin.
    /// HarfBuzz's horizontal cell metrics are not enough after a vertical
    /// glyph substitution: U+3007 is a useful example because its outline is
    /// slightly taller and sits differently from the font-wide ascent box.
    /// </summary>
}
