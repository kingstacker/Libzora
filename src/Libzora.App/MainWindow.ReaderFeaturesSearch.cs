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
/// Whole-book and in-page search.
/// </summary>
public partial class MainWindow
{
    private async void ReaderTocSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_readerSearchVisible) return;
        var sequence = ++_readerWholeSearchSequence;
        _readerWholeSearchCancellation?.Cancel();
        var query = ReaderTocSearchBox.Text?.Trim() ?? string.Empty;
        _readerSearchQuery = query;
        if (query.Length == 0)
        {
            await RefreshReaderWholeSearchAsync(query, sequence);
            return;
        }
        ShowReaderSearchStatus(T("正在本地搜索…"));
        await Task.Delay(180);
        if (sequence != _readerWholeSearchSequence) return;
        await RefreshReaderWholeSearchAsync(ReaderTocSearchBox.Text?.Trim() ?? string.Empty, sequence);
    }

    private async Task RefreshReaderWholeSearchAsync(string query, int? sequence = null)
    {
        if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
        _readerWholeSearchCancellation?.Cancel();
        using var searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerWholeSearchCancellation = searchCancellation;
        var searchToken = searchCancellation.Token;
        if (query.Length == 0)
        {
            ClearReaderSearchResultSelection();
            ReaderSearchResults.Clear();
            ReaderWholeSearchCountText.Text = string.Empty;
            ShowReaderSearchStatus(T("输入关键词，实时搜索整本书。"));
            if (ReferenceEquals(_readerWholeSearchCancellation, searchCancellation))
                _readerWholeSearchCancellation = null;
            return;
        }

        ReaderSearchResults.Clear();
        ClearReaderSearchResultSelection();
        ReaderWholeSearchCountText.Text = string.Empty;
        ShowReaderSearchStatus(T("正在本地搜索…"));
        try
        {
            var pendingResults = new List<ReaderSearchResultViewModel>();
            if (_readerIsPdf)
            {
                var fileId = _readerBookFile?.Id;
                await EnsureReaderPdfTextIndexAsync(searchToken);
                if (!_readerIsPdf || _readerBookFile?.Id != fileId) return;
                var pages = _readerPdfPages;
                var results = await Task.Run(() => PdfTextService.Search(pages, query, MaxWholeBookSearchResults, searchToken), searchToken);
                foreach (var result in results)
                    pendingResults.Add(new ReaderSearchResultViewModel(
                        T("第 {0} 页", result.PageNumber),
                        result.Excerpt,
                        result.PageNumber - 1,
                        $"pdf:page:{result.PageNumber}",
                        pageNumber: result.PageNumber,
                        query: query,
                        startOffset: result.MatchIndex));
            }
            else if (_readerBookCard is not null && _readerBookFile is not null && _readerDocument is not null)
            {
                var book = _readerBookCard.Book;
                var file = _readerBookFile;
                var document = _readerDocument;
                var indexToken = ReaderToken;
                var results = await Task.Run(async () =>
                {
                    await _readerWholeSearchGate.WaitAsync(searchToken);
                    try
                    {
                        await _bookContent.EnsureIndexedAsync(book, file, document, indexToken);
                        return await _readerData.SearchBookAsync(book.Id, query,
                            MaxWholeBookSearchResults, searchToken,
                            exactPhraseOnly: true, bookFileId: file.Id);
                    }
                    finally { _readerWholeSearchGate.Release(); }
                }, searchToken);
                var distinct = EpubReaderSearchPolicy
                    .FilterAndOrder(document, results)
                    .Select(result => new ReaderSearchResultViewModel(result, query))
                        .ToArray();
                foreach (var item in distinct)
                    pendingResults.Add(item);
            }
            if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
            ReaderSearchResults.Clear();
            foreach (var item in pendingResults)
                ReaderSearchResults.Add(item);
            ReaderWholeSearchCountText.Text = _readerIsPdf
                ? T("全书最多 {0} 条结果 · PDF 本地文本索引", ReaderSearchResults.Count)
                : T("全书最多 {0} 段结果", ReaderSearchResults.Count);
            ShowReaderSearchStatus(
                ReaderSearchResults.Count == 0
                    ? (_readerIsPdf ? T("没有找到匹配的内容。") : T("没有找到匹配的片段。"))
                    : null);
        }
        catch (OperationCanceledException) when (searchToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
            ReaderWholeSearchCountText.Text = string.Empty;
            ShowReaderSearchStatus(T("搜索失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            if (ReferenceEquals(_readerWholeSearchCancellation, searchCancellation))
                _readerWholeSearchCancellation = null;
        }
    }

    private void ClearReaderSearchResultSelection()
    {
        if (_selectedReaderSearchResult is not null)
            _selectedReaderSearchResult.IsSelected = false;
        _selectedReaderSearchResult = null;
    }

    private async void ReaderSearchResultButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OpenReaderSearchResultAsync(sender);
        }
        catch (OperationCanceledException)
        {
            // Closing the reader or starting another jump cancels the pending
            // navigation; the newer UI state already reflects that.
        }
        catch (Exception exception)
        {
            // async void event handlers surface exceptions on the UI
            // dispatcher, which can terminate the whole application. A failed
            // search jump must stay a local status message.
            ShowReaderSearchStatus(T("跳转失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private async Task OpenReaderSearchResultAsync(object? sender)
    {
        if (sender is not Button { Tag: ReaderSearchResultViewModel result }) return;
        if (!ReferenceEquals(_selectedReaderSearchResult, result))
        {
            if (_selectedReaderSearchResult is not null)
                _selectedReaderSearchResult.IsSelected = false;
            _selectedReaderSearchResult = result;
            result.IsSelected = true;
        }
        if (result.PageNumber is { } page)
        {
            await NavigatePdfPageAsync(page, ReaderToken);
            if (CurrentReaderHost is IPdfNavigationHost pdf && pdf.PageNumber == page
                && pdf is IPdfSearchHost search)
            {
                var match = search.Find(result.Query ?? string.Empty, result.StartOffset);
                _readerSearchCount = match.Count;
                _readerSearchIndex = match.Index;
                UpdateReaderSearchCount();
            }
            return;
        }
        if (result.Source is { } source)
        {
            var chunkNavigated = await NavigateToReaderChunkAsync(source, result.Query ?? string.Empty);
            // The chunk jump positions the page by offset alone. In dense
            // vertical body text the keyword itself carries no mark unless the
            // in-page highlights are applied, which reads exactly like a jump
            // that never happened.
            if (chunkNavigated && !string.IsNullOrWhiteSpace(result.Query))
            {
                var sequence = ++_readerSearchSequence;
                await ApplyReaderSearchAsync(result.Query, sequence, navigate: false);
            }
            return;
        }
        if (_readerDocument is null || string.IsNullOrWhiteSpace(result.Target)) return;
        ReaderSearchStatusText.Text = T("正在跳转并定位关键词…");
        var navigated = await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(result.Title, result.Target, result.ChapterIndex),
            ReaderToken,
            ReaderNavigationIntent.Search);
        if (navigated && !string.IsNullOrWhiteSpace(result.Query))
        {
            var sequence = ++_readerSearchSequence;
            await ApplyReaderSearchAsync(result.Query, sequence);
        }
        ReaderSearchStatusText.Text = navigated
            ? T("已跳转到《{0}》相关位置。", result.Title)
            : T("搜索结果定位失败，请重试。");
    }

    private async Task<bool> NavigateToReaderChunkAsync(BookContentChunk source, string query)
    {
        if (_readerDocument is null)
        {
            ShowReaderSearchStatus(T("无法定位正文：书籍内容尚未准备完成。"));
            return false;
        }

        var targetPath = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            source.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, targetPath) || !File.Exists(targetPath))
        {
            ShowReaderSearchStatus(T("无法定位正文：对应章节文件不存在。"));
            return false;
        }

        var matchOffset = FindReaderSearchMatchOffset(source.Content, query);
        _readerPendingChunkOffset = source.StartOffset + Math.Max(0, matchOffset);
        _readerPendingSearchQuery = query;
        _readerPendingSearchContext = CreateReaderSearchContext(
            source.Content,
            matchOffset,
            query.Length);

        ReaderSearchStatusText.Text = T("正在跳转并定位关键词…");
        var navigated = await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(
                source.ChapterTitle,
                new Uri(targetPath).AbsoluteUri,
                source.ChapterIndex),
            ReaderToken,
            ReaderNavigationIntent.Search);
        ReaderSearchStatusText.Text = navigated
            ? T("已跳转到《{0}》相关位置。", source.ChapterTitle)
            : T("搜索结果定位失败，请重试。");
        return navigated;
    }

    // Mirrors the search index's whitespace handling when re-locating a
    // result: selection queries can contain newlines while indexed chunks use
    // normalized text. Try the exact raw query first, then the earliest term.
    private static int FindReaderSearchMatchOffset(string content, string query)
    {
        if (string.IsNullOrWhiteSpace(content)) return -1;
        var exact = content.IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase);
        if (exact >= 0) return exact;

        var normalizedQuery = Regex.Replace(query.Trim(), @"\s+", " ").Trim();
        var earliest = -1;
        foreach (var run in normalizedQuery.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = content.IndexOf(run, StringComparison.CurrentCultureIgnoreCase);
            if (index >= 0 && (earliest < 0 || index < earliest)) earliest = index;
        }
        return earliest;
    }

    private static string CreateReaderSearchContext(
        string content,
        int matchOffset,
        int matchLength)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var safeMatch = Math.Clamp(matchOffset, 0, Math.Max(0, content.Length - 1));
        var paragraphStart = content.LastIndexOf('\n', safeMatch);
        paragraphStart = paragraphStart < 0 ? 0 : paragraphStart + 1;
        var paragraphEnd = content.IndexOf(
            '\n',
            Math.Min(content.Length, safeMatch + Math.Max(1, matchLength)));
        if (paragraphEnd < 0) paragraphEnd = content.Length;

        while (paragraphStart < paragraphEnd && char.IsWhiteSpace(content[paragraphStart])) paragraphStart++;
        while (paragraphEnd > paragraphStart && char.IsWhiteSpace(content[paragraphEnd - 1])) paragraphEnd--;
        const int maximumContextLength = 420;
        if (paragraphEnd - paragraphStart <= maximumContextLength)
            return content[paragraphStart..paragraphEnd];

        var contextStart = Math.Max(paragraphStart, safeMatch - 150);
        var contextEnd = Math.Min(paragraphEnd, contextStart + maximumContextLength);
        contextStart = Math.Max(paragraphStart, contextEnd - maximumContextLength);
        return content[contextStart..contextEnd];
    }

    private async void ReaderInPageSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        try
        {
            var sequence = _readerIsPdf
                ? ++_readerPdfSearchSequence
                : ++_readerSearchSequence;
            await Task.Delay(120);
            if (_readerIsPdf
                ? sequence != _readerPdfSearchSequence
                : sequence != _readerSearchSequence)
                return;
            var query = ReaderInPageSearchBox.Text?.Trim() ?? string.Empty;
            if (_readerIsPdf)
                await ApplyReaderPdfSearchAsync(query, sequence);
            else
                await ApplyReaderSearchAsync(query, sequence);
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0
                ? "0/0"
                : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch
        {
            _readerSearchCount = 0;
            _readerSearchIndex = -1;
            UpdateReaderSearchCount();
        }
    }

    private async void ReaderInPageSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                await ReaderInPageSearchCloseAsync();
            }
            else if (e.Key == Key.Enter
                || (e.Key == Key.G
                    && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0
                    && (e.KeyModifiers & KeyModifiers.Alt) == 0))
            {
                e.Handled = true;
                await StepReaderInPageSearchAsync((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            }
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task StepReaderInPageSearchAsync(int step)
    {
        await NavigateReaderSearchAsync(_readerSearchIndex + step);
        ReaderInPageSearchCountText.Text = _readerSearchCount <= 0
            ? "0/0"
            : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
    }

    private async void ReaderInPageSearchPreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateReaderSearchAsync(_readerSearchIndex - 1);
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
        catch { }
    }

    private async void ReaderInPageSearchNextButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateReaderSearchAsync(_readerSearchIndex + 1);
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
        catch { }
    }

    private async void ReaderInPageSearchCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        try { await ReaderInPageSearchCloseAsync(); }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested) { }
        catch { }
    }

    private async Task ReaderInPageSearchCloseAsync()
    {
        await ClearReaderSearchAsync();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderInPageSearchBox.Text = string.Empty;
    }

    private Task ApplyReaderPdfSearchAsync(string query, int sequence)
    {
        if (sequence != _readerPdfSearchSequence || CurrentReaderHost is not IPdfSearchHost pdf) return Task.CompletedTask;
        var result = pdf.Find(query);
        _readerSearchCount = result.Count;
        _readerSearchIndex = result.Index;
        UpdateReaderSearchCount();
        return Task.CompletedTask;
    }
}
