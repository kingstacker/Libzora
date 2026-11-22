using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Plain-text footnote extraction and merging.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
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
            var definitionOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
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
                    // Remember where this note starts so the reader can jump
                    // from the body marker to its definition.
                    definitionOffsets.TryAdd(key, GetBodyTextOffsetBefore(body, element));
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
                DefinitionOffsets = definitionOffsets,
            });
            previousDefinitionEndOffset = Math.Max(
                previousDefinitionEndOffset,
                definitionEndOffset);
        }

        PrepareExternalPlainFootnotes(body);
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

    private static bool IsPlainFootnoteHeading(XElement element) =>
        IsPlainFootnoteHeadingText(NormalizePlainFootnoteText(element.Value));

    private static bool IsPlainFootnoteHeadingText(string normalizedText)
    {
        var text = normalizedText.Replace(" ", string.Empty).Replace("\u3000", string.Empty);
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
                        // A real link already knows where the note lives; keep
                        // it so the card can offer "jump to the note" instead
                        // of a synthetic, non-navigable fragment.
                        FootnoteHref = item.LinkHref ?? CreatePlainFootnoteHref(
                            match.SectionIndex,
                            match.Key,
                            match.DefinitionOffset,
                            _plainFootnoteSections[match.SectionIndex].TargetChapterPath),
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

    private List<(int Index, int Length, string Key, string Text, int SectionIndex, int DefinitionOffset)> FindPlainFootnoteMarkers(
        InlineItem item)
    {
        var matches = new List<(int Index, int Length, string Key, string Text, int SectionIndex, int DefinitionOffset)>();
        foreach (Match match in PlainFootnoteMarkerPattern.Matches(item.Text))
        {
            var key = NormalizePlainFootnoteKey(match.Groups["number"].Value);
            if (key is null)
            {
                continue;
            }

            var start = item.TextStart + match.Index;
            var sectionIndex = FindPlainFootnoteMarkerSectionIndex(start, key);
            if (sectionIndex >= 0
                && _plainFootnoteSections[sectionIndex] is var section
                && (!section.IsExternal || item.Style.Superscript || IsSquareOrTortoiseBracket(match.Groups["open"].Value))
                && section.Notes.TryGetValue(key, out var text))
            {
                matches.Add((match.Index, match.Length, key, text, sectionIndex,
                    PlainFootnoteDefinitionOffset(sectionIndex, key)));
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
                    : FindPlainFootnoteMarkerSectionIndex(start, key);
                if (key is not null
                    && sectionIndex >= 0
                    && _plainFootnoteSections[sectionIndex].Notes.TryGetValue(key, out var text))
                {
                    matches.Add((match.Index, match.Length, key, text, sectionIndex,
                        PlainFootnoteDefinitionOffset(sectionIndex, key)));
                }
            }
        }

        return matches;
    }

    private int PlainFootnoteDefinitionOffset(int sectionIndex, string key) =>
        _plainFootnoteSections[sectionIndex].DefinitionOffsets.TryGetValue(key, out var offset)
            ? offset
            : -1;

    private int FindPlainFootnoteMarkerSectionIndex(int offset, string key)
    {
        for (var index = 0; index < _plainFootnoteSections.Count; index++)
        {
            var section = _plainFootnoteSections[index];
            if (offset >= section.MarkerStartOffset
                && offset < section.MarkerEndOffset
                && section.Notes.ContainsKey(key))
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

    private string CreatePlainFootnoteHref(
        int sectionIndex,
        string key,
        int definitionOffset,
        string? targetChapterPath = null)
    {
        // The definition offset lets the reader jump to the note inside its own
        // chapter or a companion endnote chapter; older fragments without it
        // keep the card but cannot jump.
        var fragment = definitionOffset >= 0
            ? $"__libzora-plain-footnote-{sectionIndex}-{key}-{definitionOffset}"
            : $"__libzora-plain-footnote-{sectionIndex}-{key}";
        return new UriBuilder(new Uri(targetChapterPath ?? _chapterPath, UriKind.Absolute))
        {
            Fragment = fragment,
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
            var href = ResolveHref((string?)reference.Attribute("href"));
            if (IsNavigationContext(reference) || !IsMarker(reference.Value)
                || IsFootnoteDefinitionBacklink(href, reference)) continue;
            var target = LocalTarget(href);
            var block = target is null ? null : FootnoteDefinitionBlock(target);
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
}
