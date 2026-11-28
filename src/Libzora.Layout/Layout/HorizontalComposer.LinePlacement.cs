using System.Text;
using SkiaSharp;

namespace Libzora.Layout;

internal sealed partial class HorizontalComposer
{
    private void PlaceLine(
        LineBox line,
        float startX,
        float baseline,
        float lineTop,
        float lineHeight,
        float rubyGap)
    {
        float penX = startX;
        RunAccumulator? run = null;

        for (var cellIndex = 0; cellIndex < line.Cells.Count; cellIndex++)
        {
            var cell = line.Cells[cellIndex];
            if (cellIndex == line.Cells.Count - 1 && cell.TrailingLetterSpacing != 0f)
                cell = cell with { Advance = cell.Advance - cell.TrailingLetterSpacing, TrailingLetterSpacing = 0f };
            if (cell.ImagePath is not null)
            {
                run?.Flush(_pages);
                run = null;

                var imageWidth = Math.Max(1f, cell.ImageWidth);
                var imageHeight = Math.Max(1f, cell.ImageHeight);
                var imageRect = new SKRect(
                    penX,
                    lineTop + Math.Max(0f, (lineHeight - imageHeight) / 2f),
                    penX + imageWidth,
                    lineTop + Math.Max(0f, (lineHeight - imageHeight) / 2f) + imageHeight);
                _pages.AddImage(new PlacedImage
                {
                    Path = cell.ImagePath,
                    Rect = imageRect,
                    LinkHref = cell.LinkHref,
                    FootnoteHref = cell.FootnoteHref,
                    FootnoteText = cell.FootnoteText,
                });

                if (cell.LinkHref is not null || cell.FootnoteHref is not null)
                {
                    _pages.AddHotZone(new PlacedHotZone
                    {
                        Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                        Rect = imageRect,
                        Href = cell.FootnoteHref ?? cell.LinkHref!,
                        FootnoteText = cell.FootnoteText,
                        Marker = cell.FootnoteHref is not null ? cell.Text : null,
                    });
                }

                penX += cell.Advance;
                continue;
            }

            var superscriptShift = cell.Superscript
                ? -(cell.FootnoteMarker
                    ? BaseFontSize * 0.38f
                    : Math.Max(BaseFontSize * 0.38f, cell.FontSize * 0.45f))
                : 0f;
            var subscriptShift = cell.Subscript ? cell.FontSize * 0.22f : 0f;
            var cellBaseline = baseline + superscriptShift + subscriptShift;

            if (run is null || !run.CanAppend(cell, cellBaseline))
            {
                run?.Flush(_pages);
                run = new RunAccumulator(cellBaseline);
            }

            var baseInset = cell.RubyLeadingPadding;
            run.BeginCell(cell, baseInset);
            for (var i = 0; i < cell.Glyphs.Length; i++)
            {
                var cluster = i < cell.Clusters.Length ? cell.Clusters[i] : i;
                run.AddGlyph(
                    cell.Glyphs[i],
                    penX + baseInset + cell.GlyphX[i],
                    cell.GlyphY[i],
                    cluster,
                    cell.GlyphInkShiftX is { } shifts && i < shifts.Length ? shifts[i] : 0f);
            }

            if (cell.LinkHref is not null || cell.FootnoteHref is not null)
            {
                _pages.AddHotZone(new PlacedHotZone
                {
                    Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                    Rect = new SKRect(penX, lineTop, penX + Math.Max(cell.Advance, 4f), lineTop + lineHeight),
                    Href = cell.FootnoteHref ?? cell.LinkHref!,
                    FootnoteText = cell.FootnoteText,
                    Marker = cell.FootnoteHref is not null ? cell.Text : null,
                });
            }

            if (cell.Style.Underline && cell.TextLength > 0)
            {
                var underlineY = cellBaseline + cell.FontSize * 0.14f;
                _pages.AddDecoration(new PlacedRect
                {
                    Kind = DecorationKind.Underline,
                    Rect = new SKRect(penX, underlineY, penX + cell.Advance, underlineY + 1.2f),
                    TextStart = cell.TextStart,
                    TextLength = cell.TextLength,
                });
            }

            if (cell.Style.Strikeout && cell.TextLength > 0)
            {
                var strikeY = cellBaseline - cell.FontSize * 0.25f;
                _pages.AddDecoration(new PlacedRect
                {
                    Kind = DecorationKind.Strikeout,
                    Rect = new SKRect(penX, strikeY, penX + cell.Advance, strikeY + 1.2f),
                    TextStart = cell.TextStart,
                    TextLength = cell.TextLength,
                });
            }

            if (cell.RubyAnnotation is not null)
            {
                run?.Flush(_pages);
                run = null;
                PlaceRubyAnnotation(cell, penX, baseline, rubyGap);
            }

            penX += cell.Advance;
        }

        run?.Flush(_pages);
    }

    private void PlaceRubyAnnotation(
        LayoutCell baseCell,
        float baseLeft,
        float baseline,
        float rubyGap)
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
            // Justification adds space after this unit. It must not move the
            // annotation away from the base's original center.
            OriginX = baseLeft
                + (Math.Max(baseCell.RubyBaseAdvance, annotation.Advance + 2f * rubyGap) - annotation.Advance) / 2f,
            OriginY = baseline - baseCell.Ascent - annotation.Descent - rubyGap,
            FlowAdvance = annotation.Advance,
            TextStart = -1,
            TextLength = 0,
            Clusters = Array.Empty<int>(),
            Style = annotation.Style,
        });
    }

    private (float LineHeight, float RubyShelf) ComputeLineMetrics(
        LineBox line,
        ContentBlock block,
        bool isHeading,
        float bodyLineHeight,
        float rubyGap)
    {
        var fontScale = 1f;
        var inkExtent = 0f;
        var maxImageHeight = 0f;
        var rubyShelf = 0f;
        foreach (var cell in line.Cells)
        {
            var scale = cell.Style.FontSizeFactor > 0f ? cell.Style.FontSizeFactor : 1f;
            if (scale > fontScale) fontScale = scale;
            var extent = cell.Ascent + cell.Descent;
            if (extent > inkExtent) inkExtent = extent;
            if (cell.ImagePath is not null && cell.ImageHeight > maxImageHeight)
            {
                maxImageHeight = cell.ImageHeight;
            }
            if (cell.RubyAnnotation is { } ruby)
            {
                var shelf = ruby.Ascent + ruby.Descent + rubyGap;
                if (shelf > rubyShelf) rubyShelf = shelf;
            }
        }

        fontScale = Math.Clamp(fontScale, 0.5f, 3f);
        var lineHeight = block.LineHeightFactor > 0f
            && !(_context.Options.OverridePublisherLineHeight && !isHeading)
            ? Math.Max(BaseFontSize * fontScale * block.LineHeightFactor, inkExtent + 1f)
            : isHeading
                ? Math.Max(BaseFontSize * fontScale * 1.35f, inkExtent + 1f)
                : Math.Max(bodyLineHeight * fontScale, inkExtent);
        if (maxImageHeight > lineHeight)
        {
            lineHeight = maxImageHeight;
        }
        if (rubyShelf > 0f)
        {
            // Move the base line down by the full annotation shelf so larger
            // ruby annotations never collide with the base glyphs.
            lineHeight += rubyShelf;
        }

        return (lineHeight, rubyShelf);
    }

    private static int CountFittingLines(float[] lineHeights, int startIndex, float cursorY, float contentBottom)
    {
        var y = cursorY;
        var count = 0;
        for (var i = startIndex; i < lineHeights.Length; i++)
        {
            if (y + lineHeights[i] > contentBottom + 0.01f)
            {
                break;
            }
            y += lineHeights[i];
            count++;
        }
        return count;
    }

    private void ApplyHorizontalMicroTypography(List<LayoutCell> cells, bool naturalLatin)
    {
        if (cells.Count < 2)
        {
            return;
        }

        var cjkLatinGap = _context.Options.CjkLatinSpacing && !naturalLatin;
        for (var i = 0; i < cells.Count - 1; i++)
        {
            var current = cells[i];
            var next = cells[i + 1];
            if (current.IsLineBreak || next.IsLineBreak
                || current.IsSpace || next.IsSpace
                || current.Style.PreserveWhitespace || next.Style.PreserveWhitespace
                || KeepCellsTogether(current, next)
                || current.ImagePath is not null || next.ImagePath is not null)
            {
                continue;
            }

            // Fullwidth closing punctuation (。，、；：！？）」』》】〕) carries
            // ~0.5em built-in trailing whitespace; trim part of it before an
            // attached footnote marker so the marker sits snugly next to the mark.
            if (next.FootnoteMarker)
            {
                if (!current.FootnoteMarker
                    && current.RubyAnnotation is null && !current.ContinuesRubyBase
                    && current.GlyphInkShiftX is null
                    && TypesetText.IsCompressibleClosingPunctuation(current.Text))
                {
                    var squeeze = Math.Min(current.Advance - current.FontSize * 0.5f, current.Advance * 0.36f);
                    if (squeeze > 0.5f)
                    {
                        cells[i] = current with
                        {
                            Advance = current.Advance - squeeze,
                        };
                    }
                }
                continue;
            }

            if (current.FootnoteMarker)
            {
                continue;
            }

            // Give an inline opening fullwidth mark (already squeezed by 0.5em)
            // a slight breathing gap after a plain Han character, while keeping
            // adjacent punctuation pairs ("，“", "。“") tightly kerned.
            if (next.GlyphInkShiftX is not null
                && IsCjkIdeographCell(current)
                && TypesetText.IsCompressibleOpeningPunctuation(next.Text))
            {
                var breath = Math.Min(current.FontSize, next.FontSize) * 0.12f;
                cells[i] = current with
                {
                    Advance = current.Advance + breath,
                    TrailingLetterSpacing = current.TrailingLetterSpacing + breath,
                };
                continue;
            }

            // Automatic CJK-Latin/numeric spacing (approx 1/6 em) stored in
            // TrailingLetterSpacing so a line break between them never leaves
            // trailing whitespace at the right margin.
            if (cjkLatinGap
                && ((IsCjkIdeographCell(current) && IsLatinOrDigitCell(next))
                    || (IsLatinOrDigitCell(current) && IsCjkIdeographCell(next))))
            {
                var gap = Math.Min(current.FontSize, next.FontSize) * 0.16f;
                cells[i] = current with
                {
                    Advance = current.Advance + gap,
                    TrailingLetterSpacing = current.TrailingLetterSpacing + gap,
                };
            }
        }
    }

    private static bool IsCjkIdeographCell(LayoutCell cell)
    {
        if (cell.Text.Length == 0 || cell.IsLatinWord)
        {
            return false;
        }

        if (!TypesetText.TryGetFirstScalar(cell.Text, out var scalar))
        {
            return false;
        }

        return TypesetText.IsCjk(scalar) && !TypesetText.IsPunctuation(cell.Text);
    }

    private static bool IsLatinOrDigitCell(LayoutCell cell)
    {
        if (cell.IsLatinWord)
        {
            return true;
        }

        return cell.Character is { } c && (TypesetText.IsLatinLetter(c) || char.IsAsciiDigit(c));
    }
}
