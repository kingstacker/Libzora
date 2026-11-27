namespace Libzora.Layout;

/// <summary>
/// Paragraph-level Knuth-Plass optimal line breaking and optical margin
/// alignment for horizontal Latin prose.
/// </summary>
internal sealed partial class HorizontalComposer
{
    private readonly record struct KpBreak(
        int CellIndex,
        int NextCellIndex,
        int SplitOffset,
        bool IsHyphen,
        bool IsEnd);

    /// <summary>
    /// Computes an optical trailing protrusion for Latin lines ending with a
    /// hyphen or light punctuation so the right margin reads visually straight.
    /// </summary>
    private float ComputeOpticalTrailingProtrusion(LayoutCell lastCell)
    {
        if (!_context.Options.HangingPunctuation || InsetH <= 2f)
        {
            return 0f;
        }

        var maxProtrude = Math.Min(InsetH * 0.45f, BaseFontSize * 0.28f);
        if (lastCell.SyntheticHyphen
            || (lastCell.IsLatinWord && lastCell.Text.Length > 1 && lastCell.Text[^1] == '-'))
        {
            return Math.Min(maxProtrude, lastCell.FontSize * 0.22f);
        }

        if (lastCell.Character is ',' or '.' or '’' or '”')
        {
            return Math.Min(maxProtrude, lastCell.Advance * 0.5f);
        }

        return 0f;
    }

    private bool CanUseKnuthPlass(List<LayoutCell> cells, bool naturalLatin)
    {
        if (!naturalLatin || cells.Count < 6)
        {
            return false;
        }

        var spaceCount = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsLineBreak
                || cell.Style.PreserveWhitespace
                || cell.ImagePath is not null
                || cell.RubyAnnotation is not null
                || _context.StartsAnchoredPage(cell))
            {
                return false;
            }

            if (cell.IsSpace)
            {
                spaceCount++;
            }
        }

        return spaceCount >= 2;
    }

    /// <summary>
    /// Optimal paragraph line breaking (Knuth-Plass minimum-demerit DP) for
    /// horizontal English/Latin prose. Considers spaces, compound hyphens and
    /// optional English syllable hyphenation, balancing word spacing across the
    /// entire paragraph and avoiding consecutive hyphenated lines.
    /// Returns null if the paragraph contains an edge case better handled by
    /// the greedy breaker.
    /// </summary>
    private List<LineBox>? TryBreakLinesKnuthPlass(
        List<LayoutCell> cells,
        float availWidth,
        float firstLineIndent,
        float continuationIndent,
        bool preferLatinFont,
        float dropCapWidth = 0f,
        int dropCapLines = 0)
    {
        var start = 0;
        while (start < cells.Count && cells[start].IsSpace)
        {
            start++;
        }

        var end = cells.Count;
        while (end > start && cells[end - 1].IsSpace)
        {
            end--;
        }

        if (end - start < 4)
        {
            return null;
        }

        // Prefix sums of cell advances, space counts, exact stretch/shrink
        // headroom (matching LineBox.TryJustify), and micro-tracking gaps.
        var n = cells.Count;
        var maxSpaceWidth = BaseFontSize * 0.65f;
        var minSpaceWidth = BaseFontSize * 0.20f;
        var maxTrackPerGap = BaseFontSize * 0.045f;
        var prefixAdv = new float[n + 1];
        var prefixSpaces = new int[n + 1];
        var prefixStretchCap = new float[n + 1];
        var prefixShrinkCap = new float[n + 1];
        var prefixTrackGaps = new int[n + 1];
        for (var i = 0; i < n; i++)
        {
            var c = cells[i];
            prefixAdv[i + 1] = prefixAdv[i] + c.Advance;
            prefixSpaces[i + 1] = prefixSpaces[i] + (c.IsSpace ? 1 : 0);
            prefixStretchCap[i + 1] = prefixStretchCap[i]
                + (c.IsSpace ? Math.Max(0f, maxSpaceWidth - c.Advance) : 0f);
            prefixShrinkCap[i + 1] = prefixShrinkCap[i]
                + (c.IsSpace ? Math.Max(0f, c.Advance - minSpaceWidth) : 0f);
            prefixTrackGaps[i + 1] = prefixTrackGaps[i] + LineBox.CountMicroTrackGaps(c);
        }

        // Collect candidate break points: start sentinel, spaces, post-hyphen
        // compounds, English syllable splits, and paragraph end.
        var breaks = new List<KpBreak>(end - start + 8)
        {
            new(CellIndex: start - 1, NextCellIndex: start, SplitOffset: 0, IsHyphen: false, IsEnd: false),
        };

        var splitCache = new Dictionary<(int CellIndex, int SplitOffset), (LayoutCell Head, LayoutCell Tail)>();
        var hyphenate = _context.Options.HyphenateEnglish;

        for (var i = start; i < end; i++)
        {
            var cell = cells[i];
            if (cell.IsSpace)
            {
                if (i > start && !cells[i - 1].IsSpace)
                {
                    var next = i + 1;
                    while (next < end && cells[next].IsSpace) next++;
                    if (next < end && !cells[next].FootnoteMarker)
                    {
                        breaks.Add(new KpBreak(i - 1, next, 0, IsHyphen: false, IsEnd: false));
                    }
                }
                continue;
            }

            // Break after a hyphenated compound prefix ("well-" + "known"), or
            // after a standalone em-dash / en-dash / slash ("asked,—“morphine",
            // "and/or") while keeping paired Chinese dashes ("——") intact.
            if (i + 1 < end
                && !cells[i + 1].IsSpace
                && !cells[i + 1].FootnoteMarker
                && !IsClosingMark(cells[i + 1]))
            {
                if (cell.IsLatinWord
                    && cell.Text.Length > 1
                    && cell.Text[^1] is '-' or '–' or '—')
                {
                    breaks.Add(new KpBreak(i, i + 1, 0, IsHyphen: true, IsEnd: false));
                }
                else if (cell.Character is '—' or '–' or '/'
                    && !(cell.Character == '—' && cells[i + 1].Character == '—'))
                {
                    breaks.Add(new KpBreak(i, i + 1, 0, IsHyphen: false, IsEnd: false));
                }
            }

            // Syllable breakpoints inside long English words.
            if (hyphenate
                && cell.IsLatinWord
                && !cell.SyntheticHyphen
                && cell.TextStart >= 0
                && cell.TextLength >= 6
                && cell.Text.Length == cell.TextLength)
            {
                var points = EnglishHyphenator.GetBreakpointsForToken(cell.Text);
                foreach (var point in points)
                {
                    breaks.Add(new KpBreak(i, i, point, IsHyphen: true, IsEnd: false));
                }
            }
        }

        breaks.Add(new KpBreak(end - 1, end, 0, IsHyphen: false, IsEnd: true));

        var m = breaks.Count;
        // DP arrays per breakpoint: track best demerit, predecessor, line index
        // (for drop-cap / first-line width), fitness class (0..3), and whether
        // the incoming line ended with a hyphen.
        const double Inf = 1e18;
        var bestCost = new double[m];
        var prevIdx = new int[m];
        var lineNum = new int[m];
        var fitness = new int[m];
        Array.Fill(bestCost, Inf);
        Array.Fill(prevIdx, -1);
        bestCost[0] = 0.0;
        fitness[0] = 1;

        (LayoutCell Head, LayoutCell Tail) GetSplit(int cellIndex, int splitOffset)
        {
            var key = (cellIndex, splitOffset);
            if (splitCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var cell = cells[cellIndex];
            var shapedHead = _cells.ShapeCell(
                cell.Text[..splitOffset] + "-",
                cell.TextStart,
                cell.Style,
                cell.LinkHref,
                cell.FootnoteHref,
                vertical: false,
                preferLatinFont: preferLatinFont);
            var clusters = shapedHead.Clusters.ToArray();
            if (clusters.Length > 0 && clusters[^1] >= splitOffset)
            {
                clusters[^1] = splitOffset - 1;
            }

            var head = shapedHead with
            {
                Text = cell.Text[..splitOffset] + "-",
                TextLength = splitOffset,
                Clusters = clusters,
                IsLatinWord = true,
                SyntheticHyphen = true,
            };
            var tail = _cells.ShapeCell(
                cell.Text[splitOffset..],
                cell.TextStart + splitOffset,
                cell.Style,
                cell.LinkHref,
                cell.FootnoteHref,
                vertical: false,
                preferLatinFont: preferLatinFont) with
            {
                IsLatinWord = true,
            };
            var pair = (head, tail);
            splitCache[key] = pair;
            return pair;
        }

        for (var i = 0; i < m - 1; i++)
        {
            if (bestCost[i] >= Inf * 0.5)
            {
                continue;
            }

            var from = breaks[i];
            var curLine = lineNum[i];
            var floatInset = curLine < dropCapLines ? dropCapWidth : 0f;
            var indent = curLine == 0 ? firstLineIndent : continuationIndent;
            var lineTarget = Math.Max(BaseFontSize * 2f, availWidth - floatInset - indent);

            LayoutCell? startTail = null;
            if (from.SplitOffset > 0)
            {
                startTail = GetSplit(from.CellIndex, from.SplitOffset).Tail;
            }

            var foundFeasible = false;
            var firstOverflowJ = -1;

            for (var j = i + 1; j < m; j++)
            {
                var to = breaks[j];
                // Cannot split the same word at an earlier or equal offset.
                if (from.SplitOffset > 0 && to.CellIndex == from.CellIndex)
                {
                    if (to.SplitOffset > 0 && to.SplitOffset <= from.SplitOffset)
                    {
                        continue;
                    }
                    // Avoid double-hyphenating the same single word across two lines.
                    if (to.SplitOffset > from.SplitOffset)
                    {
                        continue;
                    }
                }

                // Measure natural width, space count, and real justification headroom on line (from -> to).
                float naturalWidth;
                int spaces;
                float spaceStretchCap;
                float spaceShrinkCap;
                int trackGaps;
                LayoutCell lastCell;
                if (from.SplitOffset > 0 && to.CellIndex == from.CellIndex)
                {
                    // Only the tail of the split word sits on this line.
                    naturalWidth = startTail!.Advance - startTail.TrailingLetterSpacing;
                    spaces = 0;
                    spaceStretchCap = 0f;
                    spaceShrinkCap = 0f;
                    trackGaps = LineBox.CountMicroTrackGaps(startTail);
                    lastCell = startTail;
                }
                else
                {
                    var rangeStart = from.SplitOffset > 0 ? from.CellIndex + 1 : from.NextCellIndex;
                    var rangeEndExclusive = to.SplitOffset > 0 ? to.CellIndex : to.CellIndex + 1;
                    if (rangeEndExclusive < rangeStart)
                    {
                        continue;
                    }

                    naturalWidth = prefixAdv[rangeEndExclusive] - prefixAdv[rangeStart];
                    spaces = prefixSpaces[rangeEndExclusive] - prefixSpaces[rangeStart];
                    spaceStretchCap = prefixStretchCap[rangeEndExclusive] - prefixStretchCap[rangeStart];
                    spaceShrinkCap = prefixShrinkCap[rangeEndExclusive] - prefixShrinkCap[rangeStart];
                    trackGaps = prefixTrackGaps[rangeEndExclusive] - prefixTrackGaps[rangeStart];
                    if (startTail is not null)
                    {
                        naturalWidth += startTail.Advance;
                        trackGaps += LineBox.CountMicroTrackGaps(startTail);
                    }

                    if (to.SplitOffset > 0)
                    {
                        var head = GetSplit(to.CellIndex, to.SplitOffset).Head;
                        naturalWidth += head.Advance;
                        trackGaps += LineBox.CountMicroTrackGaps(head);
                        lastCell = head;
                    }
                    else
                    {
                        lastCell = cells[to.CellIndex];
                        naturalWidth -= lastCell.TrailingLetterSpacing;
                    }
                }

                var protrusion = !to.IsEnd ? ComputeOpticalTrailingProtrusion(lastCell) : 0f;
                var effectiveTarget = lineTarget + protrusion;
                var slack = effectiveTarget - naturalWidth;

                if (slack < -0.01f)
                {
                    // Even slight shrinking won't save if way over.
                    if (spaces == 0 || -slack > spaceShrinkCap)
                    {
                        if (firstOverflowJ < 0 && !to.IsHyphen)
                        {
                            firstOverflowJ = j;
                        }
                        // Once the unbroken prefix before any syllable split exceeds
                        // effectiveTarget, later breakpoints will only be wider.
                        if (to.SplitOffset == 0)
                        {
                            break;
                        }
                        continue;
                    }
                }

                // Compute badness and Knuth-Plass demerit.
                double badness;
                int fitClass;
                if (to.IsEnd)
                {
                    // Last line is ragged right; only discourage an extremely short
                    // 1-word orphan last line when widow/orphan control is enabled.
                    badness = _context.Options.AvoidWidowsAndOrphans
                        && naturalWidth < lineTarget * 0.14f
                        && spaces == 0
                        ? 45.0
                        : 0.0;
                    fitClass = 1;
                }
                else if (slack >= 0f)
                {
                    var effectiveStretch = Math.Max(BaseFontSize * 0.25f, spaceStretchCap);
                    var microTrackCap = trackGaps * maxTrackPerGap;
                    if (slack <= spaceStretchCap + 0.01f)
                    {
                        var ratio = slack / effectiveStretch;
                        badness = 100.0 * ratio * ratio * ratio;
                        fitClass = ratio < 0.5f ? 1 : 2;
                    }
                    else if (slack <= spaceStretchCap + microTrackCap + 0.01f)
                    {
                        // Reachable flush-right via subtle micro-tracking after word
                        // spaces hit 0.65em; still prefer pure word-space lines.
                        var trackRatio = microTrackCap > 0.01f
                            ? (slack - spaceStretchCap) / microTrackCap
                            : 1f;
                        badness = 140.0 + 180.0 * trackRatio * trackRatio;
                        fitClass = 3;
                    }
                    else
                    {
                        // Exceeds both word-space cap and micro-tracking cap, which
                        // would leave a visible ragged notch on the right margin.
                        var excessEm = (slack - spaceStretchCap - microTrackCap) / Math.Max(1f, BaseFontSize);
                        badness = 1800.0 + excessEm * 1200.0;
                        fitClass = 3;
                    }
                }
                else
                {
                    var shrinkCap = Math.Max(0.1f, spaceShrinkCap);
                    var ratio = -slack / shrinkCap;
                    badness = 100.0 * ratio * ratio * ratio;
                    fitClass = 0;
                }

                var penalty = to.IsHyphen ? 50.0 : 0.0;
                var demerit = (1.0 + badness + penalty) * (1.0 + badness + penalty);
                if (from.IsHyphen && to.IsHyphen)
                {
                    demerit += 3000.0; // discourage consecutive hyphenated lines
                }
                if (Math.Abs(fitClass - fitness[i]) > 1)
                {
                    demerit += 100.0; // avoid jarring tight-to-loose jump
                }

                var candidateCost = bestCost[i] + demerit;
                if (candidateCost < bestCost[j])
                {
                    bestCost[j] = candidateCost;
                    prevIdx[j] = i;
                    lineNum[j] = curLine + 1;
                    fitness[j] = fitClass;
                }

                foundFeasible = true;
            }

            // If any unspaced token/grapheme run overflows the line without a
            // feasible space or hyphen break, fall back to the greedy breaker
            // which splits grapheme-by-grapheme inside the column width.
            if (!foundFeasible && firstOverflowJ > i)
            {
                return null;
            }
        }

        if (prevIdx[m - 1] < 0)
        {
            return null;
        }

        // Reconstruct optimal breakpoint chain.
        var path = new List<int>();
        for (var cur = m - 1; cur >= 0; cur = prevIdx[cur])
        {
            path.Add(cur);
            if (cur == 0) break;
        }
        path.Reverse();
        if (path[0] != 0)
        {
            return null;
        }

        var lines = new List<LineBox>(path.Count - 1);
        for (var step = 0; step < path.Count - 1; step++)
        {
            var from = breaks[path[step]];
            var to = breaks[path[step + 1]];
            var isLast = step == path.Count - 2;
            var floatInset = step < dropCapLines ? dropCapWidth : 0f;
            var line = new LineBox
            {
                Indent = step == 0 ? firstLineIndent : continuationIndent,
                FloatLeftInset = floatInset,
            };

            if (from.SplitOffset > 0)
            {
                line.Add(GetSplit(from.CellIndex, from.SplitOffset).Tail);
            }

            if (!(from.SplitOffset > 0 && to.CellIndex == from.CellIndex))
            {
                var rangeStart = from.SplitOffset > 0 ? from.CellIndex + 1 : from.NextCellIndex;
                var rangeEndExclusive = to.SplitOffset > 0 ? to.CellIndex : to.CellIndex + 1;
                for (var k = rangeStart; k < rangeEndExclusive; k++)
                {
                    line.Add(cells[k]);
                }

                if (to.SplitOffset > 0)
                {
                    line.Add(GetSplit(to.CellIndex, to.SplitOffset).Head);
                }
            }

            if (line.Cells.Count > 0)
            {
                if (!isLast)
                {
                    line.OpticalTrailingProtrusion = ComputeOpticalTrailingProtrusion(line.Cells[^1]);
                }
                lines.Add(line);
            }
        }

        return lines.Count > 0 ? lines : null;
    }
}
