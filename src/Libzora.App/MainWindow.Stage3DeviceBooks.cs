using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Device book list, selection, filtering and pagination.
/// </summary>
public partial class MainWindow
{
    private async Task OpenKindlePageAsync()
    {
        if (BlockNavigationWhileTransferring()) return;
        ShowStage3Page(DevicePage);
        await RefreshDevicesAsync(scanBooks: false);
        if (CurrentDevice is not null && (!_deviceBooksLoaded || _deviceBooksDirty))
        {
            _deviceBooksDirty = false;
            await RefreshDeviceBooksAsync();
        }
    }

    private async void KindleBooksButton_Click(object? sender, RoutedEventArgs e) => await OpenKindlePageAsync();

    private async void RefreshDevicesButton_Click(object? sender, RoutedEventArgs e)
        => await RefreshDevicesManuallyAsync();

    private async Task RefreshDevicesManuallyAsync()
    {
        _manuallyDisconnectedDeviceId = null;
        if (CurrentDevice is null)
        {
            DevicePageStatusText.Text = T("正在检测设备，请确认设备已连接并解锁…");
        }
        await RefreshDevicesAsync(scanBooks: false);
        if (DevicePage.IsVisible && CurrentDevice is not null)
            await RefreshDeviceBooksAsync();
    }

    private async void ScanDeviceBooksButton_Click(object? sender, RoutedEventArgs e) => await RefreshDeviceBooksAsync();

    private IReadOnlyList<KindleBookCardViewModel> GetSelectedDeviceBooks() =>
        DeviceBooks.Where(book => book.IsSelected).ToArray();

    private void UpdateDeviceBookSelectionUi()
    {
        // 与电脑书库一致：只有真正多选（≥2 本）时卡片才显示 ✓ 徽标，并抑制悬停提示。
        var selected = GetSelectedDeviceBooks();
        var multi = selected.Count > 1;
        var suppressTooltips = multi || _deviceRubberBandSelecting;
        foreach (var book in DeviceBooks)
        {
            book.IsMultiSelected = book.IsSelected && multi;
            book.IsTooltipEnabled = !suppressTooltips;
        }
        if (suppressTooltips)
            CloseOpenToolTips(DeviceBookGridItems, DeviceBookListItems);
        DeviceBookSelectionBar.IsVisible = selected.Count > 1 && !_isTransferring;
        DeviceBookSelectionText.Text = selected.Count == 0 ? string.Empty : T("已选择 {0} 本书", selected.Count);
    }

    private void SetDeviceBookView(bool gridView, bool persist = true)
    {
        _deviceGridView = gridView;
        DeviceBookGridScroll.IsVisible = gridView;
        DeviceBookListScroll.IsVisible = !gridView;
        var viewDescription = $"{T("切换视图")} · {T(gridView ? "网格视图" : "列表视图")}";
        ToolTip.SetTip(DeviceViewToggleButton, viewDescription);
        DeviceViewToggleIcon.Data = Geometry.Parse(gridView ? LibraryGridGlyphData : LibraryListGlyphData);
        AutomationProperties.SetName(DeviceViewToggleButton, viewDescription);
        if (persist)
            ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }

    private void DeviceBookSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_deviceShelfControlsReady) return;
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter();
    }

    private void DeviceBookFilterButton_Click(object? sender, RoutedEventArgs e)
    {
        DeviceBookFilterPanel.IsVisible = !DeviceBookFilterPanel.IsVisible;
        UpdateDeviceBookFilterButton();
    }

    private void UpdateDeviceBookFilterButton() => DeviceBookFilterButton.Classes.Set("active",
        DeviceBookFilterPanel.IsVisible || DeviceBookFormatFilterBox.SelectedIndex > 0
        || DeviceBookPresenceFilterBox.SelectedIndex > 0);

    private void DeviceBookFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_deviceShelfControlsReady) return;
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter();
        if (ReferenceEquals(sender, DeviceBookSortBox))
            ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }

    private void ClearDeviceBookFiltersButton_Click(object? sender, RoutedEventArgs e)
    {
        _deviceShelfControlsReady = false;
        try
        {
            DeviceBookSearchBox.Text = string.Empty;
            DeviceBookFormatFilterBox.SelectedIndex = 0;
            DeviceBookPresenceFilterBox.SelectedIndex = 0;
            DeviceBookSortBox.SelectedIndex = 0;
        }
        finally
        {
            _deviceShelfControlsReady = true;
        }
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter();
        ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }

    private void ApplyDeviceBookFilter()
        => ApplyDeviceBookFilter(resetPage: true);

    private void ApplyDeviceBookFilter(bool resetPage)
    {
        var query = DeviceBookSearchBox.Text?.Trim() ?? string.Empty;
        var format = (DeviceBookFormatFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var presence = (DeviceBookPresenceFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var sort = (DeviceBookSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "title";
        UpdateDeviceBookFilterButton();

        IEnumerable<KindleBookCardViewModel> filtered = DeviceBooks;
        if (query.Length > 0)
        {
            var pinyinQuery = PinyinSearchIndex.NormalizeQuery(query);
            if (pinyinQuery.Length > 0) ScheduleDevicePinyinIndexes();
            filtered = filtered.Where(card =>
                card.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || card.Authors.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || card.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || pinyinQuery.Length > 0 && _devicePinyinIndexes.TryGetValue(card, out var indexed)
                    && indexed.Stamp == DevicePinyinStamp(card) && indexed.Index.Matches(pinyinQuery));
        }
        if (format.Length > 0)
            filtered = filtered.Where(card => card.Book.Format.Equals(format, StringComparison.OrdinalIgnoreCase));
        if (presence == "kindle")
            filtered = filtered.Where(card => card.LibraryPresence == BookLibraryPresence.KindleOnly);
        else if (presence == "both")
            filtered = filtered.Where(card => card.LibraryPresence == BookLibraryPresence.Both);

        filtered = sort switch
        {
            "author" => filtered.OrderBy(card => card.Authors, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            "modified" => filtered.OrderByDescending(card => card.Book.ModifiedAt)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            "size" => filtered.OrderByDescending(card => card.Book.Size)
                .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        _filteredDeviceBooks = filtered.ToArray();
        if (resetPage) _devicePageIndex = 0;
        _devicePageCount = Math.Max(1, (int)Math.Ceiling(_filteredDeviceBooks.Count / (double)DevicePageSize));
        if (_devicePageIndex >= _devicePageCount) _devicePageIndex = _devicePageCount - 1;
        VisibleDeviceBooks.ReplaceAll(_filteredDeviceBooks
            .Skip(_devicePageIndex * DevicePageSize)
            .Take(DevicePageSize));
        RebuildDeviceShelfBooks();
        UpdateDeviceBookPaginationUi();
        UpdateDeviceBookEmptyState();
    }

    private void RebuildDeviceShelfBooks(bool? stackingEnabled = null)
    {
        var query = DeviceBookSearchBox?.Text?.Trim() ?? string.Empty;
        var enabled = (stackingEnabled ?? _appSettings.StackBookSeries) && query.Length == 0;
        var page = VisibleDeviceBooks.ToArray();
        var seriesByDevicePath = new Dictionary<string, (string? Series, double? SeriesIndex)>(StringComparer.OrdinalIgnoreCase);
        if (enabled && page.Length >= 2 && ViewModel.Books.Count > 0)
        {
            var localById = ViewModel.Books.ToDictionary(card => card.Book.Id, card => card.Book);
            var comparison = BookLibraryComparer.Compare(
                ViewModel.Books.Select(card => card.Book),
                page.Select(card => card.Book));
            foreach (var pair in comparison.Matches ?? [])
            {
                if (localById.TryGetValue(pair.LocalBookId, out var localBook)
                    && !string.IsNullOrWhiteSpace(localBook.Series))
                {
                    seriesByDevicePath.TryAdd(pair.DeviceRelativePath, (localBook.Series, localBook.SeriesIndex));
                }
            }
        }
        var shelf = SeriesShelfProjection.Project(
            page,
            enabled,
            card =>
            {
                seriesByDevicePath.TryGetValue(card.Book.RelativePath, out var matchedSeries);
                return new BookSeriesCandidate(
                    card.Book.RelativePath,
                    card.Book.Title,
                    card.Book.Authors,
                    Series: matchedSeries.Series,
                    SeriesIndex: matchedSeries.SeriesIndex,
                    FileTitles: [Path.GetFileNameWithoutExtension(card.Book.FileName)]);
            },
            card => card.Book.RelativePath,
            (card, key, title, members) => card.SetSeriesMembers(key, title, members),
            _deviceSeriesFronts);
        VisibleDeviceShelfBooks.ReplaceAll(shelf);
    }

    private static string DevicePinyinStamp(KindleBookCardViewModel card) =>
        $"{card.Book.Title}\u001f{card.Book.Authors}\u001f{card.Book.FileName}";

    private void ScheduleDevicePinyinIndexes()
    {
        if (_devicePinyinIndexBuilding) return;
        var pending = DeviceBooks.Select(card => (Card: card, Stamp: DevicePinyinStamp(card),
                Title: card.Book.Title, Authors: card.Book.Authors, FileName: card.Book.FileName))
            .Where(item => !_devicePinyinIndexes.TryGetValue(item.Card, out var old) || old.Stamp != item.Stamp)
            .ToArray();
        if (pending.Length == 0) return;
        _devicePinyinIndexBuilding = true;
        _ = BuildDevicePinyinIndexesAsync(pending);
    }

    private async Task BuildDevicePinyinIndexesAsync((KindleBookCardViewModel Card, string Stamp, string Title, string Authors, string FileName)[] pending)
    {
        try
        {
            var built = await Task.Run(() => pending.Select(item =>
                (item.Card, item.Stamp, Index: PinyinSearchIndex.Create(item.Title, item.Authors, item.FileName))).ToArray());
            if (_lifetimeCancellation.IsCancellationRequested) return;
            var current = DeviceBooks.ToHashSet();
            foreach (var item in built)
                if (current.Contains(item.Card) && DevicePinyinStamp(item.Card) == item.Stamp)
                    _devicePinyinIndexes[item.Card] = (item.Stamp, item.Index);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Device pinyin index failed: {exception}");
            return;
        }
        finally { _devicePinyinIndexBuilding = false; }
        if (PinyinSearchIndex.NormalizeQuery(DeviceBookSearchBox.Text).Length > 0)
            ApplyDeviceBookFilter(resetPage: false);
    }

    private void UpdateDeviceBookPaginationUi()
    {
        DevicePaginationBar.IsVisible = _filteredDeviceBooks.Count > DevicePageSize;
        DevicePreviousPageButton.Content = T("上一页");
        DeviceNextPageButton.Content = T("下一页");
        DevicePreviousPageButton.IsEnabled = _devicePageIndex > 0;
        DeviceNextPageButton.IsEnabled = _devicePageIndex + 1 < _devicePageCount;
        DevicePageText.Text = _devicePageCount <= 1
            ? string.Empty
            : T("第 {0} / {1} 页", _devicePageIndex + 1, _devicePageCount);
    }

    private void DevicePreviousPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_devicePageIndex <= 0) return;
        _devicePageIndex--;
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter(resetPage: false);
    }

    private void DeviceNextPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_devicePageIndex + 1 >= _devicePageCount) return;
        _devicePageIndex++;
        ClearDeviceBookSelection();
        ApplyDeviceBookFilter(resetPage: false);
    }

    private void UpdateDeviceBookEmptyState()
    {
        DeviceBookEmptyState.IsVisible = _filteredDeviceBooks.Count == 0;
        if (_filteredDeviceBooks.Count > 0) return;
        DeviceBookEmptyText.Text = DeviceBooks.Count > 0 && _devicePinyinIndexBuilding
            ? T("正在准备拼音搜索…")
            : DeviceBooks.Count > 0
            ? T("没有符合当前搜索或筛选条件的书籍。")
            : CurrentDevice is null
                ? T("无设备书籍")
                : _deviceBookScanFailed
                ? T("设备书库扫描失败。")
                : T("设备中没有可识别的书籍。");
    }

    private void ClearDeviceBookSelection()
    {
        foreach (var book in DeviceBooks)
            book.IsSelected = false;
        _deviceMultiSelectAnchor = null;
        UpdateDeviceBookSelectionUi();
    }

    private void DeviceBook_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsButtonSource(e.Source)) return;
        if (sender is not Control { DataContext: KindleBookCardViewModel card } control
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        if (_deviceGridView)
        {
            SetDeviceRubberBandStart(e.GetPosition(DeviceBookGridScroll));
            _deviceRubberBandSelecting = false;
            _deviceRubberBandPressedOnCard = true;
            _deviceRubberBandPointerSequenceHandled = false;
            e.Pointer.Capture(control);
        }

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            card.IsSelected = !card.IsSelected;
            _deviceMultiSelectAnchor = card;
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            var cards = VisibleDeviceShelfBooks.ToList();
            var clickedIndex = cards.IndexOf(card);
            var anchorIndex = _deviceMultiSelectAnchor is null ? -1 : cards.IndexOf(_deviceMultiSelectAnchor);
            foreach (var candidate in cards) candidate.IsSelected = false;
            var start = anchorIndex < 0 ? clickedIndex : Math.Min(anchorIndex, clickedIndex);
            var end = anchorIndex < 0 ? clickedIndex : Math.Max(anchorIndex, clickedIndex);
            for (var index = start; index <= end; index++) cards[index].IsSelected = true;
            _deviceMultiSelectAnchor = card;
        }
        else
        {
            foreach (var candidate in DeviceBooks) candidate.IsSelected = ReferenceEquals(candidate, card);
            _deviceMultiSelectAnchor = card;
        }

        UpdateDeviceBookSelectionUi();
        e.Handled = true;
    }

    private void DeviceBookGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_deviceBooksShowingCache || !_deviceGridView
            || !e.GetCurrentPoint(DeviceBookGridScroll).Properties.IsLeftButtonPressed)
            return;
        _deviceRubberBandPressedOnCard = IsDeviceBookCardSource(e.Source);
        if (_deviceRubberBandPressedOnCard) return;

        SetDeviceRubberBandStart(e.GetPosition(DeviceBookGridScroll));
        _deviceRubberBandSelecting = false;
        _deviceRubberBandPointerSequenceHandled = false;
        e.Pointer.Capture(DeviceBookGridScroll);
        DeviceBookGridScroll.Focus();
    }

    private void DeviceBookGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_deviceGridView
            || !e.GetCurrentPoint(DeviceBookGridScroll).Properties.IsLeftButtonPressed)
            return;
        _deviceRubberBandPointerPosition = e.GetPosition(DeviceBookGridScroll);
        _deviceRubberBandCurrent = _deviceRubberBandPointerPosition;
        if (!_deviceRubberBandSelecting)
        {
            var deltaX = _deviceRubberBandCurrent.X - _deviceRubberBandStart.X;
            var deltaY = _deviceRubberBandCurrent.Y - _deviceRubberBandStart.Y;
            if (Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) < RubberBandDragThreshold)
                return;
            _deviceRubberBandSelecting = true;
            foreach (var card in DeviceBooks) card.IsSelected = false;
            e.Pointer.Capture(DeviceBookGridScroll);
            DeviceRubberBandRectangle.IsVisible = true;
        }
        UpdateDeviceRubberBandSelection();
        UpdateDeviceRubberBandAutoScroll();
        e.Handled = true;
    }

    private void DeviceBookGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Context requests select the target before right-button release.
        // Only finish an actual left-button selection gesture here.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_deviceRubberBandPointerSequenceHandled) return;
        _deviceRubberBandPointerSequenceHandled = true;
        if (!_deviceRubberBandSelecting)
        {
            if (!_deviceRubberBandPressedOnCard)
                ClearDeviceBookSelection();
            _deviceRubberBandPressedOnCard = false;
            return;
        }
        _deviceRubberBandPointerPosition = e.GetPosition(DeviceBookGridScroll);
        _deviceRubberBandCurrent = _deviceRubberBandPointerPosition;
        UpdateDeviceRubberBandSelection();
        FinishDeviceRubberBandSelection(e.Pointer);
        _deviceRubberBandPressedOnCard = false;
        e.Handled = true;
    }

    private void DeviceBookGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopDeviceRubberBandAutoScroll();
        if (_deviceRubberBandSelecting
            && !ReferenceEquals(e.Pointer.Captured, DeviceBookGridScroll))
            FinishDeviceRubberBandSelection(null);
        _deviceRubberBandPressedOnCard = false;
    }

    private void UpdateDeviceRubberBandSelection()
    {
        var scrollOffsetY = Math.Max(0, DeviceBookGridScroll.Offset.Y);
        var currentContent = new Point(
            _deviceRubberBandCurrent.X,
            _deviceRubberBandCurrent.Y + scrollOffsetY);
        var contentLeft = Math.Min(_deviceRubberBandStartContent.X, currentContent.X);
        var contentTop = Math.Min(_deviceRubberBandStartContent.Y, currentContent.Y);
        var contentWidth = Math.Abs(currentContent.X - _deviceRubberBandStartContent.X);
        var contentHeight = Math.Abs(currentContent.Y - _deviceRubberBandStartContent.Y);
        var selection = new Rect(contentLeft, contentTop, contentWidth, contentHeight);
        var viewportTop = contentTop - scrollOffsetY;
        var viewportBottom = contentTop + contentHeight - scrollOffsetY;
        var visibleTop = Math.Clamp(viewportTop, 0, Math.Max(0, DeviceBookGridScroll.Bounds.Height));
        var visibleBottom = Math.Clamp(viewportBottom, 0, Math.Max(0, DeviceBookGridScroll.Bounds.Height));
        Canvas.SetLeft(DeviceRubberBandRectangle, contentLeft);
        Canvas.SetTop(DeviceRubberBandRectangle, Math.Min(visibleTop, visibleBottom));
        DeviceRubberBandRectangle.Width = contentWidth;
        DeviceRubberBandRectangle.Height = Math.Abs(visibleBottom - visibleTop);

        foreach (var card in VisibleDeviceShelfBooks)
        {
            if (DeviceBookGridItems.ContainerFromItem(card) is not Control container) continue;
            var origin = container.TranslatePoint(default, DeviceBookGridScroll);
            if (origin is not { } point) continue;
            var bounds = new Rect(
                point.X,
                point.Y + scrollOffsetY,
                container.Bounds.Width,
                container.Bounds.Height);
            card.IsSelected = bounds.Intersects(selection);
        }
        _deviceMultiSelectAnchor = VisibleDeviceShelfBooks.FirstOrDefault(card => card.IsSelected);
        UpdateDeviceBookSelectionUi();
    }

    private void SetDeviceRubberBandStart(Point point)
    {
        _deviceRubberBandStart = point;
        _deviceRubberBandCurrent = point;
        _deviceRubberBandPointerPosition = point;
        _deviceRubberBandStartContent = new Point(
            point.X,
            point.Y + Math.Max(0, DeviceBookGridScroll.Offset.Y));
    }

    private void UpdateDeviceRubberBandAutoScroll()
    {
        if (!_deviceRubberBandSelecting
            || !_deviceGridView
            || DeviceBookGridScroll.Viewport.Height <= 0)
        {
            StopDeviceRubberBandAutoScroll();
            return;
        }

        const double edge = 52;
        var pointerY = _deviceRubberBandPointerPosition.Y;
        var viewportHeight = DeviceBookGridScroll.Viewport.Height;
        var maximumOffset = Math.Max(0, DeviceBookGridScroll.Extent.Height - viewportHeight);
        var speed = 0d;
        if (pointerY < edge && DeviceBookGridScroll.Offset.Y > 0)
        {
            var intensity = Math.Clamp((edge - pointerY) / edge, 0, 1);
            speed = -Math.Max(4, 28 * intensity);
        }
        else if (pointerY > viewportHeight - edge && DeviceBookGridScroll.Offset.Y < maximumOffset)
        {
            var intensity = Math.Clamp((pointerY - (viewportHeight - edge)) / edge, 0, 1);
            speed = Math.Max(4, 28 * intensity);
        }

        if (speed == 0)
        {
            StopDeviceRubberBandAutoScroll();
            return;
        }

        _deviceRubberBandAutoScrollDelta = speed;
        _deviceRubberBandAutoScrollTimer.Start();
    }

    private void TickDeviceRubberBandAutoScroll()
    {
        if (!_deviceRubberBandSelecting || !_deviceGridView)
        {
            StopDeviceRubberBandAutoScroll();
            return;
        }

        var maximumOffset = Math.Max(0, DeviceBookGridScroll.Extent.Height - DeviceBookGridScroll.Viewport.Height);
        var nextOffset = Math.Clamp(
            DeviceBookGridScroll.Offset.Y + _deviceRubberBandAutoScrollDelta,
            0,
            maximumOffset);
        if (Math.Abs(nextOffset - DeviceBookGridScroll.Offset.Y) < 0.1)
        {
            StopDeviceRubberBandAutoScroll();
            return;
        }

        DeviceBookGridScroll.Offset = new Vector(DeviceBookGridScroll.Offset.X, nextOffset);
        UpdateDeviceRubberBandSelection();
    }

    private void StopDeviceRubberBandAutoScroll()
    {
        _deviceRubberBandAutoScrollDelta = 0;
        _deviceRubberBandAutoScrollTimer.Stop();
    }

    private void FinishDeviceRubberBandSelection(IPointer? pointer)
    {
        StopDeviceRubberBandAutoScroll();
        _deviceRubberBandSelecting = false;
        _deviceRubberBandPointerSequenceHandled = true;
        pointer?.Capture(null);
        DeviceRubberBandRectangle.IsVisible = false;
        UpdateDeviceBookSelectionUi();
    }

    private static bool IsDeviceBookCardSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Control { DataContext: KindleBookCardViewModel })
                return true;
        }
        return false;
    }

    // 与电脑书库一致：悬停整张卡片时显示黑色细边框。
    private void DeviceBook_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: KindleBookCardViewModel card })
            card.IsHovered = true;
    }

    private void DeviceBook_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: KindleBookCardViewModel card })
            card.IsHovered = false;
    }

    private void DeviceBook_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: KindleBookCardViewModel card } control) return;
        if (!card.IsSelected)
        {
            foreach (var candidate in DeviceBooks) candidate.IsSelected = ReferenceEquals(candidate, card);
            _deviceMultiSelectAnchor = card;
            UpdateDeviceBookSelectionUi();
        }

        var selectedBooks = GetSelectedDeviceBooks().ToArray();
        ShowDeviceBookContextMenu(control, card, selectedBooks);
        e.Handled = true;
    }

    private void ShowDeviceBookContextMenu(Control control, KindleBookCardViewModel card, KindleBookCardViewModel[] selectedBooks) =>
        ShowDeviceBookContextMenuCore(control, card, selectedBooks, allowSeriesActions: true);

    private void ShowDeviceSingleBookContextMenu(Control control, KindleBookCardViewModel card) =>
        ShowDeviceBookContextMenuCore(control, card, [card], allowSeriesActions: false);

    private void ShowDeviceBookContextMenuCore(
        Control control,
        KindleBookCardViewModel card,
        KindleBookCardViewModel[] selectedBooks,
        bool allowSeriesActions)
    {
        var selectedCount = selectedBooks.Length;
        var menu = new ContextMenu();
        if (allowSeriesActions && card.IsSeriesStack)
        {
            menu.Items.Add(BuildDeviceSeriesMenu(card));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(CreateMenuItem(
            selectedCount > 1 ? T("导出到电脑书库（{0}）", selectedCount) : T("导出到电脑书库"),
            selectedCount > 1 ? ExportSelectedDeviceBooksAsync : () => ExportDeviceBookAsync(card)));
        menu.Items.Add(CreateMenuItem(
            selectedCount > 1 ? T("从设备删除所选（{0}）", selectedCount) : T("从设备删除"),
            () => TrackDeviceOperationAsync(() => DeleteSelectedDeviceBooksCoreAsync(selectedBooks))));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("取消选择"), () =>
        {
            foreach (var candidate in DeviceBooks) candidate.IsSelected = false;
            _deviceMultiSelectAnchor = null;
            UpdateDeviceBookSelectionUi();
            return Task.CompletedTask;
        }));
        menu.Open(control);
    }

    private void DeviceBookSelectionChanged(object? sender, RoutedEventArgs e) =>
        UpdateDeviceBookSelectionUi();

    private void ClearDeviceBookSelectionButton_Click(object? sender, RoutedEventArgs e)
        => ClearDeviceBookSelection();

    private async void EjectDeviceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_kindle is null || CurrentDevice is not { } device || _deviceEjectInProgress) return;
        _deviceEjectInProgress = true;
        // MTP sessions (WPD on Windows, libmtp on macOS) are stopped rather
        // than ejected; only mounted USB disks can be safely ejected.
        var isWpd = device.Transport != KindleTransport.MassStorage;
        try
        {
            // Eject always asks first (WinUI reference): WPD sessions are
            // stopped instead of ejected, and active transfers are drained
            // before the removal request is issued.
            if (!await ShowDevicePromptAsync(
                    isWpd ? T("停止访问设备？") : T("安全弹出设备？"),
                    isWpd
                        ? T("Libzora 将等待正在进行的传输完成，再停止访问设备。完成后可拔出连接线。")
                        : T("若有传输任务正在进行，将等待其完成后自动断开。"),
                    isWpd ? T("停止访问") : T("弹出"),
                    T("取消"))) return;

            // Block refresh polling and re-detection before waiting so no new
            // device session can be opened between the final operation and the
            // eject request.
            _lastDeviceIdentity = null;
            // A read-only annotation check can stop immediately; only writes
            // must be allowed to finish.
            CancelKindleAnnotationAnalysis();

            if (_isTransferring || HasActiveDeviceOperations)
            {
                ShowTransferToast(
                    T("正在等待设备操作完成"),
                    T("检测到设备任务正在进行，完成后将自动断开设备。"),
                    isIndeterminate: true);
                try
                {
                    await WaitForActiveDeviceOperationsAsync();
                }
                finally
                {
                    HideTransferToast();
                }
            }

            SetEjectButtonsEnabled(false);
            DevicePageStatusText.Text = T("正在安全弹出设备…");
            // 等待正在进行的设备检测结束：3 秒轮询会打开 WPD/Shell 会话，
            // 若不释放，Windows 会拒绝安全弹出（旧版 StopDeviceAccessAsync
            // 行为）。书库扫描等传输任务已由上面的等待覆盖。
            var detectionDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (_isRefreshingDevices && DateTime.UtcNow < detectionDeadline)
                await Task.Delay(50, _lifetimeCancellation.Token);
            if (_isRefreshingDevices)
                throw new IOException(T("后台设备检测未能及时停止，请稍后重试断开。"));

            await _kindle.EjectAsync(device, _lifetimeCancellation.Token);
            foreach (var book in DeviceBooks) book.Dispose();
            DeviceBooks.Clear();
            _deviceCardsByPath.Clear();
            _libraryPresenceComparison = null;
            VisibleDeviceBooks.Clear();
            VisibleDeviceShelfBooks.Clear();
            _deviceSeriesFronts.Clear();
            _filteredDeviceBooks = [];
            _devicePageIndex = 0;
            _devicePageCount = 1;
            UpdateDeviceBookPaginationUi();
            DeviceBookCountText.Text = "0";
            RefreshLibraryPresenceState();
            UpdateDeviceBookSelectionUi();
            _acceptedDeviceId = null;
            _ignoredDeviceId = null;
            _manuallyDisconnectedDeviceId = device.Identity;
            var disconnectedMessage = isWpd
                ? T("{0} 已停止访问，现在可以安全移除你的设备", device.Name)
                : T("{0} 已安全弹出，现在可以安全移除你的设备", device.Name);
            SetDisconnectedDeviceUi();
            DeviceStorageText.Text = isWpd ? T("设备已停止访问") : T("设备已弹出");
            ShowDeviceStatusToast(disconnectedMessage);
        }
        catch (Exception exception)
        {
            _lastDeviceIdentity = null;
            _manuallyDisconnectedDeviceId = null;
            DevicePageStatusText.Text = T("弹出失败：{0}", UiText.Localize(exception.Message));
            SetEjectButtonsEnabled(true);
            await ShowMessageAsync(T("无法弹出设备"), UiText.Localize(exception.Message));
        }
        finally
        {
            _deviceEjectInProgress = false;
        }
    }

    // Only the lower-left Kindle heading is interactive: connected devices open
    // the remembered model picker, while a disconnected heading starts a fresh
    // detection pass. Storage details and blank card space remain inert.
    private static bool IsButtonSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button) return true;
        }
        return false;
    }

    private void DeviceStatusBox_Tapped(object? sender, TappedEventArgs e)
    {
        if (IsButtonSource(e.Source))
        {
            return;
        }

        if (CurrentDevice is not null)
        {
            ShowDeviceModelPicker();
            e.Handled = true;
            return;
        }

        // Keep the compact status card geometrically stable while detection
        // runs; inserting a temporary second line made it flash and jump.
        if (_isRefreshingDevices) return;
        _manuallyDisconnectedDeviceId = null;
        _ignoredDeviceId = null;
        _lastDeviceIdentity = null;
        _ = RefreshDevicesAsync(scanBooks: DevicePage.IsVisible, _lifetimeCancellation.Token);
        e.Handled = true;
    }

    private void ShowDeviceModelPicker()
    {
        if (CurrentDevice is null) return;

        var menu = new MenuFlyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft
        };
        menu.Items.Add(CreateMenuItem(T("默认名称（设备自带）"), () => ApplyDeviceModelAsync(null)));
        menu.Items.Add(new Separator());
        foreach (var vendor in DeviceModelCatalog.Vendors)
        {
            var vendorMenu = new MenuItem { Header = LocalizeDeviceModelLabel(vendor.Name) };
            foreach (var model in vendor.Models)
                vendorMenu.Items.Add(CreateMenuItem(LocalizeDeviceModelLabel(model), () => ApplyDeviceModelAsync(model)));
            menu.Items.Add(vendorMenu);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("自定义型号…"), () =>
        {
            ShowDeviceModelInput();
            return Task.CompletedTask;
        }));
        AttachInstantMenuHover(menu);
        menu.ShowAt(DeviceStatusBox);
    }

    private static string LocalizeDeviceModelLabel(string value) => value switch
    {
        "Kindle（基础版）" => T("Kindle（基础版）"),
        "Kindle Paperwhite 11 代" => T("Kindle Paperwhite 11 代"),
        "Kindle Paperwhite 12 代" => T("Kindle Paperwhite 12 代"),
        "Kindle Scribe 1 代" => T("Kindle Scribe 1 代"),
        "Kindle Scribe 2 代" => T("Kindle Scribe 2 代"),
        "Kindle Scribe 3 代" => T("Kindle Scribe 3 代"),
        "汉王" => T("汉王"),
        "汉王 N10" => T("汉王 N10"),
        "汉王 N10 Touch" => T("汉王 N10 Touch"),
        "汉王 N10 mini" => T("汉王 N10 mini"),
        "汉王 Clear" => T("汉王 Clear"),
        "汉王 E10" => T("汉王 E10"),
        "汉王 A5" => T("汉王 A5"),
        "掌阅" => T("掌阅"),
        "掌阅 iReader Ocean 2" => T("掌阅 iReader Ocean 2"),
        "掌阅 iReader Ocean 3" => T("掌阅 iReader Ocean 3"),
        "掌阅 iReader Ocean 4" => T("掌阅 iReader Ocean 4"),
        "掌阅 iReader Smart 3" => T("掌阅 iReader Smart 3"),
        "掌阅 iReader Smart 4" => T("掌阅 iReader Smart 4"),
        "掌阅 iReader Smart X" => T("掌阅 iReader Smart X"),
        "掌阅 iReader Light 2" => T("掌阅 iReader Light 2"),
        "掌阅 iReader Light 3" => T("掌阅 iReader Light 3"),
        "掌阅 iReader Neo" => T("掌阅 iReader Neo"),
        _ => value
    };

    private void ShowDeviceModelInput()
    {
        DeviceModelInputBox.Text = _deviceDisplayName ?? CurrentDevice?.Name ?? string.Empty;
        DeviceModelInputOverlay.IsVisible = true;
        DeviceModelInputOverlay.Opacity = 1;
        DeviceModelInputBox.Focus();
        DeviceModelInputBox.SelectAll();
    }

    private async void DeviceModelInputOkButton_Click(object? sender, RoutedEventArgs e)
        => await ApplyDeviceModelInputAsync();

    private async Task ApplyDeviceModelInputAsync()
    {
        var model = DeviceModelInputBox.Text?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            DeviceModelInputStatusText.Text = T("型号不能为空。");
            await ShowMessageAsync(T("型号不能为空"), T("请输入设备型号，或选择“默认名称”。"));
            return;
        }
        DeviceModelInputOverlay.IsVisible = false;
        DeviceModelInputStatusText.Text = string.Empty;
        await ApplyDeviceModelAsync(model);
    }

    private void DeviceModelInputCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        DeviceModelInputOverlay.IsVisible = false;
        DeviceModelInputStatusText.Text = string.Empty;
    }

    private void DeviceModelInputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DeviceModelInputOverlay.IsVisible = false;
            DeviceModelInputStatusText.Text = string.Empty;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ApplyDeviceModelInputAsync();
        }
    }
}
