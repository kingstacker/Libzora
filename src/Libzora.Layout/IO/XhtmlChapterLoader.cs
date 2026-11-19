using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Loads one sanitized spine XHTML chapter into typed blocks. Body text
/// offsets follow the same convention the WebKit bridge used: the verbatim
/// concatenation of every body text node in document order, so annotations
/// and search jumps captured against the live document keep pointing at the
/// same characters. Footnote definitions stay in the offset stream, but are
/// skipped by layout so their references can show the definition in a popup.
/// </summary>
public sealed class XhtmlChapterLoader
{
    private static readonly Regex FootnoteReferencePattern = new(
        @"(?:^|[\s:_.-])(?:noteref|doc-noteref|footnote(?:[-_ ]?ref(?:erence)?)?|endnote(?:[-_ ]?ref(?:erence)?)?|note[-_ ]?ref|fn[-_ ]?ref)(?=$|[\s:_.-]|\d)|linknote(?:ref)?(?=$|[\s:_.-]|\d)|(?:^|[\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d+(?:n|ref)?(?:$|[\s_.-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FootnoteDefinitionTypePattern = new(
        @"(?:^|[\s:])(?:doc-)?(?:footnote|endnote)(?:$|[\s:])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FootnoteDefinitionIdentityPattern = new(
        @"(?:^|[\s_-])(?:duokan-)?(?:footnotes?|endnotes?|fnote|notes?)(?:[\s_-]|\d|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FootnoteReferenceIdentityPattern = new(
        @"(?:noteref|doc-noteref|footnote[-_ ]?(?:ref|reference)|endnote[-_ ]?(?:ref|reference)|note[-_ ]?ref|fn[-_ ]?ref)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FormulaImageNamePattern = new(
        @"^w\d+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineEmHeightPattern = new(
        @"(?:^|;)\s*height\s*:\s*([0-9]+(?:\.[0-9]+)?)\s*em\s*(?:;|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlainFootnoteMarkerPattern = new(
        @"(?<open>[\(（\[【〔［])\s*(?<number>\d{1,4})\s*(?<close>[\)）\]】〕］])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PlainFootnoteBareMarkerPattern = new(
        @"^\s*(?<number>\d{1,4})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Calibre and similar converters often leave a note reference without
    // noteref/footnote semantics. The visible marker is then the only signal:
    // <sup><small><a href="notes.html#filepos123">[1]</a></small></sup>.
    private static readonly Regex FootnoteMarkerTextPattern = new(
        @"^(?:[\[［〔【(（]\s*\d{1,4}\s*[\]］〕】)）]|\d{1,4}|[注註]\s*(?:\d{0,4}|[一二三四五六七八九十百]+)|[①-⑳⓪⓵-⓾❶-➓]|[*＊†‡§¶※]{1,3})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly bool _paragraphIndent;
    private readonly StringBuilder _body = new();
    private readonly List<ContentBlock> _blocks = new();
    private readonly HashSet<string> _fragmentIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fragmentTextOffsets = new(StringComparer.Ordinal);
    private readonly List<string> _pendingFragmentIds = new();
    private readonly HashSet<string> _footnoteReferenceFragmentIds = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, CssHints> _resolvedHints = new();
    private readonly Dictionary<string, XElement> _elementsByFragment = new(StringComparer.Ordinal);
    private readonly HashSet<XElement> _inferredFootnoteDefinitions = new();
    private readonly List<InlineItem> _pending = new();
    private readonly List<(int Start, int Length)> _rubyAnnotationRanges = new();
    private readonly List<PlainFootnoteSection> _plainFootnoteSections = new();
    private MicroCss? _css;
    private string _chapterPath = string.Empty;
    private string _chapterDir = string.Empty;
    private string _resourceRoot = string.Empty;
    private CancellationToken _cancellationToken;

    private readonly string? _visibleFootnoteFragment;

    public XhtmlChapterLoader(bool paragraphIndent = true, string? visibleFootnoteFragment = null)
    {
        _paragraphIndent = paragraphIndent;
        _visibleFootnoteFragment = visibleFootnoteFragment;
    }

    public ChapterContent Load(string chapterPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken = cancellationToken;
        _body.Clear();
        _blocks.Clear();
        _fragmentIds.Clear();
        _fragmentTextOffsets.Clear();
        _pendingFragmentIds.Clear();
        _footnoteReferenceFragmentIds.Clear();
        _resolvedHints.Clear();
        _elementsByFragment.Clear();
        _inferredFootnoteDefinitions.Clear();
        _pending.Clear();
        _plainFootnoteSections.Clear();
        _rubyAnnotationRanges.Clear();
        _css = null;
        _chapterPath = Path.GetFullPath(chapterPath);
        _chapterDir = Path.GetDirectoryName(_chapterPath) ?? string.Empty;
        _resourceRoot = FindResourceRoot(_chapterPath, _chapterDir);

        var document = LoadDocument(_chapterPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (document?.Root is null)
        {
            return new ChapterContent
            {
                ChapterPath = _chapterPath,
                BodyText = string.Empty,
                Blocks = Array.Empty<ContentBlock>(),
                FragmentIds = _fragmentIds,
                FragmentTextOffsets = _fragmentTextOffsets,
            };
        }

        _css = CollectStylesheets(document, _chapterDir, _resourceRoot);

        var body = document.Root
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is not null)
        {
            IndexFootnoteTargets(body);
            TrackId(body);
            PreparePlainFootnotes(body);
            // Publisher styles often set line-height or text-align on body;
            // treat it as the outermost container so those inherit.
            var rootContext = FlowContext.Root(_paragraphIndent)
                .WithContainerHints(ResolveHints(body));
            WalkFlow(body, rootContext);
            FlushParagraph(rootContext);
            ApplyPlainFootnoteReferences();
            MergePlainFootnoteLabelBlocks();
        }

        PromoteLeadingTitle();
        MergeAdjacentHeadingBlocks();
        cancellationToken.ThrowIfCancellationRequested();

        return new ChapterContent
        {
            ChapterPath = _chapterPath,
            BodyText = _body.ToString(),
            Blocks = _blocks,
            RubyAnnotationRanges = _rubyAnnotationRanges.ToArray(),
            FragmentIds = _fragmentIds,
            FragmentTextOffsets = _fragmentTextOffsets,
        };
    }

    private static XDocument? LoadDocument(string path)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = false,
            };
            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private MicroCss CollectStylesheets(XDocument document, string chapterDir, string resourceRoot)
    {
        var sources = new List<string>();
        var head = document.Root?
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase));
        if (head is not null)
        {
            foreach (var link in head.Descendants().Where(e => e.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase)))
            {
                var rel = (string?)link.Attribute("rel");
                var href = (string?)link.Attribute("href");
                if (rel?.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) != true
                    || string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var cssPath = ResolveLocalPath(href, chapterDir, resourceRoot);
                if (cssPath is not null && File.Exists(cssPath))
                {
                    try
                    {
                        sources.Add(File.ReadAllText(cssPath));
                    }
                    catch (IOException)
                    {
                        // Unreadable stylesheet: the block still lays out plain.
                    }
                }
            }

            foreach (var style in head.Descendants().Where(e => e.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(style.Value);
            }
        }

        return MicroCss.Parse(sources.ToArray());
    }

    private static string? ResolveLocalPath(string href, string baseDir, string resourceRoot)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var pathPart = StripQueryAndFragment(href);
        if (pathPart.Length == 0)
        {
            return null;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(pathPart).Replace('/', Path.DirectorySeparatorChar);
            // Spine resources are archive-relative. Never follow file:, drive,
            // UNC, or other rooted paths supplied by the publication.
            if (Path.IsPathRooted(decoded)
                || Regex.IsMatch(decoded, @"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant))
                return null;

            var fullPath = Path.GetFullPath(Path.Combine(baseDir, decoded));
            var relative = Path.GetRelativePath(resourceRoot, fullPath);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
                return null;
            return fullPath;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static string FindResourceRoot(string chapterPath, string chapterDirectory)
    {
        // EPUB extraction uses the SHA-256 key as the cache directory name.
        // Finding that path boundary lets legal ../ links reach package files
        // while preventing a chapter from reading files outside its archive.
        for (var directory = new DirectoryInfo(chapterDirectory); directory is not null; directory = directory.Parent)
        {
            var name = directory.Name;
            if (name.Length == 64 && name.All(Uri.IsHexDigit))
                return directory.FullName;
        }

        return chapterDirectory;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string StripQueryAndFragment(string value)
    {
        var end = value.Length;
        var query = value.IndexOf('?');
        var fragment = value.IndexOf('#');
        if (query >= 0)
        {
            end = Math.Min(end, query);
        }

        if (fragment >= 0)
        {
            end = Math.Min(end, fragment);
        }

        return value[..end];
    }

    private string? ResolveHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        href = href.Trim();
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                && !absolute.IsFile)
            {
                return absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? absolute.AbsoluteUri
                    : null;
            }

            var hash = href.IndexOf('#');
            var pathPart = hash >= 0 ? href[..hash] : href;
            var fragmentPart = hash >= 0 ? href[(hash + 1)..] : null;
            var targetPath = pathPart.Length == 0
                ? _chapterPath
                : ResolveLocalPath(pathPart, _chapterDir, _resourceRoot);
            if (targetPath is null)
            {
                return null;
            }

            var target = new Uri(Path.GetFullPath(targetPath), UriKind.Absolute);
            if (fragmentPart is null)
            {
                return target.AbsoluteUri;
            }

            try
            {
                fragmentPart = Uri.UnescapeDataString(fragmentPart);
            }
            catch (UriFormatException)
            {
                // Keep the raw fragment when a publisher emitted malformed
                // percent-encoding; the navigation layer will still fail
                // closed if it cannot resolve the target.
            }

            var builder = new UriBuilder(target) { Fragment = fragmentPart };
            return builder.Uri.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // ---- flow walking ----------------------------------------------------

    private void WalkFlow(XElement element, FlowContext ctx)
    {
        foreach (var node in element.Nodes())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case XText text:
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
        if (IsHidden(element))
        {
            // display:none content is neither laid out nor part of the offset
            // stream: publishers use it for duplicate TOCs and print-only text.
            return;
        }

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
        if (style == "none") return null;
        var number = ctx.ListCounter.Value + 1;
        if (int.TryParse((string?)item.Attribute("value"), out var explicitValue))
            number = explicitValue;
        ctx.ListCounter.Value = number;
        return FormatListMarker(style, number) + " ";
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

    private bool IsHidden(XElement element)
    {
        var hidden = element.Attribute("hidden");
        if (hidden is not null && !hidden.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return true;
        return ResolveHints(element).Hidden;
    }

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

    private void WalkBlockParagraph(XElement element, BlockKind kind, FlowContext ctx, string? listMarker = null)
    {
        var id = Id(element);
        var hints = ResolveHints(element);
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
        if (IsHidden(element))
        {
            return;
        }
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
        if (IsHidden(element)) return;
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

    private void AppendGhost(XElement element)
    {
        foreach (var text in element.DescendantNodes().OfType<XText>())
        {
            AppendBodyText(text.Value);
        }
    }

    private void AppendRuby(XElement element, FlowContext ctx)
    {
        // The generated pinyin EPUB uses the compact form
        // <ruby>汉<rt>hàn</rt></ruby>. Keep the base and annotation in the
        // original body-text coordinate space, while exposing only the base
        // as a normal text range and carrying the pronunciation separately
        // for the native painter.
        foreach (var child in element.DescendantsAndSelf())
        {
            TrackId(child);
        }

        var baseText = string.Concat(element
            .DescendantNodes()
            .OfType<XText>()
            .Where(text => !text.Ancestors().Any(IsRubyAnnotation))
            .Select(text => text.Value));
        var rubyText = string.Concat(element
            .Descendants()
            .Where(child => child.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase))
            .DescendantNodes()
            .OfType<XText>()
            .Select(text => text.Value));
        var annotationMarkupText = string.Concat(element
            .DescendantNodes()
            .OfType<XText>()
            .Where(text => text.Ancestors().Any(IsRubyAnnotation))
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
        foreach (var node in element.DescendantNodes())
        {
            if (node is XText text)
            {
                AppendBodyText(text.Value);
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

    private int AppendBodyText(string value)
    {
        var start = _body.Length;
        _body.Append(value);
        return start;
    }

    private void FlushParagraph(FlowContext ctx)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var hasVisible = _pending.Any(i =>
            (!i.Ghost && i.Kind == InlineKind.Image) ||
            (!i.Ghost && i.Text.Length > 0 && !string.IsNullOrWhiteSpace(i.Text)) ||
            i.Kind == InlineKind.LineBreak ||
            i.Kind == InlineKind.FootnoteMarker);

        if (!hasVisible)
        {
            _pending.Clear();
            return;
        }

        var items = _pending.ToList();
        _pending.Clear();

        _blocks.Add(new ContentBlock
        {
            Kind = ctx.Kind == BlockKind.Paragraph && ctx.IsQuote
                ? BlockKind.Blockquote
                : ctx.Kind,
            ElementId = ctx.ElementId,
            Style = ctx.BaseStyle,
            Center = ctx.Center,
            AlignRight = ctx.AlignRight,
            Justify = ctx.Justify,
            TextIndentEm = ctx.TextIndentEm,
            SpaceBeforeLines = ctx.SpaceBeforeLines,
            SpaceAfterLines = ctx.SpaceAfterLines,
            ListMarker = ctx.ListMarker,
            ListLevel = ctx.ListLevel,
            LineHeightFactor = ctx.LineHeightFactor,
            MarginTopEm = ctx.MarginTopEm,
            MarginBottomEm = ctx.MarginBottomEm,
            MarginLeftEm = ctx.MarginLeftEm,
            MarginRightEm = ctx.MarginRightEm,
            PaddingLeftEm = ctx.PaddingLeftEm,
            PaddingRightEm = ctx.PaddingRightEm,
            FootnoteReferenceFragmentIds = _pendingFragmentIds.Where(_footnoteReferenceFragmentIds.Contains).ToArray(),
            FragmentIds = TakePendingFragmentIds(),
            Items = items,
        });
    }

    private bool _buildingTableCells;

    /// <summary>
    /// Reads a table into rows and cells while keeping the body text stream in
    /// document order. Inline content still flows through the normal walker,
    /// so character offsets, links, ruby and fragment anchors keep working.
    /// </summary>
    private void AddTableBlock(XElement table, FlowContext ctx)
    {
        var rows = new List<ContentTableRow>();
        var defaultBorder = ParseTableBorderAttribute(table);
        var wasBuildingCells = _buildingTableCells;
        _buildingTableCells = true;
        try
        {
            foreach (var rowElement in TableRowElements(table))
            {
                var cellElements = rowElement.Elements()
                    .Where(child => (child.Name.LocalName is "td" or "th") && !IsHidden(child))
                    .ToList();
                if (cellElements.Count == 0)
                {
                    continue;
                }

                var cells = new List<ContentTableCell>(cellElements.Count);
                foreach (var cellElement in cellElements)
                {
                    var isHeader = cellElement.Name.LocalName == "th";
                    var hints = ResolveHints(cellElement);
                    var cellContext = ctx.ForTableCell(hints, isHeader);
                    TrackId(cellElement);
                    WalkInlineContent(cellElement, cellContext);
                    var items = _pending.ToList();
                    _pending.Clear();
                    var anyDeclaredBorder = !float.IsNaN(hints.BorderTopEm)
                        || !float.IsNaN(hints.BorderRightEm)
                        || !float.IsNaN(hints.BorderBottomEm)
                        || !float.IsNaN(hints.BorderLeftEm);
                    cells.Add(new ContentTableCell
                    {
                        Items = items,
                        ColSpan = ParseColumnSpan(cellElement),
                        RowSpan = ParseRowSpan(cellElement),
                        Header = isHeader,
                        BorderTopEm = CellBorder(hints.BorderTopEm, anyDeclaredBorder, defaultBorder),
                        BorderRightEm = CellBorder(hints.BorderRightEm, anyDeclaredBorder, defaultBorder),
                        BorderBottomEm = CellBorder(hints.BorderBottomEm, anyDeclaredBorder, defaultBorder),
                        BorderLeftEm = CellBorder(hints.BorderLeftEm, anyDeclaredBorder, defaultBorder),
                    });
                }

                rows.Add(new ContentTableRow
                {
                    Cells = cells,
                    Header = rowElement.Ancestors()
                        .Any(ancestor => ancestor.Name.LocalName == "thead"),
                });
            }
        }
        finally
        {
            _buildingTableCells = wasBuildingCells;
        }

        if (rows.Count == 0)
        {
            return;
        }

        // Vertical typesetting and table-less hosts read this flattened copy.
        // Breaks keep cells from running together; offsets stay untouched.
        var fallback = new List<InlineItem>();
        foreach (var row in rows)
        {
            if (fallback.Count > 0)
            {
                fallback.Add(new InlineItem
                {
                    Kind = InlineKind.LineBreak,
                    TextStart = -1,
                    Style = ctx.BaseStyle,
                });
            }

            var firstCell = true;
            foreach (var cell in row.Cells)
            {
                if (!firstCell)
                {
                    // A forced break keeps each cell's text offsets in its own
                    // run; a synthetic space would start a run at offset -1
                    // and make the following real text unselectable.
                    fallback.Add(new InlineItem
                    {
                        Kind = InlineKind.LineBreak,
                        TextStart = -1,
                        Style = ctx.BaseStyle,
                    });
                }

                firstCell = false;
                fallback.AddRange(cell.Items);
            }
        }

        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Table,
            ElementId = Id(table),
            Style = ctx.BaseStyle,
            Justify = ctx.Justify,
            SpaceBeforeLines = 0.5f,
            SpaceAfterLines = 0.5f,
            Table = new ContentTable { Rows = rows },
            Items = fallback,
            FragmentIds = TakePendingFragmentIds(),
        });
    }

    private static IEnumerable<XElement> TableRowElements(XElement table)
    {
        foreach (var child in table.Elements())
        {
            var local = child.Name.LocalName;
            if (local == "tr")
            {
                yield return child;
            }
            else if (local is "thead" or "tbody" or "tfoot")
            {
                foreach (var row in child.Elements())
                {
                    if (row.Name.LocalName == "tr")
                    {
                        yield return row;
                    }
                }
            }
        }
    }

    private static int ParseColumnSpan(XElement cell) =>
        int.TryParse((string?)cell.Attribute("colspan"), out var span)
            ? Math.Clamp(span, 1, 12)
            : 1;

    private static int ParseRowSpan(XElement cell)
    {
        if (!int.TryParse((string?)cell.Attribute("rowspan"), out var span) || span < 1)
        {
            return 1;
        }

        return Math.Clamp(span, 1, 64);
    }

    private static float ParseTableBorderAttribute(XElement table)
    {
        var attribute = table.Attribute("border");
        if (attribute is null)
        {
            return 0f;
        }

        var value = attribute.Value.Trim();
        if (value.Length == 0)
        {
            return 1f / 16f;
        }

        return int.TryParse(value, out var width) && width > 0 ? 1f / 16f : 0f;
    }

    private static float CellBorder(float hint, bool anyDeclared, float defaultBorder)
    {
        if (!float.IsNaN(hint))
        {
            return hint;
        }

        return anyDeclared ? 0f : defaultBorder;
    }

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
            if (string.IsNullOrWhiteSpace(value) || IsMarker(value)
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

    private IReadOnlyList<string> TakePendingFragmentIds()
    {
        var ids = _pendingFragmentIds.ToArray();
        _pendingFragmentIds.Clear();
        return ids;
    }

    // ---- plain numbered endnotes ---------------------------------------

    /// <summary>
    /// Some Chinese EPUBs render notes as ordinary paragraphs instead of
    /// EPUB footnote links. Their body still contains a numbered marker such
    /// as "（1）", followed much later by a section headed "【注释】". Collect
    /// that section before layout so the markers can use the same hover
    /// protocol and popup as semantic EPUB footnotes.
    /// </summary>
    private void PreparePlainFootnotes(XElement body)
    {
        var previousDefinitionEndOffset = 0;
        var headings = body.Descendants()
            .Where(element =>
                IsPlainFootnoteBlock(element) && IsPlainFootnoteHeading(element))
            .ToList();

        foreach (var heading in headings)
        {
            var headingOffset = GetBodyTextOffsetBefore(body, heading);
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);
            var noteBlocks = new List<XElement>();
            string? currentKey = null;
            var currentText = new StringBuilder();

            foreach (var element in EnumeratePlainFootnoteBlocksAfter(body, heading))
            {
                // The book may repeat 【注释】 many times in one XHTML
                // chapter. Each heading owns only the note blocks up to the
                // next editorial section such as 【译文】 or 【点评】.
                if (IsPlainSectionHeading(element))
                {
                    break;
                }

                noteBlocks.Add(element);
                var text = NormalizePlainFootnoteText(element.Value);
                if (text.Length == 0)
                {
                    continue;
                }

                if (TryReadPlainFootnoteMarkerAtStart(text, out var key, out var remainder))
                {
                    FlushPlainFootnote(currentKey, currentText, notes);
                    currentKey = key;
                    currentText.Clear();
                    AppendPlainFootnoteText(currentText, remainder);
                    continue;
                }

                if (currentKey is not null)
                {
                    AppendPlainFootnoteText(currentText, text);
                }
            }

            FlushPlainFootnote(currentKey, currentText, notes);
            // Linked note definitions already have explicit body references.
            // Do not also infer references from unrelated numbered prose.
            foreach (var anchor in noteBlocks.SelectMany(block => block.Descendants())
                         .Where(node => node.Name.LocalName == "a" && GetHrefFragment((string?)node.Attribute("href")) is not null))
            {
                if (TryReadPlainFootnoteMarkerAtStart(anchor.Value, out var linkedKey, out _))
                    notes.Remove(linkedKey);
            }
            if (notes.Count == 0)
            {
                continue;
            }

            var definitionEndOffset = noteBlocks.Count == 0
                ? headingOffset
                : GetBodyTextOffsetAfter(body, noteBlocks[^1]);
            _plainFootnoteSections.Add(new PlainFootnoteSection
            {
                MarkerStartOffset = previousDefinitionEndOffset,
                MarkerEndOffset = headingOffset,
                DefinitionStartOffset = headingOffset,
                DefinitionEndOffset = definitionEndOffset,
                Notes = notes,
            });
            previousDefinitionEndOffset = Math.Max(
                previousDefinitionEndOffset,
                definitionEndOffset);
        }
    }

    private static IEnumerable<XElement> EnumeratePlainFootnoteBlocksAfter(
        XElement body,
        XElement heading)
    {
        var afterHeading = false;
        foreach (var element in body.Descendants())
        {
            if (ReferenceEquals(element, heading))
            {
                afterHeading = true;
                continue;
            }

            if (!afterHeading || !IsPlainFootnoteBlock(element))
            {
                continue;
            }

            // Prefer the leaf paragraph inside a wrapper div/section. This
            // prevents the same note from being collected more than once.
            if (element.Descendants().Any(IsPlainFootnoteBlock))
            {
                continue;
            }

            yield return element;
        }
    }

    private static bool IsPlainFootnoteBlock(XElement element)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        return local is "p" or "li" or "dd" or "dt" or "blockquote"
            or "div" or "section" or "article" or "h1" or "h2" or "h3"
            or "h4" or "h5" or "h6";
    }

    private static bool IsPlainSectionHeading(XElement element)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        if (local is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            return true;
        }

        var text = NormalizePlainFootnoteText(element.Value).Replace(" ", string.Empty);
        return text.Length >= 3
            && text.Length <= 24
            && text[0] == '【'
            && text[^1] == '】';
    }

    private static bool IsPlainFootnoteHeading(XElement element)
    {
        var text = NormalizePlainFootnoteText(element.Value).Replace(" ", string.Empty);
        return text is "注释" or "【注释】" or "[注释]" or "脚注" or "【脚注】"
            or "[脚注]" or "尾注" or "【尾注】" or "[尾注]"
            || text.StartsWith("注释：", StringComparison.Ordinal)
            || text.StartsWith("注释:", StringComparison.Ordinal);
    }

    private static int GetBodyTextOffsetBefore(XElement body, XElement target)
    {
        var offset = 0;
        foreach (var text in body.DescendantNodes().OfType<XText>())
        {
            if (text.Ancestors().Any(ancestor => ReferenceEquals(ancestor, target)))
            {
                break;
            }

            if (text.Ancestors().Any(IsIgnoredBodyTextElement))
            {
                continue;
            }

            offset += text.Value.Length;
        }

        return offset;
    }

    private static int GetBodyTextOffsetAfter(XElement body, XElement target)
    {
        var offset = GetBodyTextOffsetBefore(body, target);
        foreach (var text in target.DescendantNodes().OfType<XText>())
        {
            if (!text.Ancestors().Any(IsIgnoredBodyTextElement))
            {
                offset += text.Value.Length;
            }
        }

        return offset;
    }

    private static bool IsIgnoredBodyTextElement(XElement element)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        return local is "style" or "script" or "head" or "title";
    }

    private static bool TryReadPlainFootnoteMarkerAtStart(
        string text,
        out string key,
        out string remainder)
    {
        var candidate = text.TrimStart();
        var match = PlainFootnoteMarkerPattern.Match(candidate);
        if (!match.Success || match.Index != 0)
        {
            key = string.Empty;
            remainder = string.Empty;
            return false;
        }

        key = NormalizePlainFootnoteKey(match.Groups["number"].Value) ?? string.Empty;
        if (key.Length == 0)
        {
            remainder = string.Empty;
            return false;
        }

        remainder = candidate[match.Length..].Trim();
        return true;
    }

    private static string? NormalizePlainFootnoteKey(string value)
    {
        return int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string NormalizePlainFootnoteText(string value)
    {
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AppendPlainFootnoteText(StringBuilder target, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (target.Length > 0)
        {
            target.Append(' ');
        }

        target.Append(value);
    }

    private static void FlushPlainFootnote(
        string? key,
        StringBuilder text,
        Dictionary<string, string> notes)
    {
        if (key is null || notes.ContainsKey(key))
        {
            return;
        }

        var value = NormalizePlainFootnoteText(text.ToString());
        if (value.Length == 0)
        {
            return;
        }

        notes[key] = value.Length <= 1200 ? value : value[..1200] + "…";
    }

    private void ApplyPlainFootnoteReferences()
    {
        if (_plainFootnoteSections.Count == 0)
        {
            return;
        }

        foreach (var block in _blocks)
        {
            var original = block.Items.ToArray();
            var rewritten = new List<InlineItem>(original.Length);
            foreach (var item in original)
            {
                if (item.Kind != InlineKind.Text
                    || item.Ghost
                    || item.TextStart < 0
                    || item.FootnoteHref is not null
                    || item.Text.Length == 0)
                {
                    rewritten.Add(item);
                    continue;
                }

                var matches = FindPlainFootnoteMarkers(item);
                if (matches.Count == 0)
                {
                    rewritten.Add(item);
                    continue;
                }

                var cursor = 0;
                foreach (var match in matches)
                {
                    if (match.Index > cursor)
                    {
                        rewritten.Add(ClonePlainTextItem(
                            item,
                            item.Text[cursor..match.Index],
                            item.TextStart + cursor));
                    }

                    rewritten.Add(new InlineItem
                    {
                        Kind = InlineKind.FootnoteMarker,
                        Text = item.Text.Substring(match.Index, match.Length),
                        TextStart = item.TextStart + match.Index,
                        Style = item.Style with
                        {
                            Superscript = true,
                            NoWrap = true,
                        },
                        LinkHref = item.LinkHref,
                        FootnoteHref = CreatePlainFootnoteHref(
                            match.SectionIndex,
                            match.Key),
                        FootnoteText = match.Text,
                    });
                    cursor = match.Index + match.Length;
                }

                if (cursor < item.Text.Length)
                {
                    rewritten.Add(ClonePlainTextItem(
                        item,
                        item.Text[cursor..],
                        item.TextStart + cursor));
                }
            }

            block.Items.Clear();
            block.Items.AddRange(rewritten);
        }
    }

    private void MergePlainFootnoteLabelBlocks()
    {
        if (_plainFootnoteSections.Count == 0)
        {
            return;
        }

        for (var index = 0; index + 1 < _blocks.Count; index++)
        {
            var labelBlock = _blocks[index];
            if (!TryGetPlainFootnoteLabel(labelBlock, out _))
            {
                continue;
            }

            var bodyBlock = _blocks[index + 1];
            if (TryGetPlainFootnoteLabel(bodyBlock, out _)
                || !IsPlainFootnoteBodyBlock(bodyBlock))
            {
                continue;
            }

            // A label-only paragraph and its following explanation are one
            // visual note. Merging them removes the artificial paragraph gap
            // while preserving both blocks' source offsets and styles.
            _blocks[index] = new ContentBlock
            {
                Kind = labelBlock.Kind,
                ElementId = labelBlock.ElementId,
                FragmentIds = labelBlock.FragmentIds
                    .Concat(bodyBlock.FragmentIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Style = labelBlock.Style,
                Center = labelBlock.Center,
                AlignRight = labelBlock.AlignRight,
                Justify = labelBlock.Justify,
                TextIndentEm = labelBlock.TextIndentEm,
                SpaceBeforeLines = labelBlock.SpaceBeforeLines,
                SpaceAfterLines = bodyBlock.SpaceAfterLines,
                LineHeightFactor = labelBlock.LineHeightFactor,
                MarginTopEm = labelBlock.MarginTopEm,
                MarginBottomEm = labelBlock.MarginBottomEm,
                MarginLeftEm = labelBlock.MarginLeftEm,
                MarginRightEm = labelBlock.MarginRightEm,
                PaddingLeftEm = labelBlock.PaddingLeftEm,
                PaddingRightEm = labelBlock.PaddingRightEm,
                Items = labelBlock.Items.Concat(bodyBlock.Items).ToList(),
            };
            _blocks.RemoveAt(index + 1);
        }
    }

    private bool TryGetPlainFootnoteLabel(ContentBlock block, out string key)
    {
        key = string.Empty;
        foreach (var item in block.Items)
        {
            if (item.Ghost || item.Kind != InlineKind.Text || item.TextStart < 0)
            {
                continue;
            }

            var sectionIndex = FindPlainFootnoteDefinitionSectionIndex(item.TextStart);
            if (sectionIndex < 0)
            {
                return false;
            }

            if (!TryReadPlainFootnoteMarkerAtStart(item.Text, out key, out _)
                || !_plainFootnoteSections[sectionIndex].Notes.ContainsKey(key))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private bool IsPlainFootnoteBodyBlock(ContentBlock block)
    {
        if (block.Kind is BlockKind.Image or BlockKind.Rule)
        {
            return false;
        }

        return block.Items.Any(item =>
            !item.Ghost
            && item.Kind == InlineKind.Text
            && FindPlainFootnoteDefinitionSectionIndex(item.TextStart) >= 0
            && !string.IsNullOrWhiteSpace(item.Text));
    }

    private List<(int Index, int Length, string Key, string Text, int SectionIndex)> FindPlainFootnoteMarkers(
        InlineItem item)
    {
        var matches = new List<(int Index, int Length, string Key, string Text, int SectionIndex)>();
        foreach (Match match in PlainFootnoteMarkerPattern.Matches(item.Text))
        {
            var key = NormalizePlainFootnoteKey(match.Groups["number"].Value);
            if (key is null)
            {
                continue;
            }

            var start = item.TextStart + match.Index;
            var sectionIndex = FindPlainFootnoteMarkerSectionIndex(start);
            if (sectionIndex >= 0
                && _plainFootnoteSections[sectionIndex].Notes.TryGetValue(key, out var text))
            {
                matches.Add((match.Index, match.Length, key, text, sectionIndex));
            }
        }

        // A few publishers put only a superscript digit in the body and use
        // parentheses solely in the note list. Limit this fallback to an
        // already-superscript item so ordinary prose numbers stay untouched.
        if (matches.Count == 0 && item.Style.Superscript)
        {
            var match = PlainFootnoteBareMarkerPattern.Match(item.Text);
            if (match.Success)
            {
                var key = NormalizePlainFootnoteKey(match.Groups["number"].Value);
                var start = item.TextStart + match.Index;
                var sectionIndex = key is null
                    ? -1
                    : FindPlainFootnoteMarkerSectionIndex(start);
                if (key is not null
                    && sectionIndex >= 0
                    && _plainFootnoteSections[sectionIndex].Notes.TryGetValue(key, out var text))
                {
                    matches.Add((match.Index, match.Length, key, text, sectionIndex));
                }
            }
        }

        return matches;
    }

    private int FindPlainFootnoteMarkerSectionIndex(int offset)
    {
        for (var index = 0; index < _plainFootnoteSections.Count; index++)
        {
            var section = _plainFootnoteSections[index];
            if (offset >= section.MarkerStartOffset
                && offset < section.MarkerEndOffset)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindPlainFootnoteDefinitionSectionIndex(int offset)
    {
        for (var index = 0; index < _plainFootnoteSections.Count; index++)
        {
            var section = _plainFootnoteSections[index];
            if (offset >= section.DefinitionStartOffset
                && offset < section.DefinitionEndOffset)
            {
                return index;
            }
        }

        return -1;
    }

    private string CreatePlainFootnoteHref(int sectionIndex, string key)
    {
        return new UriBuilder(new Uri(_chapterPath, UriKind.Absolute))
        {
            Fragment = $"__libzora-plain-footnote-{sectionIndex}-{key}",
        }.Uri.AbsoluteUri;
    }

    private static InlineItem ClonePlainTextItem(
        InlineItem source,
        string text,
        int textStart)
    {
        return new InlineItem
        {
            Kind = InlineKind.Text,
            Text = text,
            TextStart = textStart,
            Style = source.Style,
            LinkHref = source.LinkHref,
            Ghost = source.Ghost,
        };
    }

    // ---- helpers ---------------------------------------------------------

    private void IndexFootnoteTargets(XElement body)
    {
        foreach (var element in body.DescendantsAndSelf())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            foreach (var fragment in FragmentIds(element))
                _elementsByFragment.TryAdd(fragment, element);
        }

        // A reciprocal pair is stronger evidence than any particular ID
        // spelling. Only the definition's leading marker may be the backlink;
        // ordinary numbered cross-references and TOC entries stay visible.
        foreach (var reference in body.Descendants().Where(element => element.Name.LocalName == "a"))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (IsNavigationContext(reference) || !IsMarker(reference.Value)) continue;
            var target = LocalTarget(ResolveHref((string?)reference.Attribute("href")));
            var block = target is null ? null : NearestFootnoteBlock(target);
            var backlink = block is null ? null : LeadingMarkerLink(block);
            if (backlink is null || backlink == reference) continue;
            var returnHref = ResolveHref((string?)backlink.Attribute("href"));
            var returnFragment = GetHrefFragment(returnHref);
            if (LocalTarget(returnHref) is null || returnFragment is null
                || !ReferenceFragmentIds(reference).Contains(returnFragment)) continue;
            var referenceBlock = NearestFootnoteBlock(reference);
            if (referenceBlock is not null && LeadingMarkerLink(referenceBlock) == reference
                && !IsSuperscriptMarkerLink(reference) && !HasReferenceMarkup(reference)) continue;
            _inferredFootnoteDefinitions.Add(block!);
        }
    }

    private XElement? LocalTarget(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile
            || !string.Equals(uri.LocalPath, _chapterPath, PathComparison)) return null;
        var fragment = GetHrefFragment(href);
        return fragment is not null && _elementsByFragment.TryGetValue(fragment, out var target) ? target : null;
    }

    private static XElement? NearestFootnoteBlock(XElement element) =>
        element.AncestorsAndSelf().FirstOrDefault(node => node.Name.LocalName is
            "p" or "li" or "dd" or "aside" or "blockquote" or "section" or "article" or "div");

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
        _inferredFootnoteDefinitions.Contains(element) || HasFootnoteDefinitionMarkup(element);

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

        return FootnoteDefinitionIdentityPattern.IsMatch(identity)
            || LooksLikeConvertedFootnoteDefinition(element);
    }

    /// <summary>
    /// Recognizes an endnote paragraph written by converters that preserve
    /// neither epub:type nor a footnote class:
    /// <p id="filepos652450"><a href="chapter.html#filepos12816">[1]</a>note text</p>.
    /// The leading marker link must point into another document, which keeps
    /// ordinary cross-references inside the same chapter visible.
    /// </summary>
    private static bool LooksLikeConvertedFootnoteDefinition(XElement element)
    {
        var first = LeadingMarkerLink(element);
        if (first is null) return false;

        var href = (string?)first.Attribute("href");
        var fragment = GetHrefFragment(href);
        if (fragment is null || href is null)
        {
            return false;
        }

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
            Hidden = hints.Hidden || inline.Hidden,
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
        public float SpaceBeforeLines { get; private init; }
        public float SpaceAfterLines { get; private init; }
        public float LineHeightFactor { get; private init; }
        public float MarginTopEm { get; private init; }
        public float MarginBottomEm { get; private init; }
        public float MarginLeftEm { get; private init; }
        public float MarginRightEm { get; private init; }
        public float PaddingLeftEm { get; private init; }
        public float PaddingRightEm { get; private init; }
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

        public FlowContext AsQuote() => this with
        {
            IsQuote = true,
            Justify = !NoJustify,
            TextIndentEm = ParagraphIndentEnabled ? 2f : 0f,
            SpaceBeforeLines = 0.78f,
            SpaceAfterLines = 0.78f,
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

        public FlowContext ForBlock(BlockKind kind, CssHints hints, string? elementId)
        {
            var isHeading = kind == BlockKind.Heading;
            var indented = !isHeading
                && !hints.Center
                && ParagraphIndentEnabled
                && kind == BlockKind.Paragraph;
            return this with
            {
                Kind = kind,
                ElementId = elementId,
                Center = isHeading || hints.Center,
                AlignRight = AlignRight || hints.AlignRight,
                NoJustify = NoJustify || hints.NoJustify,
                Justify = !isHeading && !hints.Center && !hints.NoJustify && !NoJustify,
                TextIndentEm = indented ? 2f : 0f,
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
                BaseStyle = MergeTextHints(isHeading ? BaseStyle with { Bold = true } : BaseStyle, hints) with
                {
                    FontSizeFactor = ScaleFontSize(BaseStyle.FontSizeFactor, hints.FontSizeFactor),
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
        var target = LocalTarget(href);
        // Duokan-style EPUBs use a small image with an empty alt as the whole
        // reference, e.g. <a id="A_1" href="#B_1"><img alt=""/></a>. The target
        // being an explicit note definition is the signal, not the link text.
        if (target is not null && !IsNavigationContext(target)
            && target.AncestorsAndSelf().Any(IsFootnoteDefinition)) return true;
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
        if (Regex.IsMatch(reference, @"^(?:notes?|fn|ftn)\d+$", RegexOptions.CultureInvariant)) return definition == reference + "n";
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
