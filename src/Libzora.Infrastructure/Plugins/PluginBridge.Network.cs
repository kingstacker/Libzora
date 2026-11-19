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
/// Book JSON projection and network fetch.
/// </summary>
public sealed partial class PluginBridge : IDisposable
{
    private async Task<JsonNode?> GetCoverAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Read);
        var id = ParseAllowedBookId(RequiredString(parameters, "id"));
        var book = await _library.GetBookAsync(id, cancellationToken);
        if (book?.CoverPath is not { Length: > 0 } coverPath) return null;
        var path = Path.GetFullPath(Path.Combine(_paths.Data, coverPath));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumCoverBytes) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var format = DetectImage(bytes);
        return format is null ? null : JsonValue.Create($"data:{format.Value.MediaType};base64,{Convert.ToBase64String(bytes)}");
    }

    private async Task<JsonNode?> SetCoverAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireLibrary(PluginLibraryAccess.Write);
        var id = ParseAllowedBookId(RequiredString(parameters, "id"));
        // Accept a bare Base64 payload, a data: URL, or "<media-type>;base64,<data>".
        var data = RequiredString(parameters, "data");
        const string Base64Marker = "base64,";
        var marker = data.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        var payload = marker >= 0 ? data[(marker + Base64Marker.Length)..] : data;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            throw new ArgumentException("封面数据不是有效的 Base64。");
        }
        if (bytes.Length == 0 || bytes.Length > MaximumCoverBytes)
            throw new ArgumentException("封面图片为空或超过 10 MB。");
        var format = DetectImage(bytes) ?? throw new ArgumentException("封面必须是 JPEG、PNG、WebP 或 GIF 图片。");

        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("书籍不存在或已被删除。");
        _paths.EnsureDirectories();
        // A fresh name per write keeps image caches from showing the old cover.
        var path = Path.Combine(_paths.Covers, $"{book.Id:N}-plugin-{Guid.NewGuid():N}{format.Extension}");
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
        book.CoverPath = Path.GetRelativePath(_paths.Data, path);
        await _library.UpdateMetadataAsync(book, cancellationToken);
        _ui.BooksChanged([id]);
        return ToJson(book);
    }

    internal static JsonObject ToJson(Book book) => new()
    {
        ["id"] = book.Id.ToString("D"),
        ["title"] = book.Title,
        ["authors"] = book.Authors,
        ["series"] = book.Series,
        ["seriesIndex"] = book.SeriesIndex,
        ["description"] = book.Description,
        ["publisher"] = book.Publisher,
        ["publishDate"] = book.PublishDate,
        ["isbn"] = book.Isbn,
        ["pageCount"] = book.PageCount,
        ["binding"] = book.Binding,
        ["rating"] = book.OnlineRating,
        ["ratingCount"] = book.OnlineRatingCount,
        ["tags"] = book.Tags,
        ["category"] = book.Category,
        ["hasCover"] = !string.IsNullOrWhiteSpace(book.CoverPath),
        ["favorite"] = book.IsFavorite,
        ["readingStatus"] = book.ReadingStatus.ToString().ToLowerInvariant(),
        ["collectionIds"] = new JsonArray(book.CollectionIds
            .Select(id => (JsonNode)JsonValue.Create(id.ToString("D"))!)
            .ToArray()),
        ["updatedAt"] = book.UpdatedAt,
        ["formats"] = new JsonArray(book.Files
            .Select(file => file.Format.ToUpperInvariant())
            .Distinct()
            .Select(format => (JsonNode)JsonValue.Create(format)!)
            .ToArray())
    };

    // ---- network -------------------------------------------------------

    private async Task<JsonNode?> FetchAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireNetwork();
        if (!_ui.NetworkEnabled)
            throw new InvalidOperationException("网络访问已在应用设置中关闭。");

        var uri = await ParseAllowedUriAsync(RequiredString(parameters, "url"), cancellationToken);
        var method = (OptionalString(parameters, "method") ?? "GET").ToUpperInvariant();
        if (method is not ("GET" or "POST" or "HEAD"))
            throw new ArgumentException($"不支持的请求方法“{method}”。");
        var body = OptionalString(parameters, "body");
        var responseType = OptionalString(parameters, "responseType") ?? "text";
        if (responseType is not ("text" or "base64"))
            throw new ArgumentException("responseType 只能是 text 或 base64。");
        var headers = ParseHeaders(parameters);

        for (var redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            string? contentType = null;
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = value; continue; }
                request.Headers.TryAddWithoutValidation(name, value);
            }
            if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            if (!request.Headers.Contains("Accept-Language")) request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.6");
            if (body is not null && method == "POST")
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "text/plain; charset=utf-8");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                if (redirect >= MaximumRedirects)
                    throw new InvalidOperationException("重定向次数过多。");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                uri = await ParseAllowedUriAsync(next.AbsoluteUri, cancellationToken);
                if (status is 301 or 302 or 303 && method == "POST") { method = "GET"; body = null; }
                continue;
            }

            var bytes = await ReadLimitedAsync(response, cancellationToken);
            var responseHeaders = new JsonObject();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                responseHeaders[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return new JsonObject
            {
                ["status"] = status,
                ["ok"] = response.IsSuccessStatusCode,
                ["url"] = uri.AbsoluteUri,
                ["contentType"] = mediaType,
                ["headers"] = responseHeaders,
                ["body"] = responseType == "base64"
                    ? Convert.ToBase64String(bytes)
                    : Decode(bytes, response.Content.Headers.ContentType?.CharSet)
            };
        }
    }

    private static List<KeyValuePair<string, string>> ParseHeaders(JsonElement parameters)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (parameters.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headerElement.EnumerateObject())
            {
                if (ForbiddenHeaders.Contains(header.Name)
                    || header.Name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)
                    || header.Name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"不允许设置请求头“{header.Name}”。");
                if (header.Value.ValueKind != JsonValueKind.String) continue;
                headers.Add(new(header.Name, header.Value.GetString() ?? string.Empty));
            }
        }
        return headers;
    }

    private async Task<Uri> ParseAllowedUriAsync(string value, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("只能请求 https:// 地址。");
        if (!uri.IsDefaultPort)
            throw new ArgumentException("只能请求默认端口（443）。");
        if (_plugin.Manifest.NetworkOrigins.Any(origin => origin.Allows(uri)))
            return uri;
        if (await _grants.IsApprovedAsync(uri.IdnHost, cancellationToken))
            return uri;
        throw new UnauthorizedAccessException($"插件没有访问 {uri.Host} 的权限。请先在插件页面请求访问授权。");
    }

    private async Task<JsonNode?> RequestNetworkAccessAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireNetwork();
        var text = RequiredString(parameters, "origin").Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort)
            throw new ArgumentException("只能请求访问 https:// 默认端口的网站。");
        var host = uri.IdnHost;
        if (!IsGrantableHost(host))
            throw new ArgumentException("这个地址不能作为插件访问的网站。");
        var approved = _plugin.Manifest.NetworkOrigins.Any(origin => origin.Allows(uri))
            || await _grants.IsApprovedAsync(host, cancellationToken);
        if (!approved)
        {
            if (!await RequestApprovalAsync(host, cancellationToken))
                return new JsonObject { ["granted"] = false, ["host"] = host };
        }
        return new JsonObject { ["granted"] = true, ["host"] = host };
    }

    private Task<bool> RequestApprovalAsync(string host, CancellationToken cancellationToken)
    {
        var pending = _networkApprovals.GetOrAdd(
            host,
            key => new Lazy<Task<bool>>(() => ApproveHostAsync(key, cancellationToken)));
        return pending.Value;
    }

    private async Task<bool> ApproveHostAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _ui.RequestNetworkAccessAsync(host)) return false;
            await _grants.ApproveAsync(host, cancellationToken);
            return true;
        }
        finally
        {
            _networkApprovals.TryRemove(host, out _);
        }
    }

    private static bool IsGrantableHost(string host) =>
        host.Length <= 253
        && Uri.CheckHostName(host) == UriHostNameType.Dns
        && host.Contains('.')
        && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the target of a download redirect. File hosts commonly hand
    /// the transfer to a CDN whose name the plugin cannot know in advance, so
    /// an undeclared https host is offered to the user instead of failing the
    /// download outright; the answer is remembered like a runtime grant.
    /// </summary>
    private async Task<Uri> AuthorizeDownloadRedirectAsync(Uri target, CancellationToken cancellationToken)
    {
        try
        {
            return await ParseAllowedUriAsync(target.AbsoluteUri, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            var host = target.IdnHost;
            if (!IsGrantableHost(host) || !await RequestApprovalAsync(host, cancellationToken))
                throw new UnauthorizedAccessException($"下载被重定向到 {target.Host}，未获允许访问。");
            return target;
        }
    }

    private void RequireNetwork()
    {
        if (!_plugin.Manifest.HasNetworkPermission)
            throw new UnauthorizedAccessException("插件没有声明网络权限。");
    }

    private void RequireImport()
    {
        if (!_plugin.Manifest.CanImportLibrary)
            throw new UnauthorizedAccessException("插件没有导入书籍的权限。");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidOperationException("响应内容超过 10 MB。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("响应内容超过 10 MB。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
