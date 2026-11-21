using Avalonia;
using Avalonia.Controls;
using Libzora.Core;

namespace Libzora;

public partial class MainWindow
{
    private void ApplyMainAppearance()
    {
        if (Application.Current is App app)
        {
            app.ApplyTheme(_appSettings.MainTheme);
            app.ApplyUiFont(_appSettings.MainUiFont);
        }
    }

    private void MainThemeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave || !_appSettingsAutoSaveConfigured
            || MainThemeBox.SelectedItem is not ComboBoxItem { Tag: not null } item
            || !Enum.TryParse<AppTheme>(item.Tag.ToString(), out var theme)
            || !Enum.IsDefined(theme)) return;

        // Preview immediately while the normal settings debounce and shutdown
        // flush persist the selection, together with other pending preferences.
        if (Application.Current is App app) app.ApplyTheme(theme);
        ScheduleAppSettingsAutoSave();
        _appSettings = _appSettings with { MainTheme = theme };
        BroadcastPluginThemeChanged(theme.ToString());
    }

    private AppTheme ReadMainThemeFromControls() =>
        MainThemeBox.SelectedItem is ComboBoxItem item
        && Enum.TryParse<AppTheme>(item.Tag?.ToString(), out var theme)
        && Enum.IsDefined(theme) ? theme : _appSettings.MainTheme;

    private void MainUiFontBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave || !_appSettingsAutoSaveConfigured
            || MainUiFontBox.SelectedItem is not ComboBoxItem { Tag: not null } item
            || !Enum.TryParse<AppUiFont>(item.Tag.ToString(), out var font)
            || !Enum.IsDefined(font)) return;

        if (Application.Current is App app) app.ApplyUiFont(font);
        ScheduleAppSettingsAutoSave();
        _appSettings = _appSettings with { MainUiFont = font };
    }

    private AppUiFont ReadMainUiFontFromControls() =>
        MainUiFontBox.SelectedItem is ComboBoxItem item
        && Enum.TryParse<AppUiFont>(item.Tag?.ToString(), out var font)
        && Enum.IsDefined(font) ? font : _appSettings.MainUiFont;
}
