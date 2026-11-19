using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Context building, citations and source navigation.
/// </summary>
public partial class MainWindow
{
    private async Task<UiReaderAiContext> BuildReaderAiContextAsync(
        string question,
        ReaderAiRequestKind requestKind,
        CancellationToken cancellationToken)
    {
        var sources = new List<ReaderAiSourceViewModel>();
        var selectedText = requestKind is ReaderAiRequestKind.BookQuestion or ReaderAiRequestKind.SelectionExplain
            ? _readerPendingSelection?.Trim() ?? string.Empty : string.Empty;
        if (_readerIsPdf)
        {
            if (requestKind == ReaderAiRequestKind.BookSummary)
                await EnsureReaderPdfTextIndexAsync(cancellationToken);
            RememberCurrentPdfPageText();
            SetReaderAiRetrievalStatus("本次请求未使用向量模型。");
            var pages = _readerPdfPages.Where(page => !string.IsNullOrWhiteSpace(page.Text))
                .OrderBy(page => page.PageNumber).ToArray();
            // PDF chapters are not reliably available: chapter/selection actions
            // explicitly operate on the current page; book overview samples all pages.
            var selected = requestKind == ReaderAiRequestKind.BookSummary
                ? ReaderAiContextBuilder.SampleEvenly(pages, 16)
                : pages.Where(page => page.PageNumber == _readerPdfPage).ToArray();
            var text = new StringBuilder().AppendLine(requestKind == ReaderAiRequestKind.BookSummary
                ? T("PDF 快速概览：抽样覆盖 {0}/{1} 个有文本的页面。请明确说明这是抽样，不是全文总结。", selected.Count, pages.Length)
                : T("PDF 问答范围：仅当前第 {0} 页，不代表全书或完整章节。", _readerPdfPage));
            var allowance = Math.Max(1, 6000 / Math.Max(1, selected.Count));
            foreach (var page in selected)
            {
                var source = new ReaderAiSourceViewModel(page, $"S{sources.Count + 1}");
                sources.Add(source);
                text.AppendLine($"[{source.SourceId}] location: page={page.PageNumber}")
                    .AppendLine("content:")
                    .AppendLine(page.Text.Length > allowance ? page.Text[..allowance] + "…" : page.Text);
            }
            ReaderAiScopeText.Text = requestKind == ReaderAiRequestKind.BookSummary
                ? T("PDF 快速概览 · 抽样 {0} 页", selected.Count)
                : T("仅当前 PDF 第 {0} 页", _readerPdfPage);
            return new UiReaderAiContext(sources.Count == 0 ? string.Empty : text.ToString(), sources, selectedText);
        }

        if (_readerBookCard is null || _readerBookFile is null || _readerDocument is null)
        {
            SetReaderAiRetrievalStatus("本次请求未使用向量模型。");
            return new UiReaderAiContext(T("（当前没有可用的书籍文本。）"), sources, selectedText);
        }

        var book = _readerBookCard.Book;
        var bookFile = _readerBookFile;
        await _bookContent.EnsureIndexedAsync(book, bookFile, _readerDocument, cancellationToken);

        IReadOnlyList<ReaderRetrievalResult> retrievalResults;
        switch (requestKind)
        {
            case ReaderAiRequestKind.SelectionExplain:
            {
                var chapterPath = GetReaderChapterPath();
                var selectionStart = Math.Min(
                    _readerPendingSelectionStartOffset,
                    _readerPendingSelectionEndOffset);
                var selectionEnd = Math.Max(
                    _readerPendingSelectionStartOffset,
                    _readerPendingSelectionEndOffset);
                var chunks = await _readerData.GetBookChapterChunksAsync(
                    bookFile.Id,
                    _readerChapterIndex,
                    selectionStart,
                    Math.Max(selectionStart + 1, selectionEnd),
                    256,
                    cancellationToken);
                var selected = chunks
                    .Where(chunk => chapterPath is null
                        || string.Equals(chunk.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (selected.Length == 0)
                {
                    selected = (await _readerData.GetBookChapterChunksAsync(
                            bookFile.Id,
                            _readerChapterIndex,
                            limit: 3,
                            cancellationToken: cancellationToken))
                        .Where(chunk => chapterPath is null
                            || string.Equals(chunk.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                retrievalResults = selected
                    .Select((chunk, index) => new ReaderRetrievalResult(chunk, 1d - index * 0.01d))
                    .ToArray();
                SetReaderAiRetrievalStatus("本次请求未使用向量模型。");
                break;
            }

            case ReaderAiRequestKind.ChapterSummary:
            case ReaderAiRequestKind.BookSummary:
            {
                var chunks = requestKind == ReaderAiRequestKind.BookSummary
                    ? await _readerData.GetBookOverviewChunksAsync(
                        book.Id,
                        64,
                        cancellationToken,
                        bookFile.Id)
                    : await _readerData.GetBookChapterChunksAsync(
                        bookFile.Id,
                        _readerChapterIndex,
                        limit: 256,
                        cancellationToken: cancellationToken);
                var overview = ReaderAiContextBuilder.BuildOverview(chunks.Where(chunk =>
                    requestKind == ReaderAiRequestKind.BookSummary || chunk.ChapterIndex == _readerChapterIndex).ToArray());
                ReaderAiScopeText.Text = requestKind == ReaderAiRequestKind.BookSummary
                    ? T("快速概览 · 跨章节抽样") : T("当前章节 · 原文抽样");
                SetReaderAiRetrievalStatus("本次请求未使用向量模型。");
                return new UiReaderAiContext(overview.Text, overview.Sources.Select(source => new ReaderAiSourceViewModel(source)).ToArray());
            }

            default:
            {
                // Reserve context for the passage being read before global hits
                // consume the budget, especially for short definition questions.
                var readingChunks = await GetReaderAiReadingChunksAsync(cancellationToken);
                var retrievalQuery = await _queryRewriteService.RewriteAsync(
                    _readerAiSettings,
                    question,
                    _readerAiConversation,
                    cancellationToken);
                try
                {
                    var progress = new Progress<EmbeddingIndexProgress>(state =>
                    {
                        ReaderAiStatusText.Text = T(
                            "正在建立 AI 语义索引 {0:0}%",
                            state.Percentage);
                    });
                    var indexResult = await _readerEmbeddingIndex.EnsureIndexedAsync(
                        book.Id,
                        bookFile.Id,
                        bookFile.Sha256,
                        progress,
                        cancellationToken);
                    if (!indexResult.IsAvailable)
                    {
                        ReaderAiStatusText.Text = T("本地语义模型不可用，正在使用关键词检索…");
                        Debug.WriteLine($"[RAG] Semantic index unavailable: {indexResult.Message}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReaderAiStatusText.Text = T("语义索引不可用，正在使用关键词检索…");
                    Debug.WriteLine($"[RAG] Semantic index failed; keyword search remains available: {exception.Message}");
                }

                retrievalResults = await _readerRetriever.RetrieveAsync(
                    book.Id,
                    retrievalQuery,
                    12,
                    cancellationToken);
                var vectorSearchUsed = retrievalResults.Any(result => result.VectorRank is not null);
                SetReaderAiRetrievalStatus(
                    vectorSearchUsed
                        ? T("本次问答使用了本地 {0} 进行语义检索。", _embeddingService.SelectedPackage.DisplayName)
                        : "本次问答未使用向量模型，使用关键词检索。");
                retrievalResults = readingChunks
                    .Select((chunk, index) => new ReaderRetrievalResult(chunk, 2d - index * 0.01d))
                    .Concat(retrievalResults)
                    .DistinctBy(result => result.Chunk.Id)
                    .ToArray();
                break;
            }
        }

        var context = await _readerAiContextBuilder.BuildAsync(
            book.Id,
            retrievalResults,
            ReaderAiContextBuilder.DefaultMaxTokenBudget,
            neighborRadius: 1,
            cancellationToken);
        foreach (var source in context.Sources)
            sources.Add(new ReaderAiSourceViewModel(source));
        return new UiReaderAiContext(
            context.Text.Length == 0 ? T("（本地索引中没有找到相关片段。）") : context.Text,
            sources, selectedText);
    }

    private async Task<IReadOnlyList<BookContentChunk>> GetReaderAiReadingChunksAsync(CancellationToken cancellationToken)
    {
        if (_readerBookFile is null) return [];
        var hasSelection = !string.IsNullOrWhiteSpace(_readerPendingSelection);
        var start = hasSelection
            ? Math.Min(_readerPendingSelectionStartOffset, _readerPendingSelectionEndOffset)
            : (CurrentReaderHost as IReaderPositionHost)?.CaptureContentPosition()?.TextOffset ?? 0;
        start = Math.Max(0, start);
        var end = hasSelection
            ? Math.Max(start + 1, Math.Max(_readerPendingSelectionStartOffset, _readerPendingSelectionEndOffset))
            : start + 1;
        var chapterPath = GetReaderChapterPath();
        var chunks = await _readerData.GetBookChapterChunksAsync(
            _readerBookFile.Id, _readerChapterIndex, start, end, limit: 3, cancellationToken: cancellationToken);
        return chunks.Where(chunk => chapterPath is null
            || string.Equals(chunk.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void HandleReaderAiCitation(ReaderAiSourceViewModel source) =>
        _ = ObserveReaderTaskAsync(NavigateReaderAiSourceAsync(source));

    private static ReaderAiSourceViewModel CloneReaderAiSource(ReaderAiSourceViewModel source) =>
        source.Chunk is { } chunk
            ? new ReaderAiSourceViewModel(new ReaderAiSource(source.SourceId, chunk, source.Content))
            : new ReaderAiSourceViewModel(source.Page!, source.SourceId);

    private void ReaderAiSourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAiSourceViewModel source }) return;
        _ = ObserveReaderTaskAsync(NavigateReaderAiSourceAsync(source));
    }

    private async Task NavigateReaderAiSourceAsync(ReaderAiSourceViewModel source)
    {
        if (source.Page is { } page)
        {
            await NavigatePdfPageAsync(page.PageNumber, ReaderToken);
            return;
        }
        if (source.Chunk is not { } chunk || _readerDocument is null) return;
        var path = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            chunk.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, path)) return;
        _readerPendingChunkOffset = Math.Max(0, chunk.StartOffset);
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(chunk.ChapterTitle, new Uri(path).AbsoluteUri, chunk.ChapterIndex),
            ReaderToken,
            ReaderNavigationIntent.AiSource);
    }

    private static string LimitReaderContext(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 2400 ? normalized : normalized[..2400] + "…";
    }

    private sealed record UiReaderAiContext(
        string Text,
        IReadOnlyList<ReaderAiSourceViewModel> Sources,
        string SelectedText = "",
        UiReaderAiWebContext? WebSearch = null);

    private sealed record UiReaderAiWebContext(
        string Query, IReadOnlyList<ReaderWebSearchResult> Results, bool Failed = false)
    {
        public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.Now;
    }
}
