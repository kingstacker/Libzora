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
/// TOC navigation, reading locations, annotations and in-page search.
/// </summary>
public partial class MainWindow
{
    private async Task<bool> NavigateToReaderItemAsync(
        EpubReaderNavigationItem item,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent = ReaderNavigationIntent.None,
        int? transitionDirection = null)
    {
        if (_readerIsPdf)
        {
            if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var pdfTarget) || !pdfTarget.IsFile) return false;
            var pdfNavigationCancellation = BeginReaderPdfTocNavigation(
                cancellationToken,
                out var requestVersion);
            var pdfNavigationToken = pdfNavigationCancellation.Token;
            try
            {
                var targetPage = item.ChapterIndex + 1;
                if (!await NavigatePdfPageCoreAsync(
                    targetPage,
                    pdfNavigationToken,
                    saveProgress: false,
                    syncTocSelection: false,
                    destination: pdfTarget))
                {
                    return false;
                }
                if (!IsCurrentReaderPdfTocNavigation(requestVersion, pdfNavigationToken))
                    return false;
                if (CurrentReaderHost is not IPdfReadingHost pdf
                    || pdf.PageNumber != targetPage)
                {
                    return false;
                }

                // The host resolves and renders the complete destination while
                // the outgoing snapshot is still covering the page. Commit the
                // TOC only after that transition has reached its final position.
                pdfNavigationToken.ThrowIfCancellationRequested();
                if (!IsCurrentReaderPdfTocNavigation(requestVersion, pdfNavigationToken))
                    return false;

                _readerPdfPage = pdf.PageNumber;
                _readerChapterIndex = _readerPdfPage - 1;
                _readerPinnedTocTarget = item.Target;
                _readerPinnedTocNavigationTop = pdf.NavigationTop;
                SetReaderTocSelection(item);
                ReaderChapterText.Text = GetReaderChapterPositionLabel();
                UpdateReaderToolbar();
                await UpdateReaderBookmarkIndicatorAsync();
                await SaveReaderProgressAsync(pdfNavigationToken);
                return true;
            }
            catch (OperationCanceledException) when (pdfNavigationToken.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                if (ReferenceEquals(_readerPdfNavigationCancellation, pdfNavigationCancellation))
                {
                    _readerPdfTocNavigationVersion = 0;
                    _readerPdfNavigationCancellation = null;
                    pdfNavigationCancellation.Dispose();
                }
            }
        }
        var isReaderTtsNavigation = !_readerTtsAutoNavigation
            && intent is ReaderNavigationIntent.Toc or ReaderNavigationIntent.Progress;
        var shouldResumeReaderTts = isReaderTtsNavigation
            && (_readerTtsResumeAfterNavigation
                || _readerTtsFloatingRequested
                || IsReaderTtsActiveState(_readerTts.State));
        var navigationRequestVersion = BeginReaderTtsNavigationRequest(
            shouldResumeReaderTts);
        if (!isReaderTtsNavigation && !_readerTtsAutoNavigation)
            _readerTtsResumeAfterNavigation = false;
        if (!_readerTtsAutoNavigation)
            await _readerTts.StopAsync();
        if (!IsCurrentReaderTtsNavigationRequest(navigationRequestVersion))
            return false;
        if (_readerDocument is null || CurrentReaderHost is null)
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
            return false;
        }
        if (item.ChapterIndex < 0 || item.ChapterIndex >= _readerDocument.Chapters.Count)
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
            return false;
        }
        if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target) || !target.IsFile)
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
            return false;
        }
        if (!IsPathInside(_readerDocument.RootPath, target.LocalPath))
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
            return false;
        }
        if (CurrentReaderHost is not { } currentHost
            || !ReaderHostHasLoadedDocument(currentHost, target))
        {
            // Commit an open annotation against the outgoing chapter before
            // the active host changes. If the save fails, keep the editor open
            // and cancel navigation so the draft cannot be attached to the
            // next chapter by a later Save click.
            if (ReaderAnnotationInputPopup.IsOpen)
            {
                await SaveReaderAnnotationAsync(ReaderAnnotationInputBox.Text ?? string.Empty);
                if (ReaderAnnotationInputPopup.IsOpen)
                    return false;
            }
            HideReaderAnnotationInputPopup();
            HideReaderAnnotationHoverPopup();
        }
        CancelReaderChapterPreload(target);
        PruneReaderPendingLocations(intent);
        HideReaderSelectionPopup();
        if (!ReaderNavigationLocationPolicy.UsesRestorePosition(intent))
            _readerRestoredProgress = null;
        ResetReaderContinuousEdgeTracking();
        var linuxFallbackStartsAtTarget = UseLinuxPlainTextRecoveryFallback
            && OperatingSystem.IsLinux()
            && !_readerIsPdf
            && intent is ReaderNavigationIntent.Toc or ReaderNavigationIntent.Progress;
        var linuxFallbackMovesToTargetEnd = linuxFallbackStartsAtTarget
            && _readerLinuxTextFallbackMoveToChapterEnd;
        _readerLinuxTextFallbackTargetTitle = linuxFallbackStartsAtTarget ? item.Title : null;
        if (linuxFallbackStartsAtTarget && !_readerLinuxTextFallbackMoveToChapterEnd)
            _readerLinuxTextFallbackEndFragment = null;

        var current = CurrentReaderHost;
        if (ReaderHostHasLoadedDocument(current, target))
        {
            var sameDocumentSessionToken = _readerSession.GetTokenOr(cancellationToken);
            _readerNavigationCancellation?.Cancel();
            var sameDocumentNavigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sameDocumentSessionToken);
            _readerNavigationCancellation = sameDocumentNavigationCancellation;
            var sameDocumentNavigationToken = sameDocumentNavigationCancellation.Token;
            try
            {
                var direction = transitionDirection
                    ?? (item.ChapterIndex < _readerChapterIndex ? -1 : 1);
                await RunReaderContentTransitionAsync(
                    current,
                    current,
                    direction,
                    async () =>
                    {
                        await ApplyReaderLocationAsync(
                            current,
                            target,
                            sameDocumentNavigationToken,
                            intent,
                            _readerRestoredProgress is not null);
                        _readerChapterIndex = item.ChapterIndex;
                        _readerCurrentFragment = GetReaderTargetFragment(target);
                        await UpdateReaderScrollStateAsync(current);
                        if (linuxFallbackStartsAtTarget)
                        {
                            _readerScrollPosition = linuxFallbackMovesToTargetEnd ? -1 : 0;
                            _readerScrollRatio = 0;
                            _readerLinuxTextFallbackPageIndex = linuxFallbackMovesToTargetEnd ? -1 : 0;
                        }
                        await UpdateLinuxReaderTextFallbackAsync(sameDocumentNavigationToken);
                        PositionLinuxReaderTextFallbackAtChapterEnd(
                            linuxFallbackMovesToTargetEnd);
                        return true;
                    },
                    sameDocumentNavigationToken,
                    animate: intent != ReaderNavigationIntent.None);
                PrimeReaderContinuousEdgeTracking();
                SetReaderTocSelection(item);
                ReaderChapterText.Text = GetReaderChapterPositionLabel();
                UpdateReaderToolbar();
                if (CanResumeReaderTtsNavigation(
                        navigationRequestVersion,
                        shouldResumeReaderTts))
                {
                    await ResumeReaderTtsAfterNavigationAsync(true);
                }
                await UpdateReaderBookmarkIndicatorAsync();
                await SaveReaderProgressAsync(sameDocumentSessionToken);
                return true;
            }
            catch (OperationCanceledException) when (sameDocumentNavigationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                ReaderStatusText.Text = T("定位失败：{0}", UiText.Localize(exception.Message));
                return false;
            }
            finally
            {
                CompleteReaderTtsNavigationRequest(navigationRequestVersion);
                if (ReferenceEquals(_readerNavigationCancellation, sameDocumentNavigationCancellation))
                    _readerNavigationCancellation = null;
                sameDocumentNavigationCancellation.Dispose();
            }
        }

        await ResetReaderInPageSearchForNavigationAsync();
        var sessionToken = _readerSession.GetTokenOr(cancellationToken);
        _readerNavigationCancellation?.Cancel();
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _readerNavigationCancellation = navigationCancellation;
        var navigationToken = navigationCancellation.Token;
        var hiddenHost = HiddenReaderHost;
        var host = IsReaderHostReady(hiddenHost) ? hiddenHost! : CurrentReaderHost;
        var previousChapterIndex = _readerChapterIndex;
        var previousFragment = _readerCurrentFragment;
        try
        {
            ReaderStatusText.Text = string.Empty;
            if (linuxFallbackStartsAtTarget)
            {
                _readerChapterIndex = item.ChapterIndex;
                _readerCurrentFragment = GetReaderTargetFragment(target);
                if (linuxFallbackStartsAtTarget)
                {
                    _readerScrollPosition = linuxFallbackMovesToTargetEnd ? -1 : 0;
                    _readerScrollRatio = 0;
                    _readerLinuxTextFallbackPageIndex = linuxFallbackMovesToTargetEnd ? -1 : 0;
                }
            }
            var direction = transitionDirection
                ?? (item.ChapterIndex < previousChapterIndex ? -1 : 1);
            await RunReaderContentTransitionAsync(
                current,
                host,
                direction,
                async () =>
                {
                    var loaded = await NavigateReaderHostAndWaitAsync(host, target, navigationToken);
                    if (!loaded) throw new InvalidOperationException(T("章节加载失败。"));

                    await ApplySavedAnnotationsAsync(host, navigationToken);
                    _readerChapterIndex = item.ChapterIndex;
                    _readerScrollPosition = 0;
                    _readerScrollRatio = 0;
                    // host was picked as the hidden host, so the layer must flip
                    // unconditionally; deriving it from CurrentReaderHost would read
                    // the stale pre-swap flag and freeze the visible chapter after the
                    // first jump (TOC / next-chapter worked only once).
                    _readerShowingPreload = ReferenceEquals(host, _readerPreloadHost);
                    SetReaderHostLayer();
                    await ApplyReaderLocationAsync(
                        host,
                        target,
                        navigationToken,
                        intent,
                        _readerRestoredProgress is not null);
                    _readerCurrentFragment = GetReaderTargetFragment(target);
                    await UpdateReaderScrollStateAsync(host);
                    if (linuxFallbackStartsAtTarget)
                    {
                        _readerScrollPosition = linuxFallbackMovesToTargetEnd ? -1 : 0;
                        _readerScrollRatio = 0;
                        _readerLinuxTextFallbackPageIndex = linuxFallbackMovesToTargetEnd ? -1 : 0;
                    }
                    await UpdateLinuxReaderTextFallbackAsync(navigationToken);
                    PositionLinuxReaderTextFallbackAtChapterEnd(
                        linuxFallbackMovesToTargetEnd);
                    return true;
                },
                navigationToken,
                animate: intent != ReaderNavigationIntent.None,
                holdOutgoingPage: true);
            FocusCurrentReaderHost();
            PrimeReaderContinuousEdgeTracking();
            SetReaderTocSelection(item);
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            UpdateReaderToolbar();
            ReaderStatusText.Text = string.Empty;
            if (CanResumeReaderTtsNavigation(
                    navigationRequestVersion,
                    shouldResumeReaderTts))
            {
                await ResumeReaderTtsAfterNavigationAsync(true);
            }
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
            return true;
        }
        catch (OperationCanceledException) when (navigationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
            {
                _readerChapterIndex = previousChapterIndex;
                _readerCurrentFragment = previousFragment;
            }
            return false;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
            {
                _readerChapterIndex = previousChapterIndex;
                _readerCurrentFragment = previousFragment;
            }
            ReaderStatusText.Text = T("打开章节失败：{0}", UiText.Localize(exception.Message));
            return false;
        }
        finally
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    private async Task ResetReaderInPageSearchForNavigationAsync()
    {
        if (_readerIsPdf
            || (!ReaderInPageSearchBar.IsVisible && _readerSearchCount <= 0
                && string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text)))
            return;

        await ClearReaderSearchAsync();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderInPageSearchBox.Text = string.Empty;
    }

    // Intent-aware positioning (Libzora.Core.ReaderNavigationLocationPolicy):
    // TOC entries without an explicit anchor and progress-slider jumps start
    // at the chapter's first line (normalizing the chapter start); anchors are
    // scrolled for TOC/bookmark/annotation targets; search and AI-source
    // targets keep the DOM untouched because their own offset-based scrolling
    // (and annotation offset math) depends on it; plain switches normalize
    // unless a breakpoint restore is pending.
    private void PruneReaderPendingLocations(ReaderNavigationIntent intent)
    {
        if (!ReaderNavigationLocationPolicy.KeepsChunkOffset(intent))
            _readerPendingChunkOffset = null;
        if (intent != ReaderNavigationIntent.Search)
        {
            _readerPendingSearchQuery = null;
            _readerPendingSearchContext = null;
        }
        if (!ReaderNavigationLocationPolicy.KeepsBookmarkQuote(intent))
        {
            ClearReaderBookmarkPendingLocation();
        }
        if (intent != ReaderNavigationIntent.Annotation)
            _readerPendingAnnotation = null;
    }
}
