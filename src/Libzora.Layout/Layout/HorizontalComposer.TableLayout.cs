using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Table cell measurement, fitting and borders.
/// </summary>
internal sealed partial class HorizontalComposer
{
    /// <summary>
    /// Text fallback for tables that cannot use real columns on this page.
    /// A tap target is kept for the visible part so the wide table viewer is
    /// still reachable on a phone.
    /// </summary>
    private void PlaceTableFallback(ContentBlock block, bool followsHeading = false, bool followsImage = false)
    {
        if (IsNavigationLinkTable(block) && block.Table is { Rows.Count: > 0 } navTable)
        {
            // Lay out each TOC row as its own un-hyphenated, left-aligned entry
            // with tighter intra-title line height and a subtle gap between rows
            // so wrapped chapter titles visually stay grouped together.
            var rows = navTable.Rows.Where(row => row.Cells.Any(cell => cell.Items.Count > 0)).ToArray();
            for (var r = 0; r < rows.Length; r++)
            {
                var rowItems = new List<InlineItem>();
                foreach (var cell in rows[r].Cells)
                {
                    if (cell.Items.Count == 0) continue;
                    if (rowItems.Count > 0)
                    {
                        rowItems.Add(new InlineItem { Kind = InlineKind.Text, Text = " ", TextStart = -1, Style = block.Style });
                    }
                    rowItems.AddRange(cell.Items);
                }

                var isFirst = r == 0;
                var isLast = r == rows.Length - 1;
                var rowBlock = new ContentBlock
                {
                    Kind = BlockKind.Table,
                    ElementId = isFirst ? block.ElementId : null,
                    FragmentIds = isFirst ? block.FragmentIds : Array.Empty<string>(),
                    Style = block.Style,
                    Center = block.Center,
                    AlignRight = block.AlignRight,
                    Justify = false,
                    PageBreakBefore = isFirst && block.PageBreakBefore,
                    PageBreakAfter = isLast && block.PageBreakAfter,
                    KeepWithNext = isLast && block.KeepWithNext,
                    SpaceBeforeLines = isFirst ? block.SpaceBeforeLines : 0f,
                    SpaceAfterLines = isLast ? block.SpaceAfterLines : 0f,
                    MarginTopEm = isFirst ? block.MarginTopEm : 0f,
                    MarginBottomEm = isLast ? block.MarginBottomEm : 0.42f,
                    MarginLeftEm = block.MarginLeftEm,
                    MarginRightEm = block.MarginRightEm,
                    PaddingLeftEm = block.PaddingLeftEm,
                    PaddingRightEm = block.PaddingRightEm,
                    LineHeightFactor = block.LineHeightFactor,
                    Items = rowItems,
                };
                PlaceTextBlock(
                    rowBlock,
                    followsHeading: isFirst && followsHeading,
                    followsImage: isFirst && followsImage,
                    allowHyphenation: false,
                    compactNavigationEntry: true,
                    tablePreview: block);
            }
        }
        else
        {
            PlaceTextBlock(block, followsHeading, followsImage: followsImage, tablePreview: block);
        }
    }

    private static bool IsNavigationLinkTable(ContentBlock block)
    {
        var linkItems = 0;
        foreach (var item in block.Items)
        {
            if (item.Kind == InlineKind.LineBreak || item.Ghost) continue;
            if (string.IsNullOrWhiteSpace(item.Text)) continue;
            if (string.IsNullOrWhiteSpace(item.LinkHref)) return false;
            linkItems++;
        }
        return linkItems >= 2;
    }

    private void PlaceTableCell(
        ContentBlock block,
        float blockLeft,
        float rowTop,
        float cellBottom,
        MeasuredCell cell,
        float cellPadX,
        float cellPadY)
    {
        var lineTop = rowTop + cellPadY;
        for (var lineIndex = 0; lineIndex < cell.Lines.Count; lineIndex++)
        {
            var line = cell.Lines[lineIndex];
            var (height, rubyShelf) = cell.LineMetrics[lineIndex];
            var baseline = lineTop + height / 2f
                + (line.Ascent - line.Descent + rubyShelf) / 2f;
            if (block.Justify && lineIndex < cell.Lines.Count - 1)
            {
                var extra = cell.TextWidth - line.Indent - line.Used;
                if (extra > 0f)
                {
                    line.TryJustify(extra, BaseFontSize);
                }
            }

            PlaceLine(
                line,
                blockLeft + cell.X + cellPadX + line.Indent,
                baseline,
                lineTop,
                height,
                _context.RubyGap);
            lineTop += height;
        }

        if (!cell.HasBorder)
        {
            return;
        }

        var left = blockLeft + cell.X;
        var right = left + cell.Width;
        DrawCellBorders(left, rowTop, right, cellBottom, cell);
    }

    private void DrawCellBorders(float left, float top, float right, float bottom, MeasuredCell cell)
    {
        var maxThickness = BaseFontSize * 0.5f;
        DrawBorderSide(left, top, right, top, cell.BorderTop);
        DrawBorderSide(right, top, right, bottom, cell.BorderRight);
        DrawBorderSide(left, bottom, right, bottom, cell.BorderBottom);
        DrawBorderSide(left, top, left, bottom, cell.BorderLeft);

        void DrawBorderSide(float x1, float y1, float x2, float y2, float widthEm)
        {
            if (widthEm <= 0f)
            {
                return;
            }

            var thickness = Math.Clamp(widthEm * BaseFontSize, 0.6f, maxThickness);
            // Keep the rule inside the cell rectangle on both axes.
            var half = thickness / 2f;
            var rect = y1 == y2
                ? new SKRect(x1, y1 - half, x2, y1 + half)
                : new SKRect(x1 - half, y1, x1 + half, y2);
            _pages.AddDecoration(new PlacedRect { Kind = DecorationKind.Rule, Rect = rect });
        }
    }

    private static List<(int Start, int End)> BuildRowGroups(List<MeasuredRow> rows)
    {
        var groups = new List<(int Start, int End)>();
        var start = 0;
        while (start < rows.Count)
        {
            var end = start;
            for (var scan = start; scan <= end; scan++)
            {
                foreach (var cell in rows[scan].Cells)
                {
                    end = Math.Max(end, scan + Math.Max(1, cell.RowSpan) - 1);
                }
            }

            end = Math.Min(end, rows.Count - 1);
            groups.Add((start, end));
            start = end + 1;
        }

        return groups;
    }

    private static float GroupHeight(List<MeasuredRow> rows, (int Start, int End) group)
    {
        var height = 0f;
        for (var index = group.Start; index <= group.End; index++)
        {
            height += rows[index].Height;
        }

        return height;
    }

    /// <summary>Table cell after shaping, before column widths are known.</summary>
    private sealed class MeasuredCell
    {
        public required IReadOnlyList<InlineItem> Items { get; init; }
        public required List<LayoutCell> Cells { get; set; }
        public required int Row { get; init; }
        public required int RowSpan { get; init; }
        public required int Column { get; init; }
        public required int Span { get; init; }
        public required float NaturalWidth { get; init; }
        public (float Height, float RubyShelf)[] LineMetrics { get; set; } = [];
        public required float BorderTop { get; init; }
        public required float BorderRight { get; init; }
        public required float BorderBottom { get; init; }
        public required float BorderLeft { get; init; }
        public float X { get; set; }
        public float Width { get; set; }
        public float TextWidth { get; set; }
        public List<LineBox> Lines { get; set; } = [];

        public bool HasBorder =>
            BorderTop > 0f || BorderRight > 0f || BorderBottom > 0f || BorderLeft > 0f;
    }

    private sealed class MeasuredRow
    {
        public required List<MeasuredCell> Cells { get; init; }
        public float Height { get; set; }
    }

    private List<MeasuredRow>? MeasureTableRows(ContentTable table, int capacity)
    {
        var rows = new List<MeasuredRow>(table.Rows.Count);
        // HTML tables are positional: a rowspan cell removes a slot from later
        // rows, so the grid has to be simulated before columns are numbered.
        var occupiedUntil = new Dictionary<int, int>();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var cells = new List<MeasuredCell>(row.Cells.Count);
            var column = 0;
            foreach (var cell in row.Cells)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                var span = Math.Max(1, cell.ColSpan);
                var rowSpan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - rowIndex);
                var free = 0;
                while (column < capacity && free < span)
                {
                    free = occupiedUntil.GetValueOrDefault(column) > rowIndex ? 0 : free + 1;
                    column++;
                }
                if (free < span) return null;
                var startColumn = column - span;
                if (rowSpan > 1)
                {
                    for (var spannedColumn = startColumn; spannedColumn < column; spannedColumn++)
                        occupiedUntil[spannedColumn] = rowIndex + rowSpan;
                }

                var cellCells = new List<LayoutCell>();
                foreach (var item in cell.Items)
                {
                    cellCells.AddRange(_cells.BuildCells(item, vertical: false));
                }

                CellFactory.CollapseWhitespace(cellCells, attachFootnoteMarkers: true);
                cells.Add(new MeasuredCell
                {
                    Items = cell.Items,
                    Cells = cellCells,
                    Row = rowIndex,
                    RowSpan = rowSpan,
                    Column = startColumn,
                    Span = span,
                    NaturalWidth = cellCells.Sum(candidate => candidate.Advance),
                    BorderTop = cell.BorderTopEm,
                    BorderRight = cell.BorderRightEm,
                    BorderBottom = cell.BorderBottomEm,
                    BorderLeft = cell.BorderLeftEm, // copied from the content cell
                });
            }

            rows.Add(new MeasuredRow { Cells = cells });
        }

        return rows;
    }

    /// <summary>
    /// Auto table layout: start from each column's widest natural cell, then
    /// either distribute spare width proportionally or shrink toward a minimum
    /// so the table always fits the content box. Returns null when even the
    /// minimum widths cannot fit.
    /// </summary>
    private static float[]? FitTableColumns(
        List<MeasuredRow> rows,
        int columnCount,
        float availWidth,
        float gaps,
        float minColumnWidth,
        float cellPadX)
    {
        var weights = new float[columnCount];
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                var share = cell.NaturalWidth / cell.Span;
                for (var offset = 0; offset < cell.Span; offset++)
                {
                    var column = cell.Column + offset;
                    weights[column] = Math.Max(weights[column], share);
                }
            }
        }

        var desired = new float[columnCount];
        var total = 0f;
        for (var column = 0; column < columnCount; column++)
        {
            desired[column] = Math.Max(minColumnWidth, weights[column] + cellPadX * 2f);
            total += desired[column];
        }

        var available = availWidth - gaps;
        if (available <= 0f)
        {
            return null;
        }

        if (total > available)
        {
            var minimum = minColumnWidth * columnCount;
            if (minimum > available)
            {
                return null;
            }

            // Water-filling: narrow columns keep their natural width and only
            // the widest ones shrink, so a short "数量" column never wraps
            // while a long prose column gives up space.
            var low = minColumnWidth;
            var high = desired.Max();
            for (var iteration = 0; iteration < 24; iteration++)
            {
                var limit = (low + high) / 2f;
                var sum = 0f;
                for (var column = 0; column < columnCount; column++)
                {
                    sum += Math.Clamp(desired[column], minColumnWidth, limit);
                }

                if (sum > available)
                {
                    high = limit;
                }
                else
                {
                    low = limit;
                }
            }

            var waterLevel = (low + high) / 2f;
            for (var column = 0; column < columnCount; column++)
            {
                desired[column] = Math.Clamp(desired[column], minColumnWidth, waterLevel);
            }
        }
        else if (total < available)
        {
            // Let wide tables breathe; narrow ones share the spare width in
            // proportion to their natural size.
            var extra = available - total;
            for (var column = 0; column < columnCount; column++)
            {
                desired[column] += extra * (desired[column] / total);
            }
        }

        return desired;
    }

    private void LayOutTableCells(
        ContentBlock block,
        List<MeasuredRow> rows,
        float[] columnWidths,
        float columnGap,
        float cellPadX)
    {
        foreach (var row in rows)
        {
            foreach (var cell in row.Cells)
            {
                var x = 0f;
                var width = 0f;
                for (var column = 0; column < cell.Column; column++)
                {
                    x += columnWidths[column] + columnGap;
                }

                for (var offset = 0; offset < cell.Span; offset++)
                {
                    width += columnWidths[cell.Column + offset];
                }

                width += columnGap * (cell.Span - 1);
                cell.X = x;
                cell.Width = width;
                cell.TextWidth = Math.Max(1f, width - cellPadX * 2f + 2f);
                cell.Lines = BreakLines(cell.Cells, cell.TextWidth, 0f);

                // A word can be narrower than the page but wider than a narrow
                // table column. Rebuild just that cell with the column width
                // as the splitting limit so long English words wrap inside
                // the cell instead of spilling into its neighbour.
                var overflow = cell.Lines.Any(line => line.Used > cell.TextWidth + 0.01f);
                var hasWideWord = cell.Cells.Any(candidate =>
                    candidate.Advance > cell.TextWidth + 0.01f
                    && candidate.Text.Length > 1
                    && candidate.Text.Any(char.IsAsciiLetterOrDigit));
                if (overflow && hasWideWord)
                {
                    var rebuilt = new List<LayoutCell>();
                    foreach (var item in cell.Items)
                    {
                        rebuilt.AddRange(_cells.BuildCells(
                            item, vertical: false, maxWordWidth: cell.TextWidth));
                    }

                    CellFactory.CollapseWhitespace(rebuilt, attachFootnoteMarkers: true);
                    if (rebuilt.Count > 0)
                    {
                        cell.Cells = rebuilt;
                        cell.Lines = BreakLines(rebuilt, cell.TextWidth, 0f);
                    }
                }
                cell.LineMetrics = cell.Lines.Select(line => ComputeLineMetrics(
                    line, block, isHeading: false, BaseFontSize * 1.45f, _context.RubyGap)).ToArray();
            }
        }
    }

    /// <summary>
    /// Resolves row heights. Single-row cells set their row directly; a cell
    /// spanning several rows can only grow the last row it covers, which is
    /// how browsers distribute the extra height when no explicit height is set.
    /// </summary>
    private static void SetRowHeights(List<MeasuredRow> rows, float cellPadY)
    {
        foreach (var row in rows)
        {
            row.Height = 0f;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            foreach (var cell in rows[rowIndex].Cells)
            {
                var contentHeight = cell.LineMetrics.Sum(line => line.Height) + cellPadY * 2f;
                if (cell.RowSpan == 1)
                {
                    rows[rowIndex].Height = Math.Max(rows[rowIndex].Height, contentHeight);
                }
            }

            rows[rowIndex].Height = Math.Max(rows[rowIndex].Height, cellPadY * 2f);
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            foreach (var cell in rows[rowIndex].Cells)
            {
                if (cell.RowSpan <= 1)
                {
                    continue;
                }

                var end = Math.Min(rows.Count - 1, rowIndex + cell.RowSpan - 1);
                var current = 0f;
                for (var spanned = rowIndex; spanned <= end; spanned++)
                {
                    current += rows[spanned].Height;
                }

                var required = cell.LineMetrics.Sum(line => line.Height) + cellPadY * 2f;
                if (required > current)
                {
                    rows[end].Height += required - current;
                }
            }
        }
    }
}
