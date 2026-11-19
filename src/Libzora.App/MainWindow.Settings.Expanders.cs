using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Xml.Linq;

namespace Libzora;

/// <summary>
/// Expanders, AI settings and sync provider controls.
/// </summary>
public partial class MainWindow
{
    private void OpenSettingsExpander(string category, Expander expander)
    {
        if (BlockNavigationWhileTransferring()) return;
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        ShowSettingsSection(category);
        expander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (expander.IsEffectivelyVisible) expander.BringIntoView();
        }, DispatcherPriority.Loaded);
        UpdateS3SettingsActions();
    }

    private async void SettingsExpander_Expanded(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source)) return;
        UpdateS3SettingsActions();
        try
        {
            if (ReferenceEquals(sender, SettingsAiExpander))
                await EnsureMainReaderAiSettingsLoadedAsync();
            else if (ReferenceEquals(sender, SettingsTrashExpander))
                await RefreshTrashItemsAsync();
            else if (ReferenceEquals(sender, SettingsDiagnosticsExpander))
                await RefreshPlatformDiagnosticsAsync();
            else if (ReferenceEquals(sender, SettingsLocalResourcesExpander))
                await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            else if (ReferenceEquals(sender, SettingsMcpExpander))
                RefreshMcpExecutableStatus();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SettingsStatusText.Text = T("读取设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void SettingsExpander_Collapsed(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !ReferenceEquals(sender, e.Source)) return;
        if (FocusManager?.GetFocusedElement() is Control focused
            && (!focused.IsEffectivelyVisible
                || (ReferenceEquals(expander, SettingsS3Expander) && focused.GetVisualAncestors().Contains(SettingsS3ActionBar))))
            expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()?.Focus();
        UpdateS3SettingsActions();
    }

    private async Task EnsureMainReaderAiSettingsLoadedAsync()
    {
        if (_mainReaderAiSettingsLoaded) return;
        if (_mainReaderAiSettingsLoadTask is { IsCompleted: false } pending)
        {
            await pending;
            return;
        }
        _mainReaderAiSettingsLoadTask = LoadMainReaderAiSettingsAsync();
        await _mainReaderAiSettingsLoadTask;
    }

    private void EnsureSyncProviderItems()
    {
        // Provider enum 2 is retired, so an item's index is not its wire value.
        // Populate before controls load their saved provider, not during the
        // later auto-save setup (which would reset a restored iCloud choice).
        var providers = _s3SyncService.AvailableProviders;
        if (SyncProviderBox.Items.OfType<ComboBoxItem>().Select(item => item.Tag)
            .SequenceEqual(providers.Select(provider => (object)provider.Provider))) return;
        SyncProviderBox.Items.Clear();
        foreach (var provider in providers)
            SyncProviderBox.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.Provider });
    }

    private void ConfigureS3SettingsDraftTracking()
    {
        EnsureSyncProviderItems();
        ToggleSwitch[] switches = [S3SyncEnabledCheck, S3AutomaticSyncCheck, S3DownloadBookFilesCheck, S3PathStyleCheck, S3SkipTlsVerifyCheck];
        foreach (var control in switches) control.IsCheckedChanged += (_, _) => S3SettingsFieldChanged();
        TextBox[] fields = [S3EndpointBox, S3AccessKeyBox, S3SecretKeyBox, S3BucketBox, S3RegionBox, S3PrefixBox, S3EncryptionKeyBox,
            WebDavEndpointBox, WebDavUsernameBox, WebDavPasswordBox];
        foreach (var control in fields)
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) S3SettingsFieldChanged();
            };
        NumericUpDown[] numbers = [S3SyncIntervalBox, S3TimeoutBox, S3ConcurrencyBox];
        foreach (var control in numbers) control.ValueChanged += (_, _) => S3SettingsFieldChanged();
        SyncProviderBox.SelectionChanged += (_, _) =>
        {
            UpdateSyncProviderControls();
            S3SettingsFieldChanged();
        };
        UpdateSyncProviderControls();
        UpdateS3SettingsDraftState();
    }

    private SyncProvider SelectedSyncProvider => SyncProviderBox.SelectedItem is ComboBoxItem { Tag: SyncProvider provider }
        ? provider : _s3SyncStoredSettings.Settings.Provider;

    private void UpdateSyncProviderControls()
    {
        var provider = SelectedSyncProvider;
        var s3 = provider == SyncProvider.S3;
        var webDav = provider == SyncProvider.WebDav;
        var iCloud = provider == SyncProvider.ICloud;
        SetProviderVisibility(S3ConnectionPanel, s3);
        SetProviderVisibility(WebDavConnectionPanel, webDav);
        SetProviderVisibility(ICloudConnectionPanel, iCloud && _s3SyncService.AvailableProviders.Any(item => item.Provider == SyncProvider.ICloud));
        SetProviderVisibility(S3RegionRow, s3);
        SetProviderVisibility(S3PathStyleCheck, s3);
        SetProviderVisibility(S3SkipTlsVerifyCheck, !iCloud);
        SetProviderVisibility(S3ConcurrencyRow, !iCloud);
        SetProviderVisibility(SettingsS3ProfileExpander, !iCloud);
        _settingsSearchCache.Clear();
        if (!string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
            SettingsSearchBox_TextChanged(null, new TextChangedEventArgs(TextBox.TextChangedEvent));
        UpdateAccountSyncPresentation();

        static void SetProviderVisibility(Control control, bool visible)
        {
            control.IsVisible = visible;
            // Category visibility and collapsed expanders remain searchable;
            // only options belonging to another provider are excluded.
            control.Classes.Set("syncProviderHidden", !visible);
        }
    }

    private void S3SettingsFieldChanged()
    {
        if (_suppressS3SettingsDraftTracking) return;
        _s3TestConnectionCancellation?.Cancel();
        _s3SettingsEditVersion++;
        S3SyncStatusText.Text = string.Empty;
        UpdateS3SettingsDraftState();
    }

    private void UpdateS3SettingsDraftState()
    {
        _s3SettingsDirty = ReadS3SyncSettingsFromControls() != S3SyncSettings.Normalize(_s3SyncStoredSettings.Settings);
        UpdateS3SettingsActions();
    }

    private void UpdateS3SettingsActions()
    {
        if (SettingsS3ActionBar is null) return;
        SettingsS3ActionBar.IsVisible = _activeSettingsCategory.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && SettingsS3Expander.IsExpanded;
        var saved = _s3SyncStoredSettings.Settings;
        var requiresProviderConfirmation = saved.IsConfigured && !saved.HasChosenCustomProvider;
        var draftStatus = _s3SettingsSaving ? T("正在保存同步设置…")
            : _s3SettingsDirty ? T("有未保存的修改 · 保存后生效")
            : requiresProviderConfirmation ? saved.Provider == SyncProvider.ICloud
                ? T("请确认同步方式并保存后再同步。")
                : T("请确认自备 S3 / WebDAV 连接信息并保存后再同步。")
            : saved.IsConfigured ? T("配置已保存 · 立即同步使用当前保存的配置")
            : T("尚未配置 · 填写连接信息后保存");
        S3DraftStatusText.Text = ReadS3SyncSettingsFromControls().ProviderName + " · " + draftStatus;
        S3SyncNowButton.Classes.Set("quiet", true);
        S3SaveSettingsButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && (_s3SettingsDirty || requiresProviderConfirmation);
        S3DiscardSettingsButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3SettingsDirty;
        S3TestConnectionButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3TestConnectionCancellation is null;
        S3SyncNowButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && !_s3SettingsDirty
            && saved.IsConfigured && saved.HasChosenCustomProvider
            && _s3SyncService.AvailableProviders.Any(provider => provider.Provider == saved.Provider);
    }

    private void S3DiscardSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_s3SyncBusy || _s3SettingsSaving) return;
        PopulateS3SyncControls();
        S3ConnectionProfileStatusText.Text = string.Empty;
        S3SyncStatusText.Text = T("已放弃修改，恢复为上次保存的配置。");
    }
}
