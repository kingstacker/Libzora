using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Inline tags, title promotion and footnote links.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private static bool IsInlineTag(string local) =>
        local is "span" or "a" or "b" or "strong" or "em" or "i" or "u" or "s"
            or "strike" or "del" or "ins" or "sub" or "sup" or "ruby" or "rt" or "rp"
            or "small" or "big" or "mark" or "q" or "cite" or "dfn" or "var" or "abbr"
            or "time" or "font" or "label" or "code" or "kbd" or "samp";

    private void MergeAdjacentHeadingBlocks()
    {
        for (var index = 0; index + 1 < _blocks.Count;)
        {
            var first = _blocks[index];
            var second = _blocks[index + 1];
            if (first.Kind != BlockKind.Heading
                || second.Kind != BlockKind.Heading
                || first.Items.Count == 0
                || second.Items.Count == 0)
            {
                index++;
                continue;
            }

            var items = new List<InlineItem>(
                first.Items.Count + second.Items.Count + 1);
            items.AddRange(first.Items);
            items.Add(new InlineItem
            {
                Text = " ",
                TextStart = -1,
                Style = first.Style,
            });
            items.AddRange(second.Items);

            var fragmentIds = first.FragmentIds
                .Concat(second.FragmentIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            _blocks[index] = new ContentBlock
            {
                Kind = BlockKind.Heading,
                ElementId = first.ElementId ?? second.ElementId,
                FragmentIds = fragmentIds,
                FootnoteReferenceFragmentIds = first.FootnoteReferenceFragmentIds
                    .Concat(second.FootnoteReferenceFragmentIds).Distinct(StringComparer.Ordinal).ToArray(),
                Style = first.Style,
                Center = first.Center || second.Center,
                AlignRight = first.AlignRight || second.AlignRight,
                Justify = false,
                TextIndentEm = 0f,
                SpaceBeforeLines = first.SpaceBeforeLines,
                SpaceAfterLines = second.SpaceAfterLines,
                LineHeightFactor = first.LineHeightFactor,
                MarginTopEm = first.MarginTopEm,
                MarginBottomEm = second.MarginBottomEm,
                MarginLeftEm = first.MarginLeftEm,
                MarginRightEm = first.MarginRightEm,
                PaddingLeftEm = first.PaddingLeftEm,
                PaddingRightEm = first.PaddingRightEm,
                Items = items,
            };
            _blocks.RemoveAt(index + 1);
        }
    }

    private void PromoteLeadingTitle()
    {
        if (_blocks.Count < 2)
        {
            return;
        }

        var first = _blocks[0];
        if (first.Kind != BlockKind.Paragraph)
        {
            return;
        }

        // A short paragraph that starts with a decorative image is not a
        // chapter title. The dedication page uses a quote graphic followed by
        // one bold character; promoting that mixed inline line would center
        // and re-space the graphic instead of honoring the EPUB layout.
        if (first.Items.Any(item =>
                item.Kind is not (InlineKind.Text or InlineKind.Ruby) || item.Ghost))
        {
            return;
        }

        var text = string.Concat(first.Items
            .Where(i => !i.Ghost && (i.Kind is InlineKind.Text or InlineKind.Ruby))
            .Select(i => i.Text));
        if (text.Length == 0 || text.Length > 40)
        {
            return;
        }

        _blocks[0] = new ContentBlock
        {
            Kind = BlockKind.Heading,
            ElementId = first.ElementId,
            Style = first.Style with { Bold = true },
            Center = true,
            Justify = false,
            TextIndentEm = 0f,
            SpaceBeforeLines = 1.15f,
            SpaceAfterLines = 1.25f,
            LineHeightFactor = first.LineHeightFactor,
            MarginTopEm = first.MarginTopEm,
            MarginBottomEm = first.MarginBottomEm,
            MarginLeftEm = first.MarginLeftEm,
            MarginRightEm = first.MarginRightEm,
            PaddingLeftEm = first.PaddingLeftEm,
            PaddingRightEm = first.PaddingRightEm,
            FragmentIds = first.FragmentIds,
            FootnoteReferenceFragmentIds = first.FootnoteReferenceFragmentIds,
            Items = first.Items,
        };
    }

    private sealed class PlainFootnoteSection
    {
        public required int MarkerStartOffset { get; init; }
        public required int MarkerEndOffset { get; init; }
        public required int DefinitionStartOffset { get; init; }
        public required int DefinitionEndOffset { get; init; }
        public required IReadOnlyDictionary<string, string> Notes { get; init; }
        /// <summary>Body-text offset of each note's first block, used to jump to the definition.</summary>
        public required IReadOnlyDictionary<string, int> DefinitionOffsets { get; init; }
    }

    /// <summary>Shared by sibling list items; nested lists own a fresh counter.</summary>
    private sealed class ListCounter
    {
        public int Value { get; set; }
    }

    private sealed record FlowContext
    {
        public BlockKind Kind { get; private init; }
        public bool IsQuote { get; private init; }
        public bool Center { get; private init; }
        public bool AlignRight { get; private init; }
        public bool NoJustify { get; private init; }
        public bool Justify { get; private init; }
        public float TextIndentEm { get; private init; }
        public bool ExplicitTextIndent { get; private init; }
        public float SpaceBeforeLines { get; private init; }
        public float SpaceAfterLines { get; private init; }
        public float LineHeightFactor { get; private init; }
        public float MarginTopEm { get; private init; }
        public float MarginBottomEm { get; private init; }
        public float MarginLeftEm { get; private init; }
        public float MarginRightEm { get; private init; }
        public float PaddingLeftEm { get; private init; }
        public float PaddingRightEm { get; private init; }
        public float BorderLeftEm { get; private init; }
        public int HeadingLevel { get; private init; }
        public bool PageBreakBefore { get; private init; }
        public bool PageBreakAfter { get; private init; }
        public bool KeepWithNext { get; private init; }
        public bool IsCaption { get; private init; }
        public TypesetInlineStyle BaseStyle { get; private init; }
        public string? LinkHref { get; private init; }
        public string? FootnoteHref { get; private init; }
        public string? ElementId { get; private init; }
        public bool ParagraphIndentEnabled { get; private init; }
        public int ListLevel { get; private init; }
        public ListCounter? ListCounter { get; private init; }
        public string? ListStyleType { get; private init; }
        public string? ListMarker { get; private init; }

        public static FlowContext Root(bool paragraphIndent) => new()
        {
            Kind = BlockKind.Paragraph,
            Justify = true,
            ParagraphIndentEnabled = paragraphIndent,
            BaseStyle = new TypesetInlineStyle(),
        };

        public FlowContext AsQuote(CssHints? hints = null) => this with
        {
            IsQuote = true,
            Justify = !NoJustify,
            TextIndentEm = ParagraphIndentEnabled ? 2f : 0f,
            SpaceBeforeLines = 0.78f,
            SpaceAfterLines = 0.78f,
            BorderLeftEm = hints is not null && !float.IsNaN(hints.BorderLeftEm) && hints.BorderLeftEm > 0f
                ? Math.Clamp(hints.BorderLeftEm, 0.05f, 1f)
                : BorderLeftEm,
        };

        public FlowContext AsCaption() => this with
        {
            IsCaption = true,
            Center = true,
            NoJustify = true,
            Justify = false,
            TextIndentEm = 0f,
            ExplicitTextIndent = true,
            SpaceBeforeLines = 0.2f,
            SpaceAfterLines = 0.8f,
            BaseStyle = BaseStyle with { FontSizeFactor = ScaleFontSize(BaseStyle.FontSizeFactor, 0.9f) },
        };

        public FlowContext AsPreformatted() => this with
        {
            NoJustify = true,
            Justify = false,
            TextIndentEm = 0f,
            ExplicitTextIndent = true,
            SpaceBeforeLines = 0.6f,
            SpaceAfterLines = 0.6f,
            BaseStyle = BaseStyle with { Monospace = true, PreserveWhitespace = true },
        };

        public FlowContext AsList(string styleType, ListCounter counter, bool indented = true) => this with
        {
            ListLevel = indented ? ListLevel + 1 : ListLevel,
            ListStyleType = styleType,
            ListCounter = counter,
            ListMarker = null,
        };

        public FlowContext WithListMarker(string? marker) => marker is null
            ? this
            : this with { ListMarker = marker, TextIndentEm = 0f };

        private TypesetInlineStyle MergeTextHints(TypesetInlineStyle style, CssHints hints) => style with
        {
            Underline = hints.Underline ?? style.Underline,
            Strikeout = hints.Strikeout ?? style.Strikeout,
            Superscript = hints.Superscript ?? style.Superscript,
            Subscript = hints.Subscript ?? style.Subscript,
            LetterSpacingEm = float.IsNaN(hints.LetterSpacingEm)
                ? style.LetterSpacingEm
                : hints.LetterSpacingEm,
            Casing = hints.Casing ?? style.Casing,
            SmallCaps = hints.SmallCaps ?? style.SmallCaps,
            Monospace = hints.Monospace ?? style.Monospace,
            PreserveWhitespace = hints.PreserveWhitespace ?? style.PreserveWhitespace,
        };

        public FlowContext WithElementId(string? elementId) => this with { ElementId = elementId };

        private static float MarginOrZero(float value) =>
            float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 16f);

        private static float ScaleFontSize(float current, float factor)
        {
            if (float.IsNaN(factor))
            {
                return current;
            }

            // A struct created before this field existed has zero here.
            var baseline = current > 0f ? current : 1f;
            return Math.Clamp(baseline * factor, 0.5f, 3f);
        }

        public FlowContext WithContainerHints(CssHints hints) => this with
        {
            Center = Center || hints.Center,
            AlignRight = AlignRight || hints.AlignRight,
            NoJustify = NoJustify || hints.NoJustify,
            // `line-height` inherits, so a styled section sets it for its
            // paragraphs. Margins do not: applying a container's margin to
            // every child paragraph would multiply the spacing.
            LineHeightFactor = float.IsNaN(hints.LineHeightFactor)
                ? LineHeightFactor
                : hints.LineHeightFactor,
            BaseStyle = MergeTextHints(BaseStyle, hints) with
            {
                FontSizeFactor = ScaleFontSize(BaseStyle.FontSizeFactor, hints.FontSizeFactor),
                VerticalTextCombineLimit = hints.VerticalTextCombineLimit >= 0
                    ? hints.VerticalTextCombineLimit
                    : BaseStyle.VerticalTextCombineLimit,
                VerticalTextOrientation = hints.VerticalTextOrientation ?? BaseStyle.VerticalTextOrientation,
            },
        };

        public FlowContext ForBlock(BlockKind kind, CssHints hints, string? elementId, int headingLevel = 0)
        {
            var isHeading = kind == BlockKind.Heading;
            var preserveWs = hints.PreserveWhitespace ?? BaseStyle.PreserveWhitespace;
            var hasExplicitIndent = !float.IsNaN(hints.TextIndentEm) && float.IsFinite(hints.TextIndentEm);
            var indented = !isHeading
                && !hints.Center
                && !preserveWs
                && !IsCaption
                && ParagraphIndentEnabled
                && kind == BlockKind.Paragraph;
            var textIndentEm = indented
                ? hasExplicitIndent ? Math.Clamp(hints.TextIndentEm, 0f, 4f) : 2f
                : 0f;
            var effectiveHeadingScale = isHeading && float.IsNaN(hints.FontSizeFactor)
                ? headingLevel switch
                {
                    1 => 1.35f,
                    2 => 1.22f,
                    3 => 1.12f,
                    4 => 1.05f,
                    _ => 1f,
                }
                : hints.FontSizeFactor;
            var borderLeftEm = !float.IsNaN(hints.BorderLeftEm) && hints.BorderLeftEm > 0f
                ? Math.Clamp(hints.BorderLeftEm, 0.05f, 1f)
                : BorderLeftEm;
            return this with
            {
                Kind = kind,
                ElementId = elementId,
                HeadingLevel = isHeading ? headingLevel : 0,
                Center = isHeading || hints.Center || (IsCaption && !hints.AlignRight && !hints.NoJustify),
                AlignRight = AlignRight || hints.AlignRight,
                NoJustify = NoJustify || hints.NoJustify || preserveWs,
                Justify = !isHeading && !hints.Center && !hints.NoJustify && !NoJustify && !preserveWs && !IsCaption,
                TextIndentEm = textIndentEm,
                ExplicitTextIndent = hasExplicitIndent || ExplicitTextIndent,
                SpaceBeforeLines = isHeading ? 1.15f : SpaceBeforeLines,
                SpaceAfterLines = isHeading ? 1.25f : SpaceAfterLines,
                LineHeightFactor = float.IsNaN(hints.LineHeightFactor)
                    ? LineHeightFactor
                    : hints.LineHeightFactor,
                MarginTopEm = MarginOrZero(hints.MarginTopEm),
                MarginBottomEm = MarginOrZero(hints.MarginBottomEm),
                MarginLeftEm = MarginOrZero(hints.MarginLeftEm),
                MarginRightEm = MarginOrZero(hints.MarginRightEm),
                PaddingLeftEm = MarginOrZero(hints.PaddingLeftEm),
                PaddingRightEm = MarginOrZero(hints.PaddingRightEm),
                BorderLeftEm = borderLeftEm,
                PageBreakBefore = hints.PageBreakBefore,
                PageBreakAfter = hints.PageBreakAfter,
                KeepWithNext = isHeading || hints.PageBreakAfterAvoid,
                BaseStyle = MergeTextHints(isHeading ? BaseStyle with { Bold = true } : BaseStyle, hints) with
                {
                    FontSizeFactor = ScaleFontSize(BaseStyle.FontSizeFactor, effectiveHeadingScale),
                    VerticalTextCombineLimit = hints.VerticalTextCombineLimit >= 0
                        ? hints.VerticalTextCombineLimit
                        : BaseStyle.VerticalTextCombineLimit,
                    VerticalTextOrientation = hints.VerticalTextOrientation ?? BaseStyle.VerticalTextOrientation,
                },
            };
        }

        /// <summary>
        /// Cell text ignores paragraph indentation and outer margins: the
        /// table composer supplies its own padding and column geometry.
        /// </summary>
        public FlowContext ForTableCell(CssHints hints, bool header)
        {
            var noJustify = NoJustify || hints.NoJustify;
            return this with
            {
                Kind = BlockKind.Paragraph,
                ElementId = null,
                Center = hints.Center,
                AlignRight = hints.AlignRight,
                NoJustify = noJustify,
                Justify = !hints.Center && !noJustify,
                TextIndentEm = 0f,
                SpaceBeforeLines = 0f,
                SpaceAfterLines = 0f,
                MarginTopEm = 0f,
                MarginBottomEm = 0f,
                MarginLeftEm = 0f,
                MarginRightEm = 0f,
                PaddingLeftEm = 0f,
                PaddingRightEm = 0f,
                LineHeightFactor = float.IsNaN(hints.LineHeightFactor)
                    ? LineHeightFactor
                    : hints.LineHeightFactor,
                BaseStyle = MergeTextHints(BaseStyle, hints) with
                {
                    Bold = BaseStyle.Bold || hints.Bold || header,
                    Italic = BaseStyle.Italic || hints.Italic,
                    FontSizeFactor = ScaleFontSize(BaseStyle.FontSizeFactor, hints.FontSizeFactor),
                    VerticalTextCombineLimit = hints.VerticalTextCombineLimit >= 0
                        ? hints.VerticalTextCombineLimit
                        : BaseStyle.VerticalTextCombineLimit,
                    VerticalTextOrientation = hints.VerticalTextOrientation
                        ?? BaseStyle.VerticalTextOrientation,
                },
            };
        }

        public FlowContext ForLink(string? href, bool isFootnoteReference, bool isBacklink)
        {
            if (string.IsNullOrWhiteSpace(href)) return this;
            if (isBacklink)
            {
                return this with
                {
                    LinkHref = href,
                    FootnoteHref = null,
                    BaseStyle = BaseStyle with { NoWrap = true, Superscript = false, Subscript = false },
                };
            }
            if (!isFootnoteReference)
                return this with { LinkHref = href, FootnoteHref = null };
            return this with
            {
                LinkHref = null,
                FootnoteHref = href,
                BaseStyle = BaseStyle with { NoWrap = true, Superscript = true, Subscript = false },
            };
        }

        public FlowContext WithStyle(TypesetInlineStyle style) => this with { BaseStyle = style };
    }

    private FlowContext LinkContext(FlowContext context, string? href, XElement element)
    {
        var isBacklink = IsFootnoteDefinitionBacklink(href, element);
        return context.ForLink(href, !isBacklink && IsFootnoteReferenceLink(href, element), isBacklink);
    }

    private bool IsFootnoteReferenceLink(string? href, XElement element)
    {
        // Inspect fragment IDs and markup, never the entire absolute path.
        // A book named "footnotes" must not turn every chapter link into a note.
        if (!Uri.TryCreate(href, UriKind.Absolute, out var targetUri) || !targetUri.IsFile
            || string.IsNullOrEmpty(targetUri.Fragment) || IsNavigationContext(element)) return false;
        if (HasReferenceMarkup(element)) return true;
        var target = ReferenceTarget(href);
        var localTarget = string.Equals(targetUri.LocalPath, _chapterPath, PathComparison);
        // Duokan-style EPUBs use a small image with an empty alt as the whole
        // reference, e.g. <a id="A_1" href="#B_1"><img alt=""/></a>. The target
        // being an explicit note definition is the signal, not the link text.
        if (target is not null && !IsNavigationContext(target)
            && target.AncestorsAndSelf().Any(node => localTarget
                ? IsFootnoteDefinition(node) : HasFootnoteDefinitionMarkup(node))) return true;
        if (!IsMarker(element.Value)) return false;

        if (target is not null && (IsNavigationContext(target)
            || target.AncestorsAndSelf().Any(node => node.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")))
            return false;

        var fragment = GetHrefFragment(href);
        return fragment is not null && (FootnoteReferencePattern.IsMatch(fragment)
            || ReferenceFragmentIds(element).Any(id => IsLegacyFootnotePair(id, fragment))
            || IsSuperscriptMarkerLink(element));
    }

    private static bool HasReferenceMarkup(XElement element) => element.AncestorsAndSelf()
        .TakeWhile(node => node == element || IsInlineTag(node.Name.LocalName))
        .Any(node => node.Attributes().Any(attribute =>
            attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id"
            && FootnoteReferencePattern.IsMatch(attribute.Value)));

    private static bool IsLegacyFootnotePair(string reference, string definition)
    {
        if (Regex.IsMatch(reference, @"^z\d+$", RegexOptions.CultureInvariant)) return definition == "b" + reference;
        if (Regex.IsMatch(reference, @"^w\d+$", RegexOptions.CultureInvariant)) return definition == "m" + reference[1..];
        if (Regex.IsMatch(reference, @"^A[_-]\d+$", RegexOptions.CultureInvariant)) return definition == "B" + reference[1..];
        if (Regex.IsMatch(reference, @"^id\d+$", RegexOptions.CultureInvariant)) return definition == reference + "a";
        if (Regex.IsMatch(reference, @"^(?:notes?|fn|ftn)[-_:]?\d+$", RegexOptions.CultureInvariant)) return definition == reference + "n";
        if (reference.StartsWith("commentRef_", StringComparison.Ordinal)) return definition == "comment_" + reference[11..];
        if (reference.Contains("_noteBack_", StringComparison.Ordinal)) return definition == reference.Replace("_noteBack_", "_note_", StringComparison.Ordinal);
        if (reference.StartsWith("fnref", StringComparison.Ordinal)) return definition == "fn" + reference[5..];
        if (Regex.IsMatch(reference, @"^zww\d+$", RegexOptions.CultureInvariant)) return definition == "zw" + reference[3..];
        if (reference.StartsWith("linknoteref-", StringComparison.Ordinal))
            return definition == "linknote-" + reference["linknoteref-".Length..];
        return false;
    }

    private bool IsSuperscriptMarkerLink(XElement element)
    {
        if (!IsMarker(element.Value)) return false;
        // CSS-created superscripts are common in Chinese EPUBs. Stop at the
        // containing block instead of inheriting unrelated container metadata.
        return element.AncestorsAndSelf().TakeWhile(node => node == element || IsInlineTag(node.Name.LocalName)).Any(IsSuperscript)
            || element.Descendants().Any(IsSuperscript);

        bool IsSuperscript(XElement node) => node.Name.LocalName == "sup" || ResolveHints(node).Superscript == true;
    }

    private bool IsFootnoteDefinitionBacklink(string? href, XElement element)
    {
        var role = string.Join(' ', element.Attributes()
            .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class")
            .Select(attribute => attribute.Value));
        if (role.Contains("backlink", StringComparison.OrdinalIgnoreCase)) return true;
        var fragment = GetHrefFragment(href);
        if (fragment is null) return false;
        if (FragmentIds(element).Any(id => IsLegacyFootnotePair(fragment, id))) return true;
        // Gutenberg's reverse link is <a id="linknote-3" href="#linknoteref-3">.
        // It belongs to the note text and must stay a normal link, otherwise
        // the notes chapter grows a second set of markers that resolve to the
        // body paragraph rather than the note.
        if (FragmentIds(element).Any(id => id.StartsWith("linknote-", StringComparison.Ordinal)
            && fragment == "linknoteref-" + id["linknote-".Length..])) return true;
        if (!IsMarker(element.Value) || !element.Ancestors().Any(IsFootnoteDefinition)) return false;
        // A real noteref inside a definition can open another note; do not
        // confuse it with the leading link that returns to this note's source.
        if (element.Attributes().Any(attribute => attribute.Name.LocalName is "type" or "role" or "rel"
            && attribute.Value.Contains("noteref", StringComparison.OrdinalIgnoreCase))) return false;
        var target = LocalTarget(href);
        return target is null || !target.AncestorsAndSelf().Any(IsFootnoteDefinition);
    }

    private static string? GetHrefFragment(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var hash = href.IndexOf('#');
        if (hash < 0 || hash == href.Length - 1)
        {
            return null;
        }

        var fragment = href[(hash + 1)..];
        try
        {
            return Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            return fragment;
        }
    }
}
