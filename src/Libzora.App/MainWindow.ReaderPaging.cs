using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Page turns, continuous-scroll chapter advance and transitions.
/// </summary>
public partial class MainWindow
{
    private async Task SkipShortReaderChapterIfNeededAsync(
        int enteredIndex,
        CancellationToken cancellationToken)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        try
        {
            await Task.Delay(60, cancellationToken);
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return '{}'; return JSON.stringify({ sh: el.scrollHeight, ch: el.clientHeight, sw: el.scrollWidth, cw: el.clientWidth }); })();");
            if (result is null) return;
            var raw = result.Trim().Trim('"');
            if (raw.Length == 0 || raw == "{}") return;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.VerticalWriting;
            var scrollSize = horizontal
                ? ReadDouble(root, "sw")
                : ReadDouble(root, "sh");
            var clientSize = horizontal
                ? ReadDouble(root, "cw")
                : ReadDouble(root, "ch");
            if (scrollSize <= 0 || clientSize <= 0) return;
            if (scrollSize > clientSize + 16) return;
            if (_readerChapterIndex != enteredIndex) return;
            if (_readerChapterIndex + 1 >= _readerDocument.Chapters.Count) return;
            if (_readerContinuousSkipDepth >= 5) return;
            _readerContinuousSkipDepth++;
            await MoveReaderChapterAsync(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private static async Task ObserveReaderTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // DOM events can race the host being replaced or disposed. A stale
            // event must never become an unobserved UI exception.
        }
    }

    private async Task SaveReaderProgressAfterScrollAsync(int sequence)
    {
        var token = _readerSession.GetTokenOr(CancellationToken.None);
        try
        {
            await Task.Delay(700, token);
            if (sequence != _readerProgressSaveSequence || (_readerDocument is null && !_readerIsPdf)) return;
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    // ------------------------------------------------------------------
    // Scroll-edge chapter advance (滚动接章). In scroll mode the reader
    // continuously advances to the next chapter when the scroll position
    // reaches the bottom edge and steps back at the top edge, with a lock
    // that prevents one chapter from triggering repeated transitions.
    // Mirrors the WinUI reference's PollReaderScrollAsync.
    // ------------------------------------------------------------------

    private void ResetReaderContinuousEdgeTracking()
    {
        _readerContinuousPositionInitialized = false;
        _readerPreviousScrollPosition = 0;
        _readerLastNearTop = false;
        _readerLastNearBottom = false;
        _readerLinuxTextFallbackWheelDeltaRemainder = 0;
        _readerLinuxTextFallbackContinuousWheelDirection = 0;
        _readerLinuxTextFallbackContinuousWheelLastTick = 0;
    }

    private void PrimeReaderContinuousEdgeTracking()
    {
        if (_readerIsPdf || _readerLayout.FlowMode != 0)
        {
            ResetReaderContinuousEdgeTracking();
            return;
        }

        var vertical = _readerLayout.VerticalWriting;
        var scrollSize = vertical ? _readerScrollWidth : _readerScrollHeight;
        var clientSize = vertical ? _readerClientWidth : _readerClientHeight;
        _readerPreviousScrollPosition = _readerScrollPosition;
        _readerContinuousPositionInitialized = true;
        _readerLastNearTop = _readerScrollPosition <= 48;
        _readerLastNearBottom = scrollSize > 0
            && clientSize > 0
            && _readerScrollPosition + clientSize >= scrollSize - 48;
    }

    private void TryAdvanceReaderScrollChapter()
    {
        if (_readerIsPdf || _readerScrollPollRunning) return;
        if (_readerLayout.FlowMode != 0) return;
        if (_readerDocument is null || _readerDocument.Chapters.Count <= 1) return;
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerDocument.Chapters.Count) return;
        if (_readerZenMode) return;

        var vertical = _readerLayout.VerticalWriting;
        var scrollSize = vertical ? _readerScrollWidth : _readerScrollHeight;
        var clientSize = vertical ? _readerClientWidth : _readerClientHeight;
        var scrollPosition = _readerScrollPosition;
        if (scrollSize <= 0 || clientSize <= 0) return;

        var nearTop = scrollPosition <= 48;
        var nearBottom = scrollPosition + clientSize >= scrollSize - 48;
        var overflows = scrollSize > clientSize + 16;
        if (!_readerContinuousPositionInitialized)
        {
            _readerContinuousPositionInitialized = true;
            _readerPreviousScrollPosition = scrollPosition;
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
            return;
        }

        var movement = scrollPosition - _readerPreviousScrollPosition;
        _readerPreviousScrollPosition = scrollPosition;

        if (!nearTop && !nearBottom)
        {
            // Scrolled into the middle: release the continuous lock so the
            // next edge transition is treated as a fresh user action.
            _readerContinuousLocked = false;
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
            return;
        }

        if (_readerContinuousLocked)
        {
            var forceForward = _readerContinuousDirection > 0
                && overflows
                && nearBottom
                && DateTimeOffset.UtcNow - _readerLastChapterChange > TimeSpan.FromMilliseconds(500);
            var forceBackward = _readerContinuousDirection < 0
                && overflows
                && nearTop
                && DateTimeOffset.UtcNow - _readerLastChapterChange > TimeSpan.FromMilliseconds(500);
            if (forceForward || forceBackward)
                _readerContinuousLocked = false;
            else
                return;
        }

        if (nearBottom && !_readerLastNearBottom && movement > 0.5)
        {
            TryMoveReaderChapterFromContinuousEdge(1);
        }
        else if (nearTop && !_readerLastNearTop && movement < -0.5)
        {
            TryMoveReaderChapterFromContinuousEdge(-1);
        }
        _readerLastNearTop = nearTop;
        _readerLastNearBottom = nearBottom;
    }

    private void TryMoveReaderChapterFromContinuousEdge(int direction)
    {
        if (direction == 0 || _readerIsPdf || _readerLayout.FlowMode != 0) return;
        if (_readerScrollPollRunning || _readerDocument is null) return;
        if (direction < 0 && _readerChapterIndex <= 0) return;
        if (direction > 0 && _readerChapterIndex + 1 >= _readerDocument.Chapters.Count) return;

        var now = DateTimeOffset.UtcNow;
        if (_readerContinuousLocked
            && now - _readerLastChapterChange <= TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _readerContinuousLocked = true;
        _readerContinuousDirection = direction;
        _readerLastChapterChange = now;
        _readerScrollPollRunning = true;
        _ = ObserveReaderTaskAsync(MoveReaderChapterFromScrollAsync(direction));
    }

    private async Task MoveReaderChapterFromScrollAsync(int direction)
    {
        try
        {
            await MoveReaderChapterAsync(direction);
        }
        finally
        {
            _readerScrollPollRunning = false;
        }
    }

    // Continuous-mode keyboard scroll: up/down move 72 px smoothly and stop at
    // chapter edges (left/right own chapter navigation), exactly like the
    // WinUI reference's ScrollReaderWithKeyboardAsync.
    private async Task ScrollReaderWithKeyboardAsync(int direction)
    {
        if (IsLinuxReaderTextFallbackActive())
        {
            SetLinuxReaderTextFallbackOffset(
                ReaderLinuxTextFallbackScroll.Offset.Y + Math.Sign(direction) * 72);
            TryAdvanceReaderScrollChapter();
            return;
        }

        if (CurrentReaderHost is IReaderPaginationHost nativeReader)
        {
            // The continuous native surface scrolls itself; keyboard input
            // nudges the same offset the wheel drives.
            nativeReader.ScrollByPixel(Math.Sign(direction) * 72);
            _ = ObserveReaderTaskAsync(UpdateReaderScrollStateAsync(nativeReader));
            return;
        }

        if (CurrentReaderHost is not { } host) return;
        try
        {
            await host.InvokeScriptAsync(
                CreateReaderKeyboardScrollScript(direction, _readerLayout.VerticalWriting));
        }
        catch
        {
            // A stale host must never surface a keyboard scroll failure.
        }
    }

    private static string CreateReaderKeyboardScrollScript(int direction, bool vertical) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const horizontal = {{(vertical ? "true" : "false")}};
          // Vertical writing anchors scroll at the right edge with a negative
          // scrollLeft range; measure and advance by distance from the origin.
          const position = horizontal ? Math.abs(el.scrollLeft || 0) : el.scrollTop;
          const viewport = horizontal ? el.clientWidth : el.clientHeight;
          const extent = horizontal ? el.scrollWidth : el.scrollHeight;
          const sign = {{(direction < 0 ? -1 : 1)}};
          if (sign < 0 && position <= 4) return false;
          if (sign > 0 && position + viewport >= extent - 4) return false;
          const delta = sign * 72;
          if (horizontal
              && typeof window.__libzoraScrollContinuousVerticalBy === 'function')
            return window.__libzoraScrollContinuousVerticalBy(delta);
          window.scrollBy(horizontal
            ? { left: vertical ? -delta : delta, top: 0, behavior: 'smooth' }
            : { left: 0, top: delta, behavior: 'smooth' });
          return true;
        })();
        """;

    private async Task TurnLinuxReaderTextFallbackPageAsync(int direction)
    {
        if (!IsLinuxReaderTextFallbackActive() || _readerDocument is null) return;
        direction = Math.Sign(direction);
        if (direction == 0) return;

        if (_readerLayout.FlowMode == 1)
        {
            if (GetLinuxReaderTextFallbackPageCount() <= 1 && _readerLinuxTextFallbackPages.Count == 0)
                RebuildLinuxReaderTextFallbackPages();
            var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
            var maximum = Math.Max(0, GetLinuxReaderTextFallbackPageCount() - spreadSize);
            var targetPage = _readerLinuxTextFallbackPageIndex + direction * spreadSize;
            if (targetPage >= 0 && targetPage <= maximum)
            {
                await RunReaderContentTransitionAsync(
                    CurrentReaderHost!,
                    CurrentReaderHost!,
                    direction,
                    () =>
                    {
                        _readerLinuxTextFallbackPageIndex = targetPage;
                        RenderLinuxReaderTextFallbackPage();
                        SyncLinuxReaderTextFallbackPagedState(saveProgress: true);
                        return Task.FromResult(0);
                    },
                    ReaderToken);
                return;
            }

            if (await TryNavigateAdjacentReaderTocPageBoundaryAsync(direction))
                return;

            if (direction > 0 && _readerChapterIndex + 1 < _readerDocument.Chapters.Count)
                await MoveReaderChapterAsync(1);
            else if (direction < 0 && _readerChapterIndex > 0)
                await MoveReaderChapterAsync(-1);
            else
                ReaderStatusText.Text = direction > 0 ? T("已经是最后一章。") : T("已经是第一章。");
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var scrollMaximum = Math.Max(0, extent - viewport);
        var current = Math.Clamp(ReaderLinuxTextFallbackScroll.Offset.Y, 0, scrollMaximum);
        var pageDelta = Math.Max(120, viewport - 72);
        const double edgeTolerance = 4;

        if (direction > 0)
        {
            if (current < scrollMaximum - edgeTolerance)
            {
                SetLinuxReaderTextFallbackOffset(Math.Min(scrollMaximum, current + pageDelta));
                return;
            }

            if (_readerChapterIndex + 1 < _readerDocument.Chapters.Count)
                await MoveReaderChapterAsync(1);
            else
                ReaderStatusText.Text = T("已经是最后一章。");
            return;
        }

        if (current > edgeTolerance)
        {
            SetLinuxReaderTextFallbackOffset(Math.Max(0, current - pageDelta));
            return;
        }

        if (_readerChapterIndex > 0)
            await MoveReaderChapterAsync(-1);
        else
            ReaderStatusText.Text = T("已经是第一章。");
    }

    private void PositionLinuxReaderTextFallbackAtChapterEnd(bool requested)
    {
        if (!requested
            || !IsLinuxReaderTextFallbackActive()
            || _readerLayout.FlowMode != 1)
        {
            return;
        }

        if (_readerLinuxTextFallbackPageItems.Count == 0)
            RebuildLinuxReaderTextFallbackPages();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        _readerLinuxTextFallbackPageIndex =
            ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
                currentPageIndex: -1,
                scrollPosition: -1,
                moveToChapterEnd: true,
                pageCount: GetLinuxReaderTextFallbackPageCount(),
                spreadSize);
        SyncLinuxReaderTextFallbackPagedState(saveProgress: false);
    }

    private async Task<bool> TryNavigateAdjacentReaderTocPageBoundaryAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _readerTocItems.Count == 0) return false;

        var currentIndex = GetCurrentReaderTocIndex();
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _readerTocItems.Count)
            return false;

        var target = _readerTocItems[targetIndex];
        if (direction < 0)
        {
            var next = targetIndex + 1 < _readerTocItems.Count
                ? _readerTocItems[targetIndex + 1]
                : null;
            _readerLinuxTextFallbackMoveToChapterEnd = true;
            _readerLinuxTextFallbackEndFragment = next is not null
                && next.ChapterIndex == target.ChapterIndex
                && Uri.TryCreate(next.Target, UriKind.Absolute, out var nextTarget)
                    ? GetReaderTargetFragment(nextTarget)
                    : null;
        }
        else
        {
            _readerLinuxTextFallbackMoveToChapterEnd = false;
            _readerLinuxTextFallbackEndFragment = null;
        }

        var navigated = await NavigateToReaderItemAsync(
            target,
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: direction);
        if (!navigated)
        {
            _readerLinuxTextFallbackMoveToChapterEnd = false;
            _readerLinuxTextFallbackEndFragment = null;
        }
        return navigated;
    }

    private async Task TurnReaderPageAsync(int direction, bool chapterOnly = false)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return;

        var navigation = direction * (chapterOnly ? 2 : 1);
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, navigation);
        while (true)
        {
            // Park until the gate is free instead of dropping the request: a
            // click or wheel notch that arrives while a page-turn animation
            // is still playing must still turn once the animation settles,
            // exactly like the user expects from rapid paging.
            await _readerPageTurnGate.WaitAsync(ReaderToken);
            try
            {
                while ((navigation = Interlocked.Exchange(
                           ref _readerPendingKeyboardNavigation,
                           0)) != 0)
                {
                    await TurnReaderPageCoreAsync(
                        Math.Sign(navigation),
                        chapterOnly: Math.Abs(navigation) == 2);
                }
            }
            finally
            {
                _readerPageTurnGate.Release();
            }

            // A newer input may have parked while this drain ran. Reacquire
            // and honor it; the shared pending slot bounds the backlog to one
            // latest direction.
            if (Volatile.Read(ref _readerPendingKeyboardNavigation) == 0)
                return;
        }
    }

    /// <summary>
    /// Page-compose turn: the compose helper fills the next/previous page in
    /// place (the bank/history containers make this a node shuffle), the hold
    /// overlay covers the compose beat, and the reading position persists as
    /// the page-start character offset.
    /// </summary>
    private async Task TurnReaderPageCoreAsync(int direction, bool chapterOnly)
    {
        if (chapterOnly)
        {
            await MoveReaderChapterAsync(direction, startAtChapterTitle: true);
            return;
        }
        if (_readerIsPdf)
        {
            await MoveReaderPdfPositionAsync(direction);
            return;
        }
        if (CurrentReaderHost is not { } host) return;
        if (host is not IReaderPaginationHost nativeTurn)
        {
            // Only the self-drawn surface paginates EPUB now.
            return;
        }
        if (IsLinuxReaderTextFallbackActive())
        {
            await TurnLinuxReaderTextFallbackPageAsync(direction);
            return;
        }

        if (nativeTurn.CanTurn(direction))
        {
            await TurnReaderPageWithAnimationAsync(host, direction);
            return;
        }

        if (direction > 0 && _readerDocument is not null
            && _readerChapterIndex < _readerDocument.Chapters.Count - 1)
        {
            await MoveReaderChapterAsync(1);
        }
        else if (direction < 0 && _readerChapterIndex > 0)
        {
            await MoveReaderChapterAsync(-1);
        }
    }

    private async Task<string?> TurnReaderPageWithAnimationAsync(
        IReaderHost host,
        int direction)
    {
        return await RunReaderContentTransitionAsync(
            host,
            host,
            direction,
            () => host is IReaderPaginationHost nativeTurn
                ? Task.FromResult<string?>(nativeTurn.TurnPage(direction) ? "true" : "false")
                : Task.FromResult<string?>(null),
            ReaderToken);
    }

    /// <summary>
    /// Runs the one transition pipeline used by both in-chapter page turns and
    /// chapter/TOC navigation. With two hosts, the outgoing document animates
    /// out and the prepared incoming document resumes from the same visual
    /// state after the host swap.
    /// </summary>
    private async Task<T> RunReaderContentTransitionAsync<T>(
        IReaderHost outgoingHost,
        IReaderHost incomingHost,
        int direction,
        Func<Task<T>> changeContentAsync,
        CancellationToken cancellationToken,
        bool animate = true,
        bool holdOutgoingPage = false)
    {
        // A newer TOC request cancels the old load. Let its snapshot cleanup
        // finish before the next transition takes ownership of these overlays.
        await _readerTransitionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _readerCloseInProgress) != 0)
                return await changeContentAsync();

            var animation = animate ? _readerPageAnimation : ReaderAnimationNone;
            var visualDirection = ReaderPaginationPolicy.GetVisualTurnDirection(
                direction,
                IsReaderRightToLeftPaging);
            var surface = IsLinuxReaderTextFallbackActive()
                ? BuildLinuxReaderFallbackTransitionSurface()
                : outgoingHost is IReaderVisualTransitionHost
                    ? BuildReaderNativeTransitionSurface(outgoingHost)
                    : null;
            if (surface is null)
                return await changeContentAsync();

            // Capture before loading, including when both chapters share a
            // host. The same snapshot holds the old page during loading and
            // then plays exactly the selected in-chapter page-turn effect.
            return await ReaderTransitionPlayer.RunAsync(
                surface,
                animation,
                visualDirection,
                changeContentAsync,
                cancellationToken,
                holdOutgoingPage);
        }
        finally
        {
            _readerTransitionGate.Release();
        }
    }

    private void StopReaderTransitionOverlays()
    {
        ReaderNativeTransitionSnapshot.Source = null;
        ReaderNativeTransitionSnapshot.IsVisible = false;
        ReaderNativeTransitionSnapshot.Opacity = 1;
        ReaderNativeTransitionSnapshot.OpacityMask = null;
        ReaderNativeTransitionSnapshot.Clip = null;
        ReaderNativeTransitionSnapshot.RenderTransform = null;
        ReaderNativeTransitionTrail.IsVisible = false;
        ReaderNativeTransitionTrail.Opacity = 1;
        ReaderNativeTransitionTrail.RenderTransform = null;
        ReaderNativeTransitionFront.IsVisible = false;
        ReaderNativeTransitionFront.Opacity = 1;
        ReaderNativeTransitionFront.RenderTransform = null;
        ReaderNativeTransitionEdge.IsVisible = false;
        ReaderNativeTransitionEdge.Opacity = 1;
        ReaderNativeTransitionEdge.RenderTransform = null;

        ReaderLinuxTextFallbackTransitionSnapshot.Source = null;
        ReaderLinuxTextFallbackTransitionSnapshot.IsVisible = false;
        ReaderLinuxTextFallbackTransitionSnapshot.Opacity = 1;
        ReaderLinuxTextFallbackTransitionSnapshot.OpacityMask = null;
        ReaderLinuxTextFallbackTransitionSnapshot.Clip = null;
        ReaderLinuxTextFallbackTransitionSnapshot.RenderTransform = null;
        ReaderLinuxTextFallbackTransitionTrail.IsVisible = false;
        ReaderLinuxTextFallbackTransitionTrail.Opacity = 1;
        ReaderLinuxTextFallbackTransitionTrail.RenderTransform = null;
        ReaderLinuxTextFallbackTransitionFront.IsVisible = false;
        ReaderLinuxTextFallbackTransitionFront.Opacity = 1;
        ReaderLinuxTextFallbackTransitionFront.RenderTransform = null;
        ReaderLinuxTextFallbackTransitionEdge.IsVisible = false;
        ReaderLinuxTextFallbackTransitionEdge.Opacity = 1;
        ReaderLinuxTextFallbackTransitionEdge.RenderTransform = null;

        ReaderTransitionCover.Opacity = 0;
    }

    private ReaderTransitionSurface? BuildReaderNativeTransitionSurface(
        IReaderHost host)
    {
        if (host.View is not Control nativeView) return null;
        var bounds = ReaderNativeTransitionLayer.Bounds;
        if (bounds.Width < 32 || bounds.Height < 32) return null;
        return new ReaderTransitionSurface(
            nativeView,
            ReaderNativeTransitionSnapshot,
            ReaderNativeTransitionTrail,
            ReaderNativeTransitionFront,
            ReaderNativeTransitionEdge,
            ReaderRoot);
    }

    private ReaderTransitionSurface? BuildLinuxReaderFallbackTransitionSurface()
    {
        if (!IsLinuxReaderTextFallbackActive())
            return null;
        var bounds = ReaderLinuxTextFallbackContent.Bounds;
        if (bounds.Width < 32 || bounds.Height < 32)
            return null;
        return new ReaderTransitionSurface(
            ReaderLinuxTextFallbackContent,
            ReaderLinuxTextFallbackTransitionSnapshot,
            ReaderLinuxTextFallbackTransitionTrail,
            ReaderLinuxTextFallbackTransitionFront,
            ReaderLinuxTextFallbackTransitionEdge,
            ReaderRoot);
    }
}
