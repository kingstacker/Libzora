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
/// Physical TOC pages and navigation merging.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private static int GetMetadataConcurrency() =>
        Math.Clamp(Environment.ProcessorCount, 1, 4);

    private static async Task<List<EpubReaderNavigationItem>> ReadNavigationAsync(
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        var navigation = new List<EpubReaderNavigationItem>();
        var hasNavigation = false;
        var navItem = manifest.Values.FirstOrDefault(item =>
            HasToken(item.Properties, "nav"));
        if (navItem is not null)
        {
            var navPath = ResolvePublicationPath(
                packageDirectory,
                cacheRoot,
                Uri.UnescapeDataString(navItem.Href!.Split('#')[0]));
            if (File.Exists(navPath))
            {
                var navDocument = await LoadXmlAsync(navPath, cancellationToken);
                var navElements = navDocument.Descendants()
                    .Where(element => element.Name.LocalName.Equals("nav", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var explicitToc = navElements
                    .Where(IsTocNavigationElement)
                    .Select(element => CreateNavigationItems(
                        GetNavigationLinks(element),
                        navPath,
                        cacheRoot,
                        chapterIndexByPath))
                    .OrderByDescending(items => items.Count)
                    .FirstOrDefault(items => items.Count > 0);
                if (explicitToc is not null)
                {
                    navigation = explicitToc;
                    hasNavigation = true;
                }

                if (!hasNavigation)
                {
                    var inferredToc = navElements
                        .Where(element => !IsKnownNonTocNavigationElement(element))
                        .Select(element => CreateNavigationItems(
                            GetNavigationLinks(element),
                            navPath,
                            cacheRoot,
                            chapterIndexByPath))
                        .OrderByDescending(items => items.Count)
                        .FirstOrDefault(items => items.Count > 0);
                    if (inferredToc is not null)
                    {
                        navigation = inferredToc;
                        hasNavigation = true;
                    }
                }
            }
        }

        // EPUB 2 offers two navigation sources and neither is reliably the
        // better one: the NCX is the only source that carries nesting, but
        // some producers ship an NCX whose targets are wrong while the page
        // named by <guide type="toc"> is correct. Read both and keep the one
        // that reaches more of the book, preferring the NCX on a tie so its
        // hierarchy survives.
        var ncxItems = new List<EpubReaderNavigationItem>();
        var ncxCollisions = 0;
        if (!hasNavigation)
        {
            var spineTocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?
                .Attribute("toc")?.Value;
            if (spineTocId is not null && manifest.TryGetValue(spineTocId, out var ncxItem))
            {
                var ncxPath = ResolvePublicationPath(
                    packageDirectory,
                    cacheRoot,
                    Uri.UnescapeDataString(ncxItem.Href!.Split('#')[0]));
                if (File.Exists(ncxPath))
                {
                    var ncx = await LoadXmlAsync(ncxPath, cancellationToken);
                    var navMap = ncx.Descendants()
                        .FirstOrDefault(element => element.Name.LocalName == "navMap");
                    ncxItems = CreateNavigationItems(
                        navMap is null ? [] : ReadNcxNavigationLinks(navMap),
                        ncxPath,
                        cacheRoot,
                        chapterIndexByPath,
                        out ncxCollisions);
                }
            }
        }

        if (!hasNavigation)
        {
            var guideToc = package.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("reference", StringComparison.OrdinalIgnoreCase)
                    && HasToken(GetAttributeValue(element, "type"), "toc"));
            var guideHref = GetAttributeValue(guideToc, "href");
            if (!string.IsNullOrWhiteSpace(guideHref))
            {
                var guidePathPart = guideHref.Split('#', 2)[0].Split('?', 2)[0];
                var guidePath = ResolvePublicationPath(
                    packageDirectory,
                    cacheRoot,
                    Uri.UnescapeDataString(guidePathPart));
                if (File.Exists(guidePath))
                {
                    var guideDocument = await LoadXmlAsync(guidePath, cancellationToken);
                    var guideItems = CreateNavigationItems(
                        GetNavigationLinks(guideDocument.Root),
                        guidePath,
                        cacheRoot,
                        chapterIndexByPath,
                        out var guideCollisions);
                    if (IsBetterNavigationSource(
                            guideItems,
                            guideCollisions,
                            ncxItems,
                            ncxCollisions))
                    {
                        navigation = guideItems;
                        hasNavigation = guideItems.Count > 0;
                        ncxItems = [];
                    }
                }
            }
        }

        if (!hasNavigation && ncxItems.Count > 0)
        {
            navigation = ncxItems;
            hasNavigation = true;
        }

        return await MergePhysicalTocNavigationAsync(
            navigation,
            cacheRoot,
            chapters,
            chapterIndexByPath,
            cancellationToken);
    }

    private static async Task<List<EpubReaderNavigationItem>> MergePhysicalTocNavigationAsync(
        List<EpubReaderNavigationItem> navigation,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        // Once the authoritative TOC already reaches every spine document,
        // a second pass over every chapter cannot add a readable chapter. It
        // is especially expensive for large novels whose NCX lists every
        // chapter, so keep the physical-TOC compatibility pass only for
        // incomplete navigation sources.
        if (navigation.Count > 0
            && CountDistinctChapters(navigation) >= chapters.Count)
        {
            return navigation;
        }

        var tocPages = await ReadPhysicalTocPagesAsync(
            cacheRoot,
            chapters,
            navigation,
            chapterIndexByPath,
            cancellationToken);
        if (tocPages.Count == 0)
            return navigation;

        var result = new List<EpubReaderNavigationItem>(navigation);
        var keys = new HashSet<string>(
            result.Select(GetNavigationKey),
            StringComparer.OrdinalIgnoreCase);

        // The first physical TOC is the book-level directory. Sub-TOC pages
        // are content sources only: their chapter links are added below, but
        // the pages themselves should not create repeated "目录" rows.
        var rootTocPage = tocPages[0];
        // Some EPUBs already publish the root TOC in NCX/guide, often with a
        // heading fragment. It is still the same physical page, so do not add
        // a second synthetic page-level entry just because the fragments differ.
        if (!result.Any(item => item.ChapterIndex == rootTocPage.ChapterIndex))
        {
            AddUniqueNavigationItem(
                result,
                keys,
                new EpubReaderNavigationItem(
                    rootTocPage.Title,
                    new Uri(chapters[rootTocPage.ChapterIndex]).AbsoluteUri,
                    rootTocPage.ChapterIndex));
        }

        foreach (var tocPage in tocPages)
        {
            // A TOC page that only repeats chapters the navigation already
            // reaches contributes nothing: its links normally carry different
            // anchors, so they survive deduplication and double the rail while
            // flattening the nesting the NCX supplied. Merge a page only when
            // it opens chapters the navigation misses, which is what makes
            // per-volume sub-TOC pages worth reading.
            var covered = result.Select(item => item.ChapterIndex).ToHashSet();
            if (tocPage.Items.All(item => covered.Contains(item.ChapterIndex)))
                continue;

            foreach (var item in tocPage.Items)
                AddUniqueNavigationItem(result, keys, item);
        }

        return OrderNavigationTree(result);
    }

    // Ordering has to respect nesting: a part heading shares its chapter index
    // with its first child, so a flat sort by chapter index would scatter
    // parents into the middle of their own subtrees. Sort the top-level
    // entries and carry each subtree along intact. A flat list (no entry above
    // level 0) degenerates to exactly the previous chapter-index ordering.
    internal static List<EpubReaderNavigationItem> OrderNavigationTree(
        IReadOnlyList<EpubReaderNavigationItem> items)
    {
        if (items.Count == 0) return [];

        var roots = new List<(int SortKey, int Order, List<EpubReaderNavigationItem> Subtree)>();
        for (var index = 0; index < items.Count;)
        {
            var subtree = new List<EpubReaderNavigationItem> { items[index] };
            var rootLevel = items[index].Level;
            var next = index + 1;
            while (next < items.Count && items[next].Level > rootLevel)
                subtree.Add(items[next++]);

            roots.Add((subtree.Min(item => item.ChapterIndex), roots.Count, subtree));
            index = next;
        }

        return roots
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.Order)
            .SelectMany(entry => entry.Subtree)
            .ToList();
    }
}
