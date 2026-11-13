using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private string _readerTranslationTargetLanguage = "zh-CN";
    private ReaderTranslationProvider _readerTranslationProvider = ReaderTranslationProvider.Ai;
    private string _readerTranslationSource = string.Empty;
    private int _readerTranslationRequestSequence;
    private CancellationTokenSource? _readerTranslationCancellation;
    private bool _readerTranslationBusy;
    private string? _approvedTranslationDestination;
    private bool _suppressReaderTranslationPopupClosed;

    private async void ReaderSelectionTranslationButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = BookTranslationSettings.Normalize(_appSettings.Translation);
        _readerTranslationProvider = ReaderTranslationProvider.Ai;
        _readerTranslationTargetLanguage = TranslationLanguageCatalog.NormalizeTarget(settings.TargetLanguage);
        await TranslateReaderSelectionAsync(
            _readerTranslationProvider,
            settings.AiRequestsPerMinute,
            settings.GoogleProxyAddress);
    }

    private async Task TranslateReaderSelectionAsync(
        ReaderTranslationProvider provider,
        int aiRequestsPerMinute,
        string googleProxyAddress)
    {
        var translationAi = _readerAiSettings.Clone();
        var source = (_readerPendingSelection ?? string.Empty).Trim();
        if (source.Length == 0) return;
        if (_readerIsPdf)
        {
            var paragraphText = (CurrentReaderHost as IPdfTextHost)?.GetSelectedTextForTranslation();
            source = string.IsNullOrWhiteSpace(paragraphText)
                ? PdfTextSelectionLayout.NormalizeCopy(source)
                : paragraphText;
        }

        // Keep the selection alive while replacing an earlier translation
        // result. The next selection event will replace it naturally.
        HideReaderSelectionPopup();
        _readerTranslationProvider = provider;
        _readerTranslationPluginTitle = null;
        _readerTranslationSource = source;
        ReaderTranslationResultText.Text = string.Empty;
        ReaderTranslationSourceText.Text = source;
        ReaderTranslationProviderText.Text = GetReaderTranslationProviderLabel(provider);
        ReaderTranslationCopyButton.IsEnabled = false;
        ReaderTranslationAnnotateButton.IsEnabled = false;
        ShowReaderTranslationPopup();

        if (provider != ReaderTranslationProvider.Ai)
        {
            ReaderTranslationStatusText.Text = T("网页免费翻译已停用，请配置模型翻译服务或本地模型。");
            return;
        }

        if (!_appSettings.NetworkEnabled && !translationAi.IsLocalEndpoint)
        {
            ReaderTranslationStatusText.Text = T("网络访问已关闭，无法使用翻译。");
            return;
        }

        if (provider == ReaderTranslationProvider.Ai)
        {
            if (!_appSettings.AiEnabled)
            {
                ReaderTranslationStatusText.Text = T("AI 已在应用设置中关闭。");
                return;
            }

            if (!translationAi.IsConfigured)
            {
                ReaderTranslationStatusText.Text = T("请先配置模型服务地址和模型；远程服务还需要 API Key。");
                return;
            }
        }

        if (!translationAi.IsLocalEndpoint && _approvedTranslationDestination != translationAi.Destination)
        {
            var destination = translationAi.Destination;
            HideReaderTranslationPopup(clearSelection: false);
            var approvalSequence = _readerTranslationRequestSequence;
            var approvalToken = ReaderToken;
            var approved = await ConfirmAsync(T("发送文本进行翻译"),
                T("所选正文将发送至 {0} 进行翻译，可能产生服务费用。请勿发送无权提供的个人信息或保密内容。继续吗？", destination),
                T("同意并翻译"), allowDiagnosticAutoConfirm: false, cancelText: T("取消"));
            if (!approved || approvalToken.IsCancellationRequested || approvalSequence != _readerTranslationRequestSequence) return;
            _approvedTranslationDestination = destination;
            ShowReaderTranslationPopup();
        }

        _readerTranslationBusy = true;
        ReaderTranslationStatusText.Text = T("正在翻译…");
        var requestSequence = ++_readerTranslationRequestSequence;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerTranslationCancellation = cancellation;
        try
        {
            var result = await _translationService.TranslateAsync(
                source,
                provider,
                _readerTranslationTargetLanguage,
                provider == ReaderTranslationProvider.Ai ? translationAi : null,
                cancellation.Token,
                aiRequestsPerMinute,
                googleProxyAddress);
            if (requestSequence != _readerTranslationRequestSequence
                || !ReferenceEquals(_readerTranslationCancellation, cancellation))
                return;

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidDataException("翻译返回了空结果，请重试或切换翻译引擎。");
            ReaderTranslationResultText.Text = result;
            ReaderTranslationStatusText.Text = T("翻译已完成");
            ReaderTranslationCopyButton.IsEnabled = result.Length > 0;
            ReaderTranslationAnnotateButton.IsEnabled = !string.IsNullOrWhiteSpace(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (requestSequence != _readerTranslationRequestSequence
                || !ReferenceEquals(_readerTranslationCancellation, cancellation))
                return;
            ReaderTranslationResultText.Text = T("翻译失败：{0}", UiText.Localize(exception.Message));
            ReaderTranslationStatusText.Text = T("请重试或切换翻译引擎。");
            ReaderTranslationCopyButton.IsEnabled = false;
            ReaderTranslationAnnotateButton.IsEnabled = false;
        }
        finally
        {
            if (ReferenceEquals(_readerTranslationCancellation, cancellation))
            {
                _readerTranslationCancellation = null;
                _readerTranslationBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private void ShowReaderTranslationPopup()
    {
        ShowReaderPopupNearSelection(
            ReaderTranslationHostPopup,
            ReaderTranslationPopup,
            fallbackWidth: 420,
            fallbackHeight: 300,
            _readerLastSelectionPopupAnchor,
            _readerLastSelectionPopupBottom);
    }

    private async void ReaderTranslationCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var result = ReaderTranslationResultText.Text?.Trim() ?? string.Empty;
        if (result.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(result);
            ReaderTranslationStatusText.Text = T("译文已复制");
        }
    }

    private async void ReaderTranslationAnnotateButton_Click(object? sender, RoutedEventArgs e)
    {
        var result = ReaderTranslationResultText.Text?.Trim() ?? string.Empty;
        if (result.Length == 0 || string.IsNullOrWhiteSpace(_readerPendingSelection)) return;

        ReaderTranslationAnnotateButton.IsEnabled = false;
        try
        {
            await SaveReaderAnnotationAsync(result);
            // A successful save releases the pending selection. Keep the result
            // open on failure so the user can retry or copy the translation.
            if (_readerPendingSelection is null)
                HideReaderTranslationPopup(clearSelection: true);
        }
        finally
        {
            ReaderTranslationAnnotateButton.IsEnabled = !string.IsNullOrWhiteSpace(_readerPendingSelection);
        }
    }

    private void ReaderTranslationCloseButton_Click(object? sender, RoutedEventArgs e)
        => HideReaderTranslationPopup(clearSelection: true);

    private void ReaderTranslationHostPopup_Closed(object? sender, EventArgs e)
    {
        if (_suppressReaderTranslationPopupClosed) return;
        CancelReaderTranslationRequest();
        if (!string.IsNullOrWhiteSpace(_readerTranslationSource))
            ClearReaderSelectionAfterTranslation();
    }

    private void HideReaderTranslationPopup(bool clearSelection)
    {
        CancelReaderTranslationRequest();
        if (ReaderTranslationHostPopup.IsOpen)
        {
            _suppressReaderTranslationPopupClosed = true;
            try
            {
                ReaderTranslationHostPopup.IsOpen = false;
            }
            finally
            {
                _suppressReaderTranslationPopupClosed = false;
            }
        }

        if (clearSelection)
            ClearReaderSelectionAfterTranslation();
    }

    private void CancelReaderTranslationRequest()
    {
        _readerTranslationRequestSequence++;
        _readerTranslationBusy = false;
        _readerTranslationCancellation?.Cancel();
        _readerTranslationCancellation = null;
    }

    private void ClearReaderSelectionAfterTranslation()
    {
        if (IsLinuxReaderTextFallbackActive())
            ClearLinuxReaderTextFallbackVisualSelection();

        _readerTranslationSource = string.Empty;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _selectedReaderAnnotation = null;
        if (!_readerIsPdf && CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }

    private string GetReaderTranslationProviderLabel(ReaderTranslationProvider provider) => provider switch
    {
        ReaderTranslationProvider.Google => T("已停用的网页翻译"),
        ReaderTranslationProvider.Bing => T("已停用的网页翻译"),
        ReaderTranslationProvider.Ai => T("AI 翻译"),
        _ => T("翻译")
    };

    private void RefreshReaderTranslationLocalizedText()
    {
        if (ReaderTranslationHostPopup is null || !ReaderTranslationHostPopup.IsOpen) return;
        ReaderTranslationProviderText.Text = _readerTranslationPluginTitle
            ?? GetReaderTranslationProviderLabel(_readerTranslationProvider);
        if (_readerTranslationBusy)
            ReaderTranslationStatusText.Text = T("正在翻译…");
    }
}
