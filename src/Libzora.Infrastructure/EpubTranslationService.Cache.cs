using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Translation cache paths and cleanup.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private static string GetTranslationCachePath(string outputDirectory) =>
        Path.Combine(Path.GetFullPath(outputDirectory), TranslationCacheFileName);

    private static async Task<TranslationCacheSnapshot?> TryLoadTranslationCacheAsync(
        string cachePath,
        string sourcePath,
        string sourceHash,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath)) return null;

        var lines = await File.ReadAllLinesAsync(cachePath, Encoding.UTF8, cancellationToken);
        TranslationCacheHeader? header = null;
        var segments = new Dictionary<int, TranslationCacheSegmentLine>();
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        var splitPlans = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
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
                    header = JsonSerializer.Deserialize<TranslationCacheHeader>(
                        line,
                        TranslationCacheJsonOptions);
                    if (header is not null)
                        updatedAt = header.UpdatedAt > updatedAt ? header.UpdatedAt : updatedAt;
                }
                else if (string.Equals(kind, "segment", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = JsonSerializer.Deserialize<TranslationCacheSegmentLine>(
                        line,
                        TranslationCacheJsonOptions);
                    if (segment is null || segment.Index < 0) continue;
                    segments[segment.Index] = segment;
                    if (segment.UpdatedAt > updatedAt) updatedAt = segment.UpdatedAt;
                }
                else if (string.Equals(kind, "part", StringComparison.OrdinalIgnoreCase))
                {
                    var part = JsonSerializer.Deserialize<TranslationCachePartLine>(line, TranslationCacheJsonOptions);
                    if (part is null || string.IsNullOrWhiteSpace(part.SourceText) || string.IsNullOrWhiteSpace(part.TranslatedText)) continue;
                    XmlConvert.VerifyXmlChars(part.TranslatedText);
                    parts[part.SourceText] = part.TranslatedText;
                    if (part.UpdatedAt > updatedAt) updatedAt = part.UpdatedAt;
                }
                else if (string.Equals(kind, "split", StringComparison.OrdinalIgnoreCase))
                {
                    var split = JsonSerializer.Deserialize<TranslationCacheSplitLine>(line, TranslationCacheJsonOptions);
                    if (split is null || !IsValidSplitPlan(split.SourceText, split.Parts)) continue;
                    splitPlans[split.SourceText] = split.Parts;
                    if (split.UpdatedAt > updatedAt) updatedAt = split.UpdatedAt;
                }
            }
            catch (Exception exception) when (exception is JsonException or XmlException)
            {
                // An interrupted write can leave one incomplete final line;
                // earlier flushed entries are still valid and recoverable.
            }
        }

        if (header is null
            || header.Version != TranslationCacheVersion
            || string.IsNullOrWhiteSpace(header.SourcePath)
            || string.IsNullOrWhiteSpace(header.SourceHash)
            || !Path.GetFullPath(header.SourcePath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
            || !header.SourceHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            return null;

        var cachedSettings = BookTranslationSettings.Normalize(header.Settings);
        if (!cachedSettings.SourceLanguage.Equals(settings.SourceLanguage, StringComparison.OrdinalIgnoreCase)
            || !cachedSettings.TargetLanguage.Equals(settings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            return null;

        return new TranslationCacheSnapshot(
            header,
            segments,
            parts,
            splitPlans,
            updatedAt == DateTimeOffset.MinValue ? header.UpdatedAt : updatedAt);
    }

    private static int GetCachedTotalSegments(TranslationCacheSnapshot snapshot)
    {
        var inferredTotal = snapshot.Segments.Keys.DefaultIfEmpty(-1).Max() + 1;
        return Math.Max(snapshot.Header.TotalSegments, inferredTotal);
    }

    private static bool IsValidSplitPlan(string source, IReadOnlyList<string> parts)
    {
        if (string.IsNullOrEmpty(source) || parts is null || parts.Count < 2) return false;
        var offset = 0;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part) || part.Length >= source.Length) return false;
            while (offset < source.Length && char.IsWhiteSpace(source[offset])) offset++;
            if (!source.AsSpan(offset).StartsWith(part, StringComparison.Ordinal)) return false;
            offset += part.Length;
        }
        while (offset < source.Length && char.IsWhiteSpace(source[offset])) offset++;
        return offset == source.Length;
    }

    private static void TryDeleteTranslationCache(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to delete completed translation cache: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aiRequestsPerMinuteLimiter.Dispose();
        if (_ownsAiChatClient) _aiChatClient.Dispose();
    }
}
