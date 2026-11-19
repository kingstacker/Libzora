using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Flow walking, lists, visibility and text appending.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private void WalkFlow(XElement element, FlowContext ctx)
    {
        foreach (var node in element.Nodes())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case XText text:
                    // visibility is inherited, but a descendant may explicitly
                    // restore it. Skip this element's text, not its children.
                    if (ResolveHints(element).VisibilityHidden != true)
                        AppendFlowText(text.Value, ctx);
                    break;
                case XElement child:
                    HandleFlowElement(child, ctx);
                    break;
            }
        }
    }

    private void HandleFlowElement(XElement element, FlowContext ctx)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        if (IsDisplayNone(element))
        {
            // display:none content is neither laid out nor part of the offset
            // stream: publishers use it for duplicate TOCs and print-only text.
            return;
        }
        if (ResolveHints(element).VisibilityHidden == true && local is "img" or "svg" or "hr" or "br") return;

        if (IsFootnoteDefinition(element) && !ContainsVisibleFootnoteTarget(element))
        {
            FlushParagraph(ctx);
            PreserveFootnoteDefinition(element);
            return;
        }

        switch (local)
        {
            case "p":
                FlushParagraph(ctx);
                TrackId(element);
                var isSpacingParagraph = IsSpacingParagraph(element);
                var previousBlockCount = _blocks.Count;
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx);
                if (isSpacingParagraph && _blocks.Count == previousBlockCount + 1
                    && _blocks[^1].FragmentIds.Count == 0)
                {
                    _blocks.RemoveAt(_blocks.Count - 1);
                }
                break;
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.Heading, ctx);
                break;
            case "li":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.ListItem, ctx,
                    BuildListMarker(element, ResolveHints(element), ctx));
                break;
            case "dd":
            case "dt":
            case "figcaption":
            case "caption":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx);
                break;
            case "pre":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx.AsPreformatted());
                break;
            case "blockquote":
                FlushParagraph(ctx);
                TrackId(element);
                WalkFlow(element, ctx.AsQuote());
                FlushParagraph(ctx.AsQuote());
                break;
            case "div":
                FlushParagraph(ctx);
                TrackId(element);
                var containerContext = ctx.WithContainerHints(ResolveHints(element));
                WalkFlow(element, containerContext);
                FlushParagraph(containerContext);
                break;
            case "table":
                FlushParagraph(ctx);
                TrackId(element);
                AddTableBlock(element, ctx);
                FlushParagraph(ctx);
                break;
            case "ul":
            case "ol":
                FlushParagraph(ctx);
                TrackId(element);
                var listContext = ListContext(element, ctx, local == "ol");
                WalkFlow(element, listContext);
                FlushParagraph(listContext);
                break;
            case "thead":
            case "tbody":
            case "tr":
            case "td":
            case "th":
            case "figure":
                FlushParagraph(ctx);
                TrackId(element);
                WalkFlow(element, ctx);
                break;
            case "hr":
                FlushParagraph(ctx);
                TrackId(element);
                _blocks.Add(new ContentBlock
                {
                    Kind = BlockKind.Rule,
                    ElementId = Id(element),
                    FragmentIds = TakePendingFragmentIds(),
                });
                break;
            case "img":
                if (ctx.FootnoteHref is not null || IsFootnoteImage(element))
                {
                    TrackId(element);
                    AddFootnoteMarker(element, ctx);
                }
                else if (IsInlineImage(element))
                {
                    TrackId(element);
                    AddInlineImage(element, ctx);
                }
                else
                {
                    FlushParagraph(ctx);
                    TrackId(element);
                    AddImageBlock(element, ctx);
                }
                break;
            case "a":
                // Anchors are normally handled by WalkInlineElement inside a
                // paragraph. Some EPUBs put a cover/image anchor directly in
                // a figure or body, so carry its resolved target through the
                // flow walker as well.
                FlushParagraph(ctx);
                TrackId(element);
                WalkFlow(element, LinkContext(ctx, ResolveHref((string?)element.Attribute("href")), element));
                FlushParagraph(ctx);
                break;
            case "style":
            case "script":
            case "head":
            case "title":
                break;
            case "svg":
                FlushParagraph(ctx);
                TrackId(element);
                AddSvgImageBlock(element, ctx);
                break;
            default:
                if (IsInlineTag(local))
                {
                    TrackId(element);
                    AppendInlineElement(element, ctx);
                }
                else
                {
                    FlushParagraph(ctx);
                    TrackId(element);
                    WalkFlow(element, ctx);
                }

                break;
        }
    }

    private FlowContext ListContext(XElement element, FlowContext ctx, bool ordered)
    {
        var hints = ResolveHints(element);
        // TOC lists already carry chapter titles; generated "1." markers
        // would duplicate the printed numbers and add unwanted indentation.
        var navigation = IsNavigationContext(element) || LooksLikeNavigationList(element);
        var style = navigation ? "none" : hints.ListStyleType ?? (ordered ? "decimal" : "disc");
        var start = 0;
        if (ordered && int.TryParse((string?)element.Attribute("start"), out var explicitStart))
            start = explicitStart - 1;
        return ctx.AsList(style, new ListCounter { Value = start }, indented: !navigation);
    }

    /// <summary>
    /// A plain list whose every item is essentially a cross-document link is
    /// a hand-built table of contents. Generated bullets/numbers would be
    /// visually wrong even though the markup lacks nav/epub:type.
    /// </summary>
    private static bool LooksLikeNavigationList(XElement list)
    {
        var items = list.Elements().Where(child => child.Name.LocalName == "li").ToArray();
        if (items.Length == 0) return false;
        foreach (var item in items)
        {
            var link = item.Elements().FirstOrDefault(child => child.Name.LocalName == "a");
            if (link is null) return false;
            var href = (string?)link.Attribute("href");
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#')
                || !Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var target)
                || (target.IsAbsoluteUri && !target.IsFile)) return false;
            var linkText = CollapseWhitespace(link.Value);
            var itemText = CollapseWhitespace(item.Value);
            var nestedText = item.Elements()
                .Where(child => child.Name.LocalName is "ul" or "ol")
                .Sum(child => CollapseWhitespace(child.Value).Length);
            if (linkText.Length == 0 || itemText.Length - nestedText > linkText.Length * 2 + 4)
                return false;
        }
        return true;
    }

    private string? BuildListMarker(XElement item, CssHints hints, FlowContext ctx)
    {
        if (ctx.ListCounter is null || ctx.ListStyleType is null) return null;
        var style = hints.ListStyleType ?? ctx.ListStyleType;
        var number = ctx.ListCounter.Value + 1;
        if (int.TryParse((string?)item.Attribute("value"), out var explicitValue))
            number = explicitValue;
        ctx.ListCounter.Value = number;
        return style == "none" ? null : FormatListMarker(style, number) + " ";
    }

    private static string FormatListMarker(string style, int number)
    {
        return style switch
        {
            "disc" => "•",
            "circle" => "○",
            "square" => "■",
            "decimal-leading-zero" => number.ToString("00", CultureInfo.InvariantCulture) + ".",
            "lower-alpha" or "lower-latin" => AlphaNumber(number).ToLowerInvariant() + ".",
            "upper-alpha" or "upper-latin" => AlphaNumber(number).ToUpperInvariant() + ".",
            "lower-roman" => RomanNumber(number).ToLowerInvariant() + ".",
            "upper-roman" => RomanNumber(number) + ".",
            "cjk-ideographic" or "cjk-decimal" or "chinese-counting" => ChineseNumber(number),
            _ => number.ToString(CultureInfo.InvariantCulture) + ".",
        };
    }

    private static string AlphaNumber(int number)
    {
        if (number is >= 1 and <= 26) return ((char)('a' + number - 1)).ToString();
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static string RomanNumber(int number)
    {
        if (number is < 1 or > 3999) return number.ToString(CultureInfo.InvariantCulture);
        var values = new (int Value, string Text)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        };
        var builder = new StringBuilder();
        foreach (var (value, text) in values)
        {
            while (number >= value)
            {
                builder.Append(text);
                number -= value;
            }
        }
        return builder.ToString();
    }

    private static string ChineseNumber(int number)
    {
        if (number is < 1 or > 99) return number.ToString(CultureInfo.InvariantCulture);
        var digits = "〇一二三四五六七八九";
        if (number < 10) return digits[number].ToString();
        var tens = number / 10;
        var ones = number % 10;
        return (tens == 1 ? "十" : digits[tens] + "十") + (ones == 0 ? string.Empty : digits[ones].ToString());
    }

    private bool IsDisplayNone(XElement element)
    {
        var hidden = element.Attribute("hidden");
        if (hidden is not null && !hidden.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return true;
        return ResolveHints(element).DisplayNone == true;
    }

    private bool IsVisible(XElement element) => ResolveHints(element).VisibilityHidden != true
        && !element.AncestorsAndSelf().Any(IsDisplayNone);

    private static bool IsSpacingParagraph(XElement element)    {
        var children = element.Elements().ToArray();
        if (children.Length != 1
            || !children[0].Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(element.Value)
            || element.DescendantsAndSelf().Any(e => e.Attributes().Any(a =>
                a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        static XElement? AdjacentElement(XNode? node, bool previous)
        {
            while (node is not null && node is not XElement)
                node = previous ? node.PreviousNode : node.NextNode;
            return node as XElement;
        }

        static bool IsTextParagraph(XElement? sibling) =>
            sibling is not null
            && sibling.Name.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(sibling.Value);

        return IsTextParagraph(AdjacentElement(element.PreviousNode, previous: true))
            && IsTextParagraph(AdjacentElement(element.NextNode, previous: false));
    }

    private void AppendFlowText(string value, FlowContext ctx)
    {
        if (value.Length == 0)
        {
            return;
        }

        var start = AppendBodyText(value);
        foreach (var fragmentId in _pendingFragmentIds)
        {
            _fragmentTextOffsets.TryAdd(fragmentId, start);
        }

        if (value.Length > 0)
        {
            if (ctx.FootnoteHref is null
                && (ctx.BaseStyle.Casing != TypesetTextCasing.None || ctx.BaseStyle.SmallCaps))
            {
                AppendCasedText(value, start, ctx);
                return;
            }
            _pending.Add(new InlineItem
            {
                // Keep a linked footnote reference such as "[3]" together.
                // If it is emitted as ordinary text, the cell factory splits
                // the brackets and number into separate layout cells, which
                // breaks superscript alignment (especially in vertical mode).
                Kind = ctx.FootnoteHref is not null
                    ? InlineKind.FootnoteMarker
                    : InlineKind.Text,
                Text = value,
                TextStart = start,
                Style = ctx.BaseStyle,
                LinkHref = ctx.LinkHref,
                FootnoteHref = ctx.FootnoteHref,
            });
        }
    }

    /// <summary>
    /// Applies publisher text-transform/small-caps to the visible text only.
    /// BodyText keeps the original characters so annotation offsets, search
    /// and TTS still address the published source. ASCII case mapping keeps
    /// one output character per source character.
    /// </summary>
    private void AppendCasedText(string value, int start, FlowContext ctx)
    {
        var style = ctx.BaseStyle;
        var builder = new StringBuilder();
        var segmentStart = 0;
        var segmentFactor = 1f;
        var capitalizeNext = style.Casing == TypesetTextCasing.Capitalize;

        void Flush(int end)
        {
            if (builder.Length == 0) return;
            var baseFactor = style.FontSizeFactor > 0f ? style.FontSizeFactor : 1f;
            _pending.Add(new InlineItem
            {
                Kind = InlineKind.Text,
                Text = builder.ToString(),
                TextStart = start + segmentStart,
                Style = style with
                {
                    FontSizeFactor = Math.Clamp(baseFactor * segmentFactor, 0.5f, 3f),
                },
                LinkHref = ctx.LinkHref,
                FootnoteHref = ctx.FootnoteHref,
            });
            builder.Clear();
            segmentStart = end;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var source = value[index];
            var displayed = source;
            var lower = char.IsAsciiLetterLower(source);
            switch (style.Casing)
            {
                case TypesetTextCasing.Uppercase:
                    displayed = char.ToUpperInvariant(source);
                    break;
                case TypesetTextCasing.Lowercase:
                    displayed = char.ToLowerInvariant(source);
                    break;
                case TypesetTextCasing.Capitalize:
                    if (char.IsAsciiLetter(source))
                    {
                        if (capitalizeNext) displayed = char.ToUpperInvariant(source);
                        capitalizeNext = false;
                    }
                    else if (char.IsWhiteSpace(source))
                    {
                        capitalizeNext = true;
                    }
                    break;
            }

            var factor = 1f;
            if (style.SmallCaps && lower)
            {
                displayed = char.ToUpperInvariant(source);
                factor = 0.78f;
            }

            if (builder.Length > 0 && Math.Abs(factor - segmentFactor) > 0.001f)
                Flush(index);
            if (builder.Length == 0) segmentFactor = factor;
            builder.Append(displayed);
        }
        Flush(value.Length);
    }
}
