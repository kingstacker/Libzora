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

// Level is the entry's depth in the book's own table of contents: 0 for a
// top-level part or chapter, 1 for its children, and so on. The reader rail
// indents and folds by this value, so it must survive extraction rather than
// being flattened away.
public sealed record EpubReaderNavigationItem(
    string Title,
    string Target,
    int ChapterIndex,
    int Level = 0);

public sealed record EpubReaderDocument(
    string RootPath,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<EpubReaderNavigationItem> Navigation,
    IReadOnlyList<string> ChapterTitles)
{
    public IReadOnlyDictionary<string, string> ChapterAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string>? OriginalChapters { get; init; }
}

/// <summary>
/// Preparation entry point, limits and security policy.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private const long MaximumEpubEntryBytes = 256L * 1024 * 1024;
    private const long MaximumEpubExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumEpubEntries = 100_000;
    private const string ExtractionReadyFileName = ".libzora-extracted";
    private const string ReaderIndexFileName = ".libzora-reader-index.json";
    private const string ReaderIndexFormatVersion = "4";
    private const int PhysicalTocFullScanChapterLimit = 256;
    // Bump whenever sanitization changes. Existing reader caches otherwise
    // keep stale sanitized markup indefinitely.
    private const string ExtractionFormatVersion = "71";
    private const string ContentSecurityPolicyBase =
        "default-src 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'; " +
        "connect-src 'none'; form-action 'none'; img-src 'self' file:; " +
        "font-src 'self' file: data:; style-src 'self' 'unsafe-inline' file:; " +
        "media-src 'none'; worker-src 'none'; frame-ancestors 'none';";

    private static readonly Regex CssUrlPattern = new(
        """url\s*\(\s*(?<quote>['"]?)(?<value>[^)'"]+)\k<quote>\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CssImportPattern = new(
        "@import\\s+[^;]+;?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HtmlNamedEntityPattern = new(
        "&[A-Za-z][A-Za-z0-9]+;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HiddenContentPattern = new(
        @"<(script|style|noscript|svg|math|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BlockBreakPattern = new(
        @"<(br\s*/?|/p|/div|/li|/h[1-6]|/section|/tr|/blockquote)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(
        "<[^>]*>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly XNamespace XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private readonly AppPaths _paths;

    private static readonly JsonSerializerOptions ReaderIndexJsonOptions = new(JsonSerializerDefaults.Web);

    public EpubReaderPreparationService(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<EpubReaderDocument> PrepareAsync(
        string epubPath,
        string sha256,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => PrepareCoreAsync(epubPath, sha256, cancellationToken), cancellationToken);

    private async Task<EpubReaderDocument> PrepareCoreAsync(
        string epubPath,
        string sha256,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Concat(sha256.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (cacheKey.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");

        var cacheRoot = Path.GetFullPath(Path.Combine(_paths.ReaderCache, cacheKey));
        EnsureContainedPath(_paths.ReaderCache, cacheRoot);
        Directory.CreateDirectory(cacheRoot);

        var extractionReadyPath = Path.Combine(cacheRoot, ExtractionReadyFileName);
        var extractionReady = await IsExtractionReadyAsync(
            extractionReadyPath,
            cacheKey,
            cancellationToken);
        if (extractionReady
            && await TryLoadReaderIndexAsync(cacheRoot, cancellationToken) is { } cachedDocument)
        {
            return cachedDocument;
        }

        if (!extractionReady)
        {
            TryDeleteDirectory(cacheRoot);
            Directory.CreateDirectory(cacheRoot);
            // Re-extract on every format-version mismatch. Re-sanitizing an
            // already transformed cache cannot restore content removed by an
            // older sanitizer and would leave bridge changes version-skewed.
            try
            {
                var sanitizedDuringExtract = await ExtractSafelyAsync(epubPath, cacheRoot, cancellationToken);
                if (!sanitizedDuringExtract)
                    await SanitizeExtractedResourcesAsync(cacheRoot, cancellationToken);
                await File.WriteAllTextAsync(
                    extractionReadyPath,
                    $"{cacheKey}\n{ExtractionFormatVersion}",
                    Encoding.UTF8,
                    cancellationToken);
            }
            catch
            {
                TryDeleteDirectory(cacheRoot);
                throw;
            }
        }

        var containerPath = Path.Combine(cacheRoot, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");

        var container = await LoadXmlAsync(containerPath, cancellationToken);
        var packageRelativePath = container
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
            .Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(packageRelativePath))
            throw new InvalidDataException("EPUB 没有声明内容清单。");

        var packagePath = ResolveContainedPath(cacheRoot, packageRelativePath);
        if (!File.Exists(packagePath))
            throw new InvalidDataException("EPUB 内容清单不存在。");

        var package = await LoadXmlAsync(packagePath, cancellationToken);
        var manifest = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(element => new ManifestItem(
                element.Attribute("id")?.Value,
                element.Attribute("href")?.Value,
                element.Attribute("media-type")?.Value,
                element.Attribute("properties")?.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            // Some malformed EPUBs repeat a manifest id. Keep the first
            // declaration deterministically instead of throwing from
            // ToDictionary and failing the whole reader load.
            .GroupBy(item => item.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        var normalizedCacheRoot = NormalizeRootPath(cacheRoot);
        var chapters = new List<string>();
        foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idRef = itemRef.Attribute("idref")?.Value;
            if (idRef is null || !manifest.TryGetValue(idRef, out var item)) continue;
            if (item.MediaType is not ("application/xhtml+xml" or "text/html")) continue;

            var href = Uri.UnescapeDataString(item.Href!.Split('#')[0]);
            var chapterPath = ResolvePublicationPathNormalized(packageDirectory, normalizedCacheRoot, href);
            if (File.Exists(chapterPath)) chapters.Add(chapterPath);
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("EPUB 没有可阅读的章节。");

        var chapterIndexByPath = CreateChapterIndexLookup(chapters);

        var navigation = await ReadNavigationAsync(
            package,
            manifest,
            packageDirectory,
            cacheRoot,
            chapters,
            chapterIndexByPath,
            cancellationToken);
        var hasAuthoritativeNavigation = navigation.Count > 0;
        if (navigation.Count == 0)
        {
            navigation = chapters
                .Select((chapter, index) => new EpubReaderNavigationItem(
                    $"第 {index + 1} 章",
                    new Uri(chapter).AbsoluteUri,
                    index))
                .ToList();
        }

        var authoritativeNavigation = hasAuthoritativeNavigation ? navigation : [];
        var chapterTitles = await ReadChapterTitlesAsync(
            chapters,
            authoritativeNavigation,
            cancellationToken);

        await ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(
            chapters,
            chapterTitles,
            authoritativeNavigation,
            cancellationToken);

        var document = await NormalizeCoverChaptersAsync(
            new EpubReaderDocument(cacheRoot, chapters, navigation, chapterTitles),
            package, manifest, packageDirectory, cancellationToken);
        await WriteReaderIndexAsync(cacheRoot, document, cancellationToken);
        return document;
    }

    private sealed record PhysicalTocPage(
        int ChapterIndex,
        string Title,
        IReadOnlyList<EpubReaderNavigationItem> Items);

    private static IReadOnlyDictionary<string, int> CreateChapterIndexLookup(
        IReadOnlyList<string> chapters)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < chapters.Count; index++)
        {
            var path = Path.GetFullPath(chapters[index]);
            // Match the previous FindIndex behavior when a malformed EPUB
            // repeats one spine document: the first occurrence wins.
            result.TryAdd(path, index);
        }

        return result;
    }
}
