using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>The AI connection, stored in the same protected file as the desktop app's.</summary>
public partial class AiSettingsPage : UserControl, IShellPage
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private string _provider = "deepseek";
    private IReadOnlyList<string> _fetchedModels = [];

    /// <summary>Design-time only.</summary>
    public AiSettingsPage() => InitializeComponent();

    public AiSettingsPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        Loaded += async (_, _) =>
        {
            try { Present(await _modules.Ai.Settings.LoadAsync(_lifetime.Token)); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { ShowStatus("读取设置失败：" + UiText.Localize(exception.Message)); }
        };
    }

    private void Present(AiConnectionSettings settings)
    {
        _provider = settings.Provider;
        PresentProvider();
        BaseUrlBox.Text = settings.BaseUrl;
        ModelBox.Text = settings.Model;
        ApiKeyBox.Text = settings.ApiKey;
    }

    private void PresentProvider()
    {
        ProviderDeepSeek.Classes.Set("on", _provider == "deepseek");
        ProviderOpenAi.Classes.Set("on", _provider == "openai");
        ProviderCustom.Classes.Set("on", _provider == "custom");
    }

    private AiConnectionSettings ReadSettings() => new()
    {
        Provider = _provider,
        BaseUrl = BaseUrlBox.Text?.Trim() ?? string.Empty,
        Model = ModelBox.Text?.Trim() ?? string.Empty,
        ApiKey = ApiKeyBox.Text?.Trim() ?? string.Empty
    };

    private void OnProviderClick(object? sender, RoutedEventArgs e)
    {
        _provider = sender switch
        {
            Button { Name: nameof(ProviderOpenAi) } => "openai",
            Button { Name: nameof(ProviderCustom) } => "custom",
            _ => "deepseek"
        };
        var defaults = AiConnectionSettings.GetDefaults(_provider);
        BaseUrlBox.Text = defaults.BaseUrl;
        ModelBox.Text = defaults.Model;
        _fetchedModels = [];
        ChooseModelButton.IsVisible = false;
        PresentProvider();
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        FetchModelsButton.IsEnabled = !busy;
        ChooseModelButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
    }

    private async void OnFetchModelsClick(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            ShowStatus("请输入有效的接口地址");
            return;
        }
        SetBusy(true);
        ShowStatus("正在获取模型列表…");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var models = await _modules!.Ai.ChatClient.ListModelsAsync(settings, timeout.Token);
            _fetchedModels = models;
            ChooseModelButton.IsVisible = models.Count > 0;
            if (models.Count == 0)
            {
                ShowStatus("未获取到可用模型，请手动填写模型名称。");
                return;
            }
            if (string.IsNullOrWhiteSpace(ModelBox.Text))
                ModelBox.Text = models[0];
            ShowStatus($"已获取 {models.Count} 个模型，请选择。");
            OpenModelPicker();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            ShowStatus("模型获取失败：请求超时。");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("模型获取失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnChooseModelClick(object? sender, RoutedEventArgs e) => OpenModelPicker();

    private void OpenModelPicker()
    {
        if (_shell is null || _fetchedModels.Count == 0) return;
        var current = ModelBox.Text?.Trim() ?? string.Empty;
        var items = _fetchedModels.Select(model => new ChoiceItem(
            model,
            Selected: string.Equals(model, current, StringComparison.OrdinalIgnoreCase),
            Tag: model)).ToArray();
        _shell.ShowSheet("选择模型", new ChoiceSheet(items, item =>
        {
            if (item.Tag is string selected)
                ModelBox.Text = selected;
            _shell.HideSheet();
        }));
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ShowStatus("正在测试连接…");
        try
        {
            await _modules!.Ai.ChatClient.TestConnectionAsync(ReadSettings(), _lifetime.Token);
            ShowStatus("连接成功");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("连接失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
        {
            ShowStatus("请输入有效的接口地址");
            return;
        }
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) { ShowStatus("正在恢复数据，请稍后重试。"); return; }
        SetBusy(true);
        try
        {
            await _modules!.Ai.Settings.SaveAsync(settings, _lifetime.Token);
            ShowStatus(null);
            _shell?.ShowToast("已保存");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("保存失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();

    /// <summary>Pushed pages only detach this one; work ends when it leaves history.</summary>
    public void OnClosed() => _lifetime.Cancel();
}
