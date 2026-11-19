using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Infrastructure.Plugins;

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

    private async void BookCard_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsButtonSource(e.Source)) return;
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;

        // The first click already queued the details pane; a double-click is an
        // open action, not a details action.
        CancelPendingBookDetails();
        _selectedBookIds.Clear();
        _selectedBookIds.Add(card.Book.Id);
        _selectedCard = card;
        _multiSelectAnchor = card;
        UpdateMultiSelectionUi();
        e.Handled = true;
        await OpenBookAsync(card);
    }

    private void CollectionFolder_Tapped(object? sender, TappedEventArgs e)
    {
        if (_suppressCollectionTap)
        {
            _suppressCollectionTap = false;
            e.Handled = true;
            return;
        }
        if (sender is not Control { DataContext: BookCollectionFolderViewModel folder }) return;
        _ = OpenCollectionAsync(folder);
        e.Handled = true;
    }

    private void CollectionFolder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control
            || control.DataContext is not BookCollectionFolderViewModel folder
            || IsUncollectedCollection(folder)
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        // Keep a normal press available for CollectionFolder_Tapped. The
        // native drag operation starts only after the pointer crosses the
        // threshold, so opening a collection is still a simple click.
        _pendingCollectionDragFolder = folder;
        _pendingCollectionDragControl = control;
        _pendingCollectionDragPointer = e.Pointer;
        _pendingCollectionDragStartPoint = e.GetPosition(control);
        e.Pointer.Capture(control);
    }

    private void CollectionFolder_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control
            || !ReferenceEquals(control, _pendingCollectionDragControl)
            || !ReferenceEquals(e.Pointer, _pendingCollectionDragPointer)
            || _pendingCollectionDragFolder is not { } folder)
            return;

        if (_collectionDragActive)
        {
            UpdateCollectionDragVisual(e.GetPosition(CollectionDragOverlay));
            e.Handled = true;
            return;
        }

        var delta = e.GetPosition(control) - _pendingCollectionDragStartPoint;
        if (Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) < CollectionDragThreshold)
            return;

        _collectionDragActive = true;
        control.Classes.Set("dragging", true);
        CollectionDragPreviewTitle.Text = folder.Name;
        CollectionDragPreviewCount.Text = folder.BookCountLabel;
        CollectionDragPreviewCover1.Source = folder.Cover1;
        CollectionDragPreviewCover2.Source = folder.Cover2;
        CollectionDragPreviewCover3.Source = folder.Cover3;
        CollectionDragOverlay.IsVisible = true;
        UpdateCollectionDragVisual(e.GetPosition(CollectionDragOverlay));
        e.Handled = true;
    }

    private async void CollectionFolder_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _pendingCollectionDragPointer))
            return;

        if (!_collectionDragActive)
        {
            ResetPendingCollectionDrag();
            return;
        }

        var source = _pendingCollectionDragFolder;
        var target = _collectionDragTargetFolder;
        _suppressCollectionTap = true;
        Dispatcher.UIThread.Post(() => _suppressCollectionTap = false);
        ResetPendingCollectionDrag();
        e.Handled = true;

        if (source is not null
            && target is not null
            && source.Collection.Id != target.Collection.Id
            && !IsUncollectedCollection(target))
            await MergeCollectionAsync(source, target);
    }

    private void CollectionFolder_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Control control && ReferenceEquals(control, _pendingCollectionDragControl))
            ResetPendingCollectionDrag();
    }

    private void ResetPendingCollectionDrag()
    {
        var pointer = _pendingCollectionDragPointer;
        _pendingCollectionDragControl?.Classes.Set("dragging", false);
        _collectionDragTargetControl?.Classes.Set("dropTarget", false);
        _pendingCollectionDragFolder = null;
        _pendingCollectionDragControl = null;
        _pendingCollectionDragPointer = null;
        _pendingCollectionDragStartPoint = default;
        _collectionDragActive = false;
        _collectionDragTargetFolder = null;
        _collectionDragTargetControl = null;
        CollectionDragPreviewCover1.Source = null;
        CollectionDragPreviewCover2.Source = null;
        CollectionDragPreviewCover3.Source = null;
        CollectionDragOverlay.IsVisible = false;
        pointer?.Capture(null);
    }

    private void UpdateCollectionDragVisual(Point pointer)
    {
        if (!_collectionDragActive)
            return;

        var previewWidth = CollectionDragPreview.Bounds.Width > 0
            ? CollectionDragPreview.Bounds.Width
            : 210;
        var previewHeight = CollectionDragPreview.Bounds.Height > 0
            ? CollectionDragPreview.Bounds.Height
            : 142;
        var left = pointer.X - 18;
        var top = pointer.Y - 18;
        if (CollectionDragOverlay.Bounds.Width > 0)
            left = Math.Clamp(left, 0, Math.Max(0, CollectionDragOverlay.Bounds.Width - previewWidth));
        if (CollectionDragOverlay.Bounds.Height > 0)
            top = Math.Clamp(top, 0, Math.Max(0, CollectionDragOverlay.Bounds.Height - previewHeight));
        Canvas.SetLeft(CollectionDragPreview, left);
        Canvas.SetTop(CollectionDragPreview, top);

        var targetControl = FindCollectionFolderControlAt(pointer, out var targetFolder);
        if (targetFolder is null
            || _pendingCollectionDragFolder is null
            || targetFolder.Collection.Id == _pendingCollectionDragFolder.Collection.Id
            || IsUncollectedCollection(targetFolder))
        {
            targetFolder = null;
            targetControl = null;
        }

        if (!ReferenceEquals(_collectionDragTargetControl, targetControl))
        {
            _collectionDragTargetControl?.Classes.Set("dropTarget", false);
            targetControl?.Classes.Set("dropTarget", true);
        }
        _collectionDragTargetFolder = targetFolder;
        _collectionDragTargetControl = targetControl;
    }

    private Control? FindCollectionFolderControlAt(
        Point pointer,
        out BookCollectionFolderViewModel? folder)
    {
        folder = null;
        foreach (var control in CollectionList.GetVisualDescendants().OfType<Control>())
        {
            if (control is not Grid
                || !control.Classes.Contains("collectionFolder")
                || control.DataContext is not BookCollectionFolderViewModel candidate)
                continue;
            if (control.TranslatePoint(default, CollectionDragOverlay) is not { } origin)
                continue;
            if (!new Rect(origin, control.Bounds.Size).Contains(pointer))
                continue;

            folder = candidate;
            return control;
        }

        return null;
    }

    private static bool IsUncollectedCollection(BookCollectionFolderViewModel folder) =>
        string.Equals(
            folder.Collection.Name,
            BookLibraryDefaults.UncollectedCollectionName,
            StringComparison.Ordinal);

    // Right-clicking a custom collection card keeps the protected “未收藏”
    // card out of collection-management actions.
    private void CollectionFolder_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCollectionFolderViewModel folder } control) return;
        if (IsUncollectedCollection(folder))
        {
            e.Handled = true;
            return;
        }
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(T("打开收藏夹"), () => OpenCollectionAsync(folder)));
        menu.Items.Add(CreateMenuItem(T("重命名收藏夹"), () => RenameCollectionAsync(folder)));
        menu.Items.Add(CreateMenuItem(T("清空书籍"), () => ClearCollectionAsync(folder)));

        var mergeTargets = CollectionFolders
            .Where(candidate => candidate.Collection.Id != folder.Collection.Id
                && !IsUncollectedCollection(candidate))
            .ToArray();
        if (mergeTargets.Length > 0)
        {
            menu.Items.Add(new Separator());
            var mergeMenu = new MenuItem { Header = T("合并到…") };
            ApplyLegacyMenuItemSize(mergeMenu);
            mergeMenu.Resources["FlyoutThemeMinWidth"] = 0d;
            foreach (var target in mergeTargets)
                mergeMenu.Items.Add(CreateMenuItem(target.Name, () => MergeCollectionAsync(folder, target)));
            menu.Items.Add(mergeMenu);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("解散收藏夹"), () => DissolveCollectionAsync(folder)));
        menu.Open(control);
        e.Handled = true;
    }

    private void CollectionScroll_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control { DataContext: BookCollectionFolderViewModel })
            return;
        if (sender is not Control source) return;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(T("创建收藏夹"), CreateCollectionAsync));
        menu.Open(source);
        e.Handled = true;
    }

    private async void DeleteSelectedBookButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await DeleteSelectedBookAsync();

    private async void DeleteSelectedBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await DeleteSelectedBooksAsync();

    private void ClearBookMultiSelection()
    {
        _selectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookGrid.SelectedItems?.Clear();
        BookList.SelectedItems?.Clear();
        UpdateMultiSelectionUi();
    }

    private void ClearMultiSelectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ClearBookMultiSelection();

    private async void OpenFileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null && (sender as Button)?.Tag is BookFile file)
            await OpenBookAsync(_selectedCard, file);
    }

    private async void DeleteFileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BookFile file)
            await DeleteFileAsync(file);
    }

    private async void CollectionMembershipButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_updatingDetails)
            await ToggleCollectionMembershipAsync();
    }

    private async void DetailFavoriteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await ToggleFavoriteAsync(_selectedCard);
    }

    private async void DetailReadingStatusButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is null) return;
        var nextStatus = _selectedCard.Book.ReadingStatus switch
        {
            LibraryReadingStatus.Unread => LibraryReadingStatus.Reading,
            LibraryReadingStatus.Reading => LibraryReadingStatus.Finished,
            _ => LibraryReadingStatus.Unread
        };
        await UpdateReadingStatusAsync(_selectedCard, nextStatus);
    }

    private void ClearPendingDetailCover()
    {
        _pendingDetailCover = null;
        _pendingDetailCoverPreview?.Dispose();
        _pendingDetailCoverPreview = null;
    }

    private async void DetailChangeCoverButton_Click(object? sender, RoutedEventArgs e)
    {
        var card = _selectedCard;
        if (card is null) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("选择封面图片"),
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll]
            });
            if (files.Count == 0 || !ReferenceEquals(card, _selectedCard)) return;
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, _lifetimeCancellation.Token);
            var bytes = memory.ToArray();
            using var imageStream = new MemoryStream(bytes);
            var preview = new Bitmap(imageStream);
            if (!ReferenceEquals(card, _selectedCard)) { preview.Dispose(); return; }
            ClearPendingDetailCover();
            using var png = new MemoryStream();
            preview.Save(png, PngBitmapEncoderOptions.Default);
            _pendingDetailCover = png.ToArray();
            _pendingDetailCoverPreview = preview;
            DetailCoverImage.Source = preview;
            DetailCoverPlaceholder.IsVisible = false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ShowMessageAsync(T("无法读取封面"), UiText.Localize(exception.Message));
        }
    }

    private async void SaveDetailsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SaveDetailsAsync();

    private async Task SaveDetailsAsync()
    {
        if (_selectedCard is null) return;
        if (!BookDetailEditing.TryParseRating(DetailOnlineRatingBox.Text, out var rating))
        {
            await ShowMessageAsync(T("评分无效"), T("请输入 0 到 10 之间的评分，或留空清除评分。"));
            return;
        }
        var card = _selectedCard;
        var pendingCover = _pendingDetailCover;
        var title = DetailTitleText.Text?.Trim();
        var authors = DetailAuthorsText.Text?.Trim();
        var tags = DetailTagsBox.Text?.Trim() ?? string.Empty;
        var category = DetailCategoryBox.Text?.Trim() ?? string.Empty;
        var description = DetailDescriptionBox.Text?.Trim();
        var series = DetailSeriesBox.Text?.Trim();
        var publisher = DetailPublisherBox.Text?.Trim();
        var publishDate = DetailPublishDateBox.Text?.Trim();
        var isbn = DetailIsbnBox.Text?.Trim();
        var pageCount = DetailPageCountBox.Text?.Trim();
        var binding = DetailBindingBox.Text?.Trim();
        string? coverPath = null;
        if (pendingCover is not null)
        {
            try
            {
                _paths.EnsureDirectories();
                var path = Path.Combine(_paths.Covers, $"{card.Book.Id:N}-manual-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, pendingCover, _lifetimeCancellation.Token);
                coverPath = Path.GetRelativePath(_paths.Data, path);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await ShowMessageAsync(T("无法保存封面"), UiText.Localize(exception.Message));
                return;
            }
        }
        await SaveBookMetadataAsync(card, book =>
        {
            if (book.OnlineRating != rating) book.OnlineRatingCount = null;
            book.OnlineRating = rating;
            if (coverPath is not null) book.CoverPath = coverPath;
            if (!string.IsNullOrWhiteSpace(title)) book.Title = title;
            if (!string.IsNullOrWhiteSpace(authors)) book.Authors = authors;
            book.Tags = tags;
            book.Category = category;
            book.Description = description;
            book.Series = series;
            book.Publisher = publisher;
            book.PublishDate = publishDate;
            book.Isbn = isbn;
            book.PageCount = pageCount;
            book.Binding = binding;
            return T("书籍信息已保存。");
        });
    }

    private void ConfirmationCancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteConfirmation(false);

    private void ConfirmationOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteConfirmation(true);

    private void ConfirmationAlternativeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ConfirmationOverlay.IsVisible = false;
        _deviceDuplicateCompletion?.TrySetResult(DeviceDuplicateChoice.SendAll);
    }

    private void CollectionNameCancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteCollectionName(null);

    private async void CollectionNameOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = CollectionNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            if (CollectionNameBox.PasswordChar != '\0')
                await ShowMessageAsync(T("密码不能为空"), T("请输入密码"));
            else
            {
                SetTaskStatus(T("收藏夹名称不能为空。"));
                await ShowMessageAsync(T("名称不能为空"), T("请输入收藏夹名称。"));
            }
            return;
        }
        CompleteCollectionName(name);
    }
}
