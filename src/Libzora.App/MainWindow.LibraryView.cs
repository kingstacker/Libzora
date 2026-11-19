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
/// Filter controls, library UI refresh and book-card visual lifetime.
/// </summary>
public partial class MainWindow
{
    private void SetupFilterControls()
    {
        _updatingFilterControls = true;
        try
        {
            // The empty selection is represented by each ComboBox's placeholder;
            // keep the "全部..." labels out of the popup item list.
            var authors = IncludeActiveFilter(ViewModel.AvailableAuthors, ViewModel.AuthorFilter);
            AuthorFilterBox.ItemsSource = authors;
            TagFilterBox.ItemsSource = IncludeActiveFilter(ViewModel.AvailableTags, ViewModel.TagFilter);
            FormatFilterBox.ItemsSource = IncludeActiveFilter(ViewModel.AvailableFormats, ViewModel.FormatFilter);
            CategoryFilterBox.ItemsSource = IncludeActiveFilter(ViewModel.AvailableCategories, ViewModel.CategoryFilter);
            ReadingStatusFilterBox.ItemsSource = new[] { T("待读"), T("阅读中"), T("已读") };
            FavoritesFilterBox.ItemsSource = new[] { T("收藏"), T("未收藏") };
            LibrarySortBox.ItemsSource = new[] { T("最近更新"), T("标题升序"), T("作者升序"), T("创建时间"), T("进度优先") };

            AuthorFilterBox.SelectedItem = ViewModel.AuthorFilter;
            TagFilterBox.SelectedItem = ViewModel.TagFilter;
            FormatFilterBox.SelectedItem = ViewModel.FormatFilter;
            CategoryFilterBox.SelectedItem = ViewModel.CategoryFilter;
            ReadingStatusFilterBox.SelectedIndex = ViewModel.ReadingStatusFilter is { } readingStatus ? (int)readingStatus : -1;
            LibrarySortBox.SelectedIndex = (int)ViewModel.SortMode;
            FavoritesFilterBox.SelectedIndex = ViewModel.FavoriteFilter switch
            {
                true => 0,
                false => 1,
                _ => -1
            };
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private static string[] IncludeActiveFilter(IEnumerable<string> choices, string? active) =>
        string.IsNullOrWhiteSpace(active)
            ? choices.ToArray()
            : choices.Append(active).Distinct(StringComparer.Ordinal).ToArray();

    private void ViewModel_ViewChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                ApplyCollectionSearchFilter();
                UpdateLibraryUi();
                RefreshMissingBookSyncStatusesIfNeeded();
            });
            return;
        }
        ApplyCollectionSearchFilter();
        UpdateLibraryUi();
        RefreshMissingBookSyncStatusesIfNeeded();
    }

    private void RefreshMissingBookSyncStatusesIfNeeded()
    {
        // The first visit to a page has no cached badge state; without this
        // the new cards keep their default NotSynced status and clicking the
        // badge uploads instead of downloading a cloud-only book.
        if (ViewModel.Books.Any(card => !_bookSyncStatusCache.ContainsKey(card.Book.Id)))
            _ = RefreshMissingBookSyncStatusesAsync();
    }

    private void ApplyCollectionSearchFilter()
    {
        var version = Interlocked.Increment(ref _collectionSearchVersion);
        var previousCancellation = _collectionSearchCancellation;
        _collectionSearchCancellation = null;
        previousCancellation?.Cancel();

        var query = ViewModel.SearchText.Trim();
        if (query.Length == 0 && !ViewModel.HasActiveFilters)
        {
            ApplyCollectionSearchFilterCore(matchingBookCollectionIds: null, query);
            return;
        }

        // The current page is usually the whole result set. Only search beyond
        // it when the result count proves that later pages exist; otherwise a
        // folder whose matching book lives on page 2 would disappear unless
        // its own name happened to match the query.
        var visibleIds = ViewModel.Books
            .SelectMany(card => card.Book.CollectionIds)
            .ToHashSet();
        if (ViewModel.Books.Count >= ViewModel.FilteredBookCount)
        {
            ApplyCollectionSearchFilterCore(visibleIds, query);
            return;
        }

        ApplyCollectionSearchFilterCore(visibleIds, query);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _collectionSearchCancellation = cancellation;
        _ = LoadCollectionSearchMatchesAsync(version, query, cancellation);
    }

    private async Task LoadCollectionSearchMatchesAsync(
        int version,
        string query,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            var current = ViewModel.Books
                .SelectMany(card => card.Book.CollectionIds)
                .ToHashSet();
            var required = CollectionFolders
                .Select(folder => folder.Collection.Id)
                .Where(id => !current.Contains(id))
                .ToHashSet();
            if (required.Count == 0) return;

            var matching = await ViewModel.GetMatchingCollectionIdsAsync(required, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || version != Volatile.Read(ref _collectionSearchVersion))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (version != Volatile.Read(ref _collectionSearchVersion)) return;
                ApplyCollectionSearchFilterCore(matching, query);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // Keep the folders visible from the current page; a failed
            // background refinement must not blank the collections view.
            System.Diagnostics.Debug.WriteLine($"Collection search refinement failed: {exception}");
        }
        finally
        {
            if (ReferenceEquals(_collectionSearchCancellation, cancellation))
                _collectionSearchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyCollectionSearchFilterCore(
        IReadOnlySet<Guid>? matchingBookCollectionIds,
        string query)
    {
        var visible = new List<BookCollectionFolderViewModel>(CollectionFolders.Count);
        foreach (var folder in CollectionFolders)
        {
            var nameMatches = query.Length > 0
                && folder.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            var booksMatch = matchingBookCollectionIds?.Contains(folder.Collection.Id) == true;
            if (matchingBookCollectionIds is null || nameMatches || booksMatch)
                visible.Add(folder);
        }
        FilteredCollectionFolders.ReplaceAllIfChanged(visible);
    }

    private void UpdateLibraryUi()
    {
        var viewDescription = $"{T("切换视图")} · {DescribeLibraryViewMode(_libraryViewMode)}";
        ToolTip.SetTip(LibraryViewToggleButton, viewDescription);
        LibraryGridViewMenuItem.IsChecked = _libraryViewMode == LibraryViewMode.Grid;
        LibraryListViewMenuItem.IsChecked = _libraryViewMode == LibraryViewMode.List;
        LibraryCollectionsViewMenuItem.IsChecked = _libraryViewMode == LibraryViewMode.Collections;
        FilterButton.Classes.Set("active", FilterPanel.IsVisible || ViewModel.HasActiveFilters);
        AutomationProperties.SetName(LibraryViewToggleButton, viewDescription);
        TaskStatusText.Text = ViewModel.StatusText;
        SidebarCountText.Text = ViewModel.BookCount.ToString();
        foreach (var card in ViewModel.Books)
        {
            if (_bookSyncStatusCache.TryGetValue(card.Book.Id, out var cachedSyncStatus))
                card.SetSyncStatus(cachedSyncStatus);
            card.SetGalleryTextVisible(!_appSettings.GridGalleryDisplay);
            card.SetSyncStatusVisible(_appSettings.ShowSyncStatusIcon);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled && _appSettings.ShowLibraryPresenceIcon);
        }
        foreach (var card in DeviceBooks)
        {
            card.SetGalleryTextVisible(!_appSettings.GridGalleryDisplay);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled && _appSettings.ShowLibraryPresenceIcon);
        }
        RefreshLibraryPresenceState();
        SyncCardSelectionVisuals();

        var showingBooks = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List;
        var hasBooks = ViewModel.Books.Count > 0;
        var showingCollections = _libraryViewMode == LibraryViewMode.Collections;
        var collectionsEmpty = FilteredCollectionFolders.Count == 0;
        LibraryPreviousPageButton.Content = T("上一页");
        LibraryNextPageButton.Content = T("下一页");
        LibraryPaginationBar.IsVisible = showingBooks && ViewModel.PageCount > 1;
        LibraryPreviousPageButton.IsEnabled = ViewModel.CanGoToPreviousPage;
        LibraryNextPageButton.IsEnabled = ViewModel.CanGoToNextPage;
        LibraryPageText.Text = ViewModel.PageStatusText;
        EmptyLibraryState.IsVisible = !ViewModel.IsBusy
            && ((showingBooks && !hasBooks) || (showingCollections && collectionsEmpty));

        if (showingCollections && collectionsEmpty)
        {
            EmptyLibraryTitleText.Text = T("还没有收藏夹");
            EmptyLibraryMessageText.Text = T("点击“新建收藏夹”创建，之后可在书籍右键菜单中把书籍加入其中。");
        }
        else if (ViewModel.BookCount == 0)
        {
            EmptyLibraryTitleText.Text = T("电脑书库还是空的");
            EmptyLibraryMessageText.Text = T("导入 EPUB、PDF、MOBI、AZW3 或 TXT 文件开始阅读。");
        }
        else
        {
            EmptyLibraryTitleText.Text = T("没有符合条件的书籍");
            EmptyLibraryMessageText.Text = T("试试清除筛选条件，或换一个搜索词。");
        }

        CollectionHeader.IsVisible = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List
            && ViewModel.CollectionFilterId is not null;
        ActiveCollectionTitleText.Text = UiText.Localize(ViewModel.CollectionFilterName ?? string.Empty);
        CreateCollectionButton.IsVisible = _libraryViewMode == LibraryViewMode.Collections;

        if (_selectedCard is not null)
        {
            var refreshedCard = ViewModel.Books.FirstOrDefault(card => card.Book.Id == _selectedCard.Book.Id);
            if (refreshedCard is not null)
            {
                // A library refresh replaces every card instance. Preserve the
                // selection without turning that data refresh into a request to
                // open the detail pane (for example after format conversion).
                _selectedCard = refreshedCard;
                if (LibraryDetailPane.IsVisible)
                    SelectBook(refreshedCard);
            }
            else
                ClearSelectedBook();
        }
    }

    private void SetTaskStatus(string message)
    {
        TaskStatusText.Text = UiText.Localize(message);
    }

    private void SetLibraryViewMode(LibraryViewMode mode, bool persist = true)
    {
        if (mode == LibraryViewMode.Collections && _selectedCard is not null)
            ClearSelectedBook();
        if (mode != LibraryViewMode.Collections)
            _lastNonCollectionLibraryViewMode = mode;
        _libraryViewMode = mode;
        BookGrid.IsVisible = mode == LibraryViewMode.Grid;
        BookList.IsVisible = mode == LibraryViewMode.List;
        CollectionList.IsVisible = mode == LibraryViewMode.Collections;
        CollectionHeader.IsVisible = mode is LibraryViewMode.Grid or LibraryViewMode.List
            && ViewModel.CollectionFilterId is not null;
        CreateCollectionButton.IsVisible = mode == LibraryViewMode.Collections;
        LibraryViewToggleIcon.Data = Geometry.Parse(mode switch
        {
            LibraryViewMode.List => LibraryListGlyphData,
            LibraryViewMode.Collections => LibraryCollectionsGlyphData,
            _ => LibraryGridGlyphData
        });
        UpdateLibraryUi();        if (persist && mode != LibraryViewMode.Collections)
            ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }

    private static LibrarySortMode ParseLibrarySortMode(string? value) =>
        Enum.TryParse<LibrarySortMode>(value, ignoreCase: true, out var mode)
        && Enum.IsDefined(mode)
            ? mode
            : LibrarySortMode.UpdatedDescending;

    private static LibraryViewMode ParseLibraryViewMode(string? value)
    {
        if (string.Equals(value, nameof(LibraryViewMode.List), StringComparison.OrdinalIgnoreCase))
            return LibraryViewMode.List;
        if (string.Equals(value, nameof(LibraryViewMode.Collections), StringComparison.OrdinalIgnoreCase))
            return LibraryViewMode.Collections;
        return LibraryViewMode.Grid;
    }

    private static string DescribeLibraryViewMode(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.Grid => T("网格视图"),
        LibraryViewMode.List => T("列表视图"),
        _ => T("收藏夹视图")
    };

    private void BookCard_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;
        control.DataContextChanged += BookCard_DataContextChanged;
        UpdateBookCardControl(control);
    }

    private void BookCard_DataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control) UpdateBookCardControl(control);
    }

    private void UpdateBookCardControl(Control control)
    {
        var card = control.DataContext as BookCardViewModel;
        if (_bookCardControls.TryGetValue(control, out var previous))
        {
            if (ReferenceEquals(previous, card)) return;
            ReleaseBookCardControl(control, previous);
        }
        if (card is null) return;

        _bookCardControls[control] = card;
        _visibleBookCards.Add(card);
        card.LoadCover();        if (card.CoverImage is null
            && card.Book.Files.Any(file => string.Equals(file.Format, "pdf", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RestoreVisiblePdfCoverAsync(card);
        }
    }

    private void BookCard_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;
        control.DataContextChanged -= BookCard_DataContextChanged;
        if (_bookCardControls.TryGetValue(control, out var card))
            ReleaseBookCardControl(control, card);
    }

    private void ReleaseBookCardControl(Control control, BookCardViewModel card)
    {
        _bookCardControls.Remove(control);        // Grid and list can both retain a visual for the same card. One
        // view recycling its visual must not release the other view's cover.
        if (_bookCardControls.ContainsValue(card)) return;
        _visibleBookCards.Remove(card);
        // The detail pane may still be displaying this card after its grid
        // container was virtualized away. Keep that one bitmap alive until
        // the selection changes; every other off-screen card can release it.
        if (!ReferenceEquals(card, _selectedCard))
            card.UnloadCover();
    }

    private async Task RestoreVisiblePdfCoverAsync(BookCardViewModel card)
    {
        if (!_pdfCoverRequests.Add(card)) return;

        try
        {
            var cover = await _library.EnsurePdfCoverAsync(
                card.Book.Id,
                _lifetimeCancellation.Token);
            if (string.IsNullOrWhiteSpace(cover)
                || !ViewModel.Books.Any(current => ReferenceEquals(current, card)))
            {
                return;
            }

            card.Book.CoverPath = cover;
            card.Refresh();
            if (_visibleBookCards.Contains(card) || ReferenceEquals(card, _selectedCard))
                card.LoadCover();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A missing/damaged PDF keeps its placeholder; the visible shelf
            // remains usable while a later attach can retry the generation.
        }
        finally
        {
            _pdfCoverRequests.Remove(card);
        }
    }

    private void SelectedCard_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BookCardViewModel.CoverImage) || _selectedCard is null || _pendingDetailCover is not null) return;
        DetailCoverImage.Source = _selectedCard.CoverImage;
        DetailCoverPlaceholder.IsVisible = _selectedCard.CoverImage is null;
    }

    // A single click opens the details pane, but waiting for the platform
    // double-tap window keeps a double-click from flashing that pane before
    // the book opens in the reader.
    private void ScheduleBookDetails(BookCardViewModel card)
    {
        _pendingBookDetailsCard = card;
        var doubleTap = this.GetPlatformSettings()?.GetDoubleTapTime(PointerType.Mouse)
            ?? TimeSpan.FromMilliseconds(400);
        _bookDetailsDelayTimer.Interval = doubleTap + TimeSpan.FromMilliseconds(100);
        _bookDetailsDelayTimer.Stop();
        _bookDetailsDelayTimer.Start();
    }

    private void CancelPendingBookDetails()
    {
        _bookDetailsDelayTimer.Stop();
        _pendingBookDetailsCard = null;
    }

    private void CompletePendingBookDetails()
    {
        _bookDetailsDelayTimer.Stop();
        var card = _pendingBookDetailsCard;
        _pendingBookDetailsCard = null;
        if (card is not null) SelectBook(card);
    }
}
