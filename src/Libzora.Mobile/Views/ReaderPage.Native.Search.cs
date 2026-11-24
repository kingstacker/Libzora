using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Native progressive search.
/// </summary>
public partial class ReaderPage
{
    private sealed class NativeSearchSession(
        int generation,
        string searchId,
        string query,
        int limit)
    {
        public int Generation { get; } = generation;
        public string SearchId { get; } = searchId;
        public string Query { get; } = query;
        public int Limit { get; set; } = limit;
        public bool IsPdf { get; set; }
        public int Scanned { get; set; }
        public int Total { get; set; }
        public bool IsRunning { get; set; }
        public Queue<SearchHit> PendingHits { get; } = new();
        public List<int> RemainingChapters { get; } = [];
        public int NextChapterCursor { get; set; }

        public EpubReaderDocument? Document { get; set; }
        public BookFile? File { get; set; }
        public int CurrentChapter { get; set; }
        public string Body { get; set; } = string.Empty;
        public (int Start, int Length)[] RubyRanges { get; set; } = [];
        public bool CurrentChapterScanned { get; set; }

        public IReadOnlyList<PdfPageText>? PdfPages { get; set; }
        public int CurrentPage { get; set; }
        public string PdfPath { get; set; } = string.Empty;
        public bool CurrentPageScanned { get; set; }

        public bool HasMore => PendingHits.Count > 0
            || NextChapterCursor < RemainingChapters.Count
            || (IsPdf ? !CurrentPageScanned : !CurrentChapterScanned);
    }

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
        var session = BeginNativeSearch(query, searchId);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _nativeSearchCancellation = cancellation;

        if (_isPdf)
        {
            var pages = _pdfText;
            var currentPage = _pdfPage;
            var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile!);
            var totalPages = Math.Max(1, _pdfPageCount);
            lock (_nativeSearchLock)
            {
                session.IsPdf = true;
                session.PdfPages = pages;
                session.CurrentPage = currentPage;
                session.PdfPath = path;
                session.Total = totalPages;
                session.IsRunning = true;
                if (pages is { Count: > 0 })
                {
                    var current = Math.Clamp(currentPage - 1, 0, pages.Count - 1);
                    session.Total = pages.Count;
                    session.RemainingChapters.AddRange(EnumerateSearchIndices(current, pages.Count));
                }
            }
            PublishNativeSearch(session.Generation, 0, totalPages, isFinal: false);
            _ = Task.Run(() => RunProgressivePdfSearchAsync(session, cancellation, operation, isResume: false));
            return session.Generation;
        }

        if (_epubDocument is not { } document || _bookFile is not { } file)
        {
            Interlocked.CompareExchange(ref _nativeSearchCancellation, null, cancellation);
            cancellation.Dispose();
            operation.Dispose();
            lock (_nativeSearchLock)
            {
                session.CurrentChapterScanned = true;
                session.IsRunning = false;
            }
            PublishNativeSearch(session.Generation, 0, 0, isFinal: true);
            return session.Generation;
        }

        var totalChapters = Math.Max(1, document.Chapters.Count);
        var currentChapter = Math.Clamp(_chapterIndex, 0, totalChapters - 1);
        var host = _readerHost as NativeReaderHost;
        var body = host?.BodyText ?? string.Empty;
        var rubyRanges = host?.RubyAnnotationRanges.ToArray() ?? [];
        lock (_nativeSearchLock)
        {
            session.Document = document;
            session.File = file;
            session.CurrentChapter = currentChapter;
            session.Body = body;
            session.RubyRanges = rubyRanges;
            session.Total = totalChapters;
            session.IsRunning = true;
            session.RemainingChapters.AddRange(EnumerateSearchIndices(currentChapter, document.Chapters.Count));
        }

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
            AppendNativeSearchHits(session, hits, 0, totalChapters, isFinal: false);
        }
        else
        {
            PublishNativeSearch(session.Generation, 0, totalChapters, isFinal: false);
        }

        _ = Task.Run(() => RunProgressiveEpubSearchAsync(session, cancellation, operation, isResume: false));
        return session.Generation;
    }

    /// <summary>
    /// Expands the active search cap by another batch and resumes from the
    /// exact chapter/page and buffered overflow where the previous batch stopped.
    /// </summary>
    public int NativeContinueSearch(string? searchId = null)
    {
        var operation = MobileDataCoordinator.TryBeginOperation()
            ?? throw new InvalidOperationException(UiText.Get("操作已取消"));
        NativeSearchSession? session;
        CancellationTokenSource? cancellation = null;
        int generation;
        int scanned;
        int total;
        bool isPdf;

        lock (_nativeSearchLock)
        {
            session = _nativeSearchSession;
            if (session is null
                || session.Generation != _nativeSearchGeneration
                || (!string.IsNullOrEmpty(searchId) && session.SearchId != searchId)
                || session.IsRunning
                || !session.HasMore)
            {
                operation.Dispose();
                return _nativeSearchGeneration;
            }

            session.Limit += MaxSearchResults;
            session.IsRunning = true;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _nativeSearchCancellation = cancellation;
            generation = session.Generation;
            scanned = session.Scanned;
            total = session.Total;
            isPdf = session.IsPdf;
        }

        PublishNativeSearch(generation, scanned, total, isFinal: false);
        _ = Task.Run(() => isPdf
            ? RunProgressivePdfSearchAsync(session, cancellation, operation, isResume: true)
            : RunProgressiveEpubSearchAsync(session, cancellation, operation, isResume: true));
        return generation;
    }

    public void NativeCancelSearch(string? searchId = null)
    {
        CancellationTokenSource? cancellation;
        lock (_nativeSearchLock)
        {
            if (searchId is not null && searchId != _nativeSearchId) return;
            Interlocked.Increment(ref _nativeSearchGeneration);
            _nativeSearchSession = null;
            cancellation = Interlocked.Exchange(ref _nativeSearchCancellation, null);
        }
        CancelNativeSearchJump();
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private NativeSearchSession BeginNativeSearch(string query, string? searchId = null)
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
            var session = new NativeSearchSession(generation, _nativeSearchId, query, MaxSearchResults);
            _nativeSearchSession = session;
            return session;
        }
    }

    private async Task RunProgressiveEpubSearchAsync(
        NativeSearchSession session,
        CancellationTokenSource cancellation,
        MobileDataCoordinator.OperationLease operation,
        bool isResume)
    {
        var token = cancellation.Token;
        var document = session.Document!;
        var file = session.File!;
        var query = session.Query;
        var currentChapter = session.CurrentChapter;
        var body = session.Body;
        var rubyRanges = session.RubyRanges;
        var total = document.Chapters.Count;
        try
        {
            if (total == 0)
            {
                lock (_nativeSearchLock)
                {
                    session.CurrentChapterScanned = true;
                    session.IsRunning = false;
                }
                PublishNativeSearch(session.Generation, 0, 0, isFinal: true);
                return;
            }

            if (isResume)
            {
                DrainPendingNativeSearchHits(session, total);
                if (ShouldPauseAtLimit(session))
                {
                    PublishFinalSearchState(session, total);
                    return;
                }
            }

            var snapshot = await _modules.Reader.Data.GetBookContentIndexSnapshotAsync(
                file.Id, file.Sha256, token);
            token.ThrowIfCancellationRequested();

            if (!isResume)
            {
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
                    lock (_nativeSearchLock) { session.CurrentChapterScanned = true; }
                    AppendNativeSearchHits(session, hits, 1, total, isFinal: false);
                }
                else
                {
                    lock (_nativeSearchLock)
                    {
                        session.CurrentChapterScanned = true;
                        session.Scanned = Math.Max(session.Scanned, 1);
                    }
                }

                // The current chapter was searched from the already-loaded body
                // text, but still needs to enter the index for later searches.
                if (!snapshot.IsComplete && _epubPreparationSession is { IsComplete: false } prepSession)
                {
                    document = await prepSession.CompletionTask.WaitAsync(token);
                }

                if (!snapshot.IsComplete && !snapshot.IndexedChapters.Contains(currentChapter))
                {
                    await _modules.Reader.Content.IndexBookChaptersAsync(
                        _book, file, document, [currentChapter], null, token);
                }

                if (ShouldPauseAtLimit(session))
                {
                    PublishFinalSearchState(session, total);
                    return;
                }
            }

            while (TryPeekEpubChapterRun(session, snapshot, out var chapterRun, out var needsIndex))
            {
                token.ThrowIfCancellationRequested();
                if (ShouldPauseAtLimit(session))
                {
                    PublishFinalSearchState(session, total);
                    return;
                }

                if (needsIndex)
                {
                    var chapterIndex = chapterRun[0];
                    await _modules.Reader.Content.IndexBookChaptersAsync(
                        _book,
                        file,
                        document,
                        [chapterIndex],
                        (chapter, _) =>
                        {
                            var nextScanned = AdvanceSessionChapter(session, 1);
                            AppendNativeSearchHits(
                                session,
                                FindChapterTextSearchHits(query, chapter),
                                nextScanned,
                                total,
                                isFinal: false);
                            return Task.CompletedTask;
                        },
                        token);

                    if (ShouldPauseAtLimit(session))
                    {
                        PublishFinalSearchState(session, total);
                        return;
                    }
                }
                else
                {
                    // Read only this contiguous indexed run and enumerate every occurrence.
                    // The ranked retrieval API intentionally collapses paragraphs for
                    // AI context, which would hide repeated matches in the reader.
                    var chunks = await _modules.Reader.Data.GetBookChunksAsync(
                        _book.Id, file.Id, token,
                        chapterFrom: chapterRun[0], chapterTo: chapterRun[^1], sourceHash: file.Sha256);
                    token.ThrowIfCancellationRequested();
                    var chunksByChapter = chunks.GroupBy(c => c.ChapterIndex)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<BookContentChunk>)g.ToArray());

                    foreach (var chapterIndex in chapterRun)
                    {
                        token.ThrowIfCancellationRequested();
                        if (ShouldPauseAtLimit(session))
                        {
                            PublishFinalSearchState(session, total);
                            return;
                        }

                        var chapterHits = chunksByChapter.TryGetValue(chapterIndex, out var chapterChunks)
                            ? FindChunkSearchHits(chapterChunks, query, token).ToArray()
                            : [];
                        var nextScanned = AdvanceSessionChapter(session, 1);
                        AppendNativeSearchHits(
                            session,
                            chapterHits,
                            nextScanned,
                            total,
                            isFinal: false);
                    }
                }
            }

            var allChaptersScanned = false;
            lock (_nativeSearchLock)
            {
                allChaptersScanned = session.NextChapterCursor >= session.RemainingChapters.Count
                    && session.CurrentChapterScanned;
            }
            if (!snapshot.IsComplete && allChaptersScanned)
            {
                await _modules.Reader.Data.MarkBookContentIndexCompleteAsync(
                    file.Id, file.Sha256, total, token);
            }

            PublishFinalSearchState(session, total);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            int scanned;
            lock (_nativeSearchLock)
            {
                session.IsRunning = false;
                scanned = session.Scanned;
            }
            PublishNativeSearch(session.Generation, scanned, total, isFinal: true, UiText.Localize(exception.Message));
        }
        finally
        {
            lock (_nativeSearchLock) { session.IsRunning = false; }
            Interlocked.CompareExchange(ref _nativeSearchCancellation, null, cancellation);
            cancellation.Dispose();
            operation.Dispose();
        }
    }

    private async Task RunProgressivePdfSearchAsync(
        NativeSearchSession session,
        CancellationTokenSource cancellation,
        MobileDataCoordinator.OperationLease operation,
        bool isResume)
    {
        var token = cancellation.Token;
        var pages = session.PdfPages;
        var currentPage = session.CurrentPage;
        var query = session.Query;
        var total = Math.Max(1, pages?.Count ?? session.Total);
        try
        {
            if (pages is null)
            {
                PublishNativeSearch(session.Generation, 0, total, isFinal: false);
                pages = await new PdfTextService().ExtractAsync(session.PdfPath, token);
                token.ThrowIfCancellationRequested();
                _pdfText = pages;
                lock (_nativeSearchLock)
                {
                    session.PdfPages = pages;
                    session.Total = Math.Max(1, pages.Count);
                    if (pages.Count > 0 && session.RemainingChapters.Count == 0 && session.NextChapterCursor == 0)
                    {
                        var currentIndex = Math.Clamp(currentPage - 1, 0, pages.Count - 1);
                        session.RemainingChapters.AddRange(EnumerateSearchIndices(currentIndex, pages.Count));
                    }
                }
            }

            total = Math.Max(1, pages.Count);
            if (pages.Count == 0)
            {
                lock (_nativeSearchLock)
                {
                    session.CurrentPageScanned = true;
                    session.IsRunning = false;
                }
                PublishNativeSearch(session.Generation, 0, total, isFinal: true);
                return;
            }

            if (isResume)
            {
                DrainPendingNativeSearchHits(session, total);
                if (ShouldPauseAtLimit(session))
                {
                    PublishFinalSearchState(session, total);
                    return;
                }
            }
            else
            {
                var current = Math.Clamp(currentPage - 1, 0, pages.Count - 1);
                lock (_nativeSearchLock) { session.CurrentPageScanned = true; }
                AppendNativeSearchHits(
                    session,
                    SearchPdfPages(pages, current, 1, query, token),
                    1,
                    total,
                    isFinal: false);
                if (ShouldPauseAtLimit(session))
                {
                    PublishFinalSearchState(session, total);
                    return;
                }
            }

            while (TryPeekPdfPageSlice(session, out var pageSlice))
            {
                token.ThrowIfCancellationRequested();
                foreach (var pageIndex in pageSlice)
                {
                    token.ThrowIfCancellationRequested();
                    if (ShouldPauseAtLimit(session))
                    {
                        PublishFinalSearchState(session, total);
                        return;
                    }

                    var nextScanned = AdvanceSessionChapter(session, 1);
                    AppendNativeSearchHits(
                        session,
                        SearchPdfPages(pages, pageIndex, 1, query, token),
                        nextScanned,
                        total,
                        isFinal: false);
                }
            }

            PublishFinalSearchState(session, total);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            int scanned;
            lock (_nativeSearchLock)
            {
                session.IsRunning = false;
                scanned = session.Scanned;
            }
            PublishNativeSearch(session.Generation, scanned, total, isFinal: true, UiText.Localize(exception.Message));
        }
        finally
        {
            lock (_nativeSearchLock) { session.IsRunning = false; }
            Interlocked.CompareExchange(ref _nativeSearchCancellation, null, cancellation);
            cancellation.Dispose();
            operation.Dispose();
        }
    }

    private bool ShouldPauseAtLimit(NativeSearchSession session)
    {
        lock (_nativeSearchLock)
        {
            return session.Generation == _nativeSearchGeneration
                && _nativeSearchEntries.Count >= session.Limit
                && session.HasMore;
        }
    }

    private void PublishFinalSearchState(NativeSearchSession session, int total)
    {
        int scanned;
        lock (_nativeSearchLock)
        {
            session.IsRunning = false;
            scanned = session.Scanned;
        }
        PublishNativeSearch(session.Generation, scanned, total, isFinal: true);
    }

    private int AdvanceSessionChapter(NativeSearchSession session, int count)
    {
        lock (_nativeSearchLock)
        {
            session.NextChapterCursor = Math.Min(session.RemainingChapters.Count, session.NextChapterCursor + count);
            session.Scanned = Math.Min(Math.Max(session.Total, 1), session.Scanned + count);
            return session.Scanned;
        }
    }

    private bool TryPeekEpubChapterRun(
        NativeSearchSession session,
        BookContentIndexSnapshot snapshot,
        out int[] chapterRun,
        out bool needsIndex)
    {
        lock (_nativeSearchLock)
        {
            if (session.NextChapterCursor >= session.RemainingChapters.Count)
            {
                chapterRun = [];
                needsIndex = false;
                return false;
            }

            var first = session.RemainingChapters[session.NextChapterCursor];
            if (!snapshot.IsComplete && !snapshot.IndexedChapters.Contains(first))
            {
                chapterRun = [first];
                needsIndex = true;
                return true;
            }

            var run = new List<int>(NativeSearchWindowChapters) { first };
            var index = session.NextChapterCursor + 1;
            while (run.Count < NativeSearchWindowChapters && index < session.RemainingChapters.Count)
            {
                var next = session.RemainingChapters[index];
                if (next != run[^1] + 1) break;
                if (!snapshot.IsComplete && !snapshot.IndexedChapters.Contains(next)) break;
                run.Add(next);
                index++;
            }

            chapterRun = run.ToArray();
            needsIndex = false;
            return true;
        }
    }

    private bool TryPeekPdfPageSlice(NativeSearchSession session, out int[] pageSlice)
    {
        lock (_nativeSearchLock)
        {
            if (session.NextChapterCursor >= session.RemainingChapters.Count)
            {
                pageSlice = [];
                return false;
            }

            var count = Math.Min(NativeSearchWindowChapters, session.RemainingChapters.Count - session.NextChapterCursor);
            pageSlice = session.RemainingChapters.GetRange(session.NextChapterCursor, count).ToArray();
            return true;
        }
    }

    private void DrainPendingNativeSearchHits(NativeSearchSession session, int totalChapters)
    {
        NativeReaderSearchBatch? batch = null;
        lock (_nativeSearchLock)
        {
            if (session.Generation != _nativeSearchGeneration) return;
            var added = new List<NativeReaderSearchItem>();
            while (session.PendingHits.Count > 0 && _nativeSearchEntries.Count < session.Limit)
            {
                var hit = session.PendingHits.Dequeue();
                var (chapterIndex, offset) = SearchHitPosition(hit);
                added.Add(InsertNativeSearchEntryLocked(chapterIndex, offset, hit));
            }

            if (added.Count == 0) return;
            batch = BuildNativeSearchBatchLocked(session, added.ToArray(), session.Scanned, totalChapters, isFinal: false, null);
        }

        if (batch is not null) NativeSearchBatchChanged?.Invoke(batch);
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
        return PdfTextService.Search(slice, query, int.MaxValue, token)
            .Select(result => new SearchHit(
                UiText.Get("第 {0} 页", result.PageNumber),
                result.Excerpt,
                query,
                result))
            .ToArray();
    }

    private static IEnumerable<int> EnumerateSearchIndices(int current, int total)
    {
        for (var index = current + 1; index < total; index++)
            yield return index;
        for (var index = 0; index < current; index++)
            yield return index;
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

    private NativeReaderSearchItem InsertNativeSearchEntryLocked(int chapterIndex, int offset, SearchHit hit)
    {
        var entry = new NativeSearchEntry(_nativeSearchNextId++, chapterIndex, offset, hit);
        var insertAt = _nativeSearchEntries.Count;
        while (insertAt > 0
            && CompareSearchEntries(_nativeSearchEntries[insertAt - 1], entry) > 0)
        {
            insertAt--;
        }
        _nativeSearchEntries.Insert(insertAt, entry);
        _nativeSearchEntriesById[entry.Id] = entry;
        return new NativeReaderSearchItem(entry.Id, entry.Hit.Title, entry.Hit.Excerpt, chapterIndex, offset);
    }

    private void AppendNativeSearchHits(
        NativeSearchSession session,
        IEnumerable<SearchHit> hits,
        int scannedChapters,
        int totalChapters,
        bool isFinal,
        bool queueOverflow = true)
    {
        NativeReaderSearchBatch? batch = null;
        lock (_nativeSearchLock)
        {
            if (session.Generation != _nativeSearchGeneration) return;
            session.Scanned = Math.Max(session.Scanned, scannedChapters);
            var added = new List<NativeReaderSearchItem>();
            foreach (var hit in hits)
            {
                var (chapterIndex, offset) = SearchHitPosition(hit);
                var key = $"{chapterIndex}\u001f{offset}";
                if (!queueOverflow && _nativeSearchEntries.Count >= session.Limit) break;
                if (!_nativeSearchSeen.Add(key)) continue;
                if (_nativeSearchEntries.Count >= session.Limit)
                {
                    session.PendingHits.Enqueue(hit);
                    continue;
                }
                added.Add(InsertNativeSearchEntryLocked(chapterIndex, offset, hit));
            }

            if (added.Count == 0 && !isFinal) return;
            batch = BuildNativeSearchBatchLocked(
                session, added.ToArray(), session.Scanned, totalChapters, isFinal, null);
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
            if (generation != _nativeSearchGeneration || _nativeSearchSession is not { } session) return;
            batch = BuildNativeSearchBatchLocked(session, [], scannedChapters, totalChapters, isFinal, error);
        }
        if (batch is not null) NativeSearchBatchChanged?.Invoke(batch);
    }

    private NativeReaderSearchBatch BuildNativeSearchBatchLocked(
        NativeSearchSession session,
        NativeReaderSearchItem[] items,
        int scannedChapters,
        int totalChapters,
        bool isFinal,
        string? error)
    {
        return new NativeReaderSearchBatch(
            session.Generation,
            session.SearchId,
            session.Query,
            items,
            scannedChapters,
            totalChapters,
            isFinal,
            error,
            HasMore: isFinal && session.HasMore,
            IsIncremental: true,
            TotalHits: _nativeSearchEntries.Count);
    }

    private string ChapterSearchTitle(int chapterIndex, EpubReaderDocument document) =>
        document.ChapterTitles.ElementAtOrDefault(chapterIndex)
        ?? Path.GetFileNameWithoutExtension(document.Chapters[chapterIndex]);

    private sealed record NativeSearchEntry(int Id, int ChapterIndex, int Offset, SearchHit Hit);

    public async Task<IReadOnlyList<NativeReaderSearchItem>> NativeSearchAsync(string query, CancellationToken token)
    {
        var session = BeginNativeSearch(query);
        var hits = await SearchBookAsync(query, new Progress<string>(), token);
        token.ThrowIfCancellationRequested();
        if (session.Generation != Volatile.Read(ref _nativeSearchGeneration)) return [];
        AppendNativeSearchHits(session, hits, 0, 0, isFinal: true, queueOverflow: false);
        lock (_nativeSearchLock)
        {
            session.IsRunning = false;
            return _nativeSearchEntries
                .Select(entry => new NativeReaderSearchItem(
                    entry.Id, entry.Hit.Title, entry.Hit.Excerpt, entry.ChapterIndex, entry.Offset))
                .ToArray();
        }
    }
}
