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
/// XHTML/archive document handling.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private static bool IsTocNavigationDocument(XDocument document) =>
        document.Descendants().Any(element =>
            element.Name.LocalName.Equals("nav", StringComparison.OrdinalIgnoreCase)
            && (HasToken(GetAttributeValue(element, "type"), "toc")
                || HasToken(GetAttributeValue(element, "role"), "doc-toc")));


    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetAttributeValue(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static async Task WriteArchiveAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, byte[]> renderedPages,
        IReadOnlyDictionary<string, byte[]> renderedNavigationEntries,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            using (var destination = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var sourceEntry in source.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureSafeArchiveEntryName(sourceEntry.FullName);
                    var compression = sourceEntry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase)
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Optimal;
                    var destinationEntry = destination.CreateEntry(sourceEntry.FullName, compression);
                    try { destinationEntry.LastWriteTime = sourceEntry.LastWriteTime; }
                    catch (ArgumentOutOfRangeException) { }

                    await using var output = destinationEntry.Open();
                    if (renderedPages.TryGetValue(sourceEntry.FullName, out var rendered)
                        || renderedNavigationEntries.TryGetValue(sourceEntry.FullName, out rendered))
                    {
                        await output.WriteAsync(rendered, cancellationToken);
                    }
                    else
                    {
                        await using var input = sourceEntry.Open();
                        await input.CopyToAsync(output, 81920, cancellationToken);
                    }
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryParseDocument(string markup, out XDocument document)
    {
        try
        {
            using var stringReader = new StringReader(markup);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            });
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
            return document.Root is not null;
        }
        catch (XmlException)
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
                var root = html.DocumentNode.SelectSingleNode("//html")
                    ?? html.DocumentNode.ChildNodes.FirstOrDefault(node => node.NodeType == HtmlNodeType.Element);
                if (root is null)
                {
                    document = new XDocument();
                    return false;
                }

                var builder = new StringBuilder();
                using (var writer = new StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture))
                    root.WriteTo(writer);
                using var stringReader = new StringReader(builder.ToString());
                using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null
                });
                document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
                return document.Root is not null;
            }
            catch (Exception exception) when (exception is XmlException or InvalidOperationException)
            {
                document = new XDocument();
                return false;
            }
        }
    }

    private static string SerializeDocument(XDocument document)
    {
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
            document.Save(writer, SaveOptions.DisableFormatting);
        return builder.ToString();
    }

    private static async Task<string> ReadArchiveTextAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static bool TryReadMarkedTranslations(
        string response,
        int expectedCount,
        out IReadOnlyList<string> translations)
    {
        var matches = SegmentMarkerPattern.Matches(response);
        if (matches.Count != expectedCount * 2)
        {
            translations = [];
            return false;
        }

        var values = new string[expectedCount];
        for (var index = 0; index < expectedCount; index++)
        {
            var start = matches[index * 2];
            var end = matches[index * 2 + 1];
            if (!start.Groups["id"].Value.Equals(end.Groups["id"].Value, StringComparison.Ordinal)
                || !start.Groups["id"].Value.Equals(index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                translations = [];
                return false;
            }
            values[index] = response[(start.Index + start.Length)..end.Index].Trim();
            if (values[index].Length == 0)
            {
                translations = [];
                return false;
            }
        }

        translations = values;
        return true;
    }
}
