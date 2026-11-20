using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Series stack interactions shared by the computer and device shelves.
/// </summary>
public partial class MainWindow
{
    private const double SeriesTileWidth = 140d;
    private const double SeriesTileSpacing = 14d;
    private const double SeriesTilePitch = SeriesTileWidth + SeriesTileSpacing;

    private BookCardViewModel? _seriesShelfActiveStack;
    private readonly HashSet<BookCardViewModel> _seriesShelfHeldCards = [];
    private readonly List<Action> _seriesShelfCleanupActions = [];
    private int _seriesShelfTileCount;
    private int? _seriesShelfPendingCenterIndex;
    private int _seriesShelfTransitionVersion;
    private bool _seriesShelfDragScrollAttached;
    private bool _seriesShelfDragPressed;
    private bool _seriesShelfDragActive;
    private bool _seriesShelfSuppressTileClick;
    private Point _seriesShelfDragStartPoint;
    private double _seriesShelfDragStartOffsetX;

    private bool IsSeriesOverlayHoldingCard(BookCardViewModel card) =>
        SeriesShelfOverlay.IsVisible && _seriesShelfHeldCards.Contains(card);

    private void BookSeriesBadge_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCardViewModel card } || !card.IsSeriesStack)
            return;
        ShowLocalSeriesOverlay(card);
        e.Handled = true;
    }

    private MenuItem BuildLocalSeriesMenu(BookCardViewModel card) =>
        CreateMenuItem(
            T("展开系列：{0}", card.SeriesTitle ?? card.SeriesCountLabel),
            () =>
            {
                ShowLocalSeriesOverlay(card);
                return Task.CompletedTask;
            });

    private void ShowLocalSeriesOverlay(BookCardViewModel stack)
    {
        if (LibraryDetailPane.IsVisible)
            ClearSelectedBook();

        ResetSeriesShelfSession();
        _seriesShelfActiveStack = stack;
        _seriesShelfHeldCards.Add(stack);
        foreach (var member in stack.SeriesMembers)
            _seriesShelfHeldCards.Add(member);

        var orderedMembers = OrderLocalSeriesMembers(stack);
        var currentFront = stack;
        BookCardViewModel? selectedVolume = null;
        var selectionUpdaters = new List<(BookCardViewModel Volume, Action<bool, bool> UpdateState)>();

        void SyncTileStates()
        {
            foreach (var (candidate, updateState) in selectionUpdaters)
                updateState(ReferenceEquals(candidate, selectedVolume), ReferenceEquals(candidate, currentFront));
        }

        var tiles = orderedMembers.Select(member =>
        {
            var volume = member;
            volume.LoadCover();
            if (volume.CoverImage is null
                && volume.Book.Files.Any(file => string.Equals(file.Format, "pdf", StringComparison.OrdinalIgnoreCase)))
            {
                _ = RestoreVisiblePdfCoverAsync(volume);
            }

            return BuildSeriesVolumeTile(
                title: SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                authors: volume.Authors,
                formatLabel: volume.FormatLabel,
                getCover: () => volume.CoverImage,
                coverNotifier: volume,
                coverPropertyName: nameof(BookCardViewModel.CoverImage),
                readActionLabel: T("阅读"),
                currentLabel: T("阅读中"),
                isSelected: () => ReferenceEquals(selectedVolume, volume),
                isCurrent: () => ReferenceEquals(currentFront, volume),
                registerStateUpdater: updater => selectionUpdaters.Add((volume, updater)),
                onActivate: async () =>
                {
                    if (ReferenceEquals(selectedVolume, volume))
                    {
                        CloseSeriesShelfOverlay(immediate: true);
                        await OpenBookAsync(volume);
                        return;
                    }

                    selectedVolume = volume;
                    currentFront = volume;
                    SyncTileStates();
                    var activeStack = _seriesShelfActiveStack ?? stack;
                    PromoteLocalSeriesVolume(activeStack, volume);
                    if (ViewModel.ShelfBooks.Contains(volume))
                        _seriesShelfActiveStack = volume;
                },
                onOpenReader: async () =>
                {
                    CloseSeriesShelfOverlay(immediate: true);
                    await OpenBookAsync(volume);
                },
                onContextRequested: (target, _) =>
                {
                    var menu = BuildSingleBookContextMenu(volume);
                    menu.Open(target);
                });
        });

        var centerIndex = 0;
        for (var i = 0; i < orderedMembers.Count; i++)
        {
            if (ReferenceEquals(orderedMembers[i], currentFront))
            {
                centerIndex = i;
                break;
            }
        }

        ShowSeriesShelfOverlay(
            stack.SeriesTitle ?? stack.DisplayTitle,
            stack.SeriesCountLabel,
            tiles,
            centerIndex,
            orderedMembers.Count);
    }

    private void PromoteLocalSeriesVolume(BookCardViewModel stack, BookCardViewModel member)
    {
        ViewModel.PromoteSeriesVolume(stack, member);
        var promoted = ViewModel.ShelfBooks.FirstOrDefault(card => ReferenceEquals(card, member));
        if (promoted is null) return;

        _selectedBookIds.Clear();
        _selectedBookIds.Add(promoted.Book.Id);
        _selectedCard = promoted;
        _multiSelectAnchor = promoted;
        UpdateMultiSelectionUi();
    }

    private void DeviceSeriesBadge_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: KindleBookCardViewModel card } || !card.IsSeriesStack)
            return;
        ShowDeviceSeriesOverlay(card);
        e.Handled = true;
    }

    private MenuItem BuildDeviceSeriesMenu(KindleBookCardViewModel card) =>
        CreateMenuItem(
            T("展开系列：{0}", card.SeriesTitle ?? card.SeriesCountLabel),
            () =>
            {
                ShowDeviceSeriesOverlay(card);
                return Task.CompletedTask;
            });

    private void ShowDeviceSeriesOverlay(KindleBookCardViewModel stack)
    {
        ResetSeriesShelfSession();
        var orderedMembers = OrderDeviceSeriesMembers(stack);
        var centerIndex = 0;
        for (var i = 0; i < orderedMembers.Count; i++)
        {
            if (ReferenceEquals(orderedMembers[i], stack))
            {
                centerIndex = i;
                break;
            }
        }

        var tiles = orderedMembers.Select(member =>
        {
            var volume = member;
            var isCurrent = ReferenceEquals(volume, stack);
            return BuildSeriesVolumeTile(
                title: SeriesVolumeLabel(null, volume.Title),
                authors: volume.Authors,
                formatLabel: volume.FormatLabel,
                getCover: () => volume.CoverImage,
                coverNotifier: volume,
                coverPropertyName: nameof(KindleBookCardViewModel.CoverImage),
                readActionLabel: null,
                currentLabel: T("阅读中"),
                isSelected: () => false,
                isCurrent: () => isCurrent,
                registerStateUpdater: null,
                onActivate: async () =>
                {
                    CloseSeriesShelfOverlay();
                    await PromoteDeviceSeriesVolume(stack: stack, member: volume);
                },
                onOpenReader: null,
                onContextRequested: (target, _) =>
                    ShowDeviceSingleBookContextMenu(target, volume));
        });

        ShowSeriesShelfOverlay(
            stack.SeriesTitle ?? stack.DisplayTitle,
            stack.SeriesCountLabel,
            tiles,
            centerIndex,
            orderedMembers.Count);
    }

    private void ShowSeriesShelfOverlay(
        string title,
        string countLabel,
        IEnumerable<Control> tiles,
        int centerIndex,
        int tileCount)
    {
        EnsureSeriesShelfDragScrollAttached();
        var version = ++_seriesShelfTransitionVersion;
        var wasVisible = SeriesShelfOverlay.IsVisible;
        SeriesShelfOverlay.IsHitTestVisible = true;
        SeriesShelfTitleText.Text = title;
        SeriesShelfCountText.Text = countLabel;
        SeriesShelfItemsHost.Children.Clear();
        foreach (var tile in tiles)
            SeriesShelfItemsHost.Children.Add(tile);
        _seriesShelfTileCount = Math.Max(0, tileCount);
        _seriesShelfPendingCenterIndex = Math.Clamp(centerIndex, 0, Math.Max(0, _seriesShelfTileCount - 1));
        SeriesShelfScrollViewer.InvalidateMeasure();
        if (!wasVisible)
        {
            SeriesShelfOverlay.Opacity = 0;
            SeriesShelfPanel.RenderTransform = TransformOperations.Parse("scale(0.96)");
        }
        SeriesShelfOverlay.IsVisible = true;
        UpdateSeriesShelfOverlayPlacement();
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _seriesShelfTransitionVersion || !SeriesShelfOverlay.IsVisible)
                return;
            SeriesShelfOverlay.Opacity = 1;
            SeriesShelfPanel.RenderTransform = TransformOperations.Parse("scale(1)");
        }, DispatcherPriority.Render);
        SeriesShelfOverlay.Focus();
    }

    private void CloseSeriesShelfOverlay(bool immediate = false)
    {
        if (!SeriesShelfOverlay.IsVisible) return;
        var version = ++_seriesShelfTransitionVersion;
        SeriesShelfOverlay.Opacity = 0;
        SeriesShelfPanel.RenderTransform = TransformOperations.Parse("scale(0.96)");
        if (immediate)
        {
            SeriesShelfOverlay.IsVisible = false;
            SeriesShelfOverlay.IsHitTestVisible = true;
            ResetSeriesShelfSession();
            return;
        }

        SeriesShelfOverlay.IsHitTestVisible = false;
        _ = FinishCloseSeriesShelfOverlayAsync(version);
    }

    private async Task FinishCloseSeriesShelfOverlayAsync(int version)
    {
        await Task.Delay(190);
        if (version != _seriesShelfTransitionVersion || !SeriesShelfOverlay.IsVisible)
            return;
        SeriesShelfOverlay.IsVisible = false;
        SeriesShelfOverlay.IsHitTestVisible = true;
        ResetSeriesShelfSession();
    }

    private void ResetSeriesShelfSession()
    {
        foreach (var cleanup in _seriesShelfCleanupActions)
            cleanup();
        _seriesShelfCleanupActions.Clear();
        _seriesShelfHeldCards.Clear();
        _seriesShelfActiveStack = null;
        _seriesShelfPendingCenterIndex = null;
        _seriesShelfTileCount = 0;
        ResetSeriesShelfDragState();
        SeriesShelfItemsHost.Children.Clear();
        SeriesShelfItemsHost.Margin = new Thickness(0);
    }

    private void UpdateSeriesShelfOverlayPlacement()
    {
        if (!SeriesShelfOverlay.IsVisible) return;

        SeriesShelfOverlay.Margin = new Thickness(0);
        Control shelfClip = LibraryWorkspace.IsVisible
            ? (BookGrid.IsVisible ? (Control)BookGrid : BookList.IsVisible ? BookList : LibraryContentHost)
            : (DeviceBookGridScroll.IsVisible ? DeviceBookGridScroll : DeviceBookListScroll);

        var bottomOffset = LibraryWorkspace.IsVisible ? 28d : DevicePage.Margin.Bottom;
        if (shelfClip.IsVisible
            && shelfClip.Bounds.Height > 0
            && LibraryRoot.Bounds.Height > 0
            && shelfClip.TranslatePoint(new Point(0, shelfClip.Bounds.Height), LibraryRoot) is { } shelfBottom)
        {
            bottomOffset = Math.Max(0d, LibraryRoot.Bounds.Height - shelfBottom.Y);
        }

        if (Math.Abs(SeriesShelfPanel.Margin.Bottom - bottomOffset) > 0.25
            || Math.Abs(SeriesShelfPanel.Margin.Left - 28d) > 0.25)
        {
            SeriesShelfPanel.Margin = new Thickness(28, 24, 28, bottomOffset);
        }

        var sidebarWidth = LibrarySidebar.Bounds.Width > 0 ? LibrarySidebar.Bounds.Width : 200d;
        var col1Width = LibraryRoot.Bounds.Width > 0
            ? Math.Max(400d, LibraryRoot.Bounds.Width - sidebarWidth)
            : 1094d;
        SeriesShelfScrollViewer.Width = double.NaN;
        SeriesShelfScrollViewer.MaxWidth = Math.Max(SeriesTileWidth, Math.Min(920d, col1Width - 96d));
        SeriesShelfItemsHost.Margin = new Thickness(0);
    }

    private void SeriesShelfScrollViewer_LayoutUpdated(object? sender, EventArgs e)
    {
        UpdateSeriesShelfOverlayPlacement();
        TryApplyPendingSeriesShelfCenterScroll();
    }

    private void TryApplyPendingSeriesShelfCenterScroll()
    {
        if (!SeriesShelfOverlay.IsVisible || _seriesShelfPendingCenterIndex is not { } pendingIndex)
            return;

        var viewportWidth = SeriesShelfScrollViewer.Viewport.Width;
        var extentWidth = SeriesShelfScrollViewer.Extent.Width;
        var expectedExtent = _seriesShelfTileCount * SeriesTileWidth
            + Math.Max(0, _seriesShelfTileCount - 1) * SeriesTileSpacing;
        if (viewportWidth <= 0 || extentWidth <= 0 || extentWidth + 1 < expectedExtent)
            return;

        var maxOffset = Math.Max(0d, extentWidth - viewportWidth);
        if (maxOffset <= 0)
        {
            if (Math.Abs(SeriesShelfScrollViewer.Offset.X) > 0.5)
                SeriesShelfScrollViewer.Offset = new Vector(0, 0);
            _seriesShelfPendingCenterIndex = null;
            return;
        }

        var targetCenter = pendingIndex * SeriesTilePitch + SeriesTileWidth / 2d;
        var targetX = Math.Clamp(targetCenter - viewportWidth / 2d, 0d, maxOffset);
        if (Math.Abs(SeriesShelfScrollViewer.Offset.X - targetX) > 0.5)
            SeriesShelfScrollViewer.Offset = new Vector(targetX, 0);
        _seriesShelfPendingCenterIndex = null;
    }

    private Control BuildSeriesVolumeTile(
        string title,
        string authors,
        string formatLabel,
        Func<Bitmap?> getCover,
        INotifyPropertyChanged? coverNotifier,
        string? coverPropertyName,
        string? readActionLabel,
        string? currentLabel,
        Func<bool> isSelected,
        Func<bool> isCurrent,
        Action<Action<bool, bool>>? registerStateUpdater,
        Func<Task> onActivate,
        Func<Task>? onOpenReader,
        Action<Control, ContextRequestedEventArgs>? onContextRequested)
    {
        var coverLayer = new Grid { Background = Brushes.Transparent };
        coverLayer.Children.Add(new TextBlock
        {
            Text = "BOOK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
            FontSize = 15,
            IsHitTestVisible = false
        });

        var coverImageControl = new Image
        {
            Stretch = Stretch.UniformToFill
        };
        coverLayer.Children.Add(coverImageControl);

        void SyncCover()
        {
            var bitmap = getCover();
            coverImageControl.Source = bitmap;
            coverImageControl.IsVisible = bitmap is not null;
        }
        SyncCover();

        Border? currentBadge = null;
        if (!string.IsNullOrWhiteSpace(currentLabel))
        {
            currentBadge = new Border
            {
                Classes = { "seriesCurrentBadge" },
                Background = AppAppearanceResources.GetBrush("InkBrush"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 2),
                Margin = new Thickness(0, 0, 6, 6),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                IsVisible = isCurrent(),
                Child = new TextBlock
                {
                    Text = currentLabel,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppAppearanceResources.GetBrush("PaperBrush")
                }
            };
            coverLayer.Children.Add(currentBadge);
        }

        Button? readButton = null;
        if (onOpenReader is not null && !string.IsNullOrWhiteSpace(readActionLabel))
        {
            readButton = new Button
            {
                Classes = { "coverReadAction", "bookCoverRead" },
                Content = readActionLabel,
                IsVisible = isSelected()
            };
            readButton.Click += async (_, e) =>
            {
                if (_seriesShelfSuppressTileClick || _seriesShelfDragActive)
                {
                    _seriesShelfSuppressTileClick = false;
                    e.Handled = true;
                    return;
                }
                e.Handled = true;
                await onOpenReader();
            };
            coverLayer.Children.Add(readButton);
        }

        var coverBorder = new Border
        {
            Classes = { "bookCover" },
            Width = 140,
            Height = 196,
            Background = AppAppearanceResources.GetBrush("PressedBrush"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = coverLayer
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            LineHeight = 17,
            FontWeight = FontWeight.SemiBold,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var authorsText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(authors) ? formatLabel : authors,
            FontSize = 11,
            LineHeight = 14,
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var metaText = new TextBlock
        {
            Text = formatLabel,
            FontSize = 10,
            LineHeight = 12,
            Foreground = AppAppearanceResources.GetBrush("SubtleInkBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var cardStack = new StackPanel { Spacing = 0 };
        cardStack.Children.Add(coverBorder);
        var captionGrid = new StackPanel
        {
            Margin = new Thickness(2, 8, 2, 0),
            Spacing = 2
        };
        captionGrid.Children.Add(titleText);
        captionGrid.Children.Add(authorsText);
        captionGrid.Children.Add(metaText);
        cardStack.Children.Add(captionGrid);

        var outlineBorder = new Border
        {
            Classes = { "bookOutline" },
            BorderBrush = AppAppearanceResources.GetBrush(isSelected() || isCurrent() ? "InkBrush" : "HairlineBrush"),
            BorderThickness = new Thickness(isCurrent() ? 2 : 1),
            IsHitTestVisible = false
        };

        var root = new Grid
        {
            Width = 140,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { cardStack, outlineBorder }
        };
        ToolTip.SetTip(root, title);

        var isHovered = false;
        void ApplyTileState(bool selected, bool current)
        {
            if (readButton is not null)
                readButton.IsVisible = selected;
            if (currentBadge is not null)
                currentBadge.IsVisible = current;
            outlineBorder.BorderThickness = new Thickness(current ? 2 : 1);
            outlineBorder.BorderBrush = AppAppearanceResources.GetBrush(selected || current || isHovered ? "InkBrush" : "HairlineBrush");
        }
        registerStateUpdater?.Invoke(ApplyTileState);

        if (coverNotifier is not null)
        {
            void OnCoverPropertyChanged(object? _, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == coverPropertyName)
                    SyncCover();
            }

            coverNotifier.PropertyChanged += OnCoverPropertyChanged;
            var detached = false;
            void CleanupSubscription()
            {
                if (detached) return;
                detached = true;
                coverNotifier.PropertyChanged -= OnCoverPropertyChanged;
            }
            _seriesShelfCleanupActions.Add(CleanupSubscription);
            root.DetachedFromVisualTree += (_, _) => CleanupSubscription();
        }

        root.AttachedToVisualTree += (_, _) => SyncCover();

        root.PointerEntered += (_, _) =>
        {
            isHovered = true;
            coverBorder.Classes.Set("hover", true);
            outlineBorder.BorderBrush = AppAppearanceResources.GetBrush("InkBrush");
        };
        root.PointerExited += (_, _) =>
        {
            isHovered = false;
            coverBorder.Classes.Set("hover", false);
            outlineBorder.BorderBrush = AppAppearanceResources.GetBrush(isSelected() || isCurrent() ? "InkBrush" : "HairlineBrush");
        };
        var leftPressed = false;
        var pressedClickCount = 0;
        root.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;
            leftPressed = true;
            pressedClickCount = e.ClickCount;
            e.Handled = true;
        };
        root.PointerReleased += async (_, e) =>
        {
            if (!leftPressed || e.InitialPressMouseButton != MouseButton.Left) return;
            leftPressed = false;
            if (_seriesShelfSuppressTileClick || _seriesShelfDragActive)
            {
                _seriesShelfSuppressTileClick = false;
                return;
            }
            e.Handled = true;
            if (pressedClickCount >= 2 && onOpenReader is not null)
                await onOpenReader();
            else
                await onActivate();
        };
        root.PointerCaptureLost += (_, _) => leftPressed = false;
        if (onContextRequested is not null)
            root.ContextRequested += (_, e) =>
            {
                onContextRequested(root, e);
                e.Handled = true;
            };
        return root;
    }

    private void SeriesShelfCloseButton_Click(object? sender, RoutedEventArgs e) =>
        CloseSeriesShelfOverlay();

    private void SeriesShelfOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source
            && (ReferenceEquals(source, SeriesShelfPanel)
                || source.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, SeriesShelfPanel))))
            return;
        CloseSeriesShelfOverlay();
        e.Handled = true;
    }

    private void SeriesShelfOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSeriesShelfOverlay();
        e.Handled = true;
    }

    private void EnsureSeriesShelfDragScrollAttached()
    {
        if (_seriesShelfDragScrollAttached) return;
        _seriesShelfDragScrollAttached = true;
        SeriesShelfScrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            SeriesShelfScrollViewer_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        SeriesShelfScrollViewer.AddHandler(
            InputElement.PointerMovedEvent,
            SeriesShelfScrollViewer_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        SeriesShelfScrollViewer.AddHandler(
            InputElement.PointerReleasedEvent,
            SeriesShelfScrollViewer_PointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        SeriesShelfScrollViewer.PointerCaptureLost += SeriesShelfScrollViewer_PointerCaptureLost;
    }

    private void ResetSeriesShelfDragState()
    {
        _seriesShelfDragPressed = false;
        _seriesShelfDragActive = false;
        _seriesShelfSuppressTileClick = false;
        SeriesShelfScrollViewer.Cursor = Cursor.Default;
    }

    private void SeriesShelfScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!SeriesShelfOverlay.IsVisible
            || !e.GetCurrentPoint(SeriesShelfScrollViewer).Properties.IsLeftButtonPressed)
            return;

        _seriesShelfDragPressed = true;
        _seriesShelfDragActive = false;
        _seriesShelfSuppressTileClick = false;
        _seriesShelfDragStartPoint = e.GetPosition(SeriesShelfScrollViewer);
        _seriesShelfDragStartOffsetX = SeriesShelfScrollViewer.Offset.X;
    }

    private void SeriesShelfScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seriesShelfDragPressed || !SeriesShelfOverlay.IsVisible) return;
        if (!e.GetCurrentPoint(SeriesShelfScrollViewer).Properties.IsLeftButtonPressed)
        {
            ResetSeriesShelfDragState();
            return;
        }

        var current = e.GetPosition(SeriesShelfScrollViewer);
        var dx = current.X - _seriesShelfDragStartPoint.X;
        var maxX = Math.Max(0d, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        if (!_seriesShelfDragActive)
        {
            if (Math.Abs(dx) < 6d || maxX <= 0d) return;
            _seriesShelfDragActive = true;
            _seriesShelfSuppressTileClick = true;
            _seriesShelfPendingCenterIndex = null;
            SeriesShelfScrollViewer.Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(SeriesShelfScrollViewer);
        }

        var targetX = Math.Clamp(_seriesShelfDragStartOffsetX - dx, 0d, maxX);
        SeriesShelfScrollViewer.Offset = new Vector(targetX, 0d);
        e.Handled = true;
    }

    private void SeriesShelfScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seriesShelfDragPressed) return;
        var wasDragging = _seriesShelfDragActive;
        _seriesShelfDragPressed = false;
        _seriesShelfDragActive = false;
        SeriesShelfScrollViewer.Cursor = Cursor.Default;
        if (ReferenceEquals(e.Pointer.Captured, SeriesShelfScrollViewer))
            e.Pointer.Capture(null);

        if (wasDragging)
            e.Handled = true;
        else
            _seriesShelfSuppressTileClick = false;
    }

    private void SeriesShelfScrollViewer_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _seriesShelfDragPressed = false;
        _seriesShelfDragActive = false;
        SeriesShelfScrollViewer.Cursor = Cursor.Default;
    }

    private void SeriesShelfScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _seriesShelfPendingCenterIndex = null;
        if (e.Handled || Math.Abs(e.Delta.Y) <= Math.Abs(e.Delta.X) || e.Delta.Y == 0)
            return;
        var maxX = Math.Max(0, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        if (maxX <= 0) return;
        var current = SeriesShelfScrollViewer.Offset;
        var targetX = Math.Clamp(current.X - e.Delta.Y * 48, 0, maxX);
        SeriesShelfScrollViewer.Offset = new Vector(targetX, current.Y);
        e.Handled = true;
    }

    private static IReadOnlyList<BookCardViewModel> OrderLocalSeriesMembers(BookCardViewModel stack)
    {
        var members = stack.SeriesMembers;
        if (members.Count < 2) return members;

        var stacks = BookSeriesGrouping.Stacks(members.Select(card => new BookSeriesCandidate(
            card.Book.Id.ToString("N"),
            card.Book.Title,
            card.Book.Authors,
            card.Book.Series,
            card.Book.SeriesIndex,
            card.Book.Files
                .Select(file => Path.GetFileNameWithoutExtension(file.RelativePath))
                .ToArray())));
        if (stacks.Count == 1)
        {
            var byId = members.ToDictionary(card => card.Book.Id.ToString("N"), StringComparer.Ordinal);
            return stacks[0].Members.Select(member => byId[member.Id]).ToArray();
        }

        return members
            .OrderBy(card => card.Book.SeriesIndex ?? double.PositiveInfinity)
            .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Book.Id)
            .ToArray();
    }

    private static IReadOnlyList<KindleBookCardViewModel> OrderDeviceSeriesMembers(KindleBookCardViewModel stack)
    {
        var members = stack.SeriesMembers;
        if (members.Count < 2) return members;

        var stacks = BookSeriesGrouping.Stacks(members.Select(card => new BookSeriesCandidate(
            card.Book.RelativePath,
            card.Book.Title,
            card.Book.Authors,
            FileTitles: [Path.GetFileNameWithoutExtension(card.Book.RelativePath)])));
        if (stacks.Count == 1)
        {
            var byPath = members.ToDictionary(card => card.Book.RelativePath, StringComparer.Ordinal);
            return stacks[0].Members.Select(member => byPath[member.Id]).ToArray();
        }

        return members
            .OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Book.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private Task PromoteDeviceSeriesVolume(
        KindleBookCardViewModel stack,
        KindleBookCardViewModel member)
    {
        if (string.IsNullOrWhiteSpace(stack.SeriesKey)) return Task.CompletedTask;
        _deviceSeriesFronts[stack.SeriesKey!] = member.Book.RelativePath;
        RebuildDeviceShelfBooks();
        foreach (var candidate in DeviceBooks)
            candidate.IsSelected = ReferenceEquals(candidate, member);
        _deviceMultiSelectAnchor = member;
        UpdateDeviceBookSelectionUi();
        return Task.CompletedTask;
    }

    private static string SeriesVolumeLabel(double? index, string title) =>
        index is { } position && double.IsFinite(position) && position > 0
            ? $"{position:0.#} · {title}"
            : title;
}
