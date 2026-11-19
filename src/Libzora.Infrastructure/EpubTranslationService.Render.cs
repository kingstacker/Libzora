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
/// Page rendering and bilingual output.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private static IReadOnlyDictionary<string, byte[]> RenderPages(
        IReadOnlyList<TranslationPage> pages,
        IReadOnlyDictionary<string, string> translations,
        BookTranslationOutputMode mode,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var rendered = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseDocument(page.OriginalMarkup, out var document))
                throw new InvalidDataException($"无法回填 EPUB 页面：{page.EntryName}");
            var segments = ExtractSegments(document, page.EntryName);
            var changed = false;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!translations.TryGetValue(segment.Key, out var translated)
                    || string.IsNullOrWhiteSpace(translated))
                    throw new InvalidDataException($"EPUB 文本段缺少完整译文：{page.EntryName}");

                if (mode == BookTranslationOutputMode.Bilingual)
                {
                    if (page.IsNcx)
                        segment.Element.Value = $"{segment.Text} / {segment.Markup.DisplayText(translated)}";
                    else if (page.IsNavigation && segment.Element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
                        AppendBilingualNavigationText(segment, translated, targetLanguage);
                    else
                        AppendBilingualText(segment, translated, targetLanguage);
                }
                else
                {
                    ReplaceInlineText(segment, translated);
                }
                changed = true;
            }

            if (mode == BookTranslationOutputMode.Bilingual && changed && !page.IsNcx)
                AddBilingualStylesheet(document);
            if (mode == BookTranslationOutputMode.Translated && document.Root is { } root)
            {
                UpdateLanguageAttributes(root, targetLanguage);
                root.SetAttributeValue(XNamespace.Xml + "lang", targetLanguage);
                if (!page.IsNcx) root.SetAttributeValue("lang", targetLanguage);
                changed = true;
            }
            if (changed)
                rendered[page.EntryName] = Encoding.UTF8.GetBytes(SerializeDocument(document));
        }
        return rendered;
    }

    private static IReadOnlyList<TranslationSegment> ExtractSegments(
        XDocument document,
        string entryName)
    {
        var segments = new List<TranslationSegment>();
        var ordinal = 0;
        if (document.Root?.Name.LocalName.Equals("ncx", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var label in document.Descendants().Where(element =>
                         element.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase)
                         && element.Parent?.Name.LocalName.Equals("navLabel", StringComparison.OrdinalIgnoreCase) == true))
                AddSegment(label, label.Nodes().ToArray());
            return segments;
        }

        var body = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null) return [];
        var isNavigation = IsTocNavigationDocument(document);
        Visit(body);
        return segments;

        void AddSegment(XElement container, IReadOnlyList<XNode> nodes)
        {
            if (nodes.Count == 0) return;
            var markup = new EpubTranslationMarkup(nodes);
            var key = CreateSegmentKey(entryName, ordinal++);
            if (!markup.HasTranslatableText) return;
            segments.Add(new TranslationSegment(key, markup.Text, container, markup));
        }

        bool IsBoundary(XElement element) => TranslatableBlockNames.Contains(element.Name.LocalName)
            || (isNavigation && element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase));

        void Visit(XElement container)
        {
            if (EpubTranslationMarkup.IsHidden(container)) return;
            var run = new List<XNode>();
            foreach (var node in container.Nodes())
            {
                if (node is XElement element && !EpubTranslationMarkup.IsHidden(element)
                    && (IsBoundary(element) || element.Descendants().Any(IsBoundary)))
                {
                    AddSegment(container, run.ToArray());
                    run.Clear();
                    Visit(element);
                }
                else run.Add(node);
            }
            AddSegment(container, run.ToArray());
        }
    }

    private static IReadOnlyList<XNode> RenderInlineNodes(TranslationSegment segment, string translated)
    {
        if (!segment.Markup.TryRender(translated, out var nodes))
            throw new InvalidDataException("译文中的行内结构标记不完整，请重新翻译该段。");
        return nodes;
    }

    private static void ReplaceInlineText(TranslationSegment segment, string translated)
    {
        var nodes = RenderInlineNodes(segment, translated);
        segment.Markup.Nodes[0].AddBeforeSelf(nodes);
        foreach (var node in segment.Markup.Nodes) node.Remove();
    }

    private static void AppendBilingualText(TranslationSegment segment, string translated, string targetLanguage)
    {
        var ns = segment.Element.Name.Namespace;
        var span = new XElement(ns + "span",
            new XAttribute("class", "libzora-translation"),
            new XAttribute("lang", targetLanguage),
            new XAttribute(XNamespace.Xml + "lang", targetLanguage),
            RenderInlineNodes(segment, translated));
        PrepareBilingualCopy(span, targetLanguage);
        segment.Markup.Nodes[^1].AddAfterSelf(
            new XElement(ns + "br"),
            span);
    }

    // Kindle and a number of EPUB converters build the table of contents from
    // the text inside the navigation link itself. Keep the bilingual label in
    // the <a> text rather than relying on Libzora's reader-only sibling span.
    private static void AppendBilingualNavigationText(
        TranslationSegment segment,
        string translated,
        string targetLanguage)
    {
        var nodes = RenderInlineNodes(segment, translated);
        foreach (var element in nodes.OfType<XElement>()) PrepareBilingualCopy(element, targetLanguage);
        segment.Element.Add(new XText(" / "), nodes);
    }

    private static void PrepareBilingualCopy(XElement root, string targetLanguage)
    {
        foreach (var id in root.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.LocalName == "id").ToArray())
            id.Remove();
        UpdateLanguageAttributes(root, targetLanguage);
    }

    private static void UpdateLanguageAttributes(XElement root, string targetLanguage)
    {
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(attribute =>
                     attribute.Name == XNamespace.Xml + "lang" || attribute.Name == "lang"))
            attribute.Value = targetLanguage;
    }

    private static void AddBilingualStylesheet(XDocument document)
    {
        var root = document.Root;
        if (root is null) return;
        var ns = root.Name.Namespace;
        var head = root.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase));
        if (head is null)
        {
            head = new XElement(ns + "head");
            root.AddFirst(head);
        }

        if (head.Descendants().Any(element =>
                element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)
                && element.Value.Contains("libzora-translation", StringComparison.Ordinal)))
            return;

        head.Add(new XElement(
            ns + "style",
            new XAttribute("type", "text/css"),
            ".libzora-translation{display:block;margin-top:.55em;opacity:.82;}"));
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> RenderPackageEntriesAsync(
        string sourcePath,
        BookTranslationOutputMode mode,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var rendered = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        using var archive = ZipFile.OpenRead(sourcePath);
        foreach (var entry in archive.Entries.Where(entry =>
                     Path.GetExtension(entry.FullName).Equals(".opf", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > MaxArchiveEntryBytes)
                throw new InvalidDataException("EPUB 的语言元数据文件过大。");
            var markup = await ReadArchiveTextAsync(entry, cancellationToken);
            if (!TryParseDocument(markup, out var document) || document.Root is null)
                throw new InvalidDataException("无法更新 EPUB 的语言元数据。");
            var metadata = document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
            if (metadata is null)
            {
                metadata = new XElement(document.Root.Name.Namespace + "metadata");
                document.Root.AddFirst(metadata);
            }
            var languages = metadata.Elements(dc + "language").ToArray();
            if (mode == BookTranslationOutputMode.Translated)
            {
                if (languages.Length == 0) metadata.Add(new XElement(dc + "language", targetLanguage));
                else
                {
                    languages[0].Value = targetLanguage;
                    foreach (var language in languages.Skip(1)) language.Remove();
                }
            }
            else if (!languages.Any(language => language.Value.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase)))
                metadata.Add(new XElement(dc + "language", targetLanguage));
            rendered[entry.FullName] = Encoding.UTF8.GetBytes(SerializeDocument(document));
        }
        return rendered;
    }
}
