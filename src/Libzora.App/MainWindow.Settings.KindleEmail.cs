using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Kindle email provider controls.
/// </summary>
public partial class MainWindow
{
    private void InitializeKindleEmailClipboardSupport()
    {
        KindleEmailProviderBox.SelectionChanged += KindleEmailProviderBox_SelectionChanged;

        TextBox[] fields =
        [
            KindleEmailRecipientBox,
            KindleEmailSenderBox,
            KindleEmailSmtpHostBox,
            KindleEmailSmtpPortBox,
            KindleEmailUsernameBox,
            KindleEmailPasswordBox
        ];

        foreach (var field in fields)
        {
            // Keep paste working even if the platform theme does not route its
            // default TextBox hotkey/context flyout through this custom window.
            field.AddHandler(
                InputElement.KeyDownEvent,
                KindleEmailTextBox_KeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            field.ContextFlyout = CreateKindleEmailTextBoxContextFlyout(field);
        }

        UpdateKindleEmailProviderControls();
    }

    private static void KindleEmailTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsReadOnly) return;

        var modifiers = e.KeyModifiers;
        var pasteShortcut = e.Key == Key.V
            && (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shiftInsertShortcut = e.Key == Key.Insert
            && (modifiers & KeyModifiers.Shift) != 0;
        if (!pasteShortcut && !shiftInsertShortcut) return;

        e.Handled = true;
        textBox.Paste();
    }

    private MenuFlyout CreateKindleEmailTextBoxContextFlyout(TextBox textBox)
    {
        var cut = new MenuItem { Header = T("剪切") };
        var copy = new MenuItem { Header = T("复制") };
        var paste = new MenuItem { Header = T("粘贴") };
        var selectAll = new MenuItem { Header = T("全选") };

        cut.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.Cut();
        };
        copy.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.Copy();
        };
        paste.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.Paste();
        };
        selectAll.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        var menu = new MenuFlyout();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Opened += (_, _) =>
        {
            cut.IsEnabled = textBox.CanCut;
            copy.IsEnabled = textBox.CanCopy;
            // Do not use CanPaste here: the fallback exists specifically for
            // platforms where that theme command state is stale.
            paste.IsEnabled = !textBox.IsReadOnly;
            selectAll.IsEnabled = !string.IsNullOrEmpty(textBox.Text);
        };
        return menu;
    }

    private void KindleEmailProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressKindleEmailProviderChange
            || KindleEmailProviderBox.SelectedItem is not ComboBoxItem { Tag: string providerId })
            return;

        ApplyKindleEmailProviderPreset(providerId);
    }

    private void ApplyKindleEmailProviderPreset(string providerId)
    {
        var preset = KindleEmailProviderPresets.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
        KindleEmailSmtpAdvancedPanel.IsVisible = preset is null;
        if (preset is null) return;

        KindleEmailSmtpHostBox.Text = preset.SmtpHost;
        KindleEmailSmtpPortBox.Text = preset.SmtpPort.ToString(CultureInfo.InvariantCulture);
        KindleEmailSslCheck.IsChecked = preset.EnableSsl;
    }

    private void UpdateKindleEmailProviderControls()
    {
        if (KindleEmailProviderBox.SelectedItem is not ComboBoxItem { Tag: string providerId })
        {
            KindleEmailSmtpAdvancedPanel.IsVisible = true;
            return;
        }

        var preset = KindleEmailProviderPresets.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
        KindleEmailSmtpAdvancedPanel.IsVisible = preset is null;
        if (preset is not null)
            ApplyKindleEmailProviderPreset(providerId);
    }

    private static int GetKindleEmailProviderIndex(KindleEmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost)) return 0;

        var index = Array.FindIndex(KindleEmailProviderPresets, provider =>
            string.Equals(provider.SmtpHost, settings.SmtpHost, StringComparison.OrdinalIgnoreCase)
            && provider.SmtpPort == settings.SmtpPort
            && provider.EnableSsl == settings.EnableSsl);
        // The first preset build used 587 for 163. Treat that value as the
        // old built-in preset so existing users get the corrected preset
        // instead of being pushed into the custom SMTP form.
        if (index < 0
            && string.Equals(settings.SmtpHost, "smtp.163.com", StringComparison.OrdinalIgnoreCase)
            && settings.SmtpPort == 587
            && settings.EnableSsl)
            return Array.FindIndex(KindleEmailProviderPresets, provider => provider.Id == "163");
        return index >= 0 ? index : KindleEmailProviderPresets.Length;
    }
}
