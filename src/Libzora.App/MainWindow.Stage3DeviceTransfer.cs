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
/// Sending library books to a Kindle device, by USB or email.
/// </summary>
public partial class MainWindow
{
    private async Task ApplyDeviceModelAsync(string? model)
    {
        if (CurrentDevice is not { } device) return;
        var normalized = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        try
        {
            if (normalized is null)
            {
                await _deviceModelStore.DeleteModelAsync(device.Identity, _lifetimeCancellation.Token);
                // A global default is only needed until the user has a chance
                // to identify this device. Choosing the device's own name from
                // the picker should remove that onboarding fallback as well.
                if (_appSettings.DefaultDeviceModel is not null)
                {
                    _appSettings = AppSettings.Normalize(_appSettings with { DefaultDeviceModel = null });
                    await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
                }
            }
            else
                await _deviceModelStore.SetModelAsync(device.Identity, normalized, _lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法保存设备型号：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法保存设备型号"), UiText.Localize(exception.Message));
            return;
        }

        _deviceDisplayName = normalized ?? device.Name;
        KindleStatusText.Text = _deviceDisplayName;
        KindleConnectionText.Text = T("已连接");
        KindleConnectionText.IsVisible = false;
        DevicePageDeviceText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
        DeviceNameButton.IsEnabled = true;
        if (DeviceResourcePage.IsVisible)
            DeviceResourceStatusText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
    }

    private void DeviceNameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is not null) ShowDeviceModelPicker();
    }

    private async void SendSelectedBooksToKindleBatchButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleAsync();

    private async void SendSelectedBooksByEmailBatchButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBooksByEmailAsync();

    private sealed class PreparedKindleTransfer : IDisposable
    {
        private readonly string? _temporaryDirectory;

        public PreparedKindleTransfer(BookFile file, string sourcePath, string? temporaryDirectory = null, string? coverOverridePath = null)
        {
            File = file;
            SourcePath = sourcePath;
            CoverOverridePath = coverOverridePath;
            _temporaryDirectory = temporaryDirectory;
        }

        public BookFile File { get; }
        public string SourcePath { get; }

        // Library cover to prefer over the file's embedded one when building
        // the Kindle home-screen thumbnail (e.g. an online-matched cover).
        public string? CoverOverridePath { get; }

        public void Dispose()
        {
            if (_temporaryDirectory is null) return;
            try
            {
                if (System.IO.File.Exists(SourcePath)) System.IO.File.Delete(SourcePath);
                if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // CoverPath is stored relative to the data root; older entries may be
    // absolute. Returns null when the book has no readable local cover.
    private string? ResolveBookCoverAbsolutePath(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return null;
        try
        {
            var path = Path.IsPathRooted(book.CoverPath)
                ? book.CoverPath
                : Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<PreparedKindleTransfer> PrepareKindleTransferAsync(
        KindleDevice device,
        Book book,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        BookFile? requestedSource = null)
    {
        var sourceCandidates = DeviceTransferPolicy.GetCandidates(device.Profile, book.Files);
        var sourceFile = requestedSource is not null
            ? sourceCandidates.FirstOrDefault(file => file.Id == requestedSource.Id)
            : sourceCandidates.FirstOrDefault();
        if (sourceFile is null)
            throw new NotSupportedException(T("当前设备没有可发送的书籍格式，支持：{0}。", string.Join("、", device.Profile.BookFormats).ToUpperInvariant()));
        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
            sourcePath = await EnsureBookFileAvailableAsync(sourceFile, cancellationToken)
                ?? sourcePath;
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(T("找不到本地书籍文件，请先刷新书库。"), sourcePath);
        var coverOverridePath = ResolveBookCoverAbsolutePath(book);

        var requiresMetadataRepair = device.Profile.UsesKindleThumbnails
            && KindleTransferPolicy.RequiresLegacyMetadataRepair(sourceFile, sourcePath);
        if (!DeviceTransferPolicy.RequiresKindleConversion(device.Profile, sourceFile) && !requiresMetadataRepair)
            return new PreparedKindleTransfer(sourceFile, sourcePath, coverOverridePath: coverOverridePath);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "kindle-ready", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var destinationPath = Path.Combine(
            temporaryDirectory,
            KindleTransferPolicy.CreateSafeFileName(book.Title, ".azw3"));
        try
        {
            var conversionProgress = progress is null
                ? null
                : new Progress<FormatConversionProgress>(value => progress.Report(new TransferProgress(
                    value.RoundedPercentage,
                    100,
                    T("正在生成 Kindle 兼容版本 · {0}%", value.RoundedPercentage))));
            try
            {
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    destinationPath,
                    conversionProgress,
                    cancellationToken,
                    new FormatConversionMetadata(book.Title, book.Authors, coverOverridePath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Some hand-built EPUBs trip a Calibre bug in mobi_output's
                // remove_html_cover (KeyError on a manifest path). Round-tripping
                // through a normalized EPUB repairs the manifest and the second
                // pass then converts cleanly.
                var normalized = Path.Combine(
                    temporaryDirectory,
                    KindleTransferPolicy.CreateSafeFileName(book.Title, ".epub"));
                var metadata = new FormatConversionMetadata(book.Title, book.Authors, coverOverridePath);
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    normalized,
                    conversionProgress,
                    cancellationToken,
                    metadata);
                await _formatConverter.ConvertAsync(
                    normalized,
                    destinationPath,
                    conversionProgress,
                    cancellationToken,
                    metadata);
            }

            var output = new FileInfo(destinationPath);
            if (!output.Exists || output.Length < 1024)
                throw new InvalidDataException(T("Kindle 兼容版本生成失败：输出文件为空。"));

            // A missing cover is not fatal: the Kindle falls back to its
            // default thumbnail, so send anyway instead of blocking the book.

            var preparedFile = new BookFile
            {
                Id = sourceFile.Id,
                BookId = sourceFile.BookId,
                Format = "azw3",
                RelativePath = Path.GetFileName(destinationPath),
                Size = output.Length,
                Sha256 = await Hashing.Sha256Async(destinationPath, cancellationToken)
            };
            return new PreparedKindleTransfer(preparedFile, destinationPath, temporaryDirectory, coverOverridePath);
        }
        catch
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task SendSelectedBooksToKindleAsync() =>
        await TrackDeviceOperationAsync(SendSelectedBooksToKindleCoreAsync);

    private async Task SendSelectedBooksToKindleCoreAsync()
    {
        IReadOnlyList<BookCardViewModel> cards = GetSelectedCards().ToArray();
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

        try
        {
            using var progress = new CoalescingUiProgress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                TaskProgressPopupBar.Value = value.Percentage;
                TaskProgressPopupText.Text = UiText.Localize(value.Message);
                ShowTransferToast(T("发送到设备"), UiText.Localize(value.Message), progress: value.Percentage);
            });
            for (var index = 0; index < cards.Count; index++)
            {
                if (_transferStopRequested) break;
                var card = cards[index];
                ShowTransferToast(
                    T("发送到设备"),
                    T("正在发送《{0}》（{1}/{2}）…", card.Title, index + 1, cards.Count),
                    progress: index * 100 / cards.Count);
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
