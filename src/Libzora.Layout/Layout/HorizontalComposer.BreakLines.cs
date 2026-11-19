using System.Text;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Line breaking and punctuation rules.
/// </summary>
internal sealed partial class HorizontalComposer
{
    /// <summary>
    /// Greedy line fitting. Eligible closing CJK marks hang cleanly into the
    /// trailing margin; opening marks never end a line; CJK numeric clusters
    /// stay together; and long Latin words before trailing punctuation still
    /// get a hyphenation opportunity.
    /// </summary>
    private List<LineBox> BreakLines(List<LayoutCell> cells, float availWidth, float firstLineIndent,
        bool allowPageAnchor = false, float continuationIndent = 0f, bool preferLatinFont = false)
    {
        var lines = new List<LineBox>();
        var line = new LineBox();
        var indentApplied = false;

        for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            var cell = cells[cellIndex];
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (allowPageAnchor && _context.StartsAnchoredPage(cell))
            {
                if (line.Cells.Count > 0) lines.Add(line);
                line = new LineBox { StartsAnchoredPage = true };
                _context.PageStartConsumed = true;
                // An anchor within a paragraph is a continuation, not a new indent.
                if (lines.Count > 0)
                {
                    indentApplied = true;
                    line.Indent = continuationIndent;
                }
            }
            if (cell.IsLineBreak)
            {
                line.EndsWithForcedBreak = true;
                lines.Add(line);
                line = new LineBox { Indent = continuationIndent };
                indentApplied = true;
                continue;
            }

            if (cell.IsSpace && !cell.Style.PreserveWhitespace && line.Cells.Count == 0)
            {
                continue; // no leading whitespace
            }

            if (!indentApplied)
            {
                line.Indent = firstLineIndent;
                indentApplied = true;
            }

            var fits = line.Indent + line.WidthWith(cell) <= availWidth + 0.01f;
            while (!fits
                && _cells.TrySplitForHyphenation(
                    cell, availWidth - line.Indent - line.Used - 0.01f, out var head, out var tail, preferLatinFont))
            {
                // Break the word at an English syllable boundary rather than
                // leaving a conspicuous gap. The source offsets stay contiguous.
                line.Add(head);
                lines.Add(line);
                line = new LineBox { Indent = continuationIndent };
                cell = tail;
                fits = line.Indent + line.WidthWith(cell) <= availWidth + 0.01f;
            }

            if (!fits)
            {
                // Horizontal hanging punctuation: let one closing CJK mark
                // sit in the trailing margin instead of pulling a whole
                // character down and stretching the entire line. Skip when
                // another closing mark follows (e.g. "。”") so the second
                // mark cannot be orphaned at the start of the next line.
                var nextIsClosing = cellIndex + 1 < cells.Count && IsClosingMark(cells[cellIndex + 1]);
                var maxHang = Math.Min(InsetH * 0.72f, BaseFontSize * 0.72f);
                if (_context.Options.HangingPunctuation
                    && !line.HasHangingPunctuation
                    && line.Cells.Count > 0
                    && !nextIsClosing
                    && TypesetText.IsHangablePunctuation(cell.Text)
                    && cell.Advance <= maxHang + 0.01f
                    && line.Indent + line.Used <= availWidth + 0.01f)
                {
                    line.Add(cell);
                    line.HasHangingPunctuation = true;
                    lines.Add(line);
                    line = new LineBox { Indent = continuationIndent };
                    continue;
                }

                var pulled = new List<LayoutCell>();

                if (IsClosingMark(cell))
                {
                    // Pull back trailing closers, then one leading cell, so
                    // the cluster moves down together and no closer starts
                    // the next line.
                    while (line.Cells.Count > 0 && IsClosingMark(line.Cells[^1]) && pulled.Count < 4)
                    {
                        pulled.Insert(0, line.Cells[^1]);
                        line.RemoveLast();
                    }

                    if (line.Cells.Count > 0)
                    {
                        pulled.Insert(0, line.Cells[^1]);
                        line.RemoveLast();
                    }

                    // If a long English word was pulled solely because its
                    // trailing punctuation did not fit (e.g. "internationalization,"),
                    // still try hyphenating that word so the line stays balanced.
                    if (pulled.Count > 0 && pulled[0].IsLatinWord)
                    {
                        var room = availWidth - line.Indent - line.Used - 0.01f;
                        if (room > BaseFontSize * 1.5f
                            && _cells.TrySplitForHyphenation(
                                pulled[0], room, out var head, out var tail, preferLatinFont))
                        {
                            line.Add(head);
                            pulled[0] = tail;
                        }
                    }
                }

                // Keep numeric clusters (e.g. "第12章", "2026年") together
                // across horizontal line breaks, matching vertical typesetting.
                var keepGuard = 0;
                while (line.Cells.Count > 1 && keepGuard < 3)
                {
                    var rightText = pulled.Count > 0 ? pulled[0].Text : cell.Text;
                    var leftCell = line.Cells[^1];
                    if (leftCell.IsSpace || !TypesetText.ShouldKeepTogether(leftCell.Text, rightText))
                    {
                        break;
                    }

                    pulled.Insert(0, leftCell);
                    line.RemoveLast();
                    keepGuard++;
                }

                // Opening marks (including nested ones like “《 or （“) must
                // never end a line.
                var openGuard = 0;
                while (line.Cells.Count > 1 && IsOpeningMark(line.Cells[^1]) && openGuard < 4)
                {
                    pulled.Insert(0, line.Cells[^1]);
                    line.RemoveLast();
                    openGuard++;
                }

                if (line.Cells.Count == 1 && IsOpeningMark(line.Cells[0]) && !line.StartsAnchoredPage)
                {
                    pulled.Insert(0, line.Cells[0]);
                    line.RemoveLast();
                }

                var startsAnchoredPage = line.StartsAnchoredPage && line.Cells.Count == 0;
                if (line.Cells.Count > 0)
                {
                    lines.Add(line);
                }

                line = new LineBox { StartsAnchoredPage = startsAnchoredPage, Indent = continuationIndent };
                foreach (var pulledCell in pulled)
                {
                    line.Add(pulledCell);
                }
            }

            if (cell.IsSpace && !cell.Style.PreserveWhitespace && line.Cells.Count == 0)
            {
                continue;
            }

            line.Add(cell);
        }

        if (line.Cells.Count > 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static bool IsClosingMark(LayoutCell cell) =>
        cell.Character is { } c && TypesetText.IsProhibitedAtLineStart(c);

    private static bool IsOpeningMark(LayoutCell cell) =>
        cell.Character is { } c && TypesetText.IsProhibitedAtLineEnd(c);
}
