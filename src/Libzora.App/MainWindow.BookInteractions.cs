using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;

namespace Libzora;

/// <summary>
/// Book card and grid pointer interaction, rubber-band selection and
/// collection drag, plus detail-pane actions.
/// </summary>
public partial class MainWindow
{
    private void BookList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        foreach (var card in e.RemovedItems.OfType<BookCardViewModel>())
            _selectedBookIds.Remove(card.Book.Id);
        foreach (var card in e.AddedItems.OfType<BookCardViewModel>())
            _selectedBookIds.Add(card.Book.Id);
        var selectedCard = e.AddedItems.OfType<BookCardViewModel>().FirstOrDefault();
        if (selectedCard is not null)
        {
            if (_suppressSelectionPane || _suppressNextSelectionPane)
                _suppressNextSelectionPane = false;
            else
                SelectBook(selectedCard);
        }
        UpdateMultiSelectionUi();
    }

    private void BookCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsButtonSource(e.Source)) return;
        if (sender is not Control control || control.DataContext is not BookCardViewModel card)
            return;
        var pointerProperties = e.GetCurrentPoint(control).Properties;
        // 右键按下会先触发 ListBox 的原生选中（早于 ContextRequested），
        // 标记并抑制那一次 SelectionChanged 里的弹详情页。
        _suppressNextSelectionPane = pointerProperties.IsRightButtonPressed;
        if (!pointerProperties.IsLeftButtonPressed)
            return;

        // Pointer capture clears :pointerover on the card, which would make a
        // hover-zoomed cover shrink on every click. Keep the card's hover
        // state alive until the press ends.
        _pressedBookCard = card;

        // 记录框选起点：网格左侧没有空白（卡片从视口左缘开始），从左往右
        // 框选必须允许起点落在卡片上，拖动超过阈值后同样进入框选。
        SetRubberBandStart(e.GetPosition(BookGrid));
        _rubberBandSelecting = false;
        _rubberBandPointerSequenceHandled = false;
        _rubberBandPressedOnCard = true;
        _rubberBandGestureActive = true;
        // Keep the initial gesture on the card so ListBoxItem/ScrollViewer
        // cannot steal movement before the drag crosses the threshold.
        e.Pointer.Capture(control);

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (_selectedBookIds.Remove(card.Book.Id))
                card.IsMultiSelected = false;
            else
                _selectedBookIds.Add(card.Book.Id);
            _selectedCard = card;
            _multiSelectAnchor = card;
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            ApplyCardRangeSelection(card);
        }
        else
        {
            _selectedBookIds.Clear();
            _selectedBookIds.Add(card.Book.Id);
            _selectedCard = card;
            _multiSelectAnchor = card;
            ScheduleBookDetails(card);
            // A rubber-band drag may follow this press. Defer the visible
            // selection to the release so the first card never flashes the
            // reading overlay and scrim while a drag begins.
            e.Handled = true;
            return;
        }

        UpdateMultiSelectionUi();
        e.Handled = true;
    }

    // 悬停整张卡片（封面或文字区）时显示黑色细边框，与选中态一致。
    private void BookCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: BookCardViewModel card })
            card.IsHovered = true;
    }

    private void BookCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: BookCardViewModel card }
            && !ReferenceEquals(card, _pressedBookCard))
            card.IsHovered = false;
    }

    private void BookGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!BookGrid.IsVisible
            || !e.GetCurrentPoint(BookGrid).Properties.IsLeftButtonPressed)
            return;

        // handledEventsToo lets this handler observe presses already owned by
        // the embedded ScrollViewer. Never steal the pointer from its ScrollBar
        // thumb, and ignore the rest of that pointer sequence as rubber-band UI.
        if (IsScrollBarSource(e.Source))
        {
            _rubberBandGestureActive = false;
            _rubberBandPressedOnCard = false;
            _rubberBandSelecting = false;
            return;
        }

        // 按下发生在书籍卡片上时由 BookCard_PointerPressed 处理点选/多选，
        // 松开时不得再清空选择（否则点选的黑边在松开瞬间被抹掉）。
        _rubberBandPressedOnCard = IsBookCardSource(e.Source);
        if (_rubberBandPressedOnCard) return;

        SetRubberBandStart(e.GetPosition(BookGrid));
        _rubberBandSelecting = false;
        _rubberBandPointerSequenceHandled = false;
        _rubberBandGestureActive = true;
        e.Pointer.Capture(BookGrid);
        BookGrid.Focus();
    }

    private void BookGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_rubberBandGestureActive
            || !e.GetCurrentPoint(BookGrid).Properties.IsLeftButtonPressed)
            return;
        _rubberBandPointerPosition = e.GetPosition(BookGrid);
        _rubberBandCurrent = _rubberBandPointerPosition;
        if (!_rubberBandSelecting)
        {
            // 框选支持任意方向：任一轴拖拽超过阈值就启动，避免点选时
            // 轻微晃动被误识别成多选；对角线拖动不需要额外的距离。
            var deltaX = _rubberBandCurrent.X - _rubberBandStart.X;
            var deltaY = _rubberBandCurrent.Y - _rubberBandStart.Y;
            if (Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) < RubberBandDragThreshold)
                return;

            _rubberBandSelecting = true;
            // The press queued a details pane; a marquee drag is not a click.
            CancelPendingBookDetails();
            if (_pressedBookCard is { } pressed)
                pressed.IsHovered = false;
            _pressedBookCard = null;
            _selectedBookIds.Clear();
            e.Pointer.Capture(BookGrid);
            RubberBandRectangle.IsVisible = true;
            // 框选从卡片上起步时，按下那一刻已弹出详情页；进入框选即收起，
            // 避免面板挡住右侧被圈选的书籍。
            if (_rubberBandPressedOnCard && LibraryDetailPane.IsVisible)
                ClearSelectedBook();
        }
        UpdateRubberBandSelection();
        UpdateRubberBandAutoScroll();
        e.Handled = true;
    }

    private void BookGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_rubberBandGestureActive) return;
        _rubberBandGestureActive = false;
        _pressedBookCard = null;
        if (_rubberBandPointerSequenceHandled) return;
        _rubberBandPointerSequenceHandled = true;
        if (!_rubberBandSelecting)
        {
            // 按在卡片上松开：点选结果已由 BookCard_PointerPressed 维护，
            // 但视觉选择特意推迟到松开时才应用，避免起手拖动框选时闪现阅读遮罩。
            if (!_rubberBandPressedOnCard)
            {
                _selectedBookIds.Clear();
                _multiSelectAnchor = null;
            }
            UpdateMultiSelectionUi();
            _rubberBandPressedOnCard = false;
            return;
        }
        _rubberBandPointerPosition = e.GetPosition(BookGrid);
        _rubberBandCurrent = _rubberBandPointerPosition;
        UpdateRubberBandSelection();
        FinishRubberBandSelection(e.Pointer);
        _rubberBandPressedOnCard = false;
        e.Handled = true;
    }

    private void BookGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopRubberBandAutoScroll();
        if (ReferenceEquals(e.Pointer.Captured, BookGrid))
            return;
        if (_rubberBandSelecting)
            FinishRubberBandSelection(null);
        else if (_rubberBandPressedOnCard)
            UpdateMultiSelectionUi();
        _rubberBandGestureActive = false;
        _rubberBandPressedOnCard = false;
        _pressedBookCard = null;
    }

    private void BookGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        StopRubberBandAutoScroll();
        _selectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookGrid.SelectedItems?.Clear();
        UpdateMultiSelectionUi();
    }

    private void SetRubberBandStart(Point point)
    {
        _rubberBandStart = point;
        _rubberBandCurrent = point;
        _rubberBandPointerPosition = point;
        _rubberBandStartContent = new Point(point.X, point.Y + GetBookGridScrollOffsetY());
    }

    private double GetBookGridScrollOffsetY()
    {
        if (_bookGridScrollViewer is { } viewer)
            return Math.Max(0, viewer.Offset.Y);
        return 0;
    }

    private void UpdateRubberBandSelection()
    {
        var scrollOffsetY = GetBookGridScrollOffsetY();
        var currentContent = new Point(
            _rubberBandCurrent.X,
            _rubberBandCurrent.Y + scrollOffsetY);
        var contentLeft = Math.Min(_rubberBandStartContent.X, currentContent.X);
        var contentTop = Math.Min(_rubberBandStartContent.Y, currentContent.Y);
        var contentWidth = Math.Abs(currentContent.X - _rubberBandStartContent.X);
        var contentHeight = Math.Abs(currentContent.Y - _rubberBandStartContent.Y);
        var selection = new Rect(contentLeft, contentTop, contentWidth, contentHeight);

        // The rectangle is painted in viewport coordinates while card bounds
        // are compared in content coordinates. Once the viewport auto-scrolls,
        // keep the part of the rectangle that is currently visible on screen.
        var viewportTop = contentTop - scrollOffsetY;
        var viewportBottom = contentTop + contentHeight - scrollOffsetY;
        var visibleTop = Math.Clamp(viewportTop, 0, Math.Max(0, BookGrid.Bounds.Height));
        var visibleBottom = Math.Clamp(viewportBottom, 0, Math.Max(0, BookGrid.Bounds.Height));
        Canvas.SetLeft(RubberBandRectangle, contentLeft);
        Canvas.SetTop(RubberBandRectangle, Math.Min(visibleTop, visibleBottom));
        RubberBandRectangle.Width = contentWidth;
        RubberBandRectangle.Height = Math.Abs(visibleBottom - visibleTop);

        if (_bookGridPanel is not null)
        {
            foreach (var (index, container) in _bookGridPanel.EnumerateRealizedContainers())
            {
                if (index < 0 || index >= ViewModel.Books.Count) continue;
                var card = ViewModel.Books[index];
                var origin = container.TranslatePoint(default, BookGrid);
                if (origin is not { } point) continue;
                var bounds = new Rect(
                    point.X,
                    point.Y + scrollOffsetY,
                    container.Bounds.Width,
                    container.Bounds.Height);
                if (bounds.Intersects(selection))
                    _selectedBookIds.Add(card.Book.Id);
                else
                    _selectedBookIds.Remove(card.Book.Id);
            }
        }
        _multiSelectAnchor = ViewModel.Books.FirstOrDefault(card => _selectedBookIds.Contains(card.Book.Id));
        UpdateMultiSelectionUi();
    }

    private void UpdateRubberBandAutoScroll()
    {
        if (!_rubberBandSelecting
            || _bookGridScrollViewer is not { } viewer
            || viewer.Viewport.Height <= 0)
        {
            StopRubberBandAutoScroll();
            return;
        }

        const double edge = 52;
        var pointerY = _rubberBandPointerPosition.Y;
        var viewportHeight = viewer.Viewport.Height;
        var maximumOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        var speed = 0d;
        if (pointerY < edge && viewer.Offset.Y > 0)
        {
            var intensity = Math.Clamp((edge - pointerY) / edge, 0, 1);
            speed = -Math.Max(4, 28 * intensity);
        }
        else if (pointerY > viewportHeight - edge && viewer.Offset.Y < maximumOffset)
        {
            var intensity = Math.Clamp((pointerY - (viewportHeight - edge)) / edge, 0, 1);
            speed = Math.Max(4, 28 * intensity);
        }

        if (speed == 0)
        {
            StopRubberBandAutoScroll();
            return;
        }

        _rubberBandAutoScrollDelta = speed;
        _rubberBandAutoScrollTimer.Start();
    }

    private void TickRubberBandAutoScroll()
    {
        if (!_rubberBandSelecting
            || _bookGridScrollViewer is not { } viewer)
        {
            StopRubberBandAutoScroll();
            return;
        }

        var maximumOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        var nextOffset = Math.Clamp(
            viewer.Offset.Y + _rubberBandAutoScrollDelta,
            0,
            maximumOffset);
        if (Math.Abs(nextOffset - viewer.Offset.Y) < 0.1)
        {
            StopRubberBandAutoScroll();
            return;
        }

        viewer.Offset = new Vector(viewer.Offset.X, nextOffset);
        UpdateRubberBandSelection();
    }

    private void StopRubberBandAutoScroll()
    {
        _rubberBandAutoScrollDelta = 0;
        _rubberBandAutoScrollTimer.Stop();
    }

    private void FinishRubberBandSelection(IPointer? pointer)
    {
        StopRubberBandAutoScroll();
        _rubberBandSelecting = false;
        _rubberBandPointerSequenceHandled = true;
        pointer?.Capture(null);
        RubberBandRectangle.IsVisible = false;
        UpdateMultiSelectionUi();
    }

    private void ApplyCardRangeSelection(BookCardViewModel clicked)
    {
        var cards = ViewModel.Books.ToList();
        var clickedIndex = cards.FindIndex(card => ReferenceEquals(card, clicked));
        if (clickedIndex < 0) return;

        var anchorIndex = _multiSelectAnchor is null
            ? -1
            : cards.FindIndex(card => ReferenceEquals(card, _multiSelectAnchor));
        _selectedBookIds.Clear();

        var start = anchorIndex < 0 ? clickedIndex : Math.Min(anchorIndex, clickedIndex);
        var end = anchorIndex < 0 ? clickedIndex : Math.Max(anchorIndex, clickedIndex);
        for (var index = start; index <= end; index++)
            _selectedBookIds.Add(cards[index].Book.Id);

        _selectedCard = clicked;
        _multiSelectAnchor = clicked;
    }
}
