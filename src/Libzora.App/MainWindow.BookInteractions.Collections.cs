using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Collection drag and detail-pane actions.
/// </summary>
public partial class MainWindow
{
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
