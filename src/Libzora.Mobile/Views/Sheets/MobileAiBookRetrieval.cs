using System.Text;
using System.Text.RegularExpressions;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Mobile.Views.Sheets;

public sealed record MobileAiSource(
    string Id,
    string Label,
    string Content,
    bool IsPdf,
    int ChapterIndex,
    int Offset,
    int PdfPage);

public sealed record MobileAiRetrievalResult(
    string Text,
    string Status,
    IReadOnlyList<MobileAiSource> Sources);

/// <summary>Bounded, local keyword retrieval and overview sampling for the file selected by the mobile reader.</summary>
public sealed class MobileAiBookRetrieval(ApplicationModules modules, Book book)
{
    private const int MaxPdfPages = 10_000;
    private const long MaxPdfCharacters = 20_000_000;
    private const int MaxPdfHits = 8;
    private const int PdfExcerptLength = 700;

    public async Task<(string Text, string Status)> RetrieveAsync(string query, CancellationToken token)
    {
        var result = await RetrieveContextAsync(query, token).ConfigureAwait(false);
        return (result.Text, result.Status);
    }

    public async Task<MobileAiRetrievalResult> RetrieveContextAsync(string query, CancellationToken token)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return new(string.Empty, UiText.Get("数据恢复期间无法检索书籍。"), []);
        var file = SelectReadableFile();
        if (file is null) return new(string.Empty, UiText.Get("没有可检索的书籍文件，仅使用当前页。"), []);

        var path = modules.Library.Books.GetAbsoluteFilePath(file);
        if (!File.Exists(path)) return new(string.Empty, UiText.Get("书籍文件不在本机，仅使用当前页。"), []);
        var format = file.Format.Trim().TrimStart('.').ToLowerInvariant();
        if (format == "pdf")
            return await Task.Run(() => SearchPdf(path, query, token), token).ConfigureAwait(false);

        var document = await PrepareEpubDocumentAsync(file, path, format, token).ConfigureAwait(false);
        await modules.Reader.Content.EnsureIndexedAsync(book, file, document, token).ConfigureAwait(false);
        // SQLite's async operations may run synchronously, including LIKE fallback.
        return await Task.Run(async () =>
        {
            var hits = await modules.Reader.Data.SearchBookAsync(book.Id, query, 8, token, bookFileId: file.Id)
                .ConfigureAwait(false);
            if (hits.Count == 0) return new MobileAiRetrievalResult(string.Empty, UiText.Get("全书关键词检索无匹配，仅使用当前页。"), []);
            var results = hits.Select((chunk, index) => new ReaderRetrievalResult(chunk, 1d - index * 0.01d)).ToArray();
            var context = await modules.Ai.Context.BuildAsync(book.Id, results, 4500, 1, token).ConfigureAwait(false);
            if (context.Sources.Count == 0)
                return new MobileAiRetrievalResult(string.Empty, UiText.Get("全书关键词检索无可用正文，仅使用当前页。"), []);
            var sources = MapSources(context.Sources);
            return new MobileAiRetrievalResult(context.Text, UiText.Get("全书关键词检索 · {0} 个原文片段", sources.Length), sources);
        }, token).ConfigureAwait(false);
    }

    public async Task<MobileAiRetrievalResult> BuildChapterOverviewAsync(
        int chapterIndex,
        int currentPdfPage,
        string currentPageText,
        CancellationToken token)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return new(string.Empty, UiText.Get("数据恢复期间无法读取章节内容。"), []);
        var file = SelectReadableFile();
        if (file is null) return new(string.Empty, UiText.Get("没有可读取的书籍文件。"), []);

        var path = modules.Library.Books.GetAbsoluteFilePath(file);
        if (!File.Exists(path)) return new(string.Empty, UiText.Get("书籍文件不在本机。"), []);
        var format = file.Format.Trim().TrimStart('.').ToLowerInvariant();
        if (format == "pdf")
        {
            if (string.IsNullOrWhiteSpace(currentPageText))
                return new(string.Empty, UiText.Get("当前 PDF 页面没有可提取的文字；扫描版需先进行文字识别。"), []);
            var label = UiText.Get("PDF 第 {0} 页", Math.Max(1, currentPdfPage));
            var source = new MobileAiSource("S1", label, currentPageText, true, 0, 0, Math.Max(1, currentPdfPage));
            return new($"[S1] location: page={source.PdfPage}\ncontent:\n{currentPageText}",
                UiText.Get("仅当前 PDF 第 {0} 页", source.PdfPage), [source]);
        }

        var document = await PrepareEpubDocumentAsync(file, path, format, token).ConfigureAwait(false);
        await modules.Reader.Content.EnsureIndexedAsync(book, file, document, token).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var chunks = await modules.Reader.Data.GetBookChapterChunksAsync(
                file.Id, chapterIndex, limit: 256, cancellationToken: token).ConfigureAwait(false);
            var overview = ReaderAiContextBuilder.BuildOverview(chunks);
            if (overview.Sources.Count == 0)
                return new MobileAiRetrievalResult(string.Empty, UiText.Get("当前章节没有可用的正文内容。"), []);
            var sources = MapSources(overview.Sources);
            return new MobileAiRetrievalResult(overview.Text, UiText.Get("当前章节 · {0} 个原文片段", sources.Length), sources);
        }, token).ConfigureAwait(false);
    }

    public async Task<MobileAiRetrievalResult> BuildBookOverviewAsync(CancellationToken token)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return new(string.Empty, UiText.Get("数据恢复期间无法读取书籍内容。"), []);
        var file = SelectReadableFile();
        if (file is null) return new(string.Empty, UiText.Get("没有可读取的书籍文件。"), []);

        var path = modules.Library.Books.GetAbsoluteFilePath(file);
        if (!File.Exists(path)) return new(string.Empty, UiText.Get("书籍文件不在本机。"), []);
        var format = file.Format.Trim().TrimStart('.').ToLowerInvariant();
        if (format == "pdf")
            return await Task.Run(() => SamplePdfOverview(path, token), token).ConfigureAwait(false);

        var document = await PrepareEpubDocumentAsync(file, path, format, token).ConfigureAwait(false);
        await modules.Reader.Content.EnsureIndexedAsync(book, file, document, token).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var chunks = await modules.Reader.Data.GetBookOverviewChunksAsync(
                book.Id, 64, token, file.Id).ConfigureAwait(false);
            var overview = ReaderAiContextBuilder.BuildOverview(chunks);
            if (overview.Sources.Count == 0)
                return new MobileAiRetrievalResult(string.Empty, UiText.Get("书籍没有可用的正文内容。"), []);
            var sources = MapSources(overview.Sources);
            return new MobileAiRetrievalResult(overview.Text, UiText.Get("全书概览 · 抽样 {0} 个原文片段", sources.Length), sources);
        }, token).ConfigureAwait(false);
    }

    private BookFile? SelectReadableFile() =>
        // Keep this order in sync with ReaderPage.ChooseReadableFile.
        ReaderPage.ReadableFormats
            .Select(format => book.Files.FirstOrDefault(candidate =>
                candidate.Format.Trim().TrimStart('.').Equals(format, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(candidate => candidate is not null);

    private async Task<EpubReaderDocument> PrepareEpubDocumentAsync(
        BookFile file, string path, string format, CancellationToken token)
    {
        if (format is "azw3" or "mobi" or "txt")
        {
            var sourceHash = string.IsNullOrWhiteSpace(file.Sha256)
                ? await Hashing.Sha256Async(path, token).ConfigureAwait(false) : file.Sha256;
            var converted = await modules.Reader.FormatCache.PrepareEpubAsync(path, sourceHash, format, token)
                .ConfigureAwait(false);
            path = converted.EpubPath;
        }
        var hash = string.IsNullOrWhiteSpace(file.Sha256)
            ? await Hashing.Sha256Async(path, token).ConfigureAwait(false) : file.Sha256;
        return await modules.Reader.Preparation.PrepareAsync(path, hash, token).ConfigureAwait(false);
    }

    private static MobileAiSource[] MapSources(IReadOnlyList<ReaderAiSource> sources) =>
        sources.Select(source =>
        {
            var title = string.IsNullOrWhiteSpace(source.Chunk.ChapterTitle)
                ? UiText.Get("第 {0} 章", source.Chunk.ChapterIndex + 1)
                : source.Chunk.ChapterTitle;
            return new MobileAiSource(
                source.Id,
                title,
                source.Content,
                IsPdf: false,
                source.Chunk.ChapterIndex,
                source.Chunk.StartOffset,
                PdfPage: 0);
        }).ToArray();

    private static MobileAiRetrievalResult SamplePdfOverview(string path, CancellationToken token)
    {
        using var document = new PdfiumDocumentFactory().Open(path);
        var nonEmpty = new List<(int Page, string Text)>();
        var scanned = Math.Min(document.PageCount, MaxPdfPages);
        var characters = 0L;
        for (var page = 1; page <= scanned && characters < MaxPdfCharacters; page++)
        {
            token.ThrowIfCancellationRequested();
            var text = document.ReadPageText(page, token).ReplaceLineEndings(" ").Trim();
            characters += text.Length;
            if (!string.IsNullOrWhiteSpace(text))
                nonEmpty.Add((page, text));
        }
        if (nonEmpty.Count == 0)
            return new(string.Empty, UiText.Get("PDF 没有可提取的文字；扫描版需先进行文字识别。"), []);

        var sampled = ReaderAiContextBuilder.SampleEvenly(nonEmpty, 16);
        var allowance = Math.Max(160, 5000 / Math.Max(1, sampled.Count));
        var output = new StringBuilder()
            .AppendLine($"PDF 快速概览：抽样覆盖 {sampled.Count}/{nonEmpty.Count} 个有文本的页面。请明确说明这是抽样，不是全文总结。");
        var sources = new List<MobileAiSource>(sampled.Count);
        foreach (var item in sampled)
        {
            var id = $"S{sources.Count + 1}";
            var excerpt = item.Text.Length > allowance ? item.Text[..allowance] + "…" : item.Text;
            sources.Add(new MobileAiSource(id, UiText.Get("PDF 第 {0} 页", item.Page), excerpt, true, 0, 0, item.Page));
            output.AppendLine($"[{id}] location: page={item.Page}")
                .AppendLine("content:")
                .AppendLine(excerpt)
                .AppendLine();
        }
        return new(output.ToString(), UiText.Get("PDF 快速概览 · 抽样 {0} 页", sources.Count), sources);
    }

    private static MobileAiRetrievalResult SearchPdf(string path, string query, CancellationToken token)
    {
        var terms = GetPdfTerms(query);
        if (terms.Count == 0) return new(string.Empty, UiText.Get("没有可用的检索词，仅使用当前页。"), []);
        using var document = new PdfiumDocumentFactory().Open(path);
        var hits = new List<(int Page, int Score, string Text)>();
        var characters = 0L;
        var scanned = 0;
        for (var page = 1; page <= document.PageCount && page <= MaxPdfPages && characters < MaxPdfCharacters; page++)
        {
            token.ThrowIfCancellationRequested();
            var text = document.ReadPageText(page, token);
            scanned++;
            characters += text.Length;
            var bestIndex = -1;
            var score = 0;
            foreach (var term in terms)
            {
                var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                score += Math.Min(term.Length, 8);
                if (bestIndex < 0) bestIndex = index;
            }
            if (bestIndex < 0) continue;
            var start = Math.Max(0, bestIndex - PdfExcerptLength / 3);
            var length = Math.Min(PdfExcerptLength, text.Length - start);
            hits.Add((page, score, text.Substring(start, length).ReplaceLineEndings(" ").Trim()));
            if (hits.Count > MaxPdfHits)
                hits.Remove(hits.OrderBy(hit => hit.Score).ThenByDescending(hit => hit.Page).First());
        }
        var coverage = scanned < document.PageCount ? UiText.Get("（已检索 {0}/{1} 页）", scanned, document.PageCount) : string.Empty;
        if (hits.Count == 0) return new(string.Empty, UiText.Get("PDF 关键词检索无匹配{0}，仅使用当前页。", coverage), []);
        var ordered = hits.OrderBy(hit => hit.Page).ToArray();
        var output = new StringBuilder();
        var sources = new List<MobileAiSource>(ordered.Length);
        foreach (var hit in ordered)
        {
            var id = $"S{sources.Count + 1}";
            sources.Add(new MobileAiSource(id, UiText.Get("PDF 第 {0} 页", hit.Page), hit.Text, true, 0, 0, hit.Page));
            output.AppendLine($"[{id}] location: page={hit.Page}")
                .AppendLine("content:")
                .AppendLine(hit.Text)
                .AppendLine();
        }
        return new(output.ToString(), UiText.Get("PDF 关键词检索 · {0} 页{1}", sources.Count, coverage), sources);
    }

    private static IReadOnlyList<string> GetPdfTerms(string query)
    {
        if (query.Length > 300) query = query[..300];
        foreach (var phrase in new[] { "请根据", "请帮我", "这本书", "本书", "这一章", "本章", "当前章节", "当前", "如何", "怎么", "什么是", "为什么", "哪些", "是否", "请", "帮我", "一下", "总结", "概括", "解释", "分析", "介绍", "关于", "根据" })
            query = query.Replace(phrase, string.Empty, StringComparison.OrdinalIgnoreCase);
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(query, @"[A-Za-z0-9_\-]{3,}|[㐀-鿿]{2,}"))
        {
            var value = match.Value;
            if (value[0] < '㐀') terms.Add(value);
            else
            {
                if (value.Length >= 3) terms.Add(value.Length <= 18 ? value : value[..18]);
                for (var size = Math.Min(6, value.Length); size >= 3; size--)
                    for (var start = 0; start + size <= value.Length; start += Math.Max(1, size - 2))
                        terms.Add(value.Substring(start, size));
            }
        }
        if (terms.Count == 0 && !string.IsNullOrWhiteSpace(query)) terms.Add(query.Trim());
        return terms.OrderByDescending(term => term.Length).Take(24).ToArray();
    }
}
