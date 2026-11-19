using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Libzora.Core;

namespace Libzora.Infrastructure.Plugins;

/// <summary>
/// Chapter text, files, annotations and bookmarks.
/// </summary>
public sealed partial class PluginBridge : IDisposable
{
    public const int MaximumChapterSlice = 1_000_000;
    public const int MaximumFileChunk = 8 * 1024 * 1024;

    /// <summary>
    /// Every method the page API offers in this version. Plugins feature-test
    /// with <c>libzora.capabilities.methods.includes(...)</c> instead of
    /// comparing app versions.
    /// </summary>
    public static readonly IReadOnlyList<string> Methods =
    [
        "library.getBooks", "library.search", "library.getBook", "library.getCollections",
        "library.createCollection", "library.addToCollection", "library.removeFromCollection",
        "library.importFiles", "library.updateBook", "library.getCover", "library.setCover",
        "library.importDownload", "library.translateBook", "library.annotatePinyin", "library.cancelTask",
        "library.readFile", "pinyin.status", "pinyin.downloadModel", "http.fetch", "network.requestAccess",
        "secrets.get", "secrets.set", "secrets.remove", "storage.get", "storage.set", "storage.remove",
        "ui.close", "ui.notify", "ui.setTitle", "ui.openExternal", "ui.log", "ui.pickFiles", "ui.saveFile",
        "ui.copyText", "ui.readText", "reading.getProgress", "reading.getAnnotations", "reading.getBookmarks",
        "reading.getStats", "reading.addAnnotation", "reading.addBookmark", "book.getToc",
        "book.getChapterText", "ai.status", "ai.complete", "reader.open"
    ];

    private async Task<JsonNode?> GetTocAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var content = RequireContent();
        var (book, fileId) = await ResolveAccessibleBookAsync(parameters, cancellationToken);
        var outline = await content.GetOutlineAsync(book, fileId, cancellationToken);
        return new JsonObject
        {
            ["bookId"] = outline.BookId.ToString("D"),
            ["fileId"] = outline.FileId.ToString("D"),
            ["format"] = outline.Format,
            ["chapters"] = new JsonArray(outline.Chapters.Select(chapter => (JsonNode)new JsonObject
            {
                ["index"] = chapter.Index,
                ["title"] = chapter.Title,
                ["path"] = chapter.Path,
                ["length"] = chapter.Length
            }).ToArray()),
            ["toc"] = new JsonArray(outline.Toc.Select(item => (JsonNode)new JsonObject
            {
                ["title"] = item.Title,
                ["chapterIndex"] = item.ChapterIndex,
                ["level"] = item.Level,
                ["fragment"] = item.Fragment
            }).ToArray())
        };
    }

    private async Task<JsonNode?> GetChapterTextAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var content = RequireContent();
        var (book, fileId) = await ResolveAccessibleBookAsync(parameters, cancellationToken);
        var slice = await content.GetChapterTextAsync(
            book,
            fileId,
            OptionalInt(parameters, "chapterIndex", 0, 0, 100_000),
            OptionalInt(parameters, "offset", 0, 0, int.MaxValue),
            OptionalInt(parameters, "maxLength", 200_000, 1, MaximumChapterSlice),
            cancellationToken);
        return new JsonObject
        {
            ["chapterIndex"] = slice.ChapterIndex,
            ["title"] = slice.Title,
            ["path"] = slice.Path,
            ["text"] = slice.Text,
            ["offset"] = slice.Offset,
            ["length"] = slice.Length,
            ["hasMore"] = slice.HasMore
        };
    }

    private async Task<JsonNode?> ReadBookFileAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!_plugin.Manifest.CanReadBookFiles)
            throw new UnauthorizedAccessException("插件没有读取书籍文件的权限。");
        var content = _content ?? throw new InvalidOperationException("当前版本不支持读取书籍文件。");
        var (book, fileId) = await ResolveAccessibleBookAsync(parameters, cancellationToken);
        var offset = parameters.TryGetProperty("offset", out var offsetElement) && offsetElement.TryGetInt64(out var value)
            ? Math.Max(0, value)
            : 0;
        var chunk = await content.ReadFileAsync(
            book,
            fileId,
            offset,
            OptionalInt(parameters, "length", 4 * 1024 * 1024, 1, MaximumFileChunk),
            cancellationToken);
        return new JsonObject
        {
            ["fileId"] = chunk.FileId.ToString("D"),
            ["name"] = chunk.Name,
            ["format"] = chunk.Format,
            ["size"] = chunk.Size,
            ["offset"] = chunk.Offset,
            ["data"] = Convert.ToBase64String(chunk.Data),
            ["hasMore"] = chunk.HasMore
        };
    }

    // ---- reading data writes (permissions.readingWrite) ----------------

    private async Task<JsonNode?> AddAnnotationAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireReadingWrite();
        var content = RequireContentHost();
        var reader = _readerData ?? throw new InvalidOperationException("阅读数据服务不可用。");
        var (book, fileId) = await ResolveAccessibleBookAsync(parameters, cancellationToken);
        var chapterIndex = OptionalInt(parameters, "chapterIndex", -1, -1, 100_000);
        if (chapterIndex < 0) throw new ArgumentException("缺少参数“chapterIndex”。");
        var start = OptionalInt(parameters, "startOffset", -1, -1, int.MaxValue);
        var end = OptionalInt(parameters, "endOffset", -1, -1, int.MaxValue);
        if (start < 0 || end <= start)
            throw new ArgumentException("startOffset 和 endOffset 无效，结束位置必须大于开始位置。");
        if (end - start > MaximumTextLength)
            throw new ArgumentException("划线文字过长。");

        var outline = await content.GetOutlineAsync(book, fileId, cancellationToken);
        var chapter = await content.GetChapterTextAsync(book, outline.FileId, chapterIndex, 0, MaximumChapterSlice, cancellationToken);
        if (end > chapter.Length)
            throw new ArgumentException("划线位置超出章节长度。");
        var text = chapter.Text;
        var color = OptionalString(parameters, "color") ?? "#000000";
        if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            throw new ArgumentException("color 必须是 #RRGGBB 形式。");
        var style = (OptionalString(parameters, "style") ?? "solid").ToLowerInvariant();
        if (style is not ("solid" or "wavy" or "highlight" or "dashed"))
            throw new ArgumentException("style 只能是 solid、wavy、dashed 或 highlight。");

        var now = DateTimeOffset.UtcNow;
        var annotation = new ReaderAnnotation
        {
            BookId = book.Id,
            BookFileId = outline.FileId,
            ChapterPath = chapter.Path,
            StartOffset = start,
            EndOffset = end,
            SelectedText = text[start..end],
            Prefix = text[Math.Max(0, start - 32)..start],
            Suffix = text[end..Math.Min(text.Length, end + 32)],
            Color = color.ToUpperInvariant(),
            UnderlineStyle = style,
            Note = Truncate(OptionalString(parameters, "note") ?? string.Empty, MaximumTextLength),
            CreatedAt = now,
            UpdatedAt = now
        };
        await reader.SaveAnnotationAsync(annotation, cancellationToken);
        return new JsonObject
        {
            ["id"] = annotation.Id.ToString("D"),
            ["bookId"] = annotation.BookId.ToString("D"),
            ["fileId"] = annotation.BookFileId.ToString("D"),
            ["chapterPath"] = annotation.ChapterPath,
            ["startOffset"] = annotation.StartOffset,
            ["endOffset"] = annotation.EndOffset,
            ["selectedText"] = annotation.SelectedText,
            ["note"] = annotation.Note,
            ["color"] = annotation.Color,
            ["underlineStyle"] = annotation.UnderlineStyle
        };
    }

    private async Task<JsonNode?> AddBookmarkAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireReadingWrite();
        var content = RequireContentHost();
        var reader = _readerData ?? throw new InvalidOperationException("阅读数据服务不可用。");
        var (book, fileId) = await ResolveAccessibleBookAsync(parameters, cancellationToken);
        var chapterIndex = OptionalInt(parameters, "chapterIndex", -1, -1, 100_000);
        if (chapterIndex < 0) throw new ArgumentException("缺少参数“chapterIndex”。");
        var offset = OptionalInt(parameters, "offset", 0, 0, int.MaxValue);
        var outline = await content.GetOutlineAsync(book, fileId, cancellationToken);
        var chapter = await content.GetChapterTextAsync(book, outline.FileId, chapterIndex, offset, 120, cancellationToken);
        var bookmark = new ReaderBookmark
        {
            BookId = book.Id,
            BookFileId = outline.FileId,
            ChapterPath = chapter.Path,
            ChapterIndex = chapterIndex,
            ContentPosition = new ReaderContentPosition { TextOffset = chapter.Offset },
            Title = Truncate(OptionalString(parameters, "title") ?? chapter.Title, 200),
            Quote = Truncate(OptionalString(parameters, "quote") ?? chapter.Text.Trim(), 500),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await reader.SaveBookmarkAsync(bookmark, cancellationToken);
        return new JsonObject
        {
            ["id"] = bookmark.Id.ToString("D"),
            ["bookId"] = bookmark.BookId.ToString("D"),
            ["fileId"] = bookmark.BookFileId.ToString("D"),
            ["chapterPath"] = bookmark.ChapterPath,
            ["chapterIndex"] = bookmark.ChapterIndex,
            ["title"] = bookmark.Title,
            ["quote"] = bookmark.Quote
        };
    }

    // ---- AI (permissions.ai) --------------------------------------------
}
