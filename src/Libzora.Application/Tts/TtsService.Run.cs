using System.Diagnostics;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices.Tts;

/// <summary>
/// Main playback loop.
/// </summary>
public sealed partial class TtsService : IDisposable
{
    private async Task RunAsync(
        int generation,
        TtsSettings settings,
        CancellationToken sessionToken,
        ReaderTtsDocument? selection,
        CancellationToken callerToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, callerToken);
        var cancellationToken = linkedCancellation.Token;
        try
        {
            var availability = await CheckEnvironmentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!availability.IsAvailable)
                throw new InvalidOperationException(availability.Message);

            var firstDocument = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = selection ?? await ResolveReadableDocumentAsync(
                        cancellationToken,
                        allowAdvance: firstDocument || settings.AutoAdvance,
                        generation: generation)
                    .ConfigureAwait(false);
                if (!IsGenerationCurrent(generation)) return;
                if (document is null || string.IsNullOrWhiteSpace(document.Text))
                {
                    // Reaching the end after skipping image-only chapters is
                    // a normal end-of-book condition, not a playback error.
                    if (!firstDocument && settings.AutoAdvance)
                        break;

                    throw new InvalidOperationException("当前阅读位置之后没有可朗读的正文。");
                }

                var segments = TtsTextSegmenter.SplitPlainTextSentences(
                    document.Text,
                    settings.MaxCharactersPerRequest,
                    preserveLongSentences: _player is ITtsLiveSpeechPlayer);
                if (segments.Count == 0)
                    throw new InvalidOperationException("当前阅读位置之后没有可朗读的正文。");

                var startIndex = firstDocument
                    ? FindStartingSegment(segments, document.StartOffset)
                    : 0;
                firstDocument = false;
                SetCurrentChapter(document, segments, startIndex, generation);

                var documentChanged = false;
                var nextChapterPrefetchStarted = false;
                for (var index = startIndex; index < segments.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = _documentAccessor();
                    if (current is null)
                        throw new InvalidOperationException("阅读器章节已关闭。");
                    if (!string.Equals(current.Key, document.Key, StringComparison.Ordinal))
                    {
                        documentChanged = true;
                        break;
                    }

                    SetCurrentSegment(index, segments.Count, generation);
                    var segment = segments[index];
                    var navigationVersion = Volatile.Read(ref _navigationVersion);
                    TtsSettings activeSettings;
                    lock (_gate) activeSettings = _settings;
                    var options = BuildOptions(activeSettings);
                    var item = new TtsQueueItem(
                        document.BookKey ?? document.Key,
                        document.ChapterKey ?? document.Key,
                        segment,
                        options);

                    SetState(
                        TtsPlaybackState.Generating,
                        null,
                        index + 1,
                        segments.Count,
                        generation);
                    TtsResult? result = null;
                    var livePlayer = _player as ITtsLiveSpeechPlayer;
                    if (livePlayer is null)
                    {
                        var audioTask = _queue!.GetOrCreateAsync(
                            item,
                            cancellationToken);
                        _queue.Prefetch(
                            segments
                                .Skip(index + 1)
                                .Take(settings.PrefetchCount)
                                .Select(next => new TtsQueueItem(
                                    document.BookKey ?? document.Key,
                                    document.ChapterKey ?? document.Key,
                                    next,
                                    options)),
                            cancellationToken);

                        // Start the next-chapter request after the current item
                        // and its local look-ahead have entered the queue. This
                        // preserves first-sentence latency while allowing the
                        // next chapter to be ready when the transition arrives.
                        if (!nextChapterPrefetchStarted
                            && selection is null
                            && settings.PrefetchCount > 0
                            && _prefetchDocumentAccessor is not null)
                        {
                            nextChapterPrefetchStarted = true;
                            _ = ObserveNextChapterPrefetchAsync(
                                document,
                                settings,
                                cancellationToken);
                        }

                        result = await audioTask.ConfigureAwait(false);
                        if (!IsGenerationCurrent(generation)) return;
                        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.AudioPath))
                        {
                            throw new InvalidOperationException(
                                result.ErrorMessage ?? "TTS 没有生成音频。");
                        }

                    }

                    if (TryTakeRequestedSegment(generation, out var requestedIndex))
                    {
                        index = requestedIndex - 1;
                        continue;
                    }

                    current = _documentAccessor();
                    if (current is null)
                        throw new InvalidOperationException("阅读器章节已关闭。");
                    if (!string.Equals(current.Key, document.Key, StringComparison.Ordinal))
                    {
                        documentChanged = true;
                        break;
                    }

                    if (!IsGenerationCurrent(generation)) return;
                    var highlightDocument = selection ?? current;
                    var highlight = highlightDocument.MapHighlight?.Invoke(
                        segment.Start,
                        segment.Length)
                        ?? (segment.Start, segment.Length);
                    await highlightDocument.Highlight(
                            highlight.Start,
                            highlight.Length)
                        .ConfigureAwait(false);

                    using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    lock (_gate)
                    {
                        if (_disposed || generation != _generation) return;
                        if (_requestedSegmentIndex >= 0) playbackCancellation.Cancel();
                        else _playbackCancellation = playbackCancellation;
                    }
                    SetState(
                        TtsPlaybackState.Playing,
                        null,
                        index + 1,
                        segments.Count,
                        generation);
                    using var positionCancellation = CancellationTokenSource.CreateLinkedTokenSource(playbackCancellation.Token);
                    var positionTask = FollowSpeechPositionAsync(
                        highlightDocument, segment, result?.TimingMarkers, generation, navigationVersion, positionCancellation.Token);
                    try
                    {
                        if (livePlayer is not null)
                            await livePlayer.SpeakAsync(segment.Text, options, playbackCancellation.Token).ConfigureAwait(false);
                        else
                            await _player!.PlayAsync(result!.AudioPath!, playbackCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested)
                    {
                        if (!IsGenerationCurrent(generation)) return;
                        if (cancellationToken.IsCancellationRequested || !HasPendingSegmentRequest(generation)) throw;
                        // SkipSegmentAsync intentionally stops only the current player.
                    }
                    finally
                    {
                        positionCancellation.Cancel();
                        CancelPendingNavigation();
                        lock (_gate)
                            if (ReferenceEquals(_playbackCancellation, playbackCancellation)) _playbackCancellation = null;
                        try { await positionTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }

                    if (TryTakeRequestedSegment(generation, out requestedIndex))
                    {
                        index = requestedIndex - 1;
                    }
                }

                if (documentChanged)
                {
                    if (selection is not null) break;
                    continue;
                }
                bool autoAdvance;
                lock (_gate) autoAdvance = _settings.AutoAdvance;
                if (!autoAdvance || _advanceChapterAsync is null) break;

                SetState(
                    TtsPlaybackState.AdvancingChapter,
                    null,
                    segments.Count,
                    segments.Count,
                    generation);
                if (!await _advanceChapterAsync(cancellationToken)
                        .ConfigureAwait(false))
                {
                    break;
                }
            }

            SetState(TtsPlaybackState.Stopped, null, 0, 0, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(TtsPlaybackState.Stopped, null, 0, 0, generation);
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("tts.playback", exception);
            try { _player?.Stop(); }
            catch { }
            SetState(
                TtsPlaybackState.Error,
                exception.Message,
                0,
                0,
                generation);
        }
        finally
        {
            var generationIsCurrent = false;
            lock (_gate) generationIsCurrent = generation == _generation;
            if (generationIsCurrent && selection is not null)
            {
                try { selection.ClearHighlight(); }
                catch { }
            }
            if (generationIsCurrent) ClearCurrentHighlight();
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _loopTask = null;
                    _currentSegments = null;
                    _currentDocumentKey = null;
                    _currentSegmentIndex = -1;
                    _requestedSegmentIndex = -1;
                }
            }
        }
    }
}
