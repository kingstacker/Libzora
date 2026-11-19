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
/// HTML/CSS sanitisation and path containment.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private static async Task SanitizeHtmlFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(path, cancellationToken);
        var root = document.Root ?? throw new InvalidDataException("EPUB HTML 缺少根元素。");
        var isHtmlDocument = root.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase)
            || root.Descendants().Any(element => element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        var isSvgDocument = root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase);
        if (!isHtmlDocument && !isSvgDocument)
            return;
        var namespaceName = root.Name.Namespace;
        var elements = root.DescendantsAndSelf().ToArray();
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localName = element.Name.LocalName;
            if (localName is "script" or "object" or "iframe" or "frame" or "embed" or "applet" or "base")
            {
                element.Remove();
                continue;
            }

            if (localName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                element.Remove();
                continue;
            }

            EpubReaderImageReferenceNormalizer.NormalizeHtmlImageReferences(
                element,
                path,
                cacheRoot);

            foreach (var attribute in element.Attributes().ToArray())
            {
                var attributeName = attribute.Name.LocalName;
                if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attributeName is "background")
                {
                    attribute.Remove();
                    continue;
                }

                if (attributeName == "srcset")
                {
                    var sanitizedSrcSet = EpubReaderImageReferenceNormalizer.NormalizeSrcSetAttribute(
                        element,
                        path,
                        cacheRoot);
                    if (string.IsNullOrWhiteSpace(sanitizedSrcSet))
                        attribute.Remove();
                    else
                        attribute.Value = sanitizedSrcSet;
                    continue;
                }

                if (attributeName is "src" or "href" or "data-src" or "data-href"
                    or "action" or "poster" or "data"
                    or "cite" or "formaction" or "xlink:href")
                {
                    if (!IsSafeLocalReference(attribute.Value, path, cacheRoot))
                        attribute.Remove();
                }
                else if (attributeName == "style")
                {
                    var css = SanitizeCss(attribute.Value, path, cacheRoot);
                    if (string.IsNullOrWhiteSpace(css)) attribute.Remove();
                    else attribute.Value = css;
                }
            }

            // Some EPUBs use a remote 24x24 image as the complete label of a
            // local footnote link. Removing the unsafe URL while keeping the
            // now source-less <img> leaves a visible empty square in Chromium.
            // Preserve the note action with a compact text marker; discard
            // other source-less images instead of rendering broken placeholders.
            if (localName == "img" && !element.Attributes().Any(attribute =>
                    attribute.Name.LocalName is "src" or "xlink:href"
                    && !string.IsNullOrWhiteSpace(attribute.Value)))
            {
                var parent = element.Parent;
                if (parent is not null
                    && parent.Name.LocalName == "a"
                    && IsFootnoteReference(parent))
                {
                    element.ReplaceWith(
                        new XElement(
                            namespaceName + "sup",
                            new XAttribute("class", "libzora-footnote-marker"),
                            "注"));
                }
                else
                {
                    element.Remove();
                }
                continue;
            }

            var styleText = element.Name.LocalName == "style" ? element.Value : null;
            if (styleText is not null)
                element.Value = SanitizeCss(styleText, path, cacheRoot);
        }

        // Mark vertical inline units in the serialized XHTML itself. Single
        // digits get an explicit upright cell so their glyph direction and
        // baseline are independent of the surrounding mixed CJK text. The
        // bridge repeats this defensively for dynamically inserted content.
        MarkVerticalInlineRuns(root);

        var head = root.Elements().FirstOrDefault(element => element.Name.LocalName == "head");
        if (isHtmlDocument && head is null)
        {
            head = new XElement(namespaceName + "head");
            root.AddFirst(head);
        }

        head?.Elements()
             .Where(element => element.Name.LocalName == "meta"
                 && string.Equals(
                     element.Attribute("http-equiv")?.Value,
                     "Content-Security-Policy",
                     StringComparison.OrdinalIgnoreCase))
             .Remove();

        // The self-drawn engine renders the sanitized XHTML directly; the
        // WebView bridge script and its CSP nonce are no longer injected.
        await WriteXmlAsync(document, path, cancellationToken);
    }

    private static void MarkVerticalInlineRuns(XElement root)
    {
        var body = root.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null) return;
        // Preserve publication text nodes byte-for-byte. CSS Writing Modes
        // needs the original adjacent characters to shape vertical CJK,
        // punctuation, Latin and digits as a single native run.
        body.SetAttributeValue("data-libzora-vertical-inline-prepared", ExtractionFormatVersion);
    }

    private static bool IsFootnoteReference(XElement element)
    {
        if (IsFootnoteBacklink(element))
            return false;

        var metadata = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id" or "href")
                .Select(attribute => attribute.Value));
        return Regex.IsMatch(
            metadata,
            @"\b(noteref|doc-noteref|footnote|endnote|note[-_]?ref|fn[-_]?ref)\b|(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsFootnoteBacklink(XElement element)
    {
        var id = element.Attribute("id")?.Value?.Trim();
        var href = element.Attribute("href")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
            return false;

        var hash = href.IndexOf('#');
        if (hash < 0 || hash + 1 >= href.Length)
            return false;

        var fragment = href[(hash + 1)..];
        var query = fragment.IndexOfAny(['?', '#']);
        if (query >= 0) fragment = fragment[..query];
        return id.EndsWith("n", StringComparison.OrdinalIgnoreCase)
            && !fragment.EndsWith("n", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SanitizeCssFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var css = await File.ReadAllTextAsync(path, cancellationToken);
        var sanitized = SanitizeCss(css, path, cacheRoot);
        if (!string.Equals(css, sanitized, StringComparison.Ordinal))
            await File.WriteAllTextAsync(path, sanitized, Encoding.UTF8, cancellationToken);
    }

    private static string SanitizeCss(string css, string sourcePath, string cacheRoot)
    {
        var sanitized = CssImportPattern.Replace(css, string.Empty);
        return CssUrlPattern.Replace(sanitized, match =>
        {
            var value = match.Groups["value"].Value.Trim();
            return IsSafeLocalReference(value, sourcePath, cacheRoot) ? match.Value : string.Empty;
        });
    }

    private static bool IsSafeLocalReference(string value, string sourcePath, string cacheRoot) =>
        EpubReaderImageReferenceNormalizer.IsSafeLocalReference(value, sourcePath, cacheRoot);

    private static async Task WriteXmlAsync(
        XDocument document,
        string path,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
            document.Save(writer, SaveOptions.DisableFormatting);
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        EnsureContainedPath(root, fullPath);
        return fullPath;
    }

    // Manifest and navigation hrefs are relative to the document that
    // declares them, and legally climb out of the package directory: an OPF
    // under OEBPS/ may reference "../toc.ncx" at the archive root. Only the
    // extraction root is a real security boundary, so resolve against the
    // declaring document and verify containment against the cache root.
    private static string ResolvePublicationPath(
        string baseDirectory,
        string cacheRoot,
        string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
        EnsureContainedPath(cacheRoot, fullPath);
        return fullPath;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
    }
}
