using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Table composition entry and fallback.
/// </summary>
internal sealed partial class HorizontalComposer
{
    /// <summary>
    /// Lays a publisher table out as real columns. Column widths follow the
    /// cells' natural text widths, cells wrap independently, and a header row
    /// is kept with its first data row. Very narrow tables and vertical
    /// writing fall back to the flattened text flow carried on
    /// <see cref="ContentBlock.Items"/>.
    /// </summary>
    private void PlaceTable(ContentBlock block, bool followsHeading = false, bool followsImage = false)
    {
        if (block.Table is not { Rows.Count: > 0 } table)
        {
            PlaceTableFallback(block, followsHeading, followsImage);
            return;
        }

        var leftInset = (block.MarginLeftEm + block.PaddingLeftEm) * BaseFontSize;
        var rightInset = (block.MarginRightEm + block.PaddingRightEm) * BaseFontSize;
        var availWidth = Math.Max(1f, ContentWidth - leftInset - rightInset);
        var blockLeft = InsetH + leftInset;

        var cellPadX = BaseFontSize * 0.35f;
        var cellPadY = BaseFontSize * 0.25f;
        var minColumnWidth = BaseFontSize * 2.2f;
        var capacity = (int)Math.Floor(availWidth / minColumnWidth);
        var measuredRows = capacity >= 2 ? MeasureTableRows(table, capacity) : null;
        if (measuredRows is null)
        {
            PlaceTableFallback(block, followsHeading, followsImage);
            return;
        }

        var columnCount = 0;
        var hasBorders = false;
        foreach (var row in measuredRows)
        {
            foreach (var cell in row.Cells)
            {
                columnCount = Math.Max(columnCount, cell.Column + cell.Span);
                hasBorders |= cell.HasBorder;
            }
        }

        if (columnCount < 2)
        {
            PlaceTableFallback(block, followsHeading, followsImage);
            return;
        }

        // Bordered cells collapse cleanly onto shared grid edges; only
        // borderless tables need a horizontal gutter between columns.
        var columnGap = hasBorders ? 0f : BaseFontSize * 0.4f;
        var gaps = columnGap * (columnCount - 1);
        if (minColumnWidth * columnCount + gaps > availWidth)
        {
            PlaceTableFallback(block, followsHeading, followsImage);
            return;
        }

        var columnWidths = FitTableColumns(
            measuredRows, columnCount, availWidth, gaps, minColumnWidth, cellPadX);
        if (columnWidths is null)
        {
            PlaceTableFallback(block, followsHeading, followsImage);
            return;
        }

        LayOutTableCells(block, measuredRows, columnWidths, columnGap, cellPadX);
        SetRowHeights(measuredRows, cellPadY);
        var groups = BuildRowGroups(measuredRows);
        if (groups.Any(group => GroupHeight(measuredRows, group) > _context.Options.ContentHeight)
            || measuredRows.SelectMany(row => row.Cells)
                .Any(cell => cell.Lines.Any(line => line.Used > cell.TextWidth + 0.01f)))
        {
            // Flow text across pages when the column grid cannot fit a row group.
            PlaceTableFallback(block, followsHeading, followsImage);
            return;
        }

        if (!ContinuousScroll && block.PageBreakBefore)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV;
            _previousBlockBottomSpace = 0f;
        }

        var beforeSpace = block.SpaceBeforeLines * BodyLineHeight
            + block.MarginTopEm * BaseFontSize;
        if (followsHeading || followsImage)
        {
            beforeSpace = Math.Max(0f, beforeSpace - _previousBlockBottomSpace);
        }
        _cursorY += beforeSpace;

        var afterSpace = block.SpaceAfterLines * BodyLineHeight + block.MarginBottomEm * BaseFontSize;
        if (!ContinuousScroll && block.KeepWithNext)
        {
            var keepHeight = measuredRows.Sum(row => row.Height) + afterSpace + BodyLineHeight * 1.8f;
            if (keepHeight <= _context.Options.ContentHeight
                && _cursorY + keepHeight > ContentBottom && _pages.CurrentPageHasContent)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        var rowTops = new float[measuredRows.Count];

        // Keep a header row together with the first data row instead of
        // stranding the header alone at the bottom of a page. Rows tied
        // together by rowspans form one group and move as a unit.
        if (!ContinuousScroll && groups.Count > 1)
        {
            var keepWith = GroupHeight(measuredRows, groups[0])
                + GroupHeight(measuredRows, groups[1]);
            if (keepWith <= _context.Options.ContentHeight && _cursorY + keepWith > ContentBottom + 0.01f
                && _pages.CurrentPageHasContent)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }
        }

        var recordedFragments = false;
        foreach (var (start, end) in groups)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var groupHeight = 0f;
            for (var rowIndex = start; rowIndex <= end; rowIndex++)
            {
                groupHeight += measuredRows[rowIndex].Height;
            }

            if (ContinuousScroll)
            {
                WrapContinuousScrollPage();
            }
            else if (_cursorY + groupHeight > ContentBottom + 0.01f
                && _pages.CurrentPageHasContent)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV;
            }

            if (!recordedFragments)
            {
                recordedFragments = true;
                _pages.RecordFragment(block.ElementId, _cursorY);
                _pages.RecordFragments(block.FragmentIds, _cursorY);
            }

            var groupTop = _cursorY;
            var top = _cursorY;
            for (var rowIndex = start; rowIndex <= end; rowIndex++)
            {
                rowTops[rowIndex] = top;
                top += measuredRows[rowIndex].Height;
            }

            for (var rowIndex = start; rowIndex <= end; rowIndex++)
            {
                var row = measuredRows[rowIndex];
                var rowTop = rowTops[rowIndex];
                foreach (var cell in row.Cells)
                {
                    var lastRow = Math.Min(measuredRows.Count - 1, rowIndex + cell.RowSpan - 1);
                    var cellBottom = rowTop;
                    for (var spanned = rowIndex; spanned <= lastRow; spanned++)
                    {
                        cellBottom += measuredRows[spanned].Height;
                    }

                    PlaceTableCell(
                        block, blockLeft, rowTop, cellBottom, cell, cellPadX, cellPadY);
                }

                if (!hasBorders && rowIndex < measuredRows.Count - 1)
                {
                    var ruleY = rowTop + row.Height;
                    _pages.AddDecoration(new PlacedRect
                    {
                        Kind = DecorationKind.Rule,
                        Rect = new SKRect(blockLeft, ruleY - 0.6f, blockLeft + availWidth, ruleY + 0.6f),
                    });
                }
            }

            _pages.AddTable(new PlacedTable
            {
                Rect = new SKRect(blockLeft, groupTop, blockLeft + availWidth, top),
                Block = block,
            });
            _cursorY = top;
        }

        if (!ContinuousScroll && block.PageBreakAfter)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV;
            _previousBlockBottomSpace = 0f;
        }
        else
        {
            _cursorY += afterSpace;
            _previousBlockBottomSpace = afterSpace;
            WrapContinuousScrollPage();
        }
    }
}
