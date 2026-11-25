using System.Diagnostics;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices.Tts;

/// <summary>
/// Startup tracking and start core.
/// </summary>
public sealed partial class TtsService : IDisposable
{
    public Task StartAsync(
        TtsSettings settings,
        CancellationToken cancellationToken = default)
        => TrackStartupAsync(StartCoreAsync(settings, null, cancellationToken));

    /// <summary>Reads a fixed selection and stops, regardless of chapter continuation settings.</summary>
    public async Task StartSelectionAsync(
        ReaderTtsDocument selection,
        TtsSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.Text)) return;
        await StopAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var selectionSettings = settings.Clone();
        selectionSettings.AutoAdvance = false;
        await TrackStartupAsync(
                StartCoreAsync(selectionSettings, selection, cancellationToken))
            .ConfigureAwait(false);
    }

    private Task TrackStartupAsync(Task startupTask)
    {
        lock (_gate)
        {
            if (!startupTask.IsCompleted)
                _startupTask = startupTask;
        }
        return AwaitStartupAsync(startupTask);
    }

    private async Task AwaitStartupAsync(Task startupTask)
    {
        try
        {
            await startupTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_startupTask, startupTask))
                    _startupTask = null;
            }
        }
    }

    private async Task StartCoreAsync(
        TtsSettings settings,
        ReaderTtsDocument? selection,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = TtsSettings.Normalize(settings);

        CancellationTokenSource startupCancellation;
        int startupGeneration;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_previewInProgress) return;
                if (_loopTask is { IsCompleted: false }
                    || _startCancellation is not null)
                {
                    return;
                }

                _settings = normalized;
                _message = null;
                _segmentIndex = 0;
                _segmentCount = 0;
                _currentSegments = null;
                _currentDocumentKey = null;
                _currentSegmentIndex = -1;
                _requestedSegmentIndex = -1;
                _isSelectionPlayback = selection is not null;
                startupCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _startCancellation = startupCancellation;
                startupGeneration = ++_startGeneration;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        var startupToken = startupCancellation.Token;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is null || _queue is null || _player is null)
            {
                startupToken.ThrowIfCancellationRequested();
                SetState(
                    TtsPlaybackState.Error,
                    "当前平台没有配置 TTS 引擎或音频播放器。",
                    0,
                    0,
                    expectedStartGeneration: startupGeneration,
                    expectedStartupCancellation: startupCancellation);
                return;
            }

            var availability = await EnsureEnvironmentReadyAsync(
                    cancellationToken: startupToken)
                .ConfigureAwait(false);
            startupToken.ThrowIfCancellationRequested();
            if (!availability.IsAvailable)
            {
                SetState(
                    TtsPlaybackState.Error,
                    availability.Message,
                    0,
                    0,
                    expectedStartGeneration: startupGeneration,
                    expectedStartupCancellation: startupCancellation);
                return;
            }

            if (!_player.IsAvailable)
            {
                startupToken.ThrowIfCancellationRequested();
                SetState(
                    TtsPlaybackState.Error,
                    _player.UnavailableReason ?? "当前平台没有可用的音频输出。",
                    0,
                    0,
                    expectedStartGeneration: startupGeneration,
                    expectedStartupCancellation: startupCancellation);
                return;
            }

            var sessionToken = await _queue.BeginSessionAsync(
                    DefaultStopTimeout,
                    startupToken)
                .ConfigureAwait(false);
            startupToken.ThrowIfCancellationRequested();

            int generation;
            lock (_gate)
            {
                if (_disposed
                    || startupGeneration != _startGeneration
                    || !ReferenceEquals(_startCancellation, startupCancellation))
                {
                    return;
                }

                _sessionToken = sessionToken;
                generation = ++_generation;
                var loopTask = RunAsync(
                    generation,
                    normalized,
                    sessionToken,
                    selection,
                    cancellationToken);
                _loopTask = loopTask;
                _loopTasks.Add(loopTask);
                _ = RemoveLoopTaskWhenCompleteAsync(loopTask);
            }
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested) throw;
        }
        catch when (startupToken.IsCancellationRequested
            && !IsStartupCurrent(startupGeneration, startupCancellation))
        {
            // Stop invalidated startup while an external installer or provider
            // was completing. Its late exception must not become a reader error.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_startCancellation, startupCancellation))
                    _startCancellation = null;
            }
            startupCancellation.Dispose();
        }
    }
}
