using System.Globalization;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Cell building, vertical cells and hyphenation splits.
/// </summary>
internal sealed partial class CellFactory
{
    /// <summary>
    /// Builds the cell sequence for one inline item, honoring the vertical
    /// inline-run policy (single digits upright, two digits combined, Latin
    /// runs sideways) and splitting horizontal text into per-unit cells.
    /// </summary>
    public List<LayoutCell> BuildCells(
        InlineItem item,
        bool vertical,
        float maxWordWidth = float.MaxValue,
        bool preferLatinFont = false)
    {
        var cells = new List<LayoutCell>();

        if (item.Kind == InlineKind.LineBreak)
        {
            cells.Add(new LayoutCell
            {
                Glyphs = Array.Empty<ushort>(),
                GlyphX = Array.Empty<float>(),
                GlyphY = Array.Empty<float>(),
                Advance = 0f,
                FontPath = _context.MainFont,
                FontSize = _context.Options.BaseFontSize,
                TextStart = item.TextStart,
                TextLength = 0,
                Style = item.Style,
                LinkHref = item.LinkHref,
                FootnoteHref = item.FootnoteHref,
                IsLineBreak = true,
            });
            return cells;
        }

        if (item.Kind == InlineKind.Image)
        {
            if (item.ImagePath is null || !File.Exists(item.ImagePath))
            {
                return cells;
            }

            using var codec = SKCodec.Create(item.ImagePath);
            var info = codec?.Info;
            if (info is null || info.Value.Width <= 0 || info.Value.Height <= 0)
            {
                return cells;
            }

            // Inline formula/glyph images specify a font-relative height.
            // Preserve that metric instead of treating source pixels as a
            // block image size. A conservative cap keeps malformed/oversized
            // assets from escaping the content box in either mode.
            var hasWidthConstraint = item.ImageWidthFactor is > 0f;
            var requestedHeight = _context.Options.BaseFontSize
                * Math.Clamp(item.ImageHeightEm ?? 1.5f, 0.5f, 8f);
            var scale = hasWidthConstraint
                ? Math.Min(
                    _context.Options.ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                    item.DecorativeQuote ? _context.Options.BaseFontSize * 5f : float.MaxValue)
                    / info.Value.Width
                : requestedHeight / info.Value.Height;
            var maxWidth = vertical
                ? _context.Options.ContentHeight
                : _context.Options.ContentWidth;
            scale = Math.Min(scale, maxWidth / info.Value.Width);
            if (!hasWidthConstraint)
            {
                scale = Math.Min(scale, 1f);
            }
            if (vertical)
            {
                // A vertical inline image is rotated like an existing sideways
                // Latin run. Its unrotated height becomes the cross-column
                // width, so keep it comfortably within one vertical column.
                scale = Math.Min(
                    scale,
                    (_context.Options.BodyLineHeight * 0.90f) / info.Value.Height);
            }

            var width = Math.Max(1f, info.Value.Width * scale);
            var height = Math.Max(1f, info.Value.Height * scale);
            cells.Add(new LayoutCell
            {
                Glyphs = Array.Empty<ushort>(),
                GlyphX = Array.Empty<float>(),
                GlyphY = Array.Empty<float>(),
                Advance = width,
                Ascent = height * 0.84f,
                Descent = height * 0.16f,
                FontPath = _context.MainFont,
                FontSize = _context.Options.BaseFontSize,
                TextStart = item.TextStart,
                TextLength = 0,
                Style = item.Style,
                LinkHref = item.LinkHref,
                FootnoteHref = item.FootnoteHref,
                FootnoteText = item.FootnoteText,
                ImagePath = item.ImagePath,
                ImageWidth = width,
                ImageHeight = height,
                ImageSideways = vertical,
            });
            return cells;
        }

        if (item.Kind == InlineKind.FootnoteMarker || item.FootnoteHref is not null)
        {
            var marker = item.Text.Length > 0 ? item.Text : "注";
            var markerStyle = item.Style with
            {
                Superscript = true,
                NoWrap = true,
            };
            var cell = ShapeCell(
                marker,
                item.TextStart,
                markerStyle,
                item.LinkHref,
                item.FootnoteHref,
                vertical,
                item.FootnoteText,
                footnoteMarker: true);
            cells.Add(cell with
            {
                FootnoteMarker = true,
                // In vertical writing a reference such as "[3]" is a
                // tate-chu-yoko unit: one compact horizontal marker centered
                // in one vertical cell, rather than three separate rows.
                Combined = vertical,
            });
            return cells;
        }

        var isRuby = item.Kind == InlineKind.Ruby;
        if (item.Kind is not (InlineKind.Text or InlineKind.Ruby)
            || item.Text.Length == 0
            || item.Ghost)
        {
            return cells;
        }

        if (vertical)
        {
            AppendVerticalCells(cells, item);
        }
        else if (item.Style.PreserveWhitespace)
        {
            AppendPreformattedCells(cells, item, maxWordWidth, preferLatinFont);
        }
        else
        {
            var units = TypesetText.Itemize(item.Text, 0, item.Text.Length);
            foreach (var unit in units)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                if (unit.Kind == TextUnitKind.Space && cells.Count > 0 && cells[^1].IsSpace)
                {
                    continue; // collapse consecutive whitespace
                }

                var unitText = item.Text.Substring(unit.Start, unit.Length);
                var produced = ShapeTextUnit(
                    unitText,
                    item.TextStart + unit.Start,
                    item.Style,
                    item.LinkHref,
                    item.FootnoteHref,
                    preferLatinFont,
                    item.FootnoteText);
                var wordLimit = Math.Min(_context.Options.ContentWidth, maxWordWidth);
                if (unit.Kind == TextUnitKind.LatinWord
                    && unitText.Length > 1
                    && produced.Count == 1
                    && produced[0].Advance > wordLimit + 0.01f)
                {
                    // A URL or an unspaced Latin token can be wider than the
                    // entire content box. Keep ordinary words atomic, but split
                    // this exceptional case into source-preserving glyph units so
                    // one cell can never force a line past the right edge.
                    for (var offset = 0; offset < unitText.Length;)
                    {
                        var element = StringInfo.GetNextTextElement(unitText, offset);
                        cells.Add(ShapeCell(
                            element,
                            item.TextStart + unit.Start + offset,
                            item.Style,
                            item.LinkHref,
                            item.FootnoteHref,
                            vertical: false,
                            footnoteText: item.FootnoteText,
                            preferLatinFont: preferLatinFont));
                        offset += element.Length;
                    }
                }
                else
                {
                    foreach (var cell in produced)
                    {
                        cells.Add(cell with { IsSpace = unit.Kind == TextUnitKind.Space });
                    }
                }
            }
        }

        // Itemization can split synthetic text into several cells. Advancing
        // a -1 sentinel by a character offset must never create a body range.
        if (item.TextStart < 0)
        {
            for (var index = 0; index < cells.Count; index++)
                cells[index] = cells[index] with { TextStart = item.TextStart };
        }

        if (isRuby && cells.Count > 0 && !string.IsNullOrWhiteSpace(item.RubyText))
        {
            var baseCell = cells[0] with { RubyText = item.RubyText };
            var annotation = ShapeRubyCell(baseCell);
            cells[0] = baseCell with
            {
                RubyAnnotation = annotation,
                RubyBaseAdvance = baseCell.Advance - baseCell.TrailingLetterSpacing,
                // Line fitting must see the full annotation width before it
                // wraps or justifies the text. Vertical flow reserves height.
                Advance = vertical
                    ? baseCell.Advance
                    : Math.Max(baseCell.Advance - baseCell.TrailingLetterSpacing, annotation.Advance + 2f * _context.RubyGap)
                        + baseCell.TrailingLetterSpacing,
            };
        }

        return cells;
    }

    /// <summary>
    /// Collapses whitespace that was split into separate XHTML text nodes by
    /// inline markup. The retained cell keeps the first source range, while
    /// later collapsed ranges remain in BodyText for offset compatibility.
    /// </summary>
    public static void CollapseWhitespace(List<LayoutCell> cells, bool attachFootnoteMarkers = false)
    {
        var previousWasSpace = false;
        var write = 0;
        for (var read = 0; read < cells.Count; read++)
        {
            var cell = cells[read];
            if (attachFootnoteMarkers && cell.FootnoteMarker)
            {
                // XHTML indentation before a superscript is collapsible
                // whitespace. Keep it in BodyText for source offsets only.
                while (write > 0 && cells[write - 1] is { IsSpace: true, IsLineBreak: false, Style.PreserveWhitespace: false })
                    write--;

                if (write > 0 && cells[write - 1] is { TrailingLetterSpacing: > 0f } preceding)
                {
                    cells[write - 1] = preceding with
                    {
                        Advance = preceding.Advance - preceding.TrailingLetterSpacing,
                        TrailingLetterSpacing = 0f,
                    };
                }
            }
            if (cell.Style.PreserveWhitespace)
            {
                previousWasSpace = false;
                cells[write++] = cell;
                continue;
            }
            if (cell.IsSpace && !cell.IsLineBreak && previousWasSpace) continue;
            previousWasSpace = cell.IsSpace && !cell.IsLineBreak;
            cells[write++] = cell;
        }
        if (write < cells.Count) cells.RemoveRange(write, cells.Count - write);
    }

    private void AppendPreformattedCells(
        List<LayoutCell> cells,
        InlineItem item,
        float maxWordWidth,
        bool preferLatinFont)
    {
        var text = item.Text;
        var index = 0;
        var wordLimit = Math.Min(_context.Options.ContentWidth, maxWordWidth);
        while (index < text.Length)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var c = text[index];
            if (c == '\r')
            {
                var step = index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                cells.Add(new LayoutCell
                {
                    Glyphs = Array.Empty<ushort>(),
                    GlyphX = Array.Empty<float>(),
                    GlyphY = Array.Empty<float>(),
                    Advance = 0f,
                    FontPath = _context.MainFont,
                    FontSize = _context.Options.BaseFontSize,
                    TextStart = item.TextStart + index,
                    TextLength = step,
                    Style = item.Style,
                    LinkHref = item.LinkHref,
                    FootnoteHref = item.FootnoteHref,
                    IsLineBreak = true,
                });
                index += step;
                continue;
            }

            if (c == '\n')
            {
                cells.Add(new LayoutCell
                {
                    Glyphs = Array.Empty<ushort>(),
                    GlyphX = Array.Empty<float>(),
                    GlyphY = Array.Empty<float>(),
                    Advance = 0f,
                    FontPath = _context.MainFont,
                    FontSize = _context.Options.BaseFontSize,
                    TextStart = item.TextStart + index,
                    TextLength = 1,
                    Style = item.Style,
                    LinkHref = item.LinkHref,
                    FootnoteHref = item.FootnoteHref,
                    IsLineBreak = true,
                });
                index++;
                continue;
            }

            if (c is ' ' or '\t' or ' ')
            {
                var cell = ShapeCell(
                    text.Substring(index, 1),
                    item.TextStart + index,
                    item.Style,
                    item.LinkHref,
                    item.FootnoteHref,
                    vertical: false,
                    preferLatinFont: preferLatinFont);
                cells.Add(cell with { IsSpace = false }); // preserve leading/consecutive spaces
                index++;
                continue;
            }

            // Collect contiguous non-whitespace run on this preformatted line
            var end = index + 1;
            while (end < text.Length && text[end] is not ('\r' or '\n' or ' ' or '\t' or ' '))
            {
                end++;
            }

            var runText = text[index..end];
            var produced = ShapeTextUnit(
                runText,
                item.TextStart + index,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                preferLatinFont);
            if (runText.Length > 1 && produced.Count == 1 && produced[0].Advance > wordLimit + 0.01f)
            {
                for (var offset = 0; offset < runText.Length;)
                {
                    var element = StringInfo.GetNextTextElement(runText, offset);
                    cells.Add(ShapeCell(
                        element,
                        item.TextStart + index + offset,
                        item.Style,
                        item.LinkHref,
                        item.FootnoteHref,
                        vertical: false,
                        preferLatinFont: preferLatinFont));
                    offset += element.Length;
                }
            }
            else
            {
                cells.AddRange(produced);
            }

            index = end;
        }
    }

    private void AppendVerticalCells(List<LayoutCell> cells, InlineItem item)
    {
        var units = VerticalTextUnits.Tokenize(
            item.Text,
            item.Style.VerticalTextCombineLimit ?? 2,
            item.Style.VerticalTextOrientation ?? TypesetVerticalOrientation.Mixed);
        var previousWasSpace = cells.Count > 0 && cells[^1].IsSpace;
        var previousWasLineBreak = cells.Count > 0 && cells[^1].IsLineBreak;
        foreach (var unit in units)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (unit.IsLineBreak)
            {
                cells.Add(new LayoutCell
                {
                    Glyphs = Array.Empty<ushort>(),
                    GlyphX = Array.Empty<float>(),
                    GlyphY = Array.Empty<float>(),
                    Advance = 0f,
                    FontPath = _context.MainFont,
                    FontSize = _context.Options.BaseFontSize,
                    TextStart = item.TextStart + unit.Offset,
                    TextLength = 0,
                    Style = item.Style,
                    LinkHref = item.LinkHref,
                    FootnoteHref = item.FootnoteHref,
                    FootnoteText = item.FootnoteText,
                    IsLineBreak = true,
                });
                previousWasSpace = false;
                previousWasLineBreak = true;
                continue;
            }

            var unitText = item.Text.Substring(unit.Offset, unit.Length);
            var isSpace = unitText.All(TypesetText.IsSpace);
            if (isSpace)
            {
                // XHTML source formatting is normally collapsed whitespace.
                // Do not let indentation/newlines become visible empty cells
                // at the start of a paragraph or after an explicit break.
                if (previousWasSpace || previousWasLineBreak)
                {
                    previousWasSpace = true;
                    continue;
                }
            }

            var cell = ShapeCell(
                unitText,
                item.TextStart + unit.Offset,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true,
                footnoteText: item.FootnoteText);
            cell = cell with
            {
                IsSpace = isSpace,
                Sideways = unit.IsSidewaysRun,
                Combined = unit.IsCombined,
            };
            if (cell.Sideways && cell.Advance > _context.Options.ContentHeight + 0.01f)
            {
                // A publication can contain an unspaced URL, identifier or
                // formula longer than one vertical column. Split only at text
                // element boundaries, retaining the original source offsets;
                // a long run must never be clipped or make pagination stall.
                cells.AddRange(SplitOversizedSidewaysCell(item, unitText, unit, cell));
            }
            else
            {
                cells.Add(cell);
            }
            previousWasSpace = isSpace;
            previousWasLineBreak = false;
        }
    }

    /// <summary>
    /// Splits one Latin word at an English typographic break when the prefix
    /// plus a visible hyphen fits the remaining line width. Source offsets stay
    /// exact; the hyphen is display-only and never enters the text stream.
    /// </summary>
    public bool TrySplitForHyphenation(
        LayoutCell cell,
        float availableWidth,
        out LayoutCell head,
        out LayoutCell tail,
        bool preferLatinFont = false)
    {
        head = cell;
        tail = cell;
        if (!_context.Options.HyphenateEnglish
            || !cell.IsLatinWord
            || cell.SyntheticHyphen
            || cell.TextStart < 0
            || cell.TextLength < 5
            || cell.Text.Length != cell.TextLength)
        {
            return false;
        }

        var breakpoints = EnglishHyphenator.GetBreakpointsForToken(cell.Text);
        if (breakpoints.Length == 0)
        {
            return false;
        }

        foreach (var breakPoint in breakpoints.OrderDescending())
        {
            var shaped = ShapeCell(
                cell.Text[..breakPoint] + "-",
                cell.TextStart,
                cell.Style,
                cell.LinkHref,
                cell.FootnoteHref,
                vertical: false,
                preferLatinFont: preferLatinFont);
            if (shaped.Advance > availableWidth + 0.01f)
            {
                continue;
            }

            // The hyphen is synthetic: map its cluster to the last source
            // letter so hit-testing and selection never address the suffix.
            var clusters = shaped.Clusters.ToArray();
            if (clusters.Length > 0 && clusters[^1] >= breakPoint)
            {
                clusters[^1] = breakPoint - 1;
            }

            head = shaped with
            {
                Text = cell.Text[..breakPoint] + "-",
                TextLength = breakPoint,
                Clusters = clusters,
                IsLatinWord = true,
                SyntheticHyphen = true,
            };
            tail = ShapeCell(
                cell.Text[breakPoint..],
                cell.TextStart + breakPoint,
                cell.Style,
                cell.LinkHref,
                cell.FootnoteHref,
                vertical: false,
                preferLatinFont: preferLatinFont) with
            {
                IsLatinWord = true,
            };
            return true;
        }

        return false;
    }

    private List<LayoutCell> SplitOversizedSidewaysCell(
        InlineItem item,
        string unitText,
        VerticalTextUnits.Unit unit,
        LayoutCell original)
    {
        var result = new List<LayoutCell>();
        var starts = StringInfo.ParseCombiningCharacters(unitText);
        if (starts.Length == 0)
        {
            return [original];
        }

        var chunkStart = 0;
        for (var elementIndex = 0; elementIndex < starts.Length; elementIndex++)
        {
            var elementStart = starts[elementIndex];
            var elementEnd = elementIndex + 1 < starts.Length
                ? starts[elementIndex + 1]
                : unitText.Length;
            var candidateText = unitText[chunkStart..elementEnd];
            var candidate = ShapeCell(
                candidateText,
                item.TextStart + unit.Offset + chunkStart,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true,
                footnoteText: item.FootnoteText) with
            {
                Sideways = true,
            };

            if (candidate.Advance > _context.Options.ContentHeight + 0.01f
                && elementStart > chunkStart)
            {
                result.Add(ShapeCell(
                    unitText[chunkStart..elementStart],
                    item.TextStart + unit.Offset + chunkStart,
                    item.Style,
                    item.LinkHref,
                    item.FootnoteHref,
                    vertical: true,
                    footnoteText: item.FootnoteText) with
                {
                    Sideways = true,
                });
                chunkStart = elementStart;
                candidate = ShapeCell(
                    unitText[chunkStart..elementEnd],
                    item.TextStart + unit.Offset + chunkStart,
                    item.Style,
                    item.LinkHref,
                    item.FootnoteHref,
                    vertical: true,
                    footnoteText: item.FootnoteText) with
                {
                    Sideways = true,
                };
            }

            if (elementEnd == unitText.Length)
            {
                result.Add(candidate);
                chunkStart = elementEnd;
            }
        }

        if (result.Count == 0 || chunkStart < unitText.Length)
        {
            result.Add(ShapeCell(
                unitText[chunkStart..],
                item.TextStart + unit.Offset + chunkStart,
                item.Style,
                item.LinkHref,
                item.FootnoteHref,
                vertical: true,
                footnoteText: item.FootnoteText) with
            {
                Sideways = true,
            });
        }

        return result;
    }

    private float GetApostropheAdvance(string fontPath, float fontSize, ushort glyph)
    {
        var key = (fontPath, fontSize, glyph);
        if (_apostropheAdvanceCache.TryGetValue(key, out var cached)) return cached;
        using var font = new SKFont(_context.Fonts.GetTypeface(fontPath), fontSize);
        Span<ushort> glyphs = stackalloc ushort[1];
        glyphs[0] = glyph;
        _ = font.GetGlyphWidths(glyphs, out var bounds, null);
        var advance = Math.Max(fontSize * 0.3f, bounds[0].Right + fontSize * 0.06f);
        _apostropheAdvanceCache[key] = advance;
        return advance;
    }

    private (float Ascent, float Descent) GetMetrics(string fontPath, float fontSize)
    {
        var key = (fontPath, fontSize);
        if (_metricsCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var font = new SKFont(_context.Fonts.GetTypeface(fontPath), fontSize);
        var metrics = font.Metrics;
        var value = (-metrics.Ascent, metrics.Descent);
        _metricsCache[key] = value;
        return value;
    }
}
