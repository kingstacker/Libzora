using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Libzora.Infrastructure;

/// <summary>
/// Chapter title reading and normalisation.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private static async Task<string> ReadChapterTitleAsync(
        string chapterPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                chapterPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            var documentTitle = string.Empty;
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase)
                    && documentTitle.Length == 0
                    && !reader.IsEmptyElement)
                {
                    documentTitle = NormalizeTitle(
                        await ReadXmlElementTextAsync(reader, cancellationToken));
                    continue;
                }

                if (!reader.LocalName.Equals("h1", StringComparison.OrdinalIgnoreCase)
                    || reader.IsEmptyElement)
                {
                    continue;
                }

                var heading = NormalizeTitle(
                    await ReadXmlElementTextAsync(reader, cancellationToken));
                if (heading.Length > 0)
                    return NormalizeChapterTitle(heading);
            }

            return NormalizeChapterTitle(documentTitle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<List<string>> ReadChapterTitlesAsync(
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        CancellationToken cancellationToken)
    {
        var titles = new string[chapters.Count];
        Array.Fill(titles, string.Empty);

        // An EPUB TOC already contains the display title for most novel
        // chapters. Reuse it and only inspect spine files that the TOC does
        // not cover (cover, title page, dedication, etc.).
        foreach (var item in navigation)
        {
            if (item.ChapterIndex < 0
                || item.ChapterIndex >= titles.Length
                || string.IsNullOrWhiteSpace(item.Title)
                || titles[item.ChapterIndex].Length > 0)
            {
                continue;
            }

            titles[item.ChapterIndex] = NormalizeTitle(item.Title);
        }

        var missingIndexes = Enumerable.Range(0, chapters.Count)
            .Where(index => titles[index].Length == 0)
            .ToArray();
        await Parallel.ForEachAsync(
            missingIndexes,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (index, token) =>
            {
                titles[index] = await ReadChapterTitleAsync(chapters[index], token);
            });

        return titles.ToList();
    }

    private static async Task<string> ReadXmlElementTextAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        using var subtree = reader.ReadSubtree();
        var builder = new StringBuilder();
        while (await subtree.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType is XmlNodeType.Text
                or XmlNodeType.CDATA
                or XmlNodeType.Whitespace
                or XmlNodeType.SignificantWhitespace)
            {
                builder.Append(subtree.Value);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeChapterTitle(string title) =>
        title.ToLowerInvariant() switch
        {
            "cover" => "封面",
            "table of contents" => "目录",
            _ => title
        };

    private static string NormalizeTitle(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private const int ChapterTitlePreviewMaxLength = 20;

    private static readonly HashSet<string> BodyPreviewSkippedElements = new(StringComparer.OrdinalIgnoreCase)
    { "head", "title", "script", "style" };

    // Calibre-style conversions stamp the same book-level <title> on every
    // split file, so spine chapters missing from the book's own TOC (front
    // matter such as author bios and dedications) would all show one identical
    // label. Give each member of a duplicate group a title derived from its
    // first body line instead.
    private static async Task ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(
        IReadOnlyList<string> chapters,
        List<string> chapterTitles,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        CancellationToken cancellationToken)
    {
        var authoritativeIndexes = navigation
            .Select(item => item.ChapterIndex)
            .ToHashSet();
        var duplicates = chapterTitles
            .Select((title, index) => (title, index))
            .Where(item => item.title.Length > 0 && !authoritativeIndexes.Contains(item.index))
            .GroupBy(item => item.title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicates.Count == 0) return;

        var duplicateIndexes = Enumerable.Range(0, chapterTitles.Count)
            .Where(index => !authoritativeIndexes.Contains(index)
                && duplicates.Contains(chapterTitles[index]))
            .ToArray();
        var previews = new string[chapterTitles.Count];
        await Parallel.ForEachAsync(
            duplicateIndexes,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (index, token) =>
            {
                previews[index] = TruncateChapterTitle(
                    await ReadChapterBodyPreviewAsync(chapters[index], token));
            });

        foreach (var index in duplicateIndexes)
        {
            var preview = previews[index];
            // A preview that collides again would just move the duplication;
            // keep the original label when no distinct first line exists.
            if (preview.Length > 0 && !duplicates.Contains(preview))
                chapterTitles[index] = preview;
        }
    }

    private static async Task<string> ReadChapterBodyPreviewAsync(
        string chapterPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                chapterPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var localName = reader.LocalName;
                if (BodyPreviewSkippedElements.Contains(localName))
                {
                    if (!reader.IsEmptyElement)
                        reader.Skip();
                    continue;
                }

                if (localName is not (
                    "p" or "div" or "section" or "article" or "li"
                    or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"))
                {
                    continue;
                }

                if (reader.IsEmptyElement)
                    continue;

                var preview = NormalizeTitle(
                    await ReadXmlElementTextAsync(reader, cancellationToken));
                if (preview.Length > 0)
                    return preview;
            }

            return string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch { return string.Empty; }
    }

    private static string TruncateChapterTitle(string value)
    {
        value = NormalizeTitle(value);
        return value.Length <= ChapterTitlePreviewMaxLength
            ? value
            : value[..ChapterTitlePreviewMaxLength].TrimEnd() + "…";
    }
}
