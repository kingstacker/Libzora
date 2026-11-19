using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Cell advances and compatibility handling.
/// </summary>
internal sealed partial class VerticalComposer
{
    private SKRect? GetUprightGlyphInkBounds(LayoutCell cell)
    {
        if (cell.Glyphs.Length == 0)
        {
            return null;
        }

        var missing = false;
        foreach (var glyphId in cell.Glyphs)
        {
            if (!_glyphBoundsCache.ContainsKey((cell.FontPath, cell.FontSize, glyphId)))
            {
                missing = true;
                break;
            }
        }

        if (missing)
        {
            using var font = new SKFont(_context.Fonts.GetTypeface(cell.FontPath), cell.FontSize);
            _ = font.GetGlyphWidths(cell.Glyphs.AsSpan(), out var rawBounds, null);
            for (var index = 0; index < cell.Glyphs.Length && index < rawBounds.Length; index++)
            {
                _glyphBoundsCache[(cell.FontPath, cell.FontSize, cell.Glyphs[index])] = rawBounds[index];
            }
        }

        var hasInk = false;
        var union = SKRect.Empty;
        for (var index = 0; index < cell.Glyphs.Length; index++)
        {
            if (!_glyphBoundsCache.TryGetValue(
                    (cell.FontPath, cell.FontSize, cell.Glyphs[index]),
                    out var glyphBounds)
                || glyphBounds.Width <= 0f
                || glyphBounds.Height <= 0f)
            {
                continue;
            }

            var positioned = new SKRect(
                glyphBounds.Left + (index < cell.GlyphX.Length ? cell.GlyphX[index] : 0f),
                glyphBounds.Top + (index < cell.GlyphY.Length ? cell.GlyphY[index] : 0f),
                glyphBounds.Right + (index < cell.GlyphX.Length ? cell.GlyphX[index] : 0f),
                glyphBounds.Bottom + (index < cell.GlyphY.Length ? cell.GlyphY[index] : 0f));
            if (!hasInk)
            {
                union = positioned;
                hasInk = true;
                continue;
            }

            union = new SKRect(
                Math.Min(union.Left, positioned.Left),
                Math.Min(union.Top, positioned.Top),
                Math.Max(union.Right, positioned.Right),
                Math.Max(union.Bottom, positioned.Bottom));
        }

        return hasInk ? union : null;
    }

    private static float[] Zeroed(int length)
    {
        var values = new float[length];
        return values;
    }

    private int[] CloneClusters(LayoutCell cell)
    {
        if (cell.Clusters.Length == 0)
        {
            return Array.Empty<int>();
        }

        var clusters = new int[cell.Clusters.Length];
        Array.Copy(cell.Clusters, clusters, clusters.Length);
        return clusters;
    }

    private float CellFlowAdvance(LayoutCell cell)
    {
        if (cell.IsLineBreak)
        {
            return 0f;
        }

        var rubyAdvance = cell.RubyAnnotation is { } ruby
            ? ruby.Advance + 2f * _context.RubyGap
            : 0f;
        if (cell.ImagePath is not null || cell.Sideways)
        {
            // A rotated run is already measured in the same physical units as
            // the canvas. Reserve that measured extent instead of rounding it
            // up to whole CJK rows; the rounded remainder was the blank gap
            // visible before the following upright character.
            return Math.Max(Math.Max(CellPitch, cell.Advance), rubyAdvance);
        }

        return Math.Max(CellPitch, rubyAdvance);
    }

    /// <summary>
    /// Rewrites upright cells' glyph ids to the font's vertical presentation
    /// forms (OpenType vert). Combined and sideways cells keep their
    /// horizontal shaping.
    /// </summary>
    private void ResolveUprightGlyphs(List<LayoutCell> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var cell = cells[i];
            if (cell.IsLineBreak
                || cell.Sideways
                || cell.Combined
                || cell.Glyphs.Length == 0
                || string.IsNullOrEmpty(cell.Text))
            {
                continue;
            }

            var rotate = _context.Shaper.NeedsVerticalRotation(cell.Text, cell.FontPath);
            if (rotate)
            {
                cells[i] = cell with
                {
                    VerticalRotation = rotate,
                    Sideways = rotate,
                };
                continue;
            }

            var vertical = _context.Shaper.Shape(
                cell.Text,
                0,
                cell.Text.Length,
                cell.FontPath,
                cell.FontSize,
                vertical: true);
            if (!ContainsNotdef(vertical))
            {
                // Keep the original horizontal cross-flow metrics, but use
                // the complete text element's vertical glyph sequence. This
                // allows vert/vrt2 substitutions for base-plus-selector and
                // combining clusters without permitting an internal break.
                cells[i] = cell with { Glyphs = vertical.GlyphIds };
            }
        }
    }

    private static bool ContainsNotdef(ShapedText shaped) =>
        shaped.GlyphIds.Any(glyph => glyph == 0);

    private void EnsureColumnSpace()
    {
        if (_cursorX - ColumnPitch < ContentLeft - 0.01f)
        {
            _pages.NextIfUsed();
            _cursorX = ContentRight;
        }
    }
}
