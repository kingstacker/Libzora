using System.Text;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Lays a chapter out as horizontally flowing, vertically stacked pages:
/// blocks advance downward, lines wrap left-to-right, page breaks fall
/// between line boxes. CJK justification expands inter-character gaps,
/// while embedded Latin words retain their shaped internal spacing.
/// Closing punctuation hangs into the trailing margin; opening punctuation
/// never ends a line.
/// </summary>

internal sealed partial class HorizontalComposer
{
    private readonly ComposerContext _context;
    private readonly CellFactory _cells;
    private readonly PageBuilder _pages;
    private float _cursorY;

    public HorizontalComposer(ComposerContext context, CellFactory cells, PageBuilder pages)
    {
        _context = context;
        _cells = cells;
        _pages = pages;
        _cursorY = context.Options.InsetVertical;
    }

    private float ContentWidth => _context.Options.ContentWidth;

    private float ContentBottom => _context.Options.InsetVertical + _context.Options.ContentHeight;

    private float BodyLineHeight => _context.Options.BodyLineHeight;

    private float BaseFontSize => _context.Options.BaseFontSize;

    private float InsetH => _context.Options.InsetHorizontal;

    private float InsetV => _context.Options.InsetVertical;

    public void Compose(ChapterContent content)
    {
        var previousBlockWasHeading = false;
        var previousBlockWasParagraph = false;
        var blockIndex = 0;
        foreach (var block in content.Blocks)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            switch (block.Kind)
            {
                case BlockKind.Image:
                    PlaceImageBlock(block);
                    break;
                case BlockKind.Rule:
                    PlaceRule(block);
                    break;
                case BlockKind.Table:
                    PlaceTable(block);
                    break;
                default:
                    PlaceTextBlock(block, previousBlockWasHeading, previousBlockWasParagraph,
                        blockIndex + 1 < content.Blocks.Count && content.Blocks[blockIndex + 1].Kind == BlockKind.Paragraph);
                    break;
            }

            previousBlockWasHeading = block.Kind == BlockKind.Heading;
            previousBlockWasParagraph = block.Kind == BlockKind.Paragraph;
            blockIndex++;
        }
    }

    private static bool IsLatinProse(ContentBlock block)
    {
        var latin = 0;
        var cjk = 0;
        foreach (var item in block.Items)
        {
            if (item.Ghost || item.Kind is not (InlineKind.Text or InlineKind.Ruby)) continue;
            foreach (var rune in item.Text.EnumerateRunes())
            {
                if (rune.Value <= char.MaxValue && TypesetText.IsLatinLetter((char)rune.Value)) latin++;
                else if (TypesetText.IsCjk(rune.Value) && Rune.IsLetter(rune)) cjk++;
            }
        }
        // Proper names in an English paragraph do not change its rhythm;
        // a Chinese paragraph containing an English name remains CJK prose.
        return latin >= 4 && latin >= cjk * 4;
    }

    private void PlaceTextBlock(ContentBlock block, bool followsHeading, bool followsParagraph = false, bool precedesParagraph = false)
    {
        var isHeading = block.Kind == BlockKind.Heading;
        var naturalLatin = _context.Options.NaturalLatinTypography
            && (block.Kind is BlockKind.Paragraph or BlockKind.Blockquote or BlockKind.ListItem)
            && !block.Center && !block.AlignRight && IsLatinProse(block);
        var bodyLineHeight = naturalLatin
            ? BaseFontSize * Math.Max(1.4f, _context.Options.LineHeight - 0.2f)
            : BodyLineHeight;
        // A small paragraph gap distinguishes unindented English prose even
        // in compact mode; larger user-selected gaps and indentation remain.
        var paragraphSpacing = naturalLatin && !_context.Options.ParagraphIndent && block.TextIndentEm <= 0f
            ? Math.Max(0.35f, _context.Options.ParagraphSpacingEm)
            : _context.Options.ParagraphSpacingEm;
        var startsAtPageTop = isHeading
            && !followsHeading
            && (!string.IsNullOrWhiteSpace(block.ElementId)
                || block.FragmentIds.Any(id => !block.FootnoteReferenceFragmentIds.Contains(id)));
        float leftInset = 0f;
        float rightInset = 0f;
        if (block.Kind == BlockKind.Blockquote)
        {
            leftInset = BaseFontSize * 1.1f + 3f;
            rightInset = BaseFontSize * 1.1f;
        }

        // Publisher margins and padding narrow the text column. They are
        // stored in em so the reader's font scale keeps the proportions.
        leftInset += (block.MarginLeftEm + block.PaddingLeftEm) * BaseFontSize;
        rightInset += (block.MarginRightEm + block.PaddingRightEm) * BaseFontSize;
        if (block.ListLevel > 0)
            leftInset += block.ListLevel * BaseFontSize * 1.4f;

        var availWidth = Math.Max(1f, ContentWidth - leftInset - rightInset);
        var blockLeft = InsetH + leftInset;

        var cells = new List<LayoutCell>();
        foreach (var item in block.Items)
        {
            cells.AddRange(_cells.BuildCells(item, vertical: false, maxWordWidth: availWidth));
        }
        CellFactory.CollapseWhitespace(cells);
        if (naturalLatin)
        {
            // Some CJK faces use a half-em space even in English. Start with
            // a proportional gap, then share modest expansion across the line.
            for (var index = 0; index < cells.Count; index++)
            {
                var cell = cells[index];
                if (cell.IsSpace)
                    cells[index] = cell with { Advance = Math.Min(cell.Advance, cell.FontSize * 0.32f) };
            }
        }
        // Measure after Latin whitespace normalization so the continuation
        // aligns under the actual first-line body, including marker spacing.
        var hangingIndent = block.ListMarker is null ? 0f : cells
            .TakeWhile(cell => cell.TextStart < 0)
            .Sum(cell => cell.Advance);
        // A publisher line-height replaces the reader default for this block,
        // but never below the glyphs' own ink extent (or they would overlap).
        // Publisher font-size scales the pitch so an enlarged span or heading
        // keeps its proportion.
        var fontScale = Math.Clamp(
            cells.Select(cell => cell.Style.FontSizeFactor > 0f ? cell.Style.FontSizeFactor : 1f)
                .DefaultIfEmpty(1f)
                .Max(),
            0.5f,
            3f);
        var inkExtent = cells
            .Select(cell => cell.Ascent + cell.Descent)
            .DefaultIfEmpty(0f)
            .Max();
        var lineHeight = block.LineHeightFactor > 0f
            && !(_context.Options.OverridePublisherLineHeight && !isHeading)
            ? Math.Max(BaseFontSize * fontScale * block.LineHeightFactor, inkExtent + 1f)
            : isHeading
                ? Math.Max(BaseFontSize * fontScale * 1.35f, inkExtent + 1f)
                : Math.Max(bodyLineHeight * fontScale, inkExtent);
        lineHeight = Math.Max(lineHeight, cells
            .Where(cell => cell.ImagePath is not null)
            .Select(cell => cell.ImageHeight)
            .DefaultIfEmpty(0f)
            .Max());
        var rubyGap = _context.RubyGap;
        var rubyShelf = cells
            .Where(cell => cell.RubyAnnotation is not null)
            .Select(cell => cell.RubyAnnotation!.Ascent + cell.RubyAnnotation.Descent + rubyGap)
            .DefaultIfEmpty(0f)
            .Max();
        if (rubyShelf > 0f)
        {
            // Move the base line down by the full annotation shelf. Merely
            // increasing lineHeight leaves the baseline centered in the old
            // position, which makes the larger ruby overlap the base text.
            lineHeight += rubyShelf;
        }

        if (startsAtPageTop)
        {
            // TOC headings are fragment targets. A page that merely contains
            // the heading is not enough: the target must be the first visible
            // line after a click, just like scrollIntoView({ block: "start" })
            // in the old document reader.
            _pages.NextIfUsed();
            _cursorY = InsetV;
        }
        else if (!(block.Kind == BlockKind.Paragraph && followsParagraph
            && (_context.Options.ParagraphSpacingMatchesLineHeight || _context.Options.OverridePublisherParagraphSpacing)))
        {
            _cursorY += block.SpaceBeforeLines * BodyLineHeight
                + block.MarginTopEm * BaseFontSize;
        }

        var lines = BreakLines(cells, availWidth, block.TextIndentEm * BaseFontSize,
            allowPageAnchor: true, continuationIndent: hangingIndent);
        var anchorLineIndex = lines.FindIndex(line => line.StartsAnchoredPage);

        var lineIndex = 0;
        while (lineIndex < lines.Count)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (lines[lineIndex].StartsAnchoredPage)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
            if (_cursorY + lineHeight > ContentBottom + 0.01f)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }

            var capacity = (int)Math.Floor((ContentBottom + 0.01f - _cursorY) / lineHeight);
            if (capacity <= 0)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
                capacity = Math.Max(
                    1,
                    (int)Math.Floor((ContentBottom + 0.01f - _cursorY) / lineHeight));
            }

            var remaining = lines.Count - lineIndex;
            if (anchorLineIndex > lineIndex) capacity = Math.Min(capacity, anchorLineIndex - lineIndex);
            if (capacity >= remaining)
            {
                capacity = remaining;
            }
            else
            {
                // Widow: a single line stranded at the bottom of a page is
                // moved with its paragraph to the next page when possible.
                if (_context.Options.AvoidWidowsAndOrphans && capacity == 1
                    && remaining > 1
                    && _pages.CurrentPageHasContent)
                {
                    _pages.NextIfUsed();
                    _cursorY = InsetV;
                    continue;
                }

                // Orphan: leave at least two lines for the following page
                // instead of a single line starting a new page. When only two
                // lines fit, reducing further would create a widow instead.
                if (_context.Options.AvoidWidowsAndOrphans && capacity >= 3 && remaining - capacity == 1)
                {
                    capacity--;
                }
            }

            for (var placed = 0; placed < capacity; placed++, lineIndex++)
            {
                var line = lines[lineIndex];
                if (lineIndex == 0)
                {
                    _pages.RecordFragment(block.ElementId);
                    _pages.RecordFragments(block.FragmentIds);
                }

                var baseline = _cursorY
                    + lineHeight / 2f
                    + (line.Ascent - line.Descent) / 2f
                    + rubyShelf / 2f;

                var startX = blockLeft + line.Indent;
                var isLastLine = lineIndex == lines.Count - 1 || line.EndsWithForcedBreak;
                if (isHeading || block.Center)
                {
                    startX = blockLeft + Math.Max(0f, (availWidth - line.Used) / 2f);
                }
                else if (block.AlignRight)
                {
                    startX = blockLeft + Math.Max(0f, availWidth - line.Used);
                }
                else if (block.Justify && !isLastLine)
                {
                    var extra = availWidth - line.Indent - line.Used;
                    if (extra > 0f)
                    {
                        line.TryJustify(extra, BaseFontSize, naturalLatin);
                    }
                }

                PlaceLine(line, startX, baseline, _cursorY, lineHeight, rubyGap);
                _cursorY += lineHeight;
            }

            if (lineIndex < lines.Count)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        // The last line already advanced the cursor by one line height. In
        // compact mode, consecutive paragraphs need no additional gap.
        if (block.Kind == BlockKind.Paragraph && precedesParagraph
            && _context.Options.OverridePublisherParagraphSpacing)
        {
            _cursorY += paragraphSpacing * BaseFontSize;
        }
        else if (!(block.Kind == BlockKind.Paragraph && precedesParagraph
            && _context.Options.ParagraphSpacingMatchesLineHeight))
        {
            _cursorY += block.SpaceAfterLines * BodyLineHeight
                + block.MarginBottomEm * BaseFontSize;
            if (block.Kind == BlockKind.Paragraph)
                _cursorY += paragraphSpacing * BaseFontSize;
        }
    }

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
                ? -(cell.FootnoteMarker ? BaseFontSize * 0.38f : cell.FontSize * 0.35f)
                : 0f;
            var subscriptShift = cell.Subscript ? cell.FontSize * 0.22f : 0f;
            var cellBaseline = baseline + superscriptShift + subscriptShift;

            if (run is null || !run.CanAppend(cell, cellBaseline))
            {
                run?.Flush(_pages);
                run = new RunAccumulator(cellBaseline);
            }

            var baseInset = cell.RubyAnnotation is { } ruby
                ? (Math.Max(cell.RubyBaseAdvance, ruby.Advance + 2f * rubyGap) - cell.RubyBaseAdvance) / 2f
                : 0f;
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
}
