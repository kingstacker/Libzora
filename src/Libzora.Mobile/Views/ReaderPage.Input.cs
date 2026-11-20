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

        long seconds = 0;
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
                1,
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
            seconds = Interlocked.Exchange(ref _unsavedSeconds, 0);
            var chapters = _isPdf ? _pdfPageCount : _epubDocument?.Chapters.Count ?? 0;
            await _modules.Reader.Data.AddReadingTimeAsync(
                _book.Id, _bookFile.Id, seconds, progress.ProgressPercent, progress.ChapterIndex, chapters, token);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            Interlocked.Add(ref _unsavedSeconds, seconds);
            return false;
        }
        catch
        {
            Interlocked.Add(ref _unsavedSeconds, seconds);
            // Progress is best-effort; a transient database error must not
            // close the reading surface.
            return false;
        }
    }

    /// <summary>Time since the last interaction counts, capped so an idle phone adds nothing.</summary>
    private void CountReadingTime(DateTimeOffset now)
    {
        Interlocked.Add(ref _unsavedSeconds, _activityClock.CountUntil(now));
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
            CancelPress();
            e.Handled = true;
            return;
        }
        _pressPoint = e.GetPosition(ReaderHostSlot);
        _lastPanPoint = _pressPoint.Value;
        _pressMoved = false;
        _panning = false;
        _selectionArmed = false;
        _expectSelection = false;
        _tapConsumed = false;
        if (!IsMangaPannable)
        {
            _longPress.Start();
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

    /// <summary>
    /// A long press selects the character or word under the finger and opens
    /// the floating menu immediately. If the point has no text (a link,
    /// annotation or margin), the drag still arms an ordinary selection.
    /// </summary>
    private void OnLongPress(object? sender, EventArgs e)
    {
        _longPress.Stop();
        if (_pressPoint is not { } point || _pressMoved) return;
        _selectionArmed = true;
        if (_readerHost is not IReaderSelectionHost host) return;

        // The host reports the new selection synchronously from inside
        // SelectWordAt, so arm the bridge event before calling it.
        _expectSelection = true;
        if (!host.SelectWordAt(point.X, point.Y)) _expectSelection = false;
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
        var current = e.GetPosition(ReaderHostSlot);
        var delta = current - _lastPanPoint;
        _lastPanPoint = current;
        if (Math.Abs(current.X - start.X) > TapSlop || Math.Abs(current.Y - start.Y) > TapSlop)
        {
            _pressMoved = true;
            _longPress.Stop();
        }
        if (IsPdfPannable) return;
        if (IsMangaPannable && _pressMoved && _readerHost is NativeReaderHost mangaHost)
        {
            _panning = true;
            if (IsMangaZoomed)
            {
                mangaHost.PanMangaBy(new Vector(-delta.X, -delta.Y));
            }
            else if (IsMangaVerticalScroll)
            {
                mangaHost.ScrollByPixel(-delta.Y);
            }
        }
        e.Handled = true;
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
            CancelPress();
            ConsumeSurfaceRelease(e);
            return;
        }
        _longPress.Stop();
        if (_pressPoint is not { } start)
        {
            ConsumeSurfaceRelease(e);
            return;
        }
        _pressPoint = null;
        if (_selectionArmed)
        {
            _selectionArmed = false;
            _expectSelection = true;
            return;
        }

        var end = e.GetPosition(ReaderHostSlot);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dominant = Math.Abs(dx) >= Math.Abs(dy) * SwipeDominance;
        if (IsMangaPannable && (_panning || _pressMoved))
        {
            ConsumeSurfaceRelease(e);
            return;
        }
        if (Math.Abs(dx) >= SwipeDistance && dominant)
        {
            if (!IsPdfPannable)
            {
                ConsumeSurfaceRelease(e);
                HandleSwipe(dx);
            }
            else if (_isPdf && !IsPdfContinuous && Math.Abs(dx) >= 72
                && _readerHost is NativePdfReaderHost pdf
                && pdf.IsAtHorizontalEdge(dx < 0 ? 1 : -1))
            {
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
                if (_isPdf) _ = TogglePdfDoubleTapZoomAsync(end);
                else ToggleMangaDoubleTapZoom(end);
                return;
            }

            // The host reports link, annotation and figure hits after this
            // tunnel handler, but only a gesture that stayed a tap reaches it.
            var ratio = end.X / Math.Max(1, ReaderHostSlot.Bounds.Width);
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
            if (IsMangaVerticalScroll && _readerHost is NativeReaderHost scrollHost)
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
        MarkNativeNavigation();
        _tts?.NotifyManualNavigation();
        _turning = true;
        try
        {
            await EnsureLayoutCompleteAsync();
            if (_isPdf && (_pdfPage + direction < 1 || _pdfPage + direction > _pdfPageCount))
            {
                ShowEdgeNotice(direction);
                return;
            }
            if (!_isPdf && _readerHost is NativeReaderHost current
                && !current.CanTurn(direction)
                && (_epubDocument is null || _chapterIndex + direction < 0
                    || _chapterIndex + direction >= _epubDocument.Chapters.Count))
            {
                ShowEdgeNotice(direction);
                return;
            }

            var surface = new ReaderTransitionSurface(
                ReaderHostSlot, TransitionSnapshot, TransitionTrail, TransitionFront, TransitionEdge, ReaderPaper);
            var animation = IsPowerSavingActive ? 0 : (int)_shell.Preferences.Current.ReaderPageAnimation;
            await ReaderTransitionPlayer.RunAsync(surface, animation,
                MobileReaderPagingPolicy.VisualDirection(direction, _layout.VerticalWriting, _isPdf, IsRightToLeftPaging), async () =>
            {
                if (_isPdf)
                {
                    await GoToPdfPageAsync(_pdfPage + direction);
                    return true;
                }

                if (_readerHost is not NativeReaderHost host) return false;
                if (!host.TurnPage(direction))
                    await GoToChapterAsync(host, _chapterIndex + direction, fromEnd: direction < 0, direction);
                return true;
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
        finally
        {
            _turning = false;
        }
    }

    private async Task GoToChapterAsync(NativeReaderHost host, int chapter, bool fromEnd, int direction)
    {
        HideSelectionBar();
        if (_epubDocument is null) return;
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
