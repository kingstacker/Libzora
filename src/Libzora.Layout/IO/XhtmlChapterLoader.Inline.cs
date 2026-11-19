using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Inline traversal, ruby and footnote markers.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private void WalkBlockParagraph(XElement element, BlockKind kind, FlowContext ctx, string? listMarker = null)
    {
        var id = Id(element);
        var hints = ResolveHints(element);
        if (hints.VisibilityHidden == true) listMarker = null;
        var paragraphCtx = ctx.ForBlock(kind, hints, id).WithListMarker(listMarker);
        if (listMarker is not null)
        {
            // The marker is synthetic: TextStart -1 keeps it out of the body
            // offset stream used by annotations, search and TTS.
            _pending.Add(new InlineItem
            {
                Kind = InlineKind.Text,
                Text = listMarker,
                TextStart = -1,
                Style = paragraphCtx.BaseStyle with
                {
                    NoWrap = true,
                    Underline = false,
                    Strikeout = false,
                    Superscript = false,
                    Subscript = false,
                    Casing = TypesetTextCasing.None,
                    SmallCaps = false,
                },
            });
        }
        WalkInlineContent(element, paragraphCtx);
        FlushParagraph(paragraphCtx);
    }

    private void WalkInlineContent(XElement element, FlowContext ctx)
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
                    WalkInlineElement(child, ctx);
                    break;
            }
        }
    }

    private void WalkInlineElement(XElement element, FlowContext ctx)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        if (IsDisplayNone(element))
        {
            return;
        }
        if (ResolveHints(element).VisibilityHidden == true && local is "img" or "svg" or "hr" or "br") return;
        // Inside a table cell, flushing would emit a separate block and drop
        // the cell text collected so far; keep the note inline instead.
        if (!_buildingTableCells
            && IsFootnoteDefinition(element)
            && !ContainsVisibleFootnoteTarget(element))
        {
            FlushParagraph(ctx);
            PreserveFootnoteDefinition(element);
            return;
        }

        switch (local)
        {
            case "table":
                if (_buildingTableCells)
                {
                    // A nested table cannot become its own block while the
                    // outer cell is being collected; keep its text inline.
                    TrackId(element);
                    AppendInlineElement(element, ctx);
                    break;
                }

                FlushParagraph(ctx);
                TrackId(element);
                AddTableBlock(element, ctx);
                FlushParagraph(ctx);
                break;
            case "ul":
            case "ol":
                if (_buildingTableCells)
                {
                    // Block flow would flush the cell's pending text into a
                    // separate ContentBlock; keep lists inline in table cells.
                    TrackId(element);
                    AppendInlineElement(element, ctx);
                    break;
                }

                // A TOC often nests the chapter list inside its volume's li.
                // Preserve those block boundaries instead of flattening every
                // descendant link into the parent list item's paragraph.
                FlushParagraph(ctx);
                TrackId(element);
                var nestedList = ListContext(element, ctx, local == "ol");
                WalkFlow(element, nestedList);
                FlushParagraph(nestedList);
                break;
            case "br":
                TrackId(element);
                _pending.Add(new InlineItem
                {
                    Kind = InlineKind.LineBreak,
                    TextStart = -1,
                    Style = ctx.BaseStyle,
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
                    // FlushParagraph consumes fragment ids for the text that
                    // precedes an inline image. Track the image afterwards so
                    // an id on <img> (or on an enclosing link) resolves to the
                    // image page rather than the preceding paragraph.
                    FlushParagraph(ctx);
                    TrackId(element);
                    AddImageBlock(element, ctx);
                }
                break;
            case "svg":
                FlushParagraph(ctx);
                TrackId(element);
                AddSvgImageBlock(element, ctx);
                break;
            case "ruby":
                TrackId(element);
                AppendRuby(element, ctx);
                break;
            case "rt":
            case "rp":
                TrackId(element);
                AppendGhost(element);
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
            case "style":
            case "script":
            case "head":
            case "title":
                break;
            case "a":
            {
                TrackId(element);
                var href = ResolveHref((string?)element.Attribute("href"));
                var inner = LinkContext(ctx, href, element);
                if (inner.FootnoteHref is not null)
                    _footnoteReferenceFragmentIds.UnionWith(FragmentIds(element));
                WalkInlineContent(element, inner);
                break;
            }
            default:
                TrackId(element);
                AppendInlineElement(element, ctx);
                break;
        }
    }

    private void AppendInlineElement(XElement element, FlowContext ctx)
    {
        if (IsDisplayNone(element)) return;
        var local = element.Name.LocalName.ToLowerInvariant();
        var style = ctx.BaseStyle;

        switch (local)
        {
            case "b":
            case "strong":
                style = style with { Bold = true };
                break;
            case "em":
            case "i":
            case "cite":
            case "dfn":
            case "var":
                style = style with { Italic = true };
                break;
            case "u":
            case "ins":
                style = style with { Underline = true };
                break;
            case "s":
            case "strike":
            case "del":
                style = style with { Strikeout = true };
                break;
            case "sup":
                style = style with { Superscript = true, Subscript = false };
                break;
            case "sub":
                style = style with { Subscript = true, Superscript = false };
                break;
            case "code":
            case "kbd":
            case "samp":
                style = style with { Monospace = true };
                break;
        }

        var hints = ResolveHints(element);
        if (hints.Bold)
        {
            style = style with { Bold = true };
        }

        if (hints.Italic)
        {
            style = style with { Italic = true };
        }

        if (hints.NoWrap)
        {
            style = style with { NoWrap = true };
        }

        if (hints.Monospace is { } monospace)
        {
            style = style with { Monospace = monospace };
        }

        if (hints.PreserveWhitespace is { } preserveWs)
        {
            style = style with { PreserveWhitespace = preserveWs };
        }

        if (hints.Superscript is { } superscript)
        {
            style = style with { Superscript = superscript, Subscript = superscript ? false : style.Subscript };
        }

        if (hints.Subscript is { } subscript)
        {
            style = style with { Subscript = subscript, Superscript = subscript ? false : style.Superscript };
        }

        if (hints.Underline is { } underline) style = style with { Underline = underline };
        if (hints.Strikeout is { } strikeout) style = style with { Strikeout = strikeout };
        if (!float.IsNaN(hints.LetterSpacingEm)) style = style with { LetterSpacingEm = hints.LetterSpacingEm };
        if (hints.Casing is { } casing) style = style with { Casing = casing };
        if (hints.SmallCaps is { } smallCaps) style = style with { SmallCaps = smallCaps };

        if (hints.VerticalTextCombineLimit >= 0)
        {
            style = style with { VerticalTextCombineLimit = hints.VerticalTextCombineLimit };
        }

        if (hints.VerticalTextOrientation is { } orientation)
        {
            style = style with { VerticalTextOrientation = orientation };
        }

        if (!float.IsNaN(hints.FontSizeFactor))
        {
            // CSS font-size is relative to the parent, so multiply rather
            // than replace (nested spans accumulate).
            var current = style.FontSizeFactor > 0f ? style.FontSizeFactor : 1f;
            style = style with
            {
                FontSizeFactor = Math.Clamp(current * hints.FontSizeFactor, 0.5f, 3f),
            };
        }

        WalkInlineContent(element, ctx.WithStyle(style));
    }

    private void AppendGhost(XElement element) => WalkGhost(element);

    private void AppendRuby(XElement element, FlowContext ctx)
    {
        // The generated pinyin EPUB uses the compact form
        // <ruby>汉<rt>hàn</rt></ruby>. Keep the base and annotation in the
        // original body-text coordinate space, while exposing only the base
        // as a normal text range and carrying the pronunciation separately
        // for the native painter.
        foreach (var child in element.DescendantsAndSelf().Where(IsVisible))
        {
            TrackId(child);
        }

        var baseText = string.Concat(element
            .DescendantNodes()
            .OfType<XText>()
            .Where(text => IsVisible(text.Parent!) && !text.Ancestors().Any(IsRubyAnnotation))
            .Select(text => text.Value));
        var rubyText = string.Concat(element
            .Descendants()
            .Where(child => child.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase))
            .DescendantNodes()
            .OfType<XText>()
            .Where(text => IsVisible(text.Parent!))
            .Select(text => text.Value));
        var annotationMarkupText = string.Concat(element
            .DescendantNodes()
            .OfType<XText>()
            .Where(text => IsVisible(text.Parent!) && text.Ancestors().Any(IsRubyAnnotation))
            .Select(text => text.Value));

        if (baseText.Length == 0)
        {
            // Malformed ruby without a base should still preserve its text
            // for search and TTS, but must not create an empty visual item.
            if (annotationMarkupText.Length > 0)
            {
                AppendBodyText(annotationMarkupText);
            }

            return;
        }

        var start = AppendBodyText(baseText);
        if (annotationMarkupText.Length > 0)
        {
            var annotationStart = AppendBodyText(annotationMarkupText);
            _rubyAnnotationRanges.Add((annotationStart, annotationMarkupText.Length));
        }

        _pending.Add(new InlineItem
        {
            Kind = InlineKind.Ruby,
            Text = baseText,
            TextStart = start,
            Style = ctx.BaseStyle,
            LinkHref = ctx.LinkHref,
            FootnoteHref = ctx.FootnoteHref,
            RubyText = string.IsNullOrWhiteSpace(rubyText) ? null : rubyText,
        });
    }

    private static bool IsRubyAnnotation(XElement element) =>
        element.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase)
        || element.Name.LocalName.Equals("rp", StringComparison.OrdinalIgnoreCase);

    private void WalkGhost(XElement element)
    {
        if (IsDisplayNone(element) || element.Name.LocalName is "style" or "script" or "head" or "title") return;
        foreach (var node in element.Nodes())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case XText text when ResolveHints(element).VisibilityHidden != true:
                    AppendBodyText(text.Value);
                    break;
                case XElement child:
                    WalkGhost(child);
                    break;
            }
        }
    }

    private bool ContainsVisibleFootnoteTarget(XElement element) =>
        !string.IsNullOrEmpty(_visibleFootnoteFragment)
        && element.DescendantsAndSelf().Any(node => FragmentIds(node).Contains(_visibleFootnoteFragment));

    private void PreserveFootnoteDefinition(XElement element)
    {
        var start = _body.Length;

        // An id can be pending because it belongs to the body or to a
        // wrapper immediately before the hidden definition. Keep those
        // anchors resolvable even though no visible block consumes them.
        foreach (var fragmentId in _pendingFragmentIds)
        {
            _fragmentTextOffsets.TryAdd(fragmentId, start);
        }

        var definitionIds = element
            .DescendantsAndSelf()
            .SelectMany(FragmentIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var fragmentId in definitionIds)
        {
            _fragmentIds.Add(fragmentId);
            _fragmentTextOffsets.TryAdd(fragmentId, start);
        }

        // Keep the exact text coordinate space used by annotations and
        // search, without creating any InlineItems for the footnote body.
        WalkGhost(element);
        _pendingFragmentIds.Clear();
    }
}
