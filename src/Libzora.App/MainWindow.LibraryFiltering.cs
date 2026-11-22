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
/// Search, filters, view mode, pagination and collection navigation.
/// </summary>
public partial class MainWindow
{
    private void ShowAllBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        ShowLibraryPage();
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        ViewModel.RefreshView();
    }

    private async void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text ?? string.Empty;
        // The initial Text binding raises this event while the first page is
        // still loading. A debounced refresh here would cancel startup before
        // SetupFilterControls has populated the shelf controls.
        if (!_filterControlsReady) return;

        // Typing used to run a full COUNT + page query and rebuild every card
        // per keystroke. Coalesce bursts of input into one query; ViewChanged
        // already calls UpdateLibraryUi, so no explicit UI pass is needed here.
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _librarySearchDebounce, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            await Task.Delay(250, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellation.IsCancellationRequested) return;
        ViewModel.RefreshView();
    }

    private void FilterButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FilterPanel.IsVisible = !FilterPanel.IsVisible;
        UpdateLibraryContentSpacing();
        FilterButton.Classes.Set("active", FilterPanel.IsVisible || ViewModel.HasActiveFilters);
    }

    private void UpdateLibraryContentSpacing()
    {
        if (LibraryContentHost is null || FilterPanel is null) return;
        LibraryContentHost.Margin = new Thickness(0, FilterPanel.IsVisible ? 4 : 18, 0, 0);
    }

    private void LibraryViewToggleButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var next = _libraryViewMode switch
        {
            LibraryViewMode.Grid => LibraryViewMode.List,
            LibraryViewMode.List => LibraryViewMode.Collections,
            _ => LibraryViewMode.Grid
        };
        if (next == LibraryViewMode.Collections && ViewModel.CollectionFilterId is not null)
        {
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.RefreshView();
        }
        SetLibraryViewMode(next);
    }

    private void LibraryViewMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value }
            || !Enum.TryParse<LibraryViewMode>(value, out var mode)) return;
        if (mode == LibraryViewMode.Collections && ViewModel.CollectionFilterId is not null)
        {
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.RefreshView();
        }
        SetLibraryViewMode(mode);
    }

    private void LibraryPreviousPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.GoToPreviousPage();
        UpdateLibraryUi();
    }

    private void LibraryNextPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.GoToNextPage();
        UpdateLibraryUi();
    }

    private void BackToCollectionsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Collections);
    }

    private async void CreateCollectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CreateCollectionAsync();

    private bool HasBlockingOverlayVisible() =>
        OnboardingOverlay.IsVisible || MessageOverlay.IsVisible
        || ConfirmationOverlay.IsVisible || CollectionNameOverlay.IsVisible
        || DevicePromptOverlay.IsVisible || DeviceModelInputOverlay.IsVisible
        || ImportFormatSelectionOverlay.IsVisible || ImportConflictOverlay.IsVisible;

    private bool IsTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox
            || (focused as Visual)?.GetVisualAncestors().Any(parent => parent is TextBox or ComboBox) == true;
    }

    private static bool IsMacSettingsGesture(KeyEventArgs e) =>
        OperatingSystem.IsMacOS()
        && e.Key == Key.OemComma
        && e.KeyModifiers == KeyModifiers.Meta;

    private static bool IsMacShortcutsHelpGesture(KeyEventArgs e) =>
        OperatingSystem.IsMacOS()
        && e.Key == Key.OemQuestion
        && (e.KeyModifiers == KeyModifiers.Meta
            || e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift));

    private static bool IsLibraryDeleteGesture(KeyEventArgs e) =>
        (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        || (OperatingSystem.IsMacOS()
            && e.Key is Key.Back or Key.Delete
            && e.KeyModifiers == KeyModifiers.Meta);

    private void SwitchLibraryViewModeFromShortcut(LibraryViewMode mode)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (!LibraryWorkspace.IsVisible)
            ShowLibraryPage();
        if (mode == LibraryViewMode.Collections && ViewModel.CollectionFilterId is not null)
        {
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.RefreshView();
        }
        SetLibraryViewMode(mode);
    }

    private void ToggleLibraryDetailInspector()
    {
        if (!LibraryRoot.IsVisible || !LibraryWorkspace.IsVisible) return;
        if (LibraryDetailPane.IsVisible)
        {
            ClearSelectedBook();
            return;
        }
        if ((GetSingleSelectedLibraryBook() ?? _selectedCard) is { } card)
            SelectBook(card);
    }

    private async Task RefreshCurrentPageFromShortcutAsync()
    {
        if (!LibraryRoot.IsVisible) return;
        if (LibraryWorkspace.IsVisible)
            await RefreshLibraryAsync();
        else if (ReadingDashboardPage.IsVisible)
            await RefreshReadingDashboardAsync();
        else if (DevicePage.IsVisible)
            await RefreshDevicesAsync(scanBooks: true);
        else if (ReadingMaterialsPage.IsVisible)
            await RefreshReadingMaterialsAsync();
    }

    private async void MainWindow_GlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || HasBlockingOverlayVisible())
            return;
        if (TryExitReadingMaterialsExport(e)) return;
        if (ReaderRoot.IsVisible && IsReaderReturnGesture(e) && !IsReaderTextInputFocused())
        {
            e.Handled = true;
            await ReturnFromReaderAsync();
            return;
        }
        if (!ReaderRoot.IsVisible && IsReaderOpenGesture(e) && LibraryWorkspace.IsVisible)
        {
            e.Handled = true;
            if (GetSingleSelectedLibraryBook() is { } card) await OpenBookAsync(card);
            return;
        }
        if (TryHandleReaderPdfHistoryKey(e)) return;
        if (IsMacSettingsGesture(e))
        {
            e.Handled = true;
            await OpenApplicationSettingsAsync();
            return;
        }
        if (e.Key == Key.F1 || (IsMacShortcutsHelpGesture(e) && !IsTextInputFocused()))
        {
            e.Handled = true;
            await ShowKeyboardShortcutsAsync();
            return;
        }
        if (_readerDocument is not null || _readerIsPdf) return;
        if (e.Source is Control source && (source is TextBox or ComboBox or Button
            || source.GetVisualAncestors().Any(parent => parent is TextBox or ComboBox or Button))) return;

        // Command is the primary modifier on macOS; Control stays for the
        // Windows and Linux heads.
        var control = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        if (control && !shift && !alt && e.Key == Key.F)
        {
            e.Handled = true;
            ShowLibraryPage();
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        if (control && !alt && e.Key == Key.I)
        {
            e.Handled = true;
            if (shift)
                ImportFolderButton_Click(this, new RoutedEventArgs());
            else
                ImportFilesButton_Click(this, new RoutedEventArgs());
            return;
        }
        if (control && !shift && !alt && e.Key == Key.R
            && (LibraryWorkspace.IsVisible || ReadingDashboardPage.IsVisible || DevicePage.IsVisible || ReadingMaterialsPage.IsVisible))
        {
            e.Handled = true;
            await RefreshCurrentPageFromShortcutAsync();
            return;
        }
        if (control && !shift && !alt && e.Key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.D3 or Key.NumPad3)
        {
            e.Handled = true;
            SwitchLibraryViewModeFromShortcut(e.Key switch
            {
                Key.D2 or Key.NumPad2 => LibraryViewMode.List,
                Key.D3 or Key.NumPad3 => LibraryViewMode.Collections,
                _ => LibraryViewMode.Grid
            });
            return;
        }
        if (!LibraryWorkspace.IsVisible) return;
        if (control && shift && !alt && e.Key == Key.N)
        {
            e.Handled = true;
            await CreateCollectionAsync();
            return;
        }
        if (control && alt && !shift && e.Key == Key.F)
        {
            e.Handled = true;
            FilterButton_Click(this, new RoutedEventArgs());
            return;
        }
        if (control && alt && !shift && e.Key == Key.I)
        {
            e.Handled = true;
            ToggleLibraryDetailInspector();
            return;
        }
        if (control && shift && !alt && e.Key == Key.R)
        {
            e.Handled = true;
            if ((GetSingleSelectedLibraryBook() ?? _selectedCard) is { } card)
                await OpenBookDirectoryFromContextAsync(card);
            return;
        }
        if (control && !shift && !alt && e.Key == Key.A)
        {
            e.Handled = true;
            _selectedBookIds.Clear();
            foreach (var card in ViewModel.Books) _selectedBookIds.Add(card.Book.Id);
            SyncCardSelectionVisuals();
            UpdateMultiSelectionUi();
            return;
        }
        if (IsLibraryDeleteGesture(e) && _selectedBookIds.Count > 0)
        {
            e.Handled = true;
            DeleteSelectedBooksButton_Click(this, new RoutedEventArgs());
            return;
        }
        var openCard = _selectedCard
            ?? ViewModel.Books.FirstOrDefault(card => _selectedBookIds.Contains(card.Book.Id));
        if (e.Key == Key.Enter && openCard is not null)
        {
            e.Handled = true;
            await OpenBookAsync(openCard);
            return;
        }
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
        }
        else if (_selectedBookIds.Count > 0)
        {
            _selectedBookIds.Clear();
            SyncCardSelectionVisuals();
            UpdateMultiSelectionUi();
        }
        else if (FilterPanel.IsVisible)
        {
            FilterPanel.IsVisible = false;
            UpdateLibraryContentSpacing();
            FilterButton.Classes.Set("active", ViewModel.HasActiveFilters);
        }
    }

    private Task ShowKeyboardShortcutsAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ShowMessageAsync(
                T("键盘快捷键"),
                string.Join("\n", new[]
                {
                    T("⌘F  搜索书库 / 页内搜索"),
                    T("⇧⌘F  全书搜索（阅读器）"),
                    T("⌘G / ⇧⌘G  查找下一个 / 上一个"),
                    T("⌘I / ⇧⌘I  导入书籍 / 导入文件夹"),
                    T("⇧⌘N  新建收藏夹"),
                    T("⌘O / Enter  阅读所选书籍"),
                    T("⌥⌘I  显示或隐藏书籍详情"),
                    T("⌥⌘F  显示或隐藏筛选面板"),
                    T("⌘1 / ⌘2 / ⌘3  网格 / 列表 / 收藏夹视图"),
                    T("⌘R / ⇧⌘R  刷新 / 打开书籍所在目录"),
                    T("⌘A  全选当前页"),
                    T("⌘⌫ / Delete  删除选中的书"),
                    T("⌘D / ⌘B  添加或取消书签"),
                    T("⇧⌘T / ⌥⌘B  目录 / 书签栏"),
                    T("⇧⌘A  AI 与笔记侧栏"),
                    T("⌘+ / ⌘- / ⌘0  缩放或调整字号"),
                    T("⌃⌘F / ⌃⌘Z  全屏 / 禅模式"),
                    T("{0}  返回上一页面", ReaderReturnShortcut),
                    T("⌘,  打开设置"),
                    T("Esc  关闭浮层、清除搜索或选择"),
                    T("⌘/ 或 F1  显示本帮助")
                }));
        }

        const string modifier = "Ctrl";
        string Shortcut(string source) =>
            T(source).Replace("Ctrl", modifier, StringComparison.Ordinal);

        return ShowMessageAsync(
            T("键盘快捷键"),
            string.Join("\n", new[]
            {
                Shortcut("Ctrl+F  搜索书库"),
                Shortcut("Ctrl+Shift+F  全书搜索（阅读器）"),
                Shortcut("Ctrl+I  导入书籍"),
                Shortcut("Ctrl+Shift+I  导入文件夹"),
                Shortcut("Ctrl+A  全选当前页"),
                T("Enter  打开选中的书"),
                T("Delete  删除选中的书"),
                T("Esc  清除搜索或选择"),
                Shortcut("Ctrl+O  阅读所选书籍"),
                Shortcut("Ctrl+B  添加或取消书签"),
                Shortcut("Ctrl++ / Ctrl+- / Ctrl+0  缩放或调整字号"),
                T("{0}  返回上一页面", ReaderReturnShortcut),
                T("F11  切换禅模式"),
                T("F1  显示本帮助")
            }));
    }

    private void ImportButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var menu = new ContextMenu { Classes = { "shelfViewMenu" } };
        var importFiles = new MenuItem
        {
            Header = T("导入文件"),
            FontSize = 14,
            Height = double.NaN,
            MinHeight = 0,
            MaxHeight = double.PositiveInfinity
        };
        importFiles.Click += ImportFilesButton_Click;
        menu.Items.Add(importFiles);

        var importFolder = new MenuItem
        {
            Header = T("导入文件夹"),
            FontSize = 14,
            Height = double.NaN,
            MinHeight = 0,
            MaxHeight = double.PositiveInfinity
        };
        importFolder.Click += ImportFolderButton_Click;
        menu.Items.Add(importFolder);

        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Open(ImportButton);
    }

    private async void ImportFilesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入书籍文件"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(T("电子书"))
                {
                    Patterns = ["*.epub", "*.pdf", "*.mobi", "*.azw3", "*.txt"]
                }
            ]
        });
        try
        {
            var sources = files.Select(file => new ImportFileSource(
                file.Name,
                file.TryGetLocalPath(),
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await file.OpenReadAsync();
                }));
            using var staging = await ImportFileStaging.PrepareAsync(
                sources, Path.Combine(_paths.ReaderCache, "import-staging"), _lifetimeCancellation.Token);
            await ImportPathsAsync(staging.Paths);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SetTaskStatus(T("导入文件失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
    }

    private async void ImportFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("导入书籍文件夹"),
            AllowMultiple = false
        });
        await ImportPathsAsync(folders
            .Select(folder => folder.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!));
    }

    private void LibraryFilter_TemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        // Set local values: the Fluent template's popup placement otherwise
        // takes precedence over style setters and centers wide option lists.
        if (e.NameScope.Find<Popup>("PART_Popup") is not { } popup) return;
        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 0;
        // Keep both sides of the open outline flush, including long author names.
        if (sender is ComboBox filter)
            popup.Bind(Popup.WidthProperty, new Binding("Bounds.Width") { Source = filter });
    }

    private void FilterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_filterControlsReady || _updatingFilterControls) return;
        ViewModel.AuthorFilter = AuthorFilterBox.SelectedIndex < 0 ? null : AuthorFilterBox.SelectedItem as string;
        ViewModel.TagFilter = TagFilterBox.SelectedIndex < 0 ? null : TagFilterBox.SelectedItem as string;
        ViewModel.FormatFilter = FormatFilterBox.SelectedIndex < 0 ? null : FormatFilterBox.SelectedItem as string;
        ViewModel.CategoryFilter = CategoryFilterBox.SelectedIndex < 0 ? null : CategoryFilterBox.SelectedItem as string;
        ViewModel.ReadingStatusFilter = ReadingStatusFilterBox.SelectedIndex < 0
            ? null
            : (LibraryReadingStatus)ReadingStatusFilterBox.SelectedIndex;
        ViewModel.FavoriteFilter = FavoritesFilterBox.SelectedIndex switch
        {
            0 => true,
            1 => false,
            _ => null
        };
        ViewModel.SortMode = LibrarySortBox.SelectedIndex < 0
            ? LibrarySortMode.UpdatedDescending
            : (LibrarySortMode)LibrarySortBox.SelectedIndex;
        ViewModel.RefreshView();
        UpdateLibraryUi();
        if (ReferenceEquals(sender, LibrarySortBox))
            ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }

    private void ClearFiltersButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _updatingFilterControls = true;
        try
        {
            ViewModel.SearchText = string.Empty;
            ViewModel.AuthorFilter = null;
            ViewModel.TagFilter = null;
            ViewModel.FormatFilter = null;
            ViewModel.CategoryFilter = null;
            ViewModel.ReadingStatusFilter = null;
            ViewModel.FavoriteFilter = null;
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.SortMode = LibrarySortMode.UpdatedDescending;
            SearchBox.Text = string.Empty;
            AuthorFilterBox.SelectedIndex = -1;
            TagFilterBox.SelectedIndex = -1;
            FormatFilterBox.SelectedIndex = -1;
            CategoryFilterBox.SelectedIndex = -1;
            ReadingStatusFilterBox.SelectedIndex = -1;
            FavoritesFilterBox.SelectedIndex = -1;
            LibrarySortBox.SelectedIndex = 0;
            ViewModel.RefreshView();
        }
        finally
        {
            _updatingFilterControls = false;
        }
        ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }
}
