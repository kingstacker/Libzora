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
/// Reading and saving application settings, including data-root
/// migration.
/// </summary>
public partial class MainWindow
{
    private AppSettings ReadAppSettingsFromControls()
    {
        return AppSettings.Normalize(_appSettings with
        {
            MainTheme = ReadMainThemeFromControls(),
            UiLanguage = UiText.NormalizeLanguage(UiLanguageBox.SelectedItem is ComboBoxItem languageItem
                ? languageItem.Tag?.ToString()
                : _appSettings.UiLanguage),
            PreferredOpenFormat = (PreferredOpenFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "epub",
            CalibrePath = CalibrePathBox.Text ?? string.Empty,
            AutoBackupEnabled = AutoBackupCheck.IsChecked == true,
            DeleteAnnotationsWhenDeletingBook = DeleteBookAnnotationsCheck.IsChecked == true,
            AutoBackupRetention = AutoBackupRetentionBox.Value is { } retention ? (int)retention : 5,
            AutoGenerateEpubAndAzw3OnImport = AutoGenerateReaderFormatsCheck.IsChecked == true,
            CollectionsMutuallyExclusive = CollectionsMutuallyExclusiveCheck.IsChecked != false,
            AiEnabled = AiEnabledCheck.IsChecked != false,
            NetworkEnabled = NetworkEnabledCheck.IsChecked != false,
            PluginDeveloperMode = PluginDeveloperModeCheck.IsChecked == true,
            PluginBackgroundTasksEnabled = PluginBackgroundTasksCheck.IsChecked != false,
            AutoUpdateCheckEnabled = AutoUpdateCheck.IsChecked != false,
            DevelopmentUpdateCheckEnabled = DevelopmentUpdateCheck.IsChecked == true,
            AutoConnectDevice = AutoConnectDeviceCheck.IsChecked != false,
            CompareKindleLibraryEnabled = CompareKindleLibraryCheck.IsChecked != false,
            SendToKindleWebEnabled = SendToKindleWebEnabledCheck.IsChecked != false,
            GridGalleryDisplay = GridGalleryDisplayCheck.IsChecked == true,
            StackBookSeries = StackBookSeriesCheck.IsChecked != false,
            LibraryViewMode = _lastNonCollectionLibraryViewMode.ToString(),
            LibrarySortMode = ViewModel.SortMode.ToString(),
            DeviceLibraryViewMode = _deviceGridView ? "Grid" : "List",
            DeviceBookSortMode = (DeviceBookSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "title",
            ShowSyncStatusIcon = ShowSyncStatusIconCheck.IsChecked != false,
            ShowLibraryPresenceIcon = ShowLibraryPresenceIconCheck.IsChecked != false,
            ShowUnearnedReadingMedals = _showUnearnedReadingMedals,
            ReadingMaterialsCollapsedByDefault = ReadingMaterialsCollapsedByDefaultCheck.IsChecked != false,
            Translation = BookTranslationSettings.Normalize(_appSettings.Translation with
            {
                GoogleProxyAddress = TranslationGoogleProxyBox.Text ?? string.Empty
            }),
            // Reader layout preferences are edited from the reader layout
            // popup; keep the saved global defaults untouched here.
            DefaultReaderLayout = _appSettings.DefaultReaderLayout
        });
    }

    private async Task<bool> SaveAppSettingsCoreAsync()
    {
        await _appSettingsSaveGate.WaitAsync();
        try
        {
            if (_appSettingsSavedVersion == _appSettingsEditVersion) return true;
            var version = _appSettingsEditVersion;
            var settings = ReadAppSettingsFromControls();
            var updateChannelChanged = _appSettings.DevelopmentUpdateCheckEnabled
                != settings.DevelopmentUpdateCheckEnabled;
            if (updateChannelChanged)
            {
                // A pending result belongs to the channel that was selected
                // when it was found. Drop it when switching channels so a
                // disabled development channel cannot keep showing or
                // installing an old development package.
                settings = AppSettings.Normalize(settings with
                {
                    LastAutoUpdateCheckAt = null,
                    PendingUpdateVersion = null,
                    PendingUpdateReleaseNotes = null,
                    PendingUpdateReleaseNotesEnglish = null,
                    PendingUpdatePackagePath = null,
                    PendingUpdateDownloadedAt = null,
                    PendingUpdateInstallStartedAt = null
                });
                _pendingUpdateVersion = null;
                HideUpdateBadge();
            }
            var autoConnectChanged = _appSettings.AutoConnectDevice != settings.AutoConnectDevice;
            _appSettings = settings;
            await _appSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            _appSettingsSavedVersion = version;
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            Environment.SetEnvironmentVariable("LIBZORA_CALIBRE_CONVERT",
                string.IsNullOrWhiteSpace(settings.CalibrePath) ? null : settings.CalibrePath,
                EnvironmentVariableTarget.Process);
            foreach (var group in ReadingMaterialGroups)
                group.IsExpanded = !settings.ReadingMaterialsCollapsedByDefault;
            UpdateLibraryUi();
            SettingsStatusText.Text = string.Empty;
            if (_appSettingsStartupSettled && !_s3SyncExitInProgress && _appSettingsAutoSaveShowStatus)
                ShowSettingsSavedStatus();
            _appSettingsAutoSaveShowStatus = true;
            if (autoConnectChanged && settings.AutoConnectDevice && !_s3SyncExitInProgress)
            {
                _ignoredDeviceId = null;
                _ = RefreshDevicesAsync(scanBooks: DevicePage.IsVisible, _lifetimeCancellation.Token);
            }
            return true;
        }
        catch (Exception exception)
        {
            var message = T("保存失败：{0}", UiText.Localize(exception.Message));
            SettingsStatusText.Text = message;
            ShowSettingsCapsule(message, 4000);
            return false;
        }
        finally { _appSettingsSaveGate.Release(); }
    }

    private async void MigrateDataDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerDocument is not null || _readerIsPdf || _bookOpenInProgress > 0 || _readerCloseInProgress != 0)
        {
            ShowSettingsCapsule(T("请先关闭阅读器，再迁移数据目录。"), 4000);
            await ShowMessageAsync(T("请先返回书库"), T("迁移数据目录前请关闭阅读器。"));
            return;
        }
        if (_backupBusy || _readingDataResetBusy || _s3SyncBusy || _isTransferring)
        {
            await ShowMessageAsync(
                T("请稍候"),
                T("备份、同步或设备传输正在进行，请完成后再迁移数据目录。"));
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("选择新的 Libzora 数据目录"),
            AllowMultiple = false
        });
        var targetRoot = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(targetRoot)) return;
        targetRoot = Path.GetFullPath(targetRoot);
        if (string.Equals(targetRoot, Path.GetFullPath(_paths.Root), StringComparison.OrdinalIgnoreCase))
        {
            ShowSettingsCapsule(T("所选目录已经是当前数据根目录。"), 4000);
            return;
        }
        if (_backupBusy || _readingDataResetBusy || _s3SyncBusy || _isTransferring) return;

        var restarting = false;
        _suppressAppSettingsAutoSave = true;
        _dataRootMigrationInProgress = true;
        CancelAppSettingsDebounce();
        // Background sync would queue on the migration's data lock. Keep those
        // writers idle for the duration; they restart if migration fails.
        _s3SyncTimer.Stop();
        _s3LocalChangeSyncTimer.Stop();
        _backupBusy = true;
        ShowTaskProgressPopup();
        TaskProgressPopupBar.IsIndeterminate = false;
        TaskProgressPopupBar.Value = 0;
        TaskProgressPopupText.Text = T("正在准备迁移数据目录…");
        try
        {
            using var progress = new CoalescingUiProgress<AppDataMigrationProgress>(value =>
            {
                TaskProgressPopupBar.Value = Math.Clamp(value.Fraction, 0, 1) * 100;
                TaskProgressPopupText.Text = UiText.Localize(value.Message);
            });
            // The service performs SQLite snapshot/verification work that is
            // partly synchronous; keep it off the UI thread so the progress
            // popup and cancellation stay responsive.
            var migrationService = new AppDataMigrationService(_paths, _rootConfigurationDirectory);
            var result = await Task.Run(
                () => migrationService.MigrateAsync(targetRoot, progress, _lifetimeCancellation.Token),
                _lifetimeCancellation.Token);
            TaskProgressPopupBar.Value = 100;
            TaskProgressPopupText.Text = T("迁移完成：{0} 本书、{1} 个文件。", result.BookCount, result.FileCount);
            if (result.RestartRequired)
            {
                // The running process still uses the old AppPaths. Continuing
                // to use it would write books/settings into the old root and
                // silently lose them on the next launch, so exit as soon as
                // the new root is published and restart against it.
                TaskProgressPopupText.Text = T("迁移完成，正在重启 Libzora…");
                ShowSettingsCapsule(
                    T("迁移完成：{0} 本书、{1} 个文件。正在重启…", result.BookCount, result.FileCount),
                    5000,
                    success: true);
                restarting = true;
                await Task.Delay(1200);
                RestartAfterDataRootMigration();
                return;
            }

            ShowSettingsCapsule(T("迁移完成。"), 4000);
            await ShowMessageAsync(T("迁移完成"), T("迁移完成：{0} 本书、{1} 个文件已复制到：\n{2}", result.BookCount, result.FileCount, result.TargetRoot));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            ShowSettingsCapsule(T("迁移已取消，原数据目录未改变。"), 4000);
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("迁移失败：{0}", UiText.Localize(exception.Message)), 6000);
            await ShowMessageAsync(T("迁移失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (!restarting)
            {
                _suppressAppSettingsAutoSave = false;
                _dataRootMigrationInProgress = false;
                _s3SyncTimer.Start();
                _s3LocalChangeSyncTimer.Start();
            }
            _backupBusy = false;
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    /// <summary>
    /// Stops background writers and exits without the normal exit-time saves
    /// (they would target the old root). The platform head starts a new
    /// process after releasing the single-instance lock.
    /// </summary>
    private void RestartAfterDataRootMigration()
    {
        _allowWindowCloseForDataRootSwitch = true;
        _applicationShuttingDown = true;
        _stage3Timer.Stop();
        _deviceChangeDebounceTimer.Stop();
        _s3SyncTimer.Stop();
        _s3LocalChangeSyncTimer.Stop();
        CancelQuietly(_appSettingsAutoSaveCancellation);
        CancelQuietly(_s3SyncCancellation);
        CancelQuietly(_s3SyncExitCancellation);
        CancelQuietly(_deviceBookScanCancellation);
        CancelQuietly(_calibreDetectionCancellation);
        AppRestartRequest.Request();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    private static void CancelQuietly(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<bool> EnsureBackupEncryptionForUiAsync()
    {
        try
        {
            await _backupService.EnsureEncryptionConfiguredAsync(_lifetimeCancellation.Token);
            return true;
        }
        catch (SyncEncryptionKeyRequiredException exception)
        {
            SettingsBackupStatusText.Text = UiText.Localize(exception.Message);
            await ShowMessageAsync(
                T("请先设置同步密钥"),
                UiText.Localize(exception.Message),
                secondaryActionText: T("去设置"),
                secondaryAction: OpenSyncEncryptionKeySettings);
            return false;
        }
    }

    private void OpenSyncEncryptionKeySettings()
    {
        OpenSettingsExpander("Data", SettingsS3Expander);
        SettingsS3AdvancedExpander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!S3EncryptionKeyBox.IsEffectivelyVisible) return;
            S3EncryptionKeyBox.BringIntoView();
            S3EncryptionKeyBox.Focus();
        }, DispatcherPriority.Loaded);
    }
}
