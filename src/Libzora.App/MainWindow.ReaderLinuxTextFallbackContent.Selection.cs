using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Libzora;

/// <summary>
/// Selection synchronisation and anchors.
/// </summary>
public partial class MainWindow
{
    private void ClearLinuxReaderTextFallbackSelectionState()
    {
        ClearLinuxReaderTextFallbackVisualSelection();
        HideReaderSelectionPopup();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _selectedReaderAnnotation = null;
    }

    private void ClearLinuxReaderTextFallbackVisualSelection()
    {
        var blocks = new[]
            {
                ReaderLinuxTextFallbackText,
                ReaderLinuxTextFallbackPageLeft,
                ReaderLinuxTextFallbackPageRight
            }
            .Concat(ReaderLinuxTextFallbackOverlay
                .GetVisualDescendants()
                .OfType<SelectableTextBlock>())
            .Distinct();

        foreach (var block in blocks)
            block.ClearSelection();
        ReaderLinuxTextFallbackPageVertical.ClearSelection();
    }

    private bool HasLinuxReaderTextFallbackSelection()
    {
        if (!string.IsNullOrWhiteSpace(_readerPendingSelection)
            || ReaderSelectionHostPopup.IsOpen)
        {
            return true;
        }

        return new[]
            {
                ReaderLinuxTextFallbackText,
                ReaderLinuxTextFallbackPageLeft,
                ReaderLinuxTextFallbackPageRight
            }
            .Concat(ReaderLinuxTextFallbackOverlay
                .GetVisualDescendants()
                .OfType<SelectableTextBlock>())
            .Distinct()
            .Any(block => block.SelectionStart != block.SelectionEnd)
            || (ReaderLinuxTextFallbackPageVertical.IsVisible
                && ReaderLinuxTextFallbackPageVertical.SelectionStart
                    != ReaderLinuxTextFallbackPageVertical.SelectionEnd);
    }

    private void SyncLinuxReaderTextFallbackSelectionState(Point? placementPoint = null, double? selectionBottom = null)
    {
        if (!IsLinuxReaderTextFallbackActive())
            return;

        if (_readerLayout.FlowMode == 0)
        {
            ClearLinuxReaderTextFallbackSelectionState();
            return;
        }

        if (ReaderLinuxTextFallbackPageVertical.IsVisible
            && TrySyncLinuxReaderTextFallbackSelectionFromVertical())
        {
            var anchor = GetVerticalReaderSelectionAnchorRect();
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        if (TrySyncLinuxReaderTextFallbackSelectionFromBlock(
                ReaderLinuxTextFallbackPageLeft,
                GetLinuxReaderTextFallbackPageText(0),
                _readerLinuxTextFallbackPageIndex))
        {
            var anchor = GetReaderSelectionAnchorRect(ReaderLinuxTextFallbackPageLeft);
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        if (ReaderLinuxTextFallbackPageRight.IsVisible
            && TrySyncLinuxReaderTextFallbackSelectionFromBlock(
                ReaderLinuxTextFallbackPageRight,
                GetLinuxReaderTextFallbackPageText(1),
                _readerLinuxTextFallbackPageIndex + 1))
        {
            var anchor = GetReaderSelectionAnchorRect(ReaderLinuxTextFallbackPageRight);
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ScheduleLinuxReaderTextFallbackSelectionSync(
        Point placementPoint,
        double? selectionBottom = null)
    {
        var sequence = ++_readerLinuxTextFallbackSelectionSyncSequence;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (sequence != _readerLinuxTextFallbackSelectionSyncSequence
                    || !IsLinuxReaderTextFallbackActive()
                    || ReaderSelectionHostPopup.IsOpen)
                    return;

                // SelectableTextBlock occasionally commits SelectionEnd after
                // its PointerReleased route has completed. Re-read it on the
                // next input frame so a valid drag never loses its toolbar.
                SyncLinuxReaderTextFallbackSelectionState(
                    placementPoint,
                    selectionBottom);
            },
            DispatcherPriority.Input);
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromBlock(
        SelectableTextBlock block,
        string pageText,
        int pageIndex)
    {
        var selectedText = block.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);
        var pageOffset = _readerLayout.FlowMode == 0 ? 0 : GetLinuxReaderTextFallbackPageOffset(pageIndex);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = pageOffset + start;
        _readerPendingSelectionEndOffset = pageOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    private Rect? GetReaderSelectionAnchorRect(SelectableTextBlock block)
    {
        try
        {
            var start = Math.Max(0, Math.Min(block.SelectionStart, block.SelectionEnd));
            var end = Math.Max(0, Math.Max(block.SelectionStart, block.SelectionEnd));
            if (block.TextLayout is not { } layout)
                return null;

            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var boundsBottom = double.NegativeInfinity;
            var lineTop = 0d;
            foreach (var line in layout.TextLines)
            {
                var lineStart = Math.Max(0, line.FirstTextSourceIndex);
                var lineEnd = Math.Max(lineStart, lineStart + line.Length);
                var selectedStart = Math.Max(start, lineStart);
                var selectedEnd = Math.Min(end, lineEnd);
                if (selectedEnd <= selectedStart)
                {
                    lineTop += Math.Max(0, line.Height);
                    continue;
                }

                var segmentLeft = double.PositiveInfinity;
                var segmentRight = double.NegativeInfinity;
                foreach (var textBounds in line.GetTextBounds(
                             selectedStart,
                             selectedEnd - selectedStart))
                {
                    segmentLeft = Math.Min(segmentLeft, textBounds.Rectangle.Left);
                    segmentRight = Math.Max(segmentRight, textBounds.Rectangle.Right);
                }

                if (!double.IsFinite(segmentLeft)
                    || !double.IsFinite(segmentRight))
                {
                    var startHit = layout.HitTestTextPosition(selectedStart);
                    var endHit = layout.HitTestTextPosition(Math.Max(selectedStart, selectedEnd - 1));
                    segmentLeft = selectedStart <= lineStart
                        ? line.Start
                        : startHit.X;
                    segmentRight = selectedEnd >= lineEnd
                        ? line.Start + line.WidthIncludingTrailingWhitespace
                        : endHit.Right;
                }
                if (!double.IsFinite(segmentLeft)
                    || !double.IsFinite(segmentRight)
                    || !double.IsFinite(lineTop)
                    || !double.IsFinite(line.Height))
                {
                    lineTop += Math.Max(0, line.Height);
                    continue;
                }

                left = Math.Min(left, segmentLeft);
                top = Math.Min(top, lineTop);
                right = Math.Max(right, Math.Max(segmentLeft, segmentRight));
                boundsBottom = Math.Max(boundsBottom, lineTop + line.Height);
                lineTop += Math.Max(0, line.Height);
            }

            if (!double.IsFinite(left)
                || !double.IsFinite(top)
                || !double.IsFinite(right)
                || !double.IsFinite(boundsBottom))
                return null;

            var topLeft = block.TranslatePoint(new Point(left, top), ReaderWebViewHost);
            var bottomRight = block.TranslatePoint(new Point(right, boundsBottom), ReaderWebViewHost);
            return topLeft is { } topPoint && bottomRight is { } bottomPoint
                ? new Rect(topPoint, bottomPoint)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromBlock(
        SelectableTextBlock block,
        ReaderLinuxTextFallbackBlock textBlock)
    {
        var selectedText = block.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var pageText = textBlock.Text;
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = textBlock.TextOffset + start;
        _readerPendingSelectionEndOffset = textBlock.TextOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    // Vertical pages keep selection state on the custom drawing control; the
    // offsets map onto the raw page text exactly like the horizontal slots.
    private bool TrySyncLinuxReaderTextFallbackSelectionFromVertical()
    {
        var control = ReaderLinuxTextFallbackPageVertical;
        var selectedText = control.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var pageIndex = _readerLinuxTextFallbackPageIndex;
        if (pageIndex < 0 || pageIndex >= _readerLinuxTextFallbackPageItems.Count)
            return false;
        var item = _readerLinuxTextFallbackPageItems[pageIndex];
        if (item.IsImage)
            return false;

        var pageText = item.Text;
        var start = Math.Min(control.SelectionStart, control.SelectionEnd);
        var end = Math.Max(control.SelectionStart, control.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = item.PaginationOffset + start;
        _readerPendingSelectionEndOffset = item.PaginationOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    private Rect? GetVerticalReaderSelectionAnchorRect()
    {
        try
        {
            var control = ReaderLinuxTextFallbackPageVertical;
            var start = Math.Max(0, Math.Min(control.SelectionStart, control.SelectionEnd));
            var end = Math.Max(0, Math.Max(control.SelectionStart, control.SelectionEnd));
            if (end <= start
                || control.GetRangeAnchorRect(start, end) is not { } localRect)
            {
                return null;
            }

            var topLeft = control.TranslatePoint(localRect.TopLeft, ReaderWebViewHost);
            var bottomRight = control.TranslatePoint(localRect.BottomRight, ReaderWebViewHost);
            return topLeft is { } topPoint && bottomRight is { } bottomPoint
                ? new Rect(topPoint, bottomPoint)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromVisual(
        Visual visual,
        Point placementPoint)
    {
        Visual? current = visual;
        while (current is not null)
        {
            if (current is SelectableTextBlock block
                && block.DataContext is ReaderLinuxTextFallbackBlock textBlock
                && TrySyncLinuxReaderTextFallbackSelectionFromBlock(block, textBlock))
            {
                var anchor = GetReaderSelectionAnchorRect(block);
                ShowReaderSelectionPopup(
                    anchor?.Position ?? placementPoint,
                    anchor?.Bottom);
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }
}
