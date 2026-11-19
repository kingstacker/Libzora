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
/// Embedding model selection, status and download.
/// </summary>
public partial class MainWindow
{
    private static IReadOnlyList<EmbeddingModelPackage> ReaderEmbeddingModelPackages =>
        EmbeddingModelPackage.Supported;

    private enum ReaderAiRequestKind
    {
        BookQuestion,
        ChapterSummary,
        SelectionExplain,
        BookSummary
    }

    private bool _aiConnectivityTestBusy;
    private bool _aiModelFetchBusy;
    private bool _updatingAiModelSelectorLayout;
    private bool _updatingEmbeddingModelSelectors;
    private bool _readerAiScrollToEndQueued;
    private bool _readerAiFollowOutput = true;
    private const double ReaderAiPanelDefaultWidth = 380d;
    private const double ReaderAiPanelMinimumWidth = 280d;
    private double _readerAiPanelWidth = ReaderAiPanelDefaultWidth;
    private const double ReaderPdfThumbnailsPanelMinimumWidth = 224d;
    private double _readerPdfThumbnailsPanelWidth = ReaderPdfThumbnailsPanelMinimumWidth;
    private bool _readerAiPanelResizing;
    private double _readerAiPanelResizeStartX;
    private double _readerAiPanelResizeStartWidth;
    private readonly DispatcherTimer _readerAiThinkingAnimationTimer =
        new() { Interval = TimeSpan.FromMilliseconds(60) };
    private ReaderAiMessageViewModel? _readerAiThinkingMessage;
    private bool _readerAiVectorIndicatorGreen;
    private string _readerAiVectorIndicatorTooltip = "正在检查本地语义模型…";
    private string _readerAiRetrievalTooltip = string.Empty;
    private int _readerEmbeddingStatusVersion;

    private IEnumerable<ComboBox> GetEmbeddingModelSelectors()
    {
        yield return MainReaderEmbeddingModelSelectorBox;
        yield return ReaderEmbeddingModelSelectorBox;
    }

    private void InitializeReaderEmbeddingModelSelectors()
    {
        var options = ReaderEmbeddingModelPackages
            .Select(package => $"{package.DisplayName} · {package.EstimatedSizeText}")
            .ToArray();
        var selected = EmbeddingModelPackage.Find(_appSettings.EmbeddingModelId)
            ?? EmbeddingModelPackage.Default;
        var selectedIndex = ReaderEmbeddingModelPackages
            .ToList()
            .FindIndex(package => package.ModelId.Equals(
                selected.ModelId,
                StringComparison.OrdinalIgnoreCase));

        _updatingEmbeddingModelSelectors = true;
        try
        {
            foreach (var selector in GetEmbeddingModelSelectors())
            {
                selector.ItemsSource = options;
                selector.SelectedIndex = selectedIndex < 0 ? 0 : selectedIndex;
            }
        }
        finally
        {
            _updatingEmbeddingModelSelectors = false;
        }

        UpdateEmbeddingModelLicenseUi(selected);
    }

    private void ApplyReaderEmbeddingModelSelection()
    {
        var selected = _embeddingService.SelectModel(_appSettings.EmbeddingModelId);
        var selectedIndex = ReaderEmbeddingModelPackages
            .ToList()
            .FindIndex(package => package.ModelId.Equals(
                selected.ModelId,
                StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0) selectedIndex = 0;

        _updatingEmbeddingModelSelectors = true;
        try
        {
            foreach (var selector in GetEmbeddingModelSelectors())
                selector.SelectedIndex = selectedIndex;
        }
        finally
        {
            _updatingEmbeddingModelSelectors = false;
        }

        UpdateEmbeddingModelLicenseUi(selected);
    }

    private void UpdateEmbeddingModelLicenseUi(EmbeddingModelPackage package)
    {
        var licenseLabel = $"{package.LicenseName} 许可证";
        var attribution = $"{package.Attribution} · {package.LicenseName} 许可 · 按需下载自 Hugging Face";
        MainReaderEmbeddingModelLicenseButton.Content = licenseLabel;
        ReaderEmbeddingModelLicenseButton.Content = licenseLabel;
        MainReaderEmbeddingModelLicenseText.Text = attribution;
        ReaderEmbeddingModelLicenseText.Text = attribution;
    }

    private void EmbeddingModelLicenseButton_Click(object? sender, RoutedEventArgs e)
    {
        var selector = ReferenceEquals(sender, MainReaderEmbeddingModelLicenseButton)
            ? MainReaderEmbeddingModelSelectorBox
            : ReaderEmbeddingModelSelectorBox;
        var package = selector.SelectedIndex >= 0
            && selector.SelectedIndex < ReaderEmbeddingModelPackages.Count
                ? ReaderEmbeddingModelPackages[selector.SelectedIndex]
                : EmbeddingModelPackage.Default;
        if (package.LicenseUri is not { } licenseUri) return;

        try
        {
            Process.Start(new ProcessStartInfo(licenseUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetEmbeddingModelStatus(T("打开许可证页面失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private async Task HandleReaderEmbeddingModelSelectionChangedAsync(object? sender)
    {
        if (_updatingEmbeddingModelSelectors
            || sender is not ComboBox selector
            || selector.SelectedIndex < 0
            || selector.SelectedIndex >= ReaderEmbeddingModelPackages.Count)
            return;

        if (_embeddingModelDownloadBusy || _readerAiBusy)
        {
            ApplyReaderEmbeddingModelSelection();
            return;
        }

        var package = ReaderEmbeddingModelPackages[selector.SelectedIndex];
        _embeddingService.SelectModel(package.ModelId);
        _readerAiRetrievalTooltip = string.Empty;
        SetReaderAiVectorIndicator(false, "正在检查本地语义模型…");
        _appSettings = AppSettings.Normalize(_appSettings with
        {
            EmbeddingModelId = package.ModelId
        });
        ApplyReaderEmbeddingModelSelection();

        try
        {
            await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            SetEmbeddingModelStatus(T("已选择 {0}。", package.DisplayName));
            await RefreshReaderEmbeddingModelStatusAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetEmbeddingModelStatus(T("模型选择保存失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private async void MainReaderEmbeddingModelSelectorBox_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e) =>
        await HandleReaderEmbeddingModelSelectionChangedAsync(sender);

    private async void ReaderEmbeddingModelSelectorBox_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e) =>
        await HandleReaderEmbeddingModelSelectionChangedAsync(sender);

    private void SetReaderAiVectorIndicator(bool available, string tooltipSource)
    {
        _readerAiVectorIndicatorGreen = available;
        _readerAiVectorIndicatorTooltip = tooltipSource;
        ApplyReaderAiVectorIndicator();
    }

    // The dot describes model readiness; an overview/selection request can skip
    // retrieval without making an installed model unavailable.
    private void SetReaderAiRetrievalStatus(string tooltipSource)
    {
        _readerAiRetrievalTooltip = tooltipSource;
        ApplyReaderAiVectorIndicator();
    }

    private void ApplyReaderAiVectorIndicator()
    {
        if (ReaderAiVectorStatusDot is null) return;

        ReaderAiVectorStatusDot.Fill = new SolidColorBrush(Color.Parse(
            _readerAiVectorIndicatorGreen ? "#2E8B57" : "#D6A100"));
        var tooltip = T(_readerAiVectorIndicatorTooltip)
            + (string.IsNullOrEmpty(_readerAiRetrievalTooltip) ? string.Empty : "\n" + T(_readerAiRetrievalTooltip))
            + "\n" + T("点击设置向量模型");
        ToolTip.SetTip(ReaderAiVectorStatusButton, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(ReaderAiVectorStatusButton, tooltip);
    }

    private void UpdateReaderAiLanguageText()
    {
        ApplyReaderAiVectorIndicator();
        if (ReaderAiStatusText is null || _readerAiBusy) return;
        ReaderAiStatusText.Text = _appSettings.AiEnabled
            ? _readerAiSettings.IsConfigured
                ? T("AI 已就绪；优先参考书籍，必要时自动联网搜索。")
                : T("AI 尚未配置，请打开设置填写 API Key。")
            : T("AI 已在应用设置中关闭。");
    }

    private async Task InitializeReaderAiAsync(CancellationToken cancellationToken)
    {
        try
        {
            _readerAiSettings = await _aiSettingsStore.LoadAsync(cancellationToken);
            ApplyReaderAiSettingsToControls();
            _ = RefreshReaderAiModelSelectorAsync(cancellationToken);
            _ = ObserveReaderTaskAsync(RefreshReaderEmbeddingModelStatusAsync(cancellationToken));
            UpdateReaderAiLanguageText();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderAiStatusText.Text = T("读取 AI 设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void ApplyReaderAiSettingsToControls()
    {
        _readerAiReasoningDepth = _appSettings.ReaderAiReasoningDepth;
        ApplyReaderEmbeddingModelSelection();
        _suppressAiProviderChange = true;
        _suppressAiModelChange = true;
        _suppressMainAiModelChange = true;
        _suppressAiReasoningDepthChange = true;
        try
        {
            SelectReaderAiProvider(_readerAiSettings.Provider);
            ReaderAiBaseUrlBox.Text = _readerAiSettings.BaseUrl;
            ReaderAiModelBox.Text = _readerAiSettings.Model;
            ReaderAiApiKeyBox.Text = _readerAiSettings.ApiKey;
            ApplyReaderAiModelSelectors(_readerAiSettings.Provider, _readerAiSettings.Model);

            UpdateReaderAiReasoningDepthSelector();
        }
        finally
        {
            _suppressAiProviderChange = false;
            _suppressAiModelChange = false;
            _suppressMainAiModelChange = false;
            _suppressAiReasoningDepthChange = false;
        }
    }

    private IEnumerable<(TextBlock Status, ProgressBar Progress, Button Download, Button Cancel)>
        GetEmbeddingModelControls()
    {
        yield return (
            MainReaderEmbeddingModelStatusText,
            MainReaderEmbeddingModelProgressBar,
            MainReaderEmbeddingModelDownloadButton,
            MainReaderEmbeddingModelCancelButton);
        yield return (
            ReaderEmbeddingModelStatusText,
            ReaderEmbeddingModelProgressBar,
            ReaderEmbeddingModelDownloadButton,
            ReaderEmbeddingModelCancelButton);
    }

    private async Task RefreshReaderEmbeddingModelStatusAsync(CancellationToken cancellationToken)
    {
        if (_embeddingModelDownloadBusy) return;

        var package = _embeddingService.SelectedPackage;
        var statusVersion = ++_readerEmbeddingStatusVersion;
        bool IsCurrent() => statusVersion == _readerEmbeddingStatusVersion
            && package.ModelId == _embeddingService.SelectedPackage.ModelId;
        bool installed;
        try
        {
            installed = _embeddingModelDownloader.IsInstalled(package);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[RAG] Failed to inspect embedding model files: {exception.Message}");
            ApplyReaderEmbeddingModelStatus(installed: false, available: false);
            return;
        }

        if (!installed)
        {
            ApplyReaderEmbeddingModelStatus(installed: false, available: false);
            return;
        }

        try
        {
            if (_embeddingService is not IEmbeddingAvailability availabilityService)
            {
                ApplyReaderEmbeddingModelStatus(installed: true, available: false);
                return;
            }
            var availability = await availabilityService.CheckAvailabilityAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsCurrent()) return;
            ApplyReaderEmbeddingModelStatus(installed: true, available: availability.IsAvailable);
            if (!availability.IsAvailable)
                Debug.WriteLine($"[RAG] Local embedding model is unavailable: {availability.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[RAG] Failed to check embedding model: {exception.Message}");
            if (!IsCurrent()) return;
            ApplyReaderEmbeddingModelStatus(installed: true, available: false);
        }
    }

    private void ApplyReaderEmbeddingModelStatus(bool installed, bool available)
    {
        if (_embeddingModelDownloadBusy) return;

        var package = _embeddingService.SelectedPackage;

        SetReaderAiVectorIndicator(
            available,
            available
                ? "本地语义模型已就绪，普通书籍问答会使用向量检索。"
                : "本地语义模型未启用，当前将使用关键词检索。");

        var status = available
            ? T("已安装，可用于语义检索。")
            : installed
                ? T("模型文件已存在，但加载失败，可重新下载。")
                : _appSettings.NetworkEnabled
                    ? T(
                        "未安装，可下载 {0} 的本地模型。",
                        package.EstimatedSizeText)
                    : T("未安装；请先开启网络访问后下载。");
        var downloadLabel = installed ? T("重新下载") : T("下载模型");

        foreach (var controls in GetEmbeddingModelControls())
        {
            controls.Status.Text = status;
            controls.Progress.IsVisible = false;
            controls.Progress.IsIndeterminate = false;
            controls.Progress.Value = 0;
            controls.Cancel.IsVisible = false;
            controls.Cancel.IsEnabled = false;
            controls.Download.Content = downloadLabel;
            controls.Download.IsVisible = !available;
            controls.Download.IsEnabled = !available && _appSettings.NetworkEnabled;
        }
    }

    private void SetEmbeddingModelDownloadBusy(bool busy)
    {
        foreach (var selector in GetEmbeddingModelSelectors())
            selector.IsEnabled = !busy && !_readerAiBusy;

        foreach (var controls in GetEmbeddingModelControls())
        {
            controls.Download.IsVisible = !busy;
            controls.Download.IsEnabled = !busy && _appSettings.NetworkEnabled;
            controls.Cancel.IsVisible = busy;
            controls.Cancel.IsEnabled = busy;
            controls.Progress.IsVisible = busy;
            controls.Progress.IsIndeterminate = busy;
            if (!busy) controls.Progress.Value = 0;
        }
    }

    private void UpdateEmbeddingModelDownloadProgress(EmbeddingModelDownloadProgress progress)
    {
        var percentage = progress.OverallPercentage ?? progress.FilePercentage;
        var progressText = percentage is { } percentageValue
            ? T("正在下载 {0} · {1:0}%", progress.FileName, percentageValue)
            : T("正在下载 {0} · {1}", progress.FileName, FormatEmbeddingBytes(progress.BytesReceived));
        if (progress.FileTotalBytes is > 0)
        {
            progressText += T(
                "（{0}/{1}）",
                FormatEmbeddingBytes(progress.BytesReceived),
                FormatEmbeddingBytes(progress.FileTotalBytes.Value));
        }

        foreach (var controls in GetEmbeddingModelControls())
        {
            controls.Progress.IsVisible = true;
            controls.Progress.IsIndeterminate = percentage is null;
            if (percentage is { } progressValue)
                controls.Progress.Value = progressValue;
            controls.Status.Text = progressText;
        }
    }

    private async Task HandleEmbeddingModelDownloadRequestAsync()
    {
        if (_embeddingModelDownloadBusy) return;
        var downloadTask = DownloadReaderEmbeddingModelAsync();
        _embeddingModelDownloadTask = downloadTask;
        try
        {
            await downloadTask;
        }
        finally
        {
            if (ReferenceEquals(_embeddingModelDownloadTask, downloadTask))
                _embeddingModelDownloadTask = null;
        }
    }

    private async Task DownloadReaderEmbeddingModelAsync()
    {
        if (!_appSettings.NetworkEnabled)
        {
            SetEmbeddingModelStatus(T("请先在应用设置中开启网络访问，再下载本地语义模型。"));
            return;
        }

        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _embeddingModelDownloadCancellation = downloadCancellation;
        _embeddingModelDownloadBusy = true;
        var package = _embeddingService.SelectedPackage;
        SetEmbeddingModelDownloadBusy(true);
        SetReaderAiVectorIndicator(false, "正在准备本地向量模型，当前未使用向量检索。");
        SetEmbeddingModelStatus(T(
            "准备下载 {0}…",
            package.DisplayName));

        string? terminalMessage = null;
        var cancelled = false;
        try
        {
            var progress = new Progress<EmbeddingModelDownloadProgress>(
                UpdateEmbeddingModelDownloadProgress);
            await _embeddingModelDownloader.DownloadAsync(
                package,
                force: true,
                progress: progress,
                cancellationToken: downloadCancellation.Token);
            if (downloadCancellation.IsCancellationRequested)
            {
                cancelled = true;
                return;
            }

            // A previous availability check may have cached a load error for
            // an incomplete package. Allow the freshly downloaded files to be
            // loaded without restarting the application.
            _embeddingService.ResetLoadFailure();
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            terminalMessage = T("模型下载失败：{0}", UiText.Localize(exception.Message));
            Debug.WriteLine($"[RAG] Embedding model download failed: {exception}");
        }
        finally
        {
            if (ReferenceEquals(_embeddingModelDownloadCancellation, downloadCancellation))
                _embeddingModelDownloadCancellation = null;
            _embeddingModelDownloadBusy = false;
            SetEmbeddingModelDownloadBusy(false);

            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                await RefreshReaderEmbeddingModelStatusAsync(CancellationToken.None);
                if (cancelled)
                    SetEmbeddingModelStatus(T("模型下载已取消。"));
                else if (terminalMessage is not null)
                    SetEmbeddingModelStatus(terminalMessage);
            }
        }
    }

    private void SetEmbeddingModelStatus(string text)
    {
        foreach (var controls in GetEmbeddingModelControls())
            controls.Status.Text = text;
    }

    private static string FormatEmbeddingBytes(long bytes)
    {
        const long Kilobyte = 1024;
        const long Megabyte = Kilobyte * 1024;
        return bytes >= Megabyte
            ? $"{bytes / (double)Megabyte:0.0} MB"
            : bytes >= Kilobyte
                ? $"{bytes / (double)Kilobyte:0} KB"
                : $"{bytes} B";
    }

    private async void MainReaderEmbeddingModelDownloadButton_Click(
        object? sender,
        RoutedEventArgs e) => await HandleEmbeddingModelDownloadRequestAsync();

    private void MainReaderEmbeddingModelCancelButton_Click(object? sender, RoutedEventArgs e) =>
        _embeddingModelDownloadCancellation?.Cancel();

    private async void ReaderEmbeddingModelDownloadButton_Click(
        object? sender,
        RoutedEventArgs e) => await HandleEmbeddingModelDownloadRequestAsync();

    private void ReaderEmbeddingModelCancelButton_Click(object? sender, RoutedEventArgs e) =>
        _embeddingModelDownloadCancellation?.Cancel();

    private void UpdateReaderAiReasoningDepthSelector()
    {
        var options = _readerAiSettings.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            ? new[]
            {
                ("auto", T("自动")),
                ("high", T("深入")),
                ("max", T("极致"))
            }
            : new[]
            {
                ("auto", T("自动")),
                ("low", T("快速")),
                ("medium", T("平衡")),
                ("high", T("深入"))
            };
        var selectedDepth = options.Any(option => option.Item1.Equals(
                _readerAiReasoningDepth,
                StringComparison.OrdinalIgnoreCase))
            ? _readerAiReasoningDepth
            : "auto";
        _readerAiReasoningDepth = selectedDepth;
        PopulateReaderAiReasoningMenu(options, selectedDepth);
    }
}
