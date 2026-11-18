using System.Text.Json;
using Libzora.Core;
using Libzora.ApplicationServices.Tts;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

public sealed record NativeReaderState(
    bool Ready, bool ChromeVisible, bool IsPdf, string Title, string Chapter,
    string Position, double Progress, bool Bookmark, string? Error,
    TtsPlaybackState SpeechState, string SpeechStatus, string? SelectedText,
    bool FootnoteReturnVisible = false)
{
    public NativeReaderSelectionBounds? SelectionBounds { get; init; }
}

public sealed record NativeReaderSelectionBounds(double X, double Y, double Bottom,
    double ViewportWidth, double ViewportHeight);

/// <summary>One in-page note card. Anchors use the reader surface's coordinates.</summary>
public sealed record NativeReaderFootnote(long Id, bool Visible, string Title, string Href, string? Text,
    bool Loading, bool CanJump, NativeReaderFootnoteAnchor Anchor, string? Error);

public sealed record NativeReaderFootnoteAnchor(double X, double Y, double Width, double Height,
    double ViewportWidth, double ViewportHeight)
{
    public static readonly NativeReaderFootnoteAnchor Empty = new(0, 0, 0, 0, 0, 0);
}

public sealed record NativeReaderTocItem(int Id, string Title, int Level, bool Selected);
public sealed record NativeReaderSearchItem(int Id, string Title, string Excerpt);

/// <summary>
/// One progressive search update. Items are cumulative and keep stable ids so
/// a card can be tapped while later chapters are still being scanned.
/// </summary>
public sealed record NativeReaderSearchBatch(
    int Generation,
    string SearchId,
    string Query,
    NativeReaderSearchItem[] Items,
    int ScannedChapters,
    int TotalChapters,
    bool IsFinal,
    string? Error);

public sealed record NativeReaderDialog(string Kind, string Title, string Text, Guid? AnnotationId = null, string? Note = null);

/// <summary>A figure or table the native frontend should show full screen.</summary>
public sealed record NativeReaderMedia(Guid Id, string Kind, string Title, string Path);

/// <summary>
/// Data/actions for a native frontend around the existing rendering surface.
/// This mode has no Avalonia buttons, popups, sliders, or selection toolbar in
/// the displayed tree. The renderer still owns text hit testing and pagination.
/// </summary>
public partial class ReaderPage
{
    private bool _usesNativeFrontend;
    private bool? _nativeAppAppearanceDark;
    private int _nativeAppAppearanceRevision = -1;
    private bool _nativeChromeVisible = false;
    private string? _nativeError;
    private Func<string>? _nativeErrorText;
    private readonly object _nativeSearchLock = new();
    private readonly HashSet<string> _nativeSearchSeen = new(StringComparer.Ordinal);
    private readonly List<NativeSearchEntry> _nativeSearchEntries = [];
    private readonly Dictionary<int, NativeSearchEntry> _nativeSearchEntriesById = [];
    private CancellationTokenSource? _nativeSearchCancellation;
    private int _nativeSearchGeneration;
    private int _nativeSearchNextId;
    private string _nativeSearchQuery = string.Empty;
    private string _nativeSearchId = string.Empty;
    private const int NativeSearchWindowChapters = 10;

    public event Action<NativeReaderState>? NativeStateChanged;
    public event Action<NativeReaderDialog>? NativeDialogRequested;
    public event Action<NativeReaderMedia>? NativeReaderMediaRequested;
    public event Action<NativeReaderSearchBatch>? NativeSearchBatchChanged;

    /// <summary>
    /// Figures open immediately; tables are re-composed on a wide canvas in
    /// the background because the phone page may have flattened them to text.
    /// </summary>
    private void OpenReaderObject(JsonElement root)
    {
        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        if (kind == "image")
        {
            var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                RaiseReaderMedia("image", path);
            }
            else
            {
                _shell.ShowToast(UiText.Get("图片文件已丢失"));
            }

            return;
        }

        if (kind == "table"
            && root.TryGetProperty("blockIndex", out var indexElement)
            && indexElement.TryGetInt32(out var blockIndex))
        {
            _ = OpenTablePreviewAsync(blockIndex);
        }
    }

    private void RaiseReaderMedia(string kind, string path) =>
        NativeReaderMediaRequested?.Invoke(
            new NativeReaderMedia(Guid.NewGuid(), kind, _book.Title, path));

    private async Task OpenTablePreviewAsync(int blockIndex)
    {
        if (_readerHost is not NativeReaderHost host) return;
        try
        {
            var width = Math.Max(720, ReaderHostSlot.Bounds.Width * 2.4);
            var path = await host.RenderTablePreviewAsync(blockIndex, width, _lifetime.Token);
            if (string.IsNullOrWhiteSpace(path))
            {
                _shell.ShowToast(UiText.Get("这个表格暂时无法单独显示"));
                return;
            }

            RaiseReaderMedia("table", path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Get("无法打开表格"));
            Console.WriteLine($"[Libzora] table preview failed: {exception.Message}");
        }
    }

    public void UseNativeFrontend()
    {
        _usesNativeFrontend = true;
        // The native shell opens its own viewer for figures and tables; the
        // Avalonia shells keep the existing tap-to-turn behavior.
        if (_readerHost is NativeReaderHost native) native.ReaderObjectsEnabled = true;
        TopChrome.IsVisible = false;
        BottomChrome.IsVisible = false;
        ReaderFooter.IsVisible = false;
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SelectionLayer.IsVisible = false;
        SpeechBar.IsVisible = false;
        SpeechDock.IsVisible = false;
        ProgressSliderGroup.SetHostVisible(false);
        NotifyNativeState();
    }

    public NativeReaderState GetNativeState() => new(
        _ready, _nativeChromeVisible, _isPdf, _book.Title,
        FooterChapter.Text ?? string.Empty, FooterPosition.Text ?? string.Empty,
        _overallProgress * 100, FindCurrentBookmark() is not null, _nativeError,
        _tts?.State ?? TtsPlaybackState.Stopped, SpeechStatusText.Text ?? string.Empty,
        _selection?.Text, _footnoteReturn.Count > 0)
    {
        SelectionBounds = _selection is { X: { } x, Y: { } y } selected
            ? new(x, y, selected.Bottom ?? y, ReaderPaper.Bounds.Width, ReaderPaper.Bounds.Height)
            : null
    };

    /// <summary>
    /// Refreshes UI labels after a language change without navigating, reflowing
    /// the document, clearing selection, or interrupting speech and AI sessions.
    /// Call on the UI thread.
    /// </summary>
    public void NativeRefreshUiText()
    {
        if (_disposed) return;
        TypographyLabel.Text = _isPdf ? UiText.Get("显示") : UiText.Get("排版");
        if (_ready) RefreshPositionUiText();
        RefreshSpeechUiText(_tts?.State ?? TtsPlaybackState.Stopped,
            _tts?.SegmentIndex ?? 0, _tts?.SegmentCount ?? 0);
        if (_nativeErrorText is { } errorText)
        {
            _nativeError = errorText();
            ErrorText.Text = _nativeError;
        }
        RefreshFootnoteUiText();
        NotifyNativeState();
    }

    private void NotifyNativeState()
    {
        if (_usesNativeFrontend && !_disposed) NativeStateChanged?.Invoke(GetNativeState());
    }

    public IReadOnlyList<NativeReaderTocItem> GetNativeToc()
    {
        // Match the legacy sheet: exactly one entry is current, so the native
        // list can scroll to it even when the page has no entry of its own.
        var selected = CurrentTocIndex();
        return _tocEntries.Select((item, index) =>
            new NativeReaderTocItem(index, item.DisplayTitle, item.Level, index == selected)).ToArray();
    }

    public Task NativeJumpToTocAsync(int index)
    {
        ClearFootnoteNavigation();
        return index >= 0 && index < _tocEntries.Count
            ? JumpToAsync(_tocEntries[index]) : Task.CompletedTask;
    }

    public Task NativeSeekAsync(double percent)
    {
        ClearFootnoteNavigation();
        return SeekAsync(Math.Clamp(percent, 0, 100));
    }

    public Task NativeMoveChapterAsync(int direction)
    {
        ClearFootnoteNavigation();
        return MoveChapterAsync(Math.Sign(direction));
    }
    public Task NativeApplySettingsAsync() => ApplyReaderSettingsAsync();

    public void NativeSetAppAppearance(bool dark, int revision)
    {
        if (revision <= _nativeAppAppearanceRevision) return;
        _nativeAppAppearanceRevision = revision;
        if (_nativeAppAppearanceDark == dark) return;
        _nativeAppAppearanceDark = dark;
        if (_started && !_disposed) RefreshReaderAppearance();
    }

    public Task NativeToggleSpeechAsync(CancellationToken cancellationToken = default) => ToggleListeningAsync(cancellationToken);
    public async Task NativeSpeakSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        cancellationToken.ThrowIfCancellationRequested();
        var text = _selection?.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var request = BeginSpeechPreparation(cancellationToken);
        var token = request.Token;
        var started = false;
        try
        {
            await EnsureLayoutCompleteAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (_shell.Services is not { CreateTtsEngine: { } createEngine, CreateTtsPlayer: { } createPlayer })
                throw new InvalidOperationException(UiText.Get("这台设备不支持朗读"));
            var settings = await _modules.Reader.TtsSettings.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var document = BuildSpeechDocument()
                ?? throw new InvalidOperationException(UiText.Get("当前页面没有可朗读的文字。"));
            _tts ??= CreateSpeech(createEngine(), createPlayer());
            settings.AudioFormat = "wav";
            settings.Voice = TtsSelectionVoicePolicy.SelectVoice(text, settings.Voice);
            var selection = new ReaderTtsDocument(document.Key, text, 0,
                (_, _) => Task.CompletedTask, () => { },
                BookKey: document.BookKey, ChapterKey: document.ChapterKey);
            HideSelectionBar();
            SetChromeVisible(false);
            _speechExpanded = true;
            await _tts.StartSelectionAsync(selection, settings, token);
            started = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { CompleteSpeechPreparation(request, started); }
    }

    public void NativePauseSpeech() => _tts?.PauseOrResume();
    public async Task NativeStopSpeechAsync()
    {
        CancelSpeechPreparation();
        if (_tts is { } speech) await speech.StopAsync();
    }
    public string NativePageText() => CurrentPageText();
    public void NativeClearSelection() => ClearSelection();

    /// <summary>
    /// Starts a chapter-prioritised search without holding the reader
    /// navigation gate. The visible page is published synchronously, the
    /// current chapter follows on a worker, and the remaining chapters arrive
    /// in reading order as they are found or indexed.
    /// </summary>
    public int NativeStartSearch(string query, string? searchId = null)
    {
        var operation = MobileDataCoordinator.TryBeginOperation()
            ?? throw new InvalidOperationException(UiText.Get("操作已取消"));
        var generation = BeginNativeSearch(query, searchId);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _nativeSearchCancellation = cancellation;
        if (_isPdf)
        {
            var pages = _pdfText;
            var currentPage = _pdfPage;
            var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile!);
            PublishNativeSearch(generation, 0, Math.Max(1, _pdfPageCount), false);
            _ = Task.Run(() => RunProgressivePdfSearchAsync(
                generation, query, cancellation, operation, pages, currentPage, path));
            return generation;
        }

        if (_epubDocument is not { } document || _bookFile is not { } file)
        {
            Interlocked.CompareExchange(ref _nativeSearchCancellation, null, cancellation);
            cancellation.Dispose();
            operation.Dispose();
            PublishNativeSearch(generation, 0, 0, true);
            return generation;
        }

        var totalChapters = Math.Max(1, document.Chapters.Count);
        var currentChapter = Math.Clamp(_chapterIndex, 0, totalChapters - 1);
        var host = _readerHost as NativeReaderHost;
        var body = host?.BodyText ?? string.Empty;
        var rubyRanges = host?.RubyAnnotationRanges.ToArray() ?? [];
        // Capture the source coordinates on the UI thread, before navigation
        // can replace the host's chapter while the worker is searching.
        if (host?.GetVisiblePageTextRange() is { } range && range.End <= body.Length)
        {
            var visible = body[range.Start..range.End];
            var visibleRuby = rubyRanges
                .Where(item => item.Start < range.End && item.Start + item.Length > range.Start)
                .Select(item => (Start: Math.Max(range.Start, item.Start) - range.Start,
                    Length: Math.Min(range.End, item.Start + item.Length) - Math.Max(range.Start, item.Start)))
                .ToArray();
            var hits = ReaderSearchTextPolicy.FindMatches(visible, query, visibleRuby)
                .Select(match => CreateTextSearchHit(
                    currentChapter,
                    ChapterSearchTitle(currentChapter, document),
                    ChapterPathAt(currentChapter),
                    visible,
                    match.Start,
                    match.Length,
                    query,
                    range.Start))
                .ToArray();
            AppendNativeSearchHits(generation, hits, 0, totalChapters, false);
        }
        else
        {
            PublishNativeSearch(generation, 0, totalChapters, false);
        }

        _ = Task.Run(() => RunProgressiveEpubSearchAsync(
            generation, query, cancellation, operation, document, file, currentChapter, body, rubyRanges));
        return generation;
    }

    public void NativeCancelSearch(string? searchId = null)
    {
        CancellationTokenSource? cancellation;
        lock (_nativeSearchLock)
        {
            if (searchId is not null && searchId != _nativeSearchId) return;
            Interlocked.Increment(ref _nativeSearchGeneration);
            cancellation = Interlocked.Exchange(ref _nativeSearchCancellation, null);
        }
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private int BeginNativeSearch(string query, string? searchId = null)
    {
        var generation = Interlocked.Increment(ref _nativeSearchGeneration);
        var previous = Interlocked.Exchange(ref _nativeSearchCancellation, null);
        if (previous is not null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        lock (_nativeSearchLock)
        {
            _nativeSearchSeen.Clear();
            _nativeSearchEntries.Clear();
            _nativeSearchEntriesById.Clear();
            _nativeSearchNextId = 0;
            _nativeSearchQuery = query;
            _nativeSearchId = searchId ?? Guid.NewGuid().ToString("N");
        }

        return generation;
    }

    private async Task RunProgressiveEpubSearchAsync(
        int generation,
        string query,
        CancellationTokenSource cancellation,
        MobileDataCoordinator.OperationLease operation,
        EpubReaderDocument document,
        BookFile file,
        int currentChapter,
        string body,
        IReadOnlyList<(int Start, int Length)> rubyRanges)
    {
        var token = cancellation.Token;
        var scanned = 0;
        var total = 0;
        try
        {
            total = document.Chapters.Count;
            if (total == 0)
            {
                PublishNativeSearch(generation, 0, 0, true);
                return;
            }

            scanned = 1;
            if (body.Length > 0)
            {
                var title = ChapterSearchTitle(currentChapter, document);
                var path = Path.GetRelativePath(document.RootPath, document.Chapters[currentChapter]).Replace('\\', '/');
                var matches = ReaderSearchTextPolicy.FindMatches(body, query, rubyRanges);
                token.ThrowIfCancellationRequested();
                var hits = matches
                    .Select(match => CreateTextSearchHit(
                        currentChapter, title, path, body,
                        match.Start, match.Length, query, 0))
                    .ToArray();
                AppendNativeSearchHits(generation, hits, scanned, total, false);
            }

            var snapshot = await _modules.Reader.Data.GetBookContentIndexSnapshotAsync(
                file.Id, file.Sha256, token);
            token.ThrowIfCancellationRequested();

            // The current chapter was searched from the already-loaded body
            // text, but still needs to enter the index for later searches.
            if (!snapshot.IsComplete && !snapshot.IndexedChapters.Contains(currentChapter))
            {
                await _modules.Reader.Content.IndexBookChaptersAsync(
                    _book, file, document, [currentChapter], null, token);
            }

            foreach (var window in EnumerateSearchWindows(currentChapter, total))
            {
                token.ThrowIfCancellationRequested();
                if (NativeSearchCount >= MaxSearchResults)
                {
                    // The cap is reached; do not claim the remaining chapters
                    // were indexed. A later search resumes where this stopped.
                    PublishNativeSearch(generation, scanned, total, true);
                    return;
                }

                if (!snapshot.IsComplete)
                {
                    var missing = window
                        .Where(chapter => !snapshot.IndexedChapters.Contains(chapter))
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        await _modules.Reader.Content.IndexBookChaptersAsync(
                            _book,
                            file,
                            document,
                            missing,
                            (chapter, _) =>
                            {
                                AppendNativeSearchHits(
                                    generation,
                                    FindChapterTextSearchHits(query, chapter),
                                    scanned,
                                    total,
                                    false);
                                return Task.CompletedTask;
                            },
                            token);
                    }
                }

                var remaining = MaxSearchResults - NativeSearchCount;
                if (remaining <= 0)
                {
                    PublishNativeSearch(generation, scanned, total, true);
                    return;
                }

                // Read only this window and enumerate every occurrence. The
                // ranked retrieval API intentionally collapses paragraphs for
                // AI context, which would hide repeated matches in the reader.
                var chunks = await _modules.Reader.Data.GetBookChunksAsync(
                    _book.Id, file.Id, token,
                    chapterFrom: window[0], chapterTo: window[^1], sourceHash: file.Sha256);
                token.ThrowIfCancellationRequested();
                AppendNativeSearchHits(
                    generation,
                    FindChunkSearchHits(chunks, query, token).Take(MaxSearchResults).ToArray(),
                    scanned + window.Length,
                    total,
                    false);
                scanned += window.Length;
            }

            if (!snapshot.IsComplete && scanned >= total)
            {
                await _modules.Reader.Data.MarkBookContentIndexCompleteAsync(
                    file.Id, file.Sha256, total, token);
            }

            PublishNativeSearch(generation, scanned, total, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PublishNativeSearch(generation, scanned, total, true, UiText.Localize(exception.Message));
        }
        finally
        {
            Interlocked.CompareExchange(ref _nativeSearchCancellation, null, cancellation);
            cancellation.Dispose();
            operation.Dispose();
        }
    }

    private async Task RunProgressivePdfSearchAsync(
        int generation,
        string query,
        CancellationTokenSource cancellation,
        MobileDataCoordinator.OperationLease operation,
        IReadOnlyList<PdfPageText>? pages,
        int currentPage,
        string path)
    {
        var token = cancellation.Token;
        var scanned = 0;
        var total = Math.Max(1, pages?.Count ?? 0);
        try
        {
            if (pages is null)
            {
                PublishNativeSearch(generation, 0, total, false);
                pages = await new PdfTextService().ExtractAsync(path, token);
                token.ThrowIfCancellationRequested();
                _pdfText = pages;
            }

            total = Math.Max(1, pages.Count);
            var current = Math.Clamp(currentPage - 1, 0, Math.Max(0, pages.Count - 1));
            scanned = pages.Count > 0 ? 1 : 0;
            if (pages.Count > 0)
            {
                AppendNativeSearchHits(
                    generation,
                    SearchPdfPages(pages, current, 1, query, token),
                    scanned,
                    total,
                    false);
            }

            foreach (var window in EnumerateSearchWindows(current, pages.Count))
            {
                token.ThrowIfCancellationRequested();
                if (NativeSearchCount >= MaxSearchResults)
                {
                    PublishNativeSearch(generation, scanned, total, true);
                    return;
                }

                AppendNativeSearchHits(
                    generation,
                    SearchPdfPages(pages, window[0], window.Length, query, token),
                    scanned + window.Length,
                    total,
                    false);
                scanned += window.Length;
            }

            PublishNativeSearch(generation, scanned, total, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PublishNativeSearch(generation, scanned, total, true, UiText.Localize(exception.Message));
        }
        finally
        {
            Interlocked.CompareExchange(ref _nativeSearchCancellation, null, cancellation);
            cancellation.Dispose();
            operation.Dispose();
        }
    }

    private static SearchHit[] SearchPdfPages(
        IReadOnlyList<PdfPageText> pages,
        int start,
        int count,
        string query,
        CancellationToken token)
    {
        if (start < 0 || count <= 0 || start >= pages.Count) return [];
        var slice = pages.Skip(start).Take(count).ToArray();
        var remaining = Math.Max(1, MaxSearchResults);
        return PdfTextService.Search(slice, query, remaining, token)
            .Select(result => new SearchHit(
                UiText.Get("第 {0} 页", result.PageNumber),
                result.Excerpt,
                query,
                result))
            .ToArray();
    }

    private static IEnumerable<int[]> EnumerateSearchWindows(int current, int total)
    {
        for (var start = current + 1; start < total; start += NativeSearchWindowChapters)
            yield return Enumerable.Range(start, Math.Min(NativeSearchWindowChapters, total - start)).ToArray();
        for (var start = 0; start < current; start += NativeSearchWindowChapters)
            yield return Enumerable.Range(start, Math.Min(NativeSearchWindowChapters, current - start)).ToArray();
    }

    private SearchHit[] FindChapterTextSearchHits(string query, EpubIndexedChapter chapter)
    {
        var title = string.IsNullOrWhiteSpace(chapter.ChapterTitle)
            ? UiText.Get("第 {0} 章", chapter.ChapterIndex + 1)
            : chapter.ChapterTitle;
        return ReaderSearchTextPolicy.FindMatches(chapter.Text, query)
            .Select(match => CreateTextSearchHit(
                chapter.ChapterIndex,
                title,
                chapter.ChapterPath,
                chapter.Text,
                match.Start,
                match.Length,
                query,
                0))
            .ToArray();
    }

    private SearchHit CreateTextSearchHit(
        int chapterIndex,
        string title,
        string chapterPath,
        string text,
        int start,
        int length,
        string query,
        int baseOffset)
    {
        var absoluteStart = baseOffset + start;
        var chunk = new BookContentChunk(
            0,
            _book.Id,
            _bookFile?.Id ?? Guid.Empty,
            _bookFile?.Sha256 ?? string.Empty,
            chapterIndex,
            0,
            title,
            chapterPath,
            absoluteStart,
            absoluteStart + Math.Max(1, length),
            query);
        return new SearchHit(title, ExcerptAt(text, start, length), query, chunk);
    }

    private IEnumerable<SearchHit> FindChunkSearchHits(
        IReadOnlyList<BookContentChunk> chunks, string query, CancellationToken token)
    {
        var seen = new HashSet<(int Chapter, int Offset)>();
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            foreach (var match in ReaderSearchTextPolicy.FindMatches(chunk.Content, query))
            {
                if (!seen.Add((chunk.ChapterIndex, chunk.StartOffset + match.Start))) continue;
                yield return CreateTextSearchHit(
                    chunk.ChapterIndex, chunk.ChapterTitle, chunk.ChapterPath,
                    chunk.Content, match.Start, match.Length, query, chunk.StartOffset);
            }
        }
    }

    private static string ExcerptAt(string text, int start, int length)
    {
        var from = Math.Max(0, start - ExcerptPadding);
        var to = Math.Min(text.Length, start + Math.Max(1, length) + ExcerptLength);
        return (from > 0 ? "…" : string.Empty)
            + text[from..to].ReplaceLineEndings(" ").Trim();
    }

    private int NativeSearchCount
    {
        get
        {
            lock (_nativeSearchLock) return _nativeSearchEntries.Count;
        }
    }

    private static (int ChapterIndex, int Offset) SearchHitPosition(SearchHit hit)
    {
        if (hit.Source is BookContentChunk chunk)
        {
            var local = chunk.Content.IndexOf(hit.Query, StringComparison.CurrentCultureIgnoreCase);
            return (chunk.ChapterIndex, chunk.StartOffset + Math.Max(0, local));
        }

        if (hit.Source is PdfSearchResult pdf)
        {
            return (Math.Max(0, pdf.PageNumber - 1), Math.Max(0, pdf.MatchIndex));
        }

        return (int.MaxValue, 0);
    }

    private static int CompareSearchEntries(NativeSearchEntry left, NativeSearchEntry right)
    {
        var chapter = left.ChapterIndex.CompareTo(right.ChapterIndex);
        return chapter != 0 ? chapter
            : left.Offset != right.Offset ? left.Offset.CompareTo(right.Offset)
            : left.Id.CompareTo(right.Id);
    }

    private void AppendNativeSearchHits(
        int generation,
        IEnumerable<SearchHit> hits,
        int scannedChapters,
        int totalChapters,
        bool isFinal)
    {
        NativeReaderSearchBatch? batch = null;
        lock (_nativeSearchLock)
        {
            if (generation != _nativeSearchGeneration) return;
            var changed = false;
            foreach (var hit in hits)
            {
                if (_nativeSearchEntries.Count >= MaxSearchResults) break;
                var (chapterIndex, offset) = SearchHitPosition(hit);
                var key = $"{chapterIndex}\u001f{offset}";
                if (!_nativeSearchSeen.Add(key)) continue;
                var entry = new NativeSearchEntry(_nativeSearchNextId++, chapterIndex, offset, hit);
                var insertAt = _nativeSearchEntries.Count;
                while (insertAt > 0
                    && CompareSearchEntries(_nativeSearchEntries[insertAt - 1], entry) > 0)
                {
                    insertAt--;
                }
                _nativeSearchEntries.Insert(insertAt, entry);
                _nativeSearchEntriesById[entry.Id] = entry;
                changed = true;
            }

            if (!changed && !isFinal) return;
            batch = BuildNativeSearchBatchLocked(generation, scannedChapters, totalChapters, isFinal, null);
        }

        if (batch is not null) NativeSearchBatchChanged?.Invoke(batch);
    }

    private void PublishNativeSearch(
        int generation,
        int scannedChapters,
        int totalChapters,
        bool isFinal,
        string? error = null)
    {
        NativeReaderSearchBatch? batch;
        lock (_nativeSearchLock)
        {
            if (generation != _nativeSearchGeneration) return;
            batch = BuildNativeSearchBatchLocked(generation, scannedChapters, totalChapters, isFinal, error);
        }
        if (batch is not null) NativeSearchBatchChanged?.Invoke(batch);
    }

    private NativeReaderSearchBatch BuildNativeSearchBatchLocked(
        int generation,
        int scannedChapters,
        int totalChapters,
        bool isFinal,
        string? error)
    {
        var items = _nativeSearchEntries
            .Select(entry => new NativeReaderSearchItem(entry.Id, entry.Hit.Title, entry.Hit.Excerpt))
            .ToArray();
        return new NativeReaderSearchBatch(
            generation,
            _nativeSearchId,
            _nativeSearchQuery,
            items,
            scannedChapters,
            totalChapters,
            isFinal,
            error);
    }

    private string ChapterSearchTitle(int chapterIndex, EpubReaderDocument document) =>
        document.ChapterTitles.ElementAtOrDefault(chapterIndex)
        ?? Path.GetFileNameWithoutExtension(document.Chapters[chapterIndex]);

    private sealed record NativeSearchEntry(int Id, int ChapterIndex, int Offset, SearchHit Hit);

    public async Task<IReadOnlyList<NativeReaderSearchItem>> NativeSearchAsync(string query, CancellationToken token)
    {
        var generation = BeginNativeSearch(query);
        var hits = await SearchBookAsync(query, new Progress<string>(), token);
        token.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _nativeSearchGeneration)) return [];
        AppendNativeSearchHits(generation, hits, 0, 0, true);
        lock (_nativeSearchLock)
        {
            return _nativeSearchEntries
                .Select(entry => new NativeReaderSearchItem(entry.Id, entry.Hit.Title, entry.Hit.Excerpt))
                .ToArray();
        }
    }

    public async Task NativeJumpToSearchAsync(int id, string? searchId = null)
    {
        NativeSearchEntry? entry;
        lock (_nativeSearchLock)
        {
            if (searchId is not null && searchId != _nativeSearchId) return;
            _nativeSearchEntriesById.TryGetValue(id, out entry);
        }
        if (entry is null) return;
        try
        {
            await JumpToSearchHitAsync(entry.Hit);
        }
        finally
        {
            // The shared search sheet hides chrome before jumping. Native search
            // closes inline, so bring the toolbar back once the target page is
            // ready and the user lands in the text.
            SetChromeVisible(true);
        }
    }

    public Task NativeAnnotateAsync(string? note)
    {
        if (_selection is not { } selection || _bookFile is null)
            throw new InvalidOperationException(UiText.Get("请先长按并选择正文。"));
        var annotation = NewAnnotation(selection);
        if (_annotations.Any(item => item.ChapterPath == annotation.ChapterPath
            && annotation.StartOffset < item.EndOffset && annotation.EndOffset > item.StartOffset))
            throw new InvalidOperationException(UiText.Get("所选文字已有批注，请点击已有划线进行编辑。"));
        annotation.Note = note ?? string.Empty;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeEditAnnotationAsync(Guid id, string note)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException(UiText.Get("批注已不存在。"));
        annotation.Note = note;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeDeleteAnnotationAsync(Guid id)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException(UiText.Get("批注已不存在。"));
        return DeleteAnnotationAsync(annotation);
    }
}
