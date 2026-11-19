using System.Globalization;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Settings debounce, flush and exit preparation.
/// </summary>
public partial class MainWindow
{
    private void CancelAppSettingsDebounce()
    {
        var pending = _appSettingsAutoSaveCancellation;
        _appSettingsAutoSaveCancellation = null;
        pending?.Cancel();
        // The debounce owns disposal, including when its save is already running.
    }

    private async Task DebounceAppSettingsAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(600, cancellation.Token);
            if (!cancellation.IsCancellationRequested) await SaveAppSettingsCoreAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_appSettingsAutoSaveCancellation, cancellation))
                _appSettingsAutoSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> FlushAppSettingsAsync()
    {
        do
        {
            CancelAppSettingsDebounce();
            // This also waits for any save already holding the gate. Cancelling
            // the debounce never cancels an in-flight disk write.
            if (!await SaveAppSettingsCoreAsync()) return false;
        } while (_appSettingsSavedVersion < _appSettingsEditVersion);
        return true;
    }

    private async Task<bool> PrepareSettingsForExitAsync()
    {
        if (!await FlushMcpSettingsAsync()) return false;
        if (!await FlushAppSettingsAsync()) return false;
        if (_s3SettingsSaveTask is { IsCompleted: false } saving && !await saving) return false;
        UpdateS3SettingsDraftState();
        if (!_s3SettingsDirty) return true;
        OpenSettingsExpander("Data", SettingsS3Expander);
        if (!await ConfirmAsync(T("同步设置尚未保存"),
                T("同步配置中有未保存的修改。返回设置可保存；继续退出将放弃这些修改。"), T("放弃修改并退出")))
            return false;
        PopulateS3SyncControls();
        return true;
    }

    private KindleEmailSettings ReadKindleEmailDraft() => KindleEmailSettings.Normalize(new KindleEmailSettings
    {
        KindleEmailAddress = KindleEmailRecipientBox.Text ?? string.Empty,
        SenderEmailAddress = KindleEmailSenderBox.Text ?? string.Empty,
        SmtpHost = KindleEmailSmtpHostBox.Text ?? string.Empty,
        SmtpPort = int.TryParse(KindleEmailSmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 587,
        SmtpUsername = KindleEmailUsernameBox.Text ?? string.Empty,
        SmtpPassword = KindleEmailPasswordBox.Text ?? string.Empty,
        EnableSsl = KindleEmailSslCheck.IsChecked != false
    });

    private static bool KindleEmailSettingsEqual(KindleEmailSettings left, KindleEmailSettings right) =>
        (left.KindleEmailAddress, left.SenderEmailAddress, left.SmtpHost, left.SmtpPort, left.SmtpUsername, left.SmtpPassword, left.EnableSsl)
        == (right.KindleEmailAddress, right.SenderEmailAddress, right.SmtpHost, right.SmtpPort, right.SmtpUsername, right.SmtpPassword, right.EnableSsl);

    private void PopulateKindleEmailControls()
    {
        _suppressKindleEmailProviderChange = true;
        try
        {
            KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
            KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
            KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
            KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
            KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
            KindleEmailPasswordBox.Text = _kindleEmailSettings.SmtpPassword;
            KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
            KindleEmailProviderBox.SelectedIndex = GetKindleEmailProviderIndex(_kindleEmailSettings);
        }
        finally
        {
            _suppressKindleEmailProviderChange = false;
        }

        UpdateKindleEmailProviderControls();
    }
}
