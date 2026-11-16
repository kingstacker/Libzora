using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

public partial class ReaderPage
{
    private void OnSelectionDictionaryClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection || string.IsNullOrWhiteSpace(selection.Text)) return;
        var term = selection.Text.Trim();
        ClearSelection();
        _shell.ShowSheet(UiText.Get("本地词典"), new DictionarySheet(_shell.Services, term), ThemeScope.RequestedThemeVariant);
    }
}
