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

    private async void MainWindow_GlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || OnboardingOverlay.IsVisible || MessageOverlay.IsVisible
            || ConfirmationOverlay.IsVisible || CollectionNameOverlay.IsVisible
            || DevicePromptOverlay.IsVisible || DeviceModelInputOverlay.IsVisible
            || ImportFormatSelectionOverlay.IsVisible || ImportConflictOverlay.IsVisible)
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
        if (e.Key == Key.F1)
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
        if (control && e.Key == Key.F)
        {
            e.Handled = true;
            ShowLibraryPage();
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        if (control && e.Key == Key.I)
        {
            e.Handled = true;
            ImportFilesButton_Click(this, new RoutedEventArgs());
            return;
        }
        if (!LibraryWorkspace.IsVisible) return;
        if (control && e.Key == Key.A)
        {
            e.Handled = true;
            _selectedBookIds.Clear();
            foreach (var card in ViewModel.Books) _selectedBookIds.Add(card.Book.Id);
            SyncCardSelectionVisuals();
            UpdateMultiSelectionUi();
            return;
        }
        if (e.Key == Key.Delete && _selectedBookIds.Count > 0)
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
        // macOS uses Command for the same bindings; the shortcut text should
        // match the key the user actually presses.
        var modifier = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        string Shortcut(string source) =>
            T(source).Replace("Ctrl", modifier, StringComparison.Ordinal);

        return ShowMessageAsync(
            T("键盘快捷键"),
            string.Join("\n", new[]
            {
                Shortcut("Ctrl+F  搜索书库"),
                Shortcut("Ctrl+I  导入书籍"),
                Shortcut("Ctrl+A  全选当前页"),
                T("Enter  打开选中的书"),
                T("Delete  删除选中的书"),
                T("Esc  清除搜索或选择"),
                Shortcut("Ctrl+O  阅读所选书籍"),
                T("{0}  返回上一页面", ReaderReturnShortcut),
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
