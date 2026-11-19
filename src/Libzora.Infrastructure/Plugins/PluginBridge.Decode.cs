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
/// Charset detection and byte decoding.
/// </summary>
public sealed partial class PluginBridge : IDisposable
{
    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
            catch (ArgumentException) { }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    // ---- storage -------------------------------------------------------

    private async Task<JsonNode?> StorageGetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireStorage();
        return await _storage.GetAsync(RequiredString(parameters, "key"), cancellationToken);
    }

    private async Task<JsonNode?> StorageSetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireStorage();
        var value = parameters.TryGetProperty("value", out var element) && element.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(element.GetRawText())
            : null;
        await _storage.SetAsync(RequiredString(parameters, "key"), value, cancellationToken);
        return null;
    }

    private async Task<JsonNode?> StorageRemoveAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireStorage();
        await _storage.SetAsync(RequiredString(parameters, "key"), null, cancellationToken);
        return null;
    }

    // ---- book tasks ----------------------------------------------------

    private async Task<JsonNode?> TranslateBookAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireTranslation();
        RequireBookTasks();
        var bookId = ParseAllowedBookId(RequiredString(parameters, "bookId"));
        var provider = (OptionalString(parameters, "provider") ?? "ai").ToLowerInvariant() switch
        {
            "ai" => "ai",
            "bing" or "bing-free" => throw new NotSupportedException("Bing 网页免费翻译已停用，请选择模型翻译。"),
            "google" or "google-free" => throw new NotSupportedException("Google 网页免费翻译已停用，请选择模型翻译。"),
            _ => throw new ArgumentException("不支持的翻译服务。")
        };
        var request = new PluginTranslationRequest(
            provider,
            OptionalString(parameters, "sourceLanguage") ?? "auto",
            RequiredString(parameters, "targetLanguage"),
            OptionalBool(parameters, "translated", true),
            OptionalBool(parameters, "bilingual", false),
            OptionalInt(parameters, "aiRequestsPerMinute", 0, 0, BookTranslationSettings.MaxAiRequestsPerMinute),
            Truncate(OptionalString(parameters, "googleProxyAddress") ?? string.Empty, 500),
            OptionalBool(parameters, "resume", true));
        if (!request.Translated && !request.Bilingual)
            throw new ArgumentException("请至少选择一种翻译结果。");
        return await RunTaskAsync(
            "translation",
            bookId,
            (progress, token) => _bookTasks!.TranslateBookAsync(bookId, request, progress, token),
            cancellationToken);
    }

    private async Task<JsonNode?> AnnotatePinyinAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequirePinyin();
        RequireBookTasks();
        var bookId = ParseAllowedBookId(RequiredString(parameters, "bookId"));
        var engine = (OptionalString(parameters, "engine") ?? "dotnet-g2p").ToLowerInvariant();
        if (engine is not ("dotnet-g2p" or "g2pw"))
            throw new ArgumentException("不支持的注音引擎。");
        var style = (OptionalString(parameters, "outputStyle") ?? "tone-marked").ToLowerInvariant();
        if (style is not ("tone-marked" or "tone-number" or "none"))
            throw new ArgumentException("不支持的注音样式。");
        var request = new PluginPinyinRequest(
            engine,
            style,
            OptionalBool(parameters, "aiReview", true),
            OptionalBool(parameters, "resume", true));
        return await RunTaskAsync(
            "pinyin",
            bookId,
            (progress, token) => _bookTasks!.AnnotatePinyinAsync(bookId, request, progress, token),
            cancellationToken);
    }

    private Task<JsonNode?> CancelTaskAsync()
    {
        var cancelled = _taskCancellation is not null;
        _taskCancellation?.Cancel();
        return Task.FromResult<JsonNode?>(new JsonObject { ["cancelling"] = cancelled });
    }

    private Task<JsonNode?> PinyinStatusAsync()
    {
        RequirePinyin();
        RequireBookTasks();
        var status = _bookTasks!.GetPinyinStatus();
        return Task.FromResult<JsonNode?>(new JsonObject { ["g2pwInstalled"] = status.G2pwInstalled });
    }

    private Task<JsonNode?> DownloadPinyinModelAsync(CancellationToken cancellationToken)
    {
        RequirePinyin();
        RequireBookTasks();
        return RunTaskAsync(
            "pinyin-model",
            null,
            async (progress, token) =>
            {
                await _bookTasks!.DownloadPinyinModelAsync(progress, token);
                return new JsonObject { ["installed"] = true };
            },
            cancellationToken);
    }

    private async Task<JsonNode?> RunTaskAsync(
        string task,
        Guid? bookId,
        Func<IProgress<PluginTaskProgress>, CancellationToken, Task<JsonObject>> run,
        CancellationToken cancellationToken)
    {
        if (!_taskGate.Wait(0))
            throw new InvalidOperationException("已有任务正在进行，请等待完成或先取消。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _taskCancellation = linked;
        try
        {
            var progress = new Progress<PluginTaskProgress>(
                value => ReportTaskProgress(task, bookId, value));
            var result = await run(progress, linked.Token);
            if (bookId is { } id)
                _ui.BooksChanged([id]);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new JsonObject
            {
                ["task"] = task,
                ["bookId"] = bookId?.ToString("D"),
                ["cancelled"] = true
            };
        }
        finally
        {
            _taskCancellation = null;
            _taskGate.Release();
        }
    }

    private void ReportTaskProgress(string task, Guid? bookId, PluginTaskProgress value)
    {
        try
        {
            _ui.RaiseEvent("task.progress", new JsonObject
            {
                ["task"] = task,
                ["bookId"] = bookId?.ToString("D"),
                ["stage"] = value.Stage,
                ["message"] = value.Message,
                ["percent"] = Math.Clamp(value.Percent, 0, 100),
                ["processed"] = value.Processed,
                ["total"] = value.Total,
                ["indeterminate"] = value.Indeterminate
            });
        }
        catch (Exception)
        {
            // Progress is cosmetic; a closing window must not fail the task.
        }
    }

    private void RequireTranslation()
    {
        if (!_plugin.Manifest.AllowsTranslation)
            throw new UnauthorizedAccessException("插件没有翻译书籍的权限。");
    }

    private void RequirePinyin()
    {
        if (!_plugin.Manifest.AllowsPinyin)
            throw new UnauthorizedAccessException("插件没有书籍注音的权限。");
    }

    private void RequireBookTasks()
    {
        if (_bookTasks is null)
            throw new InvalidOperationException("当前版本不支持插件任务。");
    }

    // ---- imports -------------------------------------------------------

    /// <summary>
    /// Downloads a file the plugin points at and imports it into the library.
    /// The user sees the file name and size before any bytes are written, and
    /// a per-request progress event lets the plugin page show the transfer.
    /// </summary>
    private async Task<JsonNode?> ImportDownloadAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireImport();
        RequireNetwork();
        if (!_ui.NetworkEnabled)
            throw new InvalidOperationException("网络访问已在应用设置中关闭。");

        var uri = await ParseAllowedUriAsync(RequiredString(parameters, "url"), cancellationToken);
        var headers = ParseHeaders(parameters);
        var fileName = NormalizeImportFileName(OptionalString(parameters, "fileName"), uri);
        var directory = Path.Combine(_paths.PluginData, _plugin.Id, "downloads");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var (name, value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
                if (!request.Headers.Contains("User-Agent"))
                    request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                if (!request.Headers.Contains("Accept"))
                    request.Headers.TryAddWithoutValidation("Accept", "*/*");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (redirect >= MaximumRedirects)
                        throw new InvalidOperationException("重定向次数过多。");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    uri = await AuthorizeDownloadRedirectAsync(next, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"下载失败（HTTP {status}）。");
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                // Quota and login failures come back as a JSON message or an
                // HTML page; importing either as the requested book is wrong.
                var expectsHtml = Path.GetExtension(fileName).ToLowerInvariant() is ".html" or ".htm";
                if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                    || (!expectsHtml && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("下载被服务拒绝，请检查账号状态或下载额度。");

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                if (totalBytes > MaximumImportBytes)
                    throw new InvalidOperationException($"文件超过 {MaximumImportBytes / (1024 * 1024)} MB，无法导入。");
                if (!await _ui.ConfirmImportAsync(fileName, totalBytes))
                    return new JsonObject { ["cancelled"] = true, ["fileName"] = fileName };

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var destination = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    var lastReport = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read <= 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        copied += read;
                        if (copied > MaximumImportBytes)
                            throw new InvalidOperationException($"文件超过 {MaximumImportBytes / (1024 * 1024)} MB，无法导入。");
                        if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(200))
                        {
                            lastReport = Stopwatch.GetTimestamp();
                            ReportImportProgress(fileName, copied, totalBytes, "download");
                        }
                    }
                    await destination.FlushAsync(cancellationToken);
                    ReportImportProgress(fileName, copied, totalBytes, "download");
                }
                break;
            }

            var result = await _library.ImportAsync(
                [temporaryPath],
                new Progress<TransferProgress>(progress =>
                    ReportImportProgress(fileName, progress.BytesCopied, progress.TotalBytes, "import")),
                cancellationToken);
            var item = result.Items.FirstOrDefault();
            if (item?.BookId is { } importedId)
                _ui.BooksChanged([importedId]);
            return new JsonObject
            {
                ["fileName"] = fileName,
                ["imported"] = item?.Succeeded == true,
                ["added"] = item?.Added == true,
                ["message"] = item?.Message,
                ["bookId"] = item?.BookId?.ToString("D")
            };
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ReportImportProgress(string fileName, long copied, long totalBytes, string stage)
    {
        try
        {
            _ui.RaiseEvent("import.progress", new JsonObject
            {
                ["fileName"] = fileName,
                ["stage"] = stage,
                ["downloadedBytes"] = copied,
                ["totalBytes"] = totalBytes,
                ["percent"] = totalBytes > 0 ? Math.Clamp(copied * 100d / totalBytes, 0, 100) : 0
            });
        }
        catch (Exception)
        {
            // Progress is cosmetic; a closing window must not fail the import.
        }
    }

    private static string NormalizeImportFileName(string? value, Uri uri)
    {
        var name = (value ?? string.Empty).Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character);
        }
        name = builder.ToString().Trim(' ', '.');
        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            name = name[..Math.Max(1, 120 - extension.Length)] + extension;
        }
        if (name.Length == 0)
            throw new ArgumentException("插件没有提供要导入的文件名。");
        if (!ImportExtensions.Contains(Path.GetExtension(name)))
            throw new ArgumentException("只能导入常见电子书文件（EPUB、PDF、MOBI、AZW3、TXT 等）。");
        return name;
    }

    // ---- secrets -------------------------------------------------------

    private PluginSecrets RequireSecrets()
    {
        if (!_plugin.Manifest.UsesSecrets || _secrets is null)
            throw new UnauthorizedAccessException("插件没有保存账号信息的权限。");
        return _secrets;
    }

    private async Task<JsonNode?> SecretGetAsync(JsonElement parameters, CancellationToken cancellationToken) =>
        JsonValue.Create(await RequireSecrets().GetAsync(RequiredString(parameters, "key"), cancellationToken));

    private async Task<JsonNode?> SecretSetAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        await RequireSecrets().SetAsync(
            RequiredString(parameters, "key"),
            OptionalString(parameters, "value"),
            cancellationToken);
        return null;
    }

    private async Task<JsonNode?> SecretRemoveAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        await RequireSecrets().SetAsync(RequiredString(parameters, "key"), null, cancellationToken);
        return null;
    }

    // ---- helpers -------------------------------------------------------

    private static Task<JsonNode?> Ui(Action action)
    {
        action();
        return Task.FromResult<JsonNode?>(null);
    }

    private void RequireLibrary(PluginLibraryAccess access)
    {
        if (_plugin.Manifest.LibraryAccess < access)
            throw new UnauthorizedAccessException(access == PluginLibraryAccess.Write
                ? "插件没有修改书库的权限。"
                : "插件没有读取书库的权限。");
    }

    private void RequireLibraryAll()
    {
        if (!_plugin.Manifest.HasLibraryScopeAll)
            throw new UnauthorizedAccessException("插件没有访问整个书库的权限。");
    }

    private static Guid ParseGuid(string value, string name) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new ArgumentException($"参数“{name}”不是有效的书籍或分组 id。");

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static LibraryReadingStatus? OptionalReadingStatus(JsonElement parameters) =>
        OptionalString(parameters, "readingStatus")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "unread" => LibraryReadingStatus.Unread,
            "reading" => LibraryReadingStatus.Reading,
            "finished" => LibraryReadingStatus.Finished,
            var value => throw new ArgumentException($"readingStatus 不支持“{value}”。")
        };

    private static LibrarySortMode OptionalSortMode(JsonElement parameters) =>
        OptionalString(parameters, "sort")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "updated" => LibrarySortMode.UpdatedDescending,
            "title" => LibrarySortMode.TitleAscending,
            "author" => LibrarySortMode.AuthorAscending,
            "created" => LibrarySortMode.CreatedDescending,
            "progress" => LibrarySortMode.ProgressDescending,
            var value => throw new ArgumentException($"sort 不支持“{value}”。")
        };

    private void RequireStorage()
    {
        if (!_plugin.Manifest.UsesStorage)
            throw new UnauthorizedAccessException("插件没有声明存储权限。");
    }

    private Guid ParseAllowedBookId(string? value)
    {
        if (!Guid.TryParse(value, out var id) || !_allowedBooks.Contains(id))
            throw new UnauthorizedAccessException("插件只能访问打开它时选中的书籍。");
        return id;
    }

    private static Uri ParseExternalUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("只能打开 http(s) 链接。");
        return uri;
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        OptionalString(parameters, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少参数“{name}”。");

    private static bool OptionalBool(JsonElement parameters, string name, bool fallback) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int OptionalInt(JsonElement parameters, string name, int fallback, int minimum, int maximum)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return fallback;
        // A fractional or non-numeric value must not silently become the
        // fallback (for the AI rate limit that would mean "unlimited").
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || double.IsNaN(number) || Math.Floor(number) != number)
            throw new ArgumentException($"{name} 必须是整数。");
        return (int)Math.Clamp(number, minimum, maximum);
    }

    private static string? OptionalString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequiredText(JsonElement value, string name, int maximumLength)
    {
        var text = OptionalText(value, name, maximumLength);
        return string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException($"字段“{name}”不能为空。")
            : text;
    }

    private static string? OptionalText(JsonElement value, string name, int maximumLength)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"字段“{name}”必须是文本。");
        var text = value.GetString()!.Trim();
        if (text.Length > maximumLength)
            throw new ArgumentException($"字段“{name}”过长。");
        return text.Length == 0 ? null : text;
    }

    private static double? OptionalNumber(JsonElement value, string name, double minimum, double maximum)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        double number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) number = parsed;
        else throw new ArgumentException($"字段“{name}”必须是数字。");
        if (double.IsNaN(number) || number < minimum || number > maximum)
            throw new ArgumentException($"字段“{name}”超出范围。");
        return number;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    internal static (string MediaType, string Extension)? DetectImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", ".png");
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", ".webp");
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return ("image/gif", ".gif");
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _taskCancellation?.Cancel();
        _http.Dispose();
    }
}
