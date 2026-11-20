using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Annotation hover, selection and page-turn gestures.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    // Annotation hover mirrors the painted underline overlays: the hit-test
    // uses the exact same GetOverlayRects bands, so the pointer reacts to what
    // is visible on the page (horizontal and vertical layouts included).
    private void UpdateAnnotationHover(Point? position)
    {
        if (position is { } point
            && _hoveredFootnoteHref is null
            && MapPointToPage(point) is { } mapped
            && TryGetAnnotationAt(mapped.Page, new Point(mapped.Local.X, mapped.Local.Y), out var annotation)
            && !string.IsNullOrWhiteSpace(annotation.Note))
        {
            if (_hoveredAnnotationId != annotation.Id)
            {
                _hoveredAnnotationId = annotation.Id;
                Emit(new
                {
                    type = "annotationHover",
                    id = annotation.Id,
                    quote = annotation.SelectedText,
                    note = annotation.Note,
                    x = point.X,
                    y = point.Y,
                });
            }

            return;
        }

        ClearAnnotationHover();
    }

    private void ClearAnnotationHover()
    {
        if (_hoveredAnnotationId is null)
        {
            return;
        }

        _hoveredAnnotationId = null;
        Emit(new { type = "annotationLeave" });
    }

    private bool TryGetAnnotationAt(int pageIndex, Point point, out ReaderAnnotation annotation)
    {
        annotation = null!;
        if (_layout is null
            || pageIndex < 0
            || pageIndex >= _layout.Pages.Count)
        {
            return false;
        }

        EnsureAnnotationHoverBands(pageIndex);
        foreach (var (candidate, bands) in _annotationHoverBands!)
        {
            foreach (var band in bands)
            {
                if (point.X >= band.Left && point.X <= band.Right
                    && point.Y >= band.Top && point.Y <= band.Bottom)
                {
                    annotation = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private void EnsureAnnotationHoverBands(int pageIndex)
    {
        if (_annotationHoverBands is not null
            && _annotationHoverBandsPage == pageIndex
            && ReferenceEquals(_annotationHoverBandsLayout, _layout)
            && _annotationHoverBandsVersion == _annotationsVersion)
        {
            return;
        }

        var bands = new List<(ReaderAnnotation, IReadOnlyList<SKRect>)>();
        if (pageIndex >= 0 && pageIndex < _layout!.Pages.Count)
        {
            var page = _layout.Pages[pageIndex];
            foreach (var candidate in _annotations)
            {
                if (!TryGetAnnotationRange(candidate, out var start, out var end)
                    || end <= start)
                {
                    continue;
                }

                var rects = _layout.GetOverlayRects(
                    page.Index,
                    start,
                    end - start);
                if (rects.Count > 0)
                {
                    bands.Add((candidate, rects));
                }
            }
        }

        _annotationHoverBands = bands;
        _annotationHoverBandsPage = pageIndex;
        _annotationHoverBandsLayout = _layout;
        _annotationHoverBandsVersion = _annotationsVersion;
    }

    private void EmitSelection()
    {
        if (_content is null
            || _selectionStart < 0
            || _selectionEnd <= _selectionStart)
        {
            Emit(new { type = "selection", text = (string?)null, startOffset = 0, endOffset = 0, prefix = string.Empty, suffix = string.Empty });
            return;
        }

        var start = Math.Clamp(_selectionStart, 0, _content.BodyText.Length);
        var end = Math.Clamp(_selectionEnd, start, _content.BodyText.Length);
        if (end <= start)
        {
            Emit(new { type = "selection", text = (string?)null, startOffset = 0, endOffset = 0, prefix = string.Empty, suffix = string.Empty });
            return;
        }
        var text = _content.BodyText[start..end];
        var prefixStart = Math.Max(0, start - 16);
        var prefix = _content.BodyText[prefixStart..start];
        var suffixEnd = Math.Min(_content.BodyText.Length, end + 16);
        var suffix = _content.BodyText[end..suffixEnd];
        var placement = GetSelectionPlacement(start, end);

        Emit(new
        {
            type = "selection",
            text,
            startOffset = start,
            endOffset = end,
            prefix,
            suffix,
            x = placement?.Left,
            y = placement?.Top,
            bottom = placement?.Bottom,
        });
    }

    private int GetSelectionHitOffset(Point position)
    {
        if (_layout is null)
        {
            return -1;
        }

        var mapped = MapPointToPage(position);
        if (mapped is not { } hit)
        {
            return -1;
        }

        var offset = _layout.HitTest(hit.Page, hit.Local);
        if (offset >= 0)
        {
            return offset;
        }

        // Pointer capture continues after the pointer leaves the page. Keep
        // the selection alive at the corresponding text boundary instead of
        // cancelling the auto-turn as soon as HitTest has no glyph to use.
        var page = _layout.Pages[hit.Page];
        if (page.TextStartOffset < 0 || page.TextEndOffset <= page.TextStartOffset)
        {
            return -1;
        }

        var boundaryDirection = GetPointerBoundaryDirection(position);
        if (boundaryDirection == 0)
        {
            // Inside the surface but with no glyph under the pointer (a page
            // margin or inter-column gap): keep the last endpoint instead of
            // killing the selection and the armed auto-turn.
            return _selectionActiveOffset;
        }

        // Once a page has turned, the captured pointer may still be outside
        // the surface at the same side. Preserve the newly established first
        // or last-character endpoint instead of converting it into the whole
        // target page on the next pointer event.
        if (_selectionAutoPageTurnArmed
            && _selectionActiveOffset >= 0
            && ((boundaryDirection > 0 && _selectionActiveOffset == page.TextStartOffset + 1)
                || (boundaryDirection < 0 && _selectionActiveOffset == page.TextEndOffset - 1)))
        {
            return _selectionActiveOffset;
        }

        return boundaryDirection > 0 ? page.TextEndOffset : page.TextStartOffset;
    }

    private int GetPointerBoundaryDirection(Point position)
    {
        if (Vertical)
        {
            // vertical-rl advances to the physical left: dragging past the
            // left edge enters the next page and past the right edge returns
            // to the previous page, matching the page-click hot zones.
            if (position.X < 0)
            {
                return 1;
            }

            if (position.X > Bounds.Width)
            {
                return -1;
            }

            return 0;
        }

        if (position.X > Bounds.Width || position.Y > Bounds.Height)
        {
            return 1;
        }

        if (position.X < 0 || position.Y < 0)
        {
            return -1;
        }

        return 0;
    }

    private int GetSelectionPageTurnDirection(
        int offset,
        Point position,
        Point? previousPosition)
    {
        if (_layout is null
            || _selectionAnchor < 0
            || MapPointToPage(position) is not { } mapped
            || mapped.Page < 0
            || mapped.Page >= _layout.Pages.Count)
        {
            return 0;
        }

        var page = _layout.Pages[mapped.Page];
        var atPageStart = ReaderSelectionPagingPolicy.IsAtPageStart(
            offset,
            page.TextStartOffset,
            page.TextEndOffset);
        var atPageEnd = ReaderSelectionPagingPolicy.IsAtPageEnd(
            offset,
            page.TextStartOffset,
            page.TextEndOffset);
        if (!atPageStart && !atPageEnd)
        {
            return 0;
        }

        var logicalDirection = Math.Sign(offset - _selectionAnchor);
        if (logicalDirection == 0)
        {
            return 0;
        }

        // Classic edge: the endpoint reached this page's boundary in the
        // direction of extension. IsAtPageStart/IsAtPageEnd allow a
        // one-character tolerance so a drag at the edge does not stick.
        if (atPageEnd && logicalDirection > 0)
        {
            return GateSelectionPageTurn(1, position, previousPosition);
        }

        if (atPageStart && logicalDirection < 0)
        {
            return GateSelectionPageTurn(-1, position, previousPosition);
        }

        // Continuation: after a cross-page turn the endpoint stays parked on
        // the entry boundary of the new page (its first character when
        // extending forward, its last character when extending backward), so
        // keep turning while the drag holds there. Requiring an armed turn in
        // the same direction keeps a selection that merely starts at a page
        // boundary from turning without ever having crossed a page.
        if (_selectionAutoPageTurnArmed
            && _selectionAutoPageTurnDirection == logicalDirection)
        {
            if (logicalDirection > 0 && atPageStart)
            {
                return GateSelectionPageTurn(1, position, previousPosition);
            }

            if (logicalDirection < 0 && atPageEnd)
            {
                return GateSelectionPageTurn(-1, position, previousPosition);
            }
        }

        return 0;
    }

    private int GateSelectionPageTurn(int direction, Point position, Point? previousPosition)
    {
        // A real drag reversing toward the anchor releases the page edge;
        // sub-dead-zone tremor must not reset the countdown.
        if (previousPosition is { } previous)
        {
            var dragDirection = GetDragTurnDirection(position, previous);
            if (dragDirection != 0 && dragDirection != direction)
            {
                return 0;
            }
        }

        return direction;
    }

    /// <summary>
    /// Physical drag direction expressed in logical page turns: +1 while the
    /// pointer moves toward the next page, -1 toward the previous page.
    /// Movements within the dead zone count as stationary.
    /// </summary>
    private int GetDragTurnDirection(Point position, Point previous)
    {
        const double DeadZone = 4.0;
        if (Vertical)
        {
            // vertical-rl presents the next page to the physical left,
            // matching the page-click hot zones.
            var deltaX = position.X - previous.X;
            return Math.Abs(deltaX) < DeadZone ? 0 : -Math.Sign(deltaX);
        }

        var xDelta = position.X - previous.X;
        if (Math.Abs(xDelta) >= DeadZone)
        {
            return Math.Sign(xDelta);
        }

        var yDelta = position.Y - previous.Y;
        return Math.Abs(yDelta) < DeadZone ? 0 : Math.Sign(yDelta);
    }

    private void ArmSelectionAutoPageTurn(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        if (_selectionAutoPageTurnArmed
            && _selectionAutoPageTurnDirection == direction)
        {
            return;
        }

        _selectionAutoPageTurnArmed = true;
        _selectionAutoPageTurnDirection = direction;
        _selectionAutoPageTurnTimer ??= new DispatcherTimer
        {
            Interval = SelectionAutoPageTurnDelay,
        };
        _selectionAutoPageTurnTimer.Stop();
        _selectionAutoPageTurnTimer.Tick -= SelectionAutoPageTurnTimer_Tick;
        _selectionAutoPageTurnTimer.Tick += SelectionAutoPageTurnTimer_Tick;
        _selectionAutoPageTurnTimer.Start();
    }

    private void StopSelectionAutoPageTurn()
    {
        _selectionAutoPageTurnArmed = false;
        _selectionAutoPageTurnDirection = 0;
        _selectionAutoPageTurnTimer?.Stop();
    }

    private void SelectionAutoPageTurnTimer_Tick(object? sender, EventArgs e)
    {
        _selectionAutoPageTurnTimer?.Stop();
        var direction = _selectionAutoPageTurnDirection;
        if (_disposed
            || !_selectionAutoPageTurnArmed
            || !_pointerDown
            || !_selecting
            || _selectionAnchor < 0
            || _layout is null
            || direction == 0
            || !CanTurn(direction))
        {
            StopSelectionAutoPageTurn();
            return;
        }

        if (!ContinueSelectionAcrossPage(direction))
        {
            StopSelectionAutoPageTurn();
            return;
        }

        // Keep advancing once per second while the pointer remains held at
        // the page edge. Every turn resets the endpoint to the target page's
        // first/last character boundary, so a page is never implicitly added
        // in its entirety merely because it became visible.
        _selectionAutoPageTurnTimer!.Start();
    }

    private bool ContinueSelectionAcrossPage(int direction)
    {
        direction = Math.Sign(direction);
        if (_layout is null || direction == 0 || !TurnPage(direction))
        {
            return false;
        }

        var page = _layout.Pages[_pageIndex];
        var endpoint = ReaderSelectionPagingPolicy.GetCrossPageEndpoint(
            direction,
            page.TextStartOffset,
            page.TextEndOffset);
        if (endpoint >= 0)
        {
            SetActiveSelectionOffset(endpoint);
        }

        _bitmapDirty = true;
        InvalidateVisual();
        Emit(new { type = "selectionPageTurn", direction });
        return true;
    }

    private void SetActiveSelectionOffset(int offset)
    {
        if (_selectionAnchor < 0)
        {
            return;
        }

        var maximum = _layout?.BodyTextLength
            ?? _content?.BodyText.Length
            ?? Math.Max(_selectionAnchor, offset);
        _selectionActiveOffset = Math.Clamp(offset, 0, maximum);
        _selectionStart = Math.Min(_selectionAnchor, _selectionActiveOffset);
        _selectionEnd = Math.Max(_selectionAnchor, _selectionActiveOffset);
    }

    private (float Left, float Top, float Bottom)? GetSelectionPlacement(int start, int end)
    {
        if (_layout is null)
        {
            return null;
        }

        // The selection bands live on the composed page that shows them. For
        // spread/scroll presentations several pages are visible; anchor to
        // the first visible page that actually paints part of the selection.
        foreach (var pageIndex in VisiblePageIndexes())
        {
            var placement = GetSelectionPlacementOnPage(pageIndex, start, end);
            if (placement is { } result)
            {
                var origin = PageOrigin(pageIndex);
                return (result.Left + (float)origin.X, result.Top + (float)origin.Y, result.Bottom + (float)origin.Y);
            }
        }

        return null;
    }

    private IEnumerable<int> VisiblePageIndexes()
    {
        if (_layout is null)
        {
            yield break;
        }

        if (IsSpread)
        {
            yield return _pageIndex;
            if (_pageIndex + 1 < _layout.Pages.Count)
            {
                yield return _pageIndex + 1;
            }

            yield break;
        }

        if (IsScroll)
        {
            yield return VisibleFirstPageIndex;
            if (VisibleFirstPageIndex + 1 < _layout.Pages.Count)
            {
                yield return VisibleFirstPageIndex + 1;
            }

            yield break;
        }

        yield return _pageIndex;
    }

    private (float Left, float Top, float Bottom)? GetSelectionPlacementOnPage(int pageIndex, int start, int end)
    {
        if (_layout is null
            || pageIndex < 0
            || pageIndex >= _layout.Pages.Count)
        {
            return null;
        }

        var rects = _layout.GetOverlayRects(pageIndex, start, end - start);
        if (rects.Count == 0)
        {
            // A forward cross-page selection ends immediately before the new
            // page's first character, so it has no painted selection band on
            // that page yet. Anchor the popup to that first character. The
            // reverse case is the matching position immediately after the
            // previous page's last character.
            var boundary = _selectionActiveOffset >= 0
                ? _selectionActiveOffset
                : end;
            var boundaryCharacter = _selectionActiveOffset >= _selectionAnchor
                ? boundary
                : boundary - 1;
            var page = _layout.Pages[pageIndex];
            // Only this page's actual selection boundary is a valid fallback.
            // Falling back to an arbitrary page edge lets the first visible
            // page steal the popup anchor when the selection is on the next
            // page of a spread.
            if (boundaryCharacter >= 0
                && boundaryCharacter >= page.TextStartOffset
                && boundaryCharacter < page.TextEndOffset
                && _layout.GetCharRect(pageIndex, boundaryCharacter) is { } rect)
            {
                return (rect.Left, rect.Top, rect.Bottom);
            }

            return null;
        }

        var firstBand = rects[0];
        // In a spread, later wrapped lines can start farther left than the
        // selected text's first line. Anchoring to the minimum X across every
        // band puts the floating bar at the page margin instead of beside the
        // selection. GetOverlayRects preserves text order, so its first band
        // is the correct horizontal anchor.
        var left = IsSpread
            ? firstBand.Left
            : rects.Min(rect => rect.Left);
        return (
            left,
            rects.Min(rect => rect.Top),
            rects.Max(rect => rect.Bottom));
    }

    private (int Start, int End) GetWordSelectionRange(int offset)
    {
        var text = _content!.BodyText;
        // BodyText is the DOM text-node concatenation: adjacent paragraphs and
        // <br> elements need not contribute whitespace. Keep inline emphasis
        // within a word, but never join words across a structural break.
        foreach (var block in _content.Blocks)
        {
            var segmentStart = int.MaxValue;
            var segmentEnd = 0;
            var containsOffset = false;
            foreach (var item in block.Items)
            {
                if (item.Kind is InlineKind.LineBreak or InlineKind.Image)
                {
                    if (containsOffset) break;
                    segmentStart = int.MaxValue;
                    segmentEnd = 0;
                    continue;
                }

                if (item.TextStart < 0 || item.Text.Length == 0) continue;
                var itemEnd = item.TextStart + item.Text.Length;
                segmentStart = Math.Min(segmentStart, item.TextStart);
                segmentEnd = Math.Max(segmentEnd, itemEnd);
                containsOffset |= offset >= item.TextStart && offset < itemEnd;
            }

            if (containsOffset)
            {
                var (start, end) = ReaderWordSelection.GetRange(
                    text[segmentStart..segmentEnd], offset - segmentStart);
                return (segmentStart + start, segmentStart + end);
            }
        }

        return ReaderWordSelection.GetRange(text, offset);
    }

    /// <summary>
    /// Selects the character or word under a long press or mouse double-click.
    /// Links and existing annotations keep their tap behavior instead.
    /// </summary>
    public bool SelectWordAt(double x, double y)
    {
        var position = new Point(x, y);
        if (_layout is null || _content is null || IsOverScrollBar(position))
        {
            return false;
        }

        if (MapPointToPage(position) is not { } mapped
            || _layout.GetHotZoneAt(mapped.Page, mapped.Local) is not null
            || IsBlockImageAt(mapped.Page, mapped.Local)
            || TryGetAnnotationAt(mapped.Page, new Point(mapped.Local.X, mapped.Local.Y), out _))
        {
            return false;
        }

        var offset = _layout.HitTestCharacter(mapped.Page, mapped.Local);
        if (offset < 0)
        {
            return false;
        }

        var (start, end) = GetWordSelectionRange(offset);
        if (end <= start)
        {
            return false;
        }

        CancelPendingPageClick();
        StopSelectionAutoPageTurn();
        _selecting = false;
        _wordSelectionActive = true;
        _pointerDownOffset = offset;
        _selectionAnchor = start;
        _selectionActiveOffset = end;
        _selectionStart = start;
        _selectionEnd = end;
        _bitmapDirty = true;
        InvalidateVisual();
        EmitSelection();
        return true;
    }

    public void ClearSelection()
    {
        CancelPendingPageClick();
        _selecting = false;
        _wordSelectionActive = false;
        StopSelectionAutoPageTurn();
        _selectionActiveOffset = _selectionStart = _selectionEnd = _selectionAnchor = -1;
        _hasLastPointerPosition = false;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    // ---- snapshot provider (page transition effects) -----------------------
}
