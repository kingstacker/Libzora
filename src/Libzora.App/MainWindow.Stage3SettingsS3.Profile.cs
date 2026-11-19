using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Connection profile import/export and connection test.
/// </summary>
public partial class MainWindow
{
    private async void S3SaveSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveS3SyncSettingsFromControlsAsync();
        }
        catch (Exception exception)
        {
            S3SyncStatusText.Text = T("保存 同步设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ExportS3ConnectionProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ReadS3SyncSettingsFromControls();
        var extension = SyncConnectionProfileService.FileExtension(settings.Provider);
        if (!await ConfirmAsync(
                T("导出同步连接参数"),
                T("导出的文件会包含当前同步方式的账号和密码，请像密码一样妥善保管，不要上传到公共位置。确定继续吗？")))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出同步连接参数"),
            SuggestedFileName = $"Libzora-{settings.ProviderName}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{extension}",
            FileTypeChoices =
            [
                new FilePickerFileType(T("同步连接参数"))
                {
                    Patterns = [$"*{extension}"]
                }
            ]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            await SyncConnectionProfileService.ExportAsync(
                path,
                settings,
                _lifetimeCancellation.Token);
            S3ConnectionProfileStatusText.Text = T("已导出同步连接参数：{0}", path);
        }
        catch (Exception exception)
        {
            S3ConnectionProfileStatusText.Text = T("导出同步连接参数失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ImportS3ConnectionProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入同步连接参数"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("同步连接参数"))
                {
                    Patterns = [$"*{S3ConnectionProfileService.FileExtension}", $"*{SyncConnectionProfileService.WebDavFileExtension}"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var imported = await SyncConnectionProfileService.ImportAsync(
                path,
                ReadS3SyncSettingsFromControls(),
                _lifetimeCancellation.Token);
            PopulateS3SyncControls(imported);
            S3ConnectionProfileStatusText.Text = T("已导入同步连接参数；请检查后点击“保存设置”写入。");
        }
        catch (Exception exception)
        {
            S3ConnectionProfileStatusText.Text = T("导入同步连接参数失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void S3TestConnectionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_s3SyncBusy || _s3SettingsSaving || _s3TestConnectionCancellation is not null) return;
        if (!_appSettings.NetworkEnabled)
        {
            S3SyncStatusText.Text = T("请先开启“允许网络功能”。");
            return;
        }

        var settings = ReadS3SyncSettingsFromControls();
        var validation = settings.Validate();
        if (validation is not null)
        {
            S3SyncStatusText.Text = UiText.Localize(validation);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _s3TestConnectionCancellation = cancellation;
        S3TestConnectionButton.IsEnabled = false;
        S3SyncStatusText.Text = T("正在测试 {0} 连接…", settings.ProviderName);
        try
        {
            settings = await BindSyncRegionAsync(settings, cancellation.Token);
            await _s3SyncService.TestConnectionAsync(settings, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            S3SyncStatusText.Text = T("{0} 连接成功。", settings.ProviderName);
            if (settings == _s3SyncStoredSettings.Settings) RefreshS3SyncIndicatorFromSettings();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellation.IsCancellationRequested || _s3SyncBusy) return;
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("{0} 连接失败：{1}", settings.ProviderName, error);
            if (settings == _s3SyncStoredSettings.Settings) UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
        finally
        {
            _s3TestConnectionCancellation = null;
            UpdateS3SettingsActions();
        }
    }

    private async void S3SyncNowButton_Click(object? sender, RoutedEventArgs e) => await RunS3SyncAsync(silent: false);

    private async void S3DetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var summary = await _s3SyncService.GetStatusAsync(
                _s3SyncStoredSettings.DeviceId,
                _s3SyncStoredSettings.Settings,
                _lifetimeCancellation.Token);
            var lines = new List<string>
            {
                T("上次同步：{0}", summary.LastSyncAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? T("尚未同步")),
                T("云端快照：{0} 本书 · {1} 个文件 · {2} 条批注",
                    summary.SnapshotBookCount, summary.SnapshotFileCount, summary.SnapshotAnnotationCount),
                T("已登记远端书籍：{0} 本", summary.RemoteBookCount),
                T("待按需下载文件：{0} 个", summary.PendingFileDownloadCount),
                T("删除墓碑：{0} 条", summary.TombstoneCount)
            };
            if (summary.SnapshotCreatedAt is { } created)
                lines.Add(T("快照时间：{0}", created.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
            if (_lastS3SyncResult is { } result)
            {
                lines.Add(string.Empty);
                lines.Add(T("本次同步：新增 {0} 本，下载 {1} 个文件，应用 {2} 条批注，设置 {3} 项",
                    result.BooksAdded, result.FilesDownloaded, result.AnnotationsApplied, result.SettingsApplied));
                if (result.IsPartial) lines.Add(T("本次同步部分完成，稍后会自动重试。"));
                if (!string.IsNullOrWhiteSpace(result.Warning)) lines.Add(result.Warning);
            }
            await ShowMessageAsync(T("同步详情"), string.Join("\n", lines));
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(T("无法读取同步详情"), UiText.Localize(exception.Message));
        }
    }

    private void S3RemoteSettingsApplied(object? sender, EventArgs e) => _s3RemoteSettingsChanged = true;

    private void S3CancelSyncButton_Click(object? sender, RoutedEventArgs e)
    {
        _s3SyncCancelledByUser = true;
        _s3LocalChangeSyncTimer.Stop();
        S3CancelSyncButton.IsEnabled = false;
        _s3SyncCancellation?.Cancel();
        _s3SyncExitCancellation?.Cancel();
    }

    private void SetS3SyncControlsBusy(bool busy)
    {
        Control[] controls =
        [
            SyncProviderBox, WebDavEndpointBox, WebDavUsernameBox, WebDavPasswordBox,
            S3SyncEnabledCheck, S3AutomaticSyncCheck, S3DownloadBookFilesCheck, S3SyncIntervalBox, S3EndpointBox,
            S3AccessKeyBox, S3SecretKeyBox, S3BucketBox, S3RegionBox, S3PrefixBox,
            S3PathStyleCheck, S3SkipTlsVerifyCheck, S3EncryptionKeyBox, S3EncryptionKeyConfirmBox, S3TimeoutBox,
            S3ConcurrencyBox, S3SaveSettingsButton, S3DiscardSettingsButton, S3TestConnectionButton, S3SyncNowButton,
            S3ExportConnectionProfileButton, S3ImportConnectionProfileButton
        ];
        foreach (var control in controls) control.IsEnabled = !busy && !_s3SettingsSaving;
        S3CancelSyncButton.IsVisible = busy;
        S3CancelSyncButton.IsEnabled = busy;
        UpdateS3SettingsActions();
    }

    private void SetS3SyncStatus(string status, bool silent)
    {
        if (_s3SyncExitInProgress)
            status += " " + T("再次关闭窗口可取消同步并退出；最多等待 30 秒。");
        S3SyncStatusText.Text = status;
        if (!silent || _s3SyncExitInProgress) SetTaskStatus(status);
    }
}
