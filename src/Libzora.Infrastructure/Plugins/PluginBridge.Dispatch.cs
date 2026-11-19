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
/// Method dispatch and library queries.
/// </summary>
public sealed partial class PluginBridge : IDisposable
{
    public async Task<(long Id, string Reply)?> HandleAsync(string? message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        long id;
        string method;
        JsonElement parameters;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out id)) return null;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String) return null;
            method = methodElement.GetString()!;
            parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = await DispatchAsync(method, parameters, cancellationToken);
            return (id, new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString(JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SoftwareLog.ErrorWithoutMessage("plugin.call", exception);
            var text = exception is OperationCanceledException or TaskCanceledException
                ? "请求超时或已取消。"
                : exception.Message;
            return (id, new JsonObject { ["ok"] = false, ["error"] = text }.ToJsonString(JsonOptions));
        }
    }

    private Task<JsonNode?> DispatchAsync(string method, JsonElement parameters, CancellationToken cancellationToken) => method switch
    {
        "library.getBooks" => GetBooksAsync(parameters, cancellationToken),
        "library.search" => SearchLibraryAsync(parameters, cancellationToken),
        "library.getBook" => GetLibraryBookAsync(parameters, cancellationToken),
        "library.getCollections" => GetCollectionsAsync(cancellationToken),
        "library.createCollection" => CreateCollectionAsync(parameters, cancellationToken),
        "library.addToCollection" => ChangeCollectionMembershipAsync(parameters, add: true, cancellationToken),
        "library.removeFromCollection" => ChangeCollectionMembershipAsync(parameters, add: false, cancellationToken),
        "library.importFiles" => ImportPickedFilesAsync(parameters, cancellationToken),
        "library.updateBook" => UpdateBookAsync(parameters, cancellationToken),
        "library.getCover" => GetCoverAsync(parameters, cancellationToken),
        "library.setCover" => SetCoverAsync(parameters, cancellationToken),
        "library.importDownload" => ImportDownloadAsync(parameters, cancellationToken),
        "library.translateBook" => TranslateBookAsync(parameters, cancellationToken),
        "library.annotatePinyin" => AnnotatePinyinAsync(parameters, cancellationToken),
        "library.cancelTask" => CancelTaskAsync(),
        "pinyin.status" => PinyinStatusAsync(),
        "pinyin.downloadModel" => DownloadPinyinModelAsync(cancellationToken),
        "http.fetch" => FetchAsync(parameters, cancellationToken),
        "network.requestAccess" => RequestNetworkAccessAsync(parameters, cancellationToken),
        "secrets.get" => SecretGetAsync(parameters, cancellationToken),
        "secrets.set" => SecretSetAsync(parameters, cancellationToken),
        "secrets.remove" => SecretRemoveAsync(parameters, cancellationToken),
        "storage.get" => StorageGetAsync(parameters, cancellationToken),
        "storage.set" => StorageSetAsync(parameters, cancellationToken),
        "storage.remove" => StorageRemoveAsync(parameters, cancellationToken),
        "ui.close" => Ui(() => _ui.Close()),
        "ui.notify" => Ui(() => _ui.Notify(Truncate(RequiredString(parameters, "message"), 500))),
        "ui.setTitle" => Ui(() => _ui.SetTitle(Truncate(RequiredString(parameters, "title"), 120))),
        "ui.openExternal" => Ui(() => _ui.OpenExternal(ParseExternalUri(RequiredString(parameters, "url")))),
        "ui.log" => Ui(() => _ui.Log(
            Truncate(OptionalString(parameters, "level") ?? "log", 20),
            Truncate(OptionalString(parameters, "message") ?? string.Empty, 4000))),
        "ui.pickFiles" => PickFilesAsync(parameters, cancellationToken),
        "ui.saveFile" => SaveFileAsync(parameters, cancellationToken),
        "ui.copyText" => CopyTextAsync(parameters, cancellationToken),
        "ui.readText" => ReadTextAsync(cancellationToken),
        "reading.getProgress" => GetReadingProgressAsync(parameters, cancellationToken),
        "reading.getAnnotations" => GetReadingAnnotationsAsync(parameters, cancellationToken),
        "reading.getBookmarks" => GetReadingBookmarksAsync(parameters, cancellationToken),
        "reading.getStats" => GetReadingStatsAsync(parameters, cancellationToken),
        "reading.addAnnotation" => AddAnnotationAsync(parameters, cancellationToken),
        "reading.addBookmark" => AddBookmarkAsync(parameters, cancellationToken),
        "book.getToc" => GetTocAsync(parameters, cancellationToken),
        "book.getChapterText" => GetChapterTextAsync(parameters, cancellationToken),
        "library.readFile" => ReadBookFileAsync(parameters, cancellationToken),
        "ai.status" => AiStatusAsync(cancellationToken),
        "ai.complete" => AiCompleteAsync(parameters, cancellationToken),
        "reader.open" => OpenInReaderAsync(parameters, cancellationToken),
        _ => throw new InvalidOperationException($"未知的插件接口“{method}”。")
    };

    // ---- library -------------------------------------------------------

    private async Task<JsonNode?> GetBooksAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Read);
        var ids = new List<Guid>();
        if (parameters.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in idsElement.EnumerateArray())
                ids.Add(ParseAllowedBookId(element.GetString()));
        }
        else
        {
            ids.AddRange(_context.BookIds);
        }

        var books = new JsonArray();
        foreach (var id in ids.Distinct())
        {
            var book = await _library.GetBookAsync(id, cancellationToken);
            if (book is not null) books.Add(ToJson(book));
        }
        return books;
    }

    private async Task<JsonNode?> SearchLibraryAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var pageIndex = OptionalInt(parameters, "pageIndex", 0, 0, 100_000);
        var pageSize = OptionalInt(parameters, "pageSize", 50, 1, 200);
        Guid? collectionId = null;
        if (OptionalString(parameters, "collectionId") is { Length: > 0 } collectionValue)
            collectionId = ParseGuid(collectionValue, "collectionId");
        var favorite = parameters.TryGetProperty("favorite", out var favoriteElement)
            && favoriteElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? favoriteElement.GetBoolean()
                : (bool?)null;
        var page = await _library.SearchPageAsync(
            OptionalString(parameters, "query"),
            EmptyToNull(OptionalString(parameters, "author")),
            EmptyToNull(OptionalString(parameters, "tag")),
            EmptyToNull(OptionalString(parameters, "format")),
            EmptyToNull(OptionalString(parameters, "category")),
            collectionId,
            OptionalReadingStatus(parameters),
            favorite,
            OptionalSortMode(parameters),
            pageIndex,
            pageSize,
            cancellationToken);
        return new JsonObject
        {
            ["books"] = new JsonArray(page.Books.Select(book => (JsonNode)ToJson(book)).ToArray()),
            ["totalCount"] = page.TotalCount,
            ["pageIndex"] = pageIndex,
            ["pageSize"] = pageSize
        };
    }

    private async Task<JsonNode?> GetLibraryBookAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var id = ParseGuid(RequiredString(parameters, "id"), "id");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        return ToJson(book);
    }

    private async Task<JsonNode?> GetCollectionsAsync(CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var summaries = await _library.GetCollectionSummariesAsync(cancellationToken);
        return new JsonArray(summaries.Select(summary => (JsonNode)new JsonObject
        {
            ["id"] = summary.Collection.Id.ToString("D"),
            ["name"] = summary.Collection.Name,
            ["bookCount"] = summary.BookCount
        }).ToArray());
    }

    private async Task<JsonNode?> CreateCollectionAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        RequireLibrary(PluginLibraryAccess.Write);
        var name = RequiredString(parameters, "name").Trim();
        if (name.Length > 60)
            throw new ArgumentException("分组名称不能超过 60 个字符。");
        var collection = await _library.CreateCollectionAsync(name, cancellationToken);
        return new JsonObject
        {
            ["id"] = collection.Id.ToString("D"),
            ["name"] = collection.Name,
            ["bookCount"] = 0
        };
    }

    private async Task<JsonNode?> ChangeCollectionMembershipAsync(
        JsonElement parameters,
        bool add,
        CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        RequireLibrary(PluginLibraryAccess.Write);
        var bookId = ParseGuid(RequiredString(parameters, "bookId"), "bookId");
        var collectionId = ParseGuid(RequiredString(parameters, "collectionId"), "collectionId");
        var collection = (await _library.GetCollectionsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == collectionId)
            ?? throw new ArgumentException("分组不存在或已被删除。");
        if (string.Equals(collection.Name, BookLibraryDefaults.UncollectedCollectionName, StringComparison.Ordinal))
            throw new ArgumentException("不能通过插件修改“未收藏”分组。");

        if (add)
            await _library.AddBookToCollectionAsync(bookId, collectionId, cancellationToken);
        else
            await _library.RemoveBookFromCollectionAsync(bookId, collectionId, cancellationToken);
        _ui.BooksChanged([bookId]);
        return new JsonObject
        {
            ["bookId"] = bookId.ToString("D"),
            ["collectionId"] = collectionId.ToString("D"),
            ["member"] = add
        };
    }

    private async Task<JsonNode?> ImportPickedFilesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!_plugin.Manifest.CanImportLibrary)
            throw new UnauthorizedAccessException("插件没有导入书籍的权限。");
        if (!parameters.TryGetProperty("paths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("缺少参数“paths”。");

        var paths = new List<string>();
        foreach (var element in pathsElement.EnumerateArray())
        {
            var path = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("paths 只能包含文件路径。");
            // Only files the user approved through this window's picker may be
            // imported, so a plugin cannot read arbitrary host paths.
            bool allowed;
            lock (_pickedFiles) allowed = _pickedFiles.Remove(path);
            if (!allowed)
                throw new UnauthorizedAccessException("只能导入插件通过文件选择器选中的文件。");
            paths.Add(path);
        }
        if (paths.Count == 0) return new JsonObject { ["items"] = new JsonArray() };

        var result = await _library.ImportAsync(paths, progress: null, cancellationToken);
        var items = new JsonArray();
        foreach (var item in result.Items)
        {
            items.Add(new JsonObject
            {
                ["sourcePath"] = item.SourcePath,
                ["succeeded"] = item.Succeeded,
                ["message"] = item.Message,
                ["bookId"] = item.BookId?.ToString("D"),
                ["added"] = item.Added
            });
        }
        return new JsonObject
        {
            ["items"] = items,
            ["successCount"] = result.SuccessCount,
            ["addedCount"] = result.AddedCount
        };
    }

    // ---- reading data --------------------------------------------------

    private async Task<JsonNode?> GetReadingProgressAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var files = new JsonArray();
        foreach (var file in book.Files)
        {
            var progress = await reader.GetProgressAsync(file.Id, cancellationToken);
            if (progress is null) continue;
            files.Add(new JsonObject
            {
                ["fileId"] = file.Id.ToString("D"),
                ["format"] = file.Format.ToUpperInvariant(),
                ["chapterPath"] = progress.ChapterPath,
                ["fragment"] = progress.Fragment,
                ["chapterIndex"] = progress.ChapterIndex,
                ["scrollPosition"] = progress.ScrollPosition,
                ["progressPercent"] = progress.ProgressPercent,
                ["flowMode"] = progress.FlowMode,
                ["updatedAt"] = progress.UpdatedAt,
                ["contentPosition"] = progress.ContentPosition is null
                    ? null
                    : JsonSerializer.SerializeToNode(progress.ContentPosition, JsonOptions)
            });
        }
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["title"] = book.Title,
            ["files"] = files
        };
    }

    private async Task<JsonNode?> GetReadingAnnotationsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var limit = OptionalInt(parameters, "limit", 500, 1, 2000);
        var rows = new List<ReaderAnnotation>();
        foreach (var file in book.Files)
            rows.AddRange(await reader.GetAnnotationsAsync(file.Id, cancellationToken, limit));
        var annotations = new JsonArray(rows
            .OrderByDescending(row => row.UpdatedAt)
            .Take(limit)
            .Select(row => (JsonNode)new JsonObject
            {
                ["id"] = row.Id.ToString("D"),
                ["bookId"] = row.BookId.ToString("D"),
                ["fileId"] = row.BookFileId.ToString("D"),
                ["chapterPath"] = row.ChapterPath,
                ["startOffset"] = row.StartOffset,
                ["endOffset"] = row.EndOffset,
                ["selectedText"] = Truncate(row.SelectedText, MaximumTextLength),
                ["note"] = Truncate(row.Note, MaximumTextLength),
                ["color"] = row.Color,
                ["underlineStyle"] = row.UnderlineStyle,
                ["createdAt"] = row.CreatedAt,
                ["updatedAt"] = row.UpdatedAt
            })
            .ToArray());
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["annotations"] = annotations
        };
    }

    private async Task<JsonNode?> GetReadingBookmarksAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var limit = OptionalInt(parameters, "limit", 500, 1, 2000);
        var rows = new List<ReaderBookmark>();
        foreach (var file in book.Files)
            rows.AddRange(await reader.GetBookmarksAsync(file.Id, cancellationToken, limit));
        var bookmarks = new JsonArray(rows
            .OrderByDescending(row => row.CreatedAt)
            .Take(limit)
            .Select(row => (JsonNode)new JsonObject
            {
                ["id"] = row.Id.ToString("D"),
                ["bookId"] = row.BookId.ToString("D"),
                ["fileId"] = row.BookFileId.ToString("D"),
                ["chapterPath"] = row.ChapterPath,
                ["chapterIndex"] = row.ChapterIndex,
                ["title"] = row.Title,
                ["quote"] = Truncate(row.Quote, MaximumTextLength),
                ["createdAt"] = row.CreatedAt
            })
            .ToArray());
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["bookmarks"] = bookmarks
        };
    }

    private async Task<JsonNode?> GetReadingStatsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var (book, reader) = await ResolveReadingBookAsync(parameters, cancellationToken);
        var files = new JsonArray();
        foreach (var file in book.Files)
        {
            var stats = await reader.GetReadingStatsAsync(file.Id, cancellationToken);
            if (stats is null) continue;
            files.Add(new JsonObject
            {
                ["fileId"] = file.Id.ToString("D"),
                ["format"] = file.Format.ToUpperInvariant(),
                ["cumulativeSeconds"] = stats.CumulativeSeconds,
                ["progressPercent"] = stats.ProgressPercent,
                ["completedChapters"] = stats.CompletedChapters,
                ["totalChapters"] = stats.TotalChapters,
                ["updatedAt"] = stats.UpdatedAt
            });
        }
        return new JsonObject
        {
            ["bookId"] = book.Id.ToString("D"),
            ["files"] = files
        };
    }

    private async Task<(Book Book, ReaderDataService Reader)> ResolveReadingBookAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        RequireLibraryAll();
        var reader = _readerData ?? throw new InvalidOperationException("阅读数据服务不可用。");
        var id = ParseGuid(RequiredString(parameters, "bookId"), "bookId");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        return (book, reader);
    }

    // ---- book content (permissions.content / bookFiles) ---------------
}
