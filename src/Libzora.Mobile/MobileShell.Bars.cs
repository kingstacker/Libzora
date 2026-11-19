using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using Libzora.Mobile.Views.Sheets;
using Libzora.Mobile.Controls;
using Libzora.Mobile.Localization;

namespace Libzora.Mobile;

/// <summary>
/// Root navigation, system bars and lifecycle.
/// </summary>
public partial class MobileShell
{
    public async void ShowToast(string message)
    {
        if (NativeToast is { } native)
        {
            native(UiText.Localize(message));
            return;
        }
        var version = ++_toastVersion;
        ToastText.Text = message;
        Toast.IsVisible = true;
        await Task.Delay(ToastDuration);
        if (version == _toastVersion) Toast.IsVisible = false;
    }

    /// <summary>
    /// Reader paper can differ from the app theme, so the reader tints the system
    /// bars itself. Hiding them instead would reflow the page on every chrome
    /// toggle and raise Android's full-screen confirmation.
    /// </summary>
    public void SetPaperSystemBars(Color paper, bool dark)
    {
        // A local override also repaints the safe area behind the status bar.
        Resources[PageBackgroundKey] = new SolidColorBrush(paper);
        ApplySystemBars(paper, dark);
    }

    public void ClearPaperSystemBars()
    {
        Resources.Remove(PageBackgroundKey);
        SyncSystemBars();
    }

    public bool GoBack()
    {
        if (_backupBlocked) return true;
        if (SheetScope.IsVisible)
        {
            HideSheet();
            return true;
        }
        if (_history.Count <= 1) return false;
        var (closed, _) = _history.Pop();
        Apply();
        (closed as IShellPage)?.OnClosed();
        return true;
    }

    private void ShowRoot(ShellTab tab, Control page)
    {
        if (_backupBlocked) return;
        HideSheet();
        _tab = tab;
        var closed = _history.Select(entry => entry.Page).ToArray();
        _history.Clear();
        _history.Push((page, tab));
        Apply();
        foreach (var previous in closed) (previous as IShellPage)?.OnClosed();
    }

    private void Push(Control page)
    {
        if (_backupBlocked) return;
        HideSheet();
        _history.Push((page, _tab));
        Apply();
    }

    private void Apply()
    {
        var (page, tab) = _history.Peek();
        page.Classes.Remove("page-enter");
        page.Classes.Add("page-enter");
        PageHost.Content = page;
        // Pushed pages (book detail, reader) own the full screen; the glass
        // capsule around the tab bar must disappear with it.
        var showTabs = _history.Count == 1;
        TabBar.IsVisible = showTabs;
        TabBarGlass.IsVisible = showTabs;
        _services.TabBar?.Update(showTabs && !SheetScope.IsVisible, (int)tab);
        LibraryTab.Classes.Set("active", tab == ShellTab.Library);
        MaterialsTab.Classes.Set("active", tab == ShellTab.Materials);
        ReadingDataTab.Classes.Set("active", tab == ShellTab.ReadingData);
        ProfileTab.Classes.Set("active", tab == ShellTab.Profile);
    }

    private void SyncSystemBars()
    {
        _services.TabBar?.SetDarkAppearance(ActualThemeVariant == ThemeVariant.Dark);
        if (this.TryFindResource(PageBackgroundKey, ActualThemeVariant, out var value)
            && value is ISolidColorBrush brush)
        {
            ApplySystemBars(brush.Color, ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    private void ApplySystemBars(Color background, bool dark)
    {
        if (TopLevel.GetTopLevel(this)?.InsetsManager is { } insets)
            insets.SystemBarColor = background;
        _services.SystemBars?.SetIconsForBackground(dark);
    }

    private void OnNativeTabSelected(int index)
    {
        if (_backupBlocked) return;
        switch ((ShellTab)index)
        {
            case ShellTab.Materials: ShowMaterials(); break;
            case ShellTab.ReadingData: ShowReadingData(); break;
            case ShellTab.Profile: ShowProfile(); break;
            default: ShowLibrary(); break;
        }
    }

    private void OnLibraryTabClick(object? sender, RoutedEventArgs e) => ShowLibrary();

    private void OnMaterialsTabClick(object? sender, RoutedEventArgs e) => ShowMaterials();

    private void OnReadingDataTabClick(object? sender, RoutedEventArgs e) => ShowReadingData();

    private void OnProfileTabClick(object? sender, RoutedEventArgs e) => ShowProfile();

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => HideSheet();

    private void OnCloseSheetClick(object? sender, RoutedEventArgs e) => HideSheet();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.BackRequested += OnBackRequested;
            if (topLevel.InputPane is { } inputPane) inputPane.StateChanged += OnInputPaneChanged;
        }
        SyncSystemBars();
        RequestAutoSync(AutoSyncGap.OnStart);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.BackRequested -= OnBackRequested;
            if (topLevel.InputPane is { } inputPane) inputPane.StateChanged -= OnInputPaneChanged;
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Keeps the sheet's text field and confirm button above the keyboard.</summary>
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        var open = e.NewState == InputPaneState.Open;
        var lift = open ? e.EndRect.Height : 0;
        SheetPanel.Margin = new Thickness(0, 0, 0, lift);
        // A full-height sheet would otherwise be pushed off the top of the screen.
        if (_sheetFillsHeight) SheetPanel.Height = Math.Max(200, SheetPanel.MaxHeight - lift);
        SheetPanel.Padding = new Thickness(0, 0, 0, open ? 0 : Padding.Bottom);
        if (!open && SheetScope.IsVisible
            && TopLevel.GetTopLevel(this)?.FocusManager is { } focusManager
            && focusManager.GetFocusedElement() is TextBox focused
            && focused.GetVisualAncestors().Contains(SheetBody))
            focusManager.Focus(null);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (GoBack()) e.Handled = true;
    }
}
