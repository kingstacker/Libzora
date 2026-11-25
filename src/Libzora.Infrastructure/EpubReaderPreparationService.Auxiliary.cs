using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Libzora.Infrastructure;

/// <summary>
/// Detection of spine documents that only hold footnote or endnote
/// definitions. They remain part of the document so notes stay resolvable,
/// but the reader must not page into them or count them as reading progress.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    // Probing every spine file of a large book would add a full XML parse per
    // chapter. Notes normally sit near the end, and any file named by the
    // table of contents is already excluded, so a bounded scan is enough.
    private const int MaximumAuxiliaryChapterProbes = 32;
    private const long MaximumAuxiliaryChapterBytes = 2L * 1024 * 1024;

    private static readonly Regex FootnoteDefinitionMarkerPattern = new(
        @"^(?:注|注释|脚注|尾注|译注|原注)\s*[\d０-９一二三四五六七八九十百千零〇两]+$|^[\[［〔【]\s*\d+\s*[\]］〕】]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> FootnoteSemanticTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "footnote", "footnotes", "endnote", "endnotes", "rearnote", "rearnotes",
        "doc-footnote", "doc-footnotes", "doc-endnote", "doc-endnotes"
    };

    private static async Task<IReadOnlyList<string>> DetectAuxiliaryChaptersAsync(
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation,
        CancellationToken cancellationToken)
    {
        var targeted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in navigation)
        {
            if (Uri.TryCreate(item.Target, UriKind.Absolute, out var target) && target.IsFile)
                targeted.Add(Path.GetFullPath(target.LocalPath));
        }

        var auxiliary = new List<string>();
        var probes = 0;
        for (var index = 0; index < chapters.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = chapters[index];
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }
            if (targeted.Contains(fullPath)) continue;
            if (probes++ >= MaximumAuxiliaryChapterProbes) break;

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length <= 0 || info.Length > MaximumAuxiliaryChapterBytes)
                    continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (await IsFootnoteOnlyChapterAsync(fullPath, cancellationToken).ConfigureAwait(false))
                auxiliary.Add(path);
        }
        return auxiliary;
    }

    private static async Task<bool> IsFootnoteOnlyChapterAsync(
        string path,
        CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            document = await LoadXmlAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // An unreadable candidate is reading content until proven otherwise.
            return false;
        }

        if (document.Root is not { } root) return false;
        var body = root.DescendantsAndSelf()
            .FirstOrDefault(element => element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null) return false;
        if (HasFootnoteSemanticMarkup(body)) return true;

        var blocks = body.Descendants()
            .Where(element => element.Name.LocalName is
                "p" or "li" or "dd" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Where(element => NormalizeTitle(element.Value).Length > 0)
            .Where(element => !element.Descendants().Any(child => child.Name.LocalName is
                "p" or "li" or "dd" or "blockquote"))
            .ToList();
        if (blocks.Count < 3) return false;

        var definitions = 0;
        foreach (var block in blocks)
        {
            if (StartsWithFootnoteReference(block)) definitions++;
        }
        // A real chapter may open one paragraph with a note marker; a notes
        // container opens almost every block with one.
        return definitions * 5 >= blocks.Count * 4;
    }

    private static bool HasFootnoteSemanticMarkup(XElement body)
    {
        foreach (var element in body.Elements().Prepend(body))
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))
                {
                    if (attribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Any(token => FootnoteSemanticTokens.Contains(token)))
                        return true;
                    continue;
                }
                if (!attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Only the EPUB structural vocabulary is trustworthy here: a
                // plain HTML `type` attribute says nothing about notes.
                if (attribute.Name.NamespaceName.Length > 0
                    && !attribute.Name.NamespaceName.Contains("idpf.org/2007/ops", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (attribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => FootnoteSemanticTokens.Contains(token)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the block's first meaningful node is a note marker, either a
    /// link to the note's home (the usual backlink) or a marker-shaped run of
    /// text such as <c>注1</c> or <c>[1]</c>.
    /// </summary>
    private static bool StartsWithFootnoteReference(XElement block)
    {
        XElement? first = null;
        foreach (var node in block.DescendantNodes())
        {
            if (node is XText text)
            {
                if (string.IsNullOrWhiteSpace(text.Value)) continue;
                break;
            }
            if (node is not XElement element) break;
            if (element.Name.LocalName is "br" or "img" or "hr" or "wbr") continue;
            first = element;
            break;
        }
        if (first is null) return false;

        var marker = NormalizeTitle(first.Value);
        if (!FootnoteDefinitionMarkerPattern.IsMatch(marker)) return false;
        if (first.Name.LocalName is "a" or "sup" or "span")
        {
            var link = first.Name.LocalName == "a"
                ? first
                : first.Descendants().FirstOrDefault(child => child.Name.LocalName == "a");
            if (link?.Attribute("href") is { Value.Length: > 0 }) return true;
            // Chinese "注1" style labels are specific enough without a link;
            // bracketed numbers are not.
            return !marker.StartsWith('[') && !marker.StartsWith('［')
                && !marker.StartsWith('〔') && !marker.StartsWith('【');
        }
        return false;
    }
}
