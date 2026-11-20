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
/// Book selection, the detail pane animation and library refresh.
/// </summary>
public partial class MainWindow
{
    private void SelectBook(BookCardViewModel card)
    {
        CancelPendingBookDetails();
        ClearPendingDetailCover();
        if (_selectedCard is not null) _selectedCard.PropertyChanged -= SelectedCard_PropertyChanged;
        _selectedCard = card;
        card.PropertyChanged += SelectedCard_PropertyChanged;
        card.LoadCover();
        DetailCoverImage.Source = card.CoverImage;
        DetailCoverPlaceholder.IsVisible = card.CoverImage is null;
        DetailTitleText.Text = card.Title;
        DetailAuthorsText.Text = card.Authors;
        DetailFormatText.Text = $"{card.FormatLabel} · {card.ReadingStateLabel}";
        DetailOnlineRatingBox.Text = card.Book.OnlineRating is null
            ? string.Empty
            : card.Book.OnlineRating.Value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);
        DetailStateText.Text = card.ReadingStateLabel;
        DetailOrganizationText.Text = card.OrganizationLabel;
        DetailPublicationText.Text = card.PublicationLabel;
        DetailIdentifierText.Text = card.IdentifierLabel;
        DetailDescriptionText.Text = card.DescriptionLabel;
        DetailTagsBox.Text = card.Book.Tags;
        DetailCategoryBox.Text = card.Book.Category;
        UpdateDetailCollectionsSelector(card);
        DetailDescriptionBox.Text = card.Book.Description ?? string.Empty;
        DetailSeriesBox.Text = card.Book.Series ?? string.Empty;
        DetailPublisherBox.Text = card.Book.Publisher ?? string.Empty;
        DetailPublishDateBox.Text = card.Book.PublishDate ?? string.Empty;
        DetailIsbnBox.Text = card.Book.Isbn ?? string.Empty;
        DetailPageCountBox.Text = card.Book.PageCount ?? string.Empty;
        DetailBindingBox.Text = card.Book.Binding ?? string.Empty;
        UpdateDetailActionIcons(card.Book.IsFavorite, card.Book.ReadingStatus);
        DetailFiles.ItemsSource = card.Book.Files;

        _updatingDetails = true;
        try
        {
            DetailCollectionBox.ItemsSource = CollectionFolders;
            DetailCollectionBox.SelectedItem = CollectionFolders.FirstOrDefault(folder =>
                card.Book.CollectionIds.Contains(folder.Collection.Id));
            CollectionMembershipButton.Content = DetailCollectionBox.SelectedItem is null ? T("加入收藏夹") : T("移出收藏夹");
        }
        finally
        {
            _updatingDetails = false;
        }

        ShowLibraryDetailPane();
        UpdateSeriesShelfOverlayPlacement();
        _detailPaneBookId = card.Book.Id;
        SyncCardSelectionVisuals();
        _ = RefreshBookReflectionDetailsAsync(card.Book.Id);
    }

    private string DescribeBookCollections(BookCardViewModel card)
    {
        var customCollectionNames = CollectionFolders
            .Where(folder => !string.Equals(
                folder.Collection.Name,
                BookLibraryDefaults.UncollectedCollectionName,
                StringComparison.Ordinal)
                && card.Book.CollectionIds.Contains(folder.Collection.Id))
            .Select(folder => folder.Name)
            .ToArray();
        return customCollectionNames.Length > 0
            ? string.Join("、", customCollectionNames)
            : T("未收藏");
    }

    private void UpdateDetailCollectionsSelector(BookCardViewModel? card)
    {
        DetailCollectionsValue.Text = card is null ? string.Empty : DescribeBookCollections(card);
    }

    // The pane uses one render-only translation. Its content is populated
    // before this method runs, avoiding layout and image updates mid-slide.
    private void ShowLibraryDetailPane()
    {
        var wasVisible = LibraryDetailPane.IsVisible;
        var token = BeginDetailPaneAnimation();
        var version = ++_detailPaneAnimationVersion;
        if (!wasVisible)
        {
            UpdateLibraryDetailColumnWidth(0);
            LibraryToolbarActions.RenderTransform = new TranslateTransform(
                LibraryDetailPane.Width + LibraryDetailPane.Margin.Left + LibraryDetailPane.Margin.Right, 0);
            var translate = new TranslateTransform(LibraryDetailWidth, 0);
            LibraryDetailPane.RenderTransform = translate;
            LibraryDetailPane.Opacity = 1;
            LibraryDetailPane.IsVisible = true;
            _ = AnimateLibraryDetailPaneInCoreAsync(version, token, translate, LibraryDetailWidth);
        }
        else
        {
            var currentX = (LibraryDetailPane.RenderTransform as TranslateTransform)?.X ?? 0;
            LibraryDetailPane.IsVisible = true;
            LibraryDetailPane.Opacity = 1;
            if (Math.Abs(currentX) > 0.5)
            {
                var translate = new TranslateTransform(currentX, 0);
                LibraryDetailPane.RenderTransform = translate;
                _ = AnimateLibraryDetailPaneInCoreAsync(version, token, translate, currentX);
            }
            else
            {
                LibraryDetailPane.RenderTransform = new TranslateTransform(0, 0);
            }
        }
    }

    private async Task AnimateLibraryDetailPaneInCoreAsync(
        int version,
        CancellationToken token,
        TranslateTransform translate,
        double fromX)
    {
        try
        {
            // Give the newly visible pane one frame to finish measuring before
            // movement starts, so layout work cannot interrupt the first step.
            await Task.Delay(16, token);
            await SlideLibraryDetailPaneAsync(translate, fromX, 0, opening: true, token);
        }
        catch
        {
        }
        if (version != _detailPaneAnimationVersion) return;
        LibraryDetailPane.RenderTransform = new TranslateTransform(0, 0);
        LibraryToolbarActions.RenderTransform = null;
        LibraryDetailPane.Opacity = 1;
    }

    private void ClearBookDetailsSelection()
    {
        CancelPendingBookDetails();
        if (_selectedCard is not null) _selectedCard.PropertyChanged -= SelectedCard_PropertyChanged;
        _selectedCard = null;
        _detailPaneBookId = null;
        SyncCardSelectionVisuals();
    }

    private void ClearSelectedBook()
    {
        ClearBookDetailsSelection();
        var token = BeginDetailPaneAnimation();
        var version = ++_detailPaneAnimationVersion;
        if (!LibraryDetailPane.IsVisible)
        {
            CompleteClearSelectedBook();
            return;
        }
        var width = LibraryDetailPane.Bounds.Width > 0 ? LibraryDetailPane.Bounds.Width : LibraryDetailWidth;
        if (width <= 0)
        {
            CompleteClearSelectedBook();
            return;
        }
        _ = AnimateLibraryDetailPaneOutCoreAsync(version, token, width);
    }

    // Exit mirrors the entrance so speed and acceleration remain continuous.
    private async Task AnimateLibraryDetailPaneOutCoreAsync(
        int version,
        CancellationToken token,
        double width)
    {
        var currentX = (LibraryDetailPane.RenderTransform as TranslateTransform)?.X ?? 0;
        var translate = new TranslateTransform(currentX, 0);
        LibraryDetailPane.RenderTransform = translate;
        LibraryDetailPane.Opacity = 1;
        try
        {
            await SlideLibraryDetailPaneAsync(translate, currentX, width, opening: false, token);
        }
        catch
        {
        }
        if (version != _detailPaneAnimationVersion) return;
        CompleteClearSelectedBook();
    }

    private async Task SlideLibraryDetailPaneAsync(
        TranslateTransform translate, double from, double to, bool opening, CancellationToken token)
    {
        // Keep shelf layout fixed throughout the slide; only render transforms
        // move, avoiding repeated book-grid measurement and cover work.
        var toolbarTranslate = LibraryToolbarActions.RenderTransform as TranslateTransform ?? new TranslateTransform();
        LibraryToolbarActions.RenderTransform = toolbarTranslate;
        var toolbarScale = (LibraryDetailPane.Width + LibraryDetailPane.Margin.Left
            + LibraryDetailPane.Margin.Right) / LibraryDetailPane.Width;
        var duration = opening ? 340d : 240d;
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed.TotalMilliseconds < duration)
        {
            token.ThrowIfCancellationRequested();
            var progress = Math.Clamp(started.Elapsed.TotalMilliseconds / duration, 0, 1);
            var eased = opening ? 1 - Math.Pow(1 - progress, 3) : progress * progress;
            translate.X = from + (to - from) * eased;
            toolbarTranslate.X = translate.X * toolbarScale;
            await Task.Delay(16, token);
        }
        token.ThrowIfCancellationRequested();
        translate.X = to;
        toolbarTranslate.X = to * toolbarScale;
    }

    private void UpdateLibraryDetailColumnWidth(double offset)
    {
        var width = LibraryDetailPane.Width;
        var reservedWidth = width + LibraryDetailPane.Margin.Left + LibraryDetailPane.Margin.Right;
        LibraryWorkspace.ColumnDefinitions[1].Width = new GridLength(
            reservedWidth * (1 - Math.Clamp(offset / width, 0, 1)));
    }

    // Cancels any in-flight detail-pane animation and hands out a fresh token
    // for the next one, so a newer show/hide command never fights an older
    // animation that is still running.
    private CancellationToken BeginDetailPaneAnimation()
    {
        _detailPaneAnimationCancellation?.Cancel();
        _detailPaneAnimationCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        _detailPaneAnimationCancellation = cts;
        return cts.Token;
    }

    private void CancelDetailPaneAnimation()
    {
        _detailPaneAnimationCancellation?.Cancel();
        _detailPaneAnimationCancellation?.Dispose();
        _detailPaneAnimationCancellation = null;
    }

    private static async Task RunLibraryDetailPaneDoubleAnimationAsync(
        Avalonia.Animation.Animatable target,
        AvaloniaProperty property,
        double from,
        double to,
        int durationMs,
        Easing easing,
        CancellationToken token = default)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing,
            FillMode = FillMode.Forward
        };
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Avalonia.Styling.Setter(property, from) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Avalonia.Styling.Setter(property, to) }
        });
        await animation.RunAsync(target, token);
    }

    // Finishes a deselection: hiding the pane releases its auto-sized column.
    // Kept separate from ClearSelectedBook so an animated exit
    // can run with the old content visible before this resets everything.
    private void CompleteClearSelectedBook()
    {
        CancelDetailPaneAnimation();
        LibraryDetailPane.IsVisible = false;
        UpdateSeriesShelfOverlayPlacement();
        LibraryWorkspace.ColumnDefinitions[1].Width = new GridLength(0);
        LibraryToolbarActions.RenderTransform = null;
        LibraryDetailPane.RenderTransform = new TranslateTransform(0, 0);
        LibraryDetailPane.Opacity = 1;
        DetailCoverImage.Source = null;
        ClearPendingDetailCover();
        DetailCoverPlaceholder.IsVisible = true;
        DetailTitleText.Text = T("请选择一本书");
        DetailAuthorsText.Text = string.Empty;
        DetailFormatText.Text = string.Empty;
        DetailOnlineRatingBox.Text = string.Empty;
        DetailStateText.Text = string.Empty;
        DetailOrganizationText.Text = string.Empty;
        DetailPublicationText.Text = string.Empty;
        DetailIdentifierText.Text = string.Empty;
        DetailTagsBox.Text = string.Empty;
        DetailCategoryBox.Text = string.Empty;
        UpdateDetailCollectionsSelector(null);
        DetailDescriptionBox.Text = string.Empty;
        DetailSeriesBox.Text = string.Empty;
        DetailPublisherBox.Text = string.Empty;
        DetailPublishDateBox.Text = string.Empty;
        DetailIsbnBox.Text = string.Empty;
        DetailPageCountBox.Text = string.Empty;
        DetailBindingBox.Text = string.Empty;
        UpdateDetailActionIcons(false, LibraryReadingStatus.Unread);
        DetailDescriptionText.Text = T("暂无简介");
        DetailFiles.ItemsSource = Array.Empty<BookFile>();
        DetailCollectionBox.ItemsSource = CollectionFolders;
        DetailCollectionBox.SelectedItem = null;
        CollectionMembershipButton.Content = T("加入收藏夹");
    }

    private void UpdateDetailActionIcons(bool isFavorite, LibraryReadingStatus readingStatus)
    {
        // Keep one minimal star silhouette; fill conveys the selected state
        // without changing the icon's optical footprint.
        DetailFavoriteIcon.Fill = isFavorite ? AppAppearanceResources.GetBrush("InkBrush") : Brushes.Transparent;
        var favoriteLabel = isFavorite ? T("已收藏；点击取消收藏") : T("未收藏；点击加入收藏");
        ToolTip.SetTip(DetailFavoriteButton, favoriteLabel);
        AutomationProperties.SetName(DetailFavoriteButton, favoriteLabel);

        // Closed book, open book and check share the same centred 24-unit box.
        var (data, label) = readingStatus switch
        {
            LibraryReadingStatus.Reading => (
                "M 3,6 L 12,9 L 21,6 V 19 L 12,22 L 3,19 Z M 12,9 V 22",
                T("阅读中；点击标记为已读")),
            LibraryReadingStatus.Finished => (
                "M 4,12 L 9,17 L 20,6",
                T("已读；点击重置为待读")),
            _ => (
                "M 12,4 A 8,8 0 1 0 12,20 A 8,8 0 1 0 12,4",
                T("待读；点击标记为阅读中"))
        };
        DetailReadingStatusIcon.Data = Geometry.Parse(data);
        ToolTip.SetTip(DetailReadingStatusButton, label);
        AutomationProperties.SetName(DetailReadingStatusButton, label);
    }

    private async Task RefreshLibraryAsync()
    {
        await RefreshLibraryDataAsync(_lifetimeCancellation.Token);
        SetupFilterControls();
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
        SetTaskStatus(ViewModel.StatusText);
    }

    private async Task RefreshLibraryDataAsync(CancellationToken cancellationToken)
    {
        await ViewModel.RefreshAsync(cancellationToken);
        await RefreshLibraryMatchRecordsAsync(cancellationToken);
        await RefreshBookSyncStatusesAsync(cancellationToken);
    }

    private async Task RefreshMissingBookSyncStatusesAsync()
    {
        if (Interlocked.CompareExchange(ref _bookSyncStatusRefreshBusy, 1, 0) != 0) return;
        var retry = false;
        try
        {
            var attempted = ViewModel.Books
                .Select(card => card.Book.Id)
                .ToHashSet();
            // RefreshBookSyncStatusesAsync computes the whole visible page.
            // If it failed, the next ViewChanged retries for that page.
            await RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
            if (!Dispatcher.UIThread.CheckAccess())
                await Dispatcher.UIThread.InvokeAsync(UpdateLibraryUi);
            else
                UpdateLibraryUi();
            // The user may have changed pages while the refresh was running;
            // those cards were not part of this pass and need their own.
            retry = ViewModel.Books.Any(card =>
                !attempted.Contains(card.Book.Id)
                && !_bookSyncStatusCache.ContainsKey(card.Book.Id));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Visible book sync status refresh failed: {exception}");
        }
        finally
        {
            Interlocked.Exchange(ref _bookSyncStatusRefreshBusy, 0);
            if (retry) _ = RefreshMissingBookSyncStatusesAsync();
        }
    }

    private async Task RefreshBookSyncStatusesAsync(CancellationToken cancellationToken)
    {
        var cards = ViewModel.Books.ToArray();
        if (cards.Length == 0) return;

        var refreshVersion = Interlocked.Increment(ref _bookSyncStatusRefreshVersion);
        try
        {
            var statuses = await Task.Run(
                () => _s3SyncService.GetBookSyncStatusesAsync(
                    _s3SyncStoredSettings.DeviceId,
                    _s3SyncStoredSettings.Settings,
                    cards.Select(card => card.Book).ToArray(),
                    cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (refreshVersion != Volatile.Read(ref _bookSyncStatusRefreshVersion)) return;

            foreach (var (bookId, status) in statuses)
                _bookSyncStatusCache[bookId] = status;

            var visibleCards = ViewModel.Books.ToHashSet();
            foreach (var card in cards)
            {
                if (visibleCards.Contains(card))
                    card.SetSyncStatus(statuses.GetValueOrDefault(card.Book.Id, BookSyncStatus.NotSynced));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Book sync status refresh failed: {exception}");
            if (refreshVersion != Volatile.Read(ref _bookSyncStatusRefreshVersion)) return;

            // Keep the last known state for existing books. A newly imported
            // card has no cache entry and therefore keeps its own default
            // NotSynced state until a later refresh succeeds.
            var visibleCards = ViewModel.Books.ToHashSet();
            foreach (var card in cards)
            {
                if (visibleCards.Contains(card)
                    && _bookSyncStatusCache.TryGetValue(card.Book.Id, out var previousStatus))
                    card.SetSyncStatus(previousStatus);
            }
        }
    }

    private async Task RefreshLibraryMatchRecordsAsync(CancellationToken cancellationToken)
    {
        _libraryMatchRecords = await _library.GetLibraryMatchRecordsAsync(cancellationToken);
        _libraryPresenceComparison = null;
    }

    private async Task RefreshCollectionsAsync()
    {
        var collections = await _library.GetCollectionSummariesAsync(_lifetimeCancellation.Token);

        var existing = CollectionFolders.ToDictionary(folder => folder.Collection.Id);
        var ids = collections.Select(summary => summary.Collection.Id).ToHashSet();
        foreach (var folder in CollectionFolders.Where(folder => !ids.Contains(folder.Collection.Id)).ToArray())
        {
            CollectionFolders.Remove(folder);
            folder.Dispose();
        }
        for (var index = 0; index < collections.Count; index++)
        {
            var summary = collections[index];
            if (existing.TryGetValue(summary.Collection.Id, out var folder))
            {
                folder.Update(summary.Collection, summary.BookCount, _paths.Data, summary.CoverPaths);
                var currentIndex = CollectionFolders.IndexOf(folder);
                if (currentIndex != index) CollectionFolders.Move(currentIndex, index);
            }
            else CollectionFolders.Insert(index, new BookCollectionFolderViewModel(
                summary.Collection, summary.BookCount, _paths.Data, summary.CoverPaths));
        }
        ApplyCollectionSearchFilter();

    }
}
