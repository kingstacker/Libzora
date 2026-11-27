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

public sealed partial class XhtmlChapterLoader
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
    private readonly Dictionary<string, Dictionary<string, XElement>> _externalElementsByFragment = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
        _externalElementsByFragment.Clear();
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
        if (body is not null && !IsDisplayNone(body))
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
        AttachOrphanFootnoteBlocks();
        ApplyPlainFigureAndTableReferences();
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

    private static readonly object StylesheetCacheLock = new();
    private static readonly Dictionary<string, (string Key, MicroCss Css)> StylesheetCache =
        new(StringComparer.Ordinal);
    private const int StylesheetCacheCapacity = 32;

    private MicroCss CollectStylesheets(XDocument document, string chapterDir, string resourceRoot)
    {
        var externalPaths = new List<string>();
        var inlineStyles = new List<string>();
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
                if (cssPath is not null)
                {
                    externalPaths.Add(cssPath);
                }
            }

            foreach (var style in head.Descendants().Where(e => e.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                inlineStyles.Add(style.Value);
            }
        }

        if (externalPaths.Count == 1 && inlineStyles.Count == 0
            && TryGetCachedExternalStylesheet(externalPaths[0], out var cachedSingle))
        {
            return cachedSingle;
        }

        if (externalPaths.Count == 1 && TryGetCachedExternalStylesheet(externalPaths[0], out var cachedBase))
        {
            var merged = cachedBase.Clone();
            foreach (var inline in inlineStyles)
                merged.Append(inline);
            return merged;
        }

        var sources = new List<string>(externalPaths.Count + inlineStyles.Count);
        foreach (var cssPath in externalPaths)
        {
            if (File.Exists(cssPath))
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
        sources.AddRange(inlineStyles);

        return MicroCss.Parse(sources.ToArray());
    }

    private static bool TryGetCachedExternalStylesheet(string cssPath, out MicroCss css)
    {
        css = null!;
        try
        {
            var info = new FileInfo(cssPath);
            if (!info.Exists)
                return false;
            var cacheKey = $"{cssPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (StylesheetCacheLock)
            {
                if (StylesheetCache.TryGetValue(cssPath, out var entry)
                    && string.Equals(entry.Key, cacheKey, StringComparison.Ordinal))
                {
                    css = entry.Css;
                    return true;
                }
            }

            var text = File.ReadAllText(cssPath);
            var parsed = MicroCss.Parse(text);
            lock (StylesheetCacheLock)
            {
                if (StylesheetCache.Count >= StylesheetCacheCapacity)
                    StylesheetCache.Clear();
                StylesheetCache[cssPath] = (cacheKey, parsed);
            }
            css = parsed;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
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
        var current = chapterDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current.AsSpan());
            if (name.Length == 64 && IsAllHex(name))
                return current;
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        return chapterDirectory;
    }

    private static bool IsAllHex(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }
        return true;
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
}
