using System.Diagnostics;
using Avalonia.Threading;
using Libzora.Core;

namespace Libzora.Mobile.Views;

/// <summary>Time-based scroll inertia and serialized page/chapter navigation.</summary>
public partial class ReaderPage
{
    private DispatcherTimer? _scrollInertiaTimer;
    private double _scrollInertiaVelocityY;
    private long _scrollInertiaLastTicks;

    private void StopScrollInertia()
    {
        _scrollInertiaTimer?.Stop();
        _scrollInertiaVelocityY = 0;
        _scrollInertiaLastTicks = 0;
    }

    private void StartScrollInertia(NativeReaderHost host)
    {
        var now = Stopwatch.GetTimestamp();
        var age = _lastDragTicks > 0 ? (now - _lastDragTicks) / (double)Stopwatch.Frequency : 0;
        var velocity = MobileReaderScrollPhysics.ReleaseVelocity(_dragVelocityY, age, amplifyDrag: IsEpubScrollMode);
        if (velocity == 0) return;
        _scrollInertiaVelocityY = velocity;
        _scrollInertiaLastTicks = now;
        _scrollInertiaTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) =>
        {
            if (_disposed || IsChromeVisible || (_turning && !IsEpubScrollMode)
                || !IsContinuousVerticalScroll || IsMangaZoomed || _readerHost is not NativeReaderHost scrollHost)
            {
                StopScrollInertia();
                return;
            }
            var ticks = Stopwatch.GetTimestamp();
            var elapsed = (ticks - _scrollInertiaLastTicks) / (double)Stopwatch.Frequency;
            _scrollInertiaLastTicks = ticks;
            var step = MobileReaderScrollPhysics.Advance(_scrollInertiaVelocityY, elapsed);
            _scrollInertiaVelocityY = step.Velocity;
            scrollHost.ScrollByPixel(step.Distance);
            if (Math.Abs(_scrollInertiaVelocityY) < MobileReaderScrollPhysics.StopVelocity) StopScrollInertia();
        });
        _scrollInertiaTimer.Stop();
        _scrollInertiaTimer.Start();
    }

    /// <summary>Turns one page, crossing into the neighboring chapter at its edge.</summary>
    public async Task TurnAsync(int direction)
    {
        HideSelectionBar();
        if (_turning) return;
        var turnId = ++_activeTurnId;
        _turning = true;
        CancellationTokenSource? transitionCts = null;
        Task? transitionTask = null;
        try
        {
            CancelInteractiveDragImmediate();
            await CancelAndAwaitActiveTransitionAsync();
            MarkNativeNavigation();
            _tts?.NotifyManualNavigation();
            // A destination already in the composed prefix can turn immediately;
            // only crossing its edge needs the rest of a long chapter.
            if (_readerHost is not NativeReaderHost cached || !cached.CanTurn(direction))
                await EnsureLayoutCompleteAsync();
            if (_isPdf && (_pdfPage + direction < 1 || _pdfPage + direction > _pdfPageCount))
            {
                ShowEdgeNotice(direction);
                return;
            }
            if (!_isPdf && _readerHost is NativeReaderHost current && !current.CanTurn(direction))
            {
                var target = _epubDocument is null ? -1 : ResolveReadingChapter(_chapterIndex + direction, direction);
                if (_epubDocument is null || target < 0 || target >= _epubDocument.Chapters.Count)
                {
                    ShowEdgeNotice(direction);
                    return;
                }
            }
            var surface = CreateTransitionSurface();
            var animation = IsPowerSavingActive ? ReaderTransitionPlayer.AnimationNone
                : ResolveTransitionAnimation(_shell.Preferences.Current.ReaderPageAnimation);
            var visualDirection = MobileReaderPagingPolicy.VisualDirection(
                direction, _layout.VerticalWriting, _isPdf, IsRightToLeftPaging);
            transitionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var transitionToken = transitionCts.Token;
            _transitionCts = transitionCts;
            var contentSwitched = false;
            transitionTask = ReaderTransitionPlayer.RunAsync(surface, animation, visualDirection, async () =>
            {
                ClearActiveSyncedMarker();
                if (_isPdf)
                {
                    await GoToPdfPageAsync(_pdfPage + direction, cancellationToken: transitionToken);
                    contentSwitched = true;
                    return true;
                }
                if (_readerHost is not NativeReaderHost host) return false;
                if (!host.TurnPage(direction))
                    await GoToChapterAsync(host, _chapterIndex + direction, fromEnd: direction < 0, direction);
                contentSwitched = true;
                return true;
            }, transitionToken);
            _activeTransitionTask = transitionTask;
            // Unlock the next page turn once content has switched; settle its
            // remaining visual animation before the next navigation starts.
            while (!transitionTask.IsCompleted && !contentSwitched)
                await Task.WhenAny(transitionTask, Task.Delay(8, transitionToken));
            if (contentSwitched && turnId == _activeTurnId) _turning = false;
            await transitionTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _shell.ShowToast(UiText.Localize(exception.Message)); }
        finally
        {
            if (transitionTask is not null && ReferenceEquals(_activeTransitionTask, transitionTask))
                _activeTransitionTask = null;
            if (ReferenceEquals(_transitionCts, transitionCts)) _transitionCts = null;
            transitionCts?.Dispose();
            if (turnId == _activeTurnId) _turning = false;
        }
    }

    private async Task GoToChapterAsync(NativeReaderHost host, int chapter, bool fromEnd, int direction,
        double carryOffset = 0, double carryUpDelta = 0)
    {
        ClearActiveSyncedMarker();
        HideSelectionBar();
        if (_epubDocument is null) return;
        chapter = ResolveReadingChapter(chapter, direction < 0 ? -1 : 1);
        if (chapter < 0 || chapter >= _epubDocument.Chapters.Count)
        {
            StopScrollInertia();
            ShowEdgeNotice(direction);
            return;
        }
        var continuousHandoffDown = IsContinuousVerticalScroll && !fromEnd && direction > 0;
        var continuousHandoffUp = IsContinuousVerticalScroll && fromEnd && direction < 0 && carryUpDelta > 0;
        if (continuousHandoffDown)
            host.PrepareContinuousScrollHandoff(handoffDown: true, carryDownOffset: carryOffset);
        else if (continuousHandoffUp)
            host.PrepareContinuousScrollHandoff(carryUpDelta: carryUpDelta);
        if (!await NavigateToChapterAsync(host, chapter, new Uri(_epubDocument.Chapters[chapter]),
                allowPartialCompose: !continuousHandoffUp)) return;
        if (fromEnd && !continuousHandoffUp)
        {
            await EnsureLayoutCompleteAsync();
            host.SeekToBoundary(toEnd: true);
        }
        UpdateEpubLabels();
        _ = SaveProgressSoonAsync(force: true);
    }

    private void ShowEdgeNotice(int direction) =>
        _shell.ShowToast(direction < 0 ? UiText.Get("已经是第一页") : UiText.Get("已经是最后一页"));

    private async Task MoveChapterAsync(int direction)
    {
        _tts?.NotifyManualNavigation();
        try
        {
            if (_isPdf) await GoToPdfPageAsync(_pdfPage + direction);
            else if (_readerHost is NativeReaderHost host)
                await GoToChapterAsync(host, _chapterIndex + direction, fromEnd: false, direction);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { _shell.ShowToast(UiText.Localize(exception.Message)); }
    }
}
