using System.Diagnostics;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices.Tts;

/// <summary>
/// Document resolution, prefetch, state and highlight.
/// </summary>
public sealed partial class TtsService : IDisposable
{
    private async Task<ReaderTtsDocument?> ResolveReadableDocumentAsync(
        CancellationToken cancellationToken,
        bool allowAdvance,
        int generation)
    {
        var document = _documentAccessor();
        if (document is not null && !string.IsNullOrWhiteSpace(document.Text))
        {
            return document;
        }

        if (!allowAdvance || _advanceChapterAsync is null)
        {
            return null;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetState(
                TtsPlaybackState.AdvancingChapter,
                null,
                0,
                0,
                generation);
            if (!await _advanceChapterAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            document = _documentAccessor();
            if (document is not null && !string.IsNullOrWhiteSpace(document.Text))
            {
                return document;
            }
        }
    }

    private async Task ObserveNextChapterPrefetchAsync(
        ReaderTtsDocument currentDocument,
        TtsSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextDocument = await _prefetchDocumentAccessor!(cancellationToken)
                .ConfigureAwait(false);
            if (nextDocument is null
                || string.Equals(
                    nextDocument.Key,
                    currentDocument.Key,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(nextDocument.Text))
            {
                return;
            }

            var nextSegments = TtsTextSegmenter.SplitPlainTextSentences(
                nextDocument.Text,
                settings.MaxCharactersPerRequest);
            var options = BuildOptions(settings);
            _queue?.Prefetch(
                nextSegments
                    .Take(settings.PrefetchCount)
                    .Select(segment => new TtsQueueItem(
                        nextDocument.BookKey ?? nextDocument.Key,
                        nextDocument.ChapterKey ?? nextDocument.Key,
                        segment,
                        options)),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Cross-chapter prefetch is an optimization. Navigation and the
            // normal current-chapter request remain authoritative if it fails.
        }
    }

    private void SetCurrentChapter(
        ReaderTtsDocument document,
        IReadOnlyList<TtsTextSegment> segments,
        int startIndex,
        int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
                return;

            _currentDocumentKey = document.Key;
            _currentSegments = segments;
            _currentSegmentIndex = startIndex;
            _segmentIndex = Math.Max(0, startIndex);
            _segmentCount = segments.Count;
        }
    }

    private void SetCurrentSegment(int index, int count, int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
                return;

            _currentSegmentIndex = index;
            _segmentIndex = index + 1;
            _segmentCount = count;
        }
    }

    private bool HasPendingSegmentRequest(int generation)
    {
        lock (_gate)
        {
            return !_disposed
                && generation == _generation
                && _requestedSegmentIndex >= 0;
        }
    }

    private bool TryTakeRequestedSegment(int generation, out int index)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                index = -1;
                return false;
            }

            index = _requestedSegmentIndex;
            _requestedSegmentIndex = -1;
            return index >= 0;
        }
    }

    private static int FindStartingSegment(
        IReadOnlyList<TtsTextSegment> segments,
        int startOffset)
    {
        startOffset = Math.Max(0, startOffset);
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].End > startOffset) return index;
        }

        return Math.Max(0, segments.Count - 1);
    }

    private void SetState(
        TtsPlaybackState state,
        string? message,
        int segmentIndex,
        int segmentCount,
        int? expectedGeneration = null,
        int? expectedStartGeneration = null,
        CancellationTokenSource? expectedStartupCancellation = null)
    {
        bool isSelection;
        lock (_gate)
        {
            if (_disposed
                || (expectedGeneration.HasValue
                    && expectedGeneration.Value != _generation)
                || (expectedStartGeneration.HasValue
                    && expectedStartGeneration.Value != _startGeneration)
                || (expectedStartupCancellation is not null
                    && !ReferenceEquals(
                        expectedStartupCancellation,
                        _startCancellation)))
            {
                return;
            }
            isSelection = _isSelectionPlayback;
            _state = state;
            _message = message;
            _segmentIndex = segmentIndex;
            _segmentCount = segmentCount;
            if (state is TtsPlaybackState.Stopped or TtsPlaybackState.Error)
                _isSelectionPlayback = false;
        }

        try
        {
            StateChanged?.Invoke(
                this,
                new TtsStateChangedEventArgs(
                    state,
                    message,
                    segmentIndex,
                    segmentCount,
                    isSelection));
        }
        catch
        {
            // A UI observer must never terminate the audio loop.
        }
    }

    private void SetEnvironmentStatus(string? message, bool inProgress)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _environmentMessage = message;
            _environmentSetupInProgress = inProgress;
        }

        try { EnvironmentChanged?.Invoke(this, EventArgs.Empty); }
        catch
        {
            // Environment diagnostics must never terminate startup or playback.
        }
    }
}
