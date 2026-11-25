using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Unified USB bidirectional sync between the computer library and a connected
/// Kindle device: books, bookmarks, highlights, and notes.
/// </summary>
public partial class MainWindow
{
    private bool _kindleFullSyncBusy;
    private TaskCompletionSource<KindleAnnotationBackupEntry?>? _kindleBackupPickerCompletion;
    private IReadOnlyList<KindleAnnotationBackupEntry> _kindleBackupPickerEntries = [];

    private Task<KindleAnnotationBackupEntry?> PickKindleBackupAsync(IReadOnlyList<KindleAnnotationBackupEntry> backups)
    {
        if (_kindleBackupPickerCompletion is not null || KindleBackupPickerOverlay is null)
            return Task.FromResult<KindleAnnotationBackupEntry?>(null);

        _kindleBackupPickerEntries = backups;
        KindleBackupPickerTitleText.Text = T("选择要回退的 Kindle 备份");
        KindleBackupPickerSubtitleText.Text = T(
            "最多保留最近 {0} 份自动备份。回退前会先自动备份 Kindle 当前数据库，请保持 Kindle 解锁且不要操作设备。",
            KindleAnnotationBackupStore.MaxRetainedBackups);
        KindleBackupPickerConfirmButton.Content = T("确认回退");
        KindleBackupPickerList.ItemsSource = backups.Select((item, index) =>
        {
            var reason = item.Reason == "pre-restore" ? T("回退前自动备份") : T("同步写入前自动备份");
            var hash = string.IsNullOrWhiteSpace(item.Sha256)
                ? T("旧版备份")
                : item.Sha256[..Math.Min(12, item.Sha256.Length)];
            var sizeKb = Math.Max(1, (long)Math.Round(item.FileSizeBytes / 1024d));
            var tag = index == 0 ? T(" [最新]") : string.Empty;
            return $"{item.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}{tag} · {reason}\n{sizeKb:N0} KB · SHA-256: {hash}";
        }).ToArray();
        KindleBackupPickerList.SelectedIndex = backups.Count > 0 ? 0 : -1;
        KindleBackupPickerOverlay.IsVisible = true;

        _kindleBackupPickerCompletion = new TaskCompletionSource<KindleAnnotationBackupEntry?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _kindleBackupPickerCompletion.Task;
    }

    private void KindleBackupPickerCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (KindleBackupPickerOverlay is not null) KindleBackupPickerOverlay.IsVisible = false;
        var completion = _kindleBackupPickerCompletion;
        _kindleBackupPickerCompletion = null;
        _kindleBackupPickerEntries = [];
        completion?.TrySetResult(null);
    }

    private void KindleBackupPickerConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        var index = KindleBackupPickerList?.SelectedIndex ?? -1;
        var selected = index >= 0 && index < _kindleBackupPickerEntries.Count
            ? _kindleBackupPickerEntries[index]
            : null;
        if (KindleBackupPickerOverlay is not null) KindleBackupPickerOverlay.IsVisible = false;
        var completion = _kindleBackupPickerCompletion;
        _kindleBackupPickerCompletion = null;
        _kindleBackupPickerEntries = [];
        completion?.TrySetResult(selected);
    }

    private void ShowKindleSyncBlockingOverlay(string title, string stage, double? percent = null, string? hint = null)
    {
        if (KindleSyncProgressOverlay is null) return;
        KindleSyncProgressTitleText.Text = title;
        KindleSyncProgressStageText.Text = stage;
        KindleSyncProgressHintText.Text = hint ?? T("同步期间已锁定操作，请保持 Kindle 解锁且不要操作设备或断开数据线。");
        if (percent is { } value)
        {
            var clamped = Math.Clamp(value, 0d, 100d);
            KindleSyncProgressBar.IsIndeterminate = false;
            KindleSyncProgressBar.Value = clamped;
            KindleSyncProgressPercentText.Text = $"{Math.Round(clamped):0}%";
        }
        else
        {
            KindleSyncProgressBar.IsIndeterminate = true;
            KindleSyncProgressBar.Value = 0;
            KindleSyncProgressPercentText.Text = string.Empty;
        }
        KindleSyncProgressOverlay.IsVisible = true;
        SetTransferNavigationEnabled(false);
    }

    private void UpdateKindleSyncBlockingOverlay(string stage, double? percent = null)
    {
        if (KindleSyncProgressOverlay is null || !KindleSyncProgressOverlay.IsVisible) return;
        KindleSyncProgressStageText.Text = stage;
        if (percent is { } value)
        {
            var clamped = Math.Clamp(value, 0d, 100d);
            KindleSyncProgressBar.IsIndeterminate = false;
            KindleSyncProgressBar.Value = clamped;
            KindleSyncProgressPercentText.Text = $"{Math.Round(clamped):0}%";
        }
        else
        {
            KindleSyncProgressBar.IsIndeterminate = true;
            KindleSyncProgressPercentText.Text = string.Empty;
        }
    }

    private void HideKindleSyncBlockingOverlay()
    {
        if (KindleSyncProgressOverlay is not null)
        {
            KindleSyncProgressOverlay.IsVisible = false;
            KindleSyncProgressBar.IsIndeterminate = false;
            KindleSyncProgressBar.Value = 0;
            KindleSyncProgressPercentText.Text = string.Empty;
        }
        if (!_isTransferring)
            SetTransferNavigationEnabled(true);
    }

    private void UpdateKindleFullSyncButtonState()
    {
        if (SyncKindleFullButton is null) return;
        var isKindle = CurrentDevice is { Profile.Family: ReaderDeviceFamily.Kindle } && _kindle is not null;
        var canOperate = isKindle
            && !_kindleFullSyncBusy
            && !_kindleAnnotationSyncPromptBusy
            && !_isTransferring
            && _deviceBooksLoaded
            && !_deviceBooksShowingCache
            && !_deviceBookScanFailed
            && !HasActiveDeviceOperations;
        SyncKindleFullButton.IsVisible = isKindle;
        SyncKindleFullButton.IsEnabled = canOperate;
        if (RestoreKindleBackupButton is not null)
        {
            RestoreKindleBackupButton.IsVisible = isKindle;
            RestoreKindleBackupButton.IsEnabled = canOperate;
        }
    }

    private async void RestoreKindleBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is not { Profile.Family: ReaderDeviceFamily.Kindle } device || _kindle is null) return;
        await RunKindleBackupRestoreAsync(device);
    }

    private async Task RunKindleBackupRestoreAsync(KindleDevice device)
    {
        if (_kindle is null
            || _kindleFullSyncBusy
            || _kindleAnnotationSyncPromptBusy
            || _isTransferring
            || !IsCurrentDevice(device))
            return;

        var backups = KindleAnnotationBackupStore.ListBackups(_paths, device);
        if (backups.Count == 0)
        {
            var emptyMessage = T("当前 Kindle 还没有可用的自动备份。每次同步写入 Kindle 前都会自动创建本机和设备双备份。");
            DevicePageStatusText.Text = emptyMessage;
            await ShowMessageAsync(T("回退 Kindle 批注备份"), emptyMessage);
            return;
        }

        var target = await PickKindleBackupAsync(backups);
        if (target is null || !IsCurrentDevice(device)) return;

        _kindleFullSyncBusy = true;
        UpdateKindleFullSyncButtonState();
        UpdateKindleAnnotationSyncButtonVisibility();
        try
        {
            ShowKindleSyncBlockingOverlay(
                T("正在回退 Kindle 批注备份"),
                T("正在校验备份并备份当前 Kindle 数据库…"),
                hint: T("回退期间已锁定操作，请保持 Kindle 解锁且不要操作设备或断开数据线。"));
            KindleAnnotationRestoreResult? result = null;
            await TrackDeviceOperationAsync(async () =>
                result = await RunKindleAnnotationTaskAsync(
                    T("回退 Kindle 批注备份"),
                    cancellable: false,
                    (progress, _) => Task.Run(() => _kindle.RestoreAnnotationDatabaseAsync(
                        device, target, _lifetimeCancellation.Token, progress),
                        _lifetimeCancellation.Token),
                    _lifetimeCancellation.Token));
            HideKindleSyncBlockingOverlay();
            if (result is null || _lifetimeCancellation.IsCancellationRequested) return;

            await RefreshAfterKindleAnnotationSyncAsync(device);
            var message = T("已成功回退 Kindle 批注与书签数据库（SHA-256：{0}）。", result.RestoredSha256[..12]);
            if (!string.IsNullOrWhiteSpace(result.PreRestoreLocalBackupPath))
                message += Environment.NewLine + T("回退前备份：{0}", result.PreRestoreLocalBackupPath);
            if (device.Transport != KindleTransport.MassStorage)
                message += Environment.NewLine + T("如果 Kindle 上暂未立即生效，请断开连接后重启 Kindle 再查看。");
            DevicePageStatusText.Text = message.ReplaceLineEndings(" · ");
            await ShowMessageAsync(T("回退完成"), message);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HideKindleSyncBlockingOverlay();
            var message = T("回退 Kindle 批注备份失败：{0}", UiText.Localize(exception.Message));
            DevicePageStatusText.Text = message;
            await ShowMessageAsync(T("回退失败"), message);
        }
        finally
        {
            HideKindleSyncBlockingOverlay();
            _kindleFullSyncBusy = false;
            UpdateKindleFullSyncButtonState();
            UpdateKindleAnnotationSyncButtonVisibility();
        }
    }

    private async void SyncKindleFullButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is not { Profile.Family: ReaderDeviceFamily.Kindle } device || _kindle is null) return;
        await RunKindleFullSyncAsync(device);
    }

    private async Task RunKindleFullSyncAsync(KindleDevice device)
    {
        if (_kindle is null
            || _kindleFullSyncBusy
            || _kindleAnnotationSyncPromptBusy
            || _isTransferring
            || !IsCurrentDevice(device))
            return;
        if (!_deviceBooksLoaded || _deviceBooksShowingCache || _deviceBookScanFailed || HasActiveDeviceOperations)
        {
            DevicePageStatusText.Text = T("请先完成 Kindle 书籍扫描，并等待其他设备任务结束后再执行双向同步。");
            return;
        }

        _kindleFullSyncBusy = true;
        UpdateKindleFullSyncButtonState();
        UpdateKindleAnnotationSyncButtonVisibility();
        try
        {
            DevicePageStatusText.Text = T("正在分析电脑书库与 Kindle 的书籍、书签、划线与批注差异…");
            ShowKindleSyncBlockingOverlay(
                T("正在检查 Kindle 双向同步差异"),
                T("正在比对书籍、书签、划线与批注…"),
                hint: T("正在读取设备数据，请稍候，期间请保持 Kindle 解锁。"));
            var localBooks = await _library.SearchAsync(cancellationToken: _lifetimeCancellation.Token);
            var deviceBooks = DeviceBooks.Select(card => card.Book).ToArray();
            var bookPlan = KindleBookSyncPlanner.Plan(localBooks, deviceBooks, device.Profile);

            KindleAnnotationSyncPreview? annotationPreview = null;
            var annotationSyncSupported = true;
            try
            {
                await TrackDeviceOperationAsync(async () =>
                    annotationPreview = await RunKindleAnnotationTaskAsync(
                        T("检查 Kindle 书签与批注差异"),
                        cancellable: true,
                        (progress, token) => Task.Run(() => _kindleAnnotationSync.AnalyzeAsync(
                            device, deviceBooks, _kindle, token, progress), token),
                        _lifetimeCancellation.Token));
            }
            catch (NotSupportedException)
            {
                annotationSyncSupported = false;
            }
            catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
            {
                HideKindleSyncBlockingOverlay();
                DevicePageStatusText.Text = T("已取消双向同步检查。");
                return;
            }
            HideKindleSyncBlockingOverlay();

            if (!IsCurrentDevice(device)) return;

            var hasAnnotationChanges = annotationPreview?.HasChanges ?? false;
            if (!bookPlan.HasBookChanges && !hasAnnotationChanges)
            {
                var upToDate = annotationSyncSupported
                    ? T("电脑书库与 Kindle 已同步，未发现书籍、书签、划线或批注差异。")
                    : T("书籍已同步，未发现需要传输的差异。（当前 Kindle 未检测到批注数据库，跳过书签与批注同步。）");
                DevicePageStatusText.Text = upToDate;
                await ShowMessageAsync(T("Kindle 双向同步"), upToDate);
                return;
            }

            var summaryLines = new List<string>();
            if (bookPlan.BooksToDevice.Count > 0)
            {
                var sample = string.Join("、", bookPlan.BooksToDevice.Take(3).Select(book => $"《{book.Title}》"));
                if (bookPlan.BooksToDevice.Count > 3) sample += T(" 等");
                summaryLines.Add(T("• 书籍（电脑 → Kindle）：{0} 本（{1}）", bookPlan.BooksToDevice.Count, sample));
            }
            if (bookPlan.BooksToComputer.Count > 0)
            {
                var sample = string.Join("、", bookPlan.BooksToComputer.Take(3).Select(book => $"《{book.Title}》"));
                if (bookPlan.BooksToComputer.Count > 3) sample += T(" 等");
                summaryLines.Add(T("• 书籍（Kindle → 电脑）：{0} 本（{1}）", bookPlan.BooksToComputer.Count, sample));
            }
            if (annotationPreview is { HasChanges: true })
            {
                var annToComputer = annotationPreview.Books.Sum(book => book.ChangesToComputer - book.BookmarksToComputer);
                var annToDevice = annotationPreview.Books.Sum(book => book.ChangesToDevice - book.BookmarksToDevice);
                var bmToComputer = annotationPreview.Books.Sum(book => book.BookmarksToComputer);
                var bmToDevice = annotationPreview.Books.Sum(book => book.BookmarksToDevice);
                if (annToComputer > 0 || annToDevice > 0)
                    summaryLines.Add(T("• 划线与批注：导入电脑 {0} 项，写入 Kindle {1} 项", annToComputer, annToDevice));
                if (bmToComputer > 0 || bmToDevice > 0)
                    summaryLines.Add(T("• 书签：导入电脑 {0} 个，写入 Kindle {1} 个", bmToComputer, bmToDevice));
            }
            else if (!annotationSyncSupported)
            {
                summaryLines.Add(T("• 书签、划线与批注：未检测到设备批注数据库，本次仅同步书籍。"));
            }

            var confirmed = await ConfirmAsync(
                T("Kindle USB 双向同步"),
                T("检测到以下差异（双向合并，不会自动删除任一端书籍、书签或批注）：")
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, summaryLines)
                + Environment.NewLine + Environment.NewLine
                + T("同步期间请保持 Kindle 解锁，并暂时不要操作设备。"),
                T("开始双向同步"),
                _lifetimeCancellation.Token,
                allowDiagnosticAutoConfirm: false,
                cancelText: T("取消"));
            if (!confirmed || !IsCurrentDevice(device)) return;

            var booksSent = 0;
            var booksImported = 0;
            var bookFailures = new List<string>();
            KindleAnnotationSyncResult? annotationResult = null;
            var totalBookSteps = bookPlan.BooksToDevice.Count + bookPlan.BooksToComputer.Count;
            var completedBookSteps = 0;
            ShowKindleSyncBlockingOverlay(
                T("正在双向同步 Kindle"),
                T("正在准备同步任务…"),
                percent: totalBookSteps > 0 ? 0 : null);

            await TrackDeviceOperationAsync(async () =>
            {
                // 1. Computer -> Kindle books
                for (var index = 0; index < bookPlan.BooksToDevice.Count; index++)
                {
                    _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                    var book = bookPlan.BooksToDevice[index];
                    var stageText = T("正在发送书籍到 Kindle：《{0}》（{1}/{2}）…", book.Title, index + 1, bookPlan.BooksToDevice.Count);
                    DevicePageStatusText.Text = stageText;
                    UpdateKindleSyncBlockingOverlay(stageText, totalBookSteps > 0 ? 100d * completedBookSteps / totalBookSteps : null);
                    PreparedKindleTransfer? prepared = null;
                    try
                    {
                        prepared = await PrepareKindleTransferAsync(device, book, progress: null, _lifetimeCancellation.Token);
                        await EnsureCurrentDeviceAsync(device);
                        await _kindle.SendBookAsync(
                            device,
                            prepared.File,
                            prepared.SourcePath,
                            progress: null,
                            cancellationToken: _lifetimeCancellation.Token,
                            coverOverridePath: prepared.CoverOverridePath);
                        booksSent++;
                    }
                    catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        bookFailures.Add(T("发送到 Kindle 失败《{0}》：{1}", book.Title, UiText.Localize(exception.Message)));
                    }
                    finally
                    {
                        prepared?.Dispose();
                        completedBookSteps++;
                    }
                }

                // 2. Kindle -> Computer books
                if (bookPlan.BooksToComputer.Count > 0)
                {
                    var tempDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "kindle-full-sync", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDirectory);
                    var importPaths = new List<string>();
                    try
                    {
                        for (var index = 0; index < bookPlan.BooksToComputer.Count; index++)
                        {
                            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
                            var kindleBook = bookPlan.BooksToComputer[index];
                            var stageText = T("正在从 Kindle 导出书籍：《{0}》（{1}/{2}）…", kindleBook.Title, index + 1, bookPlan.BooksToComputer.Count);
                            DevicePageStatusText.Text = stageText;
                            UpdateKindleSyncBlockingOverlay(stageText, totalBookSteps > 0 ? 100d * completedBookSteps / totalBookSteps : null);
                            var bookDir = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(bookDir);
                            try
                            {
                                await EnsureCurrentDeviceAsync(device);
                                var localSource = await _kindle.ExportBookAsync(
                                    device, kindleBook, bookDir, progress: null, _lifetimeCancellation.Token);
                                var importPath = localSource;
                                if (BookFormatConversionPolicy.Normalize(kindleBook.Format) == "kfx")
                                {
                                    importPath = Path.Combine(bookDir, Path.GetFileNameWithoutExtension(localSource) + ".epub");
                                    await _formatConverter.ConvertAsync(
                                        localSource,
                                        importPath,
                                        progress: null,
                                        _lifetimeCancellation.Token,
                                        new FormatConversionMetadata(kindleBook.Title, kindleBook.Authors));
                                }
                                importPaths.Add(importPath);
                            }
                            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                bookFailures.Add(T("从 Kindle 导入失败《{0}》：{1}", kindleBook.Title, UiText.Localize(exception.Message)));
                            }
                            finally
                            {
                                completedBookSteps++;
                            }
                        }

                        if (importPaths.Count > 0)
                        {
                            var importStage = T("正在将导出的书籍导入电脑书库…");
                            DevicePageStatusText.Text = importStage;
                            UpdateKindleSyncBlockingOverlay(importStage, 95);
                            var importResult = await ViewModel.ImportAsync(importPaths, cancellationToken: _lifetimeCancellation.Token);
                            _ = await AutoGenerateReaderFormatsForImportsAsync(importResult, _lifetimeCancellation.Token);
                            booksImported = importResult.AddedCount;
                        }
                    }
                    finally
                    {
                        try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }

                // 3. Highlights, Notes, and Bookmarks bidirectional sync
                if (annotationSyncSupported && hasAnnotationChanges)
                {
                    UpdateKindleSyncBlockingOverlay(T("正在自动备份并同步书签、划线与批注…"));
                    annotationResult = await RunKindleAnnotationTaskAsync(
                        T("同步 Kindle 书签与批注"),
                        cancellable: false,
                        (progress, _) => Task.Run(() => _kindleAnnotationSync.SynchronizeAsync(
                            device, deviceBooks, _kindle, _lifetimeCancellation.Token, progress),
                            _lifetimeCancellation.Token),
                        _lifetimeCancellation.Token);
                }
            });
            HideKindleSyncBlockingOverlay();

            if (_lifetimeCancellation.IsCancellationRequested) return;

            if (booksSent > 0 || booksImported > 0)
            {
                await RefreshCollectionsAsync();
                await RefreshDeviceBooksAsync();
                await RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
                UpdateLibraryUi();
            }
            if (annotationResult is not null)
                await RefreshAfterKindleAnnotationSyncAsync(device);

            var doneLines = new List<string>();
            if (booksSent > 0 || booksImported > 0)
                doneLines.Add(T("书籍：发送到 Kindle {0} 本，导入电脑 {1} 本。", booksSent, booksImported));
            if (annotationResult is not null)
            {
                var annIn = annotationResult.ChangesToComputer - annotationResult.BookmarksToComputer;
                var annOut = annotationResult.ChangesToDevice - annotationResult.BookmarksToDevice;
                if (annIn > 0 || annOut > 0)
                    doneLines.Add(T("划线与批注：导入电脑 {0} 项，写入 Kindle {1} 项。", annIn, annOut));
                if (annotationResult.BookmarksToComputer > 0 || annotationResult.BookmarksToDevice > 0)
                    doneLines.Add(T("书签：导入电脑 {0} 个，写入 Kindle {1} 个。", annotationResult.BookmarksToComputer, annotationResult.BookmarksToDevice));
                if (!string.IsNullOrWhiteSpace(annotationResult.LocalBackupPath))
                    doneLines.Add(T("本机备份：{0}", annotationResult.LocalBackupPath));
                if (annotationResult.ChangesToDevice > 0 && device.Transport != KindleTransport.MassStorage)
                    doneLines.Add(T("如果 Kindle 上暂未显示新书签或批注，请断开连接后重启 Kindle 再查看。"));
            }
            if (bookFailures.Count > 0)
                doneLines.Add(T("部分书籍失败（{0} 本）：{1}", bookFailures.Count, string.Join("；", bookFailures.Take(3))));
            if (doneLines.Count == 0)
                doneLines.Add(T("双向同步已完成。"));

            var completion = string.Join(Environment.NewLine, doneLines);
            DevicePageStatusText.Text = completion.ReplaceLineEndings(" · ");
            await ShowMessageAsync(T("Kindle 双向同步完成"), completion);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HideKindleSyncBlockingOverlay();
            var message = T("Kindle 双向同步失败：{0}", UiText.Localize(exception.Message));
            DevicePageStatusText.Text = message;
            await ShowMessageAsync(T("Kindle 双向同步失败"), message);
        }
        finally
        {
            HideKindleSyncBlockingOverlay();
            _kindleFullSyncBusy = false;
            UpdateKindleFullSyncButtonState();
            UpdateKindleAnnotationSyncButtonVisibility();
        }
    }
}
