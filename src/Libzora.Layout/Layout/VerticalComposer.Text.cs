namespace Libzora.Layout;

/// <summary>
/// Text block placement and column filling.
/// </summary>
internal sealed partial class VerticalComposer
{
    private void PlaceTextBlock(ContentBlock block, bool followsHeading, bool followsParagraph, bool precedesParagraph)
    {
        var isHeading = block.Kind == BlockKind.Heading;
        var startsAtPageTop = isHeading
            && !followsHeading
            && (!string.IsNullOrWhiteSpace(block.ElementId)
                || block.FragmentIds.Any(id => !block.FootnoteReferenceFragmentIds.Contains(id)));

        float rightInset = 0f;
        if (block.Kind == BlockKind.Blockquote)
        {
            rightInset = _context.Options.BaseFontSize * 1.1f + 3f;
        }

        var cells = new List<LayoutCell>();
        foreach (var item in block.Items)
        {
            cells.AddRange(_cells.BuildCells(item, vertical: true));
        }
        CellFactory.CollapseWhitespace(cells);

        ResolveUprightGlyphs(cells);
        // Reuse the normal column spacing where possible, but reserve a
        // separate strip with a gap on both sides so annotations cannot touch
        // either neighboring column, even with platform-specific glyph hinting.
        _rubySideWidth = cells
            .Where(cell => cell.RubyAnnotation is not null)
            .Select(cell => cell.RubyAnnotation!.Ascent + cell.RubyAnnotation.Descent + 2f * _context.RubyGap)
            .DefaultIfEmpty(0f)
            .Max();
        _rubyBaseWidth = _rubySideWidth > 0f
            ? cells.Where(cell => cell.ImagePath is null)
                .Select(cell => Math.Max(cell.FontSize, GetUprightGlyphInkBounds(cell)?.Width ?? 0f))
                .DefaultIfEmpty(0f).Max()
            : 0f;

        if (startsAtPageTop)
        {
            // Fragment-backed headings are the destinations of TOC entries.
            // Start them in the first column of a fresh vertical page instead
            // of leaving earlier paragraph columns above the target.
            _pages.NextIfUsed();
            _cursorX = ContentRight;
        }
        else if (!(block.Kind == BlockKind.Paragraph && followsParagraph
            && (_context.Options.ParagraphSpacingMatchesLineHeight || _context.Options.OverridePublisherParagraphSpacing)))
        {
            _cursorX -= block.SpaceBeforeLines * ColumnPitch;
        }
        _cursorX -= rightInset;

        var index = 0;
        var firstColumnOfBlock = true;
        while (index < cells.Count)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (_context.StartsAnchoredPage(cells[index]))
            {
                _pages.NextIfUsed();
                _cursorX = ContentRight;
                _context.PageStartConsumed = true;
            }
            EnsureColumnSpace();
            if (firstColumnOfBlock)
            {
                _pages.RecordFragment(block.ElementId);
                _pages.RecordFragments(block.FragmentIds);
                firstColumnOfBlock = false;
            }

            var columnLeft = _cursorX - ColumnPitch;
            var colCenter = columnLeft + BodyColumnWidth / 2f;
            _lastColumnFirstRun = _pages.Current.Runs.Count;
            _lastColumnFirstDecoration = _pages.Current.Decorations.Count;
            _lastColumnFirstHotZone = _pages.Current.HotZones.Count;
            _lastColumnFirstImage = _pages.Current.Images.Count;
            _lastColumnFirstDebugBox = _pages.Current.DebugBoxes.Count;
            var (placed, flowUsed) = FillColumn(
                cells,
                index,
                colCenter,
                columnLeft,
                centerVertically: isHeading && !startsAtPageTop);

            index = placed;
            _cursorX -= ColumnPitch;
        }

        // Each column has already advanced the cursor by its pitch.
        if (block.Kind == BlockKind.Paragraph && precedesParagraph
            && _context.Options.OverridePublisherParagraphSpacing)
        {
            _cursorX -= _context.Options.ParagraphSpacingEm * _context.Options.BaseFontSize;
        }
        else if (!(block.Kind == BlockKind.Paragraph && precedesParagraph
            && _context.Options.ParagraphSpacingMatchesLineHeight))
        {
            _cursorX -= block.SpaceAfterLines * ColumnPitch;
            if (block.Kind == BlockKind.Paragraph)
                _cursorX -= _context.Options.ParagraphSpacingEm * _context.Options.BaseFontSize;
        }
    }

    /// <summary>
    /// Fills one column starting at <paramref name="start"/>. Returns the next
    /// unplaced index and the physical flow extent consumed.
    /// </summary>
    private (int Next, float FlowUsed) FillColumn(
        List<LayoutCell> cells,
        int start,
        float colCenter,
        float columnLeft,
        bool centerVertically)
    {
        var capacity = ContentHeight;
        var placements = new List<ColumnPlacement>();
        var flowUsed = 0f;
        var index = start;
        var forcedBreak = false;

        while (index < cells.Count)
        {
            var cell = cells[index];
            if (placements.Count > 0 && _context.StartsAnchoredPage(cell))
            {
                forcedBreak = true;
                break;
            }
            if (cell.IsLineBreak)
            {
                index++;
                forcedBreak = true;
                break;
            }

            if (cell.IsSpace && placements.Count == 0)
            {
                // A wrapped/collapsed whitespace run must not create an
                // otherwise empty leading row in the next vertical column.
                index++;
                continue;
            }

            var flowAdvance = CellFlowAdvance(cell);

            if (flowUsed + flowAdvance > capacity + 0.01f)
            {
                break;
            }

            placements.Add(new ColumnPlacement(
                cell,
                flowAdvance,
                placements.Count == 0 ? GetTopHang(cell) : 0f));
            flowUsed += flowAdvance;
            index++;
        }

        // The greedy fit above deliberately does not make a local decision at
        // every character. Resolve the actual boundary now, moving a complete
        // trailing cluster to the next column until both sides are legal. This
        // handles arbitrary runs of closers and numeric affixes instead of the
        // old four-character look-ahead heuristic.
        if (!forcedBreak)
        {
            while (placements.Count > 1 && index < cells.Count && !cells[index].IsLineBreak)
            {
                var last = placements[^1];
                var next = cells[index];
                if (!TypesetText.IsProhibitedAtLineEnd(last.Cell.Text)
                    && !TypesetText.IsProhibitedAtLineStart(next.Text)
                    && !TypesetText.ShouldKeepTogether(last.Cell.Text, next.Text))
                {
                    break;
                }

                placements.RemoveAt(placements.Count - 1);
                index--;
                flowUsed -= last.FlowAdvance;
            }
        }

        // A pathological unbreakable object can be wider than the whole
        // column. Always consume one cell so malformed EPUB content cannot
        // make pagination loop forever. Normal long Latin/numeric runs are
        // split into grapheme-safe cells by CellFactory before reaching here.
        if (placements.Count == 0 && index < cells.Count && !cells[index].IsLineBreak)
        {
            var cell = cells[index];
            var flowAdvance = Math.Min(Math.Max(CellFlowAdvance(cell), CellPitch), capacity);
            placements.Add(new ColumnPlacement(
                cell,
                flowAdvance,
                GetTopHang(cell)));
            flowUsed = flowAdvance;
            index++;
        }

        var y = ContentTop;
        foreach (var placement in placements)
        {
            PlaceCell(
                placement.Cell,
                colCenter,
                columnLeft,
                y,
                placement.FlowAdvance,
                placement.TopHang);
            y += placement.FlowAdvance;
        }

        if (centerVertically && flowUsed > 0f && flowUsed < capacity)
        {
            // Headings center within the column: shift the placed cells down
            // by half the leftover space.
            var shift = (capacity - flowUsed) / 2f;
            ShiftPlacedDown(shift);
        }

        return (index, flowUsed);
    }

    private int _lastColumnFirstRun;
    private int _lastColumnFirstDecoration;
    private int _lastColumnFirstHotZone;
    private int _lastColumnFirstImage;
    private int _lastColumnFirstDebugBox;
}
