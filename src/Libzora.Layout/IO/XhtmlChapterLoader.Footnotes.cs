using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Footnote markers and image blocks.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private void AddFootnoteMarker(XElement element, FlowContext ctx)
    {
        var href = ctx.FootnoteHref ?? CreateInlineFootnoteHref(element);
        _pending.Add(new InlineItem
        {
            Kind = InlineKind.FootnoteMarker,
            Text = "注",
            TextStart = -1,
            Style = ctx.BaseStyle with
            {
                Superscript = true,
                NoWrap = true,
            },
            FootnoteHref = href,
            FootnoteText = GetFootnoteText(element),
        });
    }

    private string CreateInlineFootnoteHref(XElement element)
    {
        var fragment = Id(element);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            fragment = $"__libzora-inline-footnote-{_fragmentIds.Count}-{_body.Length}-{_pending.Count}";
        }

        return new UriBuilder(new Uri(_chapterPath, UriKind.Absolute))
        {
            Fragment = fragment,
        }.Uri.AbsoluteUri;
    }

    private static string? GetFootnoteText(XElement element)
    {
        // Converter payloads contain the note, whereas alt may only be an
        // empty image description or its number. Empty attributes must not
        // mask a useful fallback attribute.
        foreach (var name in new[] { "data-footnote-text", "zy-footnote", "data-note", "alt", "title" })
        {
            var value = (string?)element.Attribute(name);
            if (string.IsNullOrWhiteSpace(value)
                || (name is "alt" or "title" && IsMarker(value))
                || (name == "data-note" && bool.TryParse(value, out _))) continue;
            var normalized = CollapseWhitespace(value);
            return normalized.Length <= 1200 ? normalized : normalized[..1200] + "…";
        }
        return null;
    }

    private void AddImageBlock(XElement element, FlowContext? ctx = null)
    {
        var src = GetImageReference(element);
        var path = ResolveLocalPath(src ?? string.Empty, _chapterDir, _resourceRoot);
        path = ResolveRasterImagePath(path);
        if (path is null)
        {
            _pendingFragmentIds.Clear();
            return;
        }

        var id = Id(element);
        var hints = ResolveHints(element);
        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Image,
            ElementId = id,
            FragmentIds = TakePendingFragmentIds(),
            Center = !IsLeftQuoteImage(element) && !(ctx?.AlignRight ?? false),
            AlignRight = ctx?.AlignRight == true || IsRightQuoteImage(element),
            SpaceBeforeLines = 1.8f * 1f / 1.8f,
            SpaceAfterLines = 1.8f / 1.8f,
            Items = new List<InlineItem>
            {
                new()
                {
                    Kind = InlineKind.Image,
                    ImagePath = path,
                    ImageWidthFactor = float.IsNaN(hints.ImageWidthFactor)
                        ? null
                        : hints.ImageWidthFactor,
                    DecorativeQuote = IsLeftQuoteImage(element) || IsRightQuoteImage(element),
                    Style = hints.Bold ? new TypesetInlineStyle(Bold: true) : new TypesetInlineStyle(),
                    LinkHref = ctx?.LinkHref,
                    FootnoteHref = ctx?.FootnoteHref,
                },
            },
        });
        _pending.Clear();
    }

    private void AddInlineImage(XElement element, FlowContext ctx)
    {
        var src = GetImageReference(element);
        var path = ResolveRasterImagePath(ResolveLocalPath(src ?? string.Empty, _chapterDir, _resourceRoot));
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var hints = ResolveHints(element);
        _pending.Add(new InlineItem
        {
            Kind = InlineKind.Image,
            ImagePath = path,
            ImageHeightEm = GetImageHeightEm(element, hints),
            ImageWidthFactor = float.IsNaN(hints.ImageWidthFactor)
                ? null
                : hints.ImageWidthFactor,
            DecorativeQuote = IsLeftQuoteImage(element) || IsRightQuoteImage(element),
            Style = ctx.BaseStyle with
            {
                Bold = ctx.BaseStyle.Bold || hints.Bold,
            },
            LinkHref = ctx.LinkHref,
            FootnoteHref = ctx.FootnoteHref,
        });
    }

    private void AddSvgImageBlock(XElement element, FlowContext ctx)
    {
        // Most EPUB SVGs are wrappers around a raster image. Resolve that
        // image directly so the native renderer does not need an SVG browser.
        var source = GetImageReference(element);
        if (source is null)
        {
            var image = element.Descendants()
                .FirstOrDefault(child => child.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase));
            source = image is null ? null : GetImageReference(image);
        }
        var path = ResolveRasterImagePath(ResolveLocalPath(source ?? string.Empty, _chapterDir, _resourceRoot));
        if (path is null)
        {
            // Preserve the SVG's text nodes in the offset stream even when a
            // vector-only illustration cannot be rasterized by SkiaCodec.
            WalkGhost(element);
            _pendingFragmentIds.Clear();
            return;
        }

        var id = Id(element);
        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Image,
            ElementId = id,
            FragmentIds = TakePendingFragmentIds(),
            Center = true,
            SpaceBeforeLines = 1f,
            SpaceAfterLines = 1f,
            Items = new List<InlineItem>
            {
                new()
                {
                    Kind = InlineKind.Image,
                    ImagePath = path,
                    LinkHref = ctx.LinkHref,
                    FootnoteHref = ctx.FootnoteHref,
                },
            },
        });
        _pending.Clear();
    }

    private static string? GetImageReference(XElement element)
    {
        var direct = (string?)element.Attribute("src")
            ?? (string?)element.Attribute("data-src")
            ?? (string?)element.Attribute("href")
            ?? (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var attributeName in new[] { "srcset", "data-srcset", "data-lazy-srcset", "data-original-set" })
        {
            var srcSet = (string?)element.Attribute(attributeName);
            if (string.IsNullOrWhiteSpace(srcSet))
            {
                continue;
            }

            var candidate = srcSet.Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsInlineFormulaImage(XElement element)
    {
        var alt = ((string?)element.Attribute("alt") ?? string.Empty).Trim();
        if (FormulaImageNamePattern.IsMatch(alt))
        {
            return true;
        }

        var source = GetImageReference(element);
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var cleanSource = source.Split('?', '#')[0];
        var name = Path.GetFileNameWithoutExtension(cleanSource);
        return FormulaImageNamePattern.IsMatch(name);
    }

    private bool IsInlineImage(XElement element)
    {
        if (IsInlineFormulaImage(element) || IsLeftQuoteImage(element))
        {
            return true;
        }

        // Many Chinese EPUBs store a missing/unusual glyph as a tiny raster
        // image. Its stylesheet gives it a font-relative height (for example
        // 0.675em) and width, but the source does not use a formula name or a
        // special quote class. Treat those glyph-sized images as part of the
        // surrounding line; otherwise WalkInlineElement promotes each one to
        // a standalone image block and leaves a lonely character on the page.
        var hints = ResolveHints(element);
        var height = GetImageHeightEm(element, hints);
        return height is > 0f and <= 2f
            && (float.IsNaN(hints.ImageWidthFactor) || hints.ImageWidthFactor <= 0.5f);
    }

    private static bool IsLeftQuoteImage(XElement element)
    {
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (classes.Any(value => value.Equals("yinhao_l", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var source = GetImageReference(element);
        var name = source is null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(source.Split('?', '#')[0]);
        return name.Equals("yinhao-left", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRightQuoteImage(XElement element)
    {
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (classes.Any(value => value.Equals("yinhao_r", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var source = GetImageReference(element);
        var name = source is null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(source.Split('?', '#')[0]);
        return name.Equals("yinhao-right", StringComparison.OrdinalIgnoreCase);
    }

    private static float? GetImageHeightEm(XElement element, CssHints hints)
    {
        var style = (string?)element.Attribute("style");
        var match = style is null ? null : InlineEmHeightPattern.Match(style);
        if (match is not null && match.Success
            && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            && height > 0f)
        {
            return height;
        }

        var attribute = ((string?)element.Attribute("height"))?.Trim();
        if (attribute is not null
            && attribute.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(attribute[..^2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out height)
            && height > 0f)
        {
            return height;
        }

        if (!float.IsNaN(hints.ImageHeightEm) && hints.ImageHeightEm > 0f)
        {
            return hints.ImageHeightEm;
        }

        return null;
    }

    private string? ResolveRasterImagePath(string? path)
    {
        if (path is null || !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        try
        {
            var svg = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var baseDir = Path.GetDirectoryName(path) ?? string.Empty;
            foreach (var image in svg.Descendants().Where(e => e.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase)))
            {
                var reference = GetImageReference(image);
                var candidate = ResolveLocalPath(reference ?? string.Empty, baseDir, _resourceRoot);
                if (candidate is not null && File.Exists(candidate)
                    && !candidate.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            // Invalid SVGs remain non-rendering rather than aborting the whole
            // chapter composition.
        }

        return null;
    }
}
