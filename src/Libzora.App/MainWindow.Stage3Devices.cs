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
/// Device discovery, connection state and resource caches.
/// </summary>
public partial class MainWindow
{
    private async Task RefreshDevicesAsync(
        bool scanBooks,
        CancellationToken cancellationToken = default)
    {
        if (_isRefreshingDevices || _deviceEjectInProgress) return;
        _isRefreshingDevices = true;
        KindleDevice? deviceToRefresh = null;
        var deviceChanged = false;
        var detectionStarted = Stopwatch.GetTimestamp();
        try
        {
            // A manually ejected device stays disconnected while it remains
            // plugged in. Once the reader is physically removed, release the
            // latch so plugging it back in reconnects automatically instead of
            // leaving the app disconnected until it is restarted.
            if (_manuallyDisconnectedDeviceId is not null)
            {
                if (_kindle is IDeviceAttachmentProbe probe)
                {
                    var probeToken = cancellationToken.CanBeCanceled
                        ? cancellationToken
                        : _lifetimeCancellation.Token;
                    bool attached;
                    try
                    {
                        attached = await probe.IsDeviceAttachedAsync(_manuallyDisconnectedDeviceId, probeToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Device attachment probe failed: {exception}");
                        attached = true;
                    }

                    if (attached)
                    {
                        // Keep the "stopped access" message instead of
                        // resetting the sidebar on every poll.
                        if (_devices.Count != 0) SetDisconnectedDeviceUi();
                        return;
                    }

#if DEBUG
                    Console.Error.WriteLine("Manual disconnect latch released: reader was removed.");
#endif
                    _manuallyDisconnectedDeviceId = null;
                }
                else
                {
                    SetDisconnectedDeviceUi();
                    return;
                }
            }

            if (_kindle is null)
            {
                _devices = [];
                DevicePageDeviceText.Text = T("平台服务未连接");
                DevicePageStatusText.Text = T("设备功能将在 Windows 平台启动头中启用。");
                SetEjectButtonsEnabled(false);
                return;
            }

            var detected = await DetectDevicesWithTimeoutAsync(cancellationToken);
            if (detected.Count != 1)
            {
                _acceptedDeviceId = null;
                _ignoredDeviceId = null;
                _manuallyDisconnectedDeviceId = null;
                _lastDeviceIdentity = null;
                SetDisconnectedDeviceUi(detected.Count > 1 ? T("检测到多台阅读设备，请只保留一台连接。") : null);
                return;
            }

            var device = detected[0];
            var displayName = await _deviceModelStore.GetModelAsync(device.Identity, cancellationToken)
                ?? (ReaderDeviceProfiles.FamilyFromName(_appSettings.DefaultDeviceModel) == device.Profile.Family
                    && device.Profile.Family != ReaderDeviceFamily.Generic ? _appSettings.DefaultDeviceModel : null)
                ?? device.Name;
            if (!string.Equals(_acceptedDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
            {
                if (_appSettings.AutoConnectDevice)
                {
                    _acceptedDeviceId = device.Identity;
                    _ignoredDeviceId = null;
                }
                else
                {
                    if (string.Equals(_ignoredDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
                    {
                        SetDisconnectedDeviceUi(T("已忽略 {0}", displayName));
                        return;
                    }

                    if (!await ShowDevicePromptAsync(
                            T("发现阅读设备"),
                            T("发现 {0}（{1}）。是否连接到 Libzora？", displayName, device.ConnectionLabel),
                            T("连接"),
                            T("暂不连接")))
                    {
                        _ignoredDeviceId = device.Identity;
                        _lastDeviceIdentity = null;
                        SetDisconnectedDeviceUi(T("已忽略 {0}", displayName));
                        return;
                    }

                    _acceptedDeviceId = device.Identity;
                    _ignoredDeviceId = null;
                }
            }

            var changed = !IsCurrentDevice(device);
            if (changed)
            {
                ResetKindleAnnotationSyncPrompt();
                SetDisconnectedDeviceUi();
            }
            _devices = [device];
            _lastDeviceIdentity = device.Identity;
            _deviceDisplayName = displayName;
            UpdateDeviceResourceCapabilities();
            DevicePageDeviceText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
            DeviceNameButton.IsEnabled = true;
            if (changed || (!_deviceBookScanFailed && _deviceBookScanTask is not { IsCompleted: false }))
                DevicePageStatusText.Text = changed ? T("设备已连接，正在准备设备信息…") : T("设备已连接。");
            KindleStatusText.Text = _deviceDisplayName;
            KindleConnectionText.Text = T("已连接");
            KindleConnectionText.IsVisible = false;
            DeviceStorageText.Text = device.CapacityLabel;
            _deviceUsedRatio = device.TotalBytes > 0
                ? Math.Clamp((device.TotalBytes - device.FreeBytes) / (double)device.TotalBytes, 0, 1)
                : 0;
            UpdateDeviceStorageBar();
            SetEjectButtonsEnabled(true);
            if (changed) ShowDeviceStatusToast(T("{0} 已连接", displayName));
#if DEBUG
            if (changed)
                Console.Error.WriteLine($"Device connection ready in {Stopwatch.GetElapsedTime(detectionStarted).TotalMilliseconds:F1}ms ({device.Transport}); content loads separately.");
#endif
            if (changed)
                _deviceWarmTask = TrackDeviceOperationAsync(() => WarmDeviceCachesAsync(
                    device, !scanBooks && !DeviceResourcePage.IsVisible && !ReadingMaterialsPage.IsVisible, cancellationToken));
            deviceToRefresh = device;
            deviceChanged = changed;
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException exception)
        {
            var detail = T("设备检测失败：{0}", UiText.Localize(exception.Message));
            if (CurrentDevice is null)
                SetDisconnectedDeviceUi(detail);
            else
                DevicePageStatusText.Text = detail;
            KindleStatusText.Text = T("设备状态读取失败");
        }
        catch (Exception exception)
        {
            SetDisconnectedDeviceUi(T("设备检测失败：{0}", UiText.Localize(exception.Message)));
            KindleStatusText.Text = T("设备状态读取失败");
        }
        finally
        {
            // Only connection discovery is single-flight. Slow book/cover/note
            // reads below must not hold off a later USB removal or reconnect.
            _isRefreshingDevices = false;
        }

        if (deviceToRefresh is not { } connected || !IsCurrentDevice(connected)) return;
        try
        {
            if (scanBooks && (deviceChanged || !_deviceBooksLoaded || _deviceBooksDirty))
                await RefreshDeviceBooksAsync(cancellationToken);
            if (!IsCurrentDevice(connected)) return;
            if (deviceChanged && DeviceResourcePage.IsVisible) await RefreshDeviceResourcesAsync();
            if (!IsCurrentDevice(connected)) return;
            if (deviceChanged && ReadingMaterialsPage.IsVisible) await RefreshReadingMaterialsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // A content read failure is not a failed USB connection, and a
            // late failure from the previous reader must not disconnect a new one.
            if (IsCurrentDevice(connected))
                DevicePageStatusText.Text = T("设备信息读取失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task<IReadOnlyList<KindleDevice>> DetectDevicesWithTimeoutAsync(
        CancellationToken cancellationToken)
    {
        if (_kindle is null) return [];
        var effectiveToken = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _lifetimeCancellation.Token;
        // Never reuse a previous detection task. After a USB unplug a libmtp
        // call can block on the vanished device; a cached task would then keep
        // every later refresh waiting on the same dead handle, so replugging
        // the reader appeared to do nothing until the app was restarted.
        // Concurrent refreshes are already coalesced by _isRefreshingDevices.
        var detectionTask = _kindle.DetectDevicesAsync(effectiveToken);
        try
        {
            return await detectionTask.WaitAsync(TimeSpan.FromSeconds(15), effectiveToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("设备扫描超时，请确认设备已解锁后重试。");
        }
    }

    private void SetDisconnectedDeviceUi(string? detail = null)
    {
        CancelKindleAnnotationAnalysis();
        ResetKindleAnnotationSyncPrompt();
        _deviceBookScanGeneration++;
        _deviceBookScanCancellation?.Cancel();
        _deviceBookScanCancellation = null;
        _deviceBookScanTask = null;
        foreach (var book in DeviceBooks) book.Dispose();
        DeviceBooks.Clear();
        _devicePinyinIndexes.Clear();
        _deviceCardsByPath.Clear();
        _libraryPresenceComparison = null;
        _filteredDeviceBooks = [];
        VisibleDeviceBooks.Clear();
        VisibleDeviceShelfBooks.Clear();
        _deviceSeriesFronts.Clear();
        _devicePageIndex = 0;
        _devicePageCount = 1;
        UpdateDeviceBookPaginationUi();
        _deviceBooksLoaded = false;
        _deviceBookScanFailed = false;
        SetDeviceBooksShowingCache(false);
        _deviceWarmTask = null;
        _deviceResourceCache.Clear();
        _deviceClippingCache.Clear();
        DeviceBookCountText.Text = "0";
        _devices = [];
        UpdateDeviceResourceCapabilities();
        MarkReadingMaterialsDirty();
        ClearDisconnectedDeviceNotes();
        RefreshLibraryPresenceState();
        UpdateDeviceBookSelectionUi();
        _deviceDisplayName = null;
        DevicePageDeviceText.Text = T("未检测到设备");
        DeviceNameButton.IsEnabled = false;
        DevicePageStatusText.Text = detail ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? T("请连接并解锁阅读设备；支持 USB 磁盘与 MTP，每次只接入一台。")
            : T("请连接并解锁阅读设备；当前平台支持 USB 磁盘，每次只接入一台。"));
        KindleStatusText.Text = T("无设备连接");
        KindleConnectionText.Text = detail ?? string.Empty;
        KindleConnectionText.IsVisible = !string.IsNullOrWhiteSpace(detail);
        DeviceStorageText.Text = T("无存储信息");
        DeviceBookEmptyText.Text = T("无设备书籍");
        _deviceUsedRatio = 0;
        UpdateDeviceStorageBar();
        SetEjectButtonsEnabled(false);
    }

    private void DeviceStorageBar_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateDeviceStorageBar();

    private void UpdateDeviceStorageBar()
    {
        var availableWidth = Math.Max(0, DeviceStorageBar.Bounds.Width - 2);
        var usedWidth = availableWidth * _deviceUsedRatio;
        if (Math.Abs(DeviceStorageUsedBar.Width - usedWidth) > 0.01)
            DeviceStorageUsedBar.Width = usedWidth;
    }

    private async Task RefreshDeviceBooksAsync(CancellationToken cancellationToken = default)
    {
        // A second refresh request should not open another MTP/USB scan while
        // the first one is still reading the device. The existing scan is
        // already publishing partial results, so there is nothing useful to
        // gain by competing with it.
        if (_deviceBookScanTask is { IsCompleted: false }) return;

        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _deviceBookScanCancellation = scanCancellation;
        var task = TrackDeviceOperationAsync(() => RefreshDeviceBooksCoreAsync(scanCancellation.Token));
        _deviceBookScanTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_deviceBookScanCancellation, scanCancellation))
                _deviceBookScanCancellation = null;
            if (ReferenceEquals(_deviceBookScanTask, task))
                _deviceBookScanTask = null;
        }
    }

    private async Task RefreshDeviceBooksAfterTransferAsync(
        KindleDevice device,
        CancellationToken cancellationToken)
    {
        // The send operation owns the whole batch. Mark the shelf dirty first
        // so leaving the device page cannot skip the next authoritative scan.
        _deviceBooksDirty = true;
        if (!IsCurrentDevice(device)) return;

        // A warm-up scan can still be finishing when the transfer completes.
        // Wait for that scan, then issue a fresh scan instead of silently
        // dropping the refresh because RefreshDeviceBooksAsync is single-flight.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_deviceBookScanTask is { } existing && !existing.IsCompleted)
            {
                try { await existing.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { return; }
            }

            if (!IsCurrentDevice(device)) return;
            await RefreshDeviceBooksAsync(cancellationToken);
            if (_deviceBookScanTask is not { } started || started.IsCompleted) return;
        }
    }

    private async Task TryRefreshDeviceBooksAfterTransferAsync(KindleDevice device)
    {
        try
        {
            await RefreshDeviceBooksAfterTransferAsync(device, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The transfer outcome is already reported; a failed shelf refresh
            // must not throw out of a finally block or replace that report.
            LogSendDiagnostic("RefreshDeviceBooksAfterTransferAsync", exception);
        }
    }

    private async Task RefreshDeviceBooksCoreAsync(CancellationToken cancellationToken)
    {
        if (_kindle is null || CurrentDevice is not { } device) return;

        var scanGeneration = ++_deviceBookScanGeneration;
        _deviceBookScanFailed = false;

        DevicePageStatusText.Text = T("正在扫描设备书籍…");
        DeviceBookEmptyText.Text = T("正在读取设备书库…");
        if (DeviceBooks.Count == 0)
            DeviceBookEmptyState.IsVisible = true;
        try
        {
            if (DeviceBooks.Count == 0)
            {
                IReadOnlyList<KindleBook> cached = [];
                try { cached = await _kindle.GetCachedBooksAsync(device, cancellationToken); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Debug.WriteLine($"Device book cache could not be read: {exception}");
                }
                if (!IsCurrentDevice(device) || scanGeneration != _deviceBookScanGeneration) return;
                if (cached.Count > 0)
                {
                    SetDeviceBooksShowingCache(true);
                    ReplaceDeviceBookCards(cached, resetPage: true);
                    DevicePageStatusText.Text = T("正在核对设备书库，暂时显示上次记录。");
                }
            }
            var progress = new Progress<KindleScanProgress>(update =>
            {
                if (scanGeneration != _deviceBookScanGeneration || !IsCurrentDevice(device)) return;
                ApplyDeviceBookScanProgress(update);
            });
            var books = await _kindle.ScanBooksProgressivelyAsync(device, progress, cancellationToken);
            if (!IsCurrentDevice(device) || scanGeneration != _deviceBookScanGeneration) return;

            // Progress<T> posts callbacks to the UI dispatcher. Invalidate
            // queued partial updates before applying the authoritative final
            // snapshot so a late callback cannot resurrect a dictionary entry.
            _deviceBookScanGeneration++;
            SetDeviceBooksShowingCache(false);
            ReplaceDeviceBookCards(books, resetPage: true);
            _libraryPresenceComparison = null;
            RefreshLibraryPresenceState();
            DevicePageStatusText.Text = T("已读取 {0} 本书 · {1}", books.Count, device.ConnectionLabel);
            _deviceBooksLoaded = true;
            _deviceBooksDirty = false;
        }
        catch (OperationCanceledException)
        {
            if (scanGeneration == _deviceBookScanGeneration)
                _deviceBookScanGeneration++;
        }
        catch (Exception exception)
        {
            if (scanGeneration != _deviceBookScanGeneration || !IsCurrentDevice(device)) return;
            _deviceBookScanGeneration++;
            _deviceBookScanFailed = true;
            if (DeviceBooks.Count == 0)
            {
                DeviceBookEmptyText.Text = T("扫描失败：{0}", UiText.Localize(exception.Message));
                DeviceBookEmptyState.IsVisible = true;
                DevicePageStatusText.Text = T("设备书库扫描失败。");
            }
            else
            {
                // Keep the fallback cards already published by the progressive
                // scan. A failed enrichment pass must not turn a visible
                // device library back into an empty state.
                DevicePageStatusText.Text = _deviceBooksShowingCache
                    ? T("设备书库核对失败，当前列表仅供查看；请重试：{0}", UiText.Localize(exception.Message))
                    : T("设备信息已读取部分内容：{0}", UiText.Localize(exception.Message));
            }
        }
    }

    private void ApplyDeviceBookScanProgress(KindleScanProgress progress)
    {
        DevicePageStatusText.Text = progress.Stage == KindleScanStage.Enumerated && progress.Total == 0
            ? T("已读取设备书库，正在整理…")
            : T("正在读取设备书籍：{0} / {1}，并补充书籍信息…", progress.Processed, progress.Total);
        if (progress.Stage == KindleScanStage.Enumerated)
        {
            ReplaceDeviceBookCards(progress.Books, resetPage: true);
            _libraryPresenceComparison = null;
            RefreshLibraryPresenceState(refreshDeviceView: false);
            if (DeviceBooks.Count == 0)
            {
                DeviceBookEmptyText.Text = T("正在读取设备书库…");
                DeviceBookEmptyState.IsVisible = true;
            }
            return;
        }

        var added = false;
        foreach (var book in progress.Books)
        {
            var card = FindDeviceBookCard(book.RelativePath);
            if (card is null)
            {
                AddDeviceBookCard(book);
                added = true;
            }
            else
            {
                card.UpdateBook(book);
            }
        }

        var removed = false;
        foreach (var path in progress.RemovedPaths)
        {
            var card = FindDeviceBookCard(path);
            if (card is null) continue;
            DeviceBooks.Remove(card);
            _deviceCardsByPath.Remove(NormalizeDeviceBookPath(card.Book.RelativePath));
            card.Dispose();
            removed = true;
        }

        DeviceBookCountText.Text = DeviceBooks.Count.ToString();
        if (removed) UpdateDeviceBookSelectionUi();
        if (added || removed)
            ApplyDeviceBookFilter(resetPage: false);
        if (DeviceBooks.Count == 0)
        {
            DeviceBookEmptyText.Text = T("正在读取设备书库…");
            DeviceBookEmptyState.IsVisible = true;
        }
    }

    private void SetDeviceBooksShowingCache(bool showing)
    {
        _deviceBooksShowingCache = showing;
        DeviceBookGridItems.IsHitTestVisible = !showing;
        DeviceBookListItems.IsHitTestVisible = !showing;
        if (showing) ClearDeviceBookSelection();
    }

    private KindleBookCardViewModel? FindDeviceBookCard(string relativePath)
    {
        if (_deviceCardsByPath.Count != DeviceBooks.Count)
        {
            _deviceCardsByPath.Clear();
            foreach (var card in DeviceBooks)
                _deviceCardsByPath[NormalizeDeviceBookPath(card.Book.RelativePath)] = card;
        }
        return _deviceCardsByPath.GetValueOrDefault(NormalizeDeviceBookPath(relativePath));
    }

    private void AddDeviceBookCard(KindleBook book)
    {
        var card = new KindleBookCardViewModel(book);
        _deviceCardsByPath[NormalizeDeviceBookPath(book.RelativePath)] = card;
        DeviceBooks.Add(card);
    }

    private void ReplaceDeviceBookCards(IReadOnlyList<KindleBook> books, bool resetPage)
    {
        var incoming = books
            .GroupBy(book => NormalizeDeviceBookPath(book.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(book => NormalizeDeviceBookPath(book.RelativePath), StringComparer.OrdinalIgnoreCase);

        foreach (var old in DeviceBooks.ToArray())
        {
            if (incoming.ContainsKey(NormalizeDeviceBookPath(old.Book.RelativePath))) continue;
            DeviceBooks.Remove(old);
            _devicePinyinIndexes.Remove(old);
            _deviceCardsByPath.Remove(NormalizeDeviceBookPath(old.Book.RelativePath));
            old.Dispose();
        }

        foreach (var book in incoming.Values)
        {
            var card = FindDeviceBookCard(book.RelativePath);
            if (card is null)
                AddDeviceBookCard(book);
            else
                card.UpdateBook(book);
        }

        DeviceBookCountText.Text = DeviceBooks.Count.ToString();
        UpdateDeviceBookSelectionUi();
        ApplyDeviceBookFilter(resetPage);
    }

    private static string NormalizeDeviceBookPath(string path) =>
        path.Replace('/', '\\').TrimStart('\\');

    private void RefreshLibraryPresenceState(bool refreshDeviceView = true)
    {
        var localBooks = _libraryMatchRecords.Count > 0
            ? _libraryMatchRecords
            : ViewModel.LibraryBooks
                .Select(book => new LibraryBookMatch(
                    book.Id,
                    book.Title,
                    book.Authors,
                    book.Files.Select(file => new LibraryFileMatch(file.RelativePath, file.Sha256)).ToArray()))
                .ToArray();
        _libraryPresenceComparison ??= BookLibraryComparer.Compare(
            localBooks,
            DeviceBooks.Select(card => card.Book));
        var comparison = _libraryPresenceComparison;
        foreach (var localCard in ViewModel.Books)
        {
            localCard.SetLibraryPresence(
                comparison.BooksOnKindle.Contains(localCard.Book.Id)
                    ? BookLibraryPresence.Both
                    : BookLibraryPresence.ComputerOnly);
            localCard.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled && _appSettings.ShowLibraryPresenceIcon);
        }
        foreach (var card in DeviceBooks)
        {
            card.SetLibraryPresence(
                comparison.KindleBooksOnComputer.Contains(card.Book.RelativePath)
                    ? BookLibraryPresence.Both
                    : BookLibraryPresence.KindleOnly);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled && _appSettings.ShowLibraryPresenceIcon);
        }

        if (refreshDeviceView && _stage3Ready)
            ApplyDeviceBookFilter();
    }

    private async Task WarmDeviceCachesAsync(
        KindleDevice device,
        bool preloadBooks,
        CancellationToken cancellationToken)
    {
        if (_kindle is null || !IsCurrentDevice(device)) return;
        try
        {
            // Prepare the book shelf; fonts, dictionaries and notes are read
            // when their respective pages are opened.
            if (preloadBooks && !_deviceBooksLoaded)
                await RefreshDeviceBooksAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentDevice(device)) return;
            DevicePageStatusText.Text = T("设备信息已读取部分内容：{0}", UiText.Localize(exception.Message));
        }
    }

    private void SetEjectButtonsEnabled(bool enabled)
    {
        DeviceStatusEjectButton.IsEnabled = enabled;
        EjectDeviceButton.IsEnabled = enabled;
        // ToolTip / automation name follow the connection state and transport,
        // exactly like the WinUI reference (WPD sessions are "stopped", USB
        // disks are "ejected").
        var action = !enabled
            ? T("未连接设备")
            : CurrentDevice?.Transport != KindleTransport.MassStorage ? T("停止访问设备") : T("安全弹出设备");
        ToolTip.SetTip(DeviceStatusEjectButton, action);
        AutomationProperties.SetName(DeviceStatusEjectButton, action);
        ToolTip.SetTip(EjectDeviceButton, action);
        AutomationProperties.SetName(EjectDeviceButton, action);
        UpdateKindleFullSyncButtonState();
    }
    private Task<bool> ShowDevicePromptAsync(
        string title,
        string message,
        string primaryText,
        string cancelText)
    {
        if (_devicePromptCompletion is not null)
            return Task.FromResult(false);

        _devicePromptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DevicePromptTitleText.Text = title;
        DevicePromptMessageText.Text = message;
        DevicePromptPrimaryButton.Content = primaryText;
        DevicePromptCancelButton.Content = cancelText;
        DevicePromptOverlay.IsVisible = true;
        DevicePromptOverlay.Opacity = 1;
        DevicePromptOverlay.Focus();
        return _devicePromptCompletion.Task;
    }

    private void DevicePromptPrimaryButton_Click(object? sender, RoutedEventArgs e)
        => CompleteDevicePrompt(true);

    private void DevicePromptCancelButton_Click(object? sender, RoutedEventArgs e)
        => CompleteDevicePrompt(false);

    private void DevicePromptOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CompleteDevicePrompt(false);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CompleteDevicePrompt(true);
        }
    }

    private void CompleteDevicePrompt(bool result)
    {
        var completion = _devicePromptCompletion;
        if (completion is null) return;
        _devicePromptCompletion = null;
        DevicePromptOverlay.IsVisible = false;
        completion.TrySetResult(result);
    }
}
