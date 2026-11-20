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
        var startsAtPageTop = block.PageBreakBefore
            || (isHeading
                && !followsHeading
                && (!string.IsNullOrWhiteSpace(block.ElementId)
                    || block.FragmentIds.Any(id => !block.FootnoteReferenceFragmentIds.Contains(id))));
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
            cells.AddRange(_cells.BuildCells(item, vertical: false, maxWordWidth: availWidth, preferLatinFont: naturalLatin));
        }
        CellFactory.CollapseWhitespace(cells);
        if (_context.Options.MangaMode
            && cells.All(cell => cell.ImagePath is null && (cell.IsSpace || cell.IsLineBreak)))
        {
            return;
        }
        if (naturalLatin)
        {
            // Some CJK faces use a half-em space even in English. Start with
            // a proportional gap, then share modest expansion across the line.
            for (var index = 0; index < cells.Count; index++)
            {
                var cell = cells[index];
                if (cell.IsSpace && !cell.Style.PreserveWhitespace)
                    cells[index] = cell with { Advance = Math.Min(cell.Advance, cell.FontSize * 0.32f) };
            }
        }
        ApplyHorizontalMicroTypography(cells, naturalLatin);
        // Measure after Latin whitespace normalization so the continuation
        // aligns under the actual first-line body, including marker spacing.
        var hangingIndent = block.ListMarker is null ? 0f : cells
            .TakeWhile(cell => cell.TextStart < 0)
            .Sum(cell => cell.Advance);
        var rubyGap = _context.RubyGap;

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

        // True float:left drop cap: extract an initial oversized floated letter
        // so the first 2-3 lines wrap beside it instead of pushing them down.
        LayoutCell? floatedDropCap = null;
        var dropCapWidth = 0f;
        var dropCapLines = 0;
        if (!isHeading
            && !block.Center
            && !block.AlignRight
            && block.ListMarker is null
            && cells.Count >= 2)
        {
            var firstNonSpace = cells.FindIndex(c => !c.IsSpace && !c.IsLineBreak);
            if (firstNonSpace >= 0)
            {
                var candidate = cells[firstNonSpace];
                if (candidate.Style.FloatLeft
                    && candidate.Style.FontSizeFactor >= 1.5f
                    && candidate.ImagePath is null
                    && candidate.TextLength is > 0 and <= 2
                    && candidate.Advance < availWidth * 0.4f)
                {
                    floatedDropCap = candidate;
                    cells.RemoveAt(firstNonSpace);
                    dropCapWidth = candidate.Advance + BaseFontSize * 0.25f;
                    dropCapLines = Math.Clamp((int)Math.Ceiling((candidate.Ascent + candidate.Descent) / bodyLineHeight), 2, 4);
                }
            }
        }

        // Adapt default CJK 2em indentation for Latin prose: keep the first
        // paragraph after a heading flush left, and use a calmer 1.25em indent
        // on subsequent paragraphs unless publisher CSS explicitly set one.
        var effectiveIndentEm = floatedDropCap is not null ? 0f : block.TextIndentEm;
        if (naturalLatin && !block.ExplicitTextIndent && effectiveIndentEm > 0f)
        {
            effectiveIndentEm = followsHeading ? 0f : Math.Min(effectiveIndentEm, 1.25f);
        }

        List<LineBox> lines;
        if (CanUseKnuthPlass(cells, naturalLatin)
            && TryBreakLinesKnuthPlass(cells, availWidth, effectiveIndentEm * BaseFontSize,
                hangingIndent, preferLatinFont: true, dropCapWidth, dropCapLines) is { } optimalLines)
        {
            lines = optimalLines;
        }
        else
        {
            lines = BreakLines(cells, availWidth, effectiveIndentEm * BaseFontSize,
                allowPageAnchor: true, continuationIndent: hangingIndent, preferLatinFont: naturalLatin,
                dropCapWidth: dropCapWidth, dropCapLines: dropCapLines);
        }
        var anchorLineIndex = lines.FindIndex(line => line.StartsAnchoredPage);

        // Compute line height per line rather than taking the paragraph maximum:
        // a single drop-cap, ruby annotation or inline graphic only expands the
        // line it sits on instead of blowing up every line in the paragraph.
        var lineHeights = new float[lines.Count];
        var lineRubyShelves = new float[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            (lineHeights[i], lineRubyShelves[i]) = ComputeLineMetrics(
                lines[i], block, isHeading, bodyLineHeight, rubyGap);
        }

        // Keep-with-next: avoid stranding a subheading (or break-after: avoid
        // block) at the bottom of a page without room for its following text.
        if (block.KeepWithNext && !startsAtPageTop && _pages.CurrentPageHasContent && lineHeights.Length > 0)
        {
            var blockHeight = 0f;
            for (var i = 0; i < lineHeights.Length; i++) blockHeight += lineHeights[i];
            var afterReserve = block.SpaceAfterLines * BodyLineHeight
                + block.MarginBottomEm * BaseFontSize
                + bodyLineHeight * 1.8f;
            if (_cursorY + blockHeight + afterReserve > ContentBottom + 0.01f
                && blockHeight + afterReserve <= _context.Options.ContentHeight)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        // Keep a floated drop cap together with the lines wrapped beside it.
        if (floatedDropCap is not null && _pages.CurrentPageHasContent && lineHeights.Length > 0)
        {
            var dropHeight = 0f;
            var spanCount = Math.Min(dropCapLines, lineHeights.Length);
            for (var i = 0; i < spanCount; i++) dropHeight += lineHeights[i];
            dropHeight = Math.Max(dropHeight, floatedDropCap.Ascent + floatedDropCap.Descent);
            if (_cursorY + dropHeight > ContentBottom + 0.01f)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        var dropCapPlaced = false;
        var lineIndex = 0;
        while (lineIndex < lines.Count)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (lines[lineIndex].StartsAnchoredPage)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
            if (_cursorY + lineHeights[lineIndex] > ContentBottom + 0.01f)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }

            var capacity = CountFittingLines(lineHeights, lineIndex, _cursorY, ContentBottom);
            if (capacity <= 0)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
                capacity = Math.Max(1, CountFittingLines(lineHeights, lineIndex, _cursorY, ContentBottom));
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

            var pageSliceTop = _cursorY;
            if (floatedDropCap is not null && !dropCapPlaced && lineIndex == 0)
            {
                dropCapPlaced = true;
                var firstLineHeight = lineHeights[0];
                var firstBaseline = _cursorY
                    + firstLineHeight / 2f
                    + (lines[0].Ascent - lines[0].Descent) / 2f;
                var capTop = firstBaseline - lines[0].Ascent;
                var capBaseline = capTop + floatedDropCap.Ascent;
                var dropLine = new LineBox();
                dropLine.Add(floatedDropCap);
                PlaceLine(dropLine, blockLeft, capBaseline, capTop, floatedDropCap.Ascent + floatedDropCap.Descent, rubyGap);
            }

            for (var placed = 0; placed < capacity; placed++, lineIndex++)
            {
                var line = lines[lineIndex];
                var lineHeight = lineHeights[lineIndex];
                var rubyShelf = lineRubyShelves[lineIndex];
                if (lineIndex == 0)
                {
                    _pages.RecordFragment(block.ElementId);
                    _pages.RecordFragments(block.FragmentIds);
                }

                var baseline = _cursorY
                    + lineHeight / 2f
                    + (line.Ascent - line.Descent) / 2f
                    + rubyShelf / 2f;

                var lineAvail = Math.Max(BaseFontSize * 2f, availWidth - line.FloatLeftInset);
                var startX = blockLeft + line.FloatLeftInset + line.Indent;
                var isLastLine = lineIndex == lines.Count - 1 || line.EndsWithForcedBreak;
                if (isHeading || block.Center)
                {
                    startX = blockLeft + line.FloatLeftInset + Math.Max(0f, (lineAvail - line.Used) / 2f);
                }
                else if (block.AlignRight)
                {
                    startX = blockLeft + line.FloatLeftInset + Math.Max(0f, lineAvail - line.Used);
                }
                else if (block.Justify && !_context.Options.AlignLeft && !isLastLine)
                {
                    var extra = lineAvail - line.Indent - line.Used;
                    if (extra > 0f || line.HasHangingPunctuation || line.OpticalTrailingProtrusion > 0f)
                    {
                        line.TryJustify(extra, BaseFontSize, naturalLatin);
                    }
                }

                PlaceLine(line, startX, baseline, _cursorY, lineHeight, rubyGap);
                _cursorY += lineHeight;
            }

            if (block.BorderLeftEm > 0f && _cursorY > pageSliceTop)
            {
                var barWidth = Math.Clamp(block.BorderLeftEm * BaseFontSize, 2f, 5f);
                var barX = Math.Max(InsetH, blockLeft - Math.Max(barWidth + 4f, BaseFontSize * 0.6f));
                _pages.AddDecoration(new PlacedRect
                {
                    Kind = DecorationKind.BlockquoteBar,
                    Rect = new SKRect(barX, pageSliceTop + 2f, barX + barWidth, _cursorY - 2f),
                });
            }

            if (lineIndex < lines.Count)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        // The last line already advanced the cursor by one line height. In
        // compact mode, consecutive paragraphs need no additional gap.
        if (block.PageBreakAfter)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV;
        }
        else if (block.Kind == BlockKind.Paragraph && precedesParagraph
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
                || current.ImagePath is not null || next.ImagePath is not null
                || current.FootnoteMarker || next.FootnoteMarker)
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
