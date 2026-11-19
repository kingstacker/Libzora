using System.Text;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Line breaking and punctuation rules.
/// </summary>
internal sealed partial class HorizontalComposer
{
    /// <summary>
    /// Greedy line fitting. A closing mark that does not fit hangs into the
    /// trailing margin; an opening mark never ends a line — it moves down
    /// together with the character that follows it.
    /// </summary>
    private List<LineBox> BreakLines(List<LayoutCell> cells, float availWidth, float firstLineIndent,
        bool allowPageAnchor = false, float continuationIndent = 0f)
    {
        var lines = new List<LineBox>();
        var line = new LineBox();
        var indentApplied = false;

        foreach (var sourceCell in cells)
        {
            var cell = sourceCell;
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

            if (cell.IsSpace && line.Cells.Count == 0)
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
                    cell, availWidth - line.Indent - line.Used - 0.01f, out var head, out var tail))
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
                }

                if (line.Cells.Count > 0 && IsOpeningMark(line.Cells[^1]))
                {
                    // An opening mark must not end the line.
                    pulled.Insert(0, line.Cells[^1]);
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

            if (cell.IsSpace && line.Cells.Count == 0)
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
