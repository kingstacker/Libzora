using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Pointer, wheel and keyboard interaction.
/// </summary>
public sealed partial class NativePdfReaderHost
{
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var page = PageAtPoint(point);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed || properties.IsXButton2Pressed)
        {
            Emit(new { type = "pdfHistoryNavigate", direction = properties.IsXButton1Pressed ? -1 : 1 });
            e.Handled = true;
            return;
        }
                if (PdfAnnotTrace.Enabled && page > 0)
        {
            var dump = string.Join("; ", AnnotationsForPage(page).Select(a =>
                a.StartOffset < a.EndOffset
                    ? $"{a.Id.ToString()[..8]} {a.StartOffset}-{a.EndOffset} [{(GetRangeBounds(page, a.StartOffset, a.EndOffset).FirstOrDefault())}]"
                    : $"{a.Id.ToString()[..8]} point {a.Fragment} [{GetPdfPointPosition(a)}]"));
            PdfAnnotTrace.Write($"page {page} annotations: {dump}");
        }
        if (PdfAnnotTrace.Enabled)
            PdfAnnotTrace.Write($"press right={properties.IsRightButtonPressed} left={properties.IsLeftButtonPressed} source={e.Source?.GetType().Name} page={page} point={point.X:F1},{point.Y:F1} sourceIsHost={e.Source == this}");
        if (e.Source != this) return;
        CloseFootnotePreview();
        if (properties.IsRightButtonPressed)
        {
            if (page > 0 && AnnotationAt(point) is { } annotation)
            {
                // Right-clicking an annotation opens the shared PDF tools menu
                // with its edit/delete actions; empty space opens the same tools.
                PdfAnnotTrace.Write($"right-click hit annotation {annotation.Id} ({annotation.SelectedText})");
                SelectAnnotation(annotation.Id);
                Emit(new { type = "annotationContextMenu", id = annotation.Id, x = point.X, y = point.Y });
            }
            else
            {
                ClearAnnotationSelection();
                Emit(new { type = "annotationBlankClick" });
            }
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        Focus();
        if (_handToolMode)
        {
            _handDragStart = point;
            _handDragOrigin = _pan;
            _pointerStart = point;
            _isPanning = true;
            _dragging = false;
            _pointerAnnotationHit = null;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (page == 0)
        {
            ClearSelection();
            Emit(new { type = "annotationBlankClick" });
            return;
        }
        ActivatePage(page);
        EmitPage();
        ClearHover();
        if (_pointAnnotationMode)
        {
            if (AnnotationAt(point) is { } annotation)
                Emit(new { type = "annotationClick", id = annotation.Id });
            else
                EmitPointAnnotation(page, point);
            e.Handled = true;
            return;
        }
        // A press that lands on a highlight/underline still starts a normal
        // drag so text can be re-selected over existing markings. The press is
        // resolved as an annotation pick on release when no drag happened.
        _pointerAnnotationHit = AnnotationAt(point)?.Id;
        _pointerFootnote = _pointerAnnotationHit is null ? FootnoteAt(point) : null;
        _pointerLink = _pointerAnnotationHit is null && _pointerFootnote is null ? LinkAt(point) : null;
        _pointerClickCount = e.ClickCount;
        _selectionPage = page;
        _anchorOffset = HitOffset(page, point, false, readingOrder: true);
        _pointerStart = point;
        _dragging = false;
        _wordSelectionActive = false;
        if (_pointerAnnotationHit is null && _anchorOffset >= 0 && e.ClickCount >= 2 && PageContent is { } content)
        {
            var start = Math.Min(HitOffset(page, point, false), Math.Max(0, content.Text.Length - 1));
            var end = start;
            while (start > 0 && !char.IsWhiteSpace(content.Text[start - 1])) start--;
            while (end < content.Text.Length && !char.IsWhiteSpace(content.Text[end])) end++;
            _pointerStart = null;
            SelectRange(start, end);
        }
        else e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_isPanning)
        {
            var delta = point - _handDragStart;
            var pan = _handDragOrigin - new Vector(delta.X, delta.Y);
            SetPan(pan, allowZoomOverpan: HasZoomOverpan(pan));
            return;
        }
        if (_pointerStart is { } start && _anchorOffset >= 0)
        {
            // A finger that just long-pressed keeps its word selection through
            // small jitter; only an intentional drag replaces it.
            _dragging |= Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y)
                > (_wordSelectionActive ? 16 : 3);
            if (_dragging)
            {
                _wordSelectionActive = false;
                var hit = HitOffset(_selectionPage, point, true, readingOrder: true);
                if (hit >= 0 && _pages.TryGetValue(_selectionPage, out var selectedPage))
                {
                    _selectionRanges = selectedPage.SelectionLayout.GetDragRanges(_anchorOffset, hit);
                    _selectionStart = _selectionRanges.Count > 0 ? _selectionRanges.Min(r => r.Start) : 0;
                    _selectionEnd = _selectionRanges.Count > 0 ? _selectionRanges.Max(r => r.End) : 0;
                    InvalidateVisual();
                }
            }
            return;
        }
        var annotation = AnnotationAt(point);
        var page = PageAtPoint(point);
        UpdateFootnoteHover(point, annotation is null && !_handToolMode && !_pointAnnotationMode);
        Cursor = new Cursor(_handToolMode
            ? StandardCursorType.Hand
            : _pointAnnotationMode
                ? StandardCursorType.Cross
                : annotation is not null || LinkAt(point) is not null
                    ? StandardCursorType.Hand
                    : page > 0 && HitOffset(page, point, false) >= 0
                        ? StandardCursorType.Ibeam
                        : StandardCursorType.Arrow);
        if (annotation is not null && !string.IsNullOrWhiteSpace(annotation.Note))
        {
            if (_hoveredAnnotation != annotation.Id)
            {
                _hoveredAnnotation = annotation.Id;
                Emit(new { type = "annotationHover", id = annotation.Id, note = annotation.Note, x = point.X, y = point.Y });
            }
        }
        else ClearHover();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning)
        {
            _isPanning = false;
            _pointerStart = null;
            e.Pointer.Capture(null);
            Cursor = new Cursor(_handToolMode ? StandardCursorType.Hand : StandardCursorType.Arrow);
            e.Handled = true;
            return;
        }
        if (_pointerStart is null) return;
        var dragged = _dragging;
        var releasePoint = _pointerStart.Value;
        // Read the pending pick before releasing the capture: CaptureLost
        // clears it, since losing capture cancels the click.
        var annotationHit = _pointerAnnotationHit;
        var clickCount = _pointerClickCount;
        var link = _pointerLink;
        var footnote = _pointerFootnote;
        _pointerLink = null;
        _pointerFootnote = null;
        if (PdfAnnotTrace.Enabled) PdfAnnotTrace.Write($"release dragged={dragged} hit={annotationHit}");
        _pointerStart = null;
        _dragging = false;
        _pointerAnnotationHit = null;
        e.Pointer.Capture(null);
        if (dragged) EmitSelection();
        else if (_wordSelectionActive)
        {
            // The word was selected when the long press fired; the finger
            // lifting must not be treated as a blank click that clears it.
            _wordSelectionActive = false;
            EmitSelection();
        }
        else if (footnote is { } note)
        {
            // Touch has no hover, so the mobile frontend needs the note text
            // on the tap that lands on a reference marker.
            Emit(new
            {
                type = "link",
                href = string.Empty,
                footnote = true,
                navigate = clickCount >= 2,
                footnoteText = note.Note.Text,
                marker = note.Note.Marker,
                x = releasePoint.X,
                y = releasePoint.Y,
                anchor = new
                {
                    x = note.Anchor.X,
                    y = note.Anchor.Y,
                    width = note.Anchor.Width,
                    height = note.Anchor.Height,
                    viewportWidth = Math.Max(1, Bounds.Width),
                    viewportHeight = Math.Max(1, Bounds.Height),
                },
            });
        }
        else if (annotationHit is { } hitId)
        {
            // Wait until release to distinguish a double-click from a drag
            // beginning on an existing marking. Opening the editor on press
            // steals capture and leaves no live text selection to copy.
            if (clickCount >= 2)
            {
                Emit(new { type = "annotationClick", id = hitId });
                e.Handled = true;
                return;
            }
            // Single click picks the highlight/underline so Delete (see
            // HandleReaderKey) or a right click can remove it.
            SelectAnnotation(hitId);
            Emit(new { type = "annotationSelect", id = hitId });
        }
        else if (link is not null) FollowLink(link);
        else
        {
            _selectedAnnotationId = null;
            ClearSelection();
            Emit(new { type = "annotationBlankClick" });
        }
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHover();
        ScheduleFootnotePreview(false);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_footnotePopup?.Child?.IsPointerOver == true) return;
        CloseFootnotePreview();
        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            _ = SetZoomAsync(Zoom + Math.Sign(e.Delta.Y) * 0.1, e.GetPosition(this));
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            ScrollBarAutoHide.Show(_horizontal);
            ScrollPanBy(new(-(e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 60, 0));
            return;
        }
        var direction = -Math.Sign(e.Delta.Y);
        if (direction == 0) return;
        if (DisplayMode == PdfReaderDisplayMode.Continuous
            || direction > 0 && _pan.Y < _vertical.Maximum - 1 || direction < 0 && _pan.Y > 1)
            ScrollBy(-e.Delta.Y * 60);
        else Emit(new { type = "page", direction });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        HandleReaderKey(e);
        base.OnKeyDown(e);
    }

    public void HandleReaderKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right
            || e.KeyModifiers == KeyModifiers.None && e.Key == Key.Back)
        {
            Emit(new { type = "pdfHistoryNavigate", direction = e.Key == Key.Right ? 1 : -1 });
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && _selectedAnnotationId is { } id)
        {
            Emit(new { type = "pdfAnnotationDelete", id });
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.Key == Key.A) { SelectRange(0, PageContent?.Text.Length ?? 0); e.Handled = true; }
            else if (e.Key == Key.C && _selectionEnd > _selectionStart) { Emit(new { type = "selectionAction", action = "copy" }); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { _ = SetFitModeAsync(PdfReaderFitMode.Width); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            CloseFootnotePreview();
            if (_pointAnnotationMode)
            {
                _pointAnnotationMode = false;
                Cursor = new Cursor(StandardCursorType.Arrow);
                InvalidateVisual();
            }
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key is Key.PageDown or Key.Space or Key.PageUp && DisplayMode == PdfReaderDisplayMode.Continuous)
        {
            ScrollBy((e.Key == Key.PageUp ? -1 : 1) * Math.Max(60, Viewport.Height - 48));
            e.Handled = true;
        }
        else if (e.Key is Key.PageDown or Key.Right or Key.Space or Key.PageUp or Key.Left)
        {
            Emit(new { type = "page", direction = e.Key is Key.PageUp or Key.Left ? -1 : 1 });
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            ScrollBy(e.Key == Key.Down ? 60 : -60);
            e.Handled = true;
        }
    }

    private ReaderAnnotation? AnnotationAt(Point point)
    {
        var page = PageAtPoint(point);
        if (page == 0) return null;
        foreach (var annotation in AnnotationsForPage(page))
        {
            if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out _, out _)
                && GetPdfPointPosition(annotation) is { } marker
                && (marker.X - point.X) * (marker.X - point.X)
                    + (marker.Y - point.Y) * (marker.Y - point.Y) <= 100)
                return annotation;
            if (annotation.EndOffset > annotation.StartOffset
                && GetAnnotationLocalBounds(page, annotation).Select(rect => rect.TransformToAABB(GetPageTransform(GetPageBounds(page))))
                    .Any(rect => rect.Inflate(4).Contains(point)))
                return annotation;
        }
        return null;
    }

    private void ClearHover()
    {
        if (_hoveredAnnotation is null) return;
        _hoveredAnnotation = null;
        Emit(new { type = "annotationLeave" });
    }

    public string GetSelectedTextForCopy() =>
        _pages.TryGetValue(_selectionPage, out var page)
            ? page.SelectionLayout.GetCopyText(_selectionRanges ?? [new(_selectionStart, _selectionEnd)])
            : string.Empty;

    public string GetSelectedTextForTranslation() =>
        _pages.TryGetValue(_selectionPage, out var page)
            ? page.SelectionLayout.GetTranslationText(_selectionRanges ?? [new(_selectionStart, _selectionEnd)])
            : string.Empty;

    private void EmitSelection()
    {
        var text = GetPageContent(_selectionPage)?.Text ?? string.Empty;
        var start = Math.Clamp(_selectionStart, 0, text.Length);
        var end = Math.Clamp(_selectionEnd, start, text.Length);
        var rects = GetSelectionBounds();
        var placement = rects.Count > 0 ? rects[0] : PageBounds;
        var selectedText = _pages.TryGetValue(_selectionPage, out var selectedPage)
            ? selectedPage.SelectionLayout.GetText(_selectionRanges ?? [new(start, end)]) : text[start..end];
        Emit(new { type = "selection", text = selectedText, startOffset = start, endOffset = end,
            prefix = text[Math.Max(0, start - 16)..start], suffix = text[end..Math.Min(text.Length, end + 16)],
            x = Math.Clamp(placement.Left, 0, Bounds.Width), y = Math.Clamp(placement.Top, 0, Bounds.Height),
            bottom = Math.Clamp(placement.Bottom, 0, Bounds.Height) });
    }
}
