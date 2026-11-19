using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Local change tracking and sync lifecycle.
/// </summary>
public partial class MainWindow
{
    private void LocalLibraryDataChanged(object? sender, LocalDataChangedEventArgs e) =>
        HandleLocalDataChanged(e.Kind);

    private void LocalReaderDataChanged(object? sender, LocalDataChangedEventArgs e) =>
        HandleLocalDataChanged(e.Kind);

    private void HandleLocalDataChanged(LocalDataChangeKind kind)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleLocalDataChanged(kind));
            return;
        }

        if (_lifetimeCancellation.IsCancellationRequested) return;
        _widgetSnapshots?.Schedule();
        _lastInProcessDataChangeUtc = DateTime.UtcNow;
        _localSyncChanges.MarkChanged();
        _s3DeletionConfirmationPending = false;
        _s3SyncCancelledByUser = false;
        if (kind == LocalDataChangeKind.Library) BroadcastLibraryChanged();
        else BroadcastReadingChanged(kind);
        if (kind is LocalDataChangeKind.Library or LocalDataChangeKind.BookReflection)
        {
            MarkReadingMaterialsDirty();
            if (kind == LocalDataChangeKind.BookReflection
                && LibraryDetailPane.IsVisible
                && _selectedCard is not null)
                _ = RefreshBookReflectionDetailsAsync(_selectedCard.Book.Id);
        }
        if (kind == LocalDataChangeKind.Library)
        {            _ = RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
        }
        if (!_s3SyncBusy && _s3SyncStoredSettings.Settings is { Enabled: true, IsConfigured: true, HasChosenCustomProvider: true })
            UpdateS3SyncIndicator(S3SyncIndicatorState.Pending);
        if (!IsAutomaticS3SyncReady())
        {
            _s3LocalChangeSyncTimer.Stop();
            return;
        }

        _localSyncChanges.ResetRetry();
        ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce, resetRetry: false);
    }

    private bool HasPendingS3LocalChanges =>
        _localSyncChanges.HasPendingChanges;

    private bool IsAutomaticS3SyncReady()
    {
        return _s3SyncStoredSettings.Settings.HasChosenCustomProvider
            && AutomaticSyncCoordinator.IsReady(_s3SyncStoredSettings.Settings,
            _stage3Ready, _appSettings.NetworkEnabled,
            _backupBusy || _readingDataResetBusy || _s3SyncExitInProgress,
            _s3DeletionConfirmationPending, _s3SyncCancelledByUser);
    }

    private void ScheduleS3LocalChangeSync(TimeSpan delay, bool resetRetry = true)
    {
        if (!HasPendingS3LocalChanges || !IsAutomaticS3SyncReady())
        {
            _s3LocalChangeSyncTimer.Stop();
            return;
        }

        if (resetRetry)
            _localSyncChanges.ResetRetry();
        _s3LocalChangeSyncTimer.Stop();
        _s3LocalChangeSyncTimer.Interval = delay;
        _s3LocalChangeSyncTimer.Start();
    }

    private TimeSpan GetNextS3LocalChangeRetryDelay() => _localSyncChanges.NextRetryDelay();

    private async Task RunPendingS3LocalChangeSyncAsync()
    {
        _s3LocalChangeSyncTimer.Stop();
        if (!HasPendingS3LocalChanges || !IsAutomaticS3SyncReady()) return;

        var succeeded = await RunS3SyncAsync(silent: true);
        if (succeeded)
        {
            _localSyncChanges.ResetRetry();
            if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
                ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce, resetRetry: false);
            return;
        }

        if (HasPendingS3LocalChanges && IsAutomaticS3SyncReady())
        {
            // A new local write may have scheduled its own short debounce
            // while the previous sync was in flight. Keep that earlier retry
            // instead of replacing it with the backoff for the old failure.
            if (_s3LocalChangeSyncTimer.IsEnabled)
                return;
            if (!_s3SyncBusy && _s3SyncIndicatorState is not (S3SyncIndicatorState.Failed or S3SyncIndicatorState.Warning))
                UpdateS3SyncIndicator(S3SyncIndicatorState.Pending);
            ScheduleS3LocalChangeSync(GetNextS3LocalChangeRetryDelay(), resetRetry: false);
        }
    }

    private async Task RunLifecycleS3SyncAsync(bool waitForExistingSync = false, CancellationToken cancellationToken = default)
    {
        if (!_stage3Ready)
            return;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
        var token = linkedCancellation.Token;
        try
        {
            if (!_appSettings.NetworkEnabled)
            {
                if (waitForExistingSync) await WaitForS3SyncAsync(token);
                return;
            }

            _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(token);
            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.Enabled || !settings.AutomaticSyncEnabled || !settings.IsConfigured
                || _s3DeletionConfirmationPending || _s3SyncCancelledByUser)
            {
                if (waitForExistingSync) await WaitForS3SyncAsync(token);
                return;
            }

            // A local write can land while a startup/exit checkpoint is
            // running. Give that write another checkpoint before continuing;
            // the cap prevents a busy shutdown from waiting forever if some
            // background activity keeps producing updates.
            await AutomaticSyncCoordinator.RunCheckpointsAsync(
                syncToken => RunS3SyncAsync(silent: true, syncToken),
                () => HasPendingS3LocalChanges, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("sync.auto", exception);
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("自动 云端同步失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
    }

    private async Task WaitForS3SyncAsync(CancellationToken cancellationToken = default)
    {
        if (_s3SyncCompletion is { } completion)
        {
            using var registration = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => _s3SyncCancellation?.Cancel()));
            await completion.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteWindowCloseAfterS3SyncAsync()
    {
        try
        {
            _s3LocalChangeSyncTimer.Stop();
            if (!await PrepareSettingsForExitAsync()) return;
            SettingsPage.IsEnabled = false;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            _s3SyncExitCancellation = cancellation;
            try
            {
                if (!_skipS3SyncOnExit)
                {
                    if (_s3SyncBusy || IsAutomaticS3SyncReady())
                        SetS3SyncStatus(T("正在退出前同步…"), silent: false);
                    await RunLifecycleS3SyncAsync(waitForExistingSync: true, cancellation.Token).WaitAsync(cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _s3SyncCancellation?.Cancel();
            }
            // The close flag is set only after all pending and in-flight
            // settings writes have completed successfully.
            if (!await FlushAppSettingsAsync()) return;
            _allowWindowCloseForS3Sync = true;
            Close();
        }
        catch (Exception exception)
        {
            ShowSettingsCapsule(T("退出前保存失败：{0}", UiText.Localize(exception.Message)), 4000);
        }
        finally
        {
            _s3SyncExitCancellation = null;
            if (!_allowWindowCloseForS3Sync)
            {
                SettingsPage.IsEnabled = true;
                _s3SyncExitInProgress = false;
                if (IsAutomaticS3SyncReady()) ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce);
            }
        }
    }

    private async Task RunAutomaticS3SyncIfDueAsync()
    {
        if (_s3SyncBusy || !IsAutomaticS3SyncReady()) return;
        try
        {
            var settings = _s3SyncStoredSettings.Settings;
            if (!settings.Enabled || !settings.AutomaticSyncEnabled) return;
            if (!AutomaticSyncCoordinator.IsDue(HasPendingS3LocalChanges,
                    _lastS3SyncAt, DateTimeOffset.UtcNow, settings.IntervalMinutes))
                return;
            await RunS3SyncAsync(silent: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("sync.auto", exception);
            var error = UiText.Localize(exception.Message);
            S3SyncStatusText.Text = T("自动 云端同步失败：{0}", error);
            UpdateS3SyncIndicator(S3SyncIndicatorState.Failed, error);
        }
    }
}
