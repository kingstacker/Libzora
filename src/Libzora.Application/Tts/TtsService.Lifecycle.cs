using System.Diagnostics;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices.Tts;

/// <summary>
/// Progress helper, loop cleanup and disposal.
/// </summary>
public sealed partial class TtsService : IDisposable
{
    /// <summary>
    /// IProgress<T> that reports inline on the caller's thread. Unlike
    /// Progress<T> it never defers callbacks to a SynchronizationContext, so
    /// state transitions cannot be reordered by a busy UI thread.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SynchronousProgress(Action<T> callback)
            => _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }

    private void ClearCurrentHighlight()
    {
        try { _documentAccessor()?.ClearHighlight(); }
        catch { }
    }

    private async Task RemoveLoopTaskWhenCompleteAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }

        lock (_gate) _loopTasks.Remove(task);
    }

    public void Dispose()
    {
        CancellationTokenSource? startup;
        Task[] pendingOperations;
        Task previewsIdle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            startup = _startCancellation;
            _startCancellation = null;
            _startGeneration++;
            _generation++;
            _loopTask = null;
            _requestedSegmentIndex = -1;
            try { _playbackCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }

            pendingOperations = _loopTasks
                .Concat(_startupTask is null ? [] : [_startupTask])
                .Where(task => !task.IsCompleted)
                .Distinct()
                .ToArray();
            previewsIdle = _activePreviewCount == 0
                ? Task.CompletedTask
                : _previewsIdle!.Task;
        }

        try { startup?.Cancel(); }
        catch { }
        try { _player?.Stop(); }
        catch { }
        try { _queue?.Dispose(); }
        catch { }

        _ = CompleteDisposeAsync(pendingOperations, previewsIdle);
    }

    private async Task CompleteDisposeAsync(
        IReadOnlyList<Task> pendingOperations,
        Task previewsIdle)
    {
        try { await Task.WhenAll(pendingOperations).ConfigureAwait(false); }
        catch { }
        try { await previewsIdle.ConfigureAwait(false); }
        catch { }

        if (_queue is not null)
        {
            try { await _queue.WaitForIdleAsync().ConfigureAwait(false); }
            catch { }
        }

        try { _player?.Dispose(); }
        catch { }
        try { _engine?.Dispose(); }
        catch { }
        try { _lifecycle.Dispose(); }
        catch { }
        try { _previewGate.Dispose(); }
        catch { }
    }
}
