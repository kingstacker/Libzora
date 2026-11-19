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
/// AI calls, host requirements and file operations.
/// </summary>
public sealed partial class PluginBridge : IDisposable
{
    public const int MaximumAiPromptLength = 200_000;

    private async Task<JsonNode?> AiStatusAsync(CancellationToken cancellationToken)
    {
        var ai = RequireAi();
        var status = await ai.GetStatusAsync(cancellationToken);
        return new JsonObject
        {
            ["available"] = status.Available,
            ["provider"] = status.Provider,
            ["model"] = status.Model,
            ["reason"] = status.Reason
        };
    }

    private async Task<JsonNode?> AiCompleteAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var ai = RequireAi();
        var prompt = RequiredString(parameters, "prompt");
        var system = OptionalString(parameters, "system");
        var history = new List<(string Role, string Content)>();
        if (parameters.TryGetProperty("history", out var historyElement) && historyElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in historyElement.EnumerateArray())
            {
                var role = OptionalString(turn, "role")?.ToLowerInvariant();
                if (role is not ("user" or "assistant"))
                    throw new ArgumentException("history 中的 role 只能是 user 或 assistant。");
                history.Add((role, OptionalString(turn, "content") ?? string.Empty));
            }
        }
        if (history.Count > 50)
            throw new ArgumentException("history 最多 50 条。");
        var total = prompt.Length + (system?.Length ?? 0) + history.Sum(turn => turn.Content.Length);
        if (total > MaximumAiPromptLength)
            throw new ArgumentException($"提示词总长度不能超过 {MaximumAiPromptLength} 个字符。");
        var text = await ai.CompleteAsync(new PluginAiRequest(system, prompt, history), cancellationToken);
        return new JsonObject { ["text"] = text };
    }

    // ---- reader (no extra permission; book access rules apply) ----------

    private async Task<JsonNode?> OpenInReaderAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Read);
        var reader = _reader ?? throw new InvalidOperationException("当前窗口不能打开阅读器。");
        var (book, fileId) = await ResolveAccessibleBookAsync(parameters, cancellationToken);
        string? chapterPath = OptionalString(parameters, "chapterPath");
        var chapterIndex = OptionalInt(parameters, "chapterIndex", -1, -1, 100_000);
        if (chapterPath is null && chapterIndex >= 0)
        {
            var content = _content ?? throw new InvalidOperationException("当前版本不支持按章节打开。");
            var outline = await content.GetOutlineAsync(book, fileId, cancellationToken);
            if (chapterIndex >= outline.Chapters.Count)
                throw new ArgumentException("章节序号超出范围。");
            chapterPath = outline.Chapters[chapterIndex].Path;
            fileId = outline.FileId;
        }
        int? offset = parameters.TryGetProperty("offset", out var offsetElement) && offsetElement.TryGetInt32(out var value)
            ? Math.Max(0, value)
            : null;
        await reader.OpenAsync(book.Id, fileId, chapterPath, offset, cancellationToken);
        return new JsonObject { ["opened"] = true };
    }

    private IPluginContentHost RequireContent()
    {
        if (!_plugin.Manifest.CanReadContent)
            throw new UnauthorizedAccessException("插件没有读取书籍正文的权限。");
        return RequireContentHost();
    }

    private IPluginContentHost RequireContentHost() =>
        _content ?? throw new InvalidOperationException("当前版本不支持读取书籍正文。");

    private void RequireReadingWrite()
    {
        if (!_plugin.Manifest.CanWriteReadingData)
            throw new UnauthorizedAccessException("插件没有写入划线和书签的权限。");
    }

    private IPluginAiHost RequireAi()
    {
        if (!_plugin.Manifest.UsesAi)
            throw new UnauthorizedAccessException("插件没有使用 AI 的权限。");
        return _ai ?? throw new InvalidOperationException("当前版本不支持插件使用 AI。");
    }

    /// <summary>
    /// Resolves <c>bookId</c> (and optional <c>fileId</c>) for book-scoped
    /// calls: the books the page was opened with, or any book with libraryAll.
    /// </summary>
    private async Task<(Book Book, Guid? FileId)> ResolveAccessibleBookAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Read);
        var id = ParseGuid(RequiredString(parameters, "bookId"), "bookId");
        if (!_allowedBooks.Contains(id) && !_plugin.Manifest.HasLibraryScopeAll)
            throw new UnauthorizedAccessException("插件只能访问打开它时选中的书籍。");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        Guid? fileId = null;
        if (OptionalString(parameters, "fileId") is { Length: > 0 } fileText)
        {
            fileId = ParseGuid(fileText, "fileId");
            if (book.Files.All(file => file.Id != fileId))
                throw new ArgumentException("fileId 不属于这本书。");
        }
        return (book, fileId);
    }

    // ---- files and clipboard -------------------------------------------

    private async Task<JsonNode?> PickFilesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var extensions = new List<string>();
        if (parameters.TryGetProperty("extensions", out var extensionsElement)
            && extensionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in extensionsElement.EnumerateArray())
            {
                var value = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(value)) continue;
                if (!value.StartsWith('.')) value = "." + value;
                if (value.Length > 12 || value.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
                    throw new ArgumentException($"文件扩展名“{value}”无效。");
                extensions.Add(value.ToLowerInvariant());
            }
        }
        if (extensions.Count > 20)
            throw new ArgumentException("最多只能指定 20 种文件扩展名。");

        var files = await _ui.PickFilesAsync(OptionalBool(parameters, "multiple", false), extensions);
        var result = new JsonArray();
        foreach (var file in files)
        {
            if (file.Path is { } path) lock (_pickedFiles) _pickedFiles.Add(path);
            result.Add(new JsonObject { ["name"] = file.Name, ["path"] = file.Path });
        }
        return new JsonObject { ["files"] = result };
    }

    private async Task<JsonNode?> SaveFileAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(RequiredString(parameters, "fileName"));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName 无效。");
        var text = OptionalString(parameters, "text");
        var base64 = OptionalString(parameters, "base64");
        byte[] content;
        if (base64 is not null)
        {
            try { content = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new ArgumentException("base64 内容无效。"); }
        }
        else if (text is not null)
        {
            content = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            throw new ArgumentException("需要提供 text 或 base64 内容。");
        }
        if (content.Length > MaximumResponseBytes)
            throw new ArgumentException("保存的文件超过 10 MB。");

        var saved = await _ui.SaveFileAsync(fileName, content);
        return saved is null
            ? null
            : new JsonObject { ["name"] = saved.Name, ["path"] = saved.Path };
    }

    private async Task<JsonNode?> CopyTextAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var text = RequiredString(parameters, "text");
        if (text.Length > 100_000)
            throw new ArgumentException("复制的文本过长。");
        await _ui.CopyTextAsync(text);
        return null;
    }

    private async Task<JsonNode?> ReadTextAsync(CancellationToken cancellationToken)
    {
        var text = await _ui.ReadTextAsync();
        return text is null ? null : new JsonObject { ["text"] = Truncate(text, 100_000) };
    }

    private async Task<JsonNode?> UpdateBookAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Write);
        var id = ParseAllowedBookId(RequiredString(parameters, "id"));
        if (!parameters.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("缺少要更新的字段。");
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");

        ApplyPatch(book, patch);
        await _library.UpdateMetadataAsync(book, cancellationToken);
        _ui.BooksChanged([id]);
        var saved = await _library.GetBookAsync(id, cancellationToken) ?? book;
        return ToJson(saved);
    }

    internal static void ApplyPatch(Book book, JsonElement patch)
    {
        // Validate everything before touching the book so a bad field cannot
        // leave a half-applied update behind.
        var changes = new List<Action>();
        foreach (var property in patch.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "title":
                {
                    var text = RequiredText(value, property.Name, MaximumFieldLength);
                    changes.Add(() => book.Title = text);
                    break;
                }
                case "authors":
                {
                    var text = RequiredText(value, property.Name, MaximumFieldLength);
                    changes.Add(() => book.Authors = text);
                    break;
                }
                case "series": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Series = text); break; }
                case "description": { var text = OptionalText(value, property.Name, MaximumTextLength); changes.Add(() => book.Description = text); break; }
                case "publisher": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Publisher = text); break; }
                case "publishDate": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.PublishDate = text); break; }
                case "isbn": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Isbn = text); break; }
                case "pageCount": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.PageCount = text); break; }
                case "binding": { var text = OptionalText(value, property.Name, MaximumFieldLength); changes.Add(() => book.Binding = text); break; }
                case "tags": { var text = OptionalText(value, property.Name, MaximumFieldLength) ?? string.Empty; changes.Add(() => book.Tags = text); break; }
                case "category": { var text = OptionalText(value, property.Name, MaximumFieldLength) ?? string.Empty; changes.Add(() => book.Category = text); break; }
                case "seriesIndex":
                {
                    var number = OptionalNumber(value, property.Name, 0, 100_000);
                    changes.Add(() => book.SeriesIndex = number);
                    break;
                }
                case "rating":
                {
                    var number = OptionalNumber(value, property.Name, 0, 10);
                    changes.Add(() => book.OnlineRating = number);
                    break;
                }
                case "ratingCount":
                {
                    var number = OptionalNumber(value, property.Name, 0, int.MaxValue);
                    changes.Add(() => book.OnlineRatingCount = number is null ? null : (int)number.Value);
                    break;
                }
                default:
                    throw new ArgumentException($"不支持更新字段“{property.Name}”。");
            }
        }
        foreach (var change in changes) change();
    }
}
