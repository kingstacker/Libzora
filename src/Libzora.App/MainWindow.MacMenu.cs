using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;

namespace Libzora;

public partial class MainWindow
{
    private readonly List<NativeMenuItemBase> _macApplicationItems = [];
    private readonly Dictionary<NativeMenuItem, string> _macSystemMenuHeaders = [];

    /// <summary>
    /// Installs the standard macOS menu bar. Without an application menu,
    /// Avalonia falls back to its own menu containing "About Avalonia", which
    /// is both unlocalized and unrelated to Libzora.
    /// </summary>
    internal void InitializeMacMenu()
    {
        if (!MacTrafficLights.IsSupported) return;
        BuildMacMenus();
        UiText.LanguageChanged += MacMenuLanguageChanged;
    }

    private void MacMenuLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) BuildMacMenus();
        else Dispatcher.UIThread.Post(BuildMacMenus);
    }

    private void BuildMacMenus()
    {
        if (Application.Current is not { } application) return;

        // The application exporter is already attached by the time the window
        // starts. Mutate its existing menu so native updates keep flowing.
        var applicationItems = NativeMenu.GetMenu(application) ?? new NativeMenu();
        foreach (var item in _macApplicationItems) applicationItems.Items.Remove(item);
        _macApplicationItems.Clear();
        var frameworkAbout = applicationItems.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => item.Header == "About Avalonia");
        if (frameworkAbout is not null) applicationItems.Items.Remove(frameworkAbout);

        // macOS populates Services with actions from unrelated installed apps.
        // Libzora does not expose system Services from its application menu.
        var servicesItem = applicationItems.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => item.Header is "Services" or "服务");
        if (servicesItem is not null)
        {
            var index = applicationItems.Items.IndexOf(servicesItem);
            applicationItems.Items.RemoveAt(index);
            if (index < applicationItems.Items.Count
                && applicationItems.Items[index] is NativeMenuItemSeparator)
                applicationItems.Items.RemoveAt(index);
            _macSystemMenuHeaders.Remove(servicesItem);
        }

        _macApplicationItems.Add(MacMenuItem(T("关于 Libzora"), ShowAboutFromMacMenu));
        _macApplicationItems.Add(new NativeMenuItemSeparator());
        _macApplicationItems.Add(MacMenuItem(T("设置…"), ShowSettingsFromMacMenu,
            new KeyGesture(Key.OemComma, KeyModifiers.Meta)));
        for (var index = 0; index < _macApplicationItems.Count; index++)
            applicationItems.Items.Insert(index, _macApplicationItems[index]);

        foreach (var item in applicationItems.Items.OfType<NativeMenuItem>())
        {
            if (_macApplicationItems.Contains(item)) continue;
            if (!_macSystemMenuHeaders.TryGetValue(item, out var original))
                _macSystemMenuHeaders[item] = original = item.Header ?? string.Empty;
            item.Header = original switch
            {
                "Hide Libzora" => T("隐藏 Libzora"),
                "Hide Others" => T("隐藏其他"),
                "Show All" => T("显示全部"),
                "Quit" => T("退出 Libzora"),
                _ => original
            };
            if (original == "Hide Others")
                item.Gesture = new KeyGesture(Key.H, KeyModifiers.Alt | KeyModifiers.Meta);
        }

        var menu = new NativeMenu();
        menu.Items.Add(BuildMacFileMenu());
        menu.Items.Add(BuildMacViewMenu());
        menu.Items.Add(BuildMacWindowMenu());
        menu.Items.Add(BuildMacHelpMenu());
        // Application items are a flat menu; Avalonia supplies the Libzora
        // heading and standard macOS commands. Window menus belong to the window.
        NativeMenu.SetMenu(application, applicationItems);
        NativeMenu.SetMenu(this, menu);
    }

    private NativeMenuItem BuildMacFileMenu()
    {
        var items = new NativeMenu();
        items.Items.Add(MacMenuItem(
            T("导入书籍…"),
            ImportBooksFromMacMenu,
            new KeyGesture(Key.I, KeyModifiers.Meta)));
        items.Items.Add(MacMenuItem(
            T("导入文件夹…"),
            ImportFolderFromMacMenu,
            new KeyGesture(Key.I, KeyModifiers.Shift | KeyModifiers.Meta)));
        var newCollection = MacMenuItem(
            T("新建收藏夹…"),
            CreateCollectionFromMacMenu,
            new KeyGesture(Key.N, KeyModifiers.Shift | KeyModifiers.Meta));
        items.Items.Add(newCollection);
        items.Items.Add(new NativeMenuItemSeparator());
        var openDirectory = MacMenuItem(
            T("打开书籍所在目录"),
            OpenSelectedBookDirectoryFromMacMenu,
            new KeyGesture(Key.R, KeyModifiers.Shift | KeyModifiers.Meta));
        var deleteBooks = MacMenuItem(
            T("删除所选书籍…"),
            DeleteSelectedBooksFromMacMenu,
            new KeyGesture(Key.Back, KeyModifiers.Meta));
        items.Items.Add(openDirectory);
        items.Items.Add(deleteBooks);
        items.Items.Add(new NativeMenuItemSeparator());
        // Close goes through the normal window closing path, which hides the
        // window instead of quitting when the app keeps running in the menu bar.
        items.Items.Add(MacMenuItem(
            T("关闭窗口"),
            Close,
            new KeyGesture(Key.W, KeyModifiers.Meta)));
        items.NeedsUpdate += (_, _) =>
        {
            var inLibrary = LibraryRoot.IsVisible && LibraryWorkspace.IsVisible && !HasBlockingOverlayVisible();
            newCollection.IsEnabled = inLibrary;
            openDirectory.IsEnabled = inLibrary && (GetSingleSelectedLibraryBook() ?? _selectedCard) is not null;
            deleteBooks.IsEnabled = inLibrary && _selectedBookIds.Count > 0 && !IsTextInputFocused();
        };
        return new NativeMenuItem(T("文件")) { Menu = items };
    }

    private NativeMenuItem BuildMacViewMenu()
    {
        var items = new NativeMenu();
        var search = MacMenuItem(
            T("搜索…"),
            SearchFromMacMenu,
            new KeyGesture(Key.F, KeyModifiers.Meta));
        var wholeBookSearch = new NativeMenuItem(T("全书搜索…"))
        {
            Gesture = new KeyGesture(Key.F, KeyModifiers.Shift | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        wholeBookSearch.Click += (_, _) => ToggleReaderWholeBookSearch();
        items.Items.Add(search);
        items.Items.Add(wholeBookSearch);
        items.Items.Add(new NativeMenuItemSeparator());

        var gridView = new NativeMenuItem(T("网格视图"))
        {
            Gesture = new KeyGesture(Key.D1, KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.Radio
        };
        gridView.Click += (_, _) => SwitchLibraryViewModeFromShortcut(LibraryViewMode.Grid);
        var listView = new NativeMenuItem(T("列表视图"))
        {
            Gesture = new KeyGesture(Key.D2, KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.Radio
        };
        listView.Click += (_, _) => SwitchLibraryViewModeFromShortcut(LibraryViewMode.List);
        var collectionsView = new NativeMenuItem(T("收藏夹视图"))
        {
            Gesture = new KeyGesture(Key.D3, KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.Radio
        };
        collectionsView.Click += (_, _) => SwitchLibraryViewModeFromShortcut(LibraryViewMode.Collections);
        var filterPanel = new NativeMenuItem(T("筛选面板"))
        {
            Gesture = new KeyGesture(Key.F, KeyModifiers.Alt | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        filterPanel.Click += (_, _) =>
        {
            if (LibraryRoot.IsVisible && LibraryWorkspace.IsVisible)
                FilterButton_Click(this, new RoutedEventArgs());
        };
        var bookDetails = new NativeMenuItem(T("书籍详情"))
        {
            Gesture = new KeyGesture(Key.I, KeyModifiers.Alt | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        bookDetails.Click += (_, _) => ToggleLibraryDetailInspector();
        var refresh = MacMenuItem(
            T("刷新"),
            () => { _ = RefreshCurrentPageFromShortcutAsync(); },
            new KeyGesture(Key.R, KeyModifiers.Meta));
        items.Items.Add(gridView);
        items.Items.Add(listView);
        items.Items.Add(collectionsView);
        items.Items.Add(filterPanel);
        items.Items.Add(bookDetails);
        items.Items.Add(refresh);
        items.Items.Add(new NativeMenuItemSeparator());

        var toc = new NativeMenuItem(T("目录"))
        {
            Gesture = new KeyGesture(Key.T, KeyModifiers.Shift | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        toc.Click += (_, _) =>
        {
            if (ReaderRoot.IsVisible)
                ReaderTocButton_Click(this, new RoutedEventArgs());
        };
        var bookmarks = new NativeMenuItem(T("书签栏"))
        {
            Gesture = new KeyGesture(Key.B, KeyModifiers.Alt | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        bookmarks.Click += (_, _) =>
        {
            if (ReaderRoot.IsVisible)
                ToggleReaderBookmarkPanel();
        };
        var toggleBookmark = MacMenuItem(
            T("添加/取消书签"),
            () =>
            {
                if (ReaderRoot.IsVisible)
                    _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
            },
            new KeyGesture(Key.D, KeyModifiers.Meta));
        var assistant = new NativeMenuItem(T("AI 与笔记侧栏"))
        {
            Gesture = new KeyGesture(Key.A, KeyModifiers.Shift | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        assistant.Click += (_, _) =>
        {
            if (ReaderRoot.IsVisible && !_readerZenMode)
                ReaderAssistantToggleButton_Click(this, new RoutedEventArgs());
        };
        items.Items.Add(toc);
        items.Items.Add(bookmarks);
        items.Items.Add(toggleBookmark);
        items.Items.Add(assistant);
        items.Items.Add(new NativeMenuItemSeparator());

        var zoomIn = MacMenuItem(
            T("增大字号 / 放大"),
            () =>
            {
                if (ReaderRoot.IsVisible)
                    _ = ObserveReaderTaskAsync(ChangeReaderFontAsync(0.1));
            },
            new KeyGesture(Key.OemPlus, KeyModifiers.Meta));
        var zoomOut = MacMenuItem(
            T("减小字号 / 缩小"),
            () =>
            {
                if (ReaderRoot.IsVisible)
                    _ = ObserveReaderTaskAsync(ChangeReaderFontAsync(-0.1));
            },
            new KeyGesture(Key.OemMinus, KeyModifiers.Meta));
        var zoomReset = MacMenuItem(
            T("实际大小 / 默认字号"),
            () =>
            {
                if (ReaderRoot.IsVisible)
                    _ = ObserveReaderTaskAsync(ResetReaderZoomOrFontAsync());
            },
            new KeyGesture(Key.D0, KeyModifiers.Meta));
        items.Items.Add(zoomIn);
        items.Items.Add(zoomOut);
        items.Items.Add(zoomReset);
        items.Items.Add(new NativeMenuItemSeparator());

        var fullscreen = MacMenuItem(
            T("全屏"),
            () => WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen,
            new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta));
        var zen = new NativeMenuItem(T("禅模式"))
        {
            Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Meta),
            ToggleType = MenuItemToggleType.CheckBox
        };
        zen.Click += (_, _) => ToggleReaderZenMode();
        items.Items.Add(fullscreen);
        items.Items.Add(zen);
        items.NeedsUpdate += (_, _) =>
        {
            var inReader = ReaderRoot.IsVisible;
            var inLibraryShell = LibraryRoot.IsVisible && !HasBlockingOverlayVisible();
            var inLibraryWorkspace = inLibraryShell && LibraryWorkspace.IsVisible;
            search.IsEnabled = inReader || inLibraryShell;
            wholeBookSearch.IsEnabled = inReader;
            wholeBookSearch.IsChecked = inReader && _readerSearchVisible;
            gridView.IsEnabled = inLibraryShell && !_isTransferring;
            gridView.IsChecked = _libraryViewMode == LibraryViewMode.Grid;
            listView.IsEnabled = inLibraryShell && !_isTransferring;
            listView.IsChecked = _libraryViewMode == LibraryViewMode.List;
            collectionsView.IsEnabled = inLibraryShell && !_isTransferring;
            collectionsView.IsChecked = _libraryViewMode == LibraryViewMode.Collections;
            filterPanel.IsEnabled = inLibraryWorkspace;
            filterPanel.IsChecked = inLibraryWorkspace && FilterPanel.IsVisible;
            bookDetails.IsEnabled = inLibraryWorkspace
                && (LibraryDetailPane.IsVisible || (GetSingleSelectedLibraryBook() ?? _selectedCard) is not null);
            bookDetails.IsChecked = inLibraryWorkspace && LibraryDetailPane.IsVisible;
            refresh.IsEnabled = inLibraryShell
                && (LibraryWorkspace.IsVisible || ReadingDashboardPage.IsVisible || DevicePage.IsVisible || ReadingMaterialsPage.IsVisible);
            toc.IsEnabled = inReader;
            toc.IsChecked = inReader && _readerTocExpanded && ReaderTocView.IsVisible && !_readerSearchVisible;
            bookmarks.IsEnabled = inReader;
            bookmarks.IsChecked = inReader && _readerTocExpanded && ReaderBookmarkPane.IsVisible && !_readerSearchVisible;
            toggleBookmark.IsEnabled = inReader;
            assistant.IsEnabled = inReader && !_readerZenMode;
            assistant.IsChecked = inReader && ReaderAssistantPanel.IsVisible;
            zoomIn.IsEnabled = inReader;
            zoomOut.IsEnabled = inReader;
            zoomReset.IsEnabled = inReader;
            fullscreen.Header = T(WindowState == WindowState.FullScreen ? "退出全屏" : "全屏");
            zen.IsEnabled = inReader;
            zen.IsChecked = _readerZenMode;
        };
        // Reader navigation appends the context-sensitive "back" item here.
        return new NativeMenuItem(T("视图")) { Menu = items };
    }

    private NativeMenuItem BuildMacWindowMenu()
    {
        var items = new NativeMenu();
        items.Items.Add(MacMenuItem(
            T("最小化"),
            () => WindowState = WindowState.Minimized,
            new KeyGesture(Key.M, KeyModifiers.Meta)));
        items.Items.Add(MacMenuItem(
            T("切换全屏"),
            () => WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen,
            new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta)));
        return new NativeMenuItem(T("窗口")) { Menu = items };
    }

    private NativeMenuItem BuildMacHelpMenu()
    {
        var items = new NativeMenu();
        items.Items.Add(MacMenuItem(
            T("键盘快捷键"),
            () => { _ = ShowKeyboardShortcutsAsync(); },
            new KeyGesture(Key.OemQuestion, KeyModifiers.Meta)));
        return new NativeMenuItem(T("帮助")) { Menu = items };
    }

    private static NativeMenuItem MacMenuItem(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowAboutFromMacMenu()
    {
        RestoreFromTray();
        SettingsButton_Click(this, new RoutedEventArgs());
        SettingsSearchBox.Text = string.Empty;
        ShowSettingsSection("About");
        SettingsScrollViewer.Offset = default;
    }

    private void ShowSettingsFromMacMenu()
    {
        RestoreFromTray();
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    private void ImportBooksFromMacMenu()
    {
        RestoreFromTray();
        ImportFilesButton_Click(this, new RoutedEventArgs());
    }

    private void ImportFolderFromMacMenu()
    {
        RestoreFromTray();
        ImportFolderButton_Click(this, new RoutedEventArgs());
    }

    private void CreateCollectionFromMacMenu()
    {
        RestoreFromTray();
        if (LibraryRoot.IsVisible && LibraryWorkspace.IsVisible && !HasBlockingOverlayVisible())
            _ = CreateCollectionAsync();
    }

    private void OpenSelectedBookDirectoryFromMacMenu()
    {
        RestoreFromTray();
        if (LibraryRoot.IsVisible
            && LibraryWorkspace.IsVisible
            && !HasBlockingOverlayVisible()
            && (GetSingleSelectedLibraryBook() ?? _selectedCard) is { } card)
        {
            _ = OpenBookDirectoryFromContextAsync(card);
        }
    }

    private void DeleteSelectedBooksFromMacMenu()
    {
        RestoreFromTray();
        if (LibraryRoot.IsVisible
            && LibraryWorkspace.IsVisible
            && !HasBlockingOverlayVisible()
            && _selectedBookIds.Count > 0)
        {
            DeleteSelectedBooksButton_Click(this, new RoutedEventArgs());
        }
    }

    private void SearchFromMacMenu()
    {
        RestoreFromTray();
        if (ReaderRoot.IsVisible)
        {
            OpenReaderSearchShortcut();
            return;
        }
        if (!LibraryRoot.IsVisible || HasBlockingOverlayVisible()) return;
        ShowLibraryPage();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
