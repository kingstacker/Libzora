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
/// Navigation tree building and NCX/NAV parsing.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private static async Task<List<PhysicalTocPage>> ReadPhysicalTocPagesAsync(
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        var candidateIndexes = GetPhysicalTocCandidateIndexes(
            chapters,
            navigation,
            chapterIndexByPath);
        var pages = new PhysicalTocPage?[candidateIndexes.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidateIndexes.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (resultIndex, token) =>
            {
                var chapterIndex = candidateIndexes[resultIndex];
                try
                {
                    var document = await LoadXmlAsync(chapters[chapterIndex], token);
                    var links = GetNavigationLinks(document.Root).ToArray();
                    if (!IsPhysicalTocPage(document, links))
                        return;

                    var items = CreateNavigationItems(
                        links,
                        chapters[chapterIndex],
                        cacheRoot,
                        chapterIndexByPath);
                    if (items.Count == 0)
                        return;

                    pages[resultIndex] = new PhysicalTocPage(
                        chapterIndex,
                        GetPhysicalTocTitle(document),
                        items);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A broken auxiliary XHTML page must not prevent the rest
                    // of the EPUB from opening or erase its NCX navigation.
                }
            });

        return pages
            .Where(page => page is not null)
            .Select(page => page!)
            .OrderBy(page => page.ChapterIndex)
            .ToList();
    }

    private static IReadOnlyList<int> GetPhysicalTocCandidateIndexes(
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        IReadOnlyDictionary<string, int> chapterIndexByPath)
    {
        if (chapters.Count <= PhysicalTocFullScanChapterLimit)
            return Enumerable.Range(0, chapters.Count).ToArray();

        var candidates = new HashSet<int>();
        var earlyLimit = Math.Min(PhysicalTocFullScanChapterLimit, chapters.Count);
        foreach (var item in navigation)
        {
            if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                || !target.IsFile
                || !chapterIndexByPath.TryGetValue(
                    Path.GetFullPath(target.LocalPath),
                    out var chapterIndex))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(chapters[chapterIndex]);
            if (chapterIndex < earlyLimit
                || IsPhysicalTocFileName(fileName)
                || IsTocHeading(item.Title))
            {
                candidates.Add(chapterIndex);
            }
        }

        // Physical TOCs are normally named explicitly. Keep the early spine
        // window as a compatibility fallback for publishers that use generic
        // filenames, while avoiding a full XML parse of a 10k-chapter novel.
        for (var chapterIndex = 0; chapterIndex < earlyLimit; chapterIndex++)
            candidates.Add(chapterIndex);

        for (var chapterIndex = 0; chapterIndex < chapters.Count; chapterIndex++)
        {
            var fileName = Path.GetFileNameWithoutExtension(chapters[chapterIndex]);
            if (IsPhysicalTocFileName(fileName))
                candidates.Add(chapterIndex);
        }

        return candidates.OrderBy(index => index).ToArray();
    }

    private static bool IsPhysicalTocFileName(string? value)
    {
        var compact = new string((value ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character)
                || character is >= '\u3400' and <= '\u9fff')
            .ToArray());
        return compact.Contains("toc", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("contents", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("tableofcontents", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("目录", StringComparison.Ordinal)
            || compact.Contains("目錄", StringComparison.Ordinal)
            || compact.Contains("目次", StringComparison.Ordinal);
    }

    private static bool IsPhysicalTocPage(
        XDocument document,
        IReadOnlyCollection<(string Title, string? Href, int Level)> links)
    {
        if (links.Count < 2)
            return false;
        if (document.Descendants().Any(IsTocNavigationElement))
            return true;

        return document.Descendants()
            .Where(element => element.Name.LocalName is
                "title" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                or "p" or "div" or "section")
            .Any(element => IsTocHeading(GetGeneratedAwareText(element)));
    }

    private static string GetPhysicalTocTitle(XDocument document)
    {
        var visibleTitle = document.Descendants()
            .Where(element => element.Name.LocalName is
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                or "p" or "div" or "section")
            .Select(GetGeneratedAwareText)
            .FirstOrDefault(IsTocHeading);
        if (!string.IsNullOrWhiteSpace(visibleTitle))
            return visibleTitle;

        return document.Descendants()
            .Where(element => element.Name.LocalName == "title")
            .Select(GetGeneratedAwareText)
            .FirstOrDefault(IsTocHeading)
            ?? "目录";
    }

    private static bool IsTocHeading(string value)
    {
        var compact = new string(value
            .Where(character => char.IsLetterOrDigit(character)
                || character is >= '\u3400' and <= '\u9fff')
            .ToArray());
        return compact.Equals("目录", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目录页", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目錄", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目錄頁", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("目次", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("contents", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("tableofcontents", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDistinctChapters(IReadOnlyList<EpubReaderNavigationItem> items) =>
        items.Select(item => item.ChapterIndex).Distinct().Count();

    // Reaching more of the book decides it. Only when both sources open the
    // same number of chapters does the collision count break the tie, and an
    // exact tie keeps the NCX so its nesting survives.
    private static bool IsBetterNavigationSource(
        IReadOnlyList<EpubReaderNavigationItem> candidate,
        int candidateCollisions,
        IReadOnlyList<EpubReaderNavigationItem> incumbent,
        int incumbentCollisions)
    {
        if (candidate.Count == 0) return false;
        if (incumbent.Count == 0) return true;

        var candidateChapters = CountDistinctChapters(candidate);
        var incumbentChapters = CountDistinctChapters(incumbent);
        if (candidateChapters != incumbentChapters)
            return candidateChapters > incumbentChapters;
        return candidateCollisions < incumbentCollisions;
    }

    private static string GetNavigationKey(EpubReaderNavigationItem item)
    {
        if (Uri.TryCreate(item.Target, UriKind.Absolute, out var target))
            return $"{item.ChapterIndex}\0{target.Fragment}\0{item.Level}";
        return $"{item.ChapterIndex}\0{item.Target}\0{item.Level}";
    }

    private static void AddUniqueNavigationItem(
        List<EpubReaderNavigationItem> items,
        HashSet<string> keys,
        EpubReaderNavigationItem item)
    {
        if (keys.Add(GetNavigationKey(item)))
        {
            items.Add(item);
            return;
        }
    }

    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href, int Level)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyDictionary<string, int> chapterIndexByPath) =>
        CreateNavigationItems(source, navigationDocumentPath, cacheRoot, chapterIndexByPath, out _);

    // collidedEntries counts entries discarded because another entry already
    // claimed the same target. A navigation source that keeps pointing several
    // labels at one location is describing the book worse than one that does
    // not, which is the tie-breaker between an EPUB 2 NCX and its guide page.
    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href, int Level)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        out int collidedEntries)
    {
        collidedEntries = 0;
        var result = new List<EpubReaderNavigationItem>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (title, href, level) in source)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;
            if (IsFootnoteNavigationEntry(title, href)) continue;
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && !absolute.IsFile) continue;

            var parts = href.Split('#', 2);
            var pathPart = parts[0].Split('?', 2)[0];
            var targetPath = pathPart.Length == 0
                ? navigationDocumentPath
                : ResolvePublicationPath(
                    Path.GetDirectoryName(navigationDocumentPath)!,
                    cacheRoot,
                    Uri.UnescapeDataString(pathPart));
            if (!chapterIndexByPath.TryGetValue(Path.GetFullPath(targetPath), out var chapterIndex)
                || !File.Exists(targetPath))
            {
                continue;
            }

            var target = new Uri(targetPath).AbsoluteUri;
            if (parts.Length == 2 && parts[1].Length > 0) target += $"#{parts[1]}";
            var fragmentKey = parts.Length == 2 ? DecodeNavigationFragment(parts[1]) : string.Empty;
            // A part heading and its first chapter routinely share one target.
            // Keying on depth as well keeps the child entry that a purely
            // positional key would silently drop.
            if (!targets.Add($"{chapterIndex}\0{fragmentKey}\0{level}"))
            {
                collidedEntries++;
                continue;
            }

            result.Add(new EpubReaderNavigationItem(title, target, chapterIndex, level));
        }
        return result;
    }

    // The bilingual translator keeps the original link text in <a> and adds
    // the translated title as a sibling span. Reading anchor.Value alone
    // therefore loses the translation in Kreader's own TOC even though it is
    // present in nav.xhtml. Keep the two lines together for the reader UI.
    private static string GetNavigationTitle(XElement anchor)
    {
        var original = GetGeneratedAwareText(anchor);
        var translated = anchor.Parent?
            .Elements()
            .Where(IsGeneratedTranslationElement)
            .Select(element => NormalizeTitle(element.Value))
            .FirstOrDefault(value => value.Length > 0);

        translated ??= anchor.Descendants()
            .Where(IsGeneratedTranslationElement)
            .Select(element => NormalizeTitle(element.Value))
            .FirstOrDefault(value => value.Length > 0);

        if (string.IsNullOrWhiteSpace(translated)) return original;
        if (string.IsNullOrWhiteSpace(original)) return translated;
        return $"{original}{Environment.NewLine}{translated}";
    }

    private static string GetGeneratedAwareText(XElement element) =>
        NormalizeTitle(string.Concat(
            element
                .DescendantNodes()
                .OfType<XText>()
                .Where(node => !node.Ancestors().Any(IsGeneratedTranslationElement))
                .Select(node => node.Value)));

    private static bool IsGeneratedTranslationElement(XElement element) =>
        HasToken(GetAttributeValue(element, "class"), "libzora-translation");

    private static IEnumerable<(string Title, string? Href, int Level)> GetNavigationLinks(XElement? navigation)
    {
        if (navigation is null) return [];
        var anchors = navigation.Descendants()
            .Where(element => element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
            .Where(element => !IsFootnoteLink(element))
            .ToArray();
        if (anchors.Length == 0) return [];

        // Nesting depth comes from the enclosing list elements, which is how
        // both EPUB 3 nav documents and hand-authored TOC pages express
        // hierarchy. Normalize against the shallowest link so a TOC wrapped in
        // an extra <ol> still starts at level 0.
        var depths = anchors.Select(anchor => CountListAncestors(anchor, navigation)).ToArray();
        var baseline = depths.Min();
        return anchors.Select((anchor, index) => (
            Title: GetNavigationTitle(anchor),
            Href: anchor.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value,
            Level: ClampNavigationLevel(depths[index] - baseline)));
    }

    private const int MaximumNavigationLevel = 4;

    private static int ClampNavigationLevel(int level) =>
        Math.Clamp(level, 0, MaximumNavigationLevel);

    private static int CountListAncestors(XElement element, XElement root)
    {
        var depth = 0;
        for (var ancestor = element.Parent; ancestor is not null && ancestor != root; ancestor = ancestor.Parent)
        {
            if (ancestor.Name.LocalName is "ol" or "ul") depth++;
        }
        return depth;
    }

    // NCX hierarchy lives in navPoint nesting. Walk it depth-first so the
    // emitted order matches the printed table of contents.
    private static IEnumerable<(string Title, string? Href, int Level)> ReadNcxNavigationLinks(
        XElement container,
        int level = 0)
    {
        foreach (var navPoint in container.Elements()
            .Where(element => element.Name.LocalName == "navPoint"))
        {
            var title = navPoint.Elements()
                .FirstOrDefault(child => child.Name.LocalName == "navLabel")?
                .Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "text")?.Value;
            var href = navPoint.Elements().FirstOrDefault(child => child.Name.LocalName == "content")?
                .Attribute("src")?.Value;
            yield return (NormalizeTitle(title), href, ClampNavigationLevel(level));

            foreach (var child in ReadNcxNavigationLinks(navPoint, level + 1))
                yield return child;
        }
    }

    private static bool IsFootnoteLink(XElement element)
    {
        if (HasToken(GetAttributeValue(element, "rel"), "footnote")
            || HasToken(GetAttributeValue(element, "rel"), "noteref")
            || HasToken(GetAttributeValue(element, "role"), "doc-noteref")
            || HasToken(GetAttributeValue(element, "type"), "noteref"))
        {
            return true;
        }

        var id = GetAttributeValue(element, "id");
        var href = GetAttributeValue(element, "href");
        var fragment = href?.Split('#', 2).ElementAtOrDefault(1);
        return IsFootnoteMarker(element.Value)
            && (LooksLikeFootnoteIdentifier(id) || LooksLikeFootnoteIdentifier(fragment)
                || LooksLikeNumberedNoteIdentifier(id) || LooksLikeNumberedNoteIdentifier(fragment));
    }

    private static bool IsFootnoteNavigationEntry(string title, string href)
    {
        var fragment = href.Split('#', 2).ElementAtOrDefault(1);
        return LooksLikeFootnoteIdentifier(fragment)
            || (IsFootnoteMarker(title)
                && (LooksLikeNumberedNoteIdentifier(fragment)
                    || LooksLikeFootnoteIdentifier(Path.GetFileNameWithoutExtension(
                        href.Split('#', 2)[0].Split('?', 2)[0]))));
    }

    private static bool IsFootnoteMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(
            value.Trim(),
            @"^(?:\[\s*\d+\s*\]|［\s*\d+\s*］|〔\s*\d+\s*〕)$",
            RegexOptions.CultureInvariant);

    // Generic id1/id1a anchors are notes only when paired with a note marker.
    private static bool LooksLikeNumberedNoteIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value.Trim(), @"^id\d+[a-z]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool LooksLikeFootnoteIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(
            value.Trim(),
            @"^(?:note|notes|fn|footnote|footnotes)[-_]?\d+[a-z]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsTocNavigationElement(XElement element) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase)
            && HasToken(attribute.Value, "toc"))
        || HasToken(element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))?.Value, "doc-toc")
        || HasTocHint(GetAttributeValue(element, "id"))
        || HasTocHint(GetAttributeValue(element, "class"));

    private static bool IsKnownNonTocNavigationElement(XElement element)
    {
        var metadata = element.Attributes()
            .Where(attribute => new[] { "type", "role", "id", "class" }.Any(name =>
                attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(attribute => attribute.Value);
        return metadata.Any(value => Regex.IsMatch(
            value,
            @"(?:^|[\s_-])(landmarks?|page[-_]?list|doc[-_]?pagelist)(?:$|[\s_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetAttributeValue(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool HasTocHint(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Regex.IsMatch(
            value,
            @"(?:^|[_-])sigil[_-]toc[_-]id(?:[_-]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && Regex.IsMatch(
            value,
            @"(?:^|[\s_-])(toc|table[-_]?of[-_]?contents?)(?:$|[\s_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DecodeNavigationFragment(string fragment)
    {
        try { return Uri.UnescapeDataString(fragment); }
        catch { return fragment; }
    }
}
