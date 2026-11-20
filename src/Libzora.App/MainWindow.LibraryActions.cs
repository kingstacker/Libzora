using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Infrastructure.Plugins;

namespace Libzora;

/// <summary>
/// Drag and drop, opening, deleting and renaming books.
/// </summary>
public partial class MainWindow
{
    private void LibraryPane_DragOver(object? sender, DragEventArgs e)
    {
        // This handler is installed on the window so Linux file managers can
        // reach the native XDND target. Do not let it handle other pages: in
        // particular, doing so would overwrite the Kindle resource page's
        // Copy effect with None.
        if (!LibraryWorkspace.IsVisible) return;

        _dropOverlayHideTimer?.Stop();
        // X11/Wayland drag payloads are often delivered lazily. Reading the
        // paths during DragOver can return an empty list for directories and
        // advertising None prevents the file manager from ever sending Drop.
        // The advertised File format is enough here; materialize paths only
        // after the drop is accepted.
        var hasStorageItems = LibraryDropImportPolicy.CanAccept(e.DataTransfer);
        e.DragEffects = hasStorageItems ? DragDropEffects.Copy : DragDropEffects.None;
        LibraryDropOverlay.IsVisible = hasStorageItems;
        e.Handled = true;
    }

    private void LibraryPane_DragLeave(object? sender, RoutedEventArgs e)
    {
        if (!LibraryWorkspace.IsVisible) return;

        // Crossing child element boundaries fires DragLeave immediately
        // followed by DragOver again; hiding instantly makes the overlay
        // flicker while the drag moves across the book grid. Defer the hide
        // and cancel it if another DragOver arrives within the delay.
        _dropOverlayHideTimer ??= CreateDropOverlayHideTimer();
        _dropOverlayHideTimer.Stop();
        _dropOverlayHideTimer.Start();
        e.Handled = true;
    }

    private DispatcherTimer CreateDropOverlayHideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            LibraryDropOverlay.IsVisible = false;
        };
        return timer;
    }

    private void LibraryRoot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!LibraryDetailPane.IsVisible
            || !e.GetCurrentPoint(LibraryRoot).Properties.IsLeftButtonPressed
            || IsSourceWithin(e.Source, LibraryDetailPane)
            || IsSourceWithin(e.Source, LibraryToolbar)
            || IsSourceWithin(e.Source, LibraryHeader)
            || IsSourceWithin(e.Source, FilterPanel)
            || IsBookCardSource(e.Source))
            return;

        ClearSelectedBook();
    }

    private static bool IsSourceWithin(object? source, Visual container)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, container)) return true;
        }

        return false;
    }

    private static bool IsBookCardSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if ((current is Grid || current is Border)
                && current is Control control
                && control.DataContext is BookCardViewModel)
                return true;
        }

        return false;
    }

    private static bool IsScrollBarSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is ScrollBar)
                return true;
        }

        return false;
    }

    private async void LibraryPane_Drop(object? sender, DragEventArgs e)
    {
        if (!LibraryWorkspace.IsVisible) return;

        _dropOverlayHideTimer?.Stop();
        var paths = LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer);
        LibraryDropOverlay.IsVisible = false;
        e.Handled = true;
        if (paths.Length > 0)
            await ImportPathsAsync(paths);
        else
            SetTaskStatus(T("无法读取拖入的文件或文件夹路径。"));
    }

    private async Task<string?> EnsureBookFileAvailableAsync(
        BookFile file,
        CancellationToken cancellationToken)
    {
        var path = ViewModel.GetAbsoluteFilePath(file);
        if (File.Exists(path)) return path;

        var settings = _s3SyncStoredSettings.Settings;
        if (!settings.IsConfigured || !settings.HasChosenCustomProvider || !_appSettings.NetworkEnabled)
            return null;

        var progress = new Progress<string>(message =>
        {
            void Apply() => SetTaskStatus(UiText.Localize(message));
            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        });
        var downloadedPath = await _s3SyncService.EnsureBookFileDownloadedAsync(
            _s3SyncStoredSettings.DeviceId,
            settings,
            file,
            progress,
            cancellationToken);
        if (File.Exists(downloadedPath))
            await RefreshBookSyncStatusesAsync(cancellationToken);
        return downloadedPath;
    }

    private async void BookSyncStatusIcon_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCardViewModel card }) return;

        e.Handled = true;
        try
        {
            switch (card.SyncStatus)
            {
                case BookSyncStatus.NotDownloaded:
                    await DownloadBookFileOnDemandAsync(card);
                    break;
                case BookSyncStatus.NotSynced:
                    SetTaskStatus(T("正在上传《{0}》…", card.Title));
                    await RunS3SyncAsync(silent: false);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("书籍同步失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private async Task DownloadBookFileOnDemandAsync(BookCardViewModel card)
    {
        var file = ReaderBookSelectionPolicy.SelectPreferred(
            card.Book.Files,
            _appSettings.PreferredOpenFormat);
        if (file is null)
        {
            SetTaskStatus(T("《{0}》没有可下载的书籍文件。", card.Title));
            return;
        }

        SetTaskStatus(T("正在下载《{0}》…", card.Title));
        var path = await EnsureBookFileAvailableAsync(file, _lifetimeCancellation.Token);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            SetTaskStatus(T("无法下载《{0}》：请检查云端同步配置和网络连接。", card.Title));
    }

    private int _bookOpenInProgress;

    private async Task OpenBookAsync(
        BookCardViewModel card,
        BookFile? requestedFile = null,
        bool restoreProgress = true)
    {
        if (_dataRootMigrationInProgress)
        {
            SetTaskStatus(T("数据目录迁移正在进行，请稍后再打开书籍。"));
            return;
        }
        // A remote reset can be applied by a sync that started before this
        // click. Wait before loading any checkpoint into the reader session.
        while (_s3SyncBusy)
        {
            if (_s3SyncCompletion is null) return;
            SetTaskStatus(T("正在等待云端同步完成…"));
            try { await WaitForS3SyncAsync(_lifetimeCancellation.Token); }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
        }
        if (_backupImportInProgress || _readerCloseInProgress != 0 || _bookOpenInProgress > 0)
        {
            SetTaskStatus(T("正在处理书籍或导入备份，请稍后再打开书籍。"));
            return;
        }
        if (_readingDataResetBusy)
        {
            SetTaskStatus(T("阅读数据正在重置，请稍后再打开书籍。"));
            return;
        }
        _bookOpenInProgress++;
        StartReaderOpenTransition(card, requestedFile);
        try
        {
            await OpenBookCoreAsync(card, requestedFile, restoreProgress);
            if (_openReaderInZenMode && ReaderRoot.IsVisible && !_readerZenMode)
            {
                _openReaderInZenMode = false;
                ToggleReaderZenMode();
            }
        }
        finally
        {
            await CompleteReaderOpenTransitionAsync();
            _bookOpenInProgress--;
        }
    }

    private async Task OpenBookCoreAsync(
        BookCardViewModel card,
        BookFile? requestedFile = null,
        bool restoreProgress = true)
    {
        var file = requestedFile ?? ReaderBookSelectionPolicy.SelectPreferred(
            card.Book.Files,
            _appSettings.PreferredOpenFormat);
        if (file is null)
        {
            ResetReaderOpenTransition();
            SetTaskStatus(T("这本书没有可打开的文件。"));
            await ShowMessageAsync(T("无法打开书籍"), T("所选格式文件不存在或已不再支持。"));
            return;
        }

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
                ResetReaderOpenTransition();
                var error = UiText.Localize(exception.Message);
                SetTaskStatus(T("下载书籍失败：{0}", error));
                await ShowMessageAsync(T("无法打开书籍"), error);
                return;
            }

            if (!File.Exists(path))
            {
                ResetReaderOpenTransition();
                SetTaskStatus(T("找不到文件：{0}", file.RelativePath));
                await ShowMessageAsync(T("无法打开书籍"), T("所选格式文件不存在或已被删除。"));
                return;
            }
        }

        if (string.Equals(file.Format, "epub", StringComparison.OrdinalIgnoreCase))
        {
            await OpenEpubReaderAsync(card, file, path, restoreProgress);
            return;
        }

        if (string.Equals(file.Format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            await OpenPdfReaderAsync(card, file, path);
            return;
        }

        if (file.Format.Equals("mobi", StringComparison.OrdinalIgnoreCase)
            || file.Format.Equals("azw3", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                SetTaskStatus(T("正在准备《{0}》的阅读缓存…", card.Title));
                var sourceHash = file.Sha256;
                if (sourceHash.Length != 64)
                    sourceHash = await Hashing.Sha256Async(path, _lifetimeCancellation.Token);
                var cache = await _readerFormatCache.PrepareEpubAsync(
                    path,
                    sourceHash,
                    file.Format,
                    _lifetimeCancellation.Token);
                await OpenEpubReaderAsync(card, file, cache.EpubPath, restoreProgress);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ResetReaderOpenTransition();
                SoftwareLog.ErrorWithoutMessage("reader.open", exception, ("format", file.Format));
                SetTaskStatus(T("准备阅读缓存失败：{0}", UiText.Localize(exception.Message)));
                await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
            }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetTaskStatus(T("已用系统默认程序打开《{0}》。", card.Title));
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("reader.open", exception, ("format", file.Format));
            SetTaskStatus(T("打开文件失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task DeleteSelectedBookAsync()
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除书籍"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }
        var choice = await ConfirmBookRemovalAsync(T("《{0}》将从书库移除并移入回收站，可恢复。", card.Title));
        if (choice == BookRemovalChoice.Cancel) return;

        try
        {
            await ViewModel.DeleteBookAsync(card.Book, _lifetimeCancellation.Token);
            await RefreshLibraryMatchRecordsAsync(_lifetimeCancellation.Token);
            ClearSelectedBook();
            ClearBookMultiSelection();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            var completionMessage = ViewModel.StatusText;
            SetTaskStatus(completionMessage);
            ShowSettingsCapsule(completionMessage, 2400, success: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task RenameBookFromContextAsync(BookCardViewModel card)
    {
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法重命名书籍"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }

        if (_conversionInProgress
            || _automaticReaderFormatGenerationInProgress)
        {
            await ShowMessageAsync(T("无法重命名书籍"), T("当前有书籍任务正在进行，请完成后再重命名。"));
            return;
        }

        var previousTitle = card.Book.Title;
        var name = await PromptCollectionNameAsync(
            previousTitle,
            T("重命名书籍"),
            T("请输入新的书名。"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var normalizedName = name.Trim();
        try
        {
            await _library.RenameBookAsync(
                card.Book.Id,
                normalizedName,
                _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(T("已将《{0}》重命名为《{1}》。", previousTitle, normalizedName));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("重命名书籍失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法重命名书籍"), UiText.Localize(exception.Message));
        }
    }

    private Task OpenBookDirectoryFromContextAsync(BookCardViewModel card)
    {
        try
        {
            var localFilePath = card.Book.Files
                .Select(ViewModel.GetAbsoluteFilePath)
                .FirstOrDefault(File.Exists);
            var directory = localFilePath is not null
                ? Path.GetDirectoryName(localFilePath)
                : Path.Combine(_paths.Library, card.Book.Id.ToString("N"));
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                SetTaskStatus(T("书籍文件尚未下载，无法打开所在目录。"));
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            SetTaskStatus(T("已打开《{0}》所在目录。", card.Title));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("打开书籍所在目录失败：{0}", UiText.Localize(exception.Message)));
        }

        return Task.CompletedTask;
    }

    private async Task DeleteFileAsync(BookFile file)
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
        if (card.Book.Files.Count == 1 && card.Book.Files[0].Id == file.Id)
        {
            await DeleteBookFromContextAsync(card);
            return;
        }
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除格式"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }
        if (!await ConfirmAsync(T("删除文件"), T("确定将《{0}》的 {1} 文件移入回收站吗？可在回收站恢复，其他格式不受影响。", card.Title, file.Format.ToUpperInvariant()), T("删除文件"))) return;

        try
        {
            await ViewModel.DeleteFileAsync(card.Book, file, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(ViewModel.StatusText);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除文件失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除格式"), UiText.Localize(exception.Message));
        }
    }

    private IReadOnlyList<BookCardViewModel> GetSelectedCards()
    {
        var selected = ViewModel.Books
            .Where(card => _selectedBookIds.Contains(card.Book.Id))
            .ToArray();
        return selected.Length > 0 || _selectedCard is null
            ? selected
            : [_selectedCard];
    }

    private void UpdateMultiSelectionUi()
    {
        SyncCardSelectionVisuals();
        var selectedCount = GetSelectedCards().Count;
        // Batch actions need the shelf's full width, especially at 1024px.
        if (selectedCount > 1 && LibraryDetailPane.IsVisible && _selectedCard is not null)
            ClearSelectedBook();
        MultiSelectionBar.IsVisible = selectedCount > 1 && !_isTransferring;
        MultiSelectionText.Text = selectedCount > 0 ? T("已选择 {0} 本书", selectedCount) : string.Empty;
    }

    private void SyncCardSelectionVisuals()
    {
        // 单点选中的书显示黑边；只有真正多选（≥2 本）时才附带 ✓ 徽标，并抑制悬停提示。
        // 框选过程中不显示封面阅读按钮，避免只圈住一本书时闪烁遮罩。
        var multi = _selectedBookIds.Count > 1;
        var suppressReadAction = _rubberBandSelecting;
        var suppressTooltips = multi || _rubberBandSelecting;
        foreach (var card in ViewModel.Books)
        {
            var selected = _selectedBookIds.Contains(card.Book.Id)
                || card.SeriesMembers.Any(member => _selectedBookIds.Contains(member.Book.Id));
            card.IsSelected = selected;
            card.IsSoleSelected = selected && !multi && !suppressReadAction;
            card.IsReadActionVisible = card.IsSoleSelected
                && LibraryDetailPane.IsVisible && _detailPaneBookId == card.Book.Id;
            card.IsMultiSelected = selected && multi;
            card.IsTooltipEnabled = !suppressTooltips;
        }
        if (suppressTooltips)
            CloseOpenToolTips(BookGrid, BookList);
    }

    private static void CloseOpenToolTips(params Visual[] roots)
    {
        foreach (var root in roots)
        {
            foreach (var control in root.GetVisualDescendants().OfType<Control>())
            {
                if (ToolTip.GetIsOpen(control))
                    ToolTip.SetIsOpen(control, false);
            }
        }
    }
}
