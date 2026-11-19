using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Libzora.Infrastructure;

/// <summary>
/// A resolved note: the text shown in the preview and, when the publisher
/// provides one, the absolute target of its backlink so a reader can return
/// to the reference by tapping the note's ↩ marker.
/// </summary>
public sealed record EpubFootnoteDetail(string Text, string? Backlink);

public sealed partial class EpubFootnoteResolver
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string NormalizeTargetKey(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || !uri.IsFile || string.IsNullOrEmpty(uri.Fragment)) return target;
        try
        {
            var fileUri = new Uri(Path.GetFullPath(uri.LocalPath)).AbsoluteUri;
            var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            // Only Windows paths are folded. EPUB archive entries and all
            // fragment IDs can differ by case, including on Apple platforms.
            if (OperatingSystem.IsWindows()) fileUri = fileUri.ToLowerInvariant();
            return $"{fileUri}#{fragment}";
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            return target;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string epubRoot, IEnumerable<string> absoluteTargets,
        CancellationToken cancellationToken = default)
    {
        var details = await ResolveDetailsAsync(epubRoot, absoluteTargets, cancellationToken);
        return details.ToDictionary(item => item.Key, item => item.Value.Text, StringComparer.Ordinal);
    }

    /// <summary>Resolves one target, including the note's own backlink when present.</summary>
    public async Task<EpubFootnoteDetail?> ResolveDetailAsync(
        string epubRoot, string absoluteTarget, CancellationToken cancellationToken = default)
    {
        var details = await ResolveDetailsAsync(epubRoot, [absoluteTarget], cancellationToken);
        return details.TryGetValue(NormalizeTargetKey(absoluteTarget), out var detail) ? detail : null;
    }

    private async Task<IReadOnlyDictionary<string, EpubFootnoteDetail>> ResolveDetailsAsync(
        string epubRoot, IEnumerable<string> absoluteTargets, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, EpubFootnoteDetail>(StringComparer.Ordinal);
        var documents = new Dictionary<string, XDocument>(PathComparer);
        foreach (var target in absoluteTargets.Distinct(StringComparer.Ordinal).Take(120))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
                || !uri.IsFile || string.IsNullOrEmpty(uri.Fragment)) continue;
            var path = Path.GetFullPath(uri.LocalPath);
            if (!IsPathInside(epubRoot, path) || !File.Exists(path)) continue;
            try
            {
                if (!documents.TryGetValue(path, out var document))
                {
                    document = await LoadDocumentAsync(path, cancellationToken);
                    documents[path] = document;
                }
                var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
                var element = document.Descendants().FirstOrDefault(candidate => candidate.Attributes().Any(attribute =>
                    (attribute.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase)
                     || (attribute.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase)
                         && candidate.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)))
                    && attribute.Value.Equals(fragment, StringComparison.Ordinal)));
                if (element is null) continue;

                var content = SelectFootnoteContent(element);
                var text = ReadFootnoteText(content);
                if (text.Length > 1200)
                {
                    var length = char.IsHighSurrogate(text[1199]) ? 1199 : 1200;
                    text = text[..length] + "…";
                }
                if (text.Length > 0)
                    result[NormalizeTargetKey(target)] = new EpubFootnoteDetail(
                        text, FindBacklink(content, path, epubRoot, target));
            }
            catch (System.Xml.XmlException)
            {
                // Invalid XHTML remains readable, but unsafe regex-based
                // cross-document extraction is deliberately not attempted.
            }
        }
        return result;
    }

    private static async Task<XDocument> LoadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return await XDocument.LoadAsync(stream, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (System.Xml.XmlException)
        {
            // HTML-authored EPUBs often contain named entities not defined by XML.
            var source = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            var normalized = NamedHtmlEntityRegex().Replace(source, match =>
            {
                var entity = match.Value;
                if (entity is "&amp;" or "&lt;" or "&gt;" or "&quot;" or "&apos;") return entity;
                var decoded = WebUtility.HtmlDecode(entity);
                return string.Equals(decoded, entity, StringComparison.Ordinal) ? entity : decoded;
            });
            return XDocument.Parse(normalized, LoadOptions.PreserveWhitespace);
        }
    }

    private static IReadOnlyList<XElement> SelectFootnoteContent(XElement target)
    {
        var ancestors = target.AncestorsAndSelf().TakeWhile(element => element.Name.LocalName != "body").ToArray();
        // Prefer the complete semantic note over the paragraph containing its
        // label: a single note may contain several paragraphs, lists or lines.
        var semantic = ancestors.FirstOrDefault(IsIndividualSemanticNote);
        if (semantic is not null) return [semantic];
        var container = ancestors.FirstOrDefault(element => HasNoteContainerClass(element)
            && CountNoteStarts(element) <= 1);
        if (container is not null) return [container];

        var block = ancestors.FirstOrDefault(element => IsFootnoteBlock(element.Name.LocalName));
        // A standalone <a name='n1'/> may be directly inside a shared note
        // wrapper. Using that entire wrapper would also read unrelated notes.
        if (block is not null && CountNoteStarts(block) > 1
            && target.Name.LocalName is "a" or "span" or "sup")
            block = null;
        var boundary = block ?? ancestors.LastOrDefault(element => element.Parent?.Name.LocalName == "body"
            || (element.Parent is { } parent && HasNoteContainerClass(parent))) ?? target;
        var content = new List<XElement> { boundary };
        var value = NormalizeText(boundary.Value);
        var needsFollowingBlock = value.Length == 0 || IsMarker(value);
        var sharedNotes = boundary.Parent is { } scope && (HasNoteContainerClass(scope)
            || HasSemanticToken(scope, "footnotes", "endnotes", "doc-endnotes"));
        if (!needsFollowingBlock && !sharedNotes) return content;

        foreach (var next in boundary.ElementsAfterSelf().Take(32))
        {
            var leading = LeadingMarkerLink(next);
            var hasOwnAnchor = next.DescendantsAndSelf().Any(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName is "id" or "name"));
            if (next.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                || IsIndividualSemanticNote(next)
                || (hasOwnAnchor && string.IsNullOrWhiteSpace(next.Value))
                || (!needsFollowingBlock && (leading is not null || NoteLabelAtStartRegex().IsMatch(NormalizeText(next.Value)))))
                break;
            if (string.IsNullOrWhiteSpace(next.Value)) continue;
            content.Add(next);
            if (!sharedNotes) break;
            needsFollowingBlock = false;
        }
        return content;
    }

    private static bool HasSemanticToken(XElement element, params string[] tokens) => element.Attributes()
        .Where(attribute => attribute.Name.LocalName is "type" or "role")
        .SelectMany(attribute => attribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Any(value => tokens.Contains(value, StringComparer.OrdinalIgnoreCase));

    private static bool IsIndividualSemanticNote(XElement element) =>
        HasSemanticToken(element, "footnote", "endnote", "doc-footnote", "doc-endnote");

    private static bool HasNoteContainerClass(XElement element) =>
        IsFootnoteBlock(element.Name.LocalName)
        && NoteContainerClassRegex().IsMatch((string?)element.Attribute("class") ?? string.Empty);

    private static int CountNoteStarts(XElement container) => container.DescendantsAndSelf()
        .Where(element => element.Name.LocalName is "p" or "li" or "dd")
        .Count(element => LeadingMarkerLink(element) is not null
            || NoteLabelAtStartRegex().IsMatch(NormalizeText(element.Value)));

    private static XElement? LeadingMarkerLink(XElement block)
    {
        if (block.Name.LocalName == "a" && IsMarker(block.Value)) return block;
        foreach (var node in block.DescendantNodes())
        {
            if (node is XElement element && element.Name.LocalName == "a" && IsMarker(element.Value)
                && !string.IsNullOrWhiteSpace((string?)element.Attribute("href"))) return element;
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value)) return null;
        }
        return null;
    }

    private static bool IsMarker(string text) => NoteMarkerRegex().IsMatch(NormalizeText(text));

    private static bool IsFootnoteBlock(string localName) => localName.ToLowerInvariant() is
        "p" or "li" or "dd" or "dt" or "aside" or "blockquote" or "section" or "article" or "div";

    private static string ReadFootnoteText(IEnumerable<XElement> content)
    {
        var builder = new StringBuilder();
        foreach (var element in content) Append(element);
        // Collapse publisher indentation without losing real block/BR breaks.
        return string.Join('\n', builder.ToString().Split('\n').Select(NormalizeText).Where(line => line.Length > 0));

        void Append(XNode node)
        {
            if (node is XText text) { builder.Append(WhitespaceRegex().Replace(text.Value, " ")); return; }
            if (node is not XElement element) return;
            var local = element.Name.LocalName.ToLowerInvariant();
            if (local is "script" or "style" or "head" or "rt" or "rp") return;
            if (local == "br") { builder.Append('\n'); return; }
            if (local == "img")
            {
                var alt = (string?)element.Attribute("alt");
                if (!string.IsNullOrWhiteSpace(alt)) builder.Append(alt);
                return;
            }
            var block = IsFootnoteBlock(local) || local is "ol" or "ul";
            if (block && builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
            foreach (var child in element.Nodes()) Append(child);
            if (block) builder.Append('\n');
        }
    }

    private static string? FindBacklink(
        IReadOnlyList<XElement> content, string documentPath, string epubRoot, string target)
    {
        // Reparse the explicit file URI. An implicit Unix file URI treats '#'
        // in a relative href as part of the filename instead of a fragment.
        var baseUri = new Uri(new Uri(documentPath, UriKind.Absolute).AbsoluteUri);
        var leading = content.Select(LeadingMarkerLink).FirstOrDefault(anchor => anchor is not null);
        foreach (var anchor in content.SelectMany(element => element.DescendantsAndSelf())
                     .Where(element => element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)))
        {
            var href = (string?)anchor.Attribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            var raw = anchor.Value.Trim();
            var normalized = NormalizeText(raw);
            var role = string.Join(' ', anchor.Attributes()
                .Where(attribute => attribute.Name.LocalName is "role" or "type" or "rel" or "class")
                .Select(attribute => attribute.Value));
            var looksLikeBacklink = role.Contains("backlink", StringComparison.OrdinalIgnoreCase)
                || normalized is "返回" or "返回原文" or "回到原文" or "回"
                || (raw.Length > 0 && raw.All(character => "↩↵↑←⏎".Contains(character)))
                || (anchor == leading && !HasSemanticToken(anchor, "noteref", "doc-noteref"));
            if (!looksLikeBacklink || !Uri.TryCreate(baseUri, href, out var absolute)
                || !absolute.IsFile || string.IsNullOrEmpty(absolute.Fragment)
                || !IsPathInside(epubRoot, absolute.LocalPath)) continue;
            var key = NormalizeTargetKey(absolute.AbsoluteUri);
            if (key != NormalizeTargetKey(target)) return key;
        }
        return null;
    }

    private static string NormalizeText(string value) => WhitespaceRegex().Replace(value, " ").Trim().Trim('↩', '↵', '↑', ' ');

    private static bool IsPathInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"&[A-Za-z][A-Za-z0-9]+;")]
    private static partial Regex NamedHtmlEntityRegex();
    [GeneratedRegex(@"(?:^|\s)(?:duokan-)?(?:footnotes?|endnotes?|fnote|notes?)(?:$|\s|[-_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteContainerClassRegex();
    [GeneratedRegex(@"^(?:[\[［〔【(（]\s*\d{1,4}\s*[\]］〕】)）]|\d{1,4}|[注註]\s*(?:\d{0,4}|[一二三四五六七八九十百]+)|[①-⑳⓪⓵-⓾❶-➓]|[*＊†‡§¶※]{1,3})$")]
    private static partial Regex NoteMarkerRegex();
    [GeneratedRegex(@"^(?:[\[［〔【(（]\s*\d{1,4}\s*[\]］〕】)）]|[①-⑳⓪⓵-⓾❶-➓])")]
    private static partial Regex NoteLabelAtStartRegex();
}
