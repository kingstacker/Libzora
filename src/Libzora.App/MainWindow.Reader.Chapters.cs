using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace Libzora;

/// <summary>
/// Chapter movement, labels, layers and host events.
/// </summary>
public partial class MainWindow
{
    private async Task MoveReaderChapterAsync(
        int offset,
        bool startAtChapterTitle = false,
        bool positionAtStart = false)
    {
        if (!_readerTtsAutoNavigation)
            await _readerTts.StopAsync();
        var chapterTiming = Stopwatch.StartNew();
        await ResetReaderInPageSearchForNavigationAsync();
        ClearReaderBookmarkPendingLocation();
        _readerPendingAnnotation = null;
        if (_readerIsPdf)
        {
            await MoveReaderPdfPositionAsync(offset);
            return;
        }
        if (_readerDocument is null || CurrentReaderHost is null) return;
        if (startAtChapterTitle
            && FindAdjacentReaderSubchapter(offset) is { } subchapter)
        {
            await NavigateToReaderItemAsync(
                subchapter,
                ReaderToken,
                ReaderNavigationIntent.Toc,
                transitionDirection: Math.Sign(offset));
            return;
        }

        var moveLinuxFallbackToEnd = UseLinuxPlainTextRecoveryFallback
            && OperatingSystem.IsLinux()
            && !_readerIsPdf
            && offset < 0
            && !positionAtStart;
        _readerLinuxTextFallbackTargetTitle = null;
        if (UseLinuxPlainTextRecoveryFallback
            && OperatingSystem.IsLinux()
            && !_readerIsPdf)
        {
            _readerScrollPosition = offset < 0 ? -1 : 0;
            _readerLinuxTextFallbackMoveToChapterEnd = moveLinuxFallbackToEnd;
            _readerLinuxTextFallbackEndFragment = null;
        }
        _readerCurrentFragment = null;
        var targetIndex = _readerChapterIndex + offset;
        if (targetIndex < 0 || targetIndex >= _readerDocument.Chapters.Count)
        {
            _readerLinuxTextFallbackMoveToChapterEnd = false;
            ReaderStatusText.Text = offset < 0 ? T("已经是第一章。") : T("已经是最后一章。");
            return;
        }
        ResetReaderContinuousEdgeTracking();

        var sessionToken = _readerSession.GetTokenOr(_lifetimeCancellation.Token);
        _readerNavigationCancellation?.Cancel();
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _readerNavigationCancellation = navigationCancellation;
        var token = navigationCancellation.Token;
        var target = new Uri(_readerDocument.Chapters[targetIndex]);
        var hiddenHost = HiddenReaderHost;
        var host = IsReaderHostReady(hiddenHost) ? hiddenHost! : CurrentReaderHost;
        try
        {
            ReaderStatusText.Text = string.Empty;
            var outgoingHost = CurrentReaderHost;
            await RunReaderContentTransitionAsync(
                outgoingHost,
                host,
                offset,
                async () =>
                {
                    var loaded = await NavigateReaderHostAndWaitAsync(host, target, token);
                    LogReaderChapterTiming("move.navigated", chapterTiming);
                    if (!loaded) throw new InvalidOperationException(T("章节加载失败。"));

                    await ApplySavedAnnotationsAsync(host, token);
                    await PositionReaderChapterBoundaryAsync(
                        host,
                        moveToEnd: offset < 0 && !positionAtStart,
                        token);
                    LogReaderChapterTiming("move.boundary", chapterTiming);
                    _readerChapterIndex = targetIndex;
                    // host was picked as the hidden host, so the layer must flip
                    // unconditionally; deriving it from CurrentReaderHost would read
                    // the stale pre-swap flag and freeze the visible chapter after the
                    // first jump (TOC / next-chapter worked only once).
                    _readerShowingPreload = ReferenceEquals(host, _readerPreloadHost);
                    SetReaderHostLayer();
                    await UpdateReaderScrollStateAsync(host);
                    await UpdateLinuxReaderTextFallbackAsync(token);
                    PositionLinuxReaderTextFallbackAtChapterEnd(
                        moveLinuxFallbackToEnd);
                    return true;
                },
                token,
                holdOutgoingPage: true);
            LogReaderChapterTiming("move.transition", chapterTiming);
            FocusCurrentReaderHost();
            PrimeReaderContinuousEdgeTracking();
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            UpdateReaderToolbar();
            ReaderStatusText.Text = string.Empty;
            SetReaderTocSelectionForLocation(targetIndex, _readerCurrentFragment);
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("章节加载失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    private async Task PositionReaderChapterBoundaryAsync(
        IReaderHost host,
        bool moveToEnd,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is IReaderLocationHost nativeReader)
        {
            nativeReader.SeekToBoundary(moveToEnd);
            await UpdateReaderScrollStateAsync(host);
        }
    }

    private string GetReaderChapterLabel() => _readerIsPdf
        ? T("{0} / {1} · 第 {0} 页", _readerPdfPage, Math.Max(1, _readerPdfPages.Count))
        : _readerDocument is null
            ? string.Empty
            : GetCurrentReaderTocIndex() is var tocIndex && tocIndex >= 0
                ? $"{tocIndex + 1} / {_readerTocItems.Count} · {_readerTocItems[tocIndex].Title}"
                : $"{_readerChapterIndex + 1} / {_readerDocument.Chapters.Count} · {GetReaderChapterDisplayName(_readerChapterIndex)}";

    private string GetReaderChapterPositionLabel() => _readerIsPdf
        ? GetPdfPagePositionLabel()
        : _readerDocument is null
            ? string.Empty
            : GetCurrentReaderTocIndex() is var tocIndex && tocIndex >= 0
                ? $"{tocIndex + 1} / {_readerTocItems.Count}"
                : $"{_readerChapterIndex + 1} / {_readerDocument.Chapters.Count}";

    private async Task MoveReaderFooterTocAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return;
        var currentIndex = GetReaderFooterTocIndex();
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _readerTocItems.Count)
        {
            ReaderStatusText.Text = direction < 0 ? T("已经是目录第一项。") : T("已经是目录最后一项。");
            return;
        }

        await NavigateToReaderItemAsync(
            _readerTocItems[targetIndex],
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: direction);
    }

    private string GetReaderChapterDisplayName(int chapterIndex)
    {
        var currentFragment = chapterIndex == _readerChapterIndex
            ? DecodeReaderFragment(_readerCurrentFragment)
            : null;
        var item = string.IsNullOrWhiteSpace(currentFragment)
            ? null
            : _readerTocItems.FirstOrDefault(candidate =>
                candidate.ChapterIndex == chapterIndex
                && Uri.TryCreate(candidate.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    currentFragment,
                    StringComparison.Ordinal));
        item ??= _readerTocItems.FirstOrDefault(candidate => candidate.ChapterIndex == chapterIndex)
            ?? _readerTocItems
                .Where(candidate => candidate.ChapterIndex <= chapterIndex)
                .OrderByDescending(candidate => candidate.ChapterIndex)
                .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(item?.Title)) return item.Title.Trim();
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.ChapterTitles.Count
            && !string.IsNullOrWhiteSpace(_readerDocument.ChapterTitles[chapterIndex]))
        {
            return _readerDocument.ChapterTitles[chapterIndex].Trim();
        }
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.Chapters.Count)
        {
            var fileName = Path.GetFileNameWithoutExtension(_readerDocument.Chapters[chapterIndex]);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return T("第 {0} 章", chapterIndex + 1);
    }

    private void SetReaderHostLayer(bool revealActiveHost = true)
    {
        // On Linux the plain-text reading surface replaces the native webview
        // instead of layering above it: an embedded native webview paints over
        // Avalonia visuals whatever their ZIndex. Revealing the host while that
        // overlay is the live surface covers the chapter with a blank page, so
        // the guard lives here rather than at each call site — the first open
        // used to reveal the host after the overlay had already been built.
        if (revealActiveHost && IsLinuxReaderTextFallbackActive())
            revealActiveHost = false;

        var activeSlot = _readerShowingPreload ? ReaderPreloadHostSlot : ReaderActiveHostSlot;
        var hiddenSlot = _readerShowingPreload ? ReaderActiveHostSlot : ReaderPreloadHostSlot;
        // Native webviews are HWND-backed. Opacity and ZIndex alone only affect
        // the Avalonia wrapper; the hidden child window can still cover the
        // visible chapter and consume input. Toggle actual visibility as well.
        activeSlot.IsVisible = revealActiveHost;
        hiddenSlot.IsVisible = false;
        activeSlot.Opacity = revealActiveHost ? 1 : 0;
        activeSlot.IsHitTestVisible = revealActiveHost;
        hiddenSlot.Opacity = 0;
        hiddenSlot.IsHitTestVisible = false;
        activeSlot.ZIndex = 1;
        hiddenSlot.ZIndex = 0;
    }

    private void FocusCurrentReaderHost()
    {
        if (IsLinuxReaderTextFallbackActive())
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReaderRoot.IsVisible && IsLinuxReaderTextFallbackActive())
                        ReaderLinuxTextFallbackOverlay.Focus();
                },
                DispatcherPriority.Input);
            return;
        }

        if (CurrentReaderHost?.View is not Control readerControl) return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReaderRoot.IsVisible && ReferenceEquals(CurrentReaderHost?.View, readerControl))
                    readerControl.Focus();
            },
            DispatcherPriority.Input);
    }
}
