using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace Libzora;

/// <summary>
/// TTS popup and model download/delete management.
/// </summary>
public partial class MainWindow
{
    private void OpenReaderTtsPopup(
        Control placementTarget,
        PlacementMode placement)
    {
        if (!_readerTts.CanOpen) return;
        ReaderTtsPopup.PlacementTarget = placementTarget;
        ReaderTtsPopup.Placement = placement;
        ReaderTtsPopup.IsOpen = true;
        UpdateReaderTtsUi();
        _ = RefreshReaderTtsVoicesAsync();
        RefreshReaderTtsModels();
    }

    private async void ReaderTtsButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        LogTtsDebug("header.click.enter", $"active={IsReaderTtsActiveState(_readerTts.State)} floatingRequested={_readerTtsFloatingRequested} resumeAfterNav={_readerTtsResumeAfterNavigation}");
        try
        {
            if (IsReaderTtsActiveState(_readerTts.State)
                || _readerTtsFloatingRequested
                || _readerTtsResumeAfterNavigation)
            {
                // The header button is deliberately a start/stop toggle. The
                // floating wheel remains available so its MENU is the only
                // place where voice and playback settings are configured.
                LogTtsDebug("header.click.stop", "stopping active tts");
                CancelReaderTtsContinuation();
                await _readerTts.StopAsync();
                return;
            }

            LogTtsDebug("header.click.start", "calling StartReaderTtsWithSavedSettingsAsync");
            await StartReaderTtsWithSavedSettingsAsync("启动听书失败：{0}");
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "启动听书失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private void ReaderTtsFloatingMenuButton_Click(
        object? sender,
        RoutedEventArgs e)
        => OpenReaderTtsPopup(ReaderTtsFloatingMenuButton, PlacementMode.Top);

    private async void ReaderTtsRefreshVoicesButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await RefreshReaderTtsVoicesAsync();

    private async void ReaderTtsShowAllVoicesCheck_Click(
        object? sender,
        RoutedEventArgs e)
    {
        _readerTtsSettings.ShowAllVoices = ReaderTtsShowAllVoicesCheck.IsChecked == true;
        await RefreshReaderTtsVoicesAsync();
    }

    private TtsModelStore ReaderTtsModels =>
        _readerTtsModelStore ??= new TtsModelStore(
            _paths,
            proxyAddress: _appSettings.Translation.GoogleProxyAddress);

    private void RefreshReaderTtsModels()
    {
        if (ReaderTtsModelBox is null) return;
        var previous = _readerTtsSelectedModel?.Id ?? _readerTtsSettings.Model;
        _readerTtsModelsRefreshing = true;
        try
        {
            var items = TtsModelCatalog.Packages
                .Select(package =>
                {
                    var installed = ReaderTtsModels.IsInstalled(package);
                    var status = installed ? T("已下载") : T("未下载");
                    return new ComboBoxItem
                    {
                        Content = $"{T(package.DisplayName)} · {package.EstimatedSizeText} · {status}",
                        Tag = package.Id,
                    };
                })
                .ToArray();
            ReaderTtsModelBox.ItemsSource = items;
            var selected = items.FirstOrDefault(item =>
                    string.Equals(
                        item.Tag as string,
                        previous,
                        StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(item =>
                    ReaderTtsModels.IsInstalled(TtsModelCatalog.Find(item.Tag as string)!))
                ?? items.FirstOrDefault();
            ReaderTtsModelBox.SelectedItem = selected;
            _readerTtsSelectedModel = TtsModelCatalog.Find(selected?.Tag as string);
        }
        finally
        {
            _readerTtsModelsRefreshing = false;
        }
        UpdateReaderTtsModelUi();
    }

    private void UpdateReaderTtsModelUi()
    {
        if (ReaderTtsModelPanel is null) return;
        ReaderTtsModelLabelText.Text = T("离线语音包");
        ReaderTtsModelBox.PlaceholderText = T("选择语音包");
        ReaderTtsModelDownloadButton.Content = T("下载");
        ReaderTtsModelDeleteButton.Content = T("删除");
        ReaderTtsModelLicenseButton.Content = T("许可证");
        ReaderTtsModelProgressBar.IsVisible = _readerTtsModelDownloadInProgress;
        ReaderTtsModelBox.IsEnabled = !_readerTtsModelDownloadInProgress;

        var package = _readerTtsSelectedModel;
        if (package is null)
        {
            ReaderTtsModelDetailText.Text = T("未选择语音包。");
            ReaderTtsModelDownloadButton.IsEnabled = false;
            ReaderTtsModelDeleteButton.IsEnabled = false;
            ReaderTtsModelLicenseButton.IsEnabled = false;
            ReaderTtsModelStatusText.Text = string.Empty;
            return;
        }

        var installed = ReaderTtsModels.IsInstalled(package);
        ReaderTtsModelDetailText.Text =
            $"{T(package.Description)} · {T("许可证")}: {package.LicenseName} · {T("下载源：GitHub")}";
        ReaderTtsModelDownloadButton.Content = _readerTtsModelDownloadInProgress
            ? T("取消下载")
            : installed ? T("已下载") : T("下载");
        ReaderTtsModelDownloadButton.IsEnabled =
            _readerTtsModelDownloadInProgress || !installed;
        ReaderTtsModelDeleteButton.IsEnabled =
            installed && !_readerTtsModelDownloadInProgress;
        ReaderTtsModelLicenseButton.IsEnabled = true;

        if (_readerTtsModelDownloadInProgress)
        {
            var progress = _readerTtsModelProgress;
            ReaderTtsModelProgressBar.Value = progress?.Percentage ?? 0;
            ReaderTtsModelStatusText.Text = progress?.Phase switch
            {
                TtsModelDownloadPhase.Preparing => T("正在准备下载…"),
                TtsModelDownloadPhase.Verifying => T("正在校验…"),
                TtsModelDownloadPhase.Extracting => T("正在解压…"),
                TtsModelDownloadPhase.Completed => T("语音包已下载。"),
                _ when progress?.Percentage is { } percentage
                    => T(
                        "正在下载… {0}% · {1} / {2}",
                        percentage.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                        TtsModelCatalog.FormatBytes(progress.BytesReceived),
                        TtsModelCatalog.FormatBytes(progress.TotalBytes ?? package.ArchiveBytes)),
                _ => T("正在下载…"),
            };
            if (!string.IsNullOrWhiteSpace(_readerTtsModelNotice))
                ReaderTtsModelStatusText.Text = _readerTtsModelNotice;
            return;
        }

        ReaderTtsModelStatusText.Text = _readerTtsModelNotice
            ?? (installed
                ? T(
                    "已下载 · 本机占用 {0}",
                    TtsModelCatalog.FormatBytes(ReaderTtsModels.GetInstalledBytes(package)))
                : T("模型体积 {0}", package.EstimatedSizeText));
    }

    private void ReaderTtsModelBox_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_readerTtsModelsRefreshing) return;
        _readerTtsSelectedModel = TtsModelCatalog.Find(
            (ReaderTtsModelBox.SelectedItem as ComboBoxItem)?.Tag as string);
        _readerTtsModelNotice = null;
        UpdateReaderTtsModelUi();
    }

    private async void ReaderTtsModelDownloadButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (_readerTtsModelDownloadInProgress)
        {
            _readerTtsModelDownloadCancellation?.Cancel();
            return;
        }
        var package = _readerTtsSelectedModel;
        if (package is null) return;
        var store = ReaderTtsModels;
        if (store.IsInstalled(package))
        {
            _readerTtsModelNotice = T("该语音包已下载。");
            UpdateReaderTtsModelUi();
            return;
        }

        try
        {
            var confirmed = await ConfirmAsync(
                T("下载离线语音包"),
                T(
                    "将下载 {0}（{1}），下载源为 GitHub。是否继续？",
                    package.DisplayName,
                    package.EstimatedSizeText),
                primaryText: T("下载"),
                cancellationToken: ReaderToken);
            if (!confirmed) return;

            _readerTtsModelDownloadInProgress = true;
            _readerTtsModelNotice = null;
            _readerTtsModelProgress = new TtsModelDownloadProgress(
                TtsModelDownloadPhase.Preparing,
                0,
                package.ArchiveBytes);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
            _readerTtsModelDownloadCancellation?.Dispose();
            _readerTtsModelDownloadCancellation = cancellation;
            UpdateReaderTtsModelUi();

            var progress = new Progress<TtsModelDownloadProgress>(value =>
            {
                _readerTtsModelProgress = value;
                UpdateReaderTtsModelUi();
            });
            try
            {
                await store.DownloadAsync(package, progress, cancellation.Token);
                _readerTtsSettings.Model = package.Id;
                await _ttsSettingsStore.SaveAsync(_readerTtsSettings, ReaderToken);
                _readerTtsModelNotice = T("语音包已下载。");
                RefreshReaderTtsModels();
                await RefreshReaderTtsVoicesAsync();
            }
            catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                _readerTtsModelNotice = T("下载已取消，下次可以继续。");
            }
            catch (Exception exception)
            {
                _readerTtsModelNotice = T(
                    "语音包下载失败：{0}",
                    UiText.Localize(exception.Message));
            }
            finally
            {
                _readerTtsModelDownloadInProgress = false;
                _readerTtsModelProgress = null;
                if (ReferenceEquals(_readerTtsModelDownloadCancellation, cancellation))
                    _readerTtsModelDownloadCancellation = null;
                cancellation.Dispose();
                UpdateReaderTtsModelUi();
            }
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsModelNotice = T(
                "语音包下载失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsModelUi();
        }
    }

    private async void ReaderTtsModelDeleteButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var package = _readerTtsSelectedModel;
        if (package is null || _readerTtsModelDownloadInProgress) return;
        var store = ReaderTtsModels;
        if (!store.IsInstalled(package)) return;

        try
        {
            var confirmed = await ConfirmAsync(
                T("删除离线语音包"),
                T("删除 {0}？之后可以重新下载。", package.DisplayName),
                primaryText: T("确认删除"),
                cancellationToken: ReaderToken);
            if (!confirmed) return;

            if (TtsModelCatalog.TryParseVoiceId(
                    _readerTtsSettings.Voice,
                    out var voicePackageId,
                    out _)
                && voicePackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
            {
                _readerTtsSettings.Voice = TtsOptions.DefaultVoice;
                await _ttsSettingsStore.SaveAsync(_readerTtsSettings, ReaderToken);
            }
            store.Delete(package);
            _readerTtsModelNotice = T("已删除语音包。");
            RefreshReaderTtsModels();
            await RefreshReaderTtsVoicesAsync();
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            _readerTtsModelNotice = T("删除失败：语音包正在使用，请重启应用后重试。");
            UpdateReaderTtsModelUi();
        }
        catch (Exception exception)
        {
            _readerTtsModelNotice = T(
                "删除语音包失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsModelUi();
        }
    }

    private void ReaderTtsModelLicenseButton_Click(object? sender, RoutedEventArgs e)
    {
        var package = _readerTtsSelectedModel;
        if (package is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(package.LicenseUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _readerTtsModelNotice = T(
                "打开许可证页面失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsModelUi();
        }
    }
}
