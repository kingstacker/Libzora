using System.Xml.Linq;

namespace Libzora.Infrastructure;

public sealed partial class EpubReaderPreparationService
{
    private static async Task<EpubReaderDocument> NormalizeCoverChaptersAsync(
        EpubReaderDocument document,
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        if (document.Chapters.Count < 2) return document;
        var firstPath = document.Chapters[0];
        var first = await LoadXmlAsync(firstPath, cancellationToken);
        var coverImage = GetImageOnlyPageResource(first, firstPath, document.RootPath);
        if (coverImage is null) return document;

        var coverId = package.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "meta" && element.Attribute("name")?.Value == "cover")?
            .Attribute("content")?.Value;
        var declaredImages = manifest.Values.Where(item => item.Id == coverId
            || (item.Properties ?? string.Empty).Split(' ').Contains("cover-image"));
        var declaredPages = package.Descendants().Where(element =>
            element.Name.LocalName == "reference" && element.Attribute("type")?.Value == "cover")
            .Select(element => element.Attribute("href")?.Value);
        var normalizedRoot = NormalizeRootPath(document.RootPath);
        var declaredCover = declaredImages.Any(item => MatchesPublicationResource(item.Href, coverImage))
            || declaredPages.Any(href => MatchesPublicationResource(href, firstPath))
            || first.Descendants().Any(element => element.Name.LocalName == "meta"
                && element.Attribute("name")?.Value == "calibre:cover"
                && element.Attribute("content")?.Value == "true");
        if (!declaredCover) return document;

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicateCount = 0;
        for (var index = 1; index < document.Chapters.Count; index++)
        {
            var path = document.Chapters[index];
            var page = await LoadXmlAsync(path, cancellationToken);
            var image = GetImageOnlyPageResource(page, path, document.RootPath);
            if (image is null) break;
            if (!string.Equals(image, coverImage, StringComparison.Ordinal)
                && !string.Equals(await Hashing.Sha256Async(image, cancellationToken),
                    await Hashing.Sha256Async(coverImage, cancellationToken), StringComparison.Ordinal))
                break;
            duplicateCount++;
            if (path != firstPath) aliases[path] = firstPath;
        }
        if (duplicateCount == 0) return document;

        var chapters = new[] { firstPath }.Concat(document.Chapters.Skip(duplicateCount + 1)).ToList();
        var navigation = new List<EpubReaderNavigationItem>();
        var coverEntryAdded = false;
        foreach (var item in document.Navigation)
        {
            var isCover = item.ChapterIndex <= duplicateCount;
            if (isCover && coverEntryAdded) continue;
            coverEntryAdded |= isCover;
            navigation.Add(item with
            {
                ChapterIndex = isCover ? 0 : item.ChapterIndex - duplicateCount,
                Target = isCover ? new Uri(firstPath).AbsoluteUri : item.Target
            });
        }
        return document with
        {
            Chapters = chapters,
            Navigation = navigation,
            ChapterTitles = new[] { document.ChapterTitles[0] }
                .Concat(document.ChapterTitles.Skip(duplicateCount + 1)).ToList(),
            ChapterAliases = aliases,
            OriginalChapters = document.Chapters
        };

        bool MatchesPublicationResource(string? href, string path)
        {
            if (string.IsNullOrWhiteSpace(href)) return false;
            try
            {
                return string.Equals(path, ResolvePublicationPathNormalized(packageDirectory,
                    normalizedRoot, Uri.UnescapeDataString(href.Split('#')[0])), StringComparison.Ordinal);
            }
            catch (InvalidDataException) { return false; }
        }
    }

    private static string? GetImageOnlyPageResource(XDocument page, string path, string cacheRoot)
    {
        var body = page.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
        if (body is null || !string.IsNullOrWhiteSpace(body.Value)) return null;
        var elements = body.Descendants().ToArray();
        if (elements.Any(element => element.Name.LocalName is not
            ("div" or "p" or "span" or "a" or "section" or "figure" or "img" or "svg" or "image")))
            return null;
        var images = elements.Where(element => element.Name.LocalName is "img" or "image").ToArray();
        if (images.Length != 1) return null;
        var source = images[0].Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName == (images[0].Name.LocalName == "img" ? "src" : "href"))?.Value;
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            var resource = ResolvePublicationPathNormalized(Path.GetDirectoryName(path)!,
                NormalizeRootPath(cacheRoot), Uri.UnescapeDataString(source.Split('#')[0]));
            return File.Exists(resource) ? resource : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
