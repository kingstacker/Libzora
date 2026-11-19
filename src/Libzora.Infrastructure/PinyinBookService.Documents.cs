using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotNetG2P.Chinese;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// XHTML parsing, serialisation and cache restore.
/// </summary>
public sealed partial class PinyinBookService : IPinyinBookService
{
    private static bool ShouldSkipElement(XElement element) =>
        SkippedElementNames.Contains(element.Name.LocalName);

    private static bool IsHanCharacter(char character) =>
        character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';

    private static async Task WriteArchiveAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, byte[]> renderedPages,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            var entries = source.Entries
                .OrderBy(entry => entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();
            if (!entries.Any(entry => entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("EPUB 缺少 mimetype 文件。 ");

            using (var destination = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var sourceEntry in entries)
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
                    if (renderedPages.TryGetValue(sourceEntry.FullName, out var rendered))
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

            File.Move(temporaryPath, destinationPath, overwrite: false);
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

    private static string SerializeNodes(IReadOnlyList<XNode> nodes)
    {
        var builder = new StringBuilder();
        using var stringWriter = new StringWriter(
            builder,
            System.Globalization.CultureInfo.InvariantCulture);
        using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            Indent = false
        }))
        {
            foreach (var node in nodes)
                node.WriteTo(xmlWriter);
        }
        return builder.ToString();
    }

    private static string CreateAnnotatedDisplayText(IReadOnlyList<XNode> nodes)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
                continue;
            }

            if (node is not XElement element) continue;
            if (element.Name.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase))
            {
                var baseText = string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value));
                var pronunciation = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase));
                builder.Append(baseText);
                if (pronunciation is not null)
                    builder.Append('(').Append(pronunciation.Value).Append(')');
                continue;
            }

            builder.Append(element.Value);
        }
        return builder.ToString().Trim();
    }

    private static bool TryRestoreAnnotation(
        PinyinSegment segment,
        PinyinCacheSegmentLine cached,
        out PinyinAnnotationResult annotation)
    {
        annotation = null!;
        if (segment.TextNode.Parent is null
            || !string.Equals(cached.EntryName, segment.EntryName, StringComparison.Ordinal)
            || !string.Equals(cached.OriginalText, segment.OriginalText, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(cached.AnnotatedMarkup)
            || cached.ReviewTargets is null
            || cached.ReviewTargets.Count != cached.ReviewCandidateCount)
            return false;
        try
        {
            var wrapper = XElement.Parse(
                $"<libzora-fragment>{cached.AnnotatedMarkup}</libzora-fragment>",
                LoadOptions.PreserveWhitespace);
            var nodes = wrapper.Nodes().ToList();
            var baseText = new StringBuilder();
            var rubies = new Dictionary<int, XElement>();
            foreach (var node in nodes)
            {
                if (node is XText text)
                {
                    baseText.Append(text.Value);
                    continue;
                }
                if (node is not XElement ruby || ruby.Name.LocalName != "ruby") return false;
                var character = string.Concat(ruby.Nodes().OfType<XText>().Select(text => text.Value));
                if (character.Length != 1 || ruby.Elements().All(element => element.Name.LocalName != "rt"))
                    return false;
                rubies.Add(baseText.Length, ruby);
                baseText.Append(character);
            }
            if (baseText.ToString() != segment.OriginalText
                || rubies.Count != cached.AnnotatedCharacterCount)
                return false;

            var targets = new List<PinyinReviewTarget>();
            var positions = new HashSet<int>();
            foreach (var target in cached.ReviewTargets)
            {
                if (!positions.Add(target.CharacterIndex)
                    || !rubies.TryGetValue(target.CharacterIndex, out var ruby)
                    || target.Candidates is not { Count: > 1 })
                    return false;
                var context = ExtractReviewContext(
                    segment.Context.Text, segment.ContextOffset + target.CharacterIndex);
                targets.Add(new PinyinReviewTarget(
                    segment.Index, target.CharacterIndex, ruby,
                    segment.OriginalText[target.CharacterIndex], target.CurrentPinyin,
                    target.Candidates, context.Text, context.CharacterIndex)
                {
                    Reviewed = target.Reviewed
                });
            }
            if (targets.Count(target => target.Reviewed) != cached.ReviewedCandidateCount)
                return false;

            // Avoid LINQ to XML cloning parented nodes: subsequent review must
            // update the same ruby objects that are attached to the output page.
            foreach (var node in nodes) node.Remove();
            segment.TextNode.ReplaceWith(nodes);
            annotation = new PinyinAnnotationResult(
                nodes, rubies.Count, targets, CreateAnnotatedDisplayText(nodes));
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
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

    private static bool IsXhtmlEntry(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeArchiveEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith('/')
            || name.StartsWith('\\')
            || Path.IsPathFullyQualified(name))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");

        var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");
    }
}
