using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Hit testing and pointer input.
/// </summary>
public sealed partial class ReaderLinuxTextFallbackVerticalPage : Control
{
    public int HitTestOffset(Point point)
    {
        _cells ??= BuildCells();
        var bestOffset = -1;
        var bestDistance = 64d * 64;
        foreach (var cell in _cells)
        {
            var bounds = cell.Bounds;
            var dx = point.X < bounds.Left
                ? bounds.Left - point.X
                : point.X > bounds.Right
                    ? point.X - bounds.Right
                    : 0;
            var dy = point.Y < bounds.Top
                ? bounds.Top - point.Y
                : point.Y > bounds.Bottom
                    ? point.Y - bounds.Bottom
                    : 0;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestOffset = cell.Offset;
            }
        }
        return bestOffset;
    }

    /// <summary>
    /// Bounding rectangle of a character range in control coordinates, used to
    /// anchor the shared selection toolbar popup.
    /// </summary>
    public Rect? GetRangeAnchorRect(int start, int end)
    {
        _cells ??= BuildCells();
        Rect? result = null;
        foreach (var cell in _cells)
        {
            if (cell.Offset >= end || cell.Offset + cell.Length <= start) continue;
            result = result is { } current
                ? current.Union(cell.Bounds)
                : cell.Bounds;
        }
        return result;
    }

    public void ClearSelection()
    {
        SelectionStart = 0;
        SelectionEnd = 0;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var offset = HitTestOffset(e.GetPosition(this));
        if (offset < 0) return;

        e.Pointer.Capture(this);
        _draggingSelection = true;
        _selectionAnchor = offset;
        SelectionStart = offset;
        SelectionEnd = offset;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_draggingSelection) return;
        var offset = HitTestOffset(e.GetPosition(this));
        if (offset < 0) return;
        SelectionStart = Math.Min(_selectionAnchor, offset);
        SelectionEnd = Math.Max(_selectionAnchor, offset);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_draggingSelection) return;
        _draggingSelection = false;
        e.Pointer.Capture(null);

        var offset = HitTestOffset(e.GetPosition(this));
        if (offset >= 0)
        {
            SelectionStart = Math.Min(_selectionAnchor, offset);
            SelectionEnd = Math.Max(_selectionAnchor, offset);
        }
        InvalidateVisual();

        if (SelectedText.Length > 0)
        {
            SelectionCommitted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            TryActivateFootnoteAt(e.GetPosition(this));
        }
        e.Handled = true;
    }

    private void TryActivateFootnoteAt(Point position)
    {
        _cells ??= BuildCells();
        foreach (var cell in _cells)
        {
            if (!cell.IsFootnote || !cell.Bounds.Contains(position)) continue;
            if (FootnoteHrefResolver?.Invoke(cell.FootnoteIndex) is not { } href) return;
            FootnoteActivated?.Invoke(this, (href, position));
            return;
        }
        FootnoteDismissed?.Invoke(this, EventArgs.Empty);
    }
}
