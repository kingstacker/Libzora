using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Sync run and post-sync refresh.
/// </summary>
public partial class MainWindow
{
    private async Task<bool> RunS3SyncAsync(bool silent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!silent && (_s3SettingsDirty || _s3SettingsSaving))
        {
            OpenSettingsExpander("Data", SettingsS3Expander);
            SetS3SyncStatus(T("请先保存或放弃 同步配置修改，再执行同步。"), silent: false);
            return false;
        }
        if (!silent && !await FlushAppSettingsAsync()) return false;
        if (_readingDataResetBusy)
        {
            if (!silent) SetS3SyncStatus(T("阅读数据正在重置，请完成后再执行云端同步。"), silent: false);
            return false;
        }
        if (_backupBusy)
        {
            if (!silent) SetS3SyncStatus(T("备份任务正在进行，请完成后再执行 云端同步。"), silent: false);
            return false;
        }
        if (_s3SyncBusy)
        {
            if (_s3SyncCompletion is { } completion)
            {
                await WaitForS3SyncAsync(cancellationToken);
                return await completion.Task;
            }
            return false;
        }
        if (!_appSettings.NetworkEnabled)
        {
            if (!silent)
            {
                var status = T("请先开启“允许网络功能”。");
                S3SyncStatusText.Text = status;
                SetTaskStatus(status);
                UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, status);
            }
            return false;
        }
        if (_readerDocument is not null || _readerIsPdf || _bookOpenInProgress > 0 || _readerCloseInProgress != 0)
        {
            if (!silent) S3SyncStatusText.Text = T("请先返回书库，再执行同步。");
            return false;
        }

        var localChangeVersionAtStart = _localSyncChanges.CaptureVersion();
        var succeeded = false;
        var refreshAfterFailure = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
        var token = cancellation.Token;
        // Claim the sync slot before the first await. Saving/loading settings
        // can yield to the dispatcher; without claiming it here, a manual
        // click and an automatic tick could both start an S3 operation.
        _s3SyncBusy = true;
        _crossProcessDataRefreshTimer?.Stop();
        _s3TestConnectionCancellation?.Cancel();
        _s3SyncCancellation = cancellation;
        if (!silent) _s3SyncCancelledByUser = false;
        _s3SyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetS3SyncControlsBusy(true);
        try
        {
            if (_s3SettingsSaveTask is { IsCompleted: false } saving && !await saving) return false;
            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(token);

            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.HasChosenCustomProvider)
            {
                var confirmationStatus = T("请确认自备 S3 / WebDAV 连接信息并保存后再同步。");
                SetS3SyncStatus(confirmationStatus, silent);
                UpdateS3SyncIndicator(S3SyncIndicatorState.NotConfigured, confirmationStatus);
                return false;
            }
            var validation = settings.Validate();
            if (validation is not null)
            {
                var validationStatus = UiText.Localize(validation);
                SetS3SyncStatus(validationStatus, silent);
                if (!silent)
                {
                    UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, validationStatus);
                }
                else
                {
                    UpdateS3SyncIndicator(S3SyncIndicatorState.NotConfigured, validationStatus);
                }
                return false;
            }

            UpdateS3SyncIndicator(S3SyncIndicatorState.Syncing);
            SetS3SyncStatus(T("正在同步到 {0}…", settings.ProviderName), silent);
            var progress = new Progress<string>(message =>
            {
                if (!ReferenceEquals(_s3SyncCancellation, cancellation) || token.IsCancellationRequested)
                    return;

                void ApplyProgressOnUiThread()
                {
                    if (ReferenceEquals(_s3SyncCancellation, cancellation) && !token.IsCancellationRequested)
                        SetS3SyncStatus(UiText.Localize(message), silent);
                }

                // S3 progress can be reported by an SDK worker thread. Both
                // localization (it resolves Avalonia resources) and the
                // status controls must therefore run on the UI thread.
                if (Dispatcher.UIThread.CheckAccess())
                    ApplyProgressOnUiThread();
                else
                    Dispatcher.UIThread.Post(ApplyProgressOnUiThread);
            });
            refreshAfterFailure = true;
            var result = await SyncDeletionCoordinator.RunAsync(
                (options, syncToken) => _s3SyncService.SyncAsync(
                    _s3SyncStoredSettings.DeviceId, settings, progress, syncToken, options),
                async (exception, syncToken) =>
                {
                    _s3DeletionConfirmationPending = true;
                    _s3LocalChangeSyncTimer.Stop();
                    var message = UiText.Localize(exception.Message);
                    SetS3SyncStatus(message + " " + T("请点击“立即同步”审核并确认删除。"), silent);
                    UpdateS3SyncIndicator(S3SyncIndicatorState.ConfirmationRequired, message);
                    if (silent || _s3SyncExitInProgress || !await ConfirmAsync(
                            T("确认同步删除"), message + "\n\n" + T("继续后，这些删除会同步到其他设备。"),
                            T("同步这些删除"), syncToken))
                        return false;
                    _s3DeletionConfirmationPending = false;
                    UpdateS3SyncIndicator(S3SyncIndicatorState.Syncing);
                    return true;
                }, token);
            if (result is null) return false;
            _s3DeletionConfirmationPending = false;
            _lastS3SyncAt = DateTimeOffset.UtcNow;
            _lastS3SyncResult = result;

            if (result.SettingsApplied > 0 || _s3RemoteSettingsChanged)
                await RefreshSettingsAfterS3SyncAsync(token);

            if (result.Changed)
            {
                await RefreshLibraryAfterS3SyncAsync(token);
            }
            else
            {
                // A local book can become synced without changing any SQLite
                // rows. Refresh just the badges in that case.
                await RefreshBookSyncStatusesAsync(token);
            }
            // Sync applies remote reading data directly to SQLite, so the
            // ReaderDataService change event is not raised. Refresh the
            // visible reader-facing views after every completed sync.
            await RefreshS3AffectedViewsAsync();
            refreshAfterFailure = false;
            var status = result.IsPartial
                ? T("同步未完成：新增 {0} 本书，下载 {1} 个文件；部分数据未能同步，请重试。", result.BooksAdded, result.FilesDownloaded)
                : result.Changed
                ? T("同步完成：新增 {0} 本书，下载 {1} 个文件，应用 {2} 条批注；正文将在打开时按需下载。", result.BooksAdded, result.FilesDownloaded, result.AnnotationsApplied)
                : T("同步完成，当前已是最新。 ");
            if (!string.IsNullOrWhiteSpace(result.Warning)) status += " " + UiText.Localize(result.Warning);
            SetS3SyncStatus(status, silent);
            succeeded = !result.IsPartial;
            if (succeeded)
                _localSyncChanges.MarkSynced(localChangeVersionAtStart);
            // A write that landed while the network operation was running is
            // deliberately left pending and is shown as such. This avoids a
            // misleading green check between the two sync passes.
            UpdateS3SyncIndicator(
                result.IsPartial || !string.IsNullOrWhiteSpace(result.Warning)
                    ? S3SyncIndicatorState.Warning
                    : HasPendingS3LocalChanges
                    ? S3SyncIndicatorState.Pending
                    : S3SyncIndicatorState.Succeeded,
                result.IsPartial || !string.IsNullOrWhiteSpace(result.Warning) ? status : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var status = T("云端同步已取消。");
            SetS3SyncStatus(status, silent);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Warning, status);
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("sync.run", exception, ("silent", silent));
            var error = UiText.Localize(exception.Message);
            var status = T("云端同步失败：{0}", error);
            SetS3SyncStatus(status, silent);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
        finally
        {
            // A failure after the merge (for example an upload cancellation)
            // must not leave the library displaying rows already changed on disk.
            if (refreshAfterFailure && !_lifetimeCancellation.IsCancellationRequested)
            {
                try
                {
                    if (_s3RemoteSettingsChanged)
                        await RefreshSettingsAfterS3SyncAsync(_lifetimeCancellation.Token);
                    await RefreshLibraryAfterS3SyncAsync(_lifetimeCancellation.Token);
                    await RefreshS3AffectedViewsAsync();
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
                catch (Exception exception) { Debug.WriteLine($"S3 library refresh failed: {exception.Message}"); }
            }
            // Cover delayed WAL/checkpoint notifications from our own sync.
            // Real service DataChanged events still advance the pending version.
            _lastInProcessDataChangeUtc = DateTime.UtcNow;
            _s3SyncBusy = false;
            _s3SyncCancellation = null;
            _s3SyncCompletion?.TrySetResult(succeeded);
            _s3SyncCompletion = null;
            SetS3SyncControlsBusy(false);
        }

        return succeeded;
    }

    private async Task RefreshLibraryAfterS3SyncAsync(CancellationToken cancellationToken)
    {
        await RefreshLibraryDataAsync(cancellationToken);
        SetupFilterControls();
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
    }

    private async Task RefreshS3AffectedViewsAsync()
    {
        // S3 merges write the reader database directly, so ReaderDataService
        // does not emit its local change event. Keep both reader-facing
        // collections in sync even when the page was already open.
        MarkReadingMaterialsDirty();
        if (ReadingDashboardPage.IsVisible)
            await RefreshReadingDashboardAsync();
        if (ReadingMaterialsPage.IsVisible)
            await RefreshReadingMaterialsAsync();
        if (LibraryDetailPane.IsVisible && _selectedCard is not null)
            await RefreshBookReflectionDetailsAsync(_selectedCard.Book.Id);
    }

    private async Task RefreshSettingsAfterS3SyncAsync(CancellationToken cancellationToken)
    {
        if (!await FlushAppSettingsAsync()) return;
        var editVersion = _appSettingsEditVersion;
        var previousEmail = _kindleEmailSettings;
        var refreshedSettings = await _appSettingsStore.LoadAsync(cancellationToken);
        var refreshedEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        var refreshedAi = await _aiSettingsStore.LoadAsync(cancellationToken);
        var preserveEmail = !KindleEmailSettingsEqual(ReadKindleEmailDraft(), previousEmail);
        if (_appSettingsEditVersion != editVersion)
        {
            if (!await FlushAppSettingsAsync()) return;
        }
        else _appSettings = refreshedSettings;
        _kindleEmailSettings = refreshedEmail;
        _readerAiSettings = refreshedAi;
        if (Application.Current is App app) app.ApplyLanguage(_appSettings.UiLanguage);
        PopulateSettingsControlsCore(_s3SettingsDirty, preserveEmail);
        ApplyReaderAiSettingsToControls();
        _s3RemoteSettingsChanged = false;
    }

    // Basic settings auto-save (WinUI reference MainWindow.Productivity.cs):
    // any toggle/combo/number/text change schedules a 600 ms debounced save so
    // the user never needs the save button for the basic options.
}
