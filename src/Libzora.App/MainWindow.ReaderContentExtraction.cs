using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Static EPUB content extraction used by the fallback reader: XHTML
/// walking, footnote detection and plain-text normalisation.
/// </summary>
public partial class MainWindow
{
    private static ReaderLinuxTextFallbackExtractedContent ExtractReaderFallbackContent(
        string path,
        string? startFragment = null,
        string? startTitle = null,
        string? endFragment = null)
    {
        var text = ExtractReaderPlainText(path, startFragment, startTitle, endFragment);
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var body = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
            if (body is null)
                return CreateFallbackContentFromTextAndImages(path, text, startTitle);

            var blocks = new List<ReaderLinuxTextFallbackRawBlock>();
            var builder = new StringBuilder();
            var fragment = startFragment?.TrimStart('#');
            var hasFragment = !string.IsNullOrWhiteSpace(fragment);
            var stopFragment = endFragment?.TrimStart('#');
            var hasStopFragment = !string.IsNullOrWhiteSpace(stopFragment)
                && !string.Equals(stopFragment, fragment, StringComparison.Ordinal);
            var capture = !hasFragment;
            var foundFragment = false;
            var stopCapture = false;

            void FlushText(
                bool isChapterTitle = false,
                bool isParagraphBoundary = false)
            {
                var blockText = NormalizeReaderPlainText(builder.ToString());
                builder.Clear();
                if (!string.IsNullOrWhiteSpace(blockText))
                    blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                        blockText,
                        null,
                        null,
                        null,
                        isChapterTitle,
                        isParagraphBoundary));
            }

            void Visit(
                XNode node,
                bool reflowableParagraph = false,
                bool chapterTitle = false)
            {
                if (node is XText textNode)
                {
                    if (capture)
                        AppendReaderPlainText(builder, textNode.Value);
                    return;
                }

                if (node is not XElement element)
                    return;

                var name = element.Name.LocalName.ToLowerInvariant();
                if (name is "script" or "style" or "noscript")
                    return;
                if (IsReaderFootnoteDefinition(element))
                    return;

                var chapterTitleElement = name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
                var childChapterTitle = chapterTitle || chapterTitleElement;

                var metadata = string.Join(
                    ' ',
                    element.Attributes()
                        .Where(attribute => attribute.Name.LocalName is "class" or "id")
                        .Select(attribute => attribute.Value));
                var preservesLineBreaks = name is "pre" or "code" or "kbd" or "samp"
                    || System.Text.RegularExpressions.Regex.IsMatch(
                        metadata,
                        @"(?:poem|poetry|verse|诗|詩)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                var childReflowableParagraph = !preservesLineBreaks
                    && (reflowableParagraph
                        || name is "p" or "li" or "blockquote"
                        or "div" or "section" or "article" or "main");

                var matchedHere = false;
                if (hasFragment && !capture && ElementMatchesReaderFragment(element, fragment))
                {
                    capture = true;
                    foundFragment = true;
                    matchedHere = true;
                }

                if (capture
                    && hasStopFragment
                    && ElementMatchesReaderFragment(element, stopFragment))
                {
                    FlushText(isChapterTitle: chapterTitle);
                    stopCapture = true;
                    return;
                }

                if (capture && name == "a" && IsReaderFootnoteReference(element))
                {
                    // A heading in this book contains a footnote immediately
                    // after its last character. Preserve the heading semantic
                    // when the text is flushed before the footnote record.
                    FlushText(isChapterTitle: chapterTitle);
                    var href = ResolveReaderElementHref(path, element);
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        // An EPUB footnote reference often contains an image
                        // whose alt text is the complete footnote explanation.
                        // That alt text belongs in the popup, never in the
                        // inline marker shown in the reading surface.
                        var label = GetReaderFootnoteMarkerLabel(element);
                        blocks.Add(new ReaderLinuxTextFallbackRawBlock(null, null, href, label));
                        return;
                    }
                }

                if (capture && name is "img" or "image" or "picture")
                {
                    FlushText();
                    var imagePath = EpubReaderImageReferenceNormalizer.ResolveFirstLocalImagePath(element, path);
                    if (!string.IsNullOrWhiteSpace(imagePath))
                        blocks.Add(new ReaderLinuxTextFallbackRawBlock(null, imagePath));
                    return;
                }

                foreach (var child in element.Nodes())
                {
                    if (stopCapture) break;
                    Visit(child, childReflowableParagraph, childChapterTitle);
                }
                if (stopCapture) return;

                if (capture && name is "br")
                {
                    if (reflowableParagraph && !preservesLineBreaks)
                        AppendReaderPlainText(builder, " ");
                    else
                        AppendReaderPlainTextLineBreak(builder);
                }
                else if (capture && IsReaderPlainTextBlock(name))
                {
                    AppendReaderPlainTextBreak(builder);
                    var hasText = builder
                        .ToString()
                        .Any(character => !char.IsWhiteSpace(character));
                    FlushText(
                        childChapterTitle,
                        isParagraphBoundary: true);
                    // A footnote reference can be the last node in a
                    // paragraph. Its text was flushed before the reference
                    // was emitted, so retain the paragraph boundary even
                    // though there is no text in the builder now.
                    if (!hasText)
                    {
                        blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                            null,
                            null,
                            IsParagraphBoundary: true));
                    }
                }

                if (matchedHere)
                {
                    capture = true;
                }
            }

            foreach (var node in body.Nodes())
            {
                if (stopCapture) break;
                Visit(node);
            }
            FlushText();

            if (hasFragment && !foundFragment)
                return CreateFallbackContentFromTextAndImages(path, text, startTitle);

            var normalizedStartTitle = NormalizeReaderPlainTextLine(startTitle);
            var firstTextIndex = blocks.FindIndex(block => !string.IsNullOrWhiteSpace(block.Text));
            if (firstTextIndex < 0)
            {
                if (!string.IsNullOrWhiteSpace(normalizedStartTitle))
                    blocks.Insert(0, new ReaderLinuxTextFallbackRawBlock(
                        normalizedStartTitle,
                        null,
                        IsChapterTitle: true,
                        IsParagraphBoundary: true));
            }
            else if (!string.IsNullOrWhiteSpace(normalizedStartTitle))
            {
                var firstBlock = blocks[firstTextIndex];
                var normalizedFirstText = NormalizeReaderPlainText(firstBlock.Text ?? string.Empty);
                var firstLine = NormalizeReaderPlainTextLine(
                    normalizedFirstText
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault());
                if (string.Equals(firstLine, normalizedStartTitle, StringComparison.OrdinalIgnoreCase))
                {
                    // A few EPUBs put the chapter name in a div/p instead of
                    // h1-h6. Promote the matching first block so both render
                    // paths apply the same centered/bold title treatment.
                    var firstLineBreak = normalizedFirstText.IndexOf('\n');
                    if (firstLineBreak < 0)
                    {
                        blocks[firstTextIndex] = firstBlock with { IsChapterTitle = true };
                    }
                    else
                    {
                        var bodyText = normalizedFirstText[(firstLineBreak + 1)..].Trim();
                        blocks[firstTextIndex] = firstBlock with
                        {
                            Text = normalizedStartTitle,
                            IsChapterTitle = true,
                            IsParagraphBoundary = true
                        };
                        if (!string.IsNullOrWhiteSpace(bodyText))
                            blocks.Insert(
                                firstTextIndex + 1,
                                firstBlock with
                                {
                                    Text = bodyText,
                                    IsChapterTitle = false
                                });
                    }
                }
                else if (firstLine.StartsWith(
                             normalizedStartTitle,
                             StringComparison.OrdinalIgnoreCase))
                {
                    // Keep a title prefix out of the body block when a
                    // publisher placed both on one line in a generic div.
                    var bodyText = normalizedFirstText[normalizedStartTitle.Length..].TrimStart();
                    blocks[firstTextIndex] = firstBlock with { Text = bodyText };
                    blocks.Insert(
                        firstTextIndex,
                        new ReaderLinuxTextFallbackRawBlock(
                            normalizedStartTitle,
                            null,
                            IsChapterTitle: true,
                            IsParagraphBoundary: true));
                }
                else
                {
                    blocks.Insert(0, new ReaderLinuxTextFallbackRawBlock(
                        normalizedStartTitle,
                        null,
                        IsChapterTitle: true,
                        IsParagraphBoundary: true));
                }
            }

            return new ReaderLinuxTextFallbackExtractedContent(text, blocks);
        }
        catch
        {
            return CreateFallbackContentFromTextAndImages(path, text, startTitle);
        }
    }

    private static ReaderLinuxTextFallbackExtractedContent CreateFallbackContentFromTextAndImages(
        string path,
        string text,
        string? startTitle = null)
    {
        var blocks = new List<ReaderLinuxTextFallbackRawBlock>();
        var normalizedText = NormalizeReaderPlainText(text);
        var normalizedTitle = NormalizeReaderPlainTextLine(startTitle);
        if (!string.IsNullOrWhiteSpace(normalizedText))
        {
            var firstLine = NormalizeReaderPlainTextLine(
                normalizedText
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(normalizedTitle)
                && string.Equals(firstLine, normalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                    normalizedTitle,
                    null,
                    IsChapterTitle: true,
                    IsParagraphBoundary: true));
                var firstLineBreak = normalizedText.IndexOf('\n');
                var bodyText = firstLineBreak >= 0
                    ? normalizedText[(firstLineBreak + 1)..].Trim()
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(bodyText))
                    blocks.Add(new ReaderLinuxTextFallbackRawBlock(bodyText, null));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(normalizedTitle))
                    blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                        normalizedTitle,
                        null,
                        IsChapterTitle: true,
                        IsParagraphBoundary: true));
                blocks.Add(new ReaderLinuxTextFallbackRawBlock(normalizedText, null));
            }
        }
        foreach (var imagePath in EpubReaderImageReferenceNormalizer.ExtractLocalImagePaths(path))
            blocks.Add(new ReaderLinuxTextFallbackRawBlock(null, imagePath));
        return new ReaderLinuxTextFallbackExtractedContent(text, blocks);
    }

    private static bool IsReaderFootnoteReference(XElement element)
    {
        if (IsReaderFootnoteBacklink(element))
            return false;

        var metadata = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id" or "href")
                .Select(attribute => attribute.Value));
        return System.Text.RegularExpressions.Regex.IsMatch(
            metadata,
            @"\b(noteref|doc-noteref|footnote|endnote|note[-_]?ref|fn[-_]?ref)\b|(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static bool IsReaderFootnoteDefinition(XElement element)
    {
        var type = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role")
                .Select(attribute => attribute.Value));
        var identity = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "class" or "id")
                .Select(attribute => attribute.Value));
        return IsReaderFootnoteDefinition(element.Name.LocalName, type, identity);
    }

    private static bool IsReaderFootnoteDefinition(XmlReader reader, string localName)
    {
        var type = string.Join(
            ' ',
            new[]
            {
                reader.GetAttribute("type"),
                reader.GetAttribute("type", "http://www.idpf.org/2007/ops"),
                reader.GetAttribute("role")
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var identity = string.Join(
            ' ',
            new[] { reader.GetAttribute("class"), reader.GetAttribute("id") }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return IsReaderFootnoteDefinition(localName, type, identity);
    }

    private static bool IsReaderFootnoteDefinition(
        string localName,
        string type,
        string identity)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
                type,
                @"\b(?:doc-)?(?:footnote|endnote)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return true;

        var isDefinitionContainer = localName.Equals("aside", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("section", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("div", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("ol", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("ul", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("li", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("p", StringComparison.OrdinalIgnoreCase);
        return isDefinitionContainer
            && System.Text.RegularExpressions.Regex.IsMatch(
                identity,
                @"(?:^|[\s_-])(?:duokan-)?(?:footnote|endnote)(?:[\s_-]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string GetReaderFootnoteMarkerLabel(XElement element)
    {
        var label = NormalizeReaderPlainTextLine(element.Value);
        // Keep genuine short markers such as [1], 注1, or †. Long text is
        // almost always an image alt/title containing the note body itself.
        return !string.IsNullOrWhiteSpace(label) && label.Length <= 8
            ? label
            : UiText.Get("注");
    }

    private static bool IsReaderFootnoteBacklink(XElement element)
    {
        var id = element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        var href = element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
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

    private static string? ResolveReaderElementHref(string sourcePath, XElement element)
    {
        var href = element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(href)) return null;
        try
        {
            var sourceUri = new Uri(Path.GetFullPath(sourcePath), UriKind.Absolute);
            var trimmed = href.Trim();
            var hash = trimmed.IndexOf('#');
            var pathPart = hash >= 0 ? trimmed[..hash] : trimmed;
            if (!Uri.TryCreate(sourceUri, pathPart, out var uri) || !uri.IsFile)
                return null;

            if (hash >= 0)
            {
                var fragment = Uri.UnescapeDataString(trimmed[(hash + 1)..]);
                var builder = new UriBuilder(uri) { Fragment = fragment };
                uri = builder.Uri;
            }
            return uri.AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }

    private static bool ElementMatchesReaderFragment(XElement element, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name.LocalName;
            if ((name.Equals("id", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("name", StringComparison.OrdinalIgnoreCase))
                && string.Equals(attribute.Value, fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string ExtractReaderPlainText(
        string path,
        string? startFragment = null,
        string? startTitle = null,
        string? endFragment = null)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(path, settings);
            var builder = new StringBuilder();
            var fallbackBuilder = new StringBuilder();
            var inBody = false;
            var reflowParagraphDepth = 0;
            var preserveBreakDepth = 0;
            var elementStates = new Stack<(bool Reflow, bool Preserve)>();
            var fragment = startFragment?.TrimStart('#');
            var hasFragment = !string.IsNullOrWhiteSpace(fragment);
            var stopFragment = endFragment?.TrimStart('#');
            var hasStopFragment = !string.IsNullOrWhiteSpace(stopFragment)
                && !string.Equals(stopFragment, fragment, StringComparison.Ordinal);
            var capture = !hasFragment;
            var foundFragment = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var name = reader.LocalName.ToLowerInvariant();
                    if (name == "body") inBody = true;
                    if (inBody && IsReaderFootnoteDefinition(reader, name))
                    {
                        if (!reader.IsEmptyElement)
                            reader.Skip();
                        continue;
                    }
                    var metadata = $"{reader.GetAttribute("class")} {reader.GetAttribute("id")}";
                    var preservesLineBreaks = name is "pre" or "code" or "kbd" or "samp"
                        || System.Text.RegularExpressions.Regex.IsMatch(
                            metadata,
                            @"(?:poem|poetry|verse|诗|詩)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                    var entersReflow = (name is "p" or "li" or "blockquote"
                        or "div" or "section" or "article" or "main")
                        && preserveBreakDepth == 0;
                    if (preservesLineBreaks)
                        preserveBreakDepth++;
                    if (entersReflow)
                        reflowParagraphDepth++;
                    if (inBody
                        && hasFragment
                        && !capture
                        && ElementMatchesReaderFragment(reader, fragment))
                    {
                        capture = true;
                        foundFragment = true;
                    }
                    if (inBody
                        && capture
                        && hasStopFragment
                        && ElementMatchesReaderFragment(reader, stopFragment))
                    {
                        break;
                    }
                    if (inBody && name == "br")
                    {
                        if (reflowParagraphDepth > 0 && preserveBreakDepth == 0)
                            AppendReaderPlainText(fallbackBuilder, " ");
                        else
                            AppendReaderPlainTextLineBreak(fallbackBuilder);
                        if (capture)
                        {
                            if (reflowParagraphDepth > 0 && preserveBreakDepth == 0)
                                AppendReaderPlainText(builder, " ");
                            else
                                AppendReaderPlainTextLineBreak(builder);
                        }
                    }

                    if (reader.IsEmptyElement)
                    {
                        if (entersReflow)
                            reflowParagraphDepth = Math.Max(0, reflowParagraphDepth - 1);
                        if (preservesLineBreaks)
                            preserveBreakDepth = Math.Max(0, preserveBreakDepth - 1);
                    }
                    else
                    {
                        elementStates.Push((entersReflow, preservesLineBreaks));
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    var name = reader.LocalName.ToLowerInvariant();
                    if (inBody && IsReaderPlainTextBlock(name))
                    {
                        AppendReaderPlainTextBreak(fallbackBuilder);
                        if (capture)
                        AppendReaderPlainTextBreak(builder);
                    }
                    if (elementStates.Count > 0)
                    {
                        var state = elementStates.Pop();
                        if (state.Reflow)
                            reflowParagraphDepth = Math.Max(0, reflowParagraphDepth - 1);
                        if (state.Preserve)
                            preserveBreakDepth = Math.Max(0, preserveBreakDepth - 1);
                    }
                    if (name == "body") inBody = false;
                }
                else if (inBody && (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA))
                {
                    AppendReaderPlainText(fallbackBuilder, reader.Value);
                    if (capture)
                        AppendReaderPlainText(builder, reader.Value);
                }
            }

            var raw = hasFragment && foundFragment
                ? builder.ToString()
                : fallbackBuilder.ToString();
            return EnsureReaderPlainTextStartsWithTitle(
                TrimReaderPlainTextToTitle(NormalizeReaderPlainText(raw), startTitle),
                startTitle);
        }
        catch (XmlException)
        {
            var html = File.ReadAllText(path);
            var withoutTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            return EnsureReaderPlainTextStartsWithTitle(
                TrimReaderPlainTextToTitle(
                    NormalizeReaderPlainText(WebUtility.HtmlDecode(withoutTags)),
                    startTitle),
                startTitle);
        }
    }

    private static bool ElementMatchesReaderFragment(XmlReader reader, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        foreach (var attribute in new[] { "id", "name" })
        {
            var value = reader.GetAttribute(attribute);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (string.Equals(value, fragment, StringComparison.Ordinal)
                || string.Equals(WebUtility.UrlDecode(value), fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReaderPlainTextBlock(string name)
        => name is "p" or "div" or "section" or "article" or "main"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "li" or "blockquote";

    private static void AppendReaderPlainText(StringBuilder builder, string text)
    {
        text = WebUtility.HtmlDecode(text);
        foreach (var ch in text)
        {
            builder.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }
    }

    private static void AppendReaderPlainTextBreak(StringBuilder builder)
    {
        if (builder.Length == 0) return;
        if (builder[^1] != '\n') builder.AppendLine();
        if (builder.Length >= 2 && builder[^2] != '\n') builder.AppendLine();
    }

    private static void AppendReaderPlainTextLineBreak(StringBuilder builder)
    {
        if (builder.Length == 0) return;
        if (builder[^1] != '\n') builder.AppendLine();
    }

    private static string NormalizeReaderPlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = string.Join(
            "\n",
            text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"[ \t\f\v]+", " ").Trim()));
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\n{3,}", "\n\n").Trim();
        return normalized.Replace("\n", Environment.NewLine);
    }

    private static string TrimReaderPlainTextToTitle(string text, string? title)
    {
        title = NormalizeReaderPlainTextLine(title);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
            return text;

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = NormalizeReaderPlainTextLine(lines[index]);
            if (string.Equals(line, title, StringComparison.OrdinalIgnoreCase)
                || line.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                return string.Join('\n', lines.Skip(index)).Trim();
            }
        }

        var directIndex = text.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        return directIndex >= 0 ? text[directIndex..].TrimStart() : text;
    }

    private static string EnsureReaderPlainTextStartsWithTitle(string text, string? title)
    {
        title = NormalizeReaderPlainTextLine(title);
        if (string.IsNullOrWhiteSpace(title)) return text;
        if (string.IsNullOrWhiteSpace(text)) return title;

        var firstLine = NormalizeReaderPlainTextLine(
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault());
        if (string.Equals(firstLine, title, StringComparison.OrdinalIgnoreCase)
            || firstLine.Contains(title, StringComparison.OrdinalIgnoreCase))
        {
            return text.TrimStart();
        }

        return title + Environment.NewLine + Environment.NewLine + text.TrimStart();
    }

    private static string NormalizeReaderPlainTextLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }
}
