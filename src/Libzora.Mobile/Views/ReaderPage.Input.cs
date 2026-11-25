using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Progress saving, chrome visibility and touch gestures.
/// </summary>
public partial class ReaderPage
{
    private async Task<bool> SaveProgressSoonAsync(bool force)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return false;
        return await SaveProgressCoreAsync(force);
    }

    // The caller owns an operation lease for the entire save.
    private async Task<bool> SaveProgressCoreAsync(bool force)
    {
        if (!_ready || _bookFile is null || _readerHost is null) return true;
        var now = DateTimeOffset.UtcNow;
        CountReadingTime(now);
        var minSaveInterval = IsPowerSavingActive ? TimeSpan.FromSeconds(25) : TimeSpan.FromSeconds(1.2);
        if (!force && now - _lastProgressSave < minSaveInterval) return true;
        _lastProgressSave = now;

        long readingSeconds = 0;
        long listeningSeconds = 0;
        try
        {
            var token = force ? CancellationToken.None : _lifetime.Token;
            // A note detour still counts as reading. Commit its time against
            // the durable body baseline, never against the notes chapter.
            var preservingBody = _footnoteProgressSuppressed;
            var progress = preservingBody ? _footnoteBodyProgress : new ReaderProgressRow(
                _book.Id,
                _bookFile.Id,
                _isPdf ? $"{PdfPageChapterPrefix}{_pdfPage}" : ChapterPathAt(_chapterIndex),
                null,
                _isPdf ? Math.Max(0, _pdfPage - 1) : _chapterIndex,
                _isPdf ? 0 : (int)Math.Clamp(_epubPosition, 0, int.MaxValue),
                Math.Clamp(_overallProgress * 100, 0, 100),
                _isPdf ? 1 : _layout.FlowMode,
                now)
            { ContentPosition = (_readerHost as IReaderPositionHost)?.CaptureContentPosition() };
            if (progress is null) return false;
            if (!preservingBody)
            {
                await _modules.Reader.Data.SaveProgressAsync(progress, token);
                if (_lastSavedProgress is null || _lastSavedProgress.UpdatedAt <= progress.UpdatedAt)
                    _lastSavedProgress = progress;
            }

            // The dashboard, library grid and continue card read reading stats.
            readingSeconds = Interlocked.Exchange(ref _unsavedSeconds, 0);
            listeningSeconds = Interlocked.Exchange(ref _unsavedListeningSeconds, 0);
            var totalSeconds = readingSeconds + listeningSeconds;
            var chapters = _isPdf ? _pdfPageCount : _epubDocument?.Chapters.Count ?? 0;
            await _modules.Reader.Data.AddReadingTimeAsync(
                _book.Id, _bookFile.Id, totalSeconds, progress.ProgressPercent, progress.ChapterIndex, chapters,
                listeningSeconds, token);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            Interlocked.Add(ref _unsavedSeconds, readingSeconds);
            Interlocked.Add(ref _unsavedListeningSeconds, listeningSeconds);
            return false;
        }
        catch
        {
            Interlocked.Add(ref _unsavedSeconds, readingSeconds);
            Interlocked.Add(ref _unsavedListeningSeconds, listeningSeconds);
            // Progress is best-effort; a transient database error must not
            // close the reading surface.
            return false;
        }
    }

    /// <summary>Time since the last interaction counts, capped so an idle phone adds nothing.</summary>
    private void CountReadingTime(DateTimeOffset now)
    {
        Interlocked.Add(ref _unsavedSeconds, _activityClock.CountUntil(now));
        Interlocked.Add(ref _unsavedListeningSeconds, _listeningClock.CountUntil(now));
    }

    private void ShowError(Func<string> message)
    {
        _nativeErrorText = message;
        _nativeError = message();
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = !_usesNativeFrontend;
        ErrorText.Text = _nativeError;
        SetChromeVisible(false);
    }

    public void SetChromeVisible(bool visible)
    {
        if (visible)
        {
            _longPress.Stop();
            _selectionArmed = false;
            _expectSelection = false;
            ClearSelection();
        }
        if (_usesNativeFrontend)
        {
            _nativeChromeVisible = visible;
            NotifyNativeState();
            return;
        }
        TopChrome.Classes.Set("visible", visible);
        BottomChrome.Classes.Set("visible", visible);
        ProgressSliderGroup.SetHostVisible(visible);
        // The reading-aloud bar stays reachable above the toolbar.
        var speechBottom = visible ? BottomChrome.Bounds.Height + 24 : 40;
        SpeechBar.Margin = new Thickness(12, 0, 12, speechBottom);
        SpeechDock.Margin = new Thickness(0, 0, 0, speechBottom);
    }

    private CancellationTokenSource? _transitionCts;
    private Task? _activeTransitionTask;
    private long _activeTurnId;
    private ReaderTransitionPlayer.InteractiveTransitionSession? _interactiveDrag;
    private int _interactiveDragDirection;
    private int _interactiveDragVisualDirection;
    private bool _dragAttempted;
    private double _lastDragX;
    private double _lastDragY;
    private long _lastDragTicks;
    private double _dragVelocityX;
    private double _dragVelocityY;
    private DispatcherTimer? _scrollInertiaTimer;
    private double _scrollInertiaVelocityY;
    private bool _suppressScrollUpdatesDuringDrag;
    private (double Top, double Left)? _pendingDragScroll;

    private ReaderTransitionSurface CreateTransitionSurface() => new(
        ReaderHostSlot,
        TransitionSnapshot,
        TransitionTrail,
        TransitionFront,
        TransitionEdge,
        ReaderPaper,
        ReaderHostSlot);

    private static int ResolveTransitionAnimation(MobilePageAnimation animation) => animation switch
    {
        MobilePageAnimation.None => ReaderTransitionPlayer.AnimationNone,
        MobilePageAnimation.Fade => ReaderTransitionPlayer.AnimationFade,
        _ => ReaderTransitionPlayer.AnimationPush
    };

    private void StopScrollInertia()
    {
        _scrollInertiaTimer?.Stop();
        _scrollInertiaVelocityY = 0;
    }

    private void StartScrollInertia(NativeReaderHost host)
    {
        var initialVelocity = Math.Clamp(-_dragVelocityY, -3600d, 3600d);
        if (Math.Abs(initialVelocity) < 120) return;
        _scrollInertiaVelocityY = initialVelocity;
        _scrollInertiaTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (_disposed || _turning || !IsContinuousVerticalScroll || IsMangaZoomed
                || _readerHost is not NativeReaderHost scrollHost)
            {
                StopScrollInertia();
                return;
            }
            var step = _scrollInertiaVelocityY * 0.016;
            _scrollInertiaVelocityY *= 0.92;
            if (Math.Abs(_scrollInertiaVelocityY) < 25)
            {
                StopScrollInertia();
                return;
            }
            scrollHost.ScrollByPixel(step);
        });
        _scrollInertiaTimer.Stop();
        _scrollInertiaTimer.Start();
    }

    private Point TranslateToHost(Point pointInPaper)
    {
        if (_readerHost?.View is Visual hostVisual
            && ReaderPaper.TranslatePoint(pointInPaper, hostVisual) is { } translated)
        {
            return translated;
        }
        return pointInPaper;
    }

    private void ApplyScrollUpdate(double top, double left)
    {
        _lastScrollTop = top;
        _lastScrollLeft = left;
        _epubPosition = _layout.FlowMode == 0 ? Math.Max(0, top) : Math.Max(0, left);
        if (_ready
            && _activeSyncedMarker is not null
            && _layout.FlowMode == 0
            && Math.Abs(_epubPosition - _syncedMarkerScrollOrigin) >= Math.Max(120, ReaderHostSlot.Bounds.Height * 0.45))
        {
            ClearActiveSyncedMarker();
        }
        DismissFootnoteReturnIfReaderMoved(top, left);
        UpdateEpubLabels();
        _ = SaveProgressSoonAsync(force: false);
    }

    private void CommitPendingDragScroll()
    {
        _suppressScrollUpdatesDuringDrag = false;
        ClearActiveSyncedMarker();
        if (_pendingDragScroll is { } pending)
        {
            _pendingDragScroll = null;
            ApplyScrollUpdate(pending.Top, pending.Left);
        }
    }

    private void RevertPendingDragScroll()
    {
        if (_readerHost is NativeReaderHost host && _interactiveDragDirection != 0)
        {
            _suppressScrollUpdatesDuringDrag = true;
            try
            {
                host.TurnPage(-_interactiveDragDirection);
                host.SetResumeMarker(ToResumeMarker(_activeSyncedMarker));
            }
            finally
            {
                _pendingDragScroll = null;
                _suppressScrollUpdatesDuringDrag = false;
            }
        }
        else
        {
            _pendingDragScroll = null;
            _suppressScrollUpdatesDuringDrag = false;
        }
        _interactiveDragDirection = 0;
    }

    private void CancelActiveTransition()
    {
        if (_transitionCts is { } cts)
        {
            _transitionCts = null;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task CancelAndAwaitActiveTransitionAsync()
    {
        CancelActiveTransition();
        if (_activeTransitionTask is { } active)
        {
            try { await active; }
            catch (OperationCanceledException) { }
            catch { }
        }
    }

    private void CancelInteractiveDragImmediate()
    {
        if (_interactiveDrag is not { } drag) return;
        _interactiveDrag = null;
        drag.Cancel(RevertPendingDragScroll);
    }

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        TrackTouchPressed(e);
        // The native frontend draws its selection toolbar in SwiftUI, so the
        // Avalonia layer stays hidden while a selection is active.
        if (SelectionLayer.IsVisible || _usesNativeFrontend && _selection is not null)
        {
            // A tap outside the floating toolbar dismisses the selection
            // instead of turning the page or toggling the chrome.
            CancelPress();
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (_touches.Count > 1)
        {
            // A second finger starts a pinch, never a tap or selection.
            CancelInteractiveDragImmediate();
            CancelPress();
            e.Handled = true;
            return;
        }
        CancelInteractiveDragImmediate();
        if (!_turning && _activeTransitionTask is { IsCompleted: false })
        {
            CancelActiveTransition();
        }
        StopScrollInertia();
        _pressPoint = e.GetPosition(ReaderPaper);
        _lastPanPoint = _pressPoint.Value;
        _lastDragX = _pressPoint.Value.X;
        _lastDragY = _pressPoint.Value.Y;
        _lastDragTicks = Stopwatch.GetTimestamp();
        _dragVelocityX = 0;
        _dragVelocityY = 0;
        _dragAttempted = false;
        _pressMoved = false;
        _panning = false;
        _selectionArmed = false;
        _expectSelection = false;
        _tapConsumed = false;
        if (MobileReaderPagingPolicy.AllowsSurfaceSelection(IsChromeVisible, IsMangaPannable))
        {
            _longPress.Start();
        }
        if (IsChromeVisible)
        {
            e.Handled = true;
        }
    }

    private void CancelPress()
    {
        _longPress.Stop();
        _pressPoint = null;
        _selectionArmed = false;
        _expectSelection = false;
        _tapConsumed = true;
        CancelPendingDelayedTap();
    }

    private void CancelSurfaceGesture(bool clearTouches = true)
    {
        CancelInteractiveDragImmediate();
        CancelPress();
        StopScrollInertia();
        _pressMoved = false;
        _panning = false;
        _dragAttempted = false;
        if (clearTouches) ResetTouches();
    }

    /// <summary>
    /// A long press selects the character or word under the finger and opens
    /// the floating menu immediately. If the point has no text (a link,
    /// annotation or margin), the drag still arms an ordinary selection.
    /// </summary>
    private void OnLongPress(object? sender, EventArgs e)
    {
        _longPress.Stop();
        if (_pressPoint is not { } point || _pressMoved || !MobileReaderPagingPolicy.AllowsSurfaceSelection(IsChromeVisible, IsMangaPannable)) return;
        _selectionArmed = true;
        if (_readerHost is not IReaderSelectionHost host) return;

        // The host reports the new selection synchronously from inside
        // SelectWordAt, so arm the bridge event before calling it.
        var hostPoint = TranslateToHost(point);
        _expectSelection = true;
        if (!host.SelectWordAt(hostPoint.X, hostPoint.Y)) _expectSelection = false;
    }

    /// <summary>Stops the host from treating a finger drag as a text selection.</summary>
    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (TrackTouchMoved(e))
        {
            e.Handled = true;
            return;
        }
        if (_pressPoint is not { } start || _selectionArmed) return;
        var current = e.GetPosition(ReaderPaper);
        var delta = current - _lastPanPoint;
        _lastPanPoint = current;

        var nowTicks = Stopwatch.GetTimestamp();
        var dt = (nowTicks - _lastDragTicks) / (double)Stopwatch.Frequency;
        if (dt > 0.002)
        {
            var instantVelocityX = (current.X - _lastDragX) / dt;
            _dragVelocityX = _dragVelocityX * 0.4 + instantVelocityX * 0.6;
            _lastDragX = current.X;
            var instantVelocityY = (current.Y - _lastDragY) / dt;
            _dragVelocityY = _dragVelocityY * 0.4 + instantVelocityY * 0.6;
            _lastDragY = current.Y;
            _lastDragTicks = nowTicks;
        }

        if (Math.Abs(current.X - start.X) > TapSlop || Math.Abs(current.Y - start.Y) > TapSlop)
        {
            _pressMoved = true;
            _longPress.Stop();
        }
        if (IsChromeVisible)
        {
            e.Handled = true;
            return;
        }
        if (IsPdfPannable)
        {
            var moveDx = current.X - start.X;
            var moveDy = current.Y - start.Y;
            if (!IsPdfContinuous
                && _pressMoved
                && Math.Abs(moveDx) >= Math.Abs(moveDy) * SwipeDominance
                && _readerHost is NativePdfReaderHost pdfHost
                && pdfHost.IsAtHorizontalEdge(-1)
                && pdfHost.IsAtHorizontalEdge(1))
            {
                pdfHost.CancelPointerInteraction();
                e.Handled = true;
            }
            return;
        }
        if ((IsMangaPannable || IsEpubScrollMode) && _pressMoved && _readerHost is NativeReaderHost mangaHost)
        {
            _panning = true;
            if (IsMangaZoomed)
            {
                mangaHost.PanMangaBy(new Vector(-delta.X, -delta.Y));
            }
            else if (IsContinuousVerticalScroll)
            {
                mangaHost.ScrollByPixel(-delta.Y);
            }
            e.Handled = true;
            return;
        }

        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (_interactiveDrag is null && !_dragAttempted && _pressMoved
            && Math.Abs(dx) >= Math.Abs(dy) * SwipeDominance
            && Math.Abs(dx) > TapSlop)
        {
            _dragAttempted = true;
            TryBeginInteractiveDrag(dx);
        }

        if (_interactiveDrag is { } drag)
        {
            var (_, width) = ResolveTapViewport();
            var progress = (_interactiveDragVisualDirection >= 0 ? -dx : dx) / width;
            drag.Update(progress);
        }
        e.Handled = true;
    }

    private void TryBeginInteractiveDrag(double dx)
    {
        if (!_ready || IsChromeVisible || _isPdf || _turning) return;
        if (_activeTransitionTask is { IsCompleted: false }) return;
        if (_readerHost is not NativeReaderHost host || !host.IsLayoutComplete) return;

        var animation = IsPowerSavingActive
            ? ReaderTransitionPlayer.AnimationNone
            : ResolveTransitionAnimation(_shell.Preferences.Current.ReaderPageAnimation);
        if (IsEpubScrollMode || animation != ReaderTransitionPlayer.AnimationPush)
            return;

        var direction = MobileReaderPagingPolicy.SwipeDirection(dx, _layout.VerticalWriting, _isPdf, IsRightToLeftPaging);
        if (direction == 0 || !host.CanTurn(direction)) return;

        var visualDirection = MobileReaderPagingPolicy.VisualDirection(direction, _layout.VerticalWriting, _isPdf, IsRightToLeftPaging);
        _suppressScrollUpdatesDuringDrag = true;
        _pendingDragScroll = null;
        var session = ReaderTransitionPlayer.BeginInteractive(CreateTransitionSurface(), animation, visualDirection);
        if (session is null)
        {
            _suppressScrollUpdatesDuringDrag = false;
            return;
        }

        host.SetResumeMarker(null);
        host.TurnPage(direction);
        _interactiveDrag = session;
        _interactiveDragDirection = direction;
        _interactiveDragVisualDirection = visualDirection;
    }

    private DateTimeOffset _lastPdfTapAt = DateTimeOffset.MinValue;
    private Point _lastPdfTapPoint;
    private int _pendingDelayedTapId;

    private void CancelPendingDelayedTap() => _pendingDelayedTapId++;

    private void ScheduleDelayedTap(double ratio, int delayMs = 220)
    {
        var tapId = ++_pendingDelayedTapId;
        _ = HandleDelayedTapAsync(tapId, ratio, delayMs);
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (TrackTouchReleased(e))
        {
            CancelInteractiveDragImmediate();
            CancelPress();
            ConsumeSurfaceRelease(e);
            return;
        }
        _longPress.Stop();
        if (_pressPoint is not { } start)
        {
            CancelInteractiveDragImmediate();
            ConsumeSurfaceRelease(e);
            return;
        }
        _pressPoint = null;
        if (IsChromeVisible)
        {
            e.Handled = true;
        }
        if (_selectionArmed)
        {
            _selectionArmed = false;
            _expectSelection = true;
            return;
        }

        if (_interactiveDrag is { } drag)
        {
            _interactiveDrag = null;
            ConsumeSurfaceRelease(e);
            var forwardFlick = _interactiveDragVisualDirection >= 0
                ? _dragVelocityX < -260
                : _dragVelocityX > 260;
            var backwardFlick = _interactiveDragVisualDirection >= 0
                ? _dragVelocityX > 260
                : _dragVelocityX < -260;
            var commit = !backwardFlick && (drag.Progress >= 0.25 || forwardFlick);
            _ = FinishInteractiveDragAsync(drag, commit);
            return;
        }

        var end = e.GetPosition(ReaderPaper);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dominant = Math.Abs(dx) >= Math.Abs(dy) * SwipeDominance;
        if ((IsMangaPannable || IsEpubScrollMode) && (_panning || _pressMoved))
        {
            ConsumeSurfaceRelease(e);
            if (IsContinuousVerticalScroll && !IsMangaZoomed && _readerHost is NativeReaderHost scrollHost)
            {
                StartScrollInertia(scrollHost);
            }
            return;
        }
        if (Math.Abs(dx) >= SwipeDistance && dominant)
        {
            if (!IsPdfPannable)
            {
                if (_readerHost is NativePdfReaderHost nonPanPdf)
                    nonPanPdf.CancelPointerInteraction();
                ConsumeSurfaceRelease(e);
                HandleSwipe(dx);
            }
            else if (_isPdf && !IsPdfContinuous
                && _readerHost is NativePdfReaderHost pdf
                && ((pdf.IsAtHorizontalEdge(-1) && pdf.IsAtHorizontalEdge(1))
                    || (Math.Abs(dx) >= 72 && pdf.IsAtHorizontalEdge(dx < 0 ? 1 : -1))))
            {
                pdf.CancelPointerInteraction();
                ConsumeSurfaceRelease(e);
                HandleSwipe(dx);
            }
        }
        else if (!_pressMoved && Math.Abs(dx) <= TapSlop && Math.Abs(dy) <= TapSlop)
        {
            // Double-tap zooms to 2x at the tapped point or resets back to fit on PDF/Manga.
            var now = DateTimeOffset.UtcNow;
            if ((_isPdf || IsMangaMode) && !IsChromeVisible && (now - _lastPdfTapAt).TotalMilliseconds <= 300
                && Math.Abs(end.X - _lastPdfTapPoint.X) <= 36 && Math.Abs(end.Y - _lastPdfTapPoint.Y) <= 36)
            {
                CancelPendingDelayedTap();
                _lastPdfTapAt = DateTimeOffset.MinValue;
                _tapConsumed = true;
                var hostPoint = TranslateToHost(end);
                if (_isPdf) _ = TogglePdfDoubleTapZoomAsync(hostPoint);
                else ToggleMangaDoubleTapZoom(hostPoint);
                return;
            }

            // The host reports link, annotation and figure hits after this
            // tunnel handler, but only a gesture that stayed a tap reaches it.
            var (viewportLeft, viewportWidth) = ResolveTapViewport();
            var ratio = MobileReaderPagingPolicy.TapRatio(end.X, viewportLeft, viewportWidth);
            if ((_isPdf || IsMangaMode) && !IsChromeVisible)
            {
                _lastPdfTapAt = now;
                _lastPdfTapPoint = end;
                ScheduleDelayedTap(ratio);
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_tapConsumed) HandleTap(ratio);
                }, DispatcherPriority.Background);
            }
        }
        else if (!IsPdfPannable)
        {
            ConsumeSurfaceRelease(e);
        }
    }

    private async Task FinishInteractiveDragAsync(
        ReaderTransitionPlayer.InteractiveTransitionSession drag,
        bool commit)
    {
        if (commit)
        {
            MarkNativeNavigation();
            _tts?.NotifyManualNavigation();
        }

        await CancelAndAwaitActiveTransitionAsync();
        using var transitionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _transitionCts = transitionCts;
        var target = commit ? 1d : 0d;
        var remaining = Math.Abs(target - drag.Progress);
        var durationMs = Math.Clamp(remaining * 380d, 115d, 380d);
        var task = drag.FinishAsync(
            target,
            durationMs,
            transitionCts.Token,
            onBeforeReset: commit ? CommitPendingDragScroll : RevertPendingDragScroll);
        _activeTransitionTask = task;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_activeTransitionTask, task))
                _activeTransitionTask = null;
            if (ReferenceEquals(_transitionCts, transitionCts))
            {
                _transitionCts = null;
                transitionCts.Dispose();
            }
        }
    }

    private async Task HandleDelayedTapAsync(int tapId, double ratio, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, _lifetime.Token);
            if (tapId == _pendingDelayedTapId)
            {
                HandleTap(ratio);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TogglePdfDoubleTapZoomAsync(Point anchor)
    {
        if (_readerHost is not NativePdfReaderHost pdf) return;
        var nextZoom = pdf.Zoom > 1.05 ? 1.0 : 2.0;
        _shell.Preferences.Update(value => value.WithPdfZoom(_bookFile?.Id, nextZoom));
        if (nextZoom > 1.05)
        {
            pdf.SetHandToolMode(true);
            await pdf.SetZoomAsync(nextZoom, anchor);
        }
        else
        {
            await pdf.SetFitModeAsync(DesiredPdfFitMode);
            pdf.SetHandToolMode(IsPdfPannable);
        }
        NotifyNativeState();
    }

    private void ToggleMangaDoubleTapZoom(Point anchor)
    {
        if (_readerHost is not NativeReaderHost host) return;
        var nextZoom = host.MangaZoom > 1.05 ? 1.0 : 2.0;
        host.SetMangaZoom(nextZoom, anchor);
    }

    private (double Left, double Width) ResolveTapViewport()
    {
        var fallbackWidth = Math.Max(1, ReaderPaper.Bounds.Width);
        if (_usesNativeFrontend && _nativeViewportHint is { Width: >= 2 } hint)
        {
            var left = Math.Clamp(_nativeViewportLeft, 0, Math.Max(0, fallbackWidth - 2));
            var width = Math.Clamp(hint.Width, 2, Math.Max(2, fallbackWidth - left));
            return (left, width);
        }
        return (0, fallbackWidth);
    }

    private static void ConsumeSurfaceRelease(PointerReleasedEventArgs e)
    {
        // Movement is handled above the rendering host. Cancel its captured
        // press as well, so the release cannot activate an image or link.
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void HandleSwipe(double dx)
    {
        if (IsChromeVisible) SetChromeVisible(false);
        else _ = TurnAsync(MobileReaderPagingPolicy.SwipeDirection(dx, _layout.VerticalWriting, _isPdf, IsRightToLeftPaging));
    }

    private void HandleTap(double ratio)
    {
        if (IsChromeVisible) SetChromeVisible(false);
        else
        {
            if (IsContinuousVerticalScroll && _readerHost is NativeReaderHost scrollHost)
            {
                if (ratio is >= 1d / 3 and <= 2d / 3)
                {
                    SetChromeVisible(true);
                }
                else
                {
                    var step = Math.Max(120, ReaderHostSlot.Bounds.Height * 0.85);
                    scrollHost.ScrollByPixel(ratio < 1d / 3 ? -step : step);
                }
                return;
            }

            var direction = MobileReaderPagingPolicy.TapDirection(ratio, _layout.VerticalWriting, _isPdf, IsRightToLeftPaging);
            if (direction == 0) SetChromeVisible(true);
            else _ = TurnAsync(direction);
        }
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
            await EnsureLayoutCompleteAsync();
            if (_isPdf && (_pdfPage + direction < 1 || _pdfPage + direction > _pdfPageCount))
            {
                ShowEdgeNotice(direction);
                return;
            }
            if (!_isPdf && _readerHost is NativeReaderHost current && !current.CanTurn(direction))
            {
                // The next spine file may be a footnote container; report the
                // edge instead of animating toward a page that never opens.
                var target = _epubDocument is null
                    ? -1
                    : ResolveReadingChapter(_chapterIndex + direction, direction);
                if (_epubDocument is null || target < 0 || target >= _epubDocument.Chapters.Count)
                {
                    ShowEdgeNotice(direction);
                    return;
                }
            }

            var surface = CreateTransitionSurface();
            var animation = IsPowerSavingActive
                ? ReaderTransitionPlayer.AnimationNone
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
            // Unlock rapid consecutive page turns once the page flip has switched
            // content and entered its visual animation phase; the next turn will
            // cleanly settle the previous animation first.
            while (!transitionTask.IsCompleted && !contentSwitched)
            {
                await Task.WhenAny(transitionTask, Task.Delay(8, transitionToken));
            }
            if (contentSwitched && turnId == _activeTurnId) _turning = false;
            await transitionTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
        finally
        {
            if (transitionTask is not null && ReferenceEquals(_activeTransitionTask, transitionTask))
                _activeTransitionTask = null;
            if (ReferenceEquals(_transitionCts, transitionCts))
                _transitionCts = null;
            transitionCts?.Dispose();
            if (turnId == _activeTurnId)
                _turning = false;
        }
    }

    private async Task GoToChapterAsync(NativeReaderHost host, int chapter, bool fromEnd, int direction)
    {
        ClearActiveSyncedMarker();
        HideSelectionBar();
        if (_epubDocument is null) return;
        // Footnote containers are not reading content: stepping past the last
        // real chapter reports the edge instead of opening a blank notes page.
        chapter = ResolveReadingChapter(chapter, direction < 0 ? -1 : 1);
        if (chapter < 0 || chapter >= _epubDocument.Chapters.Count)
        {
            ShowEdgeNotice(direction);
            return;
        }

        if (!await NavigateToChapterAsync(host, chapter, new Uri(_epubDocument.Chapters[chapter]))) return;
        if (fromEnd)
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
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }
}
