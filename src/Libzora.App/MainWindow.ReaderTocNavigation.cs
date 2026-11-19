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

    private async Task ApplyReaderLocationAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent,
        bool hasPendingRestorePosition)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not IReaderLocationHost nativeReader)
        {
            return;
        }

        var nativeFragment = DecodeReaderFragment(target.Fragment);
        if (intent is ReaderNavigationIntent.Search or ReaderNavigationIntent.AiSource)
        {
            await ScrollToPendingReaderChunkAsync(host, cancellationToken);
        }
        else if (intent == ReaderNavigationIntent.Annotation && _readerPendingAnnotation is { } pending)
        {
            var startOffset = nativeReader.TryGetAnnotationRange(
                pending,
                out var relocatedStart,
                out _)
                ? relocatedStart
                : Math.Max(0, pending.StartOffset);
            nativeReader.ScrollToOffset(startOffset);
            _readerPendingAnnotation = null;
        }
        else if (intent == ReaderNavigationIntent.Bookmark)
        {
            if (nativeReader.RestoreContentPosition(_readerPendingBookmarkContentPosition))
            {
                // The semantic destination survives changes to the global
                // reading mode, font size and viewport since the bookmark.
            }
            else if (!string.IsNullOrWhiteSpace(_readerPendingBookmarkQuote)
                && nativeReader.BodyText is { } body
                && body.IndexOf(_readerPendingBookmarkQuote, StringComparison.Ordinal) is var quoteOffset
                && quoteOffset >= 0
                && body.IndexOf(_readerPendingBookmarkQuote, quoteOffset + 1, StringComparison.Ordinal) < 0)
            {
                // Older bookmarks sometimes have a unique visible quote even
                // though they do not have a portable content anchor yet.
                nativeReader.ScrollToOffset(quoteOffset);
            }
            else if (_readerPendingBookmarkPosition is { } bookmarkPosition)
            {
                if (nativeReader.Vertical)
                {
                    nativeReader.ScrollToOffset((int)Math.Max(0, bookmarkPosition));
                }
                else
                {
                    nativeReader.SeekToPixelScroll(bookmarkPosition);
                }
            }
            else if (!string.IsNullOrWhiteSpace(nativeFragment))
            {
                nativeReader.ScrollToFragment(nativeFragment);
            }
            ClearReaderBookmarkPendingLocation();
        }
        else if (ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
                     intent,
                     target,
                     hasPendingRestorePosition))
        {
            // A plain TOC/progress target can point at the same XHTML that is
            // already loaded. Reset the native page explicitly; otherwise the
            // selection changes while the old page remains visible.
            nativeReader.SeekToBoundary(toEnd: false);
        }
        else if (intent == ReaderNavigationIntent.Footnote && !string.IsNullOrWhiteSpace(nativeFragment))
        {
            await nativeReader.RevealFootnoteAsync(nativeFragment);
        }
        else if (!string.IsNullOrWhiteSpace(nativeFragment))
        {
            nativeReader.ScrollToFragment(nativeFragment);
        }

        await UpdateReaderScrollStateAsync(host);
        UpdateReaderToolbar();
    }

    private static string? GetReaderTargetFragment(Uri target)
    {
        var fragment = target.Fragment.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        return DecodeReaderFragment(fragment);
    }

    private static string? DecodeReaderFragment(string? value)
        => ReaderResourcePolicy.DecodeFragment(value);

    private void SetReaderTocSelection(EpubReaderNavigationItem? item)
    {
        var index = item is null ? -1 : FindReaderTocIndex(item);
        // A selected entry nested under a collapsed parent would be invisible
        // and unreachable, so open its branch before selecting it.
        if (index >= 0 && ExpandReaderTocAncestors(index))
        {
            RefreshReaderTocRows();
        }

        var row = index >= 0 && index < _readerTocRows.Length ? _readerTocRows[index] : null;
        SetReaderTocCurrentRow(row);
        var selectedItems = ReaderTocList.SelectedItems;
        var hasOnlyExpectedSelection = row is null
            ? selectedItems is null || selectedItems.Count == 0
            : selectedItems is { Count: 1 }
                && ReferenceEquals(selectedItems[0], row);
        if (!hasOnlyExpectedSelection)
        {
            _suppressReaderTocSelectionNavigation = true;
            try
            {
                // SelectionMode only constrains pointer input in Avalonia;
                // programmatic SelectedItem assignments can otherwise retain
                // old rows. Keep exactly one visual current-row state.
                if (selectedItems is not null)
                {
                    selectedItems.Clear();
                    if (row is not null)
                        selectedItems.Add(row);
                }
            }
            finally
            {
                _suppressReaderTocSelectionNavigation = false;
            }
        }

        // Scrolling a row that is already on screen is what made the rail
        // creep: the virtualizing panel re-anchors from estimated heights and
        // settles a little lower each time. Only chase a row that is off-screen.
        if (row is not null && ShouldScrollReaderTocRowIntoView(row))
            ReaderTocList.ScrollIntoView(row);
        SetReaderCompactSelectedItem(item);
        if (_readerIsPdf && ReaderPreviousButton is not null && ReaderNextButton is not null)
        {
            ReaderPreviousButton.IsEnabled = index > 0;
            ReaderNextButton.IsEnabled = index >= 0 && index + 1 < _readerTocItems.Count;
        }
    }

    private void SetReaderTocSelectionForChapter(int chapterIndex)
    {
        SetReaderTocSelectionForLocation(chapterIndex, null);
    }

    private void SetReaderTocSelectionForLocation(int chapterIndex, string? fragment)
    {
        var chapterItems = _readerTocItems
            .Where(item => item.ChapterIndex == chapterIndex)
            .ToArray();
        var decodedFragment = DecodeReaderFragment(fragment);
        var selected = string.IsNullOrWhiteSpace(decodedFragment)
            ? null
            : chapterItems.FirstOrDefault(item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    decodedFragment,
                    StringComparison.Ordinal));
        selected ??= chapterItems.FirstOrDefault();
        selected ??= _readerTocItems
            .Where(item => item.ChapterIndex <= chapterIndex)
            .OrderByDescending(item => item.ChapterIndex)
            .FirstOrDefault();
        SetReaderTocSelection(selected);
    }

    private void SyncReaderTocSelectionWithReadingPosition()
    {
        if (_readerIsPdf) { SyncReaderPdfTocSelection(); return; }
        if (_readerDocument is { } document && CurrentReaderHost?.Source is { } source
            && _readerChapterIndex >= 0 && _readerChapterIndex < document.Chapters.Count
            && !ReaderNavigationLocationPolicy.TargetsSameDocument(source, new Uri(document.Chapters[_readerChapterIndex])))
            return;

        var index = GetCurrentReaderTocIndex();
        var item = index >= 0 ? _readerTocItems[index] : null;
        // Preserve the user's TOC scroll and the realized rows while reading
        // within one section. Only a new section needs selection/scrolling.
        if (ReferenceEquals((ReaderTocList.SelectedItem as ReaderTocRow)?.Item, item)) return;
        SetReaderTocSelection(item);
        ReaderChapterText.Text = GetReaderChapterPositionLabel();
    }

    private EpubReaderNavigationItem? FindAdjacentReaderSubchapter(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return null;

        var chapterItems = _readerTocItems
            .Where(item => item.ChapterIndex == _readerChapterIndex)
            .ToArray();
        if (chapterItems.Length < 2) return null;

        var currentFragment = DecodeReaderFragment(_readerCurrentFragment);
        var currentIndex = -1;
        if (!string.IsNullOrWhiteSpace(currentFragment))
        {
            currentIndex = Array.FindIndex(chapterItems, item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    currentFragment,
                    StringComparison.Ordinal));
        }

        if (currentIndex < 0)
        {
            currentIndex = Array.FindIndex(chapterItems, item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.IsNullOrWhiteSpace(GetReaderTargetFragment(target)));
        }

        // When every entry is anchored, the first one normally represents the
        // containing chapter heading and therefore acts as the current base.
        if (currentIndex < 0) currentIndex = 0;
        var targetIndex = currentIndex + direction;
        return targetIndex >= 0 && targetIndex < chapterItems.Length
            ? chapterItems[targetIndex]
            : null;
    }

    private int GetReaderFooterTocIndex()
    {
        if ((ReaderTocList.SelectedItem as ReaderTocRow)?.Item is { } selected
            && (_readerIsPdf || selected.ChapterIndex == _readerChapterIndex)
            && FindReaderTocIndex(selected) is var index && index >= 0)
            return index;
        return _readerIsPdf ? -1 : GetCurrentReaderTocIndex();
    }

    private int GetCurrentReaderTocIndex()
    {
        if (_readerTocItems.Count == 0) return -1;

        var currentFragment = DecodeReaderFragment(_readerCurrentFragment);
        if (!string.IsNullOrWhiteSpace(currentFragment))
        {
            for (var index = 0; index < _readerTocItems.Count; index++)
            {
                var item = _readerTocItems[index];
                if (item.ChapterIndex == _readerChapterIndex
                    && Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                    && string.Equals(
                        GetReaderTargetFragment(target),
                        currentFragment,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        var sameChapterIndex = -1;
        var precedingIndex = -1;
        for (var index = 0; index < _readerTocItems.Count; index++)
        {
            var item = _readerTocItems[index];
            if (item.ChapterIndex == _readerChapterIndex && sameChapterIndex < 0)
                sameChapterIndex = index;
            if (item.ChapterIndex <= _readerChapterIndex)
                precedingIndex = index;
        }
        return sameChapterIndex >= 0 ? sameChapterIndex : precedingIndex;
    }

    private async Task ScrollToPendingReaderAnnotationAsync(
        IReaderHost host,
        ReaderAnnotation annotation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is IReaderLocationHost nativeReader)
        {
            var startOffset = nativeReader.TryGetAnnotationRange(
                annotation,
                out var relocatedStart,
                out _)
                ? relocatedStart
                : Math.Max(0, annotation.StartOffset);
            nativeReader.ScrollToOffset(startOffset);
            await UpdateReaderScrollStateAsync(host);
        }
    }

    private async Task ApplySavedAnnotationsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        if (host is IPdfAnnotationsHost) { await ApplySavedReaderPdfAnnotationsAsync(cancellationToken); return; }
        if (_readerBookFile is null || _readerDocument is null) return;
        var chapterPath = GetReaderChapterPath(host);
        if (chapterPath is null) return;

        if (host is IReaderAnnotationHost nativeReader)
        {
            // Resolve saved quote/context at paint and navigation time. The
            // native loader normally reproduces the same stream, but edited or
            // reparsed EPUB content can move a previously valid offset.
            var nativeAnnotations = await _readerData.GetAnnotationsAsync(
                _readerBookFile.Id,
                cancellationToken,
                MaxReaderAnnotations);
            nativeReader.SetAnnotations(nativeAnnotations
                .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
                .ToList());
            return;
        }

        var annotations = await _readerData.GetAnnotationsAsync(
            _readerBookFile.Id,
            cancellationToken,
            MaxReaderAnnotations);
        var marks = annotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
            .Select(item => new
            {
                Id = item.Id.ToString("N"),
                StartOffset = item.StartOffset,
                EndOffset = item.EndOffset,
                Quote = item.SelectedText.Trim(),
                Prefix = item.Prefix,
                Suffix = item.Suffix,
                Note = item.Note,
                Color = NormalizeReaderAnnotationColor(item.Color),
                Style = NormalizeReaderAnnotationStyle(item.UnderlineStyle)
            })
            .OrderByDescending(item => item.StartOffset)
            .ToArray();

        var serialized = JsonSerializer.Serialize(marks);
        var script = $$"""
            (() => {
              const annotations = {{serialized}};
              const commonSuffixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[left.length - 1 - length] === right[right.length - 1 - length]) length++;
                return length;
              };
              const commonPrefixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[length] === right[length]) length++;
                return length;
              };
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize?.();
              };
              for (const oldMark of Array.from(document.querySelectorAll('.libzora-saved-annotation'))) {
                unwrap(oldMark);
              }
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#libzora-selection-bar, .libzora-wave-sweep');
              };
              const collectNodes = () => {
                const nodes = [];
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                  if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
                }
                return nodes;
              };
              const segmentsFor = (nodes, start, end) => {
                const segments = [];
                let cursor = 0;
                for (const node of nodes) {
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              const styleMark = (mark, annotation) => {
                mark.className = 'libzora-saved-annotation';
                if (annotation.Id) mark.setAttribute('data-libzora-annotation', annotation.Id);
                const color = /^#[0-9a-f]{6}$/i.test(annotation.Color || '') ? annotation.Color : '#000000';
                const style = ['solid', 'double', 'dashed', 'dotted', 'wavy', 'marker'].includes(annotation.Style)
                  ? annotation.Style
                  : 'solid';
                const marker = style === 'marker';
                const invertedMarker = marker && color === '#000000';
                mark.style.setProperty('background-color', marker ? (invertedMarker ? color : color + '50') : 'transparent', 'important');
                if (marker) {
                  mark.style.setProperty('color', invertedMarker ? '#FFFFFF' : 'inherit', 'important');
                  mark.style.setProperty('text-decoration-line', 'none', 'important');
                } else {
                  mark.style.setProperty('text-decoration-line', 'underline', 'important');
                  mark.style.setProperty('text-decoration-color', color, 'important');
                  mark.style.setProperty('text-decoration-style', style, 'important');
                  mark.style.setProperty('text-decoration-thickness', '2px', 'important');
                  mark.style.setProperty('text-underline-offset', '3px', 'important');
                  mark.style.setProperty('text-decoration-skip-ink', 'none', 'important');
                }
                mark.style.setProperty('display', 'inline', 'important');
                mark.style.cursor = 'pointer';
                if (annotation.Note) mark.title = annotation.Note;
              };
              const wrapSegments = (segments, annotation) => {
                for (let index = segments.length - 1; index >= 0; index--) {
                  const segment = segments[index];
                  if (!segment.node.parentNode) return false;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('span');
                  styleMark(mark, annotation);
                  try {
                    range.surroundContents(mark);
                  } catch (_) {
                    return false;
                  }
                }
                return true;
              };
              for (const annotation of annotations) {
                const quote = annotation.Quote || '';
                if (!quote) continue;
                // Search the logical text stream, not individual text nodes.
                // EPUB markup commonly splits one visible sentence across
                // spans, emphasis tags, and inline links.
                const nodes = collectNodes();
                const text = nodes.map(node => node.data || '').join('');
                const foldedText = text.toLocaleLowerCase();
                const foldedQuote = quote.toLocaleLowerCase();
                const prefix = (annotation.Prefix || '').slice(-72).toLocaleLowerCase();
                const suffix = (annotation.Suffix || '').slice(0, 72).toLocaleLowerCase();
                let bestAt = -1;
                let bestScore = -1;
                const context = prefix + foldedQuote + suffix;
                const contextAt = context.length > foldedQuote.length
                  ? foldedText.indexOf(context)
                  : -1;
                if (contextAt >= 0) bestAt = contextAt + prefix.length;
                let at = bestAt >= 0 ? -1 : foldedText.indexOf(foldedQuote);
                while (at >= 0) {
                  const before = text.slice(Math.max(0, at - 72), at).toLocaleLowerCase();
                  const after = text.slice(at + quote.length, at + quote.length + 72).toLocaleLowerCase();
                  const score = commonSuffixLength(before, prefix) + commonPrefixLength(after, suffix);
                  if (score > bestScore) {
                    bestScore = score;
                    bestAt = at;
                  }
                  at = foldedText.indexOf(foldedQuote, at + Math.max(1, foldedQuote.length));
                }
                if (bestAt < 0) {
                  const storedStart = Math.max(0, Number(annotation.StartOffset) || 0);
                  const storedEnd = Math.max(storedStart, Number(annotation.EndOffset) || storedStart);
                  if (storedEnd > storedStart && storedEnd <= text.length) {
                    bestAt = storedStart;
                  }
                }
                if (bestAt < 0) continue;
                const length = quote.length > 0
                  ? quote.length
                  : Math.max(0, (Number(annotation.EndOffset) || 0) - (Number(annotation.StartOffset) || 0));
                wrapSegments(segmentsFor(nodes, bestAt, bestAt + length), annotation);
              }
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
    }

    private async Task ApplyReaderSearchAsync(string query, int sequence, bool navigate = true)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        if (sequence != _readerSearchSequence) return;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
            if (sequence != _readerSearchSequence) return;
            if (CurrentReaderHost is IReaderTextSearchHost nativeReader)
            {
                // Case-folded search over the same body-text stream the
                // WebKit walker saw; highlights paint per page.
                var nativeQuery = query.Trim();
                var nativeHits = nativeReader
                    .FindSearchMatches(nativeQuery)
                    .Select(match => (match.Start, match.Length))
                    .ToList();

                nativeReader.SetSearchHighlights(nativeHits, null);
                _readerSearchCount = nativeHits.Count;
                _readerSearchIndex = _readerSearchCount > 0
                    ? navigate
                        ? 0
                        : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
                    : -1;
                if (navigate && _readerSearchCount > 0)
                    await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
                else
                    UpdateReaderSearchCount();
                return;
            }
        var serializedQuery = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const oldMarks = Array.from(document.querySelectorAll('mark.libzora-page-find-hit'));
              const normalizedParents = new Set();
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                normalizedParents.add(parent);
              };
              for (let index = oldMarks.length - 1; index >= 0; index--) {
                unwrap(oldMarks[index]);
              }
              for (const parent of normalizedParents) parent.normalize?.();
              const query = ({{serializedQuery}} || '').trim();
              if (!query || !document.body) return 0;
              const folded = query.toLocaleLowerCase();
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#libzora-selection-bar, .libzora-wave-sweep, mark.libzora-page-find-hit');
              };
              const nodes = [];
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
              }
              const nodeOffsets = [];
              let totalLength = 0;
              for (const node of nodes) {
                nodeOffsets.push(totalLength);
                totalLength += (node.data || '').length;
              }
              const text = nodes.map(node => node.data || '').join('');
              const foldedText = text.toLocaleLowerCase();
              const matches = [];
              const markedMatches = new Set();
              let start = foldedText.indexOf(folded);
              while (start >= 0) {
                matches.push({ start, length: query.length });
                start = foldedText.indexOf(folded, start + Math.max(1, folded.length));
              }
              const segmentsFor = (start, end) => {
                const segments = [];
                let low = 0, high = nodes.length;
                while (low < high) {
                  const mid = (low + high) >>> 1;
                  if ((nodeOffsets[mid + 1] ?? totalLength) <= start) low = mid + 1;
                  else high = mid;
                }
                let cursor = nodeOffsets[low] || 0;
                for (let nodeIndex = low; nodeIndex < nodes.length; nodeIndex++) {
                  const node = nodes[nodeIndex];
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              for (let index = matches.length - 1; index >= 0; index--) {
                const match = matches[index];
                const segments = segmentsFor(match.start, match.start + match.length);
                let didMark = false;
                for (let segmentIndex = segments.length - 1; segmentIndex >= 0; segmentIndex--) {
                  const segment = segments[segmentIndex];
                  if (!segment.node.parentNode) continue;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('mark');
                  mark.className = 'libzora-page-find-hit';
                  mark.setAttribute('data-libzora-page-hit', String(index));
                  mark.style.setProperty('background', '#000000', 'important');
                  mark.style.setProperty('color', '#FFFFFF', 'important');
                  mark.style.setProperty('text-decoration', 'none', 'important');
                  try {
                    range.surroundContents(mark);
                    didMark = true;
                  } catch (_) {
                    // A malformed EPUB node should not abort the remaining hits.
                  }
                }
                if (didMark) markedMatches.add(index);
              }
              return markedMatches.size;
            })();
            """;
        string? result;
        try
        {
            result = await host.InvokeScriptAsync(script);
        }
        catch
        {
            if (sequence != _readerSearchSequence) return;
            _readerSearchCount = 0;
            _readerSearchIndex = -1;
            UpdateReaderSearchCount();
            return;
        }
        if (sequence != _readerSearchSequence) return;
        _readerSearchCount = ParseScriptInt(result);
        _readerSearchIndex = _readerSearchCount > 0
            ? navigate
                ? 0
                : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
            : -1;
        if (navigate)
            await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
        else
            UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }

    private async Task NavigateReaderSearchAsync(int index, int? sequence = null)
    {
        if (_readerSearchCount <= 0 || CurrentReaderHost is not { } host)
        {
            UpdateReaderSearchCount();
            return;
        }
        if (sequence is not null && sequence.Value != _readerSearchSequence) return;
        _readerSearchIndex = (index % _readerSearchCount + _readerSearchCount) % _readerSearchCount;
        if (host is IPdfSearchHost pdf)
        {
            pdf.ScrollToSearchHit(_readerSearchIndex);
            UpdateReaderSearchCount();
            return;
        }
        if (host is IReaderTextSearchHost nativeReader)
        {
            nativeReader.ScrollToSearchHit(_readerSearchIndex);
            UpdateReaderSearchCount();
        }
    }

    private async Task ClearReaderSearchAsync()
    {
        _readerSearchSequence++;
        _readerPdfSearchSequence++;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
        if (CurrentReaderHost is { } host)
        {
            try
            {
                if (host is IPdfTextHost pdf) pdf.ClearSearch();
                await host.InvokeScriptAsync("""
                    (() => {
                      const unwrap = mark => {
                        const parent = mark.parentNode;
                        if (!parent) return;
                        while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                        parent.removeChild(mark);
                        parent.normalize?.();
                      };
                      for (const mark of Array.from(document.querySelectorAll('mark.libzora-page-find-hit, mark.libzora-search-hit'))) {
                        unwrap(mark);
                      }
                    })();
                    """);
            }
            catch
            {
                // PDF's built-in viewer and a just-swapped WebView do not
                // expose a scriptable DOM; clearing search is still complete.
            }
        }
        UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }
}
