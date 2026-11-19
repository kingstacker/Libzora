using System.Diagnostics;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices.Tts;

/// <summary>
/// Pause, stop, skip, manual navigation and speech following.
/// </summary>
public sealed partial class TtsService : IDisposable
{
    public void PauseOrResume()
    {
        if (_disposed || _player is null) return;

        TtsPlaybackState state;
        lock (_gate) state = _state;
        if (state == TtsPlaybackState.Playing)
        {
            _player.Pause();
            CancelPendingNavigation();
            SetState(
                TtsPlaybackState.Paused,
                null,
                SegmentIndex,
                SegmentCount);
        }
        else if (state == TtsPlaybackState.Paused)
        {
            _player.Resume();
            SetState(
                TtsPlaybackState.Playing,
                null,
                SegmentIndex,
                SegmentCount);
        }
    }

    public Task SkipSegmentAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _disposed || _player is null) return Task.CompletedTask;

        lock (_gate)
        {
            if (_currentSegments is not { Count: > 0 }
                || _currentSegmentIndex < 0
                || _state is TtsPlaybackState.Stopped or TtsPlaybackState.Error)
            {
                return Task.CompletedTask;
            }

            var target = Math.Clamp(
                _currentSegmentIndex + direction,
                0,
                _currentSegments.Count - 1);
            if (target == _currentSegmentIndex) return Task.CompletedTask;
            _requestedSegmentIndex = target;
            try { _playbackCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        CancelPendingNavigation();
        // Stop only the audio. The session token stays alive so the loop can
        // continue with the requested segment and prefetching is preserved.
        _player.Stop();
        return Task.CompletedTask;
    }

    public Task StopAsync() => StopAsync(DefaultStopTimeout);

    public async Task StopAsync(TimeSpan timeout)
    {
        ValidateStopTimeout(timeout);
        var startedAt = Stopwatch.GetTimestamp();
        CancellationTokenSource? startup;
        Task? loop;
        int stopGeneration;
        int stopStartGeneration;
        lock (_gate)
        {
            startup = _startCancellation;
            _startCancellation = null;
            stopStartGeneration = ++_startGeneration;
            loop = _loopTask;
            _loopTask = null;
            _requestedSegmentIndex = -1;
            stopGeneration = ++_generation;
            try { _playbackCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        CancelPendingNavigation();
        try { startup?.Cancel(); }
        catch { }

        try { _player?.Stop(); }
        catch { }

        if (_queue is not null)
        {
            try
            {
                await _queue.StopAsync(RemainingStopTimeout(timeout, startedAt))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Queue generation failures are isolated. Player and loop
                // cancellation below still release the active reader session.
            }
        }

        if (loop is not null)
        {
            try
            {
                var remaining = RemainingStopTimeout(timeout, startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    // The caller receives a bounded stop. The canceled loop
                    // owns its own cleanup and cannot publish through the
                    // generation guard after a new session starts.
                }
                else
                {
                    await loop.WaitAsync(remaining).ConfigureAwait(false);
                }
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            catch { }
        }

        ClearCurrentHighlight();
        SetState(
            TtsPlaybackState.Stopped,
            null,
            0,
            0,
            stopGeneration,
            stopStartGeneration);
    }

    /// <summary>Call when the user navigates manually, before changing the visible page.</summary>
    public void NotifyManualNavigation()
    {
        Interlocked.Increment(ref _navigationVersion);
        CancelPendingNavigation();
    }

    private void CancelPendingNavigation()
    {
        lock (_gate)
        {
            try { _navigationCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task FollowSpeechPositionAsync(
        ReaderTtsDocument document, TtsTextSegment segment,
        IReadOnlyList<TtsTimingMarker>? markers, int generation, long navigationVersion, CancellationToken token)
    {
        var livePlayer = _player as ITtsLiveSpeechPlayer;
        var clock = _player as ITtsPlaybackClock;
        if (document.NavigateToSpeechOffset is null
            || document.PageBreakOffsets is not { Count: > 0 } breaks
            || (livePlayer is null && (clock is null || markers is not { Count: > 0 }))) return;

        if (livePlayer is null)
        {
            // Reject corrupt or ambiguous metadata rather than guessing a position.
            var previousStart = -1;
            var previousTime = -1d;
            foreach (var marker in markers!)
            {
                if (marker.Start < previousStart || marker.Start < 0 || marker.Length <= 0
                    || (long)marker.Start + marker.Length > segment.Text.Length
                    || !double.IsFinite(marker.Seconds) || marker.Seconds < previousTime || marker.Seconds < 0)
                    return;
                previousStart = marker.Start;
                previousTime = marker.Seconds;
            }
        }

        var targets = breaks.Where(offset => offset > segment.Start && offset < segment.End)
            .Distinct().OrderBy(offset => offset)
            .Select(offset => (Offset: offset, Marker: livePlayer is null
                ? markers!.FirstOrDefault(m => m.Start >= offset - segment.Start) : null))
            .Where(target => livePlayer is not null || target.Marker is not null).ToArray();
        for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            var target = targets[targetIndex];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsGenerationCurrent(generation) || HasPendingSegmentRequest(generation)
                    || navigationVersion != Volatile.Read(ref _navigationVersion)) return;
                if (livePlayer is not null
                    ? livePlayer.TryGetSpeechPosition(out var offset) && offset >= target.Offset - segment.Start
                    : clock!.TryGetPosition(out var position) && position.TotalSeconds >= target.Marker!.Seconds)
                    break;
                await Task.Delay(40, token).ConfigureAwait(false);
            }

            var current = _documentAccessor();
            if (current is null || current.Key != document.Key) return;
            var mapped = document.MapHighlight?.Invoke(target.Offset, 1) ?? (target.Offset, 1);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (_gate)
            {
                if (_disposed || generation != _generation || _requestedSegmentIndex >= 0
                    || navigationVersion != Volatile.Read(ref _navigationVersion)) return;
                if (_state == TtsPlaybackState.Paused)
                {
                    targetIndex--;
                    continue;
                }
                if (_state != TtsPlaybackState.Playing) return;
                _navigationCancellation = request;
            }
            try
            {
                await document.NavigateToSpeechOffset(mapped.Item1, request.Token).WaitAsync(request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                if (token.IsCancellationRequested) throw;
                targetIndex--; // A pause can cancel a UI dispatch; retry after resume.
            }
            catch { return; }
            finally
            {
                lock (_gate)
                    if (ReferenceEquals(_navigationCancellation, request)) _navigationCancellation = null;
            }
        }
    }

    private bool IsStartupCurrent(
        int generation,
        CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            return !_disposed
                && generation == _startGeneration
                && ReferenceEquals(_startCancellation, cancellation);
        }
    }

    private bool IsGenerationCurrent(int generation)
    {
        lock (_gate) return !_disposed && generation == _generation;
    }

    private static TimeSpan RemainingStopTimeout(
        TimeSpan timeout,
        long startedAt)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void ValidateStopTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }
}
