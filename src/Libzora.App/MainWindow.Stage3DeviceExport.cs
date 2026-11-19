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
/// Exporting and deleting device books.
/// </summary>
public partial class MainWindow
{
    private async Task ExportDeviceBookAsync(KindleBookCardViewModel card) =>
        await ExportDeviceBooksToLibraryAsync([card]);

    private async void ExportSelectedDeviceBooksButton_Click(object? sender, RoutedEventArgs e)
        => await ExportSelectedDeviceBooksAsync();

    private async Task ExportSelectedDeviceBooksAsync()
    {
        var selected = GetSelectedDeviceBooks();
        if (selected.Count == 0) return;
        await ExportDeviceBooksToLibraryAsync(selected);
    }

    private async Task ExportDeviceBooksToLibraryAsync(IReadOnlyList<KindleBookCardViewModel> cards)
    {
        if (_deviceBooksShowingCache) return;
        // A single export owns the device read and subsequent library import
        // as one unit. Without this gate, two card gestures can overlap after
        // the first await and race while importing their output.
        if (!_deviceBookExportGate.Wait(0))
        {
            SetTaskStatus(T("已有导出任务正在进行中。"));
            return;
        }

        try
        {
            await TrackDeviceOperationAsync(() => ExportDeviceBooksToLibraryCoreAsync(cards));
        }
        finally
        {
            _deviceBookExportGate.Release();
        }
    }

    private async Task ExportDeviceBooksToLibraryCoreAsync(IReadOnlyList<KindleBookCardViewModel> cards)
    {
        if (_kindle is null || CurrentDevice is not { } device || cards.Count == 0) return;
        var pending = cards.Where(card =>
        {
            var format = BookFormatConversionPolicy.Normalize(card.Book.Format);
            return format is "epub" or "pdf" or "mobi" or "azw3" or "kfx";
        }).ToArray();
        if (pending.Length == 0)
        {
            DevicePageStatusText.Text = T("所选书籍的格式电脑书库暂不支持。");
            await ShowMessageAsync(T("无法导出"), T("所选书籍的格式电脑书库暂不支持。"));
            return;
        }

        if (pending.Any(card => BookFormatConversionPolicy.Normalize(card.Book.Format) == "kfx"))
        {
            using var calibreSetup = new CalibreSetupService(proxyAddress: TranslationGoogleProxyBox.Text);
            if (string.IsNullOrWhiteSpace(calibreSetup.LocateCalibre(_appSettings.CalibrePath)))
            {
                var message = T("未安装 Calibre，无法完成此导出。请先安装 Calibre，或在设置中指定 ebook-convert.exe 后重试。");
                DevicePageStatusText.Text = message;
                await ShowMessageAsync(T("未安装 Calibre"), message);
                return;
            }
        }

        var titleLines = string.Join(Environment.NewLine, pending.Take(3).Select(card => T("《{0}》", card.Title)));
        if (pending.Length > 3) titleLines += T("{0}…等 {1} 本", Environment.NewLine, pending.Length);
        if (!await ConfirmAsync(
                T("导出到电脑书库：{0} 本书", pending.Length),
                T("将从 {0} 导出以下书籍并导入电脑书库：{1}{1}{2}", device.Name, Environment.NewLine, titleLines)))
            return;

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "device-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        // The export owns its snapshot of cards; dismiss the selection bar as
        // soon as the confirmed transfer begins.
        ClearDeviceBookSelection();
        var importPaths = new List<string>();
        var failed = 0;
        var calibreMissing = false;
        var failureDetails = new List<string>();
        TaskProgressPopupBar.Value = 0;
        ShowTransferToast(T("导出到电脑书库"), T("正在从设备导出 {0} 本书…", pending.Length), progress: 0);
        ShowTaskProgressPopup(recordTask: false);
        var acceptProgressUpdates = true;
        try
        {
            for (var index = 0; index < pending.Length; index++)
            {
                var card = pending[index];
                var bookDirectory = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bookDirectory);
                try
                {
                    using var progress = new CoalescingUiProgress<TransferProgress>(value =>
                    {
                        if (!acceptProgressUpdates) return;
                        var message = string.IsNullOrWhiteSpace(value.Message)
                            ? T("正在导出《{0}》：{1:0}%", card.Title, value.Percentage)
                            : UiText.Localize(value.Message);
                        DevicePageStatusText.Text = message;
                        ShowTransferToast(
                            T("导出到电脑书库"),
                            T("正在导出《{0}》（{1}/{2}）…", card.Title, index + 1, pending.Length),
                            progress: (index * 100 + Math.Min(100, value.Percentage)) / pending.Length);
                        TaskProgressPopupBar.Value = (index * 100 + Math.Min(100, value.Percentage)) / pending.Length;
                    });
                    DevicePageStatusText.Text = T("正在从设备读取《{0}》（{1}/{2}）…", card.Title, index + 1, pending.Length);
                    await EnsureCurrentDeviceAsync(device);
                    var localSource = await RecordResourceOperationAsync(T("从设备导出书籍"), card.Title, token => _kindle.ExportBookAsync(
                        device,
                        card.Book,
                        bookDirectory,
                        CreateRecordedTransferProgress(progress),
                        token), _lifetimeCancellation.Token);
                    var importPath = localSource;
                    if (BookFormatConversionPolicy.Normalize(card.Book.Format) == "kfx")
                    {
                        importPath = Path.Combine(bookDirectory, Path.GetFileNameWithoutExtension(localSource) + ".epub");
                        await _formatConverter.ConvertAsync(
                            localSource,
                            importPath,
                            new Progress<FormatConversionProgress>(value =>
                                DevicePageStatusText.Text = T("正在将《{0}》转换为 EPUB：{1}%", card.Title, value.RoundedPercentage)),
                            _lifetimeCancellation.Token,
                            new FormatConversionMetadata(card.Book.Title, card.Book.Authors));
                    }
                    importPaths.Add(importPath);
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    var detail = UiText.Localize(exception.Message);
                    var isCalibreMissing = IsCalibreMissingMessage(detail);
                    calibreMissing |= isCalibreMissing;
                    failureDetails.Add(T("《{0}》导出失败：{1}", card.Title, detail));
                    DevicePageStatusText.Text = isCalibreMissing
                        ? T("《{0}》导出失败：未检测到 Calibre。", card.Title)
                        : T("《{0}》导出失败：{1}", card.Title, detail);
                }
            }

            var imported = 0;
            var skipped = 0;
            if (importPaths.Count > 0)
            {
                DevicePageStatusText.Text = T("正在导入电脑书库…");
                var result = await ViewModel.ImportAsync(importPaths, cancellationToken: _lifetimeCancellation.Token);
                var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(result, _lifetimeCancellation.Token);
                imported = result.AddedCount;
                skipped = result.SkippedCount;
                failed += result.FailureCount + automaticFormats.Failures.Count;
                foreach (var item in result.Items.Where(item => !item.Succeeded))
                {
                    if (!string.IsNullOrWhiteSpace(item.Message))
                        failureDetails.Add(UiText.Localize(item.Message));
                }
                failureDetails.AddRange(automaticFormats.Failures);
                calibreMissing |= automaticFormats.Failures.Any(IsCalibreMissingMessage);
                await RefreshCollectionsAsync();
                // ViewModel.ImportAsync replaces the visible cards. The
                // library-change event can therefore finish on the previous
                // card set; refresh the badges after the import is complete.
                await RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
                UpdateLibraryUi();
            }
            var completionMessage = failed == 0
                ? skipped == 0
                    ? T("已从 {0} 导出并导入电脑书库 {1} 本书。", device.Name, imported)
                    : imported == 0
                        ? T("已从 {0} 导出；跳过 {1} 个重复或已有文件，未新增书籍。", device.Name, skipped)
                        : T("已从 {0} 导出并导入电脑书库 {1} 本书，跳过 {2} 个重复或已有文件。", device.Name, imported, skipped)
                : skipped == 0
                    ? T("导出完成：成功 {0} 本，失败 {1} 本。", imported, failed)
                    : T("导出完成：成功 {0} 本，跳过 {1} 个重复或已有文件，失败 {2} 本。", imported, skipped, failed);
            DevicePageStatusText.Text = completionMessage;
            acceptProgressUpdates = false;
            ShowTransferToast(T("导出到电脑书库"), completionMessage, progress: 100, autoHide: true, completed: true);
            if (failed > 0)
            {
                if (calibreMissing)
                {
                    await ShowMessageAsync(
                        T("未安装 Calibre"),
                        T("未安装 Calibre，无法完成此导出。请先安装 Calibre，或在设置中指定 ebook-convert.exe 后重试。"));
                }
                else
                {
                    var message = T("成功 {0} 本，失败 {1} 本。", imported, failed);
                    var details = failureDetails
                        .Where(detail => !string.IsNullOrWhiteSpace(detail))
                        .Distinct(StringComparer.CurrentCulture)
                        .Take(3)
                        .ToArray();
                    if (details.Length > 0)
                        message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details);
                    await ShowMessageAsync(T("导出到电脑书库失败"), message);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            acceptProgressUpdates = false;
            DevicePageStatusText.Text = T("导出已取消。");
        }
        finally
        {
            acceptProgressUpdates = false;
            HideTaskProgressPopup();
            HideTransferToast();
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsCalibreMissingMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("未找到 Calibre", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Calibre converter was not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Calibre was not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Calibre not found", StringComparison.OrdinalIgnoreCase));

    private async void DeleteSelectedDeviceBooksButton_Click(object? sender, RoutedEventArgs e)
        => await DeleteSelectedDeviceBooksAsync();

    private async Task DeleteSelectedDeviceBooksAsync() =>
        await TrackDeviceOperationAsync(DeleteSelectedDeviceBooksCoreAsync);

    private async Task WaitForDeviceBookReadsAsync(KindleDevice device)
    {
        // Initial connection publishes cards before metadata enrichment finishes.
        // Drain those reads before deleting so their final snapshot cannot put a
        // deleted card back, and MTP does not read and delete simultaneously.
        if (_deviceWarmTask is { IsCompleted: false } warm) await warm;
        if (_deviceBookScanTask is { IsCompleted: false } scan) await scan;
        await EnsureCurrentDeviceAsync(device);
    }

    private async Task DeleteSelectedDeviceBooksCoreAsync()
    {
        await DeleteSelectedDeviceBooksCoreAsync(GetSelectedDeviceBooks());
    }

    private async Task DeleteSelectedDeviceBooksCoreAsync(IReadOnlyList<KindleBookCardViewModel> selected)
    {
        if (_deviceBooksShowingCache || selected.Count == 0 || _kindle is null || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync(T("从设备删除书籍"), T("确定从 {0} 删除选中的 {1} 本书吗？电脑书库不受影响。", device.Name, selected.Count))) return;

        var removed = 0;
        ShowTransferToast(T("从设备删除书籍"), T("正在删除 {0} 本书…", selected.Count), progress: 0);
        string? firstFailure = null;
        for (var index = 0; index < selected.Count; index++)
        {
            var card = selected[index];
            try
            {
                await WaitForDeviceBookReadsAsync(device);
                await RecordResourceActionAsync(T("从设备删除书籍"), card.Title, token => _kindle.RemoveBookAsync(device, card.Book, token), _lifetimeCancellation.Token);
                DeviceBooks.Remove(card);
                _deviceCardsByPath.Remove(NormalizeDeviceBookPath(card.Book.RelativePath));
                _libraryPresenceComparison = null;
                card.Dispose();
                removed++;
                DeviceBookCountText.Text = DeviceBooks.Count.ToString();
                ShowTransferToast(T("从设备删除书籍"), T("正在删除（{0}/{1}）…", index + 1, selected.Count), progress: (index + 1) * 100 / selected.Count);
            }
            catch (Exception exception)
            {
                DevicePageStatusText.Text = T("《{0}》删除失败：{1}", card.Title, UiText.Localize(exception.Message));
                firstFailure ??= UiText.Localize(exception.Message);
            }
        }
        RefreshLibraryPresenceState();
        UpdateDeviceBookSelectionUi();
        var completionMessage = T("已从设备删除 {0} 本书。", removed);
        DevicePageStatusText.Text = completionMessage;
        ShowTransferToast(T("从设备删除书籍"), completionMessage, progress: 100, autoHide: true, completed: true);
        ClearDeviceBookSelection();
        if (firstFailure is not null)
            await ShowMessageAsync(T("无法从设备删除"), UiText.Localize(firstFailure));
    }

    private async void DeleteDeviceBookButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KindleBookCardViewModel card }
            || _deviceBooksShowingCache
            || _kindle is null
            || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync(T("从设备删除书籍"), T("确定从 {0} 删除《{1}》吗？电脑书库中的文件不会受影响。", device.Name, card.Title))) return;
        await TrackDeviceOperationAsync(async () =>
        {
            ShowTransferToast(T("从设备删除书籍"), T("正在删除（{0}/{1}）…", 1, 1), progress: 0);
            try
            {
                await WaitForDeviceBookReadsAsync(device);
                await RecordResourceActionAsync(T("从设备删除书籍"), card.Title, token => _kindle.RemoveBookAsync(device, card.Book, token), _lifetimeCancellation.Token);
                DeviceBooks.Remove(card);
                _deviceCardsByPath.Remove(NormalizeDeviceBookPath(card.Book.RelativePath));
                _libraryPresenceComparison = null;
                card.Dispose();
                DeviceBookCountText.Text = DeviceBooks.Count.ToString();
                RefreshLibraryPresenceState();
                UpdateDeviceBookSelectionUi();
                DevicePageStatusText.Text = T("已从设备删除《{0}》。", card.Title);
                ShowTransferToast(T("从设备删除书籍"), T("已从设备删除《{0}》。", card.Title), progress: 100, autoHide: true, completed: true);
                ClearDeviceBookSelection();
            }
            catch (Exception exception)
            {
                HideTransferToast();
                DevicePageStatusText.Text = T("删除失败：{0}", UiText.Localize(exception.Message));
            }
        });
    }
}
