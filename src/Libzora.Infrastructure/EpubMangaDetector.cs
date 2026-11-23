using System.Text.RegularExpressions;
using System.Xml;
using Libzora.Layout;

namespace Libzora.Infrastructure;

public readonly record struct EpubMangaDetectionResult(bool IsManga, bool IsRightToLeft, bool IsLeftToRight);

/// <summary>
/// Fast heuristic for detecting image-based comic/manga EPUBs from OPF metadata
/// and a small sample of chapters without decoding full-size page images.
/// </summary>
public static partial class EpubMangaDetector
{
    private const int FastSampleThresholdBytes = 32 * 1024;
    private const int FastSampleProseTextStopChars = 12_000;

    public static EpubMangaDetectionResult Detect(EpubReaderDocument document)
    {
        var (prePaginated, rtl, ltr) = InspectOpf(document.RootPath);
        if (document.Chapters.Count == 0)
        {
            return new EpubMangaDetectionResult(false, rtl, ltr);
        }

        var start = document.Chapters.Count > 2 ? 1 : 0;
        var count = Math.Min(6, document.Chapters.Count - start);
        var imageBlocks = 0;
        var textChars = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var (chapterImages, chapterText) = CountChapterMediaFast(
                    document.Chapters[start + i],
                    earlyExitOnProse: !prePaginated);
                imageBlocks += chapterImages;
                textChars += chapterText;
                if (!prePaginated
                    && textChars >= FastSampleProseTextStopChars
                    && textChars > imageBlocks * 90)
                {
                    return new EpubMangaDetectionResult(false, rtl, ltr);
                }
            }
            catch
            {
                // Ignore a malformed sample chapter; other chapters decide cleanly.
            }
        }

        var isManga = (prePaginated && imageBlocks >= 1)
            || (imageBlocks >= 2 && textChars <= imageBlocks * 45);
        return new EpubMangaDetectionResult(isManga, rtl, ltr);
    }

    private static (int ImageBlocks, int TextChars) CountChapterMediaFast(
        string chapterPath,
        bool earlyExitOnProse)
    {
        var info = new FileInfo(chapterPath);
        if (info.Length <= FastSampleThresholdBytes)
        {
            return CountChapterMediaWithLoader(chapterPath);
        }

        try
        {
            using var stream = new FileStream(chapterPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            var imageBlocks = 0;
            var textChars = 0;
            var inBody = false;
            var sawBody = false;
            var skipDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (skipDepth >= 0)
                    {
                        continue;
                    }

                    var name = reader.LocalName;
                    if (name.Equals("body", StringComparison.OrdinalIgnoreCase))
                    {
                        inBody = true;
                        sawBody = true;
                        continue;
                    }

                    if (name.Equals("head", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("title", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("script", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!reader.IsEmptyElement)
                        {
                            skipDepth = reader.Depth;
                        }
                        continue;
                    }

                    if (!inBody && sawBody)
                    {
                        continue;
                    }

                    if (name.Equals("img", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("image", StringComparison.OrdinalIgnoreCase))
                    {
                        imageBlocks++;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (skipDepth >= 0 && reader.Depth <= skipDepth)
                    {
                        skipDepth = -1;
                        continue;
                    }

                    if (reader.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase))
                    {
                        inBody = false;
                    }
                }
                else if (skipDepth < 0
                    && inBody
                    && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    var value = reader.Value;
                    for (var index = 0; index < value.Length; index++)
                    {
                        if (!char.IsWhiteSpace(value[index]))
                        {
                            textChars++;
                        }
                    }

                    if (earlyExitOnProse
                        && textChars >= FastSampleProseTextStopChars
                        && textChars > imageBlocks * 90)
                    {
                        return (imageBlocks, textChars);
                    }
                }
            }

            return sawBody ? (imageBlocks, textChars) : CountChapterMediaWithLoader(chapterPath);
        }
        catch (XmlException)
        {
            return CountChapterMediaWithLoader(chapterPath);
        }
    }

    private static (int ImageBlocks, int TextChars) CountChapterMediaWithLoader(string chapterPath)
    {
        var imageBlocks = 0;
        var textChars = 0;
        var content = new XhtmlChapterLoader().Load(chapterPath);
        foreach (var block in content.Blocks)
        {
            if (block.Kind == BlockKind.Image) imageBlocks++;
        }
        foreach (var ch in content.BodyText)
        {
            if (!char.IsWhiteSpace(ch)) textChars++;
        }
        return (imageBlocks, textChars);
    }

    private static (bool PrePaginated, bool Rtl, bool Ltr) InspectOpf(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return default;
        }

        try
        {
            var opf = TryResolveOpfFromContainer(rootPath)
                ?? Directory.EnumerateFiles(rootPath, "*.opf", SearchOption.AllDirectories).FirstOrDefault();
            if (opf is null) return default;
            var xml = File.ReadAllText(opf);
            var prePaginated = xml.Contains("pre-paginated", StringComparison.OrdinalIgnoreCase);
            var dirMatch = PageProgressionRegex().Match(xml);
            var dir = dirMatch.Success ? dirMatch.Groups[1].Value : string.Empty;
            var rtl = string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase);
            var ltr = string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase);
            return (prePaginated, rtl, ltr);
        }
        catch
        {
            return default;
        }
    }

    private static string? TryResolveOpfFromContainer(string rootPath)
    {
        var containerPath = Path.Combine(rootPath, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            return null;

        try
        {
            var xml = File.ReadAllText(containerPath);
            var match = ContainerRootFileRegex().Match(xml);
            if (!match.Success)
                return null;

            var relative = Uri.UnescapeDataString(match.Groups[1].Value.Trim())
                .Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative))
                return null;

            var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(rootPath, relative));
            return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate)
                ? candidate
                : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"<rootfile\b[^>]*full-path\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerRootFileRegex();

    [GeneratedRegex(@"page-progression-direction\s*=\s*[""'](rtl|ltr)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex PageProgressionRegex();
}
