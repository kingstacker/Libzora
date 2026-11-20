using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// AI provider settings, connection test and model selectors.
/// </summary>
public partial class MainWindow
{
    private void MainReaderAiBaseUrlBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressMainAiProviderChange || _readerAiAvailableModels.Count == 0) return;
        _readerAiAvailableModels = [];
        ApplyReaderAiModelSelectors(GetSelectedMainReaderAiProvider(), MainReaderAiModelBox.Text ?? string.Empty);
    }

    private void ReaderAiBaseUrlBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressAiProviderChange || _readerAiAvailableModels.Count == 0) return;
        _readerAiAvailableModels = [];
        ApplyReaderAiModelSelectors(GetSelectedReaderAiProvider(), ReaderAiModelBox.Text ?? string.Empty);
    }

    private string GetSelectedMainReaderAiProvider() =>
        (MainReaderAiProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepseek";

    private void ReaderAiSettingsOpenButton_Click(object? sender, RoutedEventArgs e)
        => ReaderAiSettingsButton_Click(sender, e);

    private void ReaderAiSettingsCancelButton_Click(object? sender, RoutedEventArgs e) => ShowReaderAiTab();

    private async void ReaderAiFetchModelsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildReaderAiConnectionSettings(
                GetSelectedReaderAiProvider(),
                ReaderAiBaseUrlBox.Text,
                ReaderAiModelBox.Text,
                ReaderAiApiKeyBox.Text,
                requireApiKey: true,
                requireModel: false,
                out var settings,
                out var validationMessage))
        {
            ReaderAiSettingsStatusText.Text = validationMessage;
            return;
        }

        await FetchReaderAiModelsAsync(
            settings,
            ReaderAiSettingsStatusText,
            ReaderAiFetchModelsButton,
            ReaderToken);
    }

    private async void ReaderAiSettingsTestButton_Click(object? sender, RoutedEventArgs e) =>
        await TestReaderAiConnectionAsync(
            GetSelectedReaderAiProvider(),
            ReaderAiBaseUrlBox.Text,
            ReaderAiModelBox.Text,
            ReaderAiApiKeyBox.Text,
            ReaderAiSettingsStatusText,
            ReaderAiSettingsTestButton,
            ReaderToken);

    private async void ReaderAiSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_aiConnectivityTestBusy || _aiModelFetchBusy) return;
        var provider = GetSelectedReaderAiProvider();
        var baseUrl = ReaderAiBaseUrlBox.Text?.Trim() ?? string.Empty;
        var model = ReaderAiModelBox.Text?.Trim() ?? string.Empty;
        var apiKey = ReaderAiApiKeyBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || model.Length == 0)
        {
            ReaderAiSettingsStatusText.Text = T("请填写有效的 HTTP Base URL 和模型名称。");
            return;
        }

        try
        {
            _readerAiSettings = new AiConnectionSettings
            {
                Provider = provider,
                BaseUrl = baseUrl.TrimEnd('/'),
                Model = AiConnectionSettings.NormalizeModel(provider, model),
                ApiKey = apiKey
            };
            await _aiSettingsStore.SaveAsync(_readerAiSettings, ReaderToken);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            ApplyReaderAiSettingsToControls();
            ReaderAiSettingsStatusText.Text = T("AI 设置已保存。");
            ShowReaderAiTab();
            ReaderAiStatusText.Text = _readerAiSettings.IsConfigured
                ? T("AI 已就绪；优先参考书籍，必要时自动联网搜索。")
                : T("设置已保存，但还缺少 API Key。");
        }
        catch (Exception exception)
        {
            ReaderAiSettingsStatusText.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task TestReaderAiConnectionAsync(
        string provider,
        string? baseUrlText,
        string? modelText,
        string? apiKeyText,
        TextBlock statusText,
        Button testButton,
        CancellationToken cancellationToken)
    {
        if (_aiConnectivityTestBusy || _aiModelFetchBusy) return;
        if (!_appSettings.NetworkEnabled)
        {
            statusText.Text = T("网络访问已关闭，无法调用 AI 服务。");
            return;
        }
        if (!TryBuildReaderAiConnectionSettings(
                provider,
                baseUrlText,
                modelText,
                apiKeyText,
                requireApiKey: true,
                requireModel: true,
                out var settings,
                out var validationMessage))
        {
            statusText.Text = validationMessage;
            return;
        }

        _aiConnectivityTestBusy = true;
        testButton.IsEnabled = false;
        statusText.Text = T("正在检测 AI 连通性…");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var answer = await _aiChatClient.TestConnectionAsync(settings, timeout.Token);
            statusText.Text = T(
                "AI 连通性检测成功，回复：{0}",
                LimitAiConnectivityAnswer(answer));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            statusText.Text = T("AI 连通性检测超时，请检查服务地址。");
        }
        catch (Exception exception)
        {
            statusText.Text = T(
                "AI 连通性检测失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            _aiConnectivityTestBusy = false;
            testButton.IsEnabled = true;
        }
    }

    private bool TryBuildReaderAiConnectionSettings(
        string provider,
        string? baseUrlText,
        string? modelText,
        string? apiKeyText,
        bool requireApiKey,
        bool requireModel,
        out AiConnectionSettings settings,
        out string validationMessage)
    {
        settings = new AiConnectionSettings();
        validationMessage = string.Empty;
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var baseUrl = baseUrlText?.Trim() ?? string.Empty;
        var model = modelText?.Trim() ?? string.Empty;
        var apiKey = apiKeyText?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            validationMessage = T("请输入有效的 HTTP 或 HTTPS Base URL。");
            return false;
        }
        if (requireModel && model.Length == 0)
        {
            validationMessage = T("请输入模型名称。");
            return false;
        }
        if (requireApiKey && apiKey.Length == 0 && !endpoint.IsLoopback)
        {
            validationMessage = T("请输入 API Key 后再测试。");
            return false;
        }

        settings = new AiConnectionSettings
        {
            Provider = normalizedProvider,
            BaseUrl = baseUrl.TrimEnd('/'),
            Model = AiConnectionSettings.NormalizeModel(normalizedProvider, model),
            ApiKey = apiKey
        };
        return true;
    }

    private static string LimitAiConnectivityAnswer(string answer)
    {
        var normalized = answer.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160] + "…";
    }

    private void ApplyReaderAiModelSelectors(string provider, string selectedModel)
    {
        var previousReaderModelSuppression = _suppressAiModelChange;
        var previousMainModelSuppression = _suppressMainAiModelChange;
        _suppressAiModelChange = true;
        _suppressMainAiModelChange = true;
        try
        {
            var modelOptions = new[] { selectedModel }
                .Concat(_readerAiAvailableModels)
                .Concat(AiConnectionSettings.GetModelOptions(provider, selectedModel))
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // The editable box and the remote model selector occupy the same
            // field. Showing both at once made the selected model appear twice
            // and allowed the popup to be measured independently from the
            // field that owns it.
            var hasRemoteModels = _readerAiAvailableModels.Count > 0;
            MainReaderAiModelBox.IsVisible = !hasRemoteModels;
            ReaderAiModelBox.IsVisible = !hasRemoteModels;
            MainReaderAiModelSelectorBox.IsVisible = hasRemoteModels;
            ReaderAiSettingsModelSelectorBox.IsVisible = hasRemoteModels;

            PopulateAiModelSelector(ReaderAiSettingsModelSelectorBox, modelOptions, selectedModel);
            PopulateAiModelSelector(MainReaderAiModelSelectorBox, modelOptions, selectedModel);
            PopulateReaderAiModelMenu(modelOptions, selectedModel);
            UpdateReaderAiModelSelectorWidths();
        }
        finally
        {
            _suppressAiModelChange = previousReaderModelSuppression;
            _suppressMainAiModelChange = previousMainModelSuppression;
        }
    }

    private void PopulateReaderAiModelMenu(
        IReadOnlyList<string> models,
        string selectedModel)
    {
        if (ReaderAiModelMenuItem is null) return;

        ReaderAiModelMenuItem.Items.Clear();
        foreach (var model in models)
        {
            var item = new MenuItem
            {
                Header = model,
                Tag = model,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = string.Equals(model, selectedModel, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += ReaderAiModelMenuItem_Click;
            ReaderAiModelMenuItem.Items.Add(item);
        }
    }

    private void PopulateAiModelSelector(
        ComboBox? selector,
        IReadOnlyList<string> models,
        string selectedModel)
    {
        if (selector is null) return;
        var width = CalculateAiModelSelectorWidth(selector, models);
        ApplyAiModelSelectorWidth(selector, width);
        selector.ItemsSource = models
            .Select(model =>
            {
                var item = new ComboBoxItem
                {
                    Content = model,
                    Tag = model,
                    Width = width,
                    MinWidth = width,
                    MaxWidth = width,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                item.Classes.Add("aiModelOption");
                return item;
            })
            .ToArray();
        selector.SelectedItem = selector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                selectedModel,
                StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateReaderAiModelSelectorWidths()
    {
        if (_updatingAiModelSelectorLayout) return;
        _updatingAiModelSelectorLayout = true;
        try
        {
            ApplyCurrentAiModelSelectorWidth(ReaderAiSettingsModelSelectorBox);
            ApplyCurrentAiModelSelectorWidth(MainReaderAiModelSelectorBox);
        }
        finally
        {
            _updatingAiModelSelectorLayout = false;
        }
    }

    private void ApplyCurrentAiModelSelectorWidth(ComboBox? selector)
    {
        if (selector is null) return;
        var models = selector.Items
            .OfType<ComboBoxItem>()
            .Select(item => item.Tag as string)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Cast<string>()
            .ToArray();
        if (models.Length == 0) return;
        var width = CalculateAiModelSelectorWidth(selector, models);
        ApplyAiModelSelectorWidth(selector, width);
        foreach (var item in selector.Items.OfType<ComboBoxItem>())
        {
            item.Width = width;
            item.MinWidth = width;
            item.MaxWidth = width;
        }
    }

    private static void ApplyAiModelSelectorWidth(ComboBox selector, double width)
    {
        selector.Width = width;
        selector.MinWidth = width;
        selector.MaxWidth = width;
        selector.HorizontalAlignment = HorizontalAlignment.Left;
        selector.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private double CalculateAiModelSelectorWidth(
        ComboBox selector,
        IReadOnlyList<string> models)
    {
        var maxModelLength = models.Count == 0
            ? 0
            : models.Max(model => model.Length);
        var fontSize = selector.FontSize;
        if (double.IsNaN(fontSize) || double.IsInfinity(fontSize) || fontSize <= 0)
            fontSize = 13;

        // Model identifiers are predominantly ASCII. This reserves a stable
        // amount of space from the longest identifier, plus the ComboBox
        // chrome and item padding, instead of recalculating from the selected
        // value each time the selection changes.
        var desiredWidth = Math.Ceiling(maxModelLength * fontSize * 0.66 + 46);
        var availableWidth = GetAiModelSelectorAvailableWidth(selector);
        if (availableWidth > 0)
            desiredWidth = Math.Min(desiredWidth, availableWidth);

        return Math.Max(1, desiredWidth);
    }

    private double GetAiModelSelectorAvailableWidth(ComboBox selector)
    {
        var modelBox = ReferenceEquals(selector, MainReaderAiModelSelectorBox)
            ? MainReaderAiModelBox
            : ReaderAiModelBox;
        var fetchButton = ReferenceEquals(selector, MainReaderAiModelSelectorBox)
            ? MainReaderAiFetchModelsButton
            : ReaderAiFetchModelsButton;
        if (modelBox.Parent is Grid fieldGrid && fieldGrid.Bounds.Width > 0)
        {
            var fetchWidth = Math.Max(fetchButton.Bounds.Width, fetchButton.DesiredSize.Width);
            return Math.Max(0, fieldGrid.Bounds.Width - fetchWidth - fieldGrid.ColumnSpacing);
        }
        if (modelBox.Bounds.Width > 0)
            return modelBox.Bounds.Width;
        if (selector.Bounds.Width > 0)
            return selector.Bounds.Width;

        // Hidden settings panes have no layout bounds during startup. This is
        // only a provisional width; the pane/view SizeChanged callbacks apply
        // the real divider-safe width as soon as the surface is shown.
        return 280;
    }

    private void ReaderAiSettingsPane_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateReaderAiModelSelectorWidths();

    private void ReaderAiSettingsView_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateReaderAiModelSelectorWidths();

    private void ReaderAiComposer_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateReaderAiModelSelectorWidths();

    private async Task FetchReaderAiModelsAsync(
        AiConnectionSettings settings,
        TextBlock statusText,
        Button fetchButton,
        CancellationToken cancellationToken)
    {
        if (_aiConnectivityTestBusy || _aiModelFetchBusy) return;
        if (!_appSettings.NetworkEnabled)
        {
            statusText.Text = T("网络访问已关闭，无法调用 AI 服务。");
            return;
        }

        _aiModelFetchBusy = true;
        fetchButton.IsEnabled = false;
        statusText.Text = T("正在获取模型列表…");
        try
        {
            var models = await FetchAiModelsAsync(settings, cancellationToken);
            if (models.Count == 0)
            {
                statusText.Text = T("未获取到可用模型，请手动填写模型名称。");
                return;
            }

            _readerAiAvailableModels = models;
            ApplyReaderAiModelSelectors(settings.Provider, settings.Model);
            statusText.Text = T("已获取 {0} 个模型，请选择。", models.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            statusText.Text = T("模型获取失败：请求超时。");
        }
        catch (Exception exception)
        {
            statusText.Text = T(
                "模型获取失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            _aiModelFetchBusy = false;
            fetchButton.IsEnabled = true;
        }
    }
}
