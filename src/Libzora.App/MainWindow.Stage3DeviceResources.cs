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
/// Device fonts and dictionaries: scanning, importing and deletion.
/// </summary>
public partial class MainWindow
{
    private async Task OpenDeviceResourcePageAsync(KindleResourceKind kind)
    {
        if (BlockNavigationWhileTransferring()) return;
        _deviceResourceKind = kind;
        ShowStage3Page(DeviceResourcePage);
        DeviceResourcePageTitle.Text = kind == KindleResourceKind.Font ? T("字体管理") : T("字典管理");
        UpdateDeviceResourceCapabilities();
        await RefreshDeviceResourcesAsync();
    }

    private async Task RefreshDeviceResourcesAsync(bool forceRefresh = false)
    {
        var resourceKind = _deviceResourceKind;
        DeviceResources.Clear();
        DeviceResourceList.SelectedItem = null;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        UpdateDeviceResourceCapabilities();
        if (_kindle is null || CurrentDevice is not { } device)
        {
            DeviceResourceDeviceText.Text = T("未检测到设备");
            DeviceResourceCountText.Text = T("0 个文件");
            DeviceResourceStatusText.Text = T("请先连接设备。");
            DeviceResourceEmptyText.IsVisible = true;
            return;
        }
        if (!device.Profile.SupportsResource(_deviceResourceKind))
        {
            DeviceResourceDeviceText.Text = _deviceDisplayName ?? device.Name;
            DeviceResourceCountText.Text = T("0 个文件");
            DeviceResourceStatusText.Text = T("当前设备暂不支持此资源管理功能。");
            DeviceResourceEmptyText.IsVisible = false;
            return;
        }
        try
        {
            if (_deviceWarmTask is not null)
                await _deviceWarmTask;
            if (!IsCurrentDevice(device) || resourceKind != _deviceResourceKind) return;
            var cacheKey = BuildDeviceCacheKey(device);
            if (!forceRefresh && _deviceResourceCache.TryGetValue((cacheKey, resourceKind), out var cached))
            {
                ApplyDeviceResources(cached, device);
                return;
            }

            var resources = await _kindle.ScanResourcesAsync(device, resourceKind, _lifetimeCancellation.Token);
            if (!IsCurrentDevice(device)) return;
            _deviceResourceCache[(cacheKey, resourceKind)] = resources;
            if (resourceKind != _deviceResourceKind) return;
            ApplyDeviceResources(resources, device);
            await PersistDeviceAuxiliaryCacheBestEffortAsync(device);
        }
        catch (Exception exception)
        {
            if (!IsCurrentDevice(device) || resourceKind != _deviceResourceKind) return;
            DeviceResourceDeviceText.Text = device.Name;
            DeviceResourceCountText.Text = T("读取失败");
            DeviceResourceStatusText.Text = T("读取失败：{0}", UiText.Localize(exception.Message));
            DeviceResourceEmptyText.IsVisible = true;
        }
    }

    private async Task<bool> RefreshDeviceResourceCachesAsync(KindleDevice device)
    {
        if (_kindle is null || !IsCurrentDevice(device)) return false;

        try
        {
            if (_deviceWarmTask is not null)
                await _deviceWarmTask;

            var cacheKey = BuildDeviceCacheKey(device);
            IReadOnlyList<KindleDeviceResource>? fonts = null;
            IReadOnlyList<KindleDeviceResource>? dictionaries = null;
            Exception? firstFailure = null;

            try
            {
                fonts = await ScanDeviceResourcesWithRetryAsync(device, KindleResourceKind.Font);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                firstFailure = exception;
                _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Font));
            }

            try
            {
                dictionaries = await ScanDeviceResourcesWithRetryAsync(device, KindleResourceKind.Dictionary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                firstFailure ??= exception;
                _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Dictionary));
            }

            if (!IsCurrentDevice(device)) return false;
            if (fonts is not null)
                _deviceResourceCache[(cacheKey, KindleResourceKind.Font)] = fonts;
            if (dictionaries is not null)
                _deviceResourceCache[(cacheKey, KindleResourceKind.Dictionary)] = dictionaries;

            var current = _deviceResourceKind == KindleResourceKind.Font ? fonts : dictionaries;
            if (current is null)
            {
                if (_deviceResourceCache.TryGetValue((cacheKey, _deviceResourceKind), out var cachedCurrent))
                    ApplyDeviceResources(cachedCurrent, device);
                throw firstFailure ?? new IOException("无法读取设备资源目录。");
            }

            ApplyDeviceResources(current, device);
            if (fonts is not null && dictionaries is not null)
                await PersistDeviceAuxiliaryCacheBestEffortAsync(device);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = T("刷新失败：{0}", UiText.Localize(exception.Message));
            return false;
        }
    }

    private async Task<IReadOnlyList<KindleDeviceResource>> ScanDeviceResourcesWithRetryAsync(
        KindleDevice device,
        KindleResourceKind kind)
    {
        if (!device.Profile.SupportsResource(kind)) return [];
        if (_kindle is null) throw new InvalidOperationException("设备服务不可用。");

        Exception? lastFailure = null;
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await _kindle.ScanResourcesAsync(device, kind, _lifetimeCancellation.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                lastFailure = exception;
                if (attempt + 1 >= maxAttempts) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(350 + attempt * 350), _lifetimeCancellation.Token);
            }
        }

        throw lastFailure ?? new IOException("无法读取设备资源目录。");
    }

    private void ApplyDeviceResources(IReadOnlyList<KindleDeviceResource> resources, KindleDevice device)
    {
        if (!IsCurrentDevice(device)) return;
        UpdateDeviceResourceCapabilities();
        DeviceResources.Clear();
        DeviceResourceList.SelectedItem = null;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        foreach (var resource in resources) DeviceResources.Add(resource);
        DeviceResourceDeviceText.Text = _deviceDisplayName ?? device.Name;
        DeviceResourceCountText.Text = T("{0} 个文件", resources.Count);
        DeviceResourceStatusText.Text = device.Profile.SupportsResource(_deviceResourceKind)
            ? T("已读取 {0} 个文件", resources.Count) : T("当前设备暂不支持此资源管理功能。");
        DeviceResourceEmptyText.IsVisible = resources.Count == 0;
    }

    private static string BuildDeviceCacheKey(KindleDevice device) =>
        $"{device.Transport}:{device.Identity}:{device.Profile.Family}:{device.Profile.BooksDirectory}:"
        + (device.Transport == KindleTransport.MassStorage ? Path.GetFullPath(device.RootPath) : device.RootPath);

    private void InvalidateCurrentDeviceResourceCaches()
    {
        if (CurrentDevice is not { } device) return;
        var cacheKey = BuildDeviceCacheKey(device);
        _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Font));
        _deviceResourceCache.Remove((cacheKey, KindleResourceKind.Dictionary));
    }

    private void RemoveDeletedDeviceResourceFromCacheAndView(
        KindleDevice device,
        KindleDeviceResource deletedResource)
    {
        var cacheKey = BuildDeviceCacheKey(device);
        var remaining = DeviceResources
            .Where(resource => !string.Equals(
                resource.RelativePath,
                deletedResource.RelativePath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _deviceResourceCache[(cacheKey, deletedResource.Kind)] = remaining;
        if (_deviceResourceKind == deletedResource.Kind)
            ApplyDeviceResources(remaining, device);
    }

    private async Task PersistDeviceAuxiliaryCacheAsync(KindleDevice device)
    {
        var key = BuildDeviceCacheKey(device);
        var previous = await _kindleAuxiliaryCacheStore.GetAsync(device.Identity, _lifetimeCancellation.Token);
        await _kindleAuxiliaryCacheStore.SaveAsync(device.Identity, new KindleDeviceAuxiliaryCacheSnapshot
        {
            Fonts = (_deviceResourceCache.GetValueOrDefault((key, KindleResourceKind.Font)) ?? previous?.Fonts ?? []).ToList(),
            Dictionaries = (_deviceResourceCache.GetValueOrDefault((key, KindleResourceKind.Dictionary)) ?? previous?.Dictionaries ?? []).ToList(),
            Clippings = (_deviceClippingCache.GetValueOrDefault(device.Identity) ?? previous?.Clippings ?? []).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        }, _lifetimeCancellation.Token);
    }

    private async Task PersistDeviceAuxiliaryCacheBestEffortAsync(KindleDevice device)
    {
        try
        {
            await PersistDeviceAuxiliaryCacheAsync(device);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The device operation has already succeeded. A locked local
            // cache must not turn a successful resource import/delete into a
            // false failure in the device page.
            DevicePageStatusText.Text = T("设备缓存暂未更新：{0}", UiText.Localize(exception.Message));
        }
    }

    private void DeviceResourceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var hasSelection = CurrentDevice is { } device && device.Profile.SupportsResource(_deviceResourceKind)
            && DeviceResourceList.SelectedItem is KindleDeviceResource;
        ExportDeviceResourceButton.IsEnabled = hasSelection;
        DeleteDeviceResourceButton.IsEnabled = hasSelection;
    }

    private async void FontManagementButton_Click(object? sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Font);
    private async void DictionaryManagementButton_Click(object? sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Dictionary);

    private async void RefreshDeviceResourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(scanBooks: false);
        if (CurrentDevice is { } device)
            await RefreshDeviceResourceCachesAsync(device);
        else
            await RefreshDeviceResourcesAsync(forceRefresh: true);
    }

    private async void ImportDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_deviceResourceBusy || _kindle is null || CurrentDevice is not { } device
            || !device.Profile.SupportsResource(_deviceResourceKind)) return;
        try
        {
            var resourceKind = _deviceResourceKind;
            var extensions = device.Profile.ResourceFormats(resourceKind).Select(format => "*." + format).ToArray();
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入设备资源"),
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(T("设备资源")) { Patterns = extensions }]
            });
            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => path is not null)
                .Select(path => path!)
                .ToArray();
            if (paths.Length == 0) return;
            await EnsureCurrentDeviceAsync(device);
            if (resourceKind != _deviceResourceKind) return;
            await ImportDeviceResourcePathsAsync(paths);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = T("导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
    }

    private void DeviceResourcePage_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CurrentDevice is { } device && device.Profile.SupportsResource(_deviceResourceKind)
            && LibraryDropImportPolicy.CanAccept(e.DataTransfer)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DeviceResourcePage_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CurrentDevice is not { } device || !device.Profile.SupportsResource(_deviceResourceKind)) return;
        try
        {
            var resourceKind = _deviceResourceKind;
            var draggedPaths = LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer);
            var paths = draggedPaths
                .Where(path => device.Profile.SupportsResourceFile(resourceKind, path))
                .ToArray();
            if (paths.Length > 0)
            {
                // Explorer can keep the drag-source handle open until the
                // Drop event returns. Yield once so the WPD reader starts
                // after that drag transaction has been released.
                await Dispatcher.UIThread.InvokeAsync(() => { });
                if (!IsCurrentDevice(device) || resourceKind != _deviceResourceKind) return;
                await ImportDeviceResourcePathsAsync(paths);
            }
            else if (draggedPaths.Length > 0)
            {
                var formats = string.Join(" / ", device.Profile.ResourceFormats(_deviceResourceKind)).ToUpperInvariant();
                await ShowMessageAsync(T("无法导入"), T("拖入的文件中没有可用的 {0} 文件。", formats));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = T("导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
    }

    private void DictionaryManagementSection_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = LibraryDropImportPolicy.CanAccept(e.DataTransfer)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DictionaryManagementSection_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var draggedPaths = LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer);
            var paths = draggedPaths
                .Where(IsSupportedLocalDictionaryPath)
                .ToArray();
            if (paths.Length > 0)
            {
                // The source application may still hold a handle during the
                // Drop event; let it finish before parsing the dictionary.
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await ImportLocalDictionaryPathsAsync(paths);
            }
            else if (draggedPaths.Length > 0)
            {
                var message = T("拖入的文件中没有可用的词典文件。支持 MDX、AZW、MOBI、PRC、TXT、TSV 和 CSV。");
                DictionaryResultText.Text = message;
                await ShowMessageAsync(T("无法导入词典"), message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DictionaryResultText.Text = T("字典导入失败：{0}", UiText.Localize(exception.Message));
            await ShowMessageAsync(T("词典导入失败"), UiText.Localize(exception.Message));
        }
    }

    private static bool IsSupportedLocalDictionaryPath(string path) =>
        LocalDictionaryExtensions.Contains(Path.GetExtension(path));

    private bool _dictionaryImportBusy;

    private async Task ImportLocalDictionaryPathsAsync(IEnumerable<string> sourcePaths)
    {
        var paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(IsSupportedLocalDictionaryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            var message = T("没有找到可导入的词典文件。");
            DictionaryResultText.Text = message;
            await ShowMessageAsync(T("词典导入失败"), message);
            return;
        }

        if (_dictionaryImportBusy) return;
        _dictionaryImportBusy = true;
        ImportDictionaryButton.IsEnabled = false;
        DictionaryImportProgressPanel.IsVisible = true;
        DictionaryImportProgressBar.IsIndeterminate = true;
        DictionaryImportStatusText.Text = T("读取词典");
        var importClock = System.Diagnostics.Stopwatch.StartNew();
        var status = T("读取词典");
        var statusTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => DictionaryImportStatusText.Text = status + "\n" + T("已用时间：{0}", importClock.Elapsed.ToString(@"hh\:mm\:ss"));
        statusTimer.Start();
        try
        {
            var knownIds = (await _dictionaryService.ListAsync(_lifetimeCancellation.Token))
                .Select(dictionary => dictionary.Id)
                .ToHashSet(StringComparer.Ordinal);
            var importedCount = 0;
            var skippedDuplicateCount = 0;
            var failures = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var fileIndex = Array.IndexOf(paths, path) + 1;
                    var progress = new Progress<DictionaryImportProgress>(update =>
                    {
                        status = $"{fileIndex}/{paths.Length} · {Path.GetFileName(path)}\n{T(update.Stage)} · {update.EntryCount:N0} " + T("词条")
                            + (update.Percent is { } percent ? $" · {percent:F0}%" : "")
                            + (string.IsNullOrWhiteSpace(update.Detail) ? "" : "\n" + UiText.Localize(update.Detail));
                        DictionaryImportStatusText.Text = status + "\n" + T("已用时间：{0}", importClock.Elapsed.ToString(@"hh\:mm\:ss"));
                        DictionaryImportProgressBar.IsIndeterminate = update.Percent is null;
                        DictionaryImportProgressBar.Value = update.Percent ?? 0;
                    });
                    var imported = await RecordResourceOperationAsync(T("导入本地词典"), Path.GetFileNameWithoutExtension(path), token => Task.Run(() => _dictionaryService.ImportAsync(path,
                        cancellationToken: token, progress: progress), token), _lifetimeCancellation.Token);
                    if (knownIds.Add(imported.Id)) importedCount++;
                    else skippedDuplicateCount++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add(T("{0}：{1}", Path.GetFileName(path), UiText.Localize(exception.Message)));
                }
            }

            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            var resultLines = new List<string>();
            if (importedCount > 0)
                resultLines.Add(T("成功导入 {0} 个词典。", importedCount));
            if (skippedDuplicateCount > 0)
                resultLines.Add(T("跳过 {0} 个重复词典。", skippedDuplicateCount));
            if (failures.Count > 0)
            {
                resultLines.Add(T("有 {0} 个词典导入失败：", failures.Count));
                resultLines.AddRange(failures);
            }

            var resultMessage = string.Join(Environment.NewLine, resultLines);
            var resultTitle = T("词典导入完成");
            if (failures.Count > 0)
            {
                if (importedCount + skippedDuplicateCount == 0)
                    resultTitle = T("词典导入失败");
            }
            else if (skippedDuplicateCount > 0 && importedCount == 0)
            {
                resultTitle = T("已跳过重复词典");
            }
            else if (importedCount > 0 && skippedDuplicateCount == 0)
            {
                resultTitle = T("词典导入成功");
            }
            DictionaryResultText.Text = resultMessage;
            await ShowMessageAsync(resultTitle, resultMessage);
        }
        finally
        {
            statusTimer.Stop();
            _dictionaryImportBusy = false;
            ImportDictionaryButton.IsEnabled = true;
            DictionaryImportProgressPanel.IsVisible = false;
        }
    }

    private async Task ImportDeviceResourcePathsAsync(IEnumerable<string> sourcePaths) =>
        await TrackDeviceOperationAsync(() => ImportDeviceResourcePathsCoreAsync(sourcePaths));

    private async Task ImportDeviceResourcePathsCoreAsync(IEnumerable<string> sourcePaths)
    {
        if (_deviceResourceBusy || _kindle is null || CurrentDevice is not { } device
            || !device.Profile.SupportsResource(_deviceResourceKind)) return;
        var resourceKind = _deviceResourceKind;
        var paths = sourcePaths
            .Where(path => device.Profile.SupportsResourceFile(resourceKind, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0) return;

        _deviceResourceBusy = true;
        var resourceChanged = false;
        try
        {
            ShowTransferToast(T("导入设备资源"), T("正在导入 {0} 个文件…", paths.Length), progress: 0);
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                DeviceResourceStatusText.Text = T("正在导入 {0}…", Path.GetFileName(path));
                await EnsureCurrentDeviceAsync(device);
                await RecordResourceActionAsync(resourceKind == KindleResourceKind.Font ? T("导入设备字体") : T("导入设备词典"), Path.GetFileNameWithoutExtension(path), token => _kindle.SendResourceAsync(
                    device,
                    resourceKind,
                    path,
                    progress: CreateRecordedTransferProgress(null),
                    cancellationToken: token), _lifetimeCancellation.Token);
                resourceChanged = true;
                ShowTransferToast(T("导入设备资源"), T("正在导入 {0}…", Path.GetFileName(path)), progress: (index + 1) * 100 / paths.Length);
            }
            InvalidateCurrentDeviceResourceCaches();
            if (CurrentDevice is { } currentDevice && !await RefreshDeviceResourceCachesAsync(currentDevice))
            {
                DeviceResourceStatusText.Text = T("已导入 {0} 个文件，但设备资源缓存刷新失败，请点击刷新。", paths.Length);
                HideTransferToast();
                return;
            }
            ShowTransferToast(T("导入设备资源"), T("已导入 {0} 个文件。", paths.Length), progress: 100, autoHide: true, completed: true);
        }
        catch (Exception exception)
        {
            if (resourceChanged)
            {
                InvalidateCurrentDeviceResourceCaches();
                if (CurrentDevice is { } currentDevice)
                    await RefreshDeviceResourceCachesAsync(currentDevice);
            }
            DeviceResourceStatusText.Text = T("导入失败：{0}", UiText.Localize(exception.Message));
            HideTransferToast();
            await ShowMessageAsync(T("无法导入"), UiText.Localize(exception.Message));
        }
        finally
        {
            _deviceResourceBusy = false;
            UpdateDeviceResourceCapabilities();
        }
    }

    private async void ExportDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        var resource = sender is Button { Tag: KindleDeviceResource taggedResource }
            ? taggedResource
            : DeviceResourceList.SelectedItem as KindleDeviceResource;
        if (resource is null || _kindle is null || CurrentDevice is not { } device) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var extension = Path.GetExtension(resource.FileName);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出设备资源"),
            SuggestedFileName = resource.FileName,
            FileTypeChoices = [new FilePickerFileType(resource.Kind == KindleResourceKind.Font ? T("字体") : T("字典")) { Patterns = [$"*{extension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await TrackDeviceOperationAsync(async () =>
        {
            ShowTransferToast(T("导出设备资源"), T("导出设备资源"), isIndeterminate: true);
            try
            {
                await EnsureCurrentDeviceAsync(device);
                await RecordResourceActionAsync(resource.Kind == KindleResourceKind.Font ? T("导出设备字体") : T("导出设备词典"), Path.GetFileNameWithoutExtension(resource.FileName), token => _kindle.ExportResourceAsync(device, resource, path, token), _lifetimeCancellation.Token);
                DeviceResourceStatusText.Text = T("已导出 {0}", resource.FileName);
                ShowTransferToast(T("导出设备资源"), T("已导出 {0}", resource.FileName), progress: 100, autoHide: true, completed: true);
            }
            catch (Exception exception)
            {
                HideTransferToast();
                DeviceResourceStatusText.Text = T("导出失败：{0}", UiText.Localize(exception.Message));
            }
        });
    }

    private async void DeleteDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        var resource = sender is Button { Tag: KindleDeviceResource taggedResource }
            ? taggedResource
            : DeviceResourceList.SelectedItem as KindleDeviceResource;
        if (resource is null || _kindle is null || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync(T("删除设备资源"), T("确定删除设备文件 {0} 吗？", resource.RelativePath))) return;
        await TrackDeviceOperationAsync(async () =>
        {
            ShowTransferToast(T("删除设备资源"), T("删除设备资源"), isIndeterminate: true);
            try
            {
                await EnsureCurrentDeviceAsync(device);
                await RecordResourceActionAsync(resource.Kind == KindleResourceKind.Font ? T("删除设备字体") : T("删除设备词典"), Path.GetFileNameWithoutExtension(resource.FileName), token => _kindle.RemoveResourceAsync(device, resource, token), _lifetimeCancellation.Token);
                InvalidateCurrentDeviceResourceCaches();
                RemoveDeletedDeviceResourceFromCacheAndView(device, resource);
                await PersistDeviceAuxiliaryCacheBestEffortAsync(device);
                await RefreshDeviceResourceCachesAsync(device);
                DeviceResourceStatusText.Text = T("已删除 {0}", resource.FileName);
                ShowTransferToast(T("删除设备资源"), T("已删除 {0}", resource.FileName), progress: 100, autoHide: true, completed: true);
            }
            catch (Exception exception)
            {
                HideTransferToast();
                DeviceResourceStatusText.Text = T("删除失败：{0}", UiText.Localize(exception.Message));
            }
        });
    }
}
