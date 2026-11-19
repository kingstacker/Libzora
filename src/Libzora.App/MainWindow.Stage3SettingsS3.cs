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
}
