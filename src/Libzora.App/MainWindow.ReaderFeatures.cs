using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Text.RegularExpressions;

namespace Libzora;

/// <summary>
/// Reader shortcuts, PDF opening and navigation state.
/// </summary>
public partial class MainWindow
{
    private const int MaxWholeBookSearchResults = 500;
    private const int MaxReaderAnnotations = 5_000;
    private const int MaxReaderBookmarks = 5_000;
    private int _readerWholeSearchSequence;
    private CancellationTokenSource? _readerWholeSearchCancellation;
    private readonly SemaphoreSlim _readerWholeSearchGate = new(1, 1);
    private int _readerPdfSearchSequence;
    private bool _readerSearchVisible;
    private bool _readerSearchLayoutCaptured;
    private bool _readerSearchPreviousTocExpanded;
    private bool _readerSearchPreviousTocMinimal;
    private bool _readerSearchPreviousBookmarkTabActive;
    private string _readerSearchQuery = string.Empty;
    private ReaderSearchResultViewModel? _selectedReaderSearchResult;

    private sealed record ReaderBookmarkLocation(
        string ChapterPath,
        int ScrollPosition,
        int FlowMode,
        string? Fragment)
    {
        public ReaderContentPosition? ContentPosition { get; init; }
    }

    private CancellationToken ReaderToken =>
        _readerSession.GetTokenOr(_lifetimeCancellation.Token);

    private bool IsNativeReaderPaginated =>
        !_readerIsPdf && CurrentReaderHost is IReaderPositionHost { IsPaginated: true };

    private bool IsReaderPaginated =>
        _readerIsPdf ? (CurrentReaderHost as IPdfReadingHost)?.DisplayMode != PdfReaderDisplayMode.Continuous
            : _readerLayout.FlowMode == 1 || IsNativeReaderPaginated;

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReaderRoot.IsVisible) return;
        // F11 toggles zen mode (enter/exit), matching the common fullscreen
        // convention used by browsers and readers; Esc is an additional way to
        // leave zen mode (both from the WinUI reference's global hook).
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleReaderZenMode();
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleReaderEscapeShortcut();
            return;
        }
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.F)
        {
            e.Handled = true;
            OpenReaderSearchShortcut();
            return;
        }

        if (IsLinuxReaderTextFallbackActive()
            && TryHandleLinuxReaderTextFallbackKeyDown(e))
        {
            return;
        }

        if (_readerIsPdf && CurrentReaderHost is IReaderKeyboardHost pdf && !IsReaderTextInputFocused())
        {
            pdf.HandleReaderKey(e);
            if (e.Handled) { FocusCurrentReaderHost(); return; }
        }

        if (IsReaderPaginated
            && CurrentReaderHost is IReaderLocationHost nativeReader
            && !IsReaderTextInputFocused()
            && e.Key is Key.Home or Key.End)
        {
            e.Handled = true;
            nativeReader.SeekToBoundary(e.Key == Key.End);
            _ = ObserveReaderTaskAsync(UpdateReaderScrollStateAsync(nativeReader));
            return;
        }

        // Native WebViews are swapped at chapter boundaries. If Windows sends
        // the next arrow to the Avalonia window before the newly visible HWND
        // has accepted focus, keep paginated navigation responsive here. Keys
        // delivered to the native WebView never enter Avalonia's routed input,
        // so any arrow reaching this handler still needs to be handled.
        if (IsReaderPaginated
            && !IsReaderTextInputFocused()
            && CurrentReaderHost?.View is Control)
        {
            var chapterDirection = !_readerIsPdf
                ? e.Key == Key.Up
                    ? -1
                    : e.Key == Key.Down
                        ? 1
                        : 0
                : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(
                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return;
            }
            var verticalPageOrder = !_readerIsPdf && _readerLayout.VerticalWriting;
            var direction = e.Key == Key.Left
                ? (verticalPageOrder ? 1 : -1)
                : e.Key == Key.Right
                    ? (verticalPageOrder ? -1 : 1)
                    : e.Key is Key.Up or Key.PageUp
                        ? -1
                        : e.Key is Key.Down or Key.PageDown
                            ? 1
                            : 0;
            if (direction != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                return;
            }
        }

        if (!_readerIsPdf
            && _readerLayout.FlowMode == 0
            && !IsNativeReaderPaginated
            && !IsReaderTextInputFocused()
            && CurrentReaderHost?.View is Control)
        {
            var chapterDirection = e.Key == Key.Left
                ? -1
                : e.Key == Key.Right
                    ? 1
                    : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(
                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return;
            }

            var scrollDirection = e.Key == Key.Up
                ? -1
                : e.Key == Key.Down
                    ? 1
                    : 0;
            if (scrollDirection != 0)
            {
                e.Handled = true;
                FocusCurrentReaderHost();
                _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                return;
            }
        }

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.B
            && !IsReaderTextInputFocused())
        {
            e.Handled = true;
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
        }
    }

    private bool IsReaderTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox;
    }

    private bool HandleReaderEscapeShortcut()
    {
        if (ReaderImageZoomOverlay.IsVisible)
        {
            CloseReaderImageZoom();
            return true;
        }
        if (ReaderInPageSearchBar.IsVisible)
        {
            _ = ObserveReaderTaskAsync(ReaderInPageSearchCloseAsync());
            return true;
        }
        // Esc closes reader overlays in priority order, matching the WinUI
        // reference's RootGrid_KeyDown: whole-book search panel first, then
        // the layout settings overlay, then zen mode.
        if (_readerSearchVisible)
        {
            HideReaderSearchPanel();
            return true;
        }
        if (ReaderLayoutSettingsPopup.IsOpen)
        {
            ReaderLayoutSettingsPopup.IsOpen = false;
            return true;
        }
        if (_readerZenMode)
        {
            ToggleReaderZenMode();
            return true;
        }
        return false;
    }

    private void OpenReaderSearchShortcut()
    {
        ReaderInPageSearchBar.IsVisible = true;
        ReaderInPageSearchBox.Focus();
        ReaderInPageSearchBox.SelectAll();
    }

    private async Task OpenPdfReaderAsync(
        BookCardViewModel card,
        BookFile file,
        string path)
    {
        // A reader can also be opened from a restored/deep-linked card that
        // was never attached to the library visual tree. The explicit open
        // action is allowed to populate the detail cover on that path; normal
        // library cards remain viewport-lazy.
        card.LoadCover();

        // Invalidate any debounced progress save from the previous reader
        // session before installing the new session cancellation source.
        Interlocked.Increment(ref _readerProgressSaveSequence);
        var token = _readerSession.Begin(_lifetimeCancellation.Token);

        try
        {
            SetTaskStatus(T("正在准备《{0}》的 PDF 阅读器…", card.Title));
            _readerBookCard = card;
            _readerBookFile = file;
            _readerDocument = null;
            _readerIsPdf = true;
            _readerPdfSourcePath = path;
            _readerPdfPages = [];
            _readerPdfEmbeddedOutline = [];
            _readerPendingPdfPoint = null;
            _readerPdfPage = 1;
            _readerChapterIndex = 0;
            _readerScrollRatio = 0;
            _readerScrollPosition = 0;
            // A PDF session always renders in the active host slot; clear any
            // preload flag left over from a previous EPUB session so the layer
            // swap below shows the right slot.
            _readerShowingPreload = false;

            await InitializeReaderInteractionAsync(
                new EpubReaderDocument(Path.GetDirectoryName(path) ?? string.Empty, [], [], []),
                token);
            // Initialization resets all per-book state. Install the PDF index
            // afterwards, and keep the global EPUB layout preferences intact.
            _readerIsPdf = true;
            _readerPdfSourcePath = path;
            await EnsureReaderHostsAsync();
            var host = CurrentReaderHost as IPdfReadingHost
                ?? throw new InvalidOperationException(T("PDF 阅读器不支持所需的文档操作。"));
            var hostView = host.View as Control
                ?? throw new InvalidOperationException(T("PDF 阅读器未提供有效的显示控件。"));
            // Do not expose the page origin while the saved zoom and pan are
            // still being restored after the first raster has loaded.
            hostView.Opacity = 0;
            var pageCount = await host.PrepareDocumentAsync(path, token);
            var pages = Enumerable.Range(1, pageCount).Select(page => new PdfPageText(page, string.Empty)).ToArray();
            _readerPdfPages = pages;
            _readerPdfIndexReady = false;
            _readerPdfIndexError = null;
            UpdateReaderBookmarkCornerSurface();

            var progress = await _readerData.GetProgressAsync(file.Id, token);
            token.ThrowIfCancellationRequested();
            if (progress is not null)
                _readerPdfPage = Math.Clamp(progress.ChapterIndex + 1, 1, pageCount);
            _readerChapterIndex = _readerPdfPage - 1;
            _readerTocItems = [];
            BuildReaderTocRows();
            RefreshReaderTocRows();
            if (GetReaderTocScrollViewer() is { } tocScroll)
                tocScroll.Offset = default;
            SetReaderCompactNavigationItems(_readerTocItems);

            ReaderBookInfoText.Text = card.Title;
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            ResetReaderStatusText();
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            ReaderTocEmptyText.Text = T("正在读取目录…");
            ReaderTocEmptyText.IsVisible = true;
            ApplyReaderPanelLayout();
            ShowReaderTocTab();
            ReaderAiView.IsVisible = true;
            ReaderNotesView.IsVisible = false;
            ReaderAiComposer.IsVisible = true;
            ReaderNotesExportBar.IsVisible = false;
            ReaderAssistantPanel.IsVisible = false;
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);

            HideLinuxReaderTextFallback();
            SetReaderHostLayer();
            FocusCurrentReaderHost();
            host.RestoreViewSettings(progress?.Fragment);
            await Dispatcher.UIThread.InvokeAsync(() => ReaderRoot.UpdateLayout(), DispatcherPriority.Loaded);
            var pdfSource = new Uri(path).AbsoluteUri + $"#page={_readerPdfPage}";
            // Start the outline work before navigation: the host parses it while
            // opening the document, so the rail fills as soon as that snapshot
            // exists instead of waiting for the first page to render.
            StartReaderPdfOutlineWork(host, file.Id, token);
            if (!await NavigateReaderHostAndWaitAsync(host, new Uri(pdfSource), token))
            {
                throw new InvalidOperationException(T("PDF 阅读器页面加载失败。"));
            }
            await ApplySavedReaderPdfAnnotationsAsync(token);
            StartReaderPdfTextIndexWork(host, file.Id, token);
            if (progress is not null)
                await host.RestoreViewStateAsync(progress.Fragment, progress.ScrollPosition);
            token.ThrowIfCancellationRequested();
            hostView.Opacity = 1;
            RememberCurrentPdfPageText();
            SyncReaderPdfTocSelection();
            UpdateReaderToolbar();
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested) return;
            SoftwareLog.ErrorWithoutMessage("reader.open", exception, ("format", "pdf"));
            await CloseReaderAsync();
            SetTaskStatus(T("打开 PDF 阅读器失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
        }
    }

    private Task NavigatePdfPageAsync(
        int page,
        CancellationToken cancellationToken,
        bool saveProgress = true) =>
        IgnorePdfPageNavigationResultAsync(page, cancellationToken, saveProgress);

    private async Task IgnorePdfPageNavigationResultAsync(
        int page,
        CancellationToken cancellationToken,
        bool saveProgress)
    {
        await NavigatePdfPageCoreAsync(
            page,
            cancellationToken,
            saveProgress,
            syncTocSelection: true);
    }

    private async Task<bool> NavigatePdfPageCoreAsync(
        int page,
        CancellationToken cancellationToken,
        bool saveProgress,
        bool syncTocSelection,
        Uri? destination = null,
        bool preserveVerticalPosition = false)
    {
        if (syncTocSelection)
            CancelReaderPdfNavigation();
        if (!_readerTtsAutoNavigation)
            await _readerTts.StopAsync();
        if (!_readerIsPdf || _readerPdfPages.Count == 0 || CurrentReaderHost is not IPdfReadingHost host) return false;
        if (string.IsNullOrWhiteSpace(_readerPdfSourcePath)) return false;
        _selectedReaderAnnotation = null;
        _readerPendingPdfPoint = null;
        HideReaderAnnotationInputPopup();
        HideReaderSelectionPopup();
        HideReaderAnnotationHoverPopup();
        var targetPage = Math.Clamp(page, 1, _readerPdfPages.Count);
        var source = destination ?? new Uri(new Uri(_readerPdfSourcePath).AbsoluteUri + $"#page={targetPage}");
        // An outline can target the other page in the currently visible spread.
        // Resolve its full destination without playing a spread-turn animation.
        var sameSpread = host.DisplayMode == PdfReaderDisplayMode.TwoPage
            && (targetPage - 1) / 2 == (host.PageNumber - 1) / 2;
        var loaded = host.DisplayMode == PdfReaderDisplayMode.Continuous
            ? await host.NavigateAsync(source, cancellationToken)
            : await RunReaderContentTransitionAsync(host, host, Math.Sign(targetPage - host.PageNumber),
                () => host.NavigateAsync(source, cancellationToken, preserveVerticalPosition: preserveVerticalPosition), cancellationToken,
                animate: targetPage != host.PageNumber && !sameSpread, holdOutgoingPage: true);
        if (!loaded)
        {
            if (!string.IsNullOrWhiteSpace(host.LastError)) ReaderStatusText.Text = host.LastError;
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _readerPdfPage = host.PageNumber;
        _readerChapterIndex = _readerPdfPage - 1;
        if (syncTocSelection)
            SyncReaderPdfTocSelection();
        if (!string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text))
            await ApplyReaderPdfSearchAsync(ReaderInPageSearchBox.Text.Trim(), ++_readerPdfSearchSequence);
        ReaderChapterText.Text = GetReaderChapterPositionLabel();
        UpdateReaderToolbar();
        await UpdateReaderBookmarkIndicatorAsync();
        if (saveProgress) await SaveReaderProgressAsync(cancellationToken);
        return true;
    }

    private void CancelReaderPdfNavigation()
    {
        ++_readerPdfNavigationVersion;
        _readerPdfTocNavigationVersion = 0;
        var cancellation = _readerPdfNavigationCancellation;
        _readerPdfNavigationCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private CancellationTokenSource BeginReaderPdfTocNavigation(
        CancellationToken cancellationToken,
        out int requestVersion)
    {
        CancelReaderPdfNavigation();
        requestVersion = ++_readerPdfNavigationVersion;
        var sessionToken = _readerSession.GetTokenOr(CancellationToken.None);
        var navigation = CancellationTokenSource.CreateLinkedTokenSource(
            sessionToken,
            cancellationToken);
        _readerPdfNavigationCancellation = navigation;
        _readerPdfTocNavigationVersion = requestVersion;
        return navigation;
    }

    private bool IsCurrentReaderPdfTocNavigation(
        int requestVersion,
        CancellationToken cancellationToken) =>
        requestVersion == _readerPdfNavigationVersion
        && requestVersion == _readerPdfTocNavigationVersion
        && !cancellationToken.IsCancellationRequested;

    private Task ApplySavedReaderPdfAnnotationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_readerBookFile is not null && CurrentReaderHost is IPdfAnnotationsHost host)
        {
            host.SetAnnotations(ReaderAnnotations.ToArray());
            HideReaderAnnotationHoverPopup();
        }
        return Task.CompletedTask;
    }

    private void SyncReaderPdfTocSelection()
    {
        if (!_readerIsPdf
            || _readerTocItems.Count == 0
            || _readerPdfTocNavigationVersion != 0)
        {
            return;
        }
        var pdf = CurrentReaderHost as IPdfReadingHost;
        var top = pdf?.NavigationTop ?? 0;
        // Several outline entries can point at the same page and even the same
        // offset (e.g. "1 Features" and "3 Description" on a two-column
        // datasheet). Once the reader has followed an explicit click, keep that
        // entry selected at the position reached by the jump. Fit-to-page may
        // clamp that position before the requested destination reaches the top.
        if (_readerPinnedTocTarget is { } pinnedTarget
            && _readerTocItems.FirstOrDefault(item =>
                string.Equals(item.Target, pinnedTarget, StringComparison.OrdinalIgnoreCase)) is { } pinned
            && pinned.ChapterIndex == _readerChapterIndex
            && _readerPinnedTocNavigationTop is { } landedTop && Math.Abs(top - landedTop) <= 0.02)
        {
            if (!ReferenceEquals((ReaderTocList.SelectedItem as ReaderTocRow)?.Item, pinned))
                SetReaderTocSelection(pinned);
            return;
        }
        _readerPinnedTocTarget = null;
        _readerPinnedTocNavigationTop = null;
        // A whole page can fit without its first heading reaching the viewport
        // top. In that case the first heading on the active page represents the
        // spread; the right-hand page must not override the left-hand page.
        var firstOnPage = pdf is { DisplayMode: not PdfReaderDisplayMode.Continuous, NavigationBottom: >= 0.98 }
            && top <= 0.02
            ? _readerTocItems.Where(item => item.ChapterIndex == _readerChapterIndex)
                .OrderBy(item => PdfNavigationTarget.ReadTop(new Uri(item.Target)))
                .ThenBy(item => item.Level).FirstOrDefault()
            : null;
        var current = firstOnPage ?? _readerTocItems.Where(item => item.ChapterIndex < _readerChapterIndex
            || item.ChapterIndex == _readerChapterIndex && PdfNavigationTarget.ReadTop(new Uri(item.Target)) <= top + 0.02)
            .OrderByDescending(item => item.ChapterIndex)
            .ThenByDescending(item => PdfNavigationTarget.ReadTop(new Uri(item.Target)))
            .ThenByDescending(item => item.Level).FirstOrDefault()
            ?? _readerTocItems.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex)
            ?? _readerTocItems[0];
        if (!ReferenceEquals((ReaderTocList.SelectedItem as ReaderTocRow)?.Item, current)) SetReaderTocSelection(current);
    }
}
