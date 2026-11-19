using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Footnote target indexing and definition detection.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private XElement? LocalTarget(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile
            || !string.Equals(uri.LocalPath, _chapterPath, PathComparison)) return null;
        var fragment = GetHrefFragment(href);
        return fragment is not null && _elementsByFragment.TryGetValue(fragment, out var target) ? target : null;
    }

    private XElement? ReferenceTarget(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        if (string.Equals(uri.LocalPath, _chapterPath, PathComparison)) return LocalTarget(href);
        var fragment = GetHrefFragment(href);
        if (fragment is null) return null;
        var relative = Path.GetRelativePath(_resourceRoot, uri.LocalPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)) return null;

        if (!_externalElementsByFragment.TryGetValue(uri.LocalPath, out var targets))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            targets = new Dictionary<string, XElement>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(uri.LocalPath) && LoadDocument(uri.LocalPath)?.Root is { } root)
                {
                    var body = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
                    foreach (var element in body?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        foreach (var id in FragmentIds(element)) targets.TryAdd(id, element);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable note target must not prevent reading its source.
            }
            _externalElementsByFragment[uri.LocalPath] = targets;
        }
        return targets.GetValueOrDefault(fragment);
    }

    private static XElement? NearestFootnoteBlock(XElement element) =>
        element.AncestorsAndSelf().FirstOrDefault(node => node.Name.LocalName is
            "p" or "li" or "dd" or "aside" or "blockquote" or "section" or "article" or "div");

    /// <summary>
    /// The innermost block that owns the note's leading backlink. An id on an
    /// outer chapter wrapper must never hide the wrapper's other paragraphs.
    /// </summary>
    private static XElement? FootnoteDefinitionBlock(XElement target)
    {
        // A semantic definition (epub:type/aside/notes container) owns the
        // whole note, including every paragraph it contains.
        if (target.AncestorsAndSelf().FirstOrDefault(HasFootnoteDefinitionMarkup) is { } semantic)
            return semantic;
        var block = NearestFootnoteBlock(target);
        if (block is null) return null;
        var marker = LeadingMarkerLink(block);
        return marker is null ? block : NearestFootnoteBlock(marker) ?? block;
    }

    private static XElement? LeadingMarkerLink(XElement block)
    {
        foreach (var node in block.DescendantNodes())
        {
            if (node is XElement element && element.Name.LocalName == "a" && IsMarker(element.Value)
                && !string.IsNullOrWhiteSpace((string?)element.Attribute("href"))) return element;
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value)) return null;
        }
        return null;
    }

    /// <summary>
    /// Variant for converted-note detection: a marker nested inside a block
    /// child belongs to that child, not to the outer container.
    /// </summary>
    private static XElement? LeadingInlineMarkerLink(XElement block)
    {
        foreach (var node in block.DescendantNodes())
        {
            if (node is XElement blockChild && !IsInlineTag(blockChild.Name.LocalName)) return null;
            if (node is XElement element && element.Name.LocalName == "a" && IsMarker(element.Value)
                && !string.IsNullOrWhiteSpace((string?)element.Attribute("href"))) return element;
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value)) return null;
        }
        return null;
    }

    private static bool IsMarker(string text) => FootnoteMarkerTextPattern.IsMatch(CollapseWhitespace(text));

    private static bool IsNavigationContext(XElement element) => element.AncestorsAndSelf().Any(node =>
        node.Name.LocalName == "nav"
        || node.Attributes().Any(attribute => attribute.Name.LocalName is "class" or "type" or "role"
            && attribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => token is "toc" or "doc-toc" or "contents")));

    private static IEnumerable<string> ReferenceFragmentIds(XElement element)
    {
        foreach (var node in element.AncestorsAndSelf().TakeWhile(node => node == element || IsInlineTag(node.Name.LocalName)))
            foreach (var fragment in FragmentIds(node)) yield return fragment;
        var previous = element.NodesBeforeSelf().LastOrDefault(node => node is not XText text || !string.IsNullOrWhiteSpace(text.Value));
        if (previous is XElement anchor && anchor.Name.LocalName == "a" && string.IsNullOrWhiteSpace(anchor.Value))
            foreach (var fragment in FragmentIds(anchor)) yield return fragment;
    }

    private static IEnumerable<string> FragmentIds(XElement element) => element.Attributes()
        .Where(attribute => attribute.Name.LocalName == "id"
            || (attribute.Name.LocalName == "name" && element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)))
        .Select(attribute => attribute.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal);

    private static string? Id(XElement element) => FragmentIds(element).FirstOrDefault();

    private static bool IsFootnoteImage(XElement element)
    {
        var markerAttribute = element.Attributes().Any(attribute =>
            attribute.Name.LocalName.Equals("data-footnote", StringComparison.OrdinalIgnoreCase)
            || attribute.Name.LocalName.Equals("data-footnote-text", StringComparison.OrdinalIgnoreCase)
            || attribute.Name.LocalName.Equals("data-note", StringComparison.OrdinalIgnoreCase));
        if (markerAttribute)
        {
            return true;
        }

        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return classes.Any(value =>
            value.Contains("footnote", StringComparison.OrdinalIgnoreCase)
            || value.Contains("endnote", StringComparison.OrdinalIgnoreCase)
            || value.Equals("fnote", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsFootnoteDefinition(XElement element) =>
        _inferredFootnoteDefinitions.Contains(element) || HasFootnoteDefinitionMarkup(element)
        || LooksLikeConvertedFootnoteDefinition(element);

    private static bool HasFootnoteDefinitionMarkup(XElement element)
    {
        var semanticType = string.Join(
            " ",
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role")
                .Select(attribute => attribute.Value));
        if (FootnoteDefinitionTypePattern.IsMatch(semanticType))
        {
            return true;
        }

        var local = element.Name.LocalName;
        if (local is not ("aside" or "section" or "div" or "ol" or "ul" or "li"
            or "p" or "blockquote" or "article"))
        {
            return false;
        }

        var identity = string.Join(
            " ",
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "class" or "id")
                .Select(attribute => attribute.Value));
        if (FootnoteReferenceIdentityPattern.IsMatch(identity))
        {
            return false;
        }

        return FootnoteDefinitionIdentityPattern.IsMatch(identity);
    }

    /// <summary>
    /// Recognizes an endnote paragraph written by converters that preserve
    /// neither epub:type nor a footnote class:
    /// <p id="filepos652450"><a href="chapter.html#filepos12816">[1]</a>note text</p>.
    /// The leading marker link must point into another document, which keeps
    /// ordinary cross-references inside the same chapter visible.
    /// </summary>
    private bool LooksLikeConvertedFootnoteDefinition(XElement element)
    {
        if (element.Name.LocalName is not ("aside" or "section" or "div" or "ol" or "ul" or "li"
            or "p" or "blockquote" or "article") || IsNavigationContext(element)
            || element.Attributes().Any(attribute => attribute.Name.LocalName is "class" or "id" or "type" or "role"
                && FootnoteReferenceIdentityPattern.IsMatch(attribute.Value))) return false;
        var first = LeadingInlineMarkerLink(element);
        if (first is null) return false;

        var href = (string?)first.Attribute("href");
        var fragment = GetHrefFragment(href);
        if (fragment is null || href is null)
        {
            return false;
        }

        // Forward noterefs, raised markers and known reference/definition ID
        // pairs are not backlinks, even when they start the paragraph.
        if (IsSuperscriptMarkerLink(first)
            || ReferenceFragmentIds(first).Any(id => IsLegacyFootnotePair(id, fragment))
            || (HasReferenceMarkup(first) && !FragmentIds(first).Any(id => IsLegacyFootnotePair(fragment, id))))
            return false;
        var target = ReferenceTarget(ResolveHref(href));
        if (target is not null && target.AncestorsAndSelf().Any(HasFootnoteDefinitionMarkup)) return false;

        // A bare "#fragment" points inside this document, not at a note's
        // source reference in another spine item.
        var hash = href.IndexOf('#');
        if (hash <= 0)
        {
            return false;
        }

        var marker = CollapseWhitespace(first.Value);
        if (marker.Length == 0 || !FootnoteMarkerTextPattern.IsMatch(marker))
        {
            return false;
        }

        var text = CollapseWhitespace(element.Value);
        if (text.Length <= marker.Length + 1)
        {
            return false;
        }

        var id = Id(element);
        return id is null || !string.Equals(fragment, id, StringComparison.Ordinal);
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void TrackId(XElement element)
    {
        foreach (var id in FragmentIds(element))
        {
            _fragmentIds.Add(id);
            _pendingFragmentIds.Add(id);
        }
    }

    private CssHints ResolveHints(XElement element)
    {
        if (_resolvedHints.TryGetValue(element, out var cached)) return cached;
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ancestorClasses = element.Ancestors()
            .SelectMany(ancestor => ((string?)ancestor.Attribute("class") ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        var hints = _css?.Resolve(
            element.Name.LocalName.ToLowerInvariant(),
            classes,
            Id(element),
            ancestorClasses) ?? CssHints.None;
        var inline = MicroCss.ParseInlineStyle((string?)element.Attribute("style"));
        var visibility = inline.VisibilitySpecified ? inline.VisibilityHidden : hints.VisibilityHidden;

        var resolved = new CssHints
        {
            Bold = hints.Bold || inline.Bold,
            Italic = hints.Italic || inline.Italic,
            Center = hints.Center || inline.Center,
            AlignRight = hints.AlignRight || inline.AlignRight,
            NoJustify = hints.NoJustify || inline.NoJustify,
            NoWrap = hints.NoWrap || inline.NoWrap,
            Superscript = inline.Superscript ?? hints.Superscript,
            Subscript = inline.Subscript ?? hints.Subscript,
            DisplayNone = inline.DisplayNone ?? hints.DisplayNone,
            VisibilityHidden = visibility
                ?? (element.Parent is { } parent ? ResolveHints(parent).VisibilityHidden : false),
            VisibilitySpecified = inline.VisibilitySpecified || hints.VisibilitySpecified,
            Underline = inline.Underline ?? hints.Underline,
            Strikeout = inline.Strikeout ?? hints.Strikeout,
            LetterSpacingEm = float.IsNaN(inline.LetterSpacingEm)
                ? hints.LetterSpacingEm
                : inline.LetterSpacingEm,
            Casing = inline.Casing ?? hints.Casing,
            SmallCaps = inline.SmallCaps ?? hints.SmallCaps,
            ListStyleType = inline.ListStyleType ?? hints.ListStyleType,
            VerticalTextCombineLimit = inline.VerticalTextCombineLimit >= 0
                ? inline.VerticalTextCombineLimit
                : hints.VerticalTextCombineLimit,
            VerticalTextOrientation = inline.VerticalTextOrientation ?? hints.VerticalTextOrientation,
            TextIndentEm = float.IsNaN(inline.TextIndentEm) ? hints.TextIndentEm : inline.TextIndentEm,
            FontSizeFactor = float.IsNaN(inline.FontSizeFactor) ? hints.FontSizeFactor : inline.FontSizeFactor,
            ImageWidthFactor = float.IsNaN(inline.ImageWidthFactor)
                ? hints.ImageWidthFactor
                : inline.ImageWidthFactor,
            ImageHeightEm = float.IsNaN(inline.ImageHeightEm)
                ? hints.ImageHeightEm
                : inline.ImageHeightEm,
            LineHeightFactor = float.IsNaN(inline.LineHeightFactor)
                ? hints.LineHeightFactor
                : inline.LineHeightFactor,
            MarginTopEm = float.IsNaN(inline.MarginTopEm) ? hints.MarginTopEm : inline.MarginTopEm,
            MarginBottomEm = float.IsNaN(inline.MarginBottomEm)
                ? hints.MarginBottomEm
                : inline.MarginBottomEm,
            MarginLeftEm = float.IsNaN(inline.MarginLeftEm) ? hints.MarginLeftEm : inline.MarginLeftEm,
            MarginRightEm = float.IsNaN(inline.MarginRightEm)
                ? hints.MarginRightEm
                : inline.MarginRightEm,
            PaddingLeftEm = float.IsNaN(inline.PaddingLeftEm)
                ? hints.PaddingLeftEm
                : inline.PaddingLeftEm,
            PaddingRightEm = float.IsNaN(inline.PaddingRightEm)
                ? hints.PaddingRightEm
                : inline.PaddingRightEm,
            BorderTopEm = float.IsNaN(inline.BorderTopEm) ? hints.BorderTopEm : inline.BorderTopEm,
            BorderRightEm = float.IsNaN(inline.BorderRightEm)
                ? hints.BorderRightEm
                : inline.BorderRightEm,
            BorderBottomEm = float.IsNaN(inline.BorderBottomEm)
                ? hints.BorderBottomEm
                : inline.BorderBottomEm,
            BorderLeftEm = float.IsNaN(inline.BorderLeftEm)
                ? hints.BorderLeftEm
                : inline.BorderLeftEm,
        };
        _resolvedHints[element] = resolved;
        return resolved;
    }
}
