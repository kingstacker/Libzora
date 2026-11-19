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
/// XML loading, repair and degraded text output.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<XDocument> LoadXmlAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (XmlException originalFailure)
        {
            var markup = await File.ReadAllTextAsync(path, cancellationToken);

            // A number of EPUB 2 generators emit HTML entities such as
            // &nbsp; while declaring XHTML. External DTD resolution stays
            // disabled; decode only entities known by the platform and retry.
            var normalized = DecodeKnownHtmlEntities(markup);
            if (!string.Equals(markup, normalized, StringComparison.Ordinal))
            {
                try { return ParseXml(normalized); }
                catch (XmlException) { }
            }

            // Publisher tooling also ships genuine tag soup under an .xhtml
            // extension: unclosed elements, stray end tags, unquoted
            // attributes. Repair it with the HTML parser instead of failing
            // the whole book because one auxiliary page is malformed.
            return TryLoadRepairedHtml(normalized)
                ?? throw new XmlException(originalFailure.Message, originalFailure);
        }
    }

    private static string DecodeKnownHtmlEntities(string markup) =>
        HtmlNamedEntityPattern.Replace(markup, match =>
        {
            var entityName = match.Value.AsSpan(1, match.Value.Length - 2);
            if (entityName.Equals("amp", StringComparison.Ordinal)
                || entityName.Equals("lt", StringComparison.Ordinal)
                || entityName.Equals("gt", StringComparison.Ordinal)
                || entityName.Equals("quot", StringComparison.Ordinal)
                || entityName.Equals("apos", StringComparison.Ordinal))
                return match.Value;

            var decoded = WebUtility.HtmlDecode(match.Value);
            return string.Equals(decoded, match.Value, StringComparison.Ordinal)
                ? match.Value
                : decoded;
        });

    private static XDocument ParseXml(string markup)
    {
        using var textReader = new StringReader(markup);
        using var reader = XmlReader.Create(textReader, CreateSecureXmlReaderSettings(asynchronous: false));
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static XDocument? TryLoadRepairedHtml(string markup)
    {
        try
        {
            var html = new HtmlDocument
            {
                OptionOutputAsXml = true,
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true,
                OptionWriteEmptyNodes = true
            };
            html.LoadHtml(markup);

            // Serialize the document element alone: the XML writer needs a
            // single root, and a DOCTYPE or trailing comment would otherwise
            // travel with it.
            var root = html.DocumentNode.SelectSingleNode("//html")
                ?? html.DocumentNode.ChildNodes.FirstOrDefault(node =>
                    node.NodeType == HtmlNodeType.Element);
            if (root is null) return null;

            var builder = new StringBuilder();
            using (var writer = new StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture))
                root.WriteTo(writer);
            return ParseXml(builder.ToString());
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static XmlReaderSettings CreateSecureXmlReaderSettings(bool asynchronous = true) => new()
    {
        Async = asynchronous,
        // Standard EPUB XHTML commonly carries a DOCTYPE. Ignore it
        // without resolving entities; the null resolver keeps external
        // DTDs and entities out of the reader process.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null
    };

    private static async Task<bool> IsExtractionReadyAsync(
        string markerPath,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return false;
        var marker = await File.ReadAllTextAsync(markerPath, cancellationToken);
        return string.Equals(
            marker.Trim(),
            $"{cacheKey}\n{ExtractionFormatVersion}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SanitizeExtractedResourcesAsync(
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var htmlFiles = Directory.EnumerateFiles(cacheRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        await Parallel.ForEachAsync(
            htmlFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (path, token) =>
                await SanitizeHtmlFileSafelyAsync(path, cacheRoot, token));

        var cssFiles = Directory.EnumerateFiles(cacheRoot, "*.css", SearchOption.AllDirectories).ToArray();
        await Parallel.ForEachAsync(
            cssFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMetadataConcurrency()
            },
            async (path, token) =>
                await SanitizeCssFileAsync(path, cacheRoot, token));
    }

    // One unrepairable page must never cost the reader the whole book. The
    // extraction marker is only written after sanitization completes, so a
    // rethrow here would also make the failure permanent: every later open
    // re-extracts and fails again at the same file.
    private static async Task SanitizeHtmlFileSafelyAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await SanitizeHtmlFileAsync(path, cacheRoot, cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException)
        {
        }

        // The renderer parses the cached XHTML directly, so leaving the
        // original markup in place would only move the failure downstream.
        // Replace it with a well-formed, script-free text rendition.
        await WriteDegradedTextDocumentAsync(path, cancellationToken);
    }

    private static async Task WriteDegradedTextDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string markup;
        try { markup = await File.ReadAllTextAsync(path, cancellationToken); }
        catch (IOException) { markup = string.Empty; }

        var text = HiddenContentPattern.Replace(markup, " ");
        text = BlockBreakPattern.Replace(text, "\n");
        text = HtmlTagPattern.Replace(text, " ");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');

        var body = new XElement(XhtmlNamespace + "body");
        foreach (var line in text.Split('\n'))
        {
            var paragraph = string.Join(
                ' ',
                line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (paragraph.Length == 0) continue;
            body.Add(new XElement(XhtmlNamespace + "p", paragraph));
        }

        var document = new XDocument(
            new XElement(
                XhtmlNamespace + "html",
                new XElement(XhtmlNamespace + "head"),
                body));
        document.Root!.Element(XhtmlNamespace + "body")!
            .SetAttributeValue("data-libzora-degraded", "html-parse-failed");
        MarkVerticalInlineRuns(document.Root!);
        await WriteXmlAsync(document, path, cancellationToken);
    }
}
