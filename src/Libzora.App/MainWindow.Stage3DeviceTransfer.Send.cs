using Libzora.Core;

namespace Libzora;

/// <summary>
/// Sending selected books to the device.
/// </summary>
public partial class MainWindow
{
    private async Task SendSelectedBooksToKindleAsync() =>
        await TrackDeviceOperationAsync(SendSelectedBooksToKindleCoreAsync);

    private async Task SendBooksToKindleAsync(IReadOnlyList<BookCardViewModel> targetCards) =>
        await TrackDeviceOperationAsync(() => SendBooksToKindleWithRetryAsync(targetCards));

    private Task SendSelectedBooksToKindleCoreAsync() =>
        SendBooksToKindleWithRetryAsync(GetSelectedCards().ToArray());

    private async Task SendBooksToKindleWithRetryAsync(IReadOnlyList<BookCardViewModel> initialCards)
    {
        IReadOnlyList<BookCardViewModel> cards = initialCards;
        List<BookCardViewModel>? ownedCards = null;
        try
        {
            while (true)
            {
                var failures = await SendBooksToKindleBatchAsync(cards);
                if (failures.Count == 0 || _lifetimeCancellation.IsCancellationRequested) return;
                var details = string.Join(Environment.NewLine, failures.Select(item => T("《{0}》：{1}", item.Title, item.Reason)));
                if (!await ConfirmAsync(T("部分书籍发送失败"),
                        T("以下 {0} 本书发送失败：", failures.Count) + Environment.NewLine + Environment.NewLine + details,
                        T("重试失败项"), _lifetimeCancellation.Token, allowDiagnosticAutoConfirm: false, cancelText: T("关闭"))) return;

                var retryCards = new List<BookCardViewModel>();
                foreach (var failure in failures)
                {
                    var book = await _library.GetBookAsync(failure.BookId, _lifetimeCancellation.Token);
                    if (book is not null) retryCards.Add(new BookCardViewModel(book, _paths.Data));
                }
                if (ownedCards is not null) foreach (var owned in ownedCards) owned.Dispose();
                ownedCards = retryCards;
                if (retryCards.Count == 0)
                {
                    SetTaskStatus(T("失败的书籍已不在电脑书库，无法重试。"));
                    return;
                }
                if (retryCards.Count < failures.Count)
                    await ShowMessageAsync(T("部分书籍无法重试"), T("有 {0} 本失败的书籍已不在电脑书库。", failures.Count - retryCards.Count));
                cards = retryCards;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            LogSendDiagnostic("SendSelectedBooksToKindleCoreAsync", exception);
            SetTaskStatus(T("发送失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("发送失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (ownedCards is not null) foreach (var owned in ownedCards) owned.Dispose();
        }
    }

    private sealed record DeviceSendFailure(Guid BookId, string Title, string Reason);

    private async Task<IReadOnlyList<DeviceSendFailure>> SendBooksToKindleBatchAsync(IReadOnlyList<BookCardViewModel> cards)
    {
        if (cards.Count == 0)
        {
            SetTaskStatus(T("请先选择至少一本书。"));
            return [];
        }
        if (_kindle is null)
        {
            SetTaskStatus(T("当前启动头未提供设备平台服务。"));
            return [];
        }
        if (_isTransferring)
        {
            ShowSettingsCapsule(T("已有发送任务正在进行中。"), 2400);
            return [];
        }
        if (Interlocked.CompareExchange(ref _transferGate, 1, 0) != 0)
        {
            ShowSettingsCapsule(T("已有发送任务正在进行中。"), 2400);
            return [];
        }

        try
        {
        await RefreshDevicesAsync(scanBooks: false, _lifetimeCancellation.Token);
        if (CurrentDevice is not { } device)
        {
            SetTaskStatus(T("请先连接并解锁设备。"));
            ShowSettingsCapsule(T("未检测到设备，请连接并解锁设备。"), 3200);
            return [];
        }

        var sent = 0;
        var skipped = 0;
        var failures = new List<DeviceSendFailure>();
        var titleLines = string.Join(Environment.NewLine, cards.Take(3).Select(card => T("《{0}》", card.Title)));
        if (cards.Count > 3) titleLines += T("{0}…等 {1} 本", Environment.NewLine, cards.Count);
        if (!await ConfirmAsync(
                T("发送到设备：{0} 本书", cards.Count),
                T("将以下书籍发送到 {0}：{1}{1}{2}", _deviceDisplayName ?? device.Name, Environment.NewLine, titleLines)
                    + DeviceTransferDescription(device)))
            return [];

        var duplicateDecision = await ChooseDeviceDuplicatesAsync(device, cards);
        if (duplicateDecision is null) return [];
        var skippedDuplicates = cards.Count - duplicateDecision.Count;
        cards = duplicateDecision;
        if (cards.Count == 0)
        {
            SetTaskStatus(T("已跳过设备中可能已有的书籍，没有发送。"));
            return [];
        }

        _isTransferring = true;
        _transferStopRequested = false;
        ClearBookMultiSelection();
        SetTransferNavigationEnabled(false);
        _transferCancellation?.Dispose();
        _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var cancellation = _transferCancellation;
        TaskProgressPopupBar.Value = 0;
        ShowTransferToast(T("发送到设备"), T("正在发送 {0} 本书…", cards.Count), progress: 0);
        ShowTaskProgressPopup(recordTask: false);
        var acceptProgressUpdates = true;
        Task<PreparedKindleTransfer>? lookaheadTask = null;
        CancellationTokenSource? lookaheadCancellation = null;

        async Task DisposeLookaheadAsync()
        {
            lookaheadCancellation?.Cancel();
            if (lookaheadTask is not null)
            {
                try
                {
                    var prepared = await lookaheadTask;
                    prepared.Dispose();
                }
                catch
                {
                    // A cancelled or failed pre-conversion has already
                    // cleaned its temporary directory in PrepareKindleTransferAsync.
                }
            }

            lookaheadTask = null;
            if (ReferenceEquals(_transferPrefetchCancellation, lookaheadCancellation))
                _transferPrefetchCancellation = null;
            lookaheadCancellation?.Dispose();
            lookaheadCancellation = null;
        }

        var currentBookIndex = 0;
        var currentBookTitle = cards[0].Title;
        try
        {
            using var progress = new CoalescingUiProgress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                var bookPercent = Math.Clamp(value.Percentage, 0d, 100d);
                var overallPercent = cards.Count > 1
                    ? (currentBookIndex * 100d + bookPercent) / cards.Count
                    : bookPercent;
                var detail = UiText.Localize(value.Message);
                var stagePrefix = cards.Count > 1
                    ? T("正在发送《{0}》（{1}/{2}）…", currentBookTitle, currentBookIndex + 1, cards.Count)
                    : T("正在发送《{0}》…", currentBookTitle);
                var stageMessage = string.IsNullOrWhiteSpace(detail)
                    ? stagePrefix
                    : $"{stagePrefix.TrimEnd('…', '.')} · {detail}";
                TaskProgressPopupBar.Value = overallPercent;
                TaskProgressPopupText.Text = stageMessage;
                ShowTransferToast(T("发送到设备"), stageMessage, progress: overallPercent);
            });
            for (var index = 0; index < cards.Count; index++)
            {
                if (_transferStopRequested) break;
                var card = cards[index];
                currentBookIndex = index;
                currentBookTitle = card.Title;
                ShowTransferToast(
                    T("发送到设备"),
                    T("正在发送《{0}》（{1}/{2}）…", card.Title, index + 1, cards.Count),
                    progress: index * 100d / cards.Count);
                PreparedKindleTransfer? prepared = null;
                try
                {
                    if (lookaheadTask is not null)
                    {
                        var pending = lookaheadTask;
                        var pendingCancellation = lookaheadCancellation;
                        lookaheadTask = null;
                        lookaheadCancellation = null;
                        if (ReferenceEquals(_transferPrefetchCancellation, pendingCancellation))
                            _transferPrefetchCancellation = null;
                        try
                        {
                            prepared = await pending;
                        }
                        finally
                        {
                            pendingCancellation?.Dispose();
                        }
                    }
                    else
                    {
                        prepared = await PrepareKindleTransferAsync(device, card.Book, progress, cancellation.Token);
                    }

                    // Start converting the next book before the current one is
                    // written to the device. Only one Calibre conversion is
                    // prefetched, so conversion stays serialized while it
                    // overlaps the device transfer.
                    if (index + 1 < cards.Count && !_transferStopRequested)
                    {
                        lookaheadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                        _transferPrefetchCancellation = lookaheadCancellation;
                        lookaheadTask = PrepareKindleTransferAsync(
                            device,
                            cards[index + 1].Book,
                            progress: null,
                            lookaheadCancellation.Token);
                    }

                    await EnsureCurrentDeviceAsync(device);
                    await RecordResourceActionAsync(T("发送书籍到设备"), card.Title, token => _kindle.SendBookAsync(
                        device,
                        prepared.File,
                        prepared.SourcePath,
                        CreateRecordedTransferProgress(progress),
                        cancellationToken: token,
                        coverOverridePath: prepared.CoverOverridePath), cancellation.Token);
                    sent++;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    skipped++;
                    failures.Add(new DeviceSendFailure(card.Book.Id, card.Title, UiText.Localize(exception.Message)));
                    ShowTransferToast(
                        T("发送到设备"),
                        T("《{0}》发送失败：{1}", card.Title, UiText.Localize(exception.Message)),
                        progress: index * 100d / cards.Count);
                }
                finally
                {
                    prepared?.Dispose();
                }
            }

            await DisposeLookaheadAsync();
            acceptProgressUpdates = false;
            var completionMessage = _transferStopRequested
                ? T("已完成当前书籍，发送已停止：成功 {0} 本，失败 {1} 本。", sent, skipped)
                : skippedDuplicates > 0
                    ? T("发送完成：成功 {0} 本，跳过可能已有 {1} 本，失败 {2} 本。", sent, skippedDuplicates, skipped)
                : skipped > 0
                    ? T("发送完成：成功 {0} 本，失败 {1} 本。", sent, skipped)
                    : T("已发送 {0} 本书到 {1}。", sent, device.Name);
            ShowTransferToast(T("发送到设备"), completionMessage, progress: 100, autoHide: true, completed: skipped == 0);
            SetTaskStatus(completionMessage);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            acceptProgressUpdates = false;
            SetTaskStatus(T("发送已中断。"));
            ShowTransferToast(T("发送到设备"), T("发送已中断，未完成的临时文件已清理。"), autoHide: true);
        }
        catch (Exception exception)
        {
            acceptProgressUpdates = false;
            LogSendDiagnostic("SendSelectedBooksToKindleCoreAsync", exception);
            SetTaskStatus(T("发送失败：{0}", UiText.Localize(exception.Message)));
            ShowTransferToast(T("发送到设备"), T("发送失败：{0}", UiText.Localize(exception.Message)), autoHide: true);
        }
        finally
        {
            acceptProgressUpdates = false;
            await DisposeLookaheadAsync();
            _isTransferring = false;
            ClearBookMultiSelection();
            SetTransferNavigationEnabled(true);
            _transferStopRequested = false;
            if (ReferenceEquals(_transferCancellation, cancellation)) _transferCancellation = null;
            cancellation.Dispose();
            HideTaskProgressPopup();
            await TryRefreshDeviceBooksAfterTransferAsync(device);
        }
        return failures;
        }
        finally
        {
            Interlocked.Exchange(ref _transferGate, 0);
        }
    }
}
