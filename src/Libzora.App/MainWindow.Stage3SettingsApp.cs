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
/// Application settings: language, Calibre detection and autosave.
/// </summary>
public partial class MainWindow
{
    private void ConfigureAppSettingsAutoSave()
    {
        if (_appSettingsAutoSaveConfigured) return;
        _appSettingsAutoSaveConfigured = true;
        ConfigureS3SettingsDraftTracking();
        AiEnabledCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        NetworkEnabledCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoUpdateCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        DevelopmentUpdateCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        PluginDeveloperModeCheck.IsCheckedChanged += (_, _) =>
        {
            InstallPluginFromFolderButton.IsVisible = PluginDeveloperModeCheck.IsChecked == true;
            ScheduleAppSettingsAutoSave();
        };
        PluginBackgroundTasksCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        CollectionsMutuallyExclusiveCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        ReadingMaterialsCollapsedByDefaultCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoGenerateReaderFormatsCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        CompareKindleLibraryCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        SendToKindleWebEnabledCheck.IsCheckedChanged += (_, _) =>
        {
            UpdateLibraryUi();
            ScheduleAppSettingsAutoSave();
        };
        GridGalleryDisplayCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoConnectDeviceCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoBackupCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        DeleteBookAnnotationsCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        ShowSyncStatusIconCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        ShowLibraryPresenceIconCheck.IsCheckedChanged += (_, _) => ScheduleAppSettingsAutoSave();
        PreferredOpenFormatBox.SelectionChanged += (_, _) => ScheduleAppSettingsAutoSave();
        AutoBackupRetentionBox.ValueChanged += (_, _) => ScheduleAppSettingsAutoSave();
        TranslationGoogleProxyBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            ScheduleAppSettingsAutoSave();
        };
        CalibrePathBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            UpdateCalibreDetectionStatus();
            ScheduleAppSettingsAutoSave();
        };
    }

    private void UiLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAppSettingsAutoSave
            || UiLanguageBox is null
            || UiLanguageBox.SelectedItem is not ComboBoxItem { Tag: not null })
            return;

        var language = UiText.NormalizeLanguage(UiLanguageBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString()
            : null);
        if (Application.Current is App app)
            app.ApplyLanguage(language);
        ScheduleAppSettingsAutoSave();
        UpdateLibraryUi();
        ViewModel.RefreshView();
    }

    private async Task DetectCalibreAtStartupAsync(CancellationToken cancellationToken)
    {
        using var setup = new CalibreSetupService(proxyAddress: TranslationGoogleProxyBox.Text);
        var detectedPath = setup.LocateCalibre(_appSettings.CalibrePath);
        if (string.IsNullOrWhiteSpace(detectedPath))
        {
            Environment.SetEnvironmentVariable(
                "LIBZORA_CALIBRE_CONVERT",
                null,
                EnvironmentVariableTarget.Process);
            return;
        }

        Environment.SetEnvironmentVariable(
            "LIBZORA_CALIBRE_CONVERT",
            detectedPath,
            EnvironmentVariableTarget.Process);
        if (string.Equals(_appSettings.CalibrePath, detectedPath, StringComparison.OrdinalIgnoreCase)) return;

        _appSettings = AppSettings.Normalize(_appSettings with { CalibrePath = detectedPath });
        await _appSettingsStore.SaveAsync(_appSettings, cancellationToken);
    }

    private void UpdateCalibreDetectionStatus(bool updateKfxStatusText = true)
    {
        var configuredPath = (CalibrePathBox.Text ?? string.Empty).Trim().Trim('"');
        var configuredFileName = Path.GetFileName(configuredPath);
        var isDetected = !string.IsNullOrWhiteSpace(configuredPath)
            && File.Exists(configuredPath)
            && (configuredFileName.Equals("ebook-convert", StringComparison.OrdinalIgnoreCase)
                || configuredFileName.Equals("ebook-convert.exe", StringComparison.OrdinalIgnoreCase));
        var status = isDetected ? T("已检测到 Calibre") : T("未检测到 Calibre");
        CalibreDetectionStatusDot.Fill = AppAppearanceResources.GetBrush(isDetected ? "SuccessBrush" : "WarningBrush");
        ToolTip.SetTip(CalibreDetectionStatusDot, status);
        AutomationProperties.SetName(CalibreDetectionStatusDot, status);

        InstallCalibreButton.IsEnabled = !_calibreSetupBusy && !isDetected;
        _calibreDetectionCancellation?.Cancel();
        _calibreDetectionCancellation?.Dispose();
        _calibreDetectionCancellation = null;

        if (!isDetected)
        {
            InstallKfxInputButton.IsEnabled = !_calibreSetupBusy;
            ToolTip.SetTip(InstallKfxInputButton, T("安装 Calibre KFX Input 插件"));
            return;
        }

        InstallKfxInputButton.IsEnabled = false;
        ToolTip.SetTip(InstallKfxInputButton, T("正在检查 KFX Input 安装状态"));
        if (_calibreSetupBusy) return;

        var detectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        detectionCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        _calibreDetectionCancellation = detectionCancellation;
        _ = DetectKfxInputInstallationAsync(configuredPath, detectionCancellation, updateKfxStatusText);
    }

    private async Task DetectKfxInputInstallationAsync(
        string calibrePath,
        CancellationTokenSource detectionCancellation,
        bool updateStatusText)
    {
        try
        {
            using var setup = new CalibreSetupService(proxyAddress: TranslationGoogleProxyBox.Text);
            var installed = await setup.IsKfxInputInstalledAsync(calibrePath, detectionCancellation.Token);
            if (!ReferenceEquals(_calibreDetectionCancellation, detectionCancellation)) return;

            InstallKfxInputButton.IsEnabled = !installed;
            ToolTip.SetTip(
                InstallKfxInputButton,
                installed ? T("KFX Input 已安装") : T("未检测到 KFX Input，点击安装"));
            if (updateStatusText)
            {
                CalibreSetupStatusText.Text = installed
                    ? T("已检测到 KFX Input 插件。")
                    : T("未检测到 KFX Input，可点击安装。");
            }
        }
        catch (OperationCanceledException) when (detectionCancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_calibreDetectionCancellation, detectionCancellation)
                && !_lifetimeCancellation.IsCancellationRequested)
            {
                InstallKfxInputButton.IsEnabled = true;
                ToolTip.SetTip(InstallKfxInputButton, T("未能确认 KFX Input 状态，点击可重新安装"));
                if (updateStatusText)
                    CalibreSetupStatusText.Text = T("KFX Input 状态检查超时，可点击安装。");
            }
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_calibreDetectionCancellation, detectionCancellation)) return;
            InstallKfxInputButton.IsEnabled = true;
            ToolTip.SetTip(InstallKfxInputButton, T("未能确认 KFX Input 状态，点击可重新安装"));
            if (updateStatusText)
                CalibreSetupStatusText.Text = T("KFX Input 状态检查失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_calibreDetectionCancellation, detectionCancellation))
            {
                _calibreDetectionCancellation = null;
                detectionCancellation.Dispose();
            }
        }
    }

    private void ScheduleAppSettingsAutoSave(bool showSavedStatus = true)
    {
        if (_suppressAppSettingsAutoSave || !_appSettingsAutoSaveConfigured) return;
        if (ReadAppSettingsFromControls() == _appSettings && _appSettingsSavedVersion == _appSettingsEditVersion) return;
        _appSettingsEditVersion++;
        _appSettingsAutoSaveShowStatus = showSavedStatus;
        CancelAppSettingsDebounce();
        var cancellation = new CancellationTokenSource();
        _appSettingsAutoSaveCancellation = cancellation;
        _ = DebounceAppSettingsAsync(cancellation);
    }

    // Explicit settings saves and ordinary preference auto-saves can show
    // feedback; library view switches opt out because they are navigation.
    private void ShowSettingsSavedStatus() => ShowSettingsCapsule(T("设置已保存"), 1000, success: true);

    // Bottom-center capsule: the single feedback surface for settings actions.
    // Routine saves show for one second with a check glyph; error notices stay
    // a little longer and appear without it. The capsule rises into place and
    // sinks away again; a newer call supersedes any pending hide of an older one.
    private void ShowSettingsCapsule(string message, int visibleMilliseconds, bool success = false)
    {
        var sequence = ++_settingsCapsuleSequence;
        SettingsSavedCapsuleText.Text = UiText.Localize(message);
        SettingsCapsuleCheckIcon.IsVisible = success;
        SettingsSavedCapsule.RenderTransform =
            Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        SettingsSavedCapsule.IsVisible = true;
        SettingsSavedCapsule.Opacity = 1;
        _ = Task.Delay(visibleMilliseconds).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (sequence != _settingsCapsuleSequence) return;
                SettingsSavedCapsule.Opacity = 0;
                SettingsSavedCapsule.RenderTransform =
                    Avalonia.Media.Transformation.TransformOperations.Parse("translateY(12px)");
                _ = Task.Delay(260).ContinueWith(
                    __ => Dispatcher.UIThread.Post(() =>
                    {
                        if (sequence == _settingsCapsuleSequence)
                            SettingsSavedCapsule.IsVisible = false;
                    }));
            }),
            TaskScheduler.Default);
    }

    private async Task RefreshManagedResourcesAsync(CancellationToken cancellationToken = default)
    {
        ManagedFonts.Clear();
        foreach (var font in await _fontLibrary.ListAsync(cancellationToken)) ManagedFonts.Add(font);
        ManagedDictionaries.Clear();
        foreach (var dictionary in await _dictionaryService.ListAsync(cancellationToken)) ManagedDictionaries.Add(dictionary);
    }
}
