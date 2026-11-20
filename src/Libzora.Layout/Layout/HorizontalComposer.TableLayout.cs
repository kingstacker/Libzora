using System.Text;
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
    private void PlaceTableFallback(ContentBlock block)
    {
        var pageIndex = _pages.CurrentPageIndex;
        var top = _cursorY;
        PlaceTextBlock(block, false);
        if (pageIndex == _pages.CurrentPageIndex && _cursorY > top)
        {
            _pages.AddTable(new PlacedTable
            {
                Rect = new SKRect(InsetH, top, InsetH + ContentWidth, _cursorY),
                Block = block,
            });
        }
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
            var baseline = lineTop + cell.LineHeight / 2f
                + (line.Ascent - line.Descent) / 2f;
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
                cell.LineHeight,
                _context.RubyGap);
            lineTop += cell.LineHeight;
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
        public required float LineHeight { get; init; }
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

    private List<MeasuredRow> MeasureTableRows(
        ContentBlock block,
        ContentTable table,
        int capacity)
    {
        var rows = new List<MeasuredRow>(table.Rows.Count);
        // HTML tables are positional: a rowspan cell removes a slot from later
        // rows, so the grid has to be simulated before columns are numbered.
        var occupied = new HashSet<long>();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var cells = new List<MeasuredCell>(row.Cells.Count);
            var column = 0;
            foreach (var cell in row.Cells)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                while (column < capacity && occupied.Contains(GridKey(rowIndex, column)))
                {
                    column++;
                }

                if (column >= capacity)
                {
                    break;
                }

                var span = Math.Clamp(Math.Max(1, cell.ColSpan), 1, capacity - column);
                while (span > 1 && IsOccupied(occupied, rowIndex, column, span))
                {
                    span--;
                }

                var rowSpan = Math.Clamp(
                    Math.Max(1, cell.RowSpan), 1, table.Rows.Count - rowIndex);
                for (var spannedRow = rowIndex; spannedRow < rowIndex + rowSpan; spannedRow++)
                {
                    for (var spannedColumn = column;
                         spannedColumn < column + span;
                         spannedColumn++)
                    {
                        occupied.Add(GridKey(spannedRow, spannedColumn));
                    }
                }

                var cellCells = new List<LayoutCell>();
                foreach (var item in cell.Items)
                {
                    cellCells.AddRange(_cells.BuildCells(item, vertical: false));
                }

                CellFactory.CollapseWhitespace(cellCells);
                var inkExtent = cellCells
                    .Select(candidate => candidate.Ascent + candidate.Descent)
                    .DefaultIfEmpty(0f)
                    .Max();
                var cellScale = Math.Clamp(
                    cellCells
                        .Select(candidate => candidate.Style.FontSizeFactor > 0f
                            ? candidate.Style.FontSizeFactor
                            : 1f)
                        .DefaultIfEmpty(1f)
                        .Max(),
                    0.5f,
                    3f);
                var lineHeight = block.LineHeightFactor > 0f
                    ? Math.Max(BaseFontSize * cellScale * block.LineHeightFactor, inkExtent + 1f)
                    : Math.Max(BaseFontSize * cellScale * 1.45f, inkExtent + 1f);
                lineHeight = Math.Max(lineHeight, cellCells
                    .Where(candidate => candidate.ImagePath is not null)
                    .Select(candidate => candidate.ImageHeight)
                    .DefaultIfEmpty(0f)
                    .Max());

                cells.Add(new MeasuredCell
                {
                    Items = cell.Items,
                    Cells = cellCells,
                    Row = rowIndex,
                    RowSpan = rowSpan,
                    Column = column,
                    Span = span,
                    NaturalWidth = cellCells.Sum(candidate => candidate.Advance),
                    LineHeight = lineHeight,
                    BorderTop = cell.BorderTopEm,
                    BorderRight = cell.BorderRightEm,
                    BorderBottom = cell.BorderBottomEm,
                    BorderLeft = cell.BorderLeftEm, // copied from the content cell
                });
                column += span;
            }

            rows.Add(new MeasuredRow { Cells = cells });
        }

        return rows;
    }

    private static long GridKey(int row, int column) => ((long)row << 32) | (uint)column;

    private static bool IsOccupied(
        HashSet<long> occupied,
        int row,
        int column,
        int span)
    {
        for (var offset = 0; offset < span; offset++)
        {
            if (occupied.Contains(GridKey(row, column + offset)))
            {
                return true;
            }
        }

        return false;
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

                    CellFactory.CollapseWhitespace(rebuilt);
                    if (rebuilt.Count > 0)
                    {
                        cell.Cells = rebuilt;
                        cell.Lines = BreakLines(rebuilt, cell.TextWidth, 0f);
                    }
                }
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
                var contentHeight = cell.Lines.Count * cell.LineHeight + cellPadY * 2f;
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

                var required = cell.Lines.Count * cell.LineHeight + cellPadY * 2f;
                if (required > current)
                {
                    rows[end].Height += required - current;
                }
            }
        }
    }

    /// <summary>A laid-out line of cells.</summary>
    private sealed class LineBox
    {
        public List<LayoutCell> Cells { get; } = new();
        private float _advance;
        public float Used => _advance - (Cells.Count > 0 ? Cells[^1].TrailingLetterSpacing : 0f);
        public float WidthWith(LayoutCell cell) => _advance + cell.Advance - cell.TrailingLetterSpacing;
        public float Ascent { get; private set; }
        public float Descent { get; private set; }
        public float Indent { get; set; }
        public bool EndsWithForcedBreak { get; set; }
        public bool StartsAnchoredPage { get; set; }
        /// <summary>True when the final cell is a closing CJK mark intentionally hanging into the trailing margin.</summary>
        public bool HasHangingPunctuation { get; set; }
        /// <summary>Optical protrusion into the trailing margin (e.g. half a Latin hyphen or period).</summary>
        public float OpticalTrailingProtrusion { get; set; }
        /// <summary>Additional left inset on this line (e.g. beside a floated drop cap).</summary>
        public float FloatLeftInset { get; set; }

        public void Add(LayoutCell cell)
        {
            Cells.Add(cell);
            _advance += cell.Advance;
            Ascent = Math.Max(Ascent, cell.Ascent);
            Descent = Math.Max(Descent, cell.Descent);
        }

        public void RemoveLast()
        {
            if (Cells.Count == 0)
            {
                return;
            }

            var removed = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            _advance -= removed.Advance;
            Ascent = 0f;
            Descent = 0f;
            foreach (var cell in Cells)
            {
                Ascent = Math.Max(Ascent, cell.Ascent);
                Descent = Math.Max(Descent, cell.Descent);
            }
        }

        /// <summary>
        /// Distributes <paramref name="extra"/> pixels. Latin-majority lines
        /// only expand their word spaces: expanding every glyph gap (the old
        /// behavior) pulled English words apart and produced the tell-tale
        /// "loose line" when a long word wrapped. Mobile prose starts with
        /// proportional spaces and fills only as far as the spacing cap permits;
        /// legacy layouts leave a line ragged when full justification is too loose.
        /// CJK and mixed lines keep inter-character justification, where the
        /// per-gap amount is shared by CJK characters and gaps around intact
        /// Latin words, never by the letters inside those words.
        /// </summary>
        public bool TryJustify(float extra, float fontSize, bool naturalLatin = false)
        {
            var end = Cells.Count;
            if (HasHangingPunctuation && end > 1)
            {
                // Align the body up to the trailing margin while letting the
                // hanging punctuation sit outside the margin.
                extra += Cells[end - 1].Advance;
                end--;
            }
            else if (OpticalTrailingProtrusion > 0f)
            {
                // Optical margin alignment: let trailing hyphens/periods peek
                // slightly past the right margin so the text column looks straight.
                extra += OpticalTrailingProtrusion;
            }

            if (extra <= 0.01f)
            {
                return true;
            }

            while (end > 0 && Cells[end - 1].IsSpace)
            {
                end--;
            }

            // The generated marker and its trailing space define a fixed
            // hanging indent. Justify the body, not those synthetic cells.
            var start = 0;
            while (start < end && Cells[start].TextStart < 0 && Cells[start].Style.NoWrap) start++;
            if (end - start <= 1)
            {
                return true;
            }

            var latinGlyphs = 0;
            var inkGlyphs = 0;
            var totalGlyphs = 0;
            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                totalGlyphs += cell.JustifyAsUnit ? 1 : glyphCount;
                if (cell.IsSpace)
                {
                    continue;
                }

                inkGlyphs += glyphCount;
                if (cell.IsLatinWord)
                {
                    latinGlyphs += glyphCount;
                }
            }

            // Only a line whose visible ink is majority Latin takes the
            // space-only path. A Chinese sentence with an English name or an
            // acronym is still justified between characters, which keeps the
            // right edge flush instead of leaving it conspicuously ragged.
            if (naturalLatin || (latinGlyphs > 0 && latinGlyphs * 2 >= inkGlyphs))
            {
                var spaceCount = 0;
                for (var index = start; index < end; index++)
                {
                    if (Cells[index].IsSpace)
                    {
                        spaceCount++;
                    }
                }

                if (spaceCount == 0
                    || (!naturalLatin && extra / spaceCount > fontSize * 0.2f))
                {
                    return false;
                }

                var perSpace = extra / spaceCount;
                if (naturalLatin)
                {
                    // Expand word spaces up to the safe typographic cap (0.65em)
                    // without ever pulling apart letters inside English words.
                    // If the remaining unabsorbed slack is still large (e.g. a
                    // short line before an unbreakable word), leave the line
                    // cleanly ragged instead of creating loose half-stretched gaps.
                    var maxSpaceWidth = fontSize * 0.65f;
                    var maxSpaceGain = 0f;
                    for (var index = start; index < end; index++)
                    {
                        var cell = Cells[index];
                        if (cell.IsSpace)
                        {
                            maxSpaceGain += Math.Max(0f, maxSpaceWidth - cell.Advance);
                        }
                    }

                    if (extra - maxSpaceGain > fontSize * 2.5f)
                    {
                        return false;
                    }

                    var added = 0f;
                    for (var index = start; index < end; index++)
                    {
                        var cell = Cells[index];
                        if (cell.IsSpace)
                        {
                            var expansion = Math.Min(perSpace, Math.Max(0f, maxSpaceWidth - cell.Advance));
                            Cells[index] = cell with { Advance = cell.Advance + expansion };
                            added += expansion;
                        }
                    }

                    _advance += added;
                    return added >= extra - 0.01f;
                }

                var legacyAdded = 0f;
                for (var index = start; index < end; index++)
                {
                    var cell = Cells[index];
                    if (cell.IsSpace)
                    {
                        Cells[index] = cell with { Advance = cell.Advance + perSpace };
                        legacyAdded += perSpace;
                    }
                }

                _advance += legacyAdded;
                return legacyAdded >= extra - 0.01f;
            }

            var gaps = totalGlyphs - 1;
            if (gaps <= 0)
            {
                return true;
            }

            var perGap = extra / gaps;
            if (perGap > fontSize * 1.5f)
            {
                return false;
            }

            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                var shiftedX = new float[cell.GlyphX.Length];
                if (!cell.JustifyAsUnit)
                {
                    for (var i = 0; i < cell.GlyphX.Length; i++)
                    {
                        // Gaps inside a shaped cell belong in its local glyph
                        // positions. The gap after a cell belongs in that
                        // cell's advance, so PlaceLine does not count the same
                        // distance twice when it advances to the next cell.
                        shiftedX[i] = cell.GlyphX[i] + perGap * i;
                    }
                }
                else
                {
                    // Markers and ruby bases stay intact. Justification adds
                    // space after the unit, not between its own glyphs.
                    Array.Copy(cell.GlyphX, shiftedX, cell.GlyphX.Length);
                }

                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                var gapsAfter = index == end - 1
                    ? cell.JustifyAsUnit ? 0 : Math.Max(0, glyphCount - 1)
                    : cell.JustifyAsUnit ? 1 : glyphCount;
                Cells[index] = cell with
                {
                    GlyphX = shiftedX,
                    Advance = cell.Advance + perGap * gapsAfter,
                };
            }

            _advance += extra;
            return true;
        }
    }

    /// <summary>Accumulates consecutive compatible cells into one placed run.</summary>
    private sealed class RunAccumulator
    {
        private readonly float _baseline;
        private readonly List<ushort> _glyphs = new();
        private readonly List<float> _x = new();
        private readonly List<float> _y = new();
        private readonly List<float> _inkShiftX = new();
        private readonly List<int> _clusters = new();
        private LayoutCell? _first;
        private LayoutCell? _last;
        private int _cellClusterBase;
        private float _flowAdvance;
        private float _leadingInset;
        private bool _syntheticHyphen;

        public RunAccumulator(float baseline)
        {
            _baseline = baseline;
        }

        public bool CanAppend(LayoutCell cell, float baseline)
        {
            return _first is not null
                && _last is not null
                && cell.FontPath == _first.FontPath
                && Math.Abs(cell.FontSize - _first.FontSize) < 0.01f
                && cell.Style.Equals(_last.Style)
                && Math.Abs(baseline - _baseline) < 0.01f
                && cell.TextStart >= 0
                && _last.TextStart >= 0
                && cell.TextStart == _last.TextStart + _last.TextLength;
        }

        public void BeginCell(LayoutCell cell, float leadingInset)
        {
            if (_first is null) _leadingInset = leadingInset;
            _first ??= cell;
            _last = cell;
            _syntheticHyphen |= cell.SyntheticHyphen;
            _cellClusterBase = _first.TextStart >= 0 && cell.TextStart >= 0
                ? cell.TextStart - _first.TextStart
                : 0;
            _flowAdvance += cell.Advance;
        }

        public void AddGlyph(ushort glyph, float x, float y, int cluster, float inkShiftX = 0f)
        {
            _glyphs.Add(glyph);
            _x.Add(x);
            _y.Add(y);
            _inkShiftX.Add(inkShiftX);
            _clusters.Add(_cellClusterBase + cluster);
        }

        public void Flush(PageBuilder pages)
        {
            if (_first is null || _glyphs.Count == 0)
            {
                Reset();
                return;
            }

            var originX = _x[0];
            var glyphs = _glyphs.ToArray();
            var x = new float[_x.Count];
            var y = new float[_y.Count];
            for (var i = 0; i < _x.Count; i++)
            {
                x[i] = _x[i] - originX;
                // Cell glyph offsets are already relative to the baseline.
                // Subtracting the absolute line baseline here moves every
                // horizontal glyph far above the page.
                y[i] = _y[i];
            }

            var textStart = _first.TextStart;
            var textLength = _first.TextStart >= 0 && _last is not null && _last.TextStart >= 0
                ? Math.Max(0, _last.TextStart + _last.TextLength - textStart)
                : 0;

            pages.AddRun(new PlacedRun
            {
                FontPath = _first.FontPath,
                FontSize = _first.FontSize,
                Glyphs = glyphs,
                X = x,
                Y = y,
                InkShiftX = _inkShiftX.Any(shift => shift != 0f) ? _inkShiftX.ToArray() : null,
                OriginX = originX,
                OriginY = _baseline,
                Ascent = _first.Ascent,
                Descent = _first.Descent,
                FlowAdvance = _flowAdvance - _leadingInset,
                TextStart = textStart,
                TextLength = textLength,
                Clusters = _clusters.ToArray(),
                SyntheticHyphen = _syntheticHyphen,
                Style = _first.Style,
            });

            Reset();
        }

        private void Reset()
        {
            _first = null;
            _last = null;
            _flowAdvance = 0f;
            _leadingInset = 0f;
            _syntheticHyphen = false;
            _glyphs.Clear();
            _x.Clear();
            _y.Clear();
            _inkShiftX.Clear();
            _clusters.Clear();
        }
    }
}
