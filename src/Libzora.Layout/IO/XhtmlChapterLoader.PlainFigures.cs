using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Libzora.Layout;

/// <summary>
/// Plain-text figure and table target discovery and in-chapter reference linking.
/// Source character offsets in <see cref="ChapterContent.BodyText"/> remain
/// completely untouched: matched references split existing <see cref="InlineItem"/>
/// runs in-place while preserving each slice's exact <see cref="InlineItem.TextStart"/>.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    public const string PlainFigureFragmentPrefix = "__libzora-figref-";

    private static readonly Regex FigureTableCaptionPattern = new(
        @"^\s*(?:[▲▼△▽●○■□◆◇★☆※►▸▹•·\-—–]+\s*)*(?:[\[［【〔(（]\s*)?(?<kind>附图|附圖|附表|插图|插圖|图|圖|表|Fig(?:ure)?s?\.?|Tab(?:le)?s?\.?)\s*(?<num>[0-9０-９]{1,4}(?:\s*[-.．—–_－]\s*[0-9０-９]{1,4})*[a-zA-ZＡ-Ｚａ-ｚ]?|[一二三四五六七八九十百零〇]{1,6}|[IVXLCDM]{1,6})(?:\s*[\]］】〕)）])?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FigureTableReferencePattern = new(
        @"(?<kind>附图|附圖|附表|插图|插圖|图|圖|表|Fig(?:ure)?s?\.?|Tab(?:le)?s?\.?)\s*(?<num>[0-9０-９]{1,4}(?:\s*[-.．—–_－]\s*[0-9０-９]{1,4})*[a-zA-ZＡ-Ｚａ-ｚ]?|[一二三四五六七八九十百零〇]{1,6}|[IVXLCDM]{1,6})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Characters that form a 2-character Chinese word ending in 图/表 (e.g. 代表, 意图, 列表, 手表)
    // and therefore must not be treated as a "图 xx" / "表 xx" reference.
    private const string InvalidPrecedingCharsForFigure = "意企试贪谋版宏地海星修拼抠美";
    private const string InvalidPrecedingCharsForTable = "代发外手秒钟怀课列日月年水电气码仪制填报图体地江华师老姑姨意";

    // Counter, measure, and date units that immediately follow a number in prose (e.g. "图 100 个", "表 1998 年").
    private const string InvalidFollowingUnitChars = "年月日号时分秒世代个种次倍人名位条项章节页行列元块万亿%％°℃";

    // Common Chinese characters that follow "一" in idioms/words after 图/表 (e.g. "图一时", "表一致", "表一样").
    private const string InvalidFollowingChineseNumeralChars =
        "致定贯般样些种个时起直旦面方体览快乐层步点番下子切共再同向味品回场块声处夜天头套家对封局届岁年座度张条杯次段派片版班百盒盘相眼票站篇粒系级线组";

    private static readonly string[] ChineseNarrativePredicates =
    [
        "所示", "展示", "显示", "说明", "描述", "给出", "反映", "列出", "汇总", "对比",
        "表示", "表明", "描绘", "呈现", "揭示", "总结", "归纳", "体现", "代表",
        "中，", "中的", "中可", "中能", "中展", "可以", "可见", "能够", "已经",
    ];

    private static readonly string[] EnglishNarrativePredicates =
    [
        "shows ", "illustrates ", "depicts ", "demonstrates ", "presents ",
        "lists ", "summarizes ", "compares ", "gives ", "provides ",
        "indicates ", "reveals ", "is ", "was ", "are ", "were ",
        "can ", "will ", "has ", "have ", "in ",
    ];

    private sealed record PlainFigureTarget(
        string Key,
        string Category,
        string NormalizedNumber,
        int TargetBlockIndex,
        int CaptionBlockIndex,
        int Score,
        string FragmentId,
        string Href,
        string CaptionText);

    private void ApplyPlainFigureAndTableReferences()
    {
        if (_blocks.Count < 2) return;
        var targets = DiscoverFigureAndTableTargets();
        if (targets.Count == 0) return;

        for (var blockIndex = 0; blockIndex < _blocks.Count; blockIndex++)
        {
            var block = _blocks[blockIndex];
            if (block.Kind is BlockKind.Image or BlockKind.Rule) continue;
            RewriteBlockFigureReferences(block, blockIndex, targets);
        }
    }

    private Dictionary<string, PlainFigureTarget> DiscoverFigureAndTableTargets()
    {
        var candidates = new Dictionary<string, PlainFigureTarget>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            if (block.Kind == BlockKind.Rule) continue;

            if (block.Kind == BlockKind.Image)
            {
                var imageItem = block.Items.FirstOrDefault(item => item.Kind == InlineKind.Image);
                if (!string.IsNullOrWhiteSpace(imageItem?.AltText)
                    && TryMatchCaptionLabel(imageItem.AltText, isExplicitCaption: true, out var altCategory, out var altNum, out _))
                {
                    RegisterCandidate(candidates, altCategory, altNum, i, i, score: 1, NormalizeCaptionPreview(imageItem.AltText));
                }
                continue;
            }

            if (block.Kind == BlockKind.Table)
            {
                var firstRowText = GetTableFirstRowText(block);
                if (firstRowText.Length > 0
                    && TryMatchCaptionLabel(firstRowText, isExplicitCaption: true, out var tableCategory, out var tableNum, out _)
                    && IsTableCategory(tableCategory))
                {
                    RegisterCandidate(candidates, tableCategory, tableNum, i, i, score: 2, NormalizeCaptionPreview(firstRowText));
                }
                continue;
            }

            var text = GetVisibleBlockText(block);
            if (text.Length == 0 || text.Length > 200) continue;
            if (!TryMatchCaptionLabel(text, block.IsCaption, out var category, out var normalizedNum, out var hasExplicitSeparator))
                continue;

            var visualIndex = FindPairedVisualBlockIndex(i, category, out var distance);
            var isPaired = visualIndex >= 0;
            if (!isPaired && !block.IsCaption) continue;
            if (!block.IsCaption && !block.Center && block.Kind != BlockKind.Heading && text.Length > 90 && !hasExplicitSeparator)
                continue;

            var score = (block.IsCaption ? 8 : 0)
                + (isPaired ? (distance == 1 ? 4 : 2) : 0)
                + (block.Center || block.Kind == BlockKind.Heading ? 2 : 0)
                + (text.Length <= 60 ? 1 : 0);

            RegisterCandidate(
                candidates,
                category,
                normalizedNum,
                isPaired ? visualIndex : i,
                captionBlockIndex: i,
                score,
                NormalizeCaptionPreview(text));
        }

        foreach (var target in candidates.Values)
        {
            if (Math.Abs(target.CaptionBlockIndex - target.TargetBlockIndex) == 1)
            {
                var upperIndex = Math.Min(target.CaptionBlockIndex, target.TargetBlockIndex);
                if (!_blocks[upperIndex].KeepWithNext)
                    _blocks[upperIndex] = CloneWithBreakFlags(_blocks[upperIndex], keepWithNext: true);
            }

            var targetBlock = _blocks[target.TargetBlockIndex];
            if (!targetBlock.FragmentIds.Contains(target.FragmentId, StringComparer.Ordinal))
                _blocks[target.TargetBlockIndex] = CloneBlockWithExtraFragmentId(targetBlock, target.FragmentId);

            _fragmentIds.Add(target.FragmentId);
            if (targetBlock.Kind != BlockKind.Image)
            {
                var offset = GetEarliestTextOffset(targetBlock);
                if (offset < 0 && target.CaptionBlockIndex != target.TargetBlockIndex)
                    offset = GetEarliestTextOffset(_blocks[target.CaptionBlockIndex]);
                if (offset >= 0)
                    _fragmentTextOffsets.TryAdd(target.FragmentId, offset);
            }
        }

        return candidates;
    }

    private void RegisterCandidate(
        Dictionary<string, PlainFigureTarget> candidates,
        string category,
        string normalizedNumber,
        int targetBlockIndex,
        int captionBlockIndex,
        int score,
        string captionText)
    {
        var key = $"{category}:{normalizedNumber}";
        if (candidates.TryGetValue(key, out var existing) && existing.Score >= score) return;

        var fragmentId = $"{PlainFigureFragmentPrefix}{category}-{normalizedNumber}";
        var href = new UriBuilder(new Uri(_chapterPath, UriKind.Absolute)) { Fragment = fragmentId }.Uri.AbsoluteUri;
        candidates[key] = new PlainFigureTarget(
            key, category, normalizedNumber, targetBlockIndex, captionBlockIndex, score, fragmentId, href, captionText);
    }

    private int FindPairedVisualBlockIndex(int captionIndex, string category, out int distance)
    {
        distance = 0;
        var isFigure = IsFigureCategory(category);
        if (isFigure && _blocks[captionIndex].Items.Any(item => item.Kind == InlineKind.Image && !item.Ghost))
            return captionIndex;

        ReadOnlySpan<int> offsets = isFigure ? [-1, 1, -2, 2] : [1, -1, 2, -2];
        if (!isFigure)
        {
            // Prefer Table blocks at distance 1 before falling back to rasterized table Images.
            foreach (var step in new[] { 1, -1 })
            {
                var candidate = captionIndex + step;
                if (candidate >= 0 && candidate < _blocks.Count && _blocks[candidate].Kind == BlockKind.Table)
                {
                    distance = 1;
                    return candidate;
                }
            }
        }

        foreach (var offset in offsets)
        {
            var candidate = captionIndex + offset;
            if (candidate < 0 || candidate >= _blocks.Count) continue;
            var abs = Math.Abs(offset);
            if (abs == 2 && !IsShortAuxiliaryBlock(_blocks[captionIndex + Math.Sign(offset)])) continue;
            var kind = _blocks[candidate].Kind;
            if (isFigure ? kind == BlockKind.Image : kind is BlockKind.Table or BlockKind.Image)
            {
                distance = abs;
                return candidate;
            }
        }

        return -1;
    }

    private static bool IsShortAuxiliaryBlock(ContentBlock block) =>
        block.IsCaption
        || block.Kind == BlockKind.Image
        || (block.Kind is not (BlockKind.Table or BlockKind.Rule) && GetVisibleBlockText(block).Length <= 80);

    private static bool TryMatchCaptionLabel(
        string text,
        bool isExplicitCaption,
        out string category,
        out string normalizedNumber,
        out bool hasExplicitSeparator)
    {
        category = string.Empty;
        normalizedNumber = string.Empty;
        hasExplicitSeparator = false;

        var match = FigureTableCaptionPattern.Match(text);
        if (!match.Success || match.Index != 0) return false;

        var kindRaw = match.Groups["kind"].Value;
        var numRaw = match.Groups["num"].Value;
        if (!TryNormalizeFigureTableKey(kindRaw, numRaw, out category, out normalizedNumber, out var isChineseNumeral, out var isRomanNumeral))
            return false;

        var afterIndex = match.Index + match.Length;
        if (afterIndex < text.Length && IsInvalidFollowingChar(text[afterIndex], isChineseNumeral, isRomanNumeral))
            return false;

        var remainder = text[afterIndex..];
        hasExplicitSeparator = remainder.Length == 0
            || char.IsWhiteSpace(remainder[0])
            || "：:.．、，,;；—–-|｜/／】]］)）〕".Contains(remainder[0]);

        var trimmedRemainder = remainder.TrimStart(' ', '\t', '\u3000', '：', ':', '.', '．', '、', '，', ',', ';', '；', '—', '–', '-', '|', '｜', '/', '／');
        return isExplicitCaption || !StartsWithNarrativePredicate(trimmedRemainder);
    }

    private static bool StartsWithNarrativePredicate(string remainder)
    {
        if (remainder.Length == 0) return false;
        foreach (var predicate in ChineseNarrativePredicates)
        {
            if (remainder.StartsWith(predicate, StringComparison.Ordinal)) return true;
        }
        foreach (var predicate in EnglishNarrativePredicates)
        {
            if (remainder.StartsWith(predicate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void RewriteBlockFigureReferences(
        ContentBlock block,
        int blockIndex,
        IReadOnlyDictionary<string, PlainFigureTarget> targets)
    {
        var original = block.Items.ToArray();
        var rewritten = new List<InlineItem>(original.Length);
        var changed = false;

        for (var itemIndex = 0; itemIndex < original.Length; itemIndex++)
        {
            var item = original[itemIndex];
            if (item.Kind != InlineKind.Text
                || item.Ghost
                || item.TextStart < 0
                || item.LinkHref is not null
                || item.FootnoteHref is not null
                || item.Text.Length == 0)
            {
                rewritten.Add(item);
                continue;
            }

            var matches = FindFigureTableReferences(
                item,
                blockIndex,
                GetPreviousVisibleChar(original, itemIndex),
                GetNextVisibleChar(original, itemIndex),
                targets);

            if (matches.Count == 0)
            {
                rewritten.Add(item);
                continue;
            }

            changed = true;
            var cursor = 0;
            foreach (var match in matches)
            {
                if (match.Index > cursor)
                {
                    rewritten.Add(ClonePlainTextItem(item, item.Text[cursor..match.Index], item.TextStart + cursor));
                }

                rewritten.Add(new InlineItem
                {
                    Kind = InlineKind.Text,
                    Text = item.Text.Substring(match.Index, match.Length),
                    TextStart = item.TextStart + match.Index,
                    Style = item.Style with { Underline = true },
                    LinkHref = match.Target.Href,
                    FootnoteText = match.Target.CaptionText,
                });
                cursor = match.Index + match.Length;
            }

            if (cursor < item.Text.Length)
            {
                rewritten.Add(ClonePlainTextItem(item, item.Text[cursor..], item.TextStart + cursor));
            }
        }

        if (!changed) return;
        block.Items.Clear();
        block.Items.AddRange(rewritten);
    }

    private static List<(int Index, int Length, PlainFigureTarget Target)> FindFigureTableReferences(
        InlineItem item,
        int blockIndex,
        char? prevItemChar,
        char? nextItemChar,
        IReadOnlyDictionary<string, PlainFigureTarget> targets)
    {
        var results = new List<(int Index, int Length, PlainFigureTarget Target)>();
        var text = item.Text;

        foreach (Match match in FigureTableReferencePattern.Matches(text))
        {
            var kindRaw = match.Groups["kind"].Value;
            var numRaw = match.Groups["num"].Value;
            if (!TryNormalizeFigureTableKey(kindRaw, numRaw, out var category, out var normalizedNumber, out var isChineseNumeral, out var isRomanNumeral))
                continue;

            if (!targets.TryGetValue($"{category}:{normalizedNumber}", out var target))
                continue;

            // Never turn a figure/table's own caption or table cells into a self-link.
            if (blockIndex == target.CaptionBlockIndex || blockIndex == target.TargetBlockIndex)
                continue;

            var prevChar = match.Index > 0 ? text[match.Index - 1] : prevItemChar;
            if (IsInvalidPrecedingChar(kindRaw, prevChar)) continue;

            var afterIndex = match.Index + match.Length;
            var nextChar = afterIndex < text.Length ? text[afterIndex] : nextItemChar;
            if (nextChar is { } following && IsInvalidFollowingChar(following, isChineseNumeral, isRomanNumeral))
                continue;

            results.Add((match.Index, match.Length, target));
        }

        return results;
    }

    private static bool IsInvalidPrecedingChar(string kindRaw, char? prevChar)
    {
        if (prevChar is not { } ch) return false;
        if (char.IsAsciiLetter(kindRaw[0])) return char.IsLetterOrDigit(ch);
        if (kindRaw is "图" or "圖") return InvalidPrecedingCharsForFigure.Contains(ch);
        if (kindRaw == "表") return InvalidPrecedingCharsForTable.Contains(ch);
        return false;
    }

    private static bool IsInvalidFollowingChar(char nextChar, bool isChineseNumeral, bool isRomanNumeral)
    {
        if (char.IsAsciiDigit(nextChar)
            || nextChar is >= '０' and <= '９'
            || char.IsAsciiLetter(nextChar)
            || nextChar is >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
            return true;

        if (InvalidFollowingUnitChars.Contains(nextChar)) return true;
        if (isChineseNumeral
            && ("一二三四五六七八九十百千万亿零〇".Contains(nextChar)
                || InvalidFollowingChineseNumeralChars.Contains(nextChar)))
            return true;

        return isRomanNumeral && char.IsLetter(nextChar);
    }

    private static bool TryNormalizeFigureTableKey(
        string kindRaw,
        string numRaw,
        out string category,
        out string normalizedNumber,
        out bool isChineseNumeral,
        out bool isRomanNumeral)
    {
        category = string.Empty;
        normalizedNumber = string.Empty;
        isChineseNumeral = false;
        isRomanNumeral = false;

        var trimmedKind = kindRaw.Trim();
        if (trimmedKind is "附图" or "附圖") category = "appfig";
        else if (trimmedKind == "附表") category = "apptab";
        else if (trimmedKind is "图" or "圖" or "插图" or "插圖" || trimmedKind.StartsWith("fig", StringComparison.OrdinalIgnoreCase))
            category = "fig";
        else if (trimmedKind == "表" || trimmedKind.StartsWith("tab", StringComparison.OrdinalIgnoreCase))
            category = "tab";
        else return false;

        var builder = new StringBuilder(numRaw.Length);
        var hasArabicDigit = false;
        foreach (var ch in numRaw)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is >= '０' and <= '９')
            {
                builder.Append((char)('0' + (ch - '０')));
                hasArabicDigit = true;
            }
            else if (ch is >= '0' and <= '9')
            {
                builder.Append(ch);
                hasArabicDigit = true;
            }
            else if (ch is >= 'Ａ' and <= 'Ｚ') builder.Append(char.ToLowerInvariant((char)('A' + (ch - 'Ａ'))));
            else if (ch is >= 'ａ' and <= 'ｚ') builder.Append((char)('a' + (ch - 'ａ')));
            else if (ch is '.' or '．' or '-' or '—' or '–' or '_' or '－')
            {
                if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            }
            else if ("一二三四五六七八九十百零〇".Contains(ch))
            {
                builder.Append(ch == '〇' ? '零' : ch);
                isChineseNumeral = true;
            }
            else if (char.IsAsciiLetter(ch)) builder.Append(char.ToLowerInvariant(ch));
        }

        while (builder.Length > 0 && builder[^1] == '-') builder.Length--;
        if (builder.Length == 0) return false;

        if (!hasArabicDigit && !isChineseNumeral)
        {
            foreach (var ch in numRaw)
            {
                if (!char.IsWhiteSpace(ch) && !"IVXLCDM".Contains(ch)) return false;
            }
            isRomanNumeral = true;
        }

        normalizedNumber = builder.ToString();
        return true;
    }

    private static bool IsFigureCategory(string category) => category is "fig" or "appfig";

    private static bool IsTableCategory(string category) => category is "tab" or "apptab";

    private static string GetVisibleBlockText(ContentBlock block)
    {
        var builder = new StringBuilder();
        foreach (var item in block.Items)
        {
            if (!item.Ghost && item.Kind is InlineKind.Text or InlineKind.Ruby)
                builder.Append(item.Text);
        }
        return CollapseWhitespace(builder.ToString());
    }

    private static string GetTableFirstRowText(ContentBlock block)
    {
        if (block.Table is not { Rows.Count: > 0 } table || table.Rows[0].Cells.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var item in table.Rows[0].Cells[0].Items)
        {
            if (!item.Ghost && item.Kind is InlineKind.Text or InlineKind.Ruby)
                builder.Append(item.Text);
        }
        return CollapseWhitespace(builder.ToString());
    }

    private static string NormalizeCaptionPreview(string value)
    {
        var collapsed = CollapseWhitespace(value);
        return collapsed.Length <= 240 ? collapsed : collapsed[..240] + "…";
    }

    private static int GetEarliestTextOffset(ContentBlock block)
    {
        foreach (var item in block.Items)
        {
            if (!item.Ghost && item.TextStart >= 0 && item.Text.Length > 0) return item.TextStart;
        }
        return -1;
    }

    private static char? GetPreviousVisibleChar(InlineItem[] items, int currentIndex)
    {
        for (var i = currentIndex - 1; i >= 0; i--)
        {
            var item = items[i];
            if (!item.Ghost && item.Kind is InlineKind.Text or InlineKind.Ruby && item.Text.Length > 0)
                return item.Text[^1];
        }
        return null;
    }

    private static char? GetNextVisibleChar(InlineItem[] items, int currentIndex)
    {
        for (var i = currentIndex + 1; i < items.Length; i++)
        {
            var item = items[i];
            if (!item.Ghost && item.Kind is InlineKind.Text or InlineKind.Ruby && item.Text.Length > 0)
                return item.Text[0];
        }
        return null;
    }

    private static ContentBlock CloneBlockWithExtraFragmentId(ContentBlock source, string fragmentId) => new()
    {
        Kind = source.Kind,
        ElementId = source.ElementId,
        FragmentIds = source.FragmentIds.Concat([fragmentId]).Distinct(StringComparer.Ordinal).ToArray(),
        FootnoteReferenceFragmentIds = source.FootnoteReferenceFragmentIds,
        Style = source.Style,
        Center = source.Center,
        AlignRight = source.AlignRight,
        Justify = source.Justify,
        TextIndentEm = source.TextIndentEm,
        ExplicitTextIndent = source.ExplicitTextIndent,
        SpaceBeforeLines = source.SpaceBeforeLines,
        SpaceAfterLines = source.SpaceAfterLines,
        ListMarker = source.ListMarker,
        ListLevel = source.ListLevel,
        LineHeightFactor = source.LineHeightFactor,
        MarginTopEm = source.MarginTopEm,
        MarginBottomEm = source.MarginBottomEm,
        MarginLeftEm = source.MarginLeftEm,
        MarginRightEm = source.MarginRightEm,
        PaddingLeftEm = source.PaddingLeftEm,
        PaddingRightEm = source.PaddingRightEm,
        BorderLeftEm = source.BorderLeftEm,
        HeadingLevel = source.HeadingLevel,
        PageBreakBefore = source.PageBreakBefore,
        PageBreakAfter = source.PageBreakAfter,
        KeepWithNext = source.KeepWithNext,
        IsCaption = source.IsCaption,
        Table = source.Table,
        Items = source.Items,
    };
}
