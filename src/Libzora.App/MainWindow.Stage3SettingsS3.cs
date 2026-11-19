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
/// S3 sync settings, local change tracking and sync lifecycle.
/// </summary>
public partial class MainWindow
{
    private void PopulateSettingsControls() => PopulateSettingsControlsCore(false, false);

    private void PopulateSettingsControlsCore(bool preserveS3Draft, bool preserveEmailDraft)
    {
        _suppressAppSettingsAutoSave = true;
        try
        {
            MainThemeBox.SelectedIndex = (int)_appSettings.MainTheme;
            ApplyMainAppearance();
            PreferredOpenFormatBox.SelectedIndex = _appSettings.PreferredOpenFormat switch
            {
                "pdf" => 1,
                "azw3" => 2,
                "mobi" => 3,
                _ => 0
            };
            UiLanguageBox.SelectedIndex = _appSettings.UiLanguage.Equals("en-US", StringComparison.Ordinal)
                ? 1
                : 0;
            CalibrePathBox.Text = _appSettings.CalibrePath;
            TranslationGoogleProxyBox.Text = _appSettings.Translation.GoogleProxyAddress;
            UpdateCalibreDetectionStatus();
            AiEnabledCheck.IsChecked = _appSettings.AiEnabled;
            DeleteBookAnnotationsCheck.IsChecked = _appSettings.DeleteAnnotationsWhenDeletingBook;
            AutoBackupCheck.IsChecked = _appSettings.AutoBackupEnabled;
            AutoBackupRetentionBox.Value = _appSettings.AutoBackupRetention;
            AutoGenerateReaderFormatsCheck.IsChecked = _appSettings.AutoGenerateEpubAndAzw3OnImport;
            CollectionsMutuallyExclusiveCheck.IsChecked = _appSettings.CollectionsMutuallyExclusive;
            NetworkEnabledCheck.IsChecked = _appSettings.NetworkEnabled;
            PluginDeveloperModeCheck.IsChecked = _appSettings.PluginDeveloperMode;
            PluginBackgroundTasksCheck.IsChecked = _appSettings.PluginBackgroundTasksEnabled;
            InstallPluginFromFolderButton.IsVisible = _appSettings.PluginDeveloperMode;
            AutoUpdateCheck.IsChecked = _appSettings.AutoUpdateCheckEnabled;
            DevelopmentUpdateCheck.IsChecked = _appSettings.DevelopmentUpdateCheckEnabled;
            AutoConnectDeviceCheck.IsChecked = _appSettings.AutoConnectDevice;
            CompareKindleLibraryCheck.IsChecked = _appSettings.CompareKindleLibraryEnabled;
            SendToKindleWebEnabledCheck.IsChecked = _appSettings.SendToKindleWebEnabled;
            GridGalleryDisplayCheck.IsChecked = _appSettings.GridGalleryDisplay;
            ShowSyncStatusIconCheck.IsChecked = _appSettings.ShowSyncStatusIcon;
            ShowLibraryPresenceIconCheck.IsChecked = _appSettings.ShowLibraryPresenceIcon;
            _showUnearnedReadingMedals = _appSettings.ShowUnearnedReadingMedals;
            ReadingMaterialsCollapsedByDefaultCheck.IsChecked = _appSettings.ReadingMaterialsCollapsedByDefault;
            AboutVersionText.Text = T("版本 {0}", ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly));
            CheckForUpdatesButton.IsEnabled = _updateService is not null;
            AboutUpdateStatusText.Text = _updateService is null
                ? T("当前平台暂不支持应用内更新")
                : T("尚未检查更新");
            AboutLogsStatusText.Text = string.Empty;
            SettingsDataPathText.Text = _paths.Data;
            UpdateDiagnosticsTexts();
            if (!preserveEmailDraft)
            {
                PopulateKindleEmailControls();
            }
            if (!preserveS3Draft) PopulateS3SyncControls();
        }
        finally
        {
            _suppressAppSettingsAutoSave = false;
        }
    }

    private void PopulateS3SyncControls(S3SyncSettings? settingsOverride = null)
    {
        var settings = S3SyncSettings.Normalize(settingsOverride ?? _s3SyncStoredSettings.Settings);
        var wasSuppressing = _suppressS3SettingsDraftTracking;
        _suppressS3SettingsDraftTracking = true;
        try
        {
            EnsureSyncProviderItems();
            SyncProviderBox.SelectedItem = SyncProviderBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is SyncProvider provider && provider == settings.Provider);
            S3SyncEnabledCheck.IsChecked = settings.Enabled;
            S3AutomaticSyncCheck.IsChecked = settings.AutomaticSyncEnabled;
            S3DownloadBookFilesCheck.IsChecked = settings.DownloadBookFilesOnSync;
            S3SyncIntervalBox.Value = settings.IntervalMinutes;
            S3EndpointBox.Text = settings.Endpoint;
            S3AccessKeyBox.Text = settings.AccessKey;
            S3SecretKeyBox.Text = settings.SecretKey;
            WebDavEndpointBox.Text = settings.WebDavEndpoint;
            WebDavUsernameBox.Text = settings.WebDavUsername;
            WebDavPasswordBox.Text = settings.WebDavPassword;
            S3BucketBox.Text = settings.Bucket;
            S3RegionBox.Text = settings.Region;
            S3PrefixBox.Text = settings.Prefix;
            S3PathStyleCheck.IsChecked = settings.PathStyle;
            S3SkipTlsVerifyCheck.IsChecked = settings.SkipTlsVerify;
            S3EncryptionKeyBox.Text = settings.EncryptionKey;
            S3EncryptionKeyConfirmBox.Text = string.Empty;
            S3TimeoutBox.Value = settings.TimeoutSeconds;
            S3ConcurrencyBox.Value = settings.ConcurrentRequests;
            S3SyncDeviceText.Text = T("当前设备 ID：{0}", _s3SyncStoredSettings.DeviceId[..Math.Min(12, _s3SyncStoredSettings.DeviceId.Length)]);
            UpdateSyncProviderControls();
        }
        finally
        {
            _suppressS3SettingsDraftTracking = wasSuppressing;
        }
        UpdateS3SettingsDraftState();
    }

    private S3SyncSettings ReadS3SyncSettingsFromControls() => S3SyncSettings.Normalize(new S3SyncSettings
    {
        Provider = SelectedSyncProvider,
        ICloudAccountIdentity = _s3SyncStoredSettings.Settings.ICloudAccountIdentity,
        ProviderChosenByUser = _s3SyncStoredSettings.Settings.ProviderChosenByUser,
        Enabled = S3SyncEnabledCheck.IsChecked == true,
        AutomaticSyncEnabled = S3AutomaticSyncCheck.IsChecked == true,
        DownloadBookFilesOnSync = S3DownloadBookFilesCheck.IsChecked == true,
        IntervalMinutes = S3SyncIntervalBox.Value is { } interval ? (int)interval : 30,
        Endpoint = S3EndpointBox.Text?.Trim() ?? string.Empty,
        AccessKey = S3AccessKeyBox.Text?.Trim() ?? string.Empty,
        SecretKey = S3SecretKeyBox.Text ?? string.Empty,
        WebDavEndpoint = WebDavEndpointBox.Text ?? string.Empty,
        WebDavUsername = WebDavUsernameBox.Text ?? string.Empty,
        WebDavPassword = WebDavPasswordBox.Text ?? string.Empty,
        Bucket = S3BucketBox.Text?.Trim() ?? string.Empty,
        Region = S3RegionBox.Text?.Trim() ?? string.Empty,
        Prefix = S3PrefixBox.Text?.Trim() ?? string.Empty,
        PathStyle = S3PathStyleCheck.IsChecked == true,
        SkipTlsVerify = S3SkipTlsVerifyCheck.IsChecked == true,
        EncryptionKey = S3EncryptionKeyBox.Text ?? string.Empty,
        AccountRegion = _modules.DataRegion,
        // Historical connection metadata is inert and is not rebound on login.
        AccountServerUrl = _s3SyncStoredSettings.Settings.AccountServerUrl,
        AccountUserId = _s3SyncStoredSettings.Settings.AccountUserId,
        TimeoutSeconds = S3TimeoutBox.Value is { } timeout ? (int)timeout : 60,
        ConcurrentRequests = S3ConcurrencyBox.Value is { } concurrency ? (int)concurrency : 4
    });

    private Task<bool> SaveS3SyncSettingsFromControlsAsync(bool showStatus = true, CancellationToken cancellationToken = default)
    {
        if (_s3SettingsSaveTask is { IsCompleted: false } pending) return pending;
        if (_s3SyncBusy) return Task.FromResult(false);
        _s3SettingsSaveTask = PersistS3SyncSettingsAsync(showStatus, cancellationToken);
        return _s3SettingsSaveTask;
    }

    private async Task<bool> PersistS3SyncSettingsAsync(bool showStatus, CancellationToken cancellationToken)
    {
        // Saving from this form is the user's explicit provider choice.
        var settings = ReadS3SyncSettingsFromControls() with { ProviderChosenByUser = true };
        var editVersion = _s3SettingsEditVersion;
        var validation = settings.Enabled ? settings.Validate() : null;
        if (validation is not null)
        {
            S3SyncStatusText.Text = UiText.Localize(validation);
            return false;
        }

        var storedEncryptionKey = S3SyncSettings.Normalize(_s3SyncStoredSettings.Settings).EncryptionKey;
        if (!string.Equals(settings.EncryptionKey, storedEncryptionKey, StringComparison.Ordinal))
        {
            var repeatedKey = S3EncryptionKeyConfirmBox.Text ?? string.Empty;
            if (settings.EncryptionKey.Length > 0
                && !string.Equals(settings.EncryptionKey, repeatedKey, StringComparison.Ordinal))
            {
                S3SyncStatusText.Text = T("两次输入的加密密钥不一致，请重新输入。");
                return false;
            }

            var confirmed = settings.EncryptionKey.Length > 0
                ? await ConfirmAsync(
                    T("确认加密密钥"),
                    T("加密密钥用于加密备份和自备存储中的同步数据。请确认两次输入一致，并已妥善保存。密钥遗忘后无法找回，已有加密数据也无法恢复。"),
                    T("使用此密钥"))
                : await ConfirmAsync(
                    T("清除加密密钥"),
                    T("清除本机保存的加密密钥后，将无法再解密已有加密备份或云端同步数据。确定清除吗？"),
                    T("清除密钥"));
            if (!confirmed)
            {
                S3SyncStatusText.Text = string.Empty;
                return false;
            }
        }

        _s3SettingsSaving = true;
        SetS3SyncControlsBusy(_s3SyncBusy);
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
            settings = await BindSyncRegionAsync(settings, linkedCancellation.Token);
            await _s3SyncService.SaveSettingsAsync(_s3SyncStoredSettings.DeviceId, settings, linkedCancellation.Token);
            var draftUnchanged = _s3SettingsEditVersion == editVersion && ReadS3SyncSettingsFromControls() == settings;
            settings = (await _s3SyncService.LoadSettingsAsync(linkedCancellation.Token)).Settings;
            if (settings != _s3SyncStoredSettings.Settings)
            {
                _s3DeletionConfirmationPending = false;
                _s3SyncCancelledByUser = false;
                // A newly selected target needs an initial sync even if the
                // previous provider was up to date a moment ago.
                _lastS3SyncAt = null;
                _localSyncChanges.MarkChanged();
            }
            _s3SyncStoredSettings = new S3SyncStoredSettings(_s3SyncStoredSettings.DeviceId, settings);
            RefreshS3SyncIndicatorFromSettings();
            // An edit made while persistence was awaiting I/O belongs to the
            // next draft and must not be replaced by the older saved snapshot.
            if (draftUnchanged) PopulateS3SyncControls();
            else UpdateS3SettingsDraftState();
            if (IsAutomaticS3SyncReady()) ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce);
            else _s3LocalChangeSyncTimer.Stop();
            if (showStatus) S3SyncStatusText.Text = T("云端同步设置已保存。");
            return true;
        }
        catch (Exception exception)
        {
            S3SyncStatusText.Text = T("保存 同步设置失败：{0}", UiText.Localize(exception.Message));
            return false;
        }
        finally
        {
            _s3SettingsSaving = false;
            SetS3SyncControlsBusy(_s3SyncBusy);
        }
    }

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

    private void LocalLibraryDataChanged(object? sender, LocalDataChangedEventArgs e) =>
        HandleLocalDataChanged(e.Kind);

    private void LocalReaderDataChanged(object? sender, LocalDataChangedEventArgs e) =>
        HandleLocalDataChanged(e.Kind);

    private void HandleLocalDataChanged(LocalDataChangeKind kind)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleLocalDataChanged(kind));
            return;
        }

        if (_lifetimeCancellation.IsCancellationRequested) return;
        _widgetSnapshots?.Schedule();
        _lastInProcessDataChangeUtc = DateTime.UtcNow;
        _localSyncChanges.MarkChanged();
        _s3DeletionConfirmationPending = false;
        _s3SyncCancelledByUser = false;
        if (kind == LocalDataChangeKind.Library) BroadcastLibraryChanged();
        else BroadcastReadingChanged(kind);
        if (kind is LocalDataChangeKind.Library or LocalDataChangeKind.BookReflection)
        {
            MarkReadingMaterialsDirty();
            if (kind == LocalDataChangeKind.BookReflection
                && LibraryDetailPane.IsVisible
                && _selectedCard is not null)
                _ = RefreshBookReflectionDetailsAsync(_selectedCard.Book.Id);
        }
        if (kind == LocalDataChangeKind.Library)
        {            _ = RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
        }
        if (!_s3SyncBusy && _s3SyncStoredSettings.Settings is { Enabled: true, IsConfigured: true, HasChosenCustomProvider: true })
            UpdateS3SyncIndicator(S3SyncIndicatorState.Pending);
        if (!IsAutomaticS3SyncReady())
        {
            _s3LocalChangeSyncTimer.Stop();
            return;
        }

        _localSyncChanges.ResetRetry();
        ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce, resetRetry: false);
    }

    private bool HasPendingS3LocalChanges =>
        _localSyncChanges.HasPendingChanges;

    private bool IsAutomaticS3SyncReady()
    {
        return _s3SyncStoredSettings.Settings.HasChosenCustomProvider
            && AutomaticSyncCoordinator.IsReady(_s3SyncStoredSettings.Settings,
            _stage3Ready, _appSettings.NetworkEnabled,
            _backupBusy || _readingDataResetBusy || _s3SyncExitInProgress,
            _s3DeletionConfirmationPending, _s3SyncCancelledByUser);
    }

    private void ScheduleS3LocalChangeSync(TimeSpan delay, bool resetRetry = true)
    {
        if (!HasPendingS3LocalChanges || !IsAutomaticS3SyncReady())
        {
            _s3LocalChangeSyncTimer.Stop();
            return;
        }

        if (resetRetry)
            _localSyncChanges.ResetRetry();
        _s3LocalChangeSyncTimer.Stop();
        _s3LocalChangeSyncTimer.Interval = delay;
        _s3LocalChangeSyncTimer.Start();
    }

    private TimeSpan GetNextS3LocalChangeRetryDelay() => _localSyncChanges.NextRetryDelay();

    private async Task RunPendingS3LocalChangeSyncAsync()
    {
        _s3LocalChangeSyncTimer.Stop();
        if (!HasPendingS3LocalChanges || !IsAutomaticS3SyncReady()) return;

        var succeeded = await RunS3SyncAsync(silent: true);
        if (succeeded)
        {
            _localSyncChanges.ResetRetry();
            if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
                ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce, resetRetry: false);
            return;
        }

        if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
        {
            // A new local write may have scheduled its own short debounce
            // while the previous sync was in flight. Keep that earlier retry
            // instead of replacing it with the backoff for the old failure.
            if (_s3LocalChangeSyncTimer.IsEnabled)
                return;
            if (!_s3SyncBusy && _s3SyncIndicatorState is not (S3SyncIndicatorState.Failed or S3SyncIndicatorState.Warning))
                UpdateS3SyncIndicator(S3SyncIndicatorState.Pending);
            ScheduleS3LocalChangeSync(GetNextS3LocalChangeRetryDelay(), resetRetry: false);
        }
    }

    private async Task RunLifecycleS3SyncAsync(bool waitForExistingSync = false, CancellationToken cancellationToken = default)
    {
        if (!_stage3Ready)
            return;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
        var token = linkedCancellation.Token;
        try
        {
            if (!_appSettings.NetworkEnabled)
            {
                if (waitForExistingSync) await WaitForS3SyncAsync(token);
                return;
            }

            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(token);
            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.Enabled || !settings.AutomaticSyncEnabled || !settings.IsConfigured
                || _s3DeletionConfirmationPending || _s3SyncCancelledByUser)
            {
                if (waitForExistingSync) await WaitForS3SyncAsync(token);
                return;
            }

            // A local write can land while a startup/exit checkpoint is
            // running. Give that write another checkpoint before continuing;
            // the cap prevents a busy shutdown from waiting forever if some
            // background activity keeps producing updates.
            await AutomaticSyncCoordinator.RunCheckpointsAsync(
                syncToken => RunS3SyncAsync(silent: true, syncToken),
                () => HasPendingS3LocalChanges, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("sync.auto", exception);
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("自动 云端同步失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
    }

    private async Task WaitForS3SyncAsync(CancellationToken cancellationToken = default)
    {
        if (_s3SyncCompletion is { } completion)
        {
            using var registration = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => _s3SyncCancellation?.Cancel()));
            await completion.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteWindowCloseAfterS3SyncAsync()
    {
        try
        {
            _s3LocalChangeSyncTimer.Stop();
            if (!await PrepareSettingsForExitAsync()) return;
            SettingsPage.IsEnabled = false;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            _s3SyncExitCancellation = cancellation;
            try
            {
                if (!_skipS3SyncOnExit)
                {
                    if (_s3SyncBusy || IsAutomaticS3SyncReady())
                        SetS3SyncStatus(T("正在退出前同步…"), silent: false);
                    await RunLifecycleS3SyncAsync(waitForExistingSync: true, cancellation.Token).WaitAsync(cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _s3SyncCancellation?.Cancel();
            }
            // The close flag is set only after all pending and in-flight
            // settings writes have completed successfully.
            if (!await FlushAppSettingsAsync()) return;
            _allowWindowCloseForS3Sync = true;
            Close();
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("退出前保存失败：{0}", UiText.Localize(exception.Message)), 4000);
        }
        finally
        {
            _s3SyncExitCancellation = null;
            if (!_allowWindowCloseForS3Sync)
            {
                SettingsPage.IsEnabled = true;
                _s3SyncExitInProgress = false;
                if (IsAutomaticS3SyncReady()) ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce);
            }
        }
    }

    private async Task RunAutomaticS3SyncIfDueAsync()
    {
        if (_s3SyncBusy || !IsAutomaticS3SyncReady()) return;
        try
        {
            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.Enabled || !settings.AutomaticSyncEnabled) return;
            if (!AutomaticSyncCoordinator.IsDue(HasPendingS3LocalChanges,
                    _lastS3SyncAt, DateTimeOffset.UtcNow, settings.IntervalMinutes))
                return;
            await RunS3SyncAsync(silent: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("sync.auto", exception);
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("自动 云端同步失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
    }

    private async Task<bool> RunS3SyncAsync(bool silent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!silent && (_s3SettingsDirty || _s3SettingsSaving))
        {
            OpenSettingsExpander("Data", SettingsS3Expander);
            SetS3SyncStatus(T("请先保存或放弃 同步配置修改，再执行同步。"), silent: false);
            return false;
        }
        if (!silent && !await FlushAppSettingsAsync()) return false;
        if (_readingDataResetBusy)
        {
            if (!silent) SetS3SyncStatus(T("阅读数据正在重置，请完成后再执行云端同步。"), silent: false);
            return false;
        }
        if (_backupBusy)
        {
            if (!silent) SetS3SyncStatus(T("备份任务正在进行，请完成后再执行 云端同步。"), silent: false);
            return false;
        }
        if (_s3SyncBusy)
        {
            if (_s3SyncCompletion is { } completion)
            {
                await WaitForS3SyncAsync(cancellationToken);
                return await completion.Task;
            }
            return false;
        }
        if (!_appSettings.NetworkEnabled)
        {
            if (!silent)
            {
                var status = T("请先开启“允许网络功能”。");
                S3SyncStatusText.Text = status;
                SetTaskStatus(status);
                UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, status);
            }
            return false;
        }
        if (_readerDocument is not null || _readerIsPdf || _bookOpenInProgress > 0 || _readerCloseInProgress != 0)
        {
            if (!silent) S3SyncStatusText.Text = T("请先返回书库，再执行同步。");
            return false;
        }

        var localChangeVersionAtStart = _localSyncChanges.CaptureVersion();
        var succeeded = false;
        var refreshAfterFailure = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
        var token = cancellation.Token;
        // Claim the sync slot before the first await. Saving/loading settings
        // can yield to the dispatcher; without claiming it here, a manual
        // click and an automatic tick could both start an S3 operation.
        _s3SyncBusy = true;
        _crossProcessDataRefreshTimer?.Stop();
        _s3TestConnectionCancellation?.Cancel();
        _s3SyncCancellation = cancellation;
        if (!silent) _s3SyncCancelledByUser = false;
        _s3SyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetS3SyncControlsBusy(true);
        try
        {
            if (_s3SettingsSaveTask is { IsCompleted: false } saving && !await saving) return false;
            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(token);

            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.HasChosenCustomProvider)
            {
                var confirmationStatus = T("请确认自备 S3 / WebDAV 连接信息并保存后再同步。");
                SetS3SyncStatus(confirmationStatus, silent);
                UpdateS3SyncIndicator(S3SyncIndicatorState.NotConfigured, confirmationStatus);
                return false;
            }
            var validation = settings.Validate();
            if (validation is not null)
            {
                var validationStatus = UiText.Localize(validation);
                SetS3SyncStatus(validationStatus, silent);
                if (!silent)
                {
                    UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, validationStatus);
                }
                else
                {
                    UpdateS3SyncIndicator(S3SyncIndicatorState.NotConfigured, validationStatus);
                }
                return false;
            }

            UpdateS3SyncIndicator(S3SyncIndicatorState.Syncing);
            SetS3SyncStatus(T("正在同步到 {0}…", settings.ProviderName), silent);
            var progress = new Progress<string>(message =>
            {
                if (!ReferenceEquals(_s3SyncCancellation, cancellation) || token.IsCancellationRequested)
                    return;

                void ApplyProgressOnUiThread()
                {
                    if (ReferenceEquals(_s3SyncCancellation, cancellation) && !token.IsCancellationRequested)
                        SetS3SyncStatus(UiText.Localize(message), silent);
                }

                // S3 progress can be reported by an SDK worker thread. Both
                // localization (it resolves Avalonia resources) and the
                // status controls must therefore run on the UI thread.
                if (Dispatcher.UIThread.CheckAccess())
                    ApplyProgressOnUiThread();
                else
                    Dispatcher.UIThread.Post(ApplyProgressOnUiThread);
            });
            refreshAfterFailure = true;
            var result = await SyncDeletionCoordinator.RunAsync(
                (options, syncToken) => _s3SyncService.SyncAsync(
                    _s3SyncStoredSettings.DeviceId, settings, progress, syncToken, options),
                async (exception, syncToken) =>
                {
                    _s3DeletionConfirmationPending = true;
                    _s3LocalChangeSyncTimer.Stop();
                    var message = UiText.Localize(exception.Message);
                    SetS3SyncStatus(message + " " + T("请点击“立即同步”审核并确认删除。"), silent);
                    UpdateS3SyncIndicator(S3SyncIndicatorState.ConfirmationRequired, message);
                    if (silent || _s3SyncExitInProgress || !await ConfirmAsync(
                            T("确认同步删除"), message + "\n\n" + T("继续后，这些删除会同步到其他设备。"),
                            T("同步这些删除"), syncToken))
                        return false;
                    _s3DeletionConfirmationPending = false;
                    UpdateS3SyncIndicator(S3SyncIndicatorState.Syncing);
                    return true;
                }, token);
            if (result is null) return false;
            _s3DeletionConfirmationPending = false;
            _lastS3SyncAt = DateTimeOffset.UtcNow;
            _lastS3SyncResult = result;

            if (result.SettingsApplied > 0 || _s3RemoteSettingsChanged)
                await RefreshSettingsAfterS3SyncAsync(token);

            if (result.Changed)
            {
                await RefreshLibraryAfterS3SyncAsync(token);
            }
            else
            {
                // A local book can become synced without changing any SQLite
                // rows. Refresh just the badges in that case.
                await RefreshBookSyncStatusesAsync(token);
            }
            // Sync applies remote reading data directly to SQLite, so the
            // ReaderDataService change event is not raised. Refresh the
            // visible reader-facing views after every completed sync.
            await RefreshS3AffectedViewsAsync();
            refreshAfterFailure = false;
            var status = result.IsPartial
                ? T("同步未完成：新增 {0} 本书，下载 {1} 个文件；部分数据未能同步，请重试。", result.BooksAdded, result.FilesDownloaded)
                : result.Changed
                ? T("同步完成：新增 {0} 本书，下载 {1} 个文件，应用 {2} 条批注；正文将在打开时按需下载。", result.BooksAdded, result.FilesDownloaded, result.AnnotationsApplied)
                : T("同步完成，当前已是最新。 ");
            if (!string.IsNullOrWhiteSpace(result.Warning)) status += " " + UiText.Localize(result.Warning);
            SetS3SyncStatus(status, silent);
            succeeded = !result.IsPartial;
            if (succeeded)
                _localSyncChanges.MarkSynced(localChangeVersionAtStart);
            // A write that landed while the network operation was running is
            // deliberately left pending and is shown as such. This avoids a
            // misleading green check between the two sync passes.
            UpdateS3SyncIndicator(
                result.IsPartial || !string.IsNullOrWhiteSpace(result.Warning)
                    ? S3SyncIndicatorState.Warning
                    : HasPendingS3LocalChanges
                    ? S3SyncIndicatorState.Pending
                    : S3SyncIndicatorState.Succeeded,
                result.IsPartial || !string.IsNullOrWhiteSpace(result.Warning) ? status : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var status = T("云端同步已取消。");
            SetS3SyncStatus(status, silent);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Warning, status);
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("sync.run", exception, ("silent", silent));
            var error = UiText.Localize(exception.Message);
            var status = T("云端同步失败：{0}", error);
            SetS3SyncStatus(status, silent);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
        finally
        {
            // A failure after the merge (for example an upload cancellation)
            // must not leave the library displaying rows already changed on disk.
            if (refreshAfterFailure && !_lifetimeCancellation.IsCancellationRequested)
            {
                try
                {
                    if (_s3RemoteSettingsChanged)
                        await RefreshSettingsAfterS3SyncAsync(_lifetimeCancellation.Token);
                    await RefreshLibraryAfterS3SyncAsync(_lifetimeCancellation.Token);
                    await RefreshS3AffectedViewsAsync();
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
                catch (Exception exception) { Debug.WriteLine($"S3 library refresh failed: {exception.Message}"); }
            }
            // Cover delayed WAL/checkpoint notifications from our own sync.
            // Real service DataChanged events still advance the pending version.
            _lastInProcessDataChangeUtc = DateTime.UtcNow;
            _s3SyncBusy = false;
            _s3SyncCancellation = null;
            _s3SyncCompletion?.TrySetResult(succeeded);
            _s3SyncCompletion = null;
            SetS3SyncControlsBusy(false);
        }

        return succeeded;
    }

    private async Task RefreshLibraryAfterS3SyncAsync(CancellationToken cancellationToken)
    {
        await RefreshLibraryDataAsync(cancellationToken);
        SetupFilterControls();
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
    }

    private async Task RefreshS3AffectedViewsAsync()
    {
        // S3 merges write the reader database directly, so ReaderDataService
        // does not emit its local change event. Keep both reader-facing
        // collections in sync even when the page was already open.
        MarkReadingMaterialsDirty();
        if (ReadingDashboardPage.IsVisible)
            await RefreshReadingDashboardAsync();
        if (ReadingMaterialsPage.IsVisible)
            await RefreshReadingMaterialsAsync();
        if (LibraryDetailPane.IsVisible && _selectedCard is not null)
            await RefreshBookReflectionDetailsAsync(_selectedCard.Book.Id);
    }

    private async Task RefreshSettingsAfterS3SyncAsync(CancellationToken cancellationToken)
    {
        if (!await FlushAppSettingsAsync()) return;
        var editVersion = _appSettingsEditVersion;
        var previousEmail = _kindleEmailSettings;
        var refreshedSettings = await _appSettingsStore.LoadAsync(cancellationToken);
        var refreshedEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        var refreshedAi = await _aiSettingsStore.LoadAsync(cancellationToken);
        var preserveEmail = !KindleEmailSettingsEqual(ReadKindleEmailDraft(), previousEmail);
        if (_appSettingsEditVersion != editVersion)
        {
            if (!await FlushAppSettingsAsync()) return;
        }
        else _appSettings = refreshedSettings;
        _kindleEmailSettings = refreshedEmail;
        _readerAiSettings = refreshedAi;
        if (Application.Current is App app) app.ApplyLanguage(_appSettings.UiLanguage);
        PopulateSettingsControlsCore(_s3SettingsDirty, preserveEmail);
        ApplyReaderAiSettingsToControls();
        _s3RemoteSettingsChanged = false;
    }

    // Basic settings auto-save (WinUI reference MainWindow.Productivity.cs):
    // any toggle/combo/number/text change schedules a 600 ms debounced save so
    // the user never needs the save button for the basic options.
}
