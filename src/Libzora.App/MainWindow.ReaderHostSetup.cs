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
/// Reader initialisation: host/surface configuration, bundled fonts and
/// navigation item construction.
/// </summary>
public partial class MainWindow
{
    private void ClearReaderAiCollections()
    {
        StopReaderAiThinkingAnimation();
        _readerAiBusy = false;
        SetReaderAiBusyState(false);
        ReaderAiNewContentButton.IsVisible = false;
        _readerAiConversation.Clear();
        _readerAiFollowOutput = true;
        ClearReaderAiDraftImages();
        foreach (var message in ReaderAiMessages)
            message.Dispose();
        ReaderAiMessages.Clear();
        ReaderAiSources.Clear();
    }

    private async Task InitializeReaderInteractionAsync(
        EpubReaderDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await LoadReaderBookProfileAsync(cancellationToken);
        InitializeReaderMangaSettings(document);
        var globalLayout = NormalizeReaderLayoutForPlatform(
            _appSettings.DefaultReaderLayout);
        _readerBaseLayout = ReaderLayoutDefaults.ApplyPreferences(
            _readerBookLayoutProfile?.Layout ?? globalLayout,
            globalLayout,
            ReaderUsesBookOverride);
        _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
        ApplyReaderAppearance();
        SyncReaderAppearanceControls();
        SyncReaderBookScopeControls();
        _readerTocItems = BuildReaderNavigationItems(document);
        _readerRestoredProgress = null;
        _readerActiveSyncedMarker = null;
        _readerSyncedMarkerScrollOrigin = 0;
        _readerSyncedMarkerScrollArmed = false;
        _readerBookmarkIndicatorSequence++;
        ClearReaderBookmarkPendingLocation();
        _readerCurrentFragment = null;
        _readerPendingAnnotation = null;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerScrollPosition = 0;
        _readerScrollRatio = 0;
        _readerScrollWidth = 0;
        _readerScrollHeight = 0;
        _readerClientWidth = 0;
        _readerClientHeight = 0;
        _readerBookPageCountCancellation?.Cancel();
        _readerBookPageCountCancellation?.Dispose();
        _readerBookPageCountCancellation = null;
        _readerBookPageCounts = [];
        _readerBookPageCountSequence++;
        CancelReaderPdfNavigation();
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        _readerPendingChunkOffset = null;
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
        _readerWholeSearchCancellation?.Cancel();
        _readerSearchVisible = false;
        _readerSearchLayoutCaptured = false;
        _readerSearchQuery = string.Empty;
        ClearReaderSearchResultSelection();
        _readerContinuousSkipDepth = 0;
        _readerLinuxTextFallbackMoveToChapterEnd = false;
        _readerContinuousLocked = false;
        _readerContinuousDirection = 0;
        ResetReaderContinuousEdgeTracking();
        _readerScrollPollRunning = false;
        _readerWheelDeltaRemainder = 0;
        _readerLinuxTextFallbackWheelDeltaRemainder = 0;
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, 0);
        _readerPdfSearchSequence++;
        _readerSessionStarted = DateTimeOffset.UtcNow;
        _readerZenMode = false;
        _readerAssistantVisibleBeforeZen = true;
        _readerIsPdf = false;
        UpdateReaderBookmarkCornerSurface();
        _readerPdfPages = [];
        _readerPdfEmbeddedOutline = [];
        _readerPendingPdfPoint = null;
        _readerPdfPage = 1;
        _readerTocExpanded = true;
        _readerTocMinimal = false;
        _readerTocExpandedBeforeZen = true;
        _readerTocMinimalBeforeZen = false;
        _readerActiveSeconds = 0;
        _readerBookReadingStats = null;
        if (_readerBookFile is { } statsFile)
            _ = LoadReaderBookReadingStatsAsync(statsFile.Id, cancellationToken);
        ReaderBookInfoText.Text = _readerBookCard?.Title ?? UiText.Get("目录");
        BuildReaderTocRows();
        CollapseReaderTocToCurrentChapter(_readerChapterIndex);
        // A new book gets a new row array; remove every old programmatic
        // selection instead of clearing only SelectedItem.
        ReaderTocList.SelectedItems?.Clear();
        ReaderTocPanel.IsVisible = false;
        ReaderTocView.IsVisible = true;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = false;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
        ReaderSearchResults.Clear();
        ReaderWholeSearchCountText.Text = string.Empty;
        ReaderSearchStatusText.IsVisible = true;
        ReaderSearchResultList.IsVisible = false;
        ReaderInPageSearchBar.IsVisible = false;
        ReaderLayoutSettingsPopup.IsOpen = false;
        HideReaderFootnotePopup();
        HideReaderAnnotationHoverPopup();
        ReaderBookmarkCornerMarker.IsVisible = false;
        ClearReaderAiCollections();
        ReaderAiEmptyState.IsVisible = true;
        ReaderAiView.IsVisible = true;
        ReaderNotesView.IsVisible = false;
        ReaderPdfThumbnailsView.IsVisible = false;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: true);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: false);
        SetReaderAssistantTabState(ReaderPdfThumbnailsTabButton, selected: false);
        ReaderAiSettingsView.IsVisible = false;
        ReaderAiComposer.IsVisible = true;
        ReaderNotesExportBar.IsVisible = false;
        ReaderAssistantPanel.IsVisible = false;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        SetReaderCompactNavigationItems(_readerTocItems);
        SetReaderCompactSelectedItem(
            _readerTocItems.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex));
        ApplyReaderPanelLayout();
        UpdateReaderZoomLabel();
        await RefreshReaderBookmarksAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshReaderAnnotationsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeReaderAiAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeReaderTtsAsync(cancellationToken);
        // Optional AI/TTS initialization handles its own cancellation. The
        // reader session must still stop here before publishing ready state.
        cancellationToken.ThrowIfCancellationRequested();
        UpdateReaderToolbar();
        StartReaderStatsTimer();
        StartReaderFootnoteHoverPoll();
    }

    private async Task LoadReaderBookReadingStatsAsync(Guid bookFileId, CancellationToken cancellationToken)
    {
        try
        {
            var stats = await _readerData.GetReadingStatsAsync(bookFileId, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _readerBookFile?.Id != bookFileId)
                return;
            _readerBookReadingStats = stats;
            if (_appSettings.ReaderProgressDisplay == ReaderProgressDisplayMode.BookTimeRemaining)
                ReaderChapterText.Text = GetReaderChapterPositionLabel();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static IReadOnlyList<EpubReaderNavigationItem> BuildReaderNavigationItems(
        EpubReaderDocument document)
        => EpubReaderNavigationPolicy.Build(
            document,
            UiText.Get("封面"),
            UiText.Get("目录"),
            chapterIndex => GetPreparedChapterTitle(document, chapterIndex));

    private static string GetPreparedChapterTitle(EpubReaderDocument document, int chapterIndex)
    {
        if (chapterIndex >= 0
            && chapterIndex < document.ChapterTitles.Count
            && !string.IsNullOrWhiteSpace(document.ChapterTitles[chapterIndex]))
        {
            return document.ChapterTitles[chapterIndex].Trim();
        }
        return chapterIndex == 0 ? UiText.Get("封面") : UiText.Get("第 {0} 章", chapterIndex + 1);
    }

    private async Task ConfigureReaderHostAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        var revealTiming = System.Diagnostics.Stopwatch.StartNew();
        // The injected bridge hides the document on every navigation so the
        // fallback-font/vertical-cell transition is never visible. That makes
        // configuration the only thing that can put a chapter back on screen:
        // if it is cancelled or throws part way through — a superseded
        // navigation, a rapid chapter turn, a failed InvokeScript — the reader
        // would stay permanently blank. Reveal on every exit path.
        try
        {
            await ConfigureReaderHostCoreAsync(host, cancellationToken);
        }
        finally
        {
            if (!_readerIsPdf)
            {
                try
                {
                    await host.InvokeScriptAsync(RevealReaderDocumentScript);
                    LogReaderChapterTiming("cfg.revealed", revealTiming);
                }
                catch
                {
                    // The document can already be gone when the navigation was
                    // superseded. The next configuration reveals the new one.
                }
            }
        }
    }

    private async Task ConfigureReaderHostCoreAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        var configTiming = System.Diagnostics.Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not IReaderConfiguredHost nativeReader)
        {
            // Only PDF still renders inside the platform webview viewer;
            // there is no reader document to configure there.
            return;
        }

        var progress = ReferenceEquals(host, CurrentReaderHost)
            && _readerRestoredProgress?.ChapterIndex == _readerChapterIndex
                ? _readerRestoredProgress : null;
        double? legacyChapterRatio = progress is not null && _readerDocument is { Chapters.Count: > 0 } document
            && double.IsFinite(progress.ProgressPercent)
                ? Math.Clamp(progress.ProgressPercent / 100d * document.Chapters.Count - progress.ChapterIndex, 0, 1)
                : null;
        if (progress is not null && progress.FlowMode == _readerLayout.FlowMode
            && (!_readerLayout.VerticalWriting
                || legacyChapterRatio is { } ratio && nativeReader.BodyText is { Length: > 0 } text
                    && Math.Abs(ratio * text.Length - progress.ScrollPosition) <= 1))
        {
            // Keep usable legacy positions in the same presentation. In
            // vertical mode the saved ratio also identifies an existing
            // character offset; horizontal pixel records do not match it.
            legacyChapterRatio = null;
        }
        // The tracked fragment describes the current section, not a request
        // to jump to its heading whenever the reader's layout is configured.
        // A pending restore must use its saved values even if the initial
        // native scroll event has already reported the document origin.
        if (host is NativeReaderHost mangaHost && host.Source is { } hostSource)
        {
            mangaHost.SetSpreadCompanionChapterPath(ResolveSpreadCompanionChapterPath(hostSource));
        }

        await nativeReader.Configure(
            _readerLayout,
            progress?.ScrollPosition ?? _readerScrollPosition,
            progress?.Fragment,
            progress is not null,
            showVerticalDebugBoxes: ShouldShowReaderVerticalDebugBoxes(),
            contentPosition: progress?.ContentPosition,
            legacyChapterRatio: legacyChapterRatio);
        if (progress is not null)
        {
            _readerRestoredProgress = null;
        }
        if (ReferenceEquals(host, CurrentReaderHost))
        {
            (host as IReaderResumeMarkerHost)?.SetResumeMarker(
                _readerActiveSyncedMarker?.ChapterIndex == _readerChapterIndex
                    ? ToResumeMarker(_readerActiveSyncedMarker)
                    : null);
        }

        await ApplySavedAnnotationsAsync(host, cancellationToken);
        LogReaderChapterTiming("cfg.configured", configTiming);
    }
    private const string FitReaderCoverImageScript = """
        (() => {
          const root = document.documentElement;
          const body = document.body;
          if (!root || !body) return false;
          const fit = () => {
            const viewWidth = root.clientWidth || window.innerWidth || 0;
            const viewHeight = root.clientHeight || window.innerHeight || 0;
            if (viewWidth <= 0 || viewHeight <= 0) return false;

            const bodyStyle = getComputedStyle(body);
            const paddingTop = parseFloat(bodyStyle.paddingTop) || 0;
            const paddingBottom = parseFloat(bodyStyle.paddingBottom) || 0;
            const viewportHeight = root.clientHeight || window.innerHeight || body.clientHeight || 0;
            const contentHeight = viewportHeight - paddingTop - paddingBottom;
            if (contentHeight > 0)
              root.style.setProperty('--libzora-page-content-h', contentHeight + 'px');

            const candidates = Array.from(
              document.querySelectorAll('body img, body svg, body svg image'));
            const shortImagePage = (body.innerText || body.textContent || '').trim().length <= 120
              && candidates.length > 0
              && candidates.length <= 4;
            // Cover pages converted from TXT often keep a bare caption such
            // as "封面" above the raster. The reading surface must show the
            // cover alone, so drop block-level captions whose entire text is
            // that label; sentences elsewhere are never touched because the
            // rule only runs on short image-only pages.
            if (shortImagePage) {
              document.querySelectorAll(
                'body p, body h1, body h2, body h3, body h4, body h5, body h6, body div, body span')
                .forEach(element => {
                  if (element.querySelector('img, svg, image')) return;
                  const label = (element.textContent || '').replace(/\s+/g, '');
                  if (label === '封面')
                    element.style.setProperty('display', 'none', 'important');
                });
            }
            for (let index = 0; index < candidates.length; index++) {
              const element = candidates[index];
              const tag = element.tagName.toLowerCase();
              const isSvgImage = tag === 'image';
              const rect = element.getBoundingClientRect?.() || { width: 0, height: 0 };
              const naturalWidth = element.naturalWidth
                || parseFloat(element.getAttribute('width')) || rect.width || 0;
              const naturalHeight = element.naturalHeight
                || parseFloat(element.getAttribute('height')) || rect.height || 0;
              if (naturalWidth <= 0 || naturalHeight <= 0) {
                if (tag === 'img' && !element.dataset.libzoraCoverLoadWatch) {
                  element.dataset.libzoraCoverLoadWatch = '1';
                  element.addEventListener('load', () => requestAnimationFrame(fit), { once: true });
                }
                continue;
              }

              const viewportArea = viewWidth * viewHeight;
              const largeImage = naturalWidth * naturalHeight >= viewportArea * 0.35
                || (naturalWidth >= viewWidth * 0.6 && naturalHeight >= viewHeight * 0.6);
              if (!largeImage && !(shortImagePage && index === 0)) continue;
              element.classList.add('libzora-cover');
              if (isSvgImage && element.parentElement
                  && /^svg$/i.test(element.parentElement.tagName))
                element.parentElement.classList.add('libzora-cover');
              return true;
            }
            return false;
          };
          window.__libzoraFitReaderCoverImage = fit;
          if (!window.__libzoraFitReaderCoverImageResizeWatch) {
            window.__libzoraFitReaderCoverImageResizeWatch = true;
            window.addEventListener('resize', () => requestAnimationFrame(fit), { passive: true });
          }
          const fitted = fit();
          requestAnimationFrame(fit);
          return fitted;
        })();
        """;

    // A WebView navigation completion only means that the document itself is
    // ready; it does not mean a font introduced by the injected style has
    // finished downloading. Keep the native host hidden while FontFaceSet
    // settles so a TOC swap cannot reveal fallback text and then reflow into
    // the bundled reading font.
    private static async Task WaitForReaderFontsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 24;
        const int delayMilliseconds = 25;
        const string waitScript = """
            (() => {
              const fonts = document.fonts;
              if (!fonts) return 'ready';
              if (!window.__libzoraReaderFontWaitStarted) {
                window.__libzoraReaderFontWaitStarted = true;
                const requests = [fonts.ready];
                try {
                  // Explicitly request the bundled family. It is the fallback
                  // used by the default reader stack and this also starts the
                  // load when the page's own CSS did not specify a font face.
                  requests.push(fonts.load('400 1em "LibzoraKingHwaOldSong"', '祖母的退化论有人挠 AaWwii'));
                } catch (_) { }
                Promise.allSettled(requests).then(() => {
                  window.__libzoraReaderFontReady = true;
                });
              }
              return window.__libzoraReaderFontReady === true ? 'ready' : 'pending';
            })();
            """;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await host.InvokeScriptAsync(waitScript);
                if (string.Equals(
                    result?.Trim().Trim('"'),
                    "ready",
                    StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch
            {
                // A fixed-layout or partially initialized document may reject
                // FontFaceSet access. The style has still been applied, so do
                // not turn a cosmetic wait into a chapter navigation failure.
                return;
            }

            await Task.Delay(delayMilliseconds, cancellationToken);
        }
    }

    // Fallback stack for the reading font, mirroring the WinUI reference's
    // BuildReaderFontStack: the chosen family first (comma-separated combined
    // families like "Source Han Serif SC, Noto Serif CJK SC" are split), then
    // the bundled KingHwaOldSong, common serif CJK fonts and sans-serif.
    private static string BuildReaderFontStack(string? fontFamily)
    {
        var families = new List<string>();
        void Add(string? family)
        {
            var value = family?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return;
            // The settings label is the human-facing family name, while the
            // bundled TTF exposes the stable internal family name below.
            // Map the default explicitly so it cannot resolve to a different
            // system font before the app asset gets a chance to load.
            if (string.Equals(value, ReaderFontDefaults.DefaultFamily, StringComparison.OrdinalIgnoreCase))
                value = ReaderWebBundledFontFamily;
            if (families.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                return;
            families.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            foreach (var part in fontFamily.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(part);
            }
        }
        Add(ReaderWebBundledFontFamily);
        Add("Source Han Serif SC");
        Add("Noto Serif CJK SC");
        if (OperatingSystem.IsLinux())
        {
            Add("Noto Sans CJK SC");
            Add("Source Han Sans SC");
            Add("DejaVu Sans");
        }
        else
        {
            Add("Microsoft YaHei UI");
        }
        families.Add("sans-serif");
        return string.Join(", ", families.Select(family => $"\"{family}\""));
    }

    private async Task<string?> GetBundledFontUriAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        string sourcePath;
        try
        {
            sourcePath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Fonts",
                ReaderFontDefaults.BundledFontFileName);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(sourcePath)) return null;

        var document = _readerDocument;
        if (document is null || string.IsNullOrWhiteSpace(document.RootPath))
            return new Uri(sourcePath).AbsoluteUri;

        string rootPath;
        string targetDirectory;
        string targetPath;
        try
        {
            rootPath = Path.GetFullPath(document.RootPath);
            targetDirectory = rootPath;
            if (!OperatingSystem.IsLinux()
                && host.Source is { IsFile: true } chapterSource
                && IsPathInside(rootPath, chapterSource.LocalPath))
            {
                var chapterDirectory = Path.GetDirectoryName(Path.GetFullPath(chapterSource.LocalPath));
                if (!string.IsNullOrWhiteSpace(chapterDirectory))
                    targetDirectory = chapterDirectory;
            }
            targetPath = Path.Combine(targetDirectory, ReaderBundledFontTargetFileName);
        }
        catch
        {
            return new Uri(sourcePath).AbsoluteUri;
        }

        await _readerBundledFontGate.WaitAsync(cancellationToken);
        try
        {
            var sourceLength = new FileInfo(sourcePath).Length;
            var targetInfo = new FileInfo(targetPath);
            if (!targetInfo.Exists || targetInfo.Length != sourceLength)
            {
                await using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep a readable fallback if a read-only cache or a transient
            // filesystem error prevents the same-origin copy.
            return new Uri(sourcePath).AbsoluteUri;
        }
        finally
        {
            _readerBundledFontGate.Release();
        }

        if (host.Source is { IsFile: true } source
            && IsPathInside(rootPath, source.LocalPath))
        {
            var chapterDirectory = Path.GetDirectoryName(Path.GetFullPath(source.LocalPath));
            if (!string.IsNullOrWhiteSpace(chapterDirectory))
            {
                var relativePath = Path.GetRelativePath(chapterDirectory, targetPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                return string.Join(
                    "/",
                    relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString));
            }
        }

        return new Uri(targetPath).AbsoluteUri;
    }
}
