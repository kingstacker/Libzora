using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace Libzora;

/// <summary>
/// TTS state, navigation requests and document lookup.
/// </summary>
public partial class MainWindow
{
    private const double ReaderTtsFloatingPanelWidth = 128;
    private const double ReaderTtsFloatingPanelHeight = 128;
    private const double ReaderTtsFloatingPanelRightMargin = 22;
    private const double ReaderTtsFloatingPanelBottomMargin = 20;
    private const double ReaderTtsFloatingCollapsedVisibleWidth = 16;
    private const double ReaderTtsFloatingCollapsedOffset =
        ReaderTtsFloatingPanelWidth
        + ReaderTtsFloatingPanelRightMargin
        - ReaderTtsFloatingCollapsedVisibleWidth;
    private const double ReaderTtsFloatingEdgeActivationWidth = 36;
    private const double ReaderTtsFloatingEdgeActivationTopPadding = 22;
    private const double ReaderTtsFloatingEdgeActivationBottomPadding = 10;
    private const int ReaderTtsFloatingCollapseDelayMs = 4000;
    private const int ReaderTtsFloatingAutoHideDelayMs = 4000;
    private const int ReaderTtsFloatingPointerExitCollapseDelayMs = 4000;
    private const int ReaderTtsButtonBreathingIntervalMs = 50;
    private const double ReaderTtsButtonBreathingPeriodMs = 1800;
    private const double ReaderTtsSpectrumMinimumHeight = 4;
    private const double ReaderTtsSpectrumMaximumHeight = 22;

    private sealed record ReaderTtsPrefetchRequest(
        int ChapterIndex,
        string ChapterPath,
        string Key,
        string BookKey,
        string ChapterKey,
        bool ParagraphIndent);

    private TtsSettings _readerTtsSettings = new();
    private bool _readerTtsVoicesLoading;
    private int _readerTtsVoicesRequest;
    private IReadOnlyList<TtsVoiceInfo> _readerTtsLastVoices = [];
    private bool _readerTtsAutoNavigation;
    private bool _readerTtsFloatingRequested;
    private bool _readerTtsFloatingExpanded;
    private bool _readerTtsFloatingStartupShowing;
    // The right-edge wake-up target is edge-triggered. If the wheel collapses
    // while the pointer is still in that strip, making the strip hit-testable
    // again must not count as a fresh entry and immediately reopen the wheel.
    private bool _readerTtsFloatingEdgeActivationArmed = true;
    private bool _readerTtsResumeAfterNavigation;
    private long _readerTtsNavigationRequestVersion;
    private bool _readerTtsEnvironmentChecked;
    private bool _readerTtsPreviewInProgress;
    private long _readerTtsHighlightVersion;
    private string? _readerTtsNotice;
    private Task? _readerTtsEnvironmentTask;
    private CancellationTokenSource? _readerSelectionTtsCancellation;
    private TtsModelStore? _readerTtsModelStore;
    private bool _readerTtsModelDownloadInProgress;
    private bool _readerTtsModelsRefreshing;
    private CancellationTokenSource? _readerTtsModelDownloadCancellation;
    private TtsModelPackage? _readerTtsSelectedModel;
    private TtsModelDownloadProgress? _readerTtsModelProgress;
    private string? _readerTtsModelNotice;
    private DispatcherTimer? _readerTtsFloatingCollapseTimer;
    private DispatcherTimer? _readerTtsFloatingAutoHideTimer;
    private DispatcherTimer? _readerTtsButtonBreathingTimer;
    private double _readerTtsButtonBreathingPhase;

    private static bool IsReaderTtsActiveState(TtsPlaybackState state)
        => state is TtsPlaybackState.Generating
            or TtsPlaybackState.Playing
            or TtsPlaybackState.Paused
            or TtsPlaybackState.AdvancingChapter;

    /// <summary>
    /// DEBUG TTS tracer: appends one line per event to tts-debug.log so a
    /// "点击无反应" report can be attributed to state, environment prep or
    /// availability without guessing.
    /// </summary>
    private void LogTtsDebug(string stage, string detail)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_TTS_DEBUG"),
                "1",
                StringComparison.Ordinal))
            return;
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                stage,
                detail,
                state = _readerTts?.State.ToString(),
                setupInProgress = _readerTts?.EnvironmentSetupInProgress,
                canOpen = _readerTts?.CanOpen,
                thread = Environment.CurrentManagedThreadId,
                uiThread = Dispatcher.UIThread.CheckAccess()
            });
            Directory.CreateDirectory(_paths.Logs);
            File.AppendAllText(
                Path.Combine(_paths.Logs, "tts-debug.log"),
                entry + Environment.NewLine);
        }
        catch
        {
            // Diagnostics only; never break TTS because of logging.
        }
    }

    private long BeginReaderTtsNavigationRequest(bool shouldResume)
    {
        var requestVersion = Interlocked.Increment(
            ref _readerTtsNavigationRequestVersion);
        if (shouldResume)
            _readerTtsResumeAfterNavigation = true;
        return requestVersion;
    }

    private bool IsCurrentReaderTtsNavigationRequest(long requestVersion)
        => requestVersion == Volatile.Read(
            ref _readerTtsNavigationRequestVersion);

    private bool CanResumeReaderTtsNavigation(
        long requestVersion,
        bool shouldResume)
        => shouldResume
            && IsCurrentReaderTtsNavigationRequest(requestVersion)
            && _readerTtsResumeAfterNavigation;

    private void CompleteReaderTtsNavigationRequest(long requestVersion)
    {
        if (IsCurrentReaderTtsNavigationRequest(requestVersion))
            _readerTtsResumeAfterNavigation = false;
    }

    private void CancelReaderTtsContinuation()
    {
        _readerSelectionTtsCancellation?.Cancel();
        _readerSelectionTtsCancellation?.Dispose();
        _readerSelectionTtsCancellation = null;
        _readerTtsResumeAfterNavigation = false;
        Interlocked.Increment(ref _readerTtsNavigationRequestVersion);
        _readerTtsFloatingRequested = false;
    }

    private ReaderTtsDocument? GetCurrentReaderTtsDocument()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            try
            {
                return Dispatcher.UIThread
                    .InvokeAsync(GetCurrentReaderTtsDocument)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                return null;
            }
        }

        if (_readerIsPdf)
            return GetCurrentPdfReaderTtsDocument();

        if (_readerDocument is null
            || CurrentReaderHost is not IReaderSpeechHost host)
        {
            return null;
        }

        var speech = host.GetSpeechText();
        if (speech is null || string.IsNullOrWhiteSpace(speech.Text))
            return null;

        var key = string.Join(
            ":",
            _readerBookFile?.Id.ToString("N") ?? string.Empty,
            _readerChapterIndex,
            host.Source?.AbsoluteUri ?? string.Empty);
        return new ReaderTtsDocument(
            key,
            speech.Text,
            speech.StartOffset,
            (start, length) => ApplyReaderTtsHighlightAsync(host, start, length),
            () => ClearReaderTtsHighlight(host),
            speech.MapToSource,
            _readerBookFile?.Id.ToString("N"),
            $"{_readerChapterIndex}:{host.Source?.AbsoluteUri}",
            speech.PageBreakOffsets);
    }

    private ReaderTtsDocument? GetCurrentPdfReaderTtsDocument()
    {
        if (_readerBookFile is null || _readerPdfPages.Count == 0)
            return null;
        RememberCurrentPdfPageText();
        var page = _readerPdfPages.FirstOrDefault(item => item.PageNumber == _readerPdfPage);
        if (page is null || string.IsNullOrWhiteSpace(page.Text))
            return null;

        var key = string.Join(":", _readerBookFile.Id.ToString("N"), "pdf", page.PageNumber);
        return new ReaderTtsDocument(
            key,
            page.Text,
            0,
            (start, length) => ApplyPdfReaderTtsHighlightAsync(page.PageNumber, start, length),
            ClearPdfReaderTtsHighlight,
            (start, length) => (start, length),
            _readerBookFile.Id.ToString("N"),
            $"pdf:{page.PageNumber}");
    }

    private Task ApplyPdfReaderTtsHighlightAsync(int page, int start, int length)
    {
        return RunOnReaderUiAsync(() =>
        {
            if (_readerIsPdf && page == _readerPdfPage)
            {
                (CurrentReaderHost as IPdfTextHost)?.SetSpeechHighlight(start, length);
                ReaderStatusText.Text = T("PDF 第 {0} 页 · 正在朗读", page);
            }
        });
    }

    private void ClearPdfReaderTtsHighlight()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ClearPdfReaderTtsHighlight);
            return;
        }
        if (!_readerIsPdf || !ReaderRoot.IsVisible) return;
        (CurrentReaderHost as IPdfTextHost)?.ClearSpeechHighlight();
        ResetReaderStatusText();
    }

    private async Task<ReaderTtsDocument?> GetNextReaderTtsDocumentAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var request = Dispatcher.UIThread.CheckAccess()
                ? CaptureNextReaderTtsPrefetchRequest()
                : await Dispatcher.UIThread.InvokeAsync(
                    CaptureNextReaderTtsPrefetchRequest);
            if (request is null) return null;

            var snapshot = await Task.Run(
                () => ReaderSpeechTextBuilder.Load(
                    request.ChapterPath,
                    request.ParagraphIndent,
                    cancellationToken),
                cancellationToken);
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text))
                return null;

            // This document is used only for cache warming. The visible host
            // supplies the real highlight callback after the chapter swap.
            return new ReaderTtsDocument(
                request.Key,
                snapshot.Text,
                snapshot.StartOffset,
                static (_, _) => Task.CompletedTask,
                static () => { },
                snapshot.MapToSource,
                request.BookKey,
                request.ChapterKey,
                snapshot.PageBreakOffsets);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            // Next-chapter preparation is an optimization. The visible
            // chapter and the normal on-demand request remain authoritative.
            return null;
        }
    }

    private ReaderTtsPrefetchRequest? CaptureNextReaderTtsPrefetchRequest()
    {
        if (_readerIsPdf
            || _readerDocument is null
            || _readerBookFile is null)
        {
            return null;
        }

        var chapterIndex = _readerChapterIndex + 1;
        if (chapterIndex < 0 || chapterIndex >= _readerDocument.Chapters.Count)
            return null;

        var chapterPath = Path.GetFullPath(_readerDocument.Chapters[chapterIndex]);
        if (!File.Exists(chapterPath)) return null;

        var target = new Uri(chapterPath, UriKind.Absolute);
        var bookKey = _readerBookFile.Id.ToString("N");
        return new ReaderTtsPrefetchRequest(
            chapterIndex,
            chapterPath,
            string.Join(":", bookKey, chapterIndex, target.AbsoluteUri),
            bookKey,
            $"{chapterIndex}:{target.AbsoluteUri}",
            _readerLayout.ParagraphIndent);
    }

    private Task ApplyReaderTtsHighlightAsync(
        IReaderSpeechHost host,
        int start,
        int length)
    {
        var version = Interlocked.Increment(ref _readerTtsHighlightVersion);
        return RunOnReaderUiAsync(() =>
        {
            if (version != Volatile.Read(ref _readerTtsHighlightVersion)
                || !ReaderRoot.IsVisible
                || !ReferenceEquals(CurrentReaderHost, host))
            {
                return;
            }

            host.SetSpeechHighlight(start, length);
        });
    }

    private void ClearReaderTtsHighlight(IReaderSpeechHost host)
    {
        var version = Interlocked.Increment(ref _readerTtsHighlightVersion);
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (version == Volatile.Read(ref _readerTtsHighlightVersion)
                    && ReferenceEquals(CurrentReaderHost, host))
                {
                    host.ClearSpeechHighlight();
                }

                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (version == Volatile.Read(ref _readerTtsHighlightVersion)
                    && ReferenceEquals(CurrentReaderHost, host))
                {
                    host.ClearSpeechHighlight();
                }
            });
        }
        catch
        {
            // The reader is closing; there is no visual state left to clear.
        }
    }

    private static Task RunOnReaderUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private async Task<bool> AdvanceReaderForTtsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await AdvanceReaderForTtsAsync(cancellationToken));
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
            }
            catch
            {
                return false;
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }

        if (_readerIsPdf)
        {
            if (_readerPdfPage >= _readerPdfPages.Count)
                return false;
            var previousPage = _readerPdfPage;
            _readerTtsAutoNavigation = true;
            try
            {
                await NavigatePdfPageAsync(previousPage + 1, cancellationToken);
                return _readerPdfPage > previousPage;
            }
            finally
            {
                _readerTtsAutoNavigation = false;
            }
        }

        if (_readerDocument is null || CurrentReaderHost is null)
        {
            return false;
        }

        var previousChapter = _readerChapterIndex;
        _readerTtsAutoNavigation = true;
        try
        {
            await MoveReaderChapterAsync(1);
            return _readerChapterIndex > previousChapter;
        }
        finally
        {
            _readerTtsAutoNavigation = false;
        }
    }

    private void ReaderTts_StateChanged(
        object? sender,
        TtsStateChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReaderTts_StateChanged(sender, e));
            return;
        }

        if (e.State is TtsPlaybackState.Generating
            or TtsPlaybackState.Playing
            or TtsPlaybackState.Paused
            or TtsPlaybackState.AdvancingChapter)
        {
            _readerTtsFloatingRequested = true;
        }
        if (e.State is TtsPlaybackState.Stopped
            or TtsPlaybackState.Error)
        {
            _readerTtsFloatingRequested = false;
            _readerTtsFloatingStartupShowing = false;
            _readerTtsFloatingExpanded = false;
            _readerTtsFloatingCollapseTimer?.Stop();
            _readerTtsFloatingAutoHideTimer?.Stop();
        }
        if (e.State is TtsPlaybackState.Paused
            or TtsPlaybackState.Stopped
            or TtsPlaybackState.Error
            && _readerListeningSeconds > 0)
        {
            _ = ObserveReadingTimeFlushAsync();
        }
        if (e.State == TtsPlaybackState.Error
            && !string.IsNullOrWhiteSpace(e.Message))
        {
            ReaderStatusText.Text = T("朗读失败：{0}", UiText.Localize(e.Message));
        }

        UpdateReaderTtsUi();
    }

    private void ReaderTts_EnvironmentChanged(
        object? sender,
        EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReaderTts_EnvironmentChanged(sender, e));
            return;
        }

        UpdateReaderTtsUi();
    }

    /// <summary>
    /// Starts the one-per-window dependency bootstrap. App startup calls this
    /// in the background; opening a reader continues without waiting for it.
    /// </summary>
}
