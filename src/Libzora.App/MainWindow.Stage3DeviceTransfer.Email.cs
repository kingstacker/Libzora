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
/// Sending books by email.
/// </summary>
public partial class MainWindow
{
    private async Task SendSelectedBooksByEmailAsync()
    {
        var cards = GetSelectedCards();
        if (cards.Count == 0)
        {
            SetTaskStatus(T("请先选择至少一本书。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后再发送 Kindle 邮件。"));
            return;
        }
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        if (!_kindleEmailSettings.IsConfigured)
        {
            await ShowMessageAsync(T("无法发送"), T("请先在设置与备份中填写并保存 Kindle 邮箱设置。"));
            KindleEmailSettingsButton_Click(null, new RoutedEventArgs());
            return;
        }

        var candidates = new List<(BookCardViewModel Card, BookFile File, string Path, long SizeBytes)>();
        foreach (var card in cards)
        {
            var file = KindleEmailSelectionPolicy.SelectPreferred(card.Book.Files);
            if (file is null) continue;

            var path = ViewModel.GetAbsoluteFilePath(file);
            if (!File.Exists(path))
            {
                try
                {
                    path = await EnsureBookFileAvailableAsync(file, _lifetimeCancellation.Token) ?? path;
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SetTaskStatus(T("《{0}》下载失败：{1}", card.Title, UiText.Localize(exception.Message)));
                    continue;
                }
            }

            if (!File.Exists(path)) continue;
            candidates.Add((card, file, path, new FileInfo(path).Length));
        }
        if (candidates.Count == 0)
        {
            SetTaskStatus(T("发送到 Kindle 邮箱只支持 EPUB 或 PDF；所选书籍没有可发送格式。"));
            return;
        }

        var oversized = candidates
            .Where(entry => !KindleEmailSelectionPolicy.IsWithinAttachmentLimit(entry.SizeBytes))
            .ToArray();
        if (oversized.Length > 0)
        {
            var oversizedLines = string.Join(
                Environment.NewLine,
                oversized.Take(4).Select(entry =>
                    T("《{0}》（{1}）", entry.Card.Title, FormatKindleEmailAttachmentSize(entry.SizeBytes))));
            if (oversized.Length > 4)
                oversizedLines += T("{0}…等 {1} 本", Environment.NewLine, oversized.Length);

            SetTaskStatus(T("有 {0} 本书超过 50 MB，无法发送到 Kindle 邮箱。", oversized.Length));
            await ShowMessageAsync(
                T("无法发送到 Kindle 邮箱"),
                T("以下书籍超过 Send to Kindle 邮箱单本 50 MB 的限制，将不会发送：{0}{0}{1}", Environment.NewLine, oversizedLines));
        }

        var pending = candidates
            .Where(entry => KindleEmailSelectionPolicy.IsWithinAttachmentLimit(entry.SizeBytes))
            .ToArray();
        if (pending.Length == 0) return;

        var titleLines = string.Join(Environment.NewLine, pending.Take(3).Select(entry => T("《{0}》", entry.Card.Title)));
        if (pending.Length > 3) titleLines += T("{0}…等 {1} 本", Environment.NewLine, pending.Length);
        if (!await ConfirmAsync(
                T("发送到 Kindle 邮箱"),
                T("确定将以下 {0} 本书发送到 {1}？{2}{2}{3}", pending.Length, _kindleEmailSettings.KindleEmailAddress, Environment.NewLine, titleLines)))
            return;

        var sent = 0;
        var skipped = cards.Count - pending.Length;
        var transferTitle = T("发送到 Kindle 邮箱");
        ShowTransferToast(transferTitle, T("正在发送邮件…"), progress: 0);
        for (var index = 0; index < pending.Length; index++)
        {
            var (card, _, sourcePath, _) = pending[index];
            var progress = index * 100d / pending.Length;
            try
            {
                var progressMessage = T("正在发送《{0}》（{1}/{2}）…", card.Title, index + 1, pending.Length);
                SetTaskStatus(progressMessage);
                ShowTransferToast(transferTitle, progressMessage, progress: progress);
                await _kindleEmailSender.SendAsync(
                    _kindleEmailSettings,
                    sourcePath,
                    $"Libzora：{card.Title}",
                    _lifetimeCancellation.Token);
                sent++;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                SetTaskStatus(T("邮件发送已取消"));
                ShowTransferToast(transferTitle, T("邮件发送已取消"), autoHide: true);
                ClearBookMultiSelection();
                return;
            }
            catch (Exception exception)
            {
                skipped++;
                var failure = UiText.Localize(KindleEmailSender.DescribeFailure(exception));
                var failureMessage = T("《{0}》邮件发送失败：{1}", card.Title, failure);
                SetTaskStatus(failureMessage);
                ShowTransferToast(transferTitle, failureMessage, progress: (index + 1) * 100d / pending.Length);
            }
        }

        var emailCompletionMessage = skipped > 0
            ? T("已通过邮件发送 {0} 本书，跳过或失败 {1} 本。", sent, skipped)
            : T("已通过邮件发送 {0} 本书。", sent);
        SetTaskStatus(emailCompletionMessage);
        ShowTransferToast(transferTitle, emailCompletionMessage, progress: 100, autoHide: true, completed: true);
        ClearBookMultiSelection();
    }

    private async void SendSelectedBookToKindleButton_Click(object? sender, RoutedEventArgs e) =>
        await TrackDeviceOperationAsync(SendSelectedBookToKindleCoreAsync);

    private void LogSendDiagnostic(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            File.AppendAllText(
                Path.Combine(_paths.Logs, "send-diagnostic.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }
    }

    private Task SendSelectedBookToKindleCoreAsync() =>
        SendSelectedBookToKindleCoreAsync(requestedSource: null);

    private async Task SendSelectedBookToKindleCoreAsync(BookFile? requestedSource)
    {
        // Snapshot the card: awaits below (device refresh, confirm dialog) can
        // re-enter UI handlers that clear _selectedCard, which previously
        // crashed the send with a NullReferenceException.
        if (_selectedCard is not { } card)
        {
            SetTaskStatus(T("请先选择一本书。"));
            return;
        }
        if (_kindle is null)
        {
            SetTaskStatus(T("当前启动头未提供设备平台服务。"));
            return;
        }
        if (_isTransferring)
        {
            ShowSettingsCapsule(T("已有发送任务正在进行中。"), 2400);
            return;
        }
        if (Interlocked.CompareExchange(ref _transferGate, 1, 0) != 0)
        {
            ShowSettingsCapsule(T("已有发送任务正在进行中。"), 2400);
            return;
        }

        try
        {
        await RefreshDevicesAsync(scanBooks: false, _lifetimeCancellation.Token);
        if (CurrentDevice is not { } device)
        {
            SetTaskStatus(T("请先连接并解锁设备。"));
            ShowSettingsCapsule(T("未检测到设备，请连接并解锁设备。"), 3200);
            return;
        }

        var transferStarted = false;
        try
        {
            if (!await ConfirmAsync(T("发送到设备"), T("将《{0}》发送到 {1}？", card.Title, _deviceDisplayName ?? device.Name)
                + DeviceTransferDescription(device)))
                return;
            var duplicateDecision = await ChooseDeviceDuplicatesAsync(device, [card]);
            if (duplicateDecision is null) return;
            if (duplicateDecision.Count == 0)
            {
                SetTaskStatus(T("已跳过设备中可能已有的书籍，没有发送。"));
                return;
            }
            transferStarted = true;
            _isTransferring = true;
            _transferStopRequested = false;
            SetTransferNavigationEnabled(false);
            _transferCancellation?.Dispose();
            _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            var cancellation = _transferCancellation;
            TaskProgressPopupBar.Value = 0;
            ShowTransferToast(T("发送到设备"), T("正在发送《{0}》…", card.Title), progress: 0);
            ShowTaskProgressPopup(recordTask: false);
            var acceptProgressUpdates = true;
            try
            {
                using var progress = new CoalescingUiProgress<TransferProgress>(value =>
                {
                    if (!acceptProgressUpdates) return;
                    TaskProgressPopupBar.Value = value.Percentage;
                    TaskProgressPopupText.Text = UiText.Localize(value.Message);
                    ShowTransferToast(T("发送到设备"), UiText.Localize(value.Message), progress: value.Percentage);
                });
                using var prepared = await PrepareKindleTransferAsync(
                    device,
                    card.Book,
                    progress,
                    cancellation.Token,
                    requestedSource);
                await EnsureCurrentDeviceAsync(device);
                await RecordResourceActionAsync(T("发送书籍到设备"), card.Title, token => _kindle.SendBookAsync(
                    device,
                    prepared.File,
                    prepared.SourcePath,
                    CreateRecordedTransferProgress(progress),
                    cancellationToken: token,
                    coverOverridePath: prepared.CoverOverridePath), cancellation.Token);
                var completionMessage = _transferStopRequested
                    ? T("已完成当前书籍，发送已停止。")
                    : T("已发送《{0}》到 {1}。", card.Title, device.Name);
                acceptProgressUpdates = false;
                ShowTransferToast(T("发送到设备"), completionMessage, progress: 100, autoHide: true, completed: true);
                SetTaskStatus(completionMessage);
            }
            finally
            {
                acceptProgressUpdates = false;
                if (ReferenceEquals(_transferCancellation, cancellation)) _transferCancellation = null;
                cancellation.Dispose();
                _isTransferring = false;
                ClearBookMultiSelection();
                SetTransferNavigationEnabled(true);
                _transferStopRequested = false;
                HideTaskProgressPopup();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogSendDiagnostic("SendSelectedBookToKindleCoreAsync", exception);
            SetTaskStatus(T("发送到设备失败：{0}", UiText.Localize(exception.Message)));
            ShowTransferToast(T("发送到设备"), T("发送到设备失败：{0}", UiText.Localize(exception.Message)), autoHide: true);
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            // A transfer can install the book and still fail afterwards (for
            // example while writing the shelf thumbnail). Refresh from a
            // finally so the device shelf never keeps a stale pre-send view.
            if (transferStarted) await TryRefreshDeviceBooksAfterTransferAsync(device);
        }
        }
        finally
        {
            Interlocked.Exchange(ref _transferGate, 0);
        }
    }

    private async void SendSelectedBookByEmailButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBookByEmailCoreAsync();

    private async Task SendSelectedBookByEmailCoreAsync(BookFile? requestedSource = null)
    {
        if (_selectedCard is not { } card)
        {
            SetTaskStatus(T("请先选择一本书。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请在应用设置中允许网络功能后再发送 Kindle 邮件。"));
            return;
        }
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(_lifetimeCancellation.Token);
        if (!_kindleEmailSettings.IsConfigured)
        {
            await ShowMessageAsync(T("无法发送"), T("请先在设置与备份中填写并保存 Kindle 邮箱设置。"));
            KindleEmailSettingsButton_Click(null, new RoutedEventArgs());
            return;
        }

        var file = requestedSource is null
            ? null
            : card.Book.Files.FirstOrDefault(candidate =>
                candidate.Id == requestedSource.Id
                && KindleEmailSelectionPolicy.IsSupportedFormat(candidate.Format));
        file ??= KindleEmailSelectionPolicy.SelectPreferred(card.Book.Files);
        if (file is null)
        {
            await ShowMessageAsync(T("无法发送"), T("发送到 Kindle 邮箱目前只支持 EPUB 或 PDF 文件。"));
            return;
        }
        var sourcePath = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(sourcePath))
        {
            try
            {
                sourcePath = await EnsureBookFileAvailableAsync(file, _lifetimeCancellation.Token) ?? sourcePath;
            }
            catch (Exception exception)
            {
                SetTaskStatus(T("下载书籍失败：{0}", UiText.Localize(exception.Message)));
                return;
            }
            if (!File.Exists(sourcePath))
            {
                SetTaskStatus(T("找不到文件：{0}", file.RelativePath));
                return;
            }
        }

        try
        {
            if (!await EnsureKindleEmailAttachmentWithinLimitAsync(card.Title, sourcePath))
                return;
            if (!await ConfirmAsync(T("发送到 Kindle 邮箱"), T("确定将《{0}》发送到 {1}？", card.Title, _kindleEmailSettings.KindleEmailAddress)))
                return;
            var transferTitle = T("发送到 Kindle 邮箱");
            var progressMessage = T("正在通过邮件发送《{0}》…", card.Title);
            SetTaskStatus(progressMessage);
            ShowTransferToast(transferTitle, progressMessage, isIndeterminate: true);
            await _kindleEmailSender.SendAsync(
                _kindleEmailSettings,
                sourcePath,
                $"Libzora：{card.Title}",
                _lifetimeCancellation.Token);
            var completionMessage = T("已通过邮件发送《{0}》。", card.Title);
            SetTaskStatus(completionMessage);
            ShowTransferToast(transferTitle, T("邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。"), progress: 100, autoHide: true, completed: true);
            ClearBookMultiSelection();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            SetTaskStatus(T("邮件发送已取消"));
            ShowTransferToast(T("发送到 Kindle 邮箱"), T("邮件发送已取消"), autoHide: true);
            ClearBookMultiSelection();
        }
        catch (Exception exception)
        {
            LogSendDiagnostic("SendSelectedBookByEmailCoreAsync", exception);
            var failure = UiText.Localize(KindleEmailSender.DescribeFailure(exception));
            SetTaskStatus(T("邮件发送失败：{0}", failure));
            ShowTransferToast(T("发送到 Kindle 邮箱"), T("邮件发送失败：{0}", failure), autoHide: true);
            ClearBookMultiSelection();
        }
    }

    private static string FormatKindleEmailAttachmentSize(long fileSizeBytes) =>
        $"{fileSizeBytes / (1024d * 1024d):0.#} MB";

    private async Task<bool> EnsureKindleEmailAttachmentWithinLimitAsync(string title, string filePath)
    {
        var fileSizeBytes = new FileInfo(filePath).Length;
        if (KindleEmailSelectionPolicy.IsWithinAttachmentLimit(fileSizeBytes)) return true;

        var message = T("《{0}》文件大小为 {1}，超过 Send to Kindle 邮箱单本 50 MB 的限制。", title, FormatKindleEmailAttachmentSize(fileSizeBytes));
        SetTaskStatus(message);
        await ShowMessageAsync(T("无法发送到 Kindle 邮箱"), message);
        return false;
    }
}
