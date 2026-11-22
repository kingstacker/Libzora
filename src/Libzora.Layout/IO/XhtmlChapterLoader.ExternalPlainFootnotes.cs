using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Cross-chapter plain-text endnote discovery for EPUBs whose body uses
/// unlinked superscript markers (such as <c>&lt;sup&gt;［1］&lt;/sup&gt;</c>) while
/// definitions live in a back-of-book endnote chapter grouped by chapter title.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private static readonly Regex ChapterOrdinalHeadingPattern = new(
        @"^第?(?<ordinal>[一二三四五六七八九十百千零〇两\d]+)[章节回篇卷部](?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly object ExternalEndnotesGate = new();
    private static readonly Dictionary<string, CachedDirectoryEndnotes> ExternalEndnotesCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private sealed record ExternalPlainEndnoteSection(
        string Heading,
        string ChapterPath,
        IReadOnlyDictionary<string, string> Notes,
        IReadOnlyDictionary<string, int> DefinitionOffsets);

    private sealed record CachedDirectoryEndnotes(
        int FileCount,
        long MaxWriteTicks,
        IReadOnlyList<ExternalPlainEndnoteSection> Sections);

    private void PrepareExternalPlainFootnotes(XElement body)
    {
        if (string.IsNullOrWhiteSpace(_chapterDir) || !Directory.Exists(_chapterDir))
        {
            return;
        }

        var leafBlocks = EnumeratePlainFootnoteLeafBlocks(body).ToList();
        var firstNonEmpty = leafBlocks.FirstOrDefault(block => NormalizePlainFootnoteText(block.Value).Length > 0);
        if (firstNonEmpty is not null && IsPlainFootnoteHeading(firstNonEmpty))
        {
            // This file is itself an endnote chapter.
            return;
        }

        if (!HasUnlinkedPlainFootnoteCandidates(body))
        {
            return;
        }

        var sections = GetDirectoryExternalEndnoteSections(_chapterDir);
        if (sections.Count == 0)
        {
            return;
        }

        var titleCandidates = leafBlocks
            .Select(block => NormalizePlainFootnoteText(block.Value))
            .Where(text => text.Length > 0)
            .Take(4)
            .ToList();

        ExternalPlainEndnoteSection? matched = null;
        foreach (var candidate in titleCandidates)
        {
            matched = sections.FirstOrDefault(section =>
                !string.Equals(section.ChapterPath, _chapterPath, PathComparison)
                && MatchesExternalSectionHeading(candidate, section.Heading));
            if (matched is not null)
            {
                break;
            }
        }

        if (matched is null
            && sections.Count == 1
            && !string.Equals(sections[0].ChapterPath, _chapterPath, PathComparison)
            && IsPlainFootnoteHeadingText(sections[0].Heading))
        {
            matched = sections[0];
        }

        if (matched is null || matched.Notes.Count == 0)
        {
            return;
        }

        var bodyLength = GetBodyTextAfterAllNodes(body);
        if (bodyLength <= 0)
        {
            return;
        }

        _plainFootnoteSections.Add(new PlainFootnoteSection
        {
            MarkerStartOffset = 0,
            MarkerEndOffset = bodyLength,
            DefinitionStartOffset = -1,
            DefinitionEndOffset = -1,
            Notes = matched.Notes,
            DefinitionOffsets = matched.DefinitionOffsets,
            TargetChapterPath = matched.ChapterPath,
        });
    }

    private bool HasUnlinkedPlainFootnoteCandidates(XElement body)
    {
        foreach (var textNode in body.DescendantNodes().OfType<XText>())
        {
            if (string.IsNullOrWhiteSpace(textNode.Value))
            {
                continue;
            }

            var hasSup = false;
            var ignored = false;
            foreach (var ancestor in textNode.Ancestors())
            {
                if (IsIgnoredBodyTextElement(ancestor))
                {
                    ignored = true;
                    break;
                }

                var local = ancestor.Name.LocalName;
                if (local.Equals("a", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace((string?)ancestor.Attribute("href")))
                {
                    ignored = true;
                    break;
                }

                if (local.Equals("sup", StringComparison.OrdinalIgnoreCase))
                {
                    hasSup = true;
                }
            }

            if (ignored)
            {
                continue;
            }

            foreach (Match match in PlainFootnoteMarkerPattern.Matches(textNode.Value))
            {
                if (!hasSup && !IsSquareOrTortoiseBracket(match.Groups["open"].Value))
                {
                    continue;
                }

                var key = NormalizePlainFootnoteKey(match.Groups["number"].Value);
                if (key is not null && !_plainFootnoteSections.Any(section => section.Notes.ContainsKey(key)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSquareOrTortoiseBracket(string openBracket) =>
        openBracket is "[" or "［" or "〔" or "【";

    private IReadOnlyList<ExternalPlainEndnoteSection> GetDirectoryExternalEndnoteSections(string chapterDir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(chapterDir)
                .Where(file =>
                {
                    var ext = Path.GetExtension(file);
                    return ext.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ExternalPlainEndnoteSection>();
        }

        if (files.Length <= 1)
        {
            return Array.Empty<ExternalPlainEndnoteSection>();
        }

        long maxTicks = 0;
        foreach (var file in files)
        {
            try
            {
                var ticks = File.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > maxTicks) maxTicks = ticks;
            }
            catch (IOException)
            {
            }
        }

        lock (ExternalEndnotesGate)
        {
            if (ExternalEndnotesCache.TryGetValue(chapterDir, out var cached)
                && cached.FileCount == files.Length
                && cached.MaxWriteTicks == maxTicks)
            {
                return cached.Sections;
            }
        }

        var parsed = new List<ExternalPlainEndnoteSection>();
        // Endnote chapters live near the back of the spine; scanning in reverse
        // finds them fast while still inspecting every candidate deterministically.
        for (var i = files.Length - 1; i >= 0; i--)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(files[i]);
            var doc = LoadDocument(fullPath);
            var endnoteBody = doc?.Root?
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
            if (endnoteBody is null)
            {
                continue;
            }

            var fileSections = ParseExternalEndnoteChapter(endnoteBody, fullPath);
            if (fileSections.Count > 0)
            {
                parsed.InsertRange(0, fileSections);
            }
        }

        lock (ExternalEndnotesGate)
        {
            if (ExternalEndnotesCache.Count >= 16)
            {
                ExternalEndnotesCache.Clear();
            }

            ExternalEndnotesCache[chapterDir] = new CachedDirectoryEndnotes(files.Length, maxTicks, parsed);
        }

        return parsed;
    }

    private static List<ExternalPlainEndnoteSection> ParseExternalEndnoteChapter(XElement endnoteBody, string fullPath)
    {
        var leafBlocks = EnumeratePlainFootnoteLeafBlocks(endnoteBody).ToList();
        var firstNonEmpty = leafBlocks.FirstOrDefault(block => NormalizePlainFootnoteText(block.Value).Length > 0);
        if (firstNonEmpty is null || !IsPlainFootnoteHeading(firstNonEmpty))
        {
            return new List<ExternalPlainEndnoteSection>();
        }

        var defaultHeading = NormalizePlainFootnoteText(firstNonEmpty.Value);
        var sections = new List<ExternalPlainEndnoteSection>();
        var pendingNonMarkerBlocks = new List<string>();
        var currentNotes = new Dictionary<string, string>(StringComparer.Ordinal);
        var currentOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentHeading = null;
        string? currentKey = null;
        var currentText = new StringBuilder();

        foreach (var block in leafBlocks)
        {
            var text = NormalizePlainFootnoteText(block.Value);
            if (text.Length == 0 || HasLinkedLeadingPlainMarker(block))
            {
                continue;
            }

            if (TryReadPlainFootnoteMarkerAtStart(text, out var key, out var remainder))
            {
                if (key == "1" && (currentNotes.Count > 0 || currentKey is not null))
                {
                    FlushPlainFootnote(currentKey, currentText, currentNotes);
                    if (currentNotes.Count > 0)
                    {
                        sections.Add(new ExternalPlainEndnoteSection(
                            currentHeading ?? defaultHeading,
                            fullPath,
                            new Dictionary<string, string>(currentNotes, StringComparer.Ordinal),
                            new Dictionary<string, int>(currentOffsets, StringComparer.Ordinal)));
                    }

                    currentHeading = SelectExternalSectionHeading(pendingNonMarkerBlocks, defaultHeading);
                    pendingNonMarkerBlocks.Clear();
                    currentNotes.Clear();
                    currentOffsets.Clear();
                    currentText.Clear();
                    currentKey = "1";
                    currentOffsets["1"] = GetBodyTextOffsetBefore(endnoteBody, block);
                    AppendPlainFootnoteText(currentText, remainder);
                    continue;
                }

                if (currentKey is null)
                {
                    currentHeading = SelectExternalSectionHeading(pendingNonMarkerBlocks, defaultHeading);
                    pendingNonMarkerBlocks.Clear();
                }
                else
                {
                    foreach (var continuation in pendingNonMarkerBlocks)
                    {
                        AppendPlainFootnoteText(currentText, continuation);
                    }

                    pendingNonMarkerBlocks.Clear();
                    FlushPlainFootnote(currentKey, currentText, currentNotes);
                }

                currentOffsets.TryAdd(key, GetBodyTextOffsetBefore(endnoteBody, block));
                currentKey = key;
                currentText.Clear();
                AppendPlainFootnoteText(currentText, remainder);
                continue;
            }

            if (currentKey is not null && currentText.Length == 0)
            {
                AppendPlainFootnoteText(currentText, text);
            }
            else
            {
                pendingNonMarkerBlocks.Add(text);
            }
        }

        if (currentKey is not null)
        {
            if (currentText.Length == 0)
            {
                foreach (var continuation in pendingNonMarkerBlocks)
                {
                    AppendPlainFootnoteText(currentText, continuation);
                }
            }

            FlushPlainFootnote(currentKey, currentText, currentNotes);
        }

        if (currentNotes.Count > 0)
        {
            sections.Add(new ExternalPlainEndnoteSection(
                currentHeading ?? defaultHeading,
                fullPath,
                currentNotes,
                currentOffsets));
        }

        return sections;
    }

    private static IEnumerable<XElement> EnumeratePlainFootnoteLeafBlocks(XElement body)
    {
        foreach (var element in body.Descendants())
        {
            if (!IsPlainFootnoteBlock(element) || element.Descendants().Any(IsPlainFootnoteBlock))
            {
                continue;
            }

            yield return element;
        }
    }

    private static bool HasLinkedLeadingPlainMarker(XElement block)
    {
        foreach (var anchor in block.Descendants().Where(node => node.Name.LocalName == "a"))
        {
            if (GetHrefFragment((string?)anchor.Attribute("href")) is not null
                && TryReadPlainFootnoteMarkerAtStart(anchor.Value, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static string SelectExternalSectionHeading(List<string> candidates, string defaultHeading)
    {
        for (var i = candidates.Count - 1; i >= Math.Max(0, candidates.Count - 4); i--)
        {
            var candidate = candidates[i];
            if (candidate.Length is > 0 and <= 80
                && !candidate.StartsWith("注：", StringComparison.Ordinal)
                && !candidate.StartsWith("注:", StringComparison.Ordinal)
                && !candidate.StartsWith("说明", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return candidates.Count > 0 ? candidates[^1] : defaultHeading;
    }

    private static bool MatchesExternalSectionHeading(string chapterTitle, string sectionHeading)
    {
        var normChapter = NormalizeHeadingForMatch(chapterTitle);
        var normSection = NormalizeHeadingForMatch(sectionHeading);
        if (normChapter.Length == 0 || normSection.Length == 0 || IsPlainFootnoteHeadingText(normSection))
        {
            return false;
        }

        if (string.Equals(normChapter, normSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normChapter.Length >= 2 && normSection.Length >= 2
            && (normChapter.Contains(normSection, StringComparison.OrdinalIgnoreCase)
                || normSection.Contains(normChapter, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return TrySplitChapterOrdinal(normChapter, out var ordChapter)
            && TrySplitChapterOrdinal(normSection, out var ordSection)
            && string.Equals(ordChapter, ordSection, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHeadingForMatch(string value)
    {
        var withoutMarkers = PlainFootnoteMarkerPattern.Replace(value, string.Empty);
        var builder = new StringBuilder(withoutMarkers.Length);
        foreach (var ch in withoutMarkers)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TrySplitChapterOrdinal(string normalizedHeading, out string ordinal)
    {
        var match = ChapterOrdinalHeadingPattern.Match(normalizedHeading);
        if (match.Success)
        {
            ordinal = match.Groups["ordinal"].Value;
            return ordinal.Length > 0;
        }

        ordinal = string.Empty;
        return false;
    }

    private static int GetBodyTextAfterAllNodes(XElement body)
    {
        var offset = 0;
        foreach (var text in body.DescendantNodes().OfType<XText>())
        {
            if (!text.Ancestors().Any(IsIgnoredBodyTextElement))
            {
                offset += text.Value.Length;
            }
        }

        return offset;
    }
}
