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
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] VerticalInlinePreparedAttributeUtf8 =
        Encoding.UTF8.GetBytes($" data-libzora-vertical-inline-prepared=\"{ExtractionFormatVersion}\"");

    private static void SanitizeHtmlBytesToFile(
        byte[] rawBytes,
        int length,
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            if (TrySanitizeCleanHtmlBytesToFile(rawBytes, length, path, cacheRoot, cancellationToken))
                return;

            var document = LoadXmlFromBytes(rawBytes, length);
            if (!SanitizeHtmlDocument(document, path, cacheRoot, cancellationToken))
            {
                WriteBytesSync(path, rawBytes.AsSpan(0, length));
                return;
            }

            WriteXmlSync(document, path);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException)
        {
        }

        var markup = DecodeTextFromBytes(rawBytes, length);
        WriteXmlSync(BuildDegradedTextDocument(markup), path);
    }

    private static bool TrySanitizeCleanHtmlBytesToFile(
        byte[] rawBytes,
        int length,
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        if (length < 16)
            return false;

        // Only handle UTF-8 streams directly; UTF-16/32 BOM or explicit non-UTF-8
        // encoding declarations fall back to XDocument so output normalizes to UTF-8.
        var span = rawBytes.AsSpan(0, length);
        var startOffset = 0;
        if (length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            startOffset = 3;
        }
        else if ((span[0] == 0xFE && span[1] == 0xFF)
            || (span[0] == 0xFF && span[1] == 0xFE)
            || span[0] == 0x00)
        {
            return false;
        }

        var headerSample = Encoding.ASCII.GetString(span.Slice(startOffset, Math.Min(160, length - startOffset)));
        var encIdx = headerSample.IndexOf("encoding=", StringComparison.OrdinalIgnoreCase);
        if (encIdx >= 0)
        {
            var rest = headerSample.AsSpan(encIdx + "encoding=".Length).TrimStart();
            if (rest.Length == 0) return false;
            var quote = rest[0];
            if (quote is not ('"' or '\'')) return false;
            rest = rest[1..];
            var closeQuote = rest.IndexOf(quote);
            if (closeQuote < 0 || !rest[..closeQuote].Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var isHtml = false;
        var hasRoot = false;
        var sawHead = false;
        var bodyCount = 0;
        try
        {
            using var stream = new MemoryStream(rawBytes, 0, length, writable: false);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings(asynchronous: false));
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType is XmlNodeType.EntityReference or XmlNodeType.DocumentType)
                    return false;
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var localName = reader.LocalName;
                if (!hasRoot)
                {
                    hasRoot = true;
                    if (!localName.Equals("html", StringComparison.OrdinalIgnoreCase))
                        return false;
                    isHtml = true;
                }

                if (IsSlowSanitizeElement(localName))
                    return false;

                if (localName.Equals("head", StringComparison.OrdinalIgnoreCase))
                    sawHead = true;
                else if (localName.Equals("body", StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.IsEmptyElement || ++bodyCount != 1 || !sawHead)
                        return false;
                }

                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        var attrName = reader.LocalName;
                        if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                            || attrName.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("background", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("srcset", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("style", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (attrName.Equals("src", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("href", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("action", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("poster", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("data", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("cite", StringComparison.OrdinalIgnoreCase)
                            || attrName.Equals("formaction", StringComparison.OrdinalIgnoreCase)
                            || reader.Name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!IsSafeLocalReference(reader.Value, path, cacheRoot))
                                return false;
                        }
                    }
                }
            }
        }
        catch (XmlException)
        {
            return false;
        }

        if (!isHtml || !sawHead || bodyCount != 1)
            return false;

        var contentSpan = span[startOffset..];
        var bodyOpenIndex = FindCleanBodyOpenEndOffset(contentSpan);
        if (bodyOpenIndex < 0)
            return false;

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 32768);
        output.Write(contentSpan[..bodyOpenIndex]);
        output.Write(VerticalInlinePreparedAttributeUtf8);
        output.Write(contentSpan[bodyOpenIndex..]);
        return true;
    }

    private static bool IsSlowSanitizeElement(string localName) =>
        localName.Equals("script", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("object", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("iframe", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("frame", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("embed", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("applet", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("base", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("meta", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("style", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("img", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("source", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("image", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("svg", StringComparison.OrdinalIgnoreCase);

    private static int FindCleanBodyOpenEndOffset(ReadOnlySpan<byte> utf8)
    {
        var bodyTag = "<body"u8;
        var idx = IndexOfAsciiCaseInsensitive(utf8, bodyTag);
        if (idx < 0)
            return -1;

        var prefix = utf8[..idx];
        if (prefix.IndexOf("<!--"u8) >= 0 || prefix.IndexOf("<![CDATA["u8) >= 0)
            return -1;

        var afterTag = idx + bodyTag.Length;
        if (afterTag >= utf8.Length)
            return -1;

        var next = utf8[afterTag];
        if (next is not ((byte)'>' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            return -1;

        return afterTag;
    }

    private static int IndexOfAsciiCaseInsensitive(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var limit = haystack.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                var b = haystack[i + j];
                if (b is >= (byte)'A' and <= (byte)'Z')
                    b = (byte)(b + 32);
                if (b != needle[j])
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
                return i;
        }
        return -1;
    }

    private static void SanitizeCssBytesToFile(
        byte[] rawBytes,
        int length,
        string path,
        string cacheRoot)
    {
        var css = DecodeTextFromBytes(rawBytes, length);
        var sanitized = SanitizeCss(css, path, cacheRoot);
        if (string.Equals(css, sanitized, StringComparison.Ordinal))
        {
            WriteBytesSync(path, rawBytes.AsSpan(0, length));
            return;
        }

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 32768);
        using var writer = new StreamWriter(output, Utf8NoBom);
        writer.Write(sanitized);
    }

    private static void WriteBytesSync(string path, ReadOnlySpan<byte> bytes)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 32768);
        output.Write(bytes);
    }

    private static void WriteXmlSync(XDocument document, string path)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 32768);
        using var writer = new StreamWriter(output, Utf8NoBom);
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static async Task SanitizeHtmlFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(path, cancellationToken);
        if (!SanitizeHtmlDocument(document, path, cacheRoot, cancellationToken))
            return;

        // The self-drawn engine renders the sanitized XHTML directly; the
        // WebView bridge script and its CSP nonce are no longer injected.
        await WriteXmlAsync(document, path, cancellationToken);
    }

    private static bool SanitizeHtmlDocument(
        XDocument document,
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var root = document.Root ?? throw new InvalidDataException("EPUB HTML 缺少根元素。");
        var isHtmlDocument = root.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase)
            || root.Descendants().Any(element => element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        var isSvgDocument = root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase);
        if (!isHtmlDocument && !isSvgDocument)
            return false;
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

            if (localName is "img" or "source" or "image")
            {
                EpubReaderImageReferenceNormalizer.NormalizeHtmlImageReferences(
                    element,
                    path,
                    cacheRoot);
            }

            if (element.HasAttributes)
            {
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

        return true;
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

    private static string NormalizeRootPath(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string ResolveContainedPath(string root, string relativePath) =>
        ResolveContainedPathNormalized(NormalizeRootPath(root), root, relativePath);

    private static string ResolveContainedPathNormalized(
        string normalizedRoot,
        string root,
        string relativePath)
    {
        var normalized = Path.DirectorySeparatorChar == '/'
            ? relativePath
            : relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
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
        string relativePath) =>
        ResolvePublicationPathNormalized(baseDirectory, NormalizeRootPath(cacheRoot), relativePath);

    private static string ResolvePublicationPathNormalized(
        string baseDirectory,
        string normalizedRoot,
        string relativePath)
    {
        var normalized = Path.DirectorySeparatorChar == '/'
            ? relativePath
            : relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
        return fullPath;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = NormalizeRootPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
    }
}
