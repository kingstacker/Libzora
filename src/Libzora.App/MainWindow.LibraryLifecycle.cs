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
/// Library initialisation, window lifecycle and cross-process database
/// watching.
/// </summary>
public partial class MainWindow
{
    public Task InitializeLibraryAsync() => _libraryInitialization ??= InitializeLibraryCoreAsync();

    /// <summary>
    /// Imports files opened from Finder, a second launch, or the command line.
    /// The window and library may still be starting, so the import waits for
    /// the initial library pass before expanding and staging the files.
    /// </summary>
    public async Task OpenExternalFilesAsync(
        IEnumerable<string> paths,
        Task? libraryInitialization = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _ = OpenExternalFilesAsync(paths, libraryInitialization));
            return;
        }

        var files = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return;

        // Do not call Show() during first-launch startup before the main
        // window has ever been displayed; the lifetime shows it right after
        // framework initialization. A loaded-but-hidden window (the macOS
        // menu-bar state) still needs to come back.
        if (IsLoaded) RestoreFromTray();
        if (LibraryWorkspace.IsVisible == false && IsLoaded) ShowLibraryPage();

        var initialization = libraryInitialization ?? _libraryInitialization;
        if (initialization is not null)
        {
            try
            {
                await initialization;
            }
            catch
            {
                // Initialization reports its own failure; an external open is
                // still attempted so the user sees the actionable error.
            }
        }

        await ImportPathsAsync(files);
    }

    private async Task InitializeLibraryCoreAsync()
    {
        try
        {
            SetTaskStatus(T("正在准备本地书库…"));
            await _library.InitializeAsync(_lifetimeCancellation.Token);

            // Publish the first page of books as soon as the database is ready.
            // Stage 3 startup (S3 settings, Calibre detection, managed resource
            // lists, automatic backup and device enumeration) does not affect
            // the shelf and used to delay it by seconds.
            SetTaskStatus(T("正在加载本地书架…"));
            await ViewModel.RefreshAsync(
                _lifetimeCancellation.Token,
                progress => SetTaskStatus(T(progress)));
            SetupFilterControls();
            SetTaskStatus(T("正在加载书籍分组…"));
            await RefreshCollectionsAsync();
            _filterControlsReady = true;
            UpdateLibraryUi();            _widgetSnapshots?.Schedule();
            SetTaskStatus(ViewModel.StatusText);
            ShowOnboardingIfNeeded();
            StartAutomaticUpdateCheck();

            SetTaskStatus(T("正在初始化设备与设置…"));
            await InitializeStage3Async(_lifetimeCancellation.Token);
            // The shelf is already populated; only the data that depends on
            // Stage 3 settings (match records and sync badges) needs a pass.
            await RefreshLibraryMatchRecordsAsync(_lifetimeCancellation.Token);
            await RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
            InitializeCrossProcessDatabaseWatcher();
            UpdateLibraryUi();
            SetTaskStatus(ViewModel.StatusText);
            if (Environment.GetEnvironmentVariable("LIBZORA_SEND_DIAG") == "1" && ViewModel.Books.Count > 0)
            {
                _selectedCard = ViewModel.Books[0];
                _ = RunSendDiagnosticAsync();
            }
#if DEBUG
            if (Environment.GetEnvironmentVariable("LIBZORA_AI_PREVIEW") == "1")
            {
                _ = Dispatcher.UIThread.InvokeAsync(RunReaderAiPreviewAsync);
            }
            if (Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE") == "1")
            {
                _ = Dispatcher.UIThread.InvokeAsync(RunKreaderValidationAndExitAsync);
            }
            if (Environment.GetEnvironmentVariable("LIBZORA_ANIMATION_PROBE") == "1")
            {
                _ = Dispatcher.UIThread.InvokeAsync(RunKreaderAnimationProbeAndExitAsync);
            }
#endif
            if (int.TryParse(Environment.GetEnvironmentVariable("LIBZORA_OPEN_BOOK_INDEX"), out var openIndex)
                && openIndex >= 0 && openIndex < ViewModel.Books.Count)
            {
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await OpenBookAsync(ViewModel.Books[openIndex], restoreProgress: false);
                    if (int.TryParse(Environment.GetEnvironmentVariable("LIBZORA_OPEN_CHAPTER_STEPS"), out var steps))
                    {
                        for (var i = 0; i < steps; i++)
                        {
                            await Task.Delay(2500);
                            await MoveReaderChapterAsync(1);
                        }
                    }
                    await Task.Delay(2000);
                    try
                    {
                        var dom = CurrentReaderHost is null
                            ? "<no host>"
                            : await CurrentReaderHost.InvokeScriptAsync(
                                "(() => JSON.stringify({t:(document.body?.innerText||'').slice(0,120), imgs:document.images.length, imgOk:[...document.images].map(i=>i.naturalWidth).join(','), src:[...document.images].map(i=>i.currentSrc||i.src).join('|').slice(0,300)}))();");
                        System.IO.File.WriteAllText("/tmp/reader-state.txt",
                            $"chapterIndex={_readerChapterIndex}\nstatus={ReaderStatusText.Text}\nsource={CurrentReaderHost?.Source}\ndom={dom}\n");
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.WriteAllText("/tmp/reader-state.txt", "probe failed: " + ex);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("library.initialize", exception);
            SetTaskStatus(T("无法读取本地书库：{0}", UiText.Localize(exception.Message)));
            EmptyLibraryTitleText.Text = T("本地书库暂时不可用");
            EmptyLibraryMessageText.Text = T("请检查数据目录后重启 Libzora。");
            EmptyLibraryState.IsVisible = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (change.NewValue is WindowState newState)
            {
                if (change.OldValue is WindowState.Minimized
                    && newState != WindowState.Minimized)
                {
                    BeginWindowRestoreAnimation();
                }
                else if (newState == WindowState.Minimized
                         && !_windowMinimizeAnimationInProgress)
                {
                    // A platform-initiated minimize has already crossed the
                    // native boundary by the time this notification arrives.
                    // Keep the next taskbar restore consistent with the
                    // button-initiated path.
                    CancelWindowStateAnimation();
                    Opacity = 0;
                }
            }
            if (MaximizeWindowGlyph is not null
                && MaximizeWindowButton is not null)
            {
                UpdateMaximizeGlyph();
                UpdateWindowShadowMargin();
            }
            if (_readerZenMode)
            {
                // Re-apply after the platform has processed the FullScreen or
                // Maximized transition; Windows may otherwise restore a native
                // caption row one layout pass later.
                ApplyReaderZenWindowChrome();
                Dispatcher.UIThread.Post(
                    ApplyReaderZenWindowChrome,
                    DispatcherPriority.Render);
            }
        }
    }

    // Keep content and its frame flush with the client area in every state.
    // Transparent gutters are rendered as opaque white on some Windows/DPI
    // combinations; resize hit targets are overlays and need no reserved space.
    private void UpdateWindowShadowMargin()
    {
        if (WindowShadowHost is null) return;
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        WindowShadowHost.Margin = new Thickness(0);
        if (WindowFrameOverlay is not null)
            WindowFrameOverlay.Margin = new Thickness(0);
        if (WindowResizeLayer is not null)
            WindowResizeLayer.IsVisible = !maximized;
    }

    // Let the platform own the resize gesture where it can. Avalonia's macOS
    // backend does not implement BeginResizeDrag, so the helper falls back to
    // updating the frame from pointer deltas there.
    private WindowEdgeResizer? _windowEdgeResizer;
    private WindowEdgeResizer WindowResizer => _windowEdgeResizer ??= new WindowEdgeResizer(this);

    private void WindowResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string edge } source
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (!WindowResizer.TryBegin(source, edge, e))
            return;

        e.Handled = true;
    }

    // Native close requests (including Alt+F4) return from Kreader to the
    // library. On Windows a subsequent native close on the library exits the
    // application; on macOS the red button hides into the menu bar instead.
    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_backupImportInProgress)
        {
            e.Cancel = true;
            SetTaskStatus(T("备份正在导入，请等待完成后再退出。"));
            return;
        }
        if (_allowWindowCloseForPendingUpdate || _allowWindowCloseForS3Sync
            || _allowWindowCloseForDataRootSwitch)
        {
            DisposeDeviceChangeNotifier();
            return;
        }
        if (OperatingSystem.IsMacOS() && ReaderRoot.IsVisible
            && e.CloseReason == WindowCloseReason.ApplicationShutdown)
        {
            e.Cancel = true;
            _ = QuitMacOSAfterReaderAsync();
            return;
        }
        if (ReaderRoot.IsVisible || Volatile.Read(ref _readerCloseInProgress) != 0)
        {
            e.Cancel = true;
            _ = CloseReaderAsync();
            return;
        }
        if (!_s3SyncExitInProgress && TryGetPendingUpdatePackage(out var packagePath, out var version))
        {
            e.Cancel = true;
            _ = PromptPendingUpdateInstallAsync(packagePath, version, exitRequested: true);
            return;
        }

        // macOS red button: close the window into the menu bar, matching the
        // platform convention for an app that lives in the tray. Quitting
        // (menu bar 退出, Cmd+Q, OS shutdown) still exits through the paths
        // above, including pending updates and exit-time S3 sync.
        if (MacTrafficLights.IsSupported
            && !_applicationShuttingDown
            && !_s3SyncExitInProgress)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_stage3Ready || _appSettingsAutoSaveConfigured)
        {
            e.Cancel = true;
            if (_s3SyncExitInProgress)
            {
                // A repeated close may skip network work, never a pending disk write.
                _skipS3SyncOnExit = true;
                _s3SyncCancellation?.Cancel();
                _s3SyncExitCancellation?.Cancel();
                return;
            }
            _skipS3SyncOnExit = false;
            _s3SyncExitInProgress = true;
            _ = CompleteWindowCloseAfterS3SyncAsync();
        }
        if (!e.Cancel) DisposeDeviceChangeNotifier();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _accountCenterView?.Dispose();
        _backgroundTaskFlyout?.Hide();
        _settingsSearchCancellation?.Cancel();
        _txtImportRulesCompletion?.TrySetResult(null);
        CancelTxtImportPreview();
        _bookReflectionFlyout?.Hide();
        _sendToKindleWindow?.CloseForShutdown();
        _readerToolbarHideTimer?.Stop();
        _readerToolbarLayoutTimer?.Stop();
        CancelWindowStateAnimation();
        _library.DataChanged -= LocalLibraryDataChanged;
        _readerData.DataChanged -= LocalReaderDataChanged;
        _databaseChangeWatcher?.Dispose();
        _databaseChangeWatcher = null;
        _crossProcessDataRefreshTimer?.Stop();
        _s3SyncService.RemoteSettingsApplied -= S3RemoteSettingsApplied;
        _s3LocalChangeSyncTimer.Stop();
        UiText.LanguageChanged -= MainWindowLanguageChanged;
        UiText.LanguageChanged -= TrayLanguageChanged;
        UiText.LanguageChanged -= MacMenuLanguageChanged;
        _stage3Timer.Stop();
        _deviceChangeDebounceTimer.Stop();
        DisposeDeviceChangeNotifier();
        _s3SyncTimer.Stop();
        StopS3SyncIndicatorAnimation();
        StopReaderAiThinkingAnimation();
        StopTaskSpinner();
        _deviceStatusToastTimer.Stop();
        _appSettingsAutoSaveCancellation?.Cancel();
        _appSettingsAutoSaveCancellation?.Dispose();
        _appSettingsAutoSaveCancellation = null;
        _calibreDetectionCancellation?.Cancel();
        _calibreDetectionCancellation?.Dispose();
        _calibreDetectionCancellation = null;
        _conversionCancellation?.Cancel();
        CancelReaderTranslationRequest();
        _transferCancellation?.Cancel();
        _isTransferring = false;
        _messageCompletion?.TrySetResult(true);
        _messageCompletion = null;
        _confirmationCompletion?.TrySetResult(false);
        _confirmationCompletion = null;
        _bookRemovalCompletion?.TrySetResult(BookRemovalChoice.Cancel);
        _bookRemovalCompletion = null;
        _deviceDuplicateCompletion?.TrySetResult(DeviceDuplicateChoice.Cancel);
        _deviceDuplicateCompletion = null;
        _importFormatSelectionCompletion?.TrySetResult(null);
        _importFormatSelectionCompletion = null;
        _importConflictCompletion?.TrySetResult(ImportConflictResolution.Skip);
        _importConflictCompletion = null;
        foreach (var diagnostic in PlatformDiagnostics)
            diagnostic.Dispose();
        PlatformDiagnostics.Clear();
        foreach (var trashItem in LibraryTrashItems)
            trashItem.Dispose();
        LibraryTrashItems.Clear();
        if (_readerDocument is not null || _readerIsPdf)
            await CloseReaderAsync();
        _readerNavigationCancellation?.Cancel();
        _readerSession.Cancel();
        _embeddingModelDownloadCancellation?.Cancel();
        ResetReaderOpenTransition();
        _lifetimeCancellation.Cancel();
        _widgetSnapshots?.Dispose();
        _widgetSnapshots = null;
        if (_embeddingModelDownloadTask is { } embeddingModelDownloadTask)
        {
            try
            {
                await embeddingModelDownloadTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Closing the window must not be held up by a slow or broken
                // model download. Its cancellation token has already fired.
            }
        }
        _librarySearchDebounce?.Cancel();
        _librarySearchDebounce?.Dispose();
        _librarySearchDebounce = null;
        _lifetimeCancellation.Dispose();
        _readerTts.EnvironmentChanged -= ReaderTts_EnvironmentChanged;
        _readerTts.StateChanged -= ReaderTts_StateChanged;
        _readerTts.Dispose();
        _readerNavigationCancellation?.Dispose();
        _readerSession.Dispose();
        _readerActiveHost?.Dispose();
        _readerPreloadHost?.Dispose();
        foreach (var folder in CollectionFolders) folder.Dispose();
        foreach (var item in _allStage3ReadingMaterials) item.Dispose();
        ViewModel.Dispose();
        _modules.Dispose();
    }

    private void InitializeCrossProcessDatabaseWatcher()
    {
        if (_databaseChangeWatcher is not null) return;
        var watcher = new FileSystemWatcher(_paths.Data, "libzora.db*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        watcher.Changed += CrossProcessDatabaseFileChanged;
        watcher.Created += CrossProcessDatabaseFileChanged;
        watcher.Deleted += CrossProcessDatabaseFileChanged;
        watcher.Renamed += CrossProcessDatabaseFileRenamed;
        watcher.EnableRaisingEvents = true;
        _databaseChangeWatcher = watcher;

        _crossProcessDataRefreshTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _crossProcessDataRefreshTimer.Tick -= CrossProcessDataRefreshTimer_Tick;
        _crossProcessDataRefreshTimer.Tick += CrossProcessDataRefreshTimer_Tick;
    }

    private void CrossProcessDatabaseFileChanged(object sender, FileSystemEventArgs e) =>
        ScheduleCrossProcessDatabaseRefresh();

    private void CrossProcessDatabaseFileRenamed(object sender, RenamedEventArgs e) =>
        ScheduleCrossProcessDatabaseRefresh();

    private void ScheduleCrossProcessDatabaseRefresh()
    {
        // Sync writes the database directly, without service DataChanged events.
        // Capture this before posting: the sync may finish before dispatch.
        if (_lifetimeCancellation.IsCancellationRequested || _s3SyncBusy) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_lifetimeCancellation.IsCancellationRequested || _s3SyncBusy) return;
            // A notification near one of our own writes might still be from
            // another process. Always refresh the shelf, but don't turn our
            // own delayed WAL notifications into another sync.
            _crossProcessRefreshNeedsSync |= DateTime.UtcNow - _lastInProcessDataChangeUtc >= TimeSpan.FromSeconds(2);
            _crossProcessDataRefreshTimer?.Stop();
            _crossProcessDataRefreshTimer?.Start();
        }, DispatcherPriority.Background);
    }

    private async void CrossProcessDataRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _crossProcessDataRefreshTimer?.Stop();
        if (_lifetimeCancellation.IsCancellationRequested) return;
        if (_crossProcessRefreshBusy || _s3SyncBusy)
        {
            _crossProcessDataRefreshTimer?.Start();
            return;
        }
        var needsSync = _crossProcessRefreshNeedsSync;
        _crossProcessRefreshNeedsSync = false;
        _crossProcessRefreshBusy = true;
        try
        {
            await RefreshLibraryAsync();
            await RefreshCollectionsAsync();
            if (needsSync) HandleLocalDataChanged(LocalDataChangeKind.Library);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("读取其他进程的书库变更失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            _crossProcessRefreshBusy = false;
        }
    }
}
