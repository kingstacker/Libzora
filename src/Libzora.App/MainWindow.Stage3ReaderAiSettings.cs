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
/// Reader AI connection settings shown in the settings page.
/// </summary>
public partial class MainWindow
{
    private async Task LoadMainReaderAiSettingsAsync()
    {
        ReaderAiSettingsPane.IsEnabled = false;
        try
        {
            _readerAiSettings = await _aiSettingsStore.LoadAsync(_lifetimeCancellation.Token);
            _suppressMainAiProviderChange = true;
            try
            {
                var provider = _readerAiSettings.Provider.Trim().ToLowerInvariant();
                MainReaderAiProviderBox.SelectedItem = MainReaderAiProviderBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                    ?? MainReaderAiProviderBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
                MainReaderAiBaseUrlBox.Text = _readerAiSettings.BaseUrl;
                MainReaderAiModelBox.Text = _readerAiSettings.Model;
                MainReaderAiApiKeyBox.Text = _readerAiSettings.ApiKey;
                MainReaderAiSettingsStatusText.Text = string.Empty;
                ApplyReaderAiModelSelectors(_readerAiSettings.Provider, _readerAiSettings.Model);
            }
            finally
            {
                _suppressMainAiProviderChange = false;
            }
            _mainReaderAiSettingsLoaded = true;
            if (_readerAiAvailableModels.Count == 0 && _readerAiSettings.IsConfigured)
                _ = ObserveReaderTaskAsync(
                    RefreshReaderAiModelSelectorAsync(_lifetimeCancellation.Token));
            _ = ObserveReaderTaskAsync(
                RefreshReaderEmbeddingModelStatusAsync(_lifetimeCancellation.Token));
        }
        catch (Exception exception)
        {
            MainReaderAiSettingsStatusText.Text = T("读取 AI 设置失败：{0}", UiText.Localize(exception.Message));
        }
        finally { ReaderAiSettingsPane.IsEnabled = true; }
    }

    private void MainReaderAiProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMainAiProviderChange
            || MainReaderAiProviderBox is null
            || MainReaderAiBaseUrlBox is null
            || MainReaderAiModelBox is null
            || MainReaderAiSettingsStatusText is null
            || MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item)
            return;
        var defaults = AiConnectionSettings.GetDefaults(item.Tag.ToString()!);
        MainReaderAiBaseUrlBox.Text = defaults.BaseUrl;
        MainReaderAiModelBox.Text = defaults.Model;
        _readerAiAvailableModels = [];
        ApplyReaderAiModelSelectors(item.Tag.ToString()!, defaults.Model);
        MainReaderAiSettingsStatusText.Text = item.Tag.ToString() == "custom"
            ? T("自定义服务使用 OpenAI-compatible Chat Completions。")
            : string.Empty;
    }

    private async void MainReaderAiSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_aiConnectivityTestBusy || _aiModelFetchBusy) return;
        if (MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item) return;
        var provider = item.Tag.ToString()!;
        var baseUrl = MainReaderAiBaseUrlBox.Text?.Trim() ?? string.Empty;
        var model = MainReaderAiModelBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            MainReaderAiSettingsStatusText.Text = T("请输入有效的 HTTP 或 HTTPS Base URL。");
            return;
        }
        if (model.Length == 0)
        {
            MainReaderAiSettingsStatusText.Text = T("请输入模型名称。");
            return;
        }

        var settings = new AiConnectionSettings
        {
            Provider = provider,
            BaseUrl = baseUrl,
            Model = AiConnectionSettings.NormalizeModel(provider, model),
            ApiKey = (MainReaderAiApiKeyBox.Text ?? string.Empty).Trim()
        };
        try
        {
            MainReaderAiSettingsStatusText.Text = T("正在安全保存…");
            await _aiSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            _readerAiSettings = settings;
            ApplyReaderAiSettingsToControls();
            MainReaderAiSettingsStatusText.Text = T("AI 设置已保存。");
        }
        catch (Exception exception)
        {
            MainReaderAiSettingsStatusText.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void MainReaderAiFetchModelsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item)
            return;

        if (!TryBuildReaderAiConnectionSettings(
                item.Tag.ToString()!,
                MainReaderAiBaseUrlBox.Text,
                MainReaderAiModelBox.Text,
                MainReaderAiApiKeyBox.Text,
                requireApiKey: true,
                requireModel: false,
                out var settings,
                out var validationMessage))
        {
            MainReaderAiSettingsStatusText.Text = validationMessage;
            return;
        }

        await FetchReaderAiModelsAsync(
            settings,
            MainReaderAiSettingsStatusText,
            MainReaderAiFetchModelsButton,
            _lifetimeCancellation.Token);
    }

    private void MainReaderAiModelSelectorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMainAiModelChange
            || MainReaderAiModelSelectorBox is null
            || MainReaderAiModelBox is null
            || MainReaderAiModelSelectorBox.SelectedItem is not ComboBoxItem { Tag: string model })
            return;
        MainReaderAiModelBox.Text = model;
    }

    private async void MainReaderAiSettingsTestButton_Click(object? sender, RoutedEventArgs e)
    {
        if (MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item)
            return;

        await TestReaderAiConnectionAsync(
            item.Tag.ToString()!,
            MainReaderAiBaseUrlBox.Text,
            MainReaderAiModelBox.Text,
            MainReaderAiApiKeyBox.Text,
            MainReaderAiSettingsStatusText,
            MainReaderAiSettingsTestButton,
            _lifetimeCancellation.Token);
    }
}
