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
/// Pointer, wheel and keyboard input.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        SyncScrollBar();
        _scrollBar.Arrange(new Rect(
            Math.Max(0, finalSize.Width - 14),
            0,
            14,
            finalSize.Height));
        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Only a genuine double-click supersedes the deferred first click.
        // Cancelling on every press swallowed the first of two independent
        // single clicks that happened to land within the double-click window.
        if (e.ClickCount > 1)
            CancelPendingPageClick();
        if (IsOverScrollBar(e.GetPosition(this)))
        {
            return;
        }

        Focus();
        StopSelectionAutoPageTurn();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (MapPointToPage(point.Position) is { } annotationMap
                && TryGetAnnotationAt(annotationMap.Page,
                    new Point(annotationMap.Local.X, annotationMap.Local.Y), out var annotation))
            {
                ClearAnnotationHover();
                Emit(new { type = "annotationContextMenu", id = annotation.Id, x = point.Position.X, y = point.Position.Y });
            }
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        _pointerDown = true;
        _pointerClickCount = e.ClickCount;
        _selecting = false;
        _wordSelectionActive = false;
        _pointerDragStarted = false;
        _pointerDownPosition = position;
        _lastPointerPosition = position;
        _hasLastPointerPosition = true;
        ClearAnnotationHover();
        var mapped = MapPointToPage(position);
        _pointerHotZone = mapped is { } downMap
            ? _layout?.GetHotZoneAt(downMap.Page, downMap.Local)
            : null;
        _pointerDownOffset = mapped is { } downMap2
            ? _layout?.HitTest(downMap2.Page, downMap2.Local) ?? -1
            : -1;

        if (_pointerHotZone is { Kind: HotZoneKind.FootnoteMarker } footnote)
        {
            UpdateFootnoteHover(position, footnote);
        }

        var downOverImage = mapped is { } downImgMap && IsBlockImageAt(downImgMap.Page, downImgMap.Local);
        if (_pointerDownOffset >= 0 && _pointerHotZone is null && !downOverImage && !_settings.MangaMode)
        {
            _selectionAnchor = _pointerDownOffset;
            SetActiveSelectionOffset(_pointerDownOffset);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        if (IsOverScrollBar(position))
        {
            return;
        }

        var previousPosition = _lastPointerPosition;
        var hadPreviousPosition = _hasLastPointerPosition;
        _lastPointerPosition = position;
        _hasLastPointerPosition = true;
        if (!_pointerDown)
        {
            UpdateFootnoteHover(position);
            UpdateAnnotationHover(position);
            if (_settings.MangaMode)
            {
                var desired = _mangaZoom > 1.01 ? StandardCursorType.SizeAll : StandardCursorType.Arrow;
                Cursor = new Cursor(desired);
            }
            return;
        }

        if (_pointerHotZone is not null)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        if (_settings.MangaMode && (_mangaZoom > 1.01 || IsScroll))
        {
            if (!_pointerDragStarted)
            {
                var dx = position.X - _pointerDownPosition.X;
                var dy = position.Y - _pointerDownPosition.Y;
                if (dx * dx + dy * dy >= 16)
                {
                    _pointerDragStarted = true;
                }
            }
            if (_pointerDragStarted && hadPreviousPosition)
            {
                var delta = position - previousPosition;
                if (_mangaZoom > 1.01)
                    PanMangaBy(new Vector(-delta.X, -delta.Y));
                else if (IsScroll)
                    ScrollByPixel(-delta.Y);
            }
            e.Handled = true;
            return;
        }

        if (!_pointerDragStarted && _pointerDownOffset >= 0)
        {
            var dx = position.X - _pointerDownPosition.X;
            var dy = position.Y - _pointerDownPosition.Y;
            if (dx * dx + dy * dy >= 16)
            {
                _pointerDragStarted = true;
                _selecting = true;
                _wordSelectionActive = false;
            }
        }

        if (!_selecting || _layout is null)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        var offset = GetSelectionHitOffset(position);
        if (offset < 0)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        SetActiveSelectionOffset(offset);
        var pageTurnDirection = GetSelectionPageTurnDirection(
            offset,
            position,
            hadPreviousPosition ? previousPosition : null);
        if (pageTurnDirection != 0)
        {
            ArmSelectionAutoPageTurn(pageTurnDirection);
        }
        else
        {
            StopSelectionAutoPageTurn();
        }
        _bitmapDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (IsOverScrollBar(e.GetPosition(this)))
        {
            if (_pointerDown)
            {
                // The press started on the page and the release landed on the
                // scrollbar. Clear the same state as a normal release so the
                // capture, selection drag and auto-page-turn timer do not
                // survive into unrelated later mouse movement.
                StopSelectionAutoPageTurn();
                _pointerDown = false;
                _pointerHotZone = null;
                _hasLastPointerPosition = false;
                _selecting = false;
                _wordSelectionActive = false;
                _pointerDragStarted = false;
                _pointerDownOffset = -1;
                _selectionAnchor = -1;
                _selectionActiveOffset = -1;
                _selectionStart = -1;
                _selectionEnd = -1;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            return;
        }

        if (!_pointerDown)
        {
            base.OnPointerReleased(e);
            return;
        }

        var position = e.GetPosition(this);
        var hotZone = _pointerHotZone;
        var wasDrag = _pointerDragStarted;
        StopSelectionAutoPageTurn();
        var insideSurface = IsInsideSurface(position);
        var releasedMap = insideSurface ? MapPointToPage(position) : null;
        var releasedHotZone = releasedMap is { } releaseMap
            ? _layout?.GetHotZoneAt(releaseMap.Page, releaseMap.Local)
            : null;
        var clickSide = insideSurface ? GetPageClickSide(position) : null;
        _pointerDown = false;
        _pointerHotZone = null;
        _hasLastPointerPosition = false;
        e.Pointer.Capture(null);

        if (hotZone is not null)
        {
            // Pointer capture keeps receiving the release after the pointer
            // leaves the page. A link is activated only when the release is
            // still over the same hot-zone; dragging off a marker must not
            // accidentally open a chapter or footnote.
            if (!wasDrag && ReferenceEquals(hotZone, releasedHotZone))
            {
                // The tap point is what the finger used; the painted marker
                // box is what the note card should point at. Both are in
                // control coordinates so the native frontend can place the
                // card without knowing the page/spread/scroll geometry.
                var anchorPage = releasedMap?.Page ?? _pageIndex;
                var pageOrigin = PageOrigin(anchorPage);
                var zoneRect = hotZone.Rect;
                Emit(new
                {
                    type = "link",
                    href = hotZone.Href,
                    footnote = hotZone.Kind == HotZoneKind.FootnoteMarker,
                    navigate = hotZone.Kind == HotZoneKind.FootnoteMarker && _pointerClickCount >= 2,
                    footnoteText = hotZone.FootnoteText,
                    marker = hotZone.Marker,
                    x = position.X,
                    y = position.Y,
                    anchor = new
                    {
                        x = pageOrigin.X + zoneRect.Left,
                        y = pageOrigin.Y + zoneRect.Top,
                        width = zoneRect.Width,
                        height = zoneRect.Height,
                        viewportWidth = Math.Max(1, Bounds.Width),
                        viewportHeight = Math.Max(1, Bounds.Height),
                    },
                });
            }
            else if (hotZone.Kind == HotZoneKind.FootnoteMarker)
            {
                UpdateFootnoteHover(null);
            }

            _selecting = false;
            e.Handled = true;
            return;
        }

        if (_selecting && wasDrag)
        {
            _selecting = false;
            EmitSelection();
            e.Handled = true;
            return;
        }

        // A long press selected a word before the finger was lifted. Keep the
        // painted selection and report it again instead of treating the
        // release as a tap that clears it.
        if (_wordSelectionActive)
        {
            _wordSelectionActive = false;
            EmitSelection();
            e.Handled = true;
            return;
        }

        _selecting = false;
        if (_settings.MangaMode && wasDrag && (_mangaZoom > 1.01 || IsScroll))
        {
            _selecting = false;
            _pointerDownOffset = -1;
            e.Handled = true;
            return;
        }

        // Resolve a mouse double-click only after release. Opening the popup
        // on press would steal input from a possible drag on the second click.
        if (!wasDrag && insideSurface && e.Pointer.Type == PointerType.Mouse
            && _pointerClickCount >= 2)
        {
            if (_settings.MangaMode)
            {
                var nextZoom = _mangaZoom > 1.05 ? 1.0 : 2.0;
                SetMangaZoom(nextZoom, position);
                _wordSelectionActive = false;
                _selectionAnchor = -1;
                _selectionActiveOffset = -1;
                _selectionStart = -1;
                _selectionEnd = -1;
                _pointerDownOffset = -1;
                e.Handled = true;
                return;
            }

            if (releasedMap is { } zoomMap && TryEmitImageZoom(zoomMap.Page, zoomMap.Local))
            {
                _wordSelectionActive = false;
                _selectionAnchor = -1;
                _selectionActiveOffset = -1;
                _selectionStart = -1;
                _selectionEnd = -1;
                _pointerDownOffset = -1;
                _bitmapDirty = true;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (SelectWordAt(position.X, position.Y))
            {
                _wordSelectionActive = false;
                e.Handled = true;
                return;
            }
        }

        if (_settings.MangaMode && _mangaZoom > 1.01)
        {
            _selecting = false;
            _pointerDownOffset = -1;
            e.Handled = true;
            return;
        }

        var clickedAnnotation = releasedMap is { } annotationMap
            && TryGetAnnotationAt(
                annotationMap.Page,
                new Point(annotationMap.Local.X, annotationMap.Local.Y),
                out var annotation)
            ? annotation
            : null;
        if (clickedAnnotation is not null)
        {
            _lastImageTapAt = DateTimeOffset.MinValue;
            Emit(new { type = "annotationClick", id = clickedAnnotation.Id });
        }
        else if (releasedMap is { } objectMap && !wasDrag
            && TryEmitReaderObject(objectMap.Page, objectMap.Local, position.X, position.Y))
        {
            // Figures wait for a double-tap before opening; tables open on
            // single tap, so single-tap fallback is managed by ReaderPage.
        }
        else if (clickSide is not null)
        {
            _lastImageTapAt = DateTimeOffset.MinValue;
            var overBlockImage = releasedMap is { } imageMap
                && IsBlockImageAt(imageMap.Page, imageMap.Local);
            EmitPageClick(clickSide, IsPaginated
                && (_pointerDownOffset >= 0 || overBlockImage || _settings.MangaMode)
                && e.Pointer.Type == PointerType.Mouse);
        }
        else if (_pointerDownOffset >= 0)
        {
            _lastImageTapAt = DateTimeOffset.MinValue;
            EmitSelection();
        }
        else
        {
            _lastImageTapAt = DateTimeOffset.MinValue;
        }

        _selectionAnchor = -1;
        _selectionActiveOffset = -1;
        _selectionStart = -1;
        _selectionEnd = -1;
        _pointerDownOffset = -1;
        _bitmapDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            UpdateFootnoteHover(null);
            UpdateAnnotationHover(null);
        }

        base.OnPointerExited(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        CancelPendingPageClick();
        _lastImageTapAt = DateTimeOffset.MinValue;
        if (_settings.MangaMode && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            var zoomStep = Math.Sign(e.Delta.Y) * 0.25;
            if (zoomStep != 0)
            {
                SetMangaZoom(_mangaZoom + zoomStep, e.GetPosition(this));
            }
            e.Handled = true;
            return;
        }
        if (_settings.MangaMode && _mangaZoom > 1.01)
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            {
                var dx = e.Delta.X != 0 ? -e.Delta.X * 60 : -e.Delta.Y * 60;
                PanMangaBy(new Vector(dx, 0));
            }
            else
            {
                PanMangaBy(new Vector(0, -e.Delta.Y * 60));
            }
            e.Handled = true;
            return;
        }
        // Scroll mode consumes the wheel itself: the offset drives the paint
        // directly and chapter edges are reported through continuousEdge.
        if (IsScroll)
        {
            var scrollDelta = -e.Delta.Y * 120;
            if (scrollDelta != 0)
            {
                ScrollByPixel(scrollDelta);
            }

            e.Handled = true;
            return;
        }

        // Avalonia's positive Y wheel delta means wheel-up; the reader bridge
        // uses the browser convention where positive deltaY advances forward.
        var delta = (int)Math.Round(-e.Delta.Y * 120);
        if (delta != 0)
        {
            Emit(new { type = "wheel", deltaY = (double)delta });
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        CancelPendingPageClick();
        _lastImageTapAt = DateTimeOffset.MinValue;
        var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl && _settings.MangaMode && e.Key is Key.D0 or Key.NumPad0)
        {
            ResetMangaZoom();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key is Key.F or Key.B)
        {
            Emit(new { type = "shortcut", key = e.Key == Key.F ? "f" : "b", ctrlKey = true });
            e.Handled = true;
            return;
        }

        var name = e.Key switch
        {
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            // Continuous mode scrolls by keys instead of turning pages, so the
            // page keys report as arrow keys and reuse the smooth scroll path.
            Key.PageUp => IsScroll ? "ArrowUp" : "PageUp",
            Key.PageDown or Key.Space => IsScroll ? "ArrowDown" : "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Escape => "escape",
            Key.F11 => "f11",
            _ => null,
        };

        if (name is not null)
        {
            if (name is "escape" or "f11")
            {
                Emit(new { type = "shortcut", key = name, ctrlKey = false });
            }
            else
            {
                Emit(new { type = "key", key = name });
            }
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void EmitPageClick(string side, bool deferForDoubleClick)
    {
        if (_pendingPageClick is not null)
        {
            // This click was not cancelled as a double-click (see
            // OnPointerPressed), so it is a second, independent tap. Publish
            // the earlier turn before scheduling this one instead of silently
            // dropping it.
            var pendingSide = _pendingPageClickSide;
            CancelPendingPageClick();
            if (!string.IsNullOrEmpty(pendingSide))
                Emit(new { type = "pageClick", side = pendingSide });
        }
        if (!deferForDoubleClick)
        {
            Emit(new { type = "pageClick", side });
            return;
        }

        // Text in the outer thirds is also a double-click target. Wait out the
        // platform's double-click window before turning so the first click
        // cannot replace the word under the pointer with a different page.
        var layout = _layout;
        var page = _pageIndex;
        var interval = this.GetPlatformSettings()?.GetDoubleTapTime(PointerType.Mouse)
            ?? TimeSpan.FromMilliseconds(400);
        _pendingPageClickSide = side;
        _pendingPageClick = DispatcherTimer.RunOnce(() =>
        {
            _pendingPageClick = null;
            _pendingPageClickSide = null;
            if (!_disposed && ReferenceEquals(_layout, layout) && _pageIndex == page)
            {
                Emit(new { type = "pageClick", side });
            }
        }, interval + TimeSpan.FromMilliseconds(100));
    }

    private void CancelPendingPageClick()
    {
        _pendingPageClick?.Dispose();
        _pendingPageClick = null;
        _pendingPageClickSide = null;
    }

    private string? GetPageClickSide(Point position)
    {
        var width = Math.Max(1, Bounds.Width);
        return position.X < width / 3
            ? "left"
            : position.X > width * 2 / 3
                ? "right"
                : null;
    }

    private bool IsInsideSurface(Point position) =>
        position.X >= 0
        && position.Y >= 0
        && position.X <= Bounds.Width
        && position.Y <= Bounds.Height;

    private void UpdateFootnoteHover(Point? position, PlacedHotZone? knownZone = null)
    {
        PlacedHotZone? zone = knownZone;
        if (zone is null && position is { } point)
        {
            var map = MapPointToPage(point);
            zone = map is { } mapped
                ? _layout?.GetHotZoneAt(mapped.Page, mapped.Local)
                : null;
        }

        if (zone is { Kind: HotZoneKind.FootnoteMarker } footnote
            && position is { } hoverPoint)
        {
            if (!string.Equals(_hoveredFootnoteHref, footnote.Href, StringComparison.Ordinal))
            {
                _hoveredFootnoteHref = footnote.Href;
                Emit(new
                {
                    type = "footnoteHover",
                    href = footnote.Href,
                    footnoteText = footnote.FootnoteText,
                    x = hoverPoint.X,
                    y = hoverPoint.Y,
                });
            }

            return;
        }

        if (_hoveredFootnoteHref is not null)
        {
            _hoveredFootnoteHref = null;
            Emit(new { type = "footnoteLeave" });
        }
    }
}
