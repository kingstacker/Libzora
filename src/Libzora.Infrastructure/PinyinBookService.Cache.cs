using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotNetG2P.Chinese;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Cache helpers and reporting.
/// </summary>
public sealed partial class PinyinBookService : IPinyinBookService
{
    private static void Report(
        IProgress<PinyinBookProgress>? progress,
        PinyinBookProgress value) => progress?.Report(value);

    private static string GetPinyinCachePath(string destinationPath) =>
        Path.GetFullPath(destinationPath) + PinyinCacheFileSuffix;

    private static async Task<PinyinCacheSnapshot?> TryLoadPinyinCacheAsync(
        string cachePath,
        string sourcePath,
        string sourceHash,
        PinyinBookOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath)) return null;

        var lines = await File.ReadAllLinesAsync(cachePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        PinyinCacheHeader? header = null;
        var segments = new Dictionary<int, PinyinCacheSegmentLine>();
        var updatedAt = DateTimeOffset.MinValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("Kind", out var kindProperty)
                    || kindProperty.ValueKind != JsonValueKind.String)
                    continue;
                var kind = kindProperty.GetString();
                if (string.Equals(kind, "header", StringComparison.OrdinalIgnoreCase))
                {
                    header = JsonSerializer.Deserialize<PinyinCacheHeader>(
                        line,
                        PinyinCacheJsonOptions);
                    if (header is not null && header.UpdatedAt > updatedAt)
                        updatedAt = header.UpdatedAt;
                }
                else if (string.Equals(kind, "segment", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = JsonSerializer.Deserialize<PinyinCacheSegmentLine>(
                        line,
                        PinyinCacheJsonOptions);
                    if (segment is null || segment.Index < 0) continue;
                    segments[segment.Index] = segment;
                    if (segment.UpdatedAt > updatedAt) updatedAt = segment.UpdatedAt;
                }
            }
            catch (JsonException)
            {
                // A process interruption can leave one incomplete final line;
                // every earlier flushed state remains recoverable.
            }
        }

        if (header is null
            || header.Version != PinyinCacheVersion
            || string.IsNullOrWhiteSpace(header.SourcePath)
            || string.IsNullOrWhiteSpace(header.SourceHash)
            || !Path.GetFullPath(header.SourcePath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
            || !header.SourceHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            return null;

        var cachedOptions = PinyinBookOptions.Normalize(header.Options);
        if (cachedOptions.Engine != options.Engine
            || cachedOptions.OutputStyle != options.OutputStyle
            || cachedOptions.EnableAiReview != options.EnableAiReview)
            return null;

        return new PinyinCacheSnapshot(
            header,
            segments,
            updatedAt == DateTimeOffset.MinValue ? header.UpdatedAt : updatedAt);
    }

    private static int GetCachedTotalSegments(PinyinCacheSnapshot snapshot)
    {
        var inferredTotal = snapshot.Segments.Keys.DefaultIfEmpty(-1).Max() + 1;
        return Math.Max(snapshot.Header.TotalSegments, inferredTotal);
    }

    private static void TryDeletePinyinCache(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to delete completed pinyin cache: {exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
