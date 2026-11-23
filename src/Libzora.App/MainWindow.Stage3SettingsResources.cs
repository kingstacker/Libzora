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
/// Local font and dictionary management in settings.
/// </summary>
public partial class MainWindow
{

    private async void RefreshLocalResourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
        SettingsStatusText.Text = T("本地字体与字典列表已刷新。");
    }

    private async void ImportFontButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入本地字体"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(T("字体")) { Patterns = ["*.ttf", "*.otf", "*.woff", "*.woff2"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            await RecordResourceActionAsync(T("导入本地字体"), Path.GetFileNameWithoutExtension(path), token => _fontLibrary.ImportAsync(path, token), _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            FontManagementStatusText.Text = T("字体已导入。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FontManagementStatusText.Text = T("字体导入失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void RemoveFontButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ManagedFontList.SelectedItem is not ManagedFont font) return;
        try
        {
            await RecordResourceActionAsync(T("删除本地字体"), font.DisplayName, token => _fontLibrary.RemoveAsync(font.Id, token), _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            FontManagementStatusText.Text = T("字体已移除。");
        }
        catch (Exception exception)
        {
            FontManagementStatusText.Text = T("字体移除失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ImportDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入本地字典"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(T("支持的词典"))
                    {
                        Patterns = ["*.mdx", "*.azw", "*.mobi", "*.prc", "*.txt", "*.tsv", "*.csv"]
                    }
                ]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            await ImportLocalDictionaryPathsAsync([path]);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DictionaryResultText.Text = T("字典导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("词典导入失败"), UiText.Localize(exception.Message));
        }
    }

    private async void RemoveDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DictionaryDefinition dictionary }) return;
        try
        {
            await RecordResourceActionAsync(T("删除本地词典"), dictionary.Name, token => _dictionaryService.RemoveAsync(dictionary.Id, token), _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            DictionaryResultText.Text = T("词典已移除。");
        }
        catch (Exception exception)
        {
            DictionaryResultText.Text = T("词典移除失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void DictionaryEnabled_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DictionaryDefinition dictionary } check) return;
        check.IsEnabled = false;
        try
        {
            await _dictionaryService.SetEnabledAsync(dictionary.Id, check.IsChecked == true, _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            check.IsChecked = dictionary.Enabled;
            DictionaryResultText.Text = UiText.Localize(exception.Message);
        }
        finally { check.IsEnabled = true; }
    }

    private async Task LookupSettingsDictionaryAsync()
    {
        SettingsDictionaryEntries.SetEntries([]);
        var term = DictionaryTestBox.Text?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            DictionaryResultText.Text = T("请输入要查询的词条。");
            return;
        }

        var entries = await _dictionaryService.LookupAsync(term, _lifetimeCancellation.Token);
        DictionaryResultText.Text = entries.Count == 0
            ? T("没有找到“{0}”。", term)
            : string.Empty;
        SettingsDictionaryEntries.SetEntries(entries);
        SettingsDictionaryEntries.TermRequested -= SettingsDictionaryTermRequested;
        SettingsDictionaryEntries.TermRequested += SettingsDictionaryTermRequested;
    }

    private async void SettingsDictionaryTermRequested(object? sender, string term)
    {
        DictionaryTestBox.Text = term;
        await LookupSettingsDictionaryAsync();
    }

    private async void DictionaryTestButton_Click(object? sender, RoutedEventArgs e) => await LookupSettingsDictionaryAsync();

    private async void DictionaryTestBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LookupSettingsDictionaryAsync();
    }

    private async void BrowseCalibreButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择 Calibre ebook-convert"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Calibre ebook-convert") { Patterns = ["ebook-convert", "ebook-convert.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) CalibrePathBox.Text = path;
    }

    private async void InstallCalibreButton_Click(object? sender, RoutedEventArgs e) =>
        await RunCalibreSetupAsync(installPlugin: false);

    private async void InstallKfxInputButton_Click(object? sender, RoutedEventArgs e) =>
        await RunCalibreSetupAsync(installPlugin: true);

    private async Task RunCalibreSetupAsync(
        bool installPlugin,
        Action<CalibreSetupProgress>? progressSink = null)
    {
        if (_calibreSetupBusy) return;
        if (NetworkEnabledCheck.IsChecked == false)
        {
            CalibreSetupStatusText.Text = T("请先开启“允许网络功能”。");
            return;
        }

        _calibreSetupBusy = true;
        InstallCalibreButton.IsEnabled = false;
        InstallKfxInputButton.IsEnabled = false;
        CalibreSetupProgressBar.IsVisible = true;
        CalibreSetupProgressBar.IsIndeterminate = true;
        CalibreSetupProgressBar.Value = 0;
        CalibreSetupPercentageText.Text = "—";
        var progress = new Progress<CalibreSetupProgress>(value =>
        {
            CalibreSetupStatusText.Text = UiText.Localize(value.Message);
            CalibreSetupProgressBar.IsIndeterminate = value.Percentage is null;
            if (value.Percentage is { } percentage) CalibreSetupProgressBar.Value = percentage;
            CalibreSetupPercentageText.Text = value.Percentage is { } displayedPercentage
                ? $"{displayedPercentage:0}%" : "—";
            progressSink?.Invoke(value);
        });

        var setupFailed = false;
        try
        {
            using var setup = new CalibreSetupService(proxyAddress: TranslationGoogleProxyBox.Text);
            if (installPlugin)
            {
                var executable = await setup.InstallKfxInputAsync(
                    CalibrePathBox.Text,
                    progress,
                    _lifetimeCancellation.Token);
                CalibrePathBox.Text = executable;
                CalibreSetupStatusText.Text = T("KFX Input 已安装，可以转换无 DRM 的 KFX 文件。");
            }
            else
            {
                var result = await setup.InstallCalibreAsync(progress, _lifetimeCancellation.Token);
                CalibrePathBox.Text = result.ExecutablePath;
                CalibreSetupStatusText.Text = UiText.Localize(result.Message);
                // KFX conversion depends on the Input plugin, so a fresh
                // Calibre install includes it instead of leaving a second
                // manual step. A plugin failure keeps the Calibre result.
                try
                {
                    await setup.InstallKfxInputAsync(
                        result.ExecutablePath,
                        progress,
                        _lifetimeCancellation.Token);
                    CalibreSetupStatusText.Text =
                        UiText.Localize(result.Message) + " · "
                        + T("KFX Input 已安装，可以转换无 DRM 的 KFX 文件。");
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception pluginException)
                {
                    CalibreSetupStatusText.Text =
                        UiText.Localize(result.Message) + " · "
                        + UiText.Localize(pluginException.Message);
                }
            }
            await SaveAppSettingsCoreAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            CalibreSetupStatusText.Text = T("安装已取消。");
        }
        catch (Exception exception)
        {
            setupFailed = true;
            CalibreSetupStatusText.Text = T("安装失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _calibreSetupBusy = false;
            CalibreSetupProgressBar.IsVisible = false;
            CalibreSetupProgressBar.IsIndeterminate = false;
            UpdateCalibreDetectionStatus(updateKfxStatusText: !setupFailed);
        }
    }

    private void OpenDataDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureDirectories();
            Process.Start(new ProcessStartInfo { FileName = _paths.Data, UseShellExecute = true });
        }
        catch (Exception exception) { ShowSettingsCapsule(T("无法打开数据目录：{0}", UiText.Localize(exception.Message)), 4000); }
    }

    private void KindleEmailGuideLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://bookfere.com/post/3.html") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("无法打开链接：{0}", UiText.Localize(exception.Message)), 4000);
        }
    }

    private void UserAgreementButton_Click(object? sender, RoutedEventArgs e) =>
        OpenLegalDocument(LegalDocumentLinks.UserAgreement);

    private void PrivacyPolicyButton_Click(object? sender, RoutedEventArgs e) =>
        OpenLegalDocument(LegalDocumentLinks.PrivacyPolicy);

    private void OpenLegalDocument(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("无法打开链接：{0}", UiText.Localize(exception.Message)), 4000);
        }
    }

    private void AboutDeveloperButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/kingstacker") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(UiText.Localize(exception.Message), 4000);
        }
    }

    private void KindleEmailCalibreGuideLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://bookfere.com/post/11.html") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("无法打开链接：{0}", UiText.Localize(exception.Message)), 4000);
        }
    }
}
