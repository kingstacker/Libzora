using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Encrypted backup export and import.
/// </summary>
public partial class MainWindow
{
    private async void ExportBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backupBusy || _readingDataResetBusy) return;
        if (!await EnsureBackupEncryptionForUiAsync()) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出 Libzora 备份"),
            SuggestedFileName = T("Libzora-备份-{0}{1}", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), AppBackupService.FileExtension),
            FileTypeChoices = [new FilePickerFileType(T("Libzora 备份")) { Patterns = [$"*{AppBackupService.FileExtension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_backupBusy || _readingDataResetBusy) return;
        _backupBusy = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在导出备份…");
        try
        {
            var result = await _backupService.ExportAsync(path, _lifetimeCancellation.Token);
            SettingsBackupStatusText.Text = T("已导出 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            TaskProgressPopupText.Text = T("已导出 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
        }
        catch (Exception exception)
        {
            SettingsBackupStatusText.Text = T("备份导出失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("导出失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            _backupBusy = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateBackupFiles(string directory) =>
        AutoBackupRetentionPolicy.SelectAutomatic(
            Directory.GetFiles(directory, $"*{AppBackupService.FileExtension}")
                .Select(path => new FileInfo(path)));

    private async Task RunAutoBackupIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (!_appSettings.AutoBackupEnabled || _backupBusy || _readingDataResetBusy) return;
        try
        {
            Directory.CreateDirectory(_paths.Backups);
            var existing = EnumerateBackupFiles(_paths.Backups);
            if (existing.FirstOrDefault() is { } latest
                && AutoBackupRetentionPolicy.IsFresh(latest.LastWriteTimeUtc, DateTime.UtcNow))
                return;

            _backupBusy = true;
            var destination = Path.Combine(
                _paths.Backups,
                $"{AutoBackupRetentionPolicy.FileNamePrefix}{DateTime.Now:yyyyMMdd-HHmmss}{AppBackupService.FileExtension}");
            await _backupService.ExportAsync(destination, cancellationToken);
            existing = EnumerateBackupFiles(_paths.Backups);
            foreach (var oldBackup in AutoBackupRetentionPolicy.SelectExpired(
                existing,
                _appSettings.AutoBackupRetention))
            {
                // Retention is best effort: a locked file must not abort
                // cleanup of the remaining expired automatic backups.
                try { oldBackup.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("自动备份失败：{0}", UiText.Localize(exception.Message)), 4000);
        }
        finally
        {
            _backupBusy = false;
        }
    }

    private async void ImportBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backupBusy || _readingDataResetBusy) return;
        if (!await EnsureBackupEncryptionForUiAsync()) return;
        if (_s3SyncBusy)
        {
            await ShowMessageAsync(T("请稍候"), T("云端同步正在进行，请完成后再导入备份。"));
            return;
        }
        if (_readerDocument is not null || _readerIsPdf || _bookOpenInProgress > 0 || _readerCloseInProgress != 0)
        {
            await ShowMessageAsync(T("请先返回书库"), T("导入备份前请先关闭阅读器，避免正在保存的阅读记录被打断。"));
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入 Libzora 备份"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Libzora 备份")) { Patterns = [$"*{AppBackupService.FileExtension}"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !await ConfirmAsync(T("导入 Libzora 备份"), T("导入会覆盖当前书库、封面和阅读记录，确定继续吗？"))) return;
        if (_backupBusy || _readingDataResetBusy) return;
        if (_s3SyncBusy)
        {
            await ShowMessageAsync(T("请稍候"), T("云端同步正在进行，请完成后再导入备份。"));
            return;
        }
        if (_readerDocument is not null || _readerIsPdf || _bookOpenInProgress > 0 || _readerCloseInProgress != 0)
        {
            await ShowMessageAsync(T("请先返回书库"), T("导入备份前请先关闭阅读器，避免正在保存的阅读记录被打断。"));
            return;
        }
        _backupImportInProgress = true;
        _backupBusy = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = true;
        TaskProgressPopupText.Text = T("正在导入备份…");
        try
        {
            var result = await _backupService.ImportAsync(path, _lifetimeCancellation.Token);
            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(_lifetimeCancellation.Token);
            // The imported archive may represent an older or different local
            // state. Reset the previous sync baseline so its missing rows are
            // not interpreted as user deletions on the next S3 sync.
            await _s3SyncService.ResetLocalBaselineAsync(
                _s3SyncStoredSettings.DeviceId,
                _lifetimeCancellation.Token);
            _lastS3SyncAt = null;
            await _library.InitializeAsync(_lifetimeCancellation.Token);
            await _readerData.InitializeAsync(_lifetimeCancellation.Token);
            await _readerProgressRecovery.InitializeAsync(_lifetimeCancellation.Token);
            await _readerReadingTimeRecovery.InitializeAsync(_lifetimeCancellation.Token);
            await _s3SyncService.InitializeDeletionTrackingAsync(_lifetimeCancellation.Token, _s3SyncStoredSettings.DeviceId);
            _appSettings = await _appSettingsStore.LoadAsync(_lifetimeCancellation.Token);
            LoadReaderVerticalDebugBoxesSetting();
            _readerAiSettings = result.AiSettings;
            _kindleEmailSettings = result.KindleEmailSettings;
            PopulateSettingsControls();
            ApplyReaderAiSettingsToControls();
            await RefreshLibraryDataAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            SettingsBackupStatusText.Text = T("已导入 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            S3SyncStatusText.Text = T("本地备份已导入；下次 云端同步将按导入内容建立新的本地基线。 ");
            TaskProgressPopupText.Text = T("已导入 {0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            UpdateLibraryUi();
            HandleLocalDataChanged(LocalDataChangeKind.Library);
        }
        catch (Exception exception)
        {
            SettingsBackupStatusText.Text = T("备份导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("导入失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            _backupImportInProgress = false;
            _backupBusy = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private void KindleEmailSettingsCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        PopulateKindleEmailControls();
        SettingsSendToKindleExpander.IsExpanded = false;
        KindleEmailSettingsStatusText.Text = string.Empty;
    }

    private async void KindleEmailSettingsTestButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_kindleEmailTestBusy) return;

        var settings = ReadKindleEmailDraft();
        var validation = settings.Validate();
        if (validation is not null)
        {
            KindleEmailSettingsStatusText.Text = UiText.Localize(validation);
            return;
        }

        if (!_appSettings.NetworkEnabled)
        {
            KindleEmailSettingsStatusText.Text = T("网络功能已关闭，请在设置中启用后再测试发信。");
            return;
        }

        _kindleEmailTestBusy = true;
        KindleEmailSettingsTestButton.IsEnabled = false;
        try
        {
            KindleEmailSettingsStatusText.Text = T("正在发送测试邮件…");
            await _kindleEmailSender.SendTestAsync(settings, _lifetimeCancellation.Token);
            KindleEmailSettingsStatusText.Text = T("测试发信成功，邮件已提交到邮箱服务器，请检查 Kindle 收件邮箱。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogSendDiagnostic("KindleEmailSettingsTestButton_Click", exception);
            KindleEmailSettingsStatusText.Text = T("测试发信失败：{0}", UiText.Localize(KindleEmailSender.DescribeFailure(exception)));
        }
        finally
        {
            _kindleEmailTestBusy = false;
            KindleEmailSettingsTestButton.IsEnabled = true;
        }
    }

    private async void MainReaderAiSettingsCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        await LoadMainReaderAiSettingsAsync();
        SettingsAiExpander.IsExpanded = false;
        MainReaderAiSettingsStatusText.Text = string.Empty;
    }

    private async void KindleEmailSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = KindleEmailSettings.Normalize(new KindleEmailSettings
        {
            KindleEmailAddress = KindleEmailRecipientBox.Text ?? string.Empty,
            SenderEmailAddress = KindleEmailSenderBox.Text ?? string.Empty,
            SmtpHost = KindleEmailSmtpHostBox.Text ?? string.Empty,
            SmtpPort = int.TryParse(KindleEmailSmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 587,
            SmtpUsername = KindleEmailUsernameBox.Text ?? string.Empty,
            SmtpPassword = KindleEmailPasswordBox.Text ?? string.Empty,
            EnableSsl = KindleEmailSslCheck.IsChecked != false
        });
        var validation = settings.Validate();
        if (validation is not null)
        {
            KindleEmailSettingsStatusText.Text = UiText.Localize(validation);
            return;
        }
        try
        {
            await _kindleEmailSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            _kindleEmailSettings = settings;
            KindleEmailSettingsStatusText.Text = T("Kindle 邮箱设置已保存。");
            ShowSettingsSavedStatus();
        }
        catch (Exception exception) { KindleEmailSettingsStatusText.Text = T("保存失败：{0}", UiText.Localize(exception.Message)); }
    }
}
