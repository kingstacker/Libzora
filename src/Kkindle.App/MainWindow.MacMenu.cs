using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private readonly List<NativeMenuItemBase> _macApplicationItems = [];
    private readonly Dictionary<NativeMenuItem, string> _macSystemMenuHeaders = [];

    /// <summary>
    /// Installs the standard macOS menu bar. Without an application menu,
    /// Avalonia falls back to its own menu containing "About Avalonia", which
    /// is both unlocalized and unrelated to Kkindle.
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
        // Kkindle does not expose system Services from its application menu.
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

        _macApplicationItems.Add(MacMenuItem(T("关于 Kkindle"), ShowAboutFromMacMenu));
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
                "Hide Kkindle" => T("隐藏 Kkindle"),
                "Hide Others" => T("隐藏其他"),
                "Show All" => T("显示全部"),
                "Quit" => T("退出 Kkindle"),
                _ => original
            };
            if (original == "Hide Others")
                item.Gesture = new KeyGesture(Key.H, KeyModifiers.Alt | KeyModifiers.Meta);
        }

        var menu = new NativeMenu();
        menu.Items.Add(BuildMacFileMenu());
        menu.Items.Add(BuildMacWindowMenu());
        menu.Items.Add(BuildMacHelpMenu());
        // Application items are a flat menu; Avalonia supplies the Kkindle
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
        items.Items.Add(new NativeMenuItemSeparator());
        // Close goes through the normal window closing path, which hides the
        // window instead of quitting when the app keeps running in the menu bar.
        items.Items.Add(MacMenuItem(
            T("关闭窗口"),
            Close,
            new KeyGesture(Key.W, KeyModifiers.Meta)));
        return new NativeMenuItem(T("文件")) { Menu = items };
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
            new KeyGesture(Key.F1)));
        items.Items.Add(MacMenuItem(
            T("用户手册"),
            () => OpenUserManualButton_Click(this, new RoutedEventArgs())));
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
}
