using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// First Avalonia Kreader slice: EPUB preparation, a two-host native webview
/// surface, chapter switching and a local-file navigation guard. The richer
/// TOC, pagination and annotation tools stay behind this boundary for the
/// following reader slices.
/// </summary>
public partial class MainWindow
{
    private EpubReaderDocument? _readerDocument;
    private BookCardViewModel? _readerBookCard;
    private BookFile? _readerBookFile;
    private int _readerChapterIndex;
    private bool _readerShowingPreload;
    private static readonly TimeSpan ReaderCloseCheckpointTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReaderCloseHostStopTimeout = TimeSpan.FromSeconds(2);
    private readonly ReaderSession _readerSession = new();
    private CancellationTokenSource? _readerNavigationCancellation;
    private CancellationTokenSource? _readerPdfNavigationCancellation;
    private int _readerPdfNavigationVersion;
    private int _readerPdfTocNavigationVersion;
    private CancellationTokenSource? _readerChapterPreloadCancellation;
    private Uri? _readerChapterPreloadTarget;
    private readonly Dictionary<IReaderHost, Uri> _readerLoadedHostSources = new();
    private int _readerCloseInProgress;
    private readonly SemaphoreSlim _readerActiveHostNavigationGate = new(1, 1);
    private readonly SemaphoreSlim _readerPreloadHostNavigationGate = new(1, 1);

    private IReaderHost? CurrentReaderHost =>
        _readerShowingPreload && _readerPreloadHost is not null
            ? _readerPreloadHost
            : _readerActiveHost;

    private IReaderHost? HiddenReaderHost =>
        _readerShowingPreload ? _readerActiveHost : _readerPreloadHost;

    private static bool IsReaderHostReady(IReaderHost? host)
        => host?.ReadyTask.IsCompletedSuccessfully == true;

    private async Task OpenEpubReaderAsync(
        BookCardViewModel card,
        BookFile file,
        string epubPath,
        bool restoreProgress = true)
    {
        // A delayed scroll checkpoint belongs to the previous reader session.
        // Invalidate it before replacing the cancellation source so it cannot
        // write the old book/page into a newly opened session.
        Interlocked.Increment(ref _readerProgressSaveSequence);
        var sessionToken = _readerSession.Begin(_lifetimeCancellation.Token);

        try
        {
            SetTaskStatus(T("正在准备《{0}》的阅读缓存…", card.Title));
            var contentHash = file.Sha256;
            // Converted AZW3/MOBI resources must follow the generated EPUB's
            // content, so rebuilt conversion caches invalidate extracted pages.
            if (contentHash.Length != 64 || !file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
                contentHash = await Hashing.Sha256Async(epubPath, sessionToken);

            var document = await _epubReader.PrepareAsync(
                epubPath,
                contentHash,
                sessionToken);
            sessionToken.ThrowIfCancellationRequested();

            _readerDocument = document;
            _readerBookCard = card;
            _readerBookFile = file;
            _readerChapterIndex = 0;
            _readerShowingPreload = false;
            await InitializeReaderInteractionAsync(document, sessionToken);

            var savedProgress = restoreProgress
                ? await _readerData.GetProgressAsync(file.Id, sessionToken)
                : null;
            sessionToken.ThrowIfCancellationRequested();
            _readerRestoredProgress = ValidateReaderProgress(document, savedProgress);
            if (restoreProgress && _readerRestoredProgress is { } progress)
            {
                _readerChapterIndex = progress.ChapterIndex;
                _readerScrollPosition = progress.ScrollPosition;
                _readerCurrentFragment = DecodeReaderFragment(progress.Fragment);
            }

            // The interaction shell is initialized before the persisted
            // position is read, so its first TOC state is based on chapter 0.
            // Synchronize the folded/selected rows before revealing the reader
            // instead of waiting for the WebView navigation to finish. This
            // prevents a visible intermediate TOC that corrects itself later.
            CollapseReaderTocToCurrentChapter(_readerChapterIndex);
            SetReaderTocSelectionForLocation(
                _readerChapterIndex,
                _readerCurrentFragment);

            ReaderBookInfoText.Text = card.Title;
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            ReaderStatusText.Text = string.Empty;
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            ApplyReaderPanelLayout();

            await EnsureReaderHostsAsync();
            // On Windows, keep the first document behind the opaque reader
            // surface until its Kreader CSS and bundled font have been applied:
            // WebView2 paints the navigated XHTML before the first script
            // injection. Linux native webviews can skip completion events for
            // hidden controls, so keep the visible host on-screen there.
            SetReaderHostLayer(revealActiveHost: !OperatingSystem.IsWindows());

            var target = new Uri(document.Chapters[_readerChapterIndex]);
            // A fragment identifies a section, not the precise page inside
            // that section. When a pixel breakpoint exists, navigating the
            // URL with its old chapter anchor can asynchronously pull the
            // WebView back to the chapter start after the breakpoint restore.
            if (_readerRestoredProgress?.ContentPosition is null
                && (_readerRestoredProgress?.ScrollPosition ?? 0) <= 0
                && !string.IsNullOrWhiteSpace(_readerCurrentFragment))
            {
                target = new Uri(
                    target.AbsoluteUri
                    + "#"
                    + Uri.EscapeDataString(_readerCurrentFragment));
            }
            var loaded = await NavigateReaderHostAndWaitAsync(
                CurrentReaderHost!,
                target,
                sessionToken);
            if (!loaded)
                throw new InvalidOperationException(T("阅读器无法加载 EPUB 章节。"));

            SetReaderHostLayer();
            FocusCurrentReaderHost();
            SetReaderTocSelectionForLocation(_readerChapterIndex, _readerCurrentFragment);
            await UpdateReaderScrollStateAsync(CurrentReaderHost!);
            ScheduleReaderBookPageCountRefresh();
            UpdateReaderToolbar();
            PrimeReaderContinuousEdgeTracking();

            await UpdateReaderBookmarkIndicatorAsync();
            // Do not replace a breakpoint with the chapter origin when its
            // first restore attempt reported that the DOM was not ready.
            if (_readerRestoredProgress is null)
                await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (sessionToken.IsCancellationRequested) return;
            SoftwareLog.ErrorWithoutMessage("reader.open", exception, ("format", "epub"));
            await CloseReaderAsync();
            SetTaskStatus(T("打开 EPUB 阅读器失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
        }
    }
}
