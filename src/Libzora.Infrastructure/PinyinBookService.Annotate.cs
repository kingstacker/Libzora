using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotNetG2P.Chinese;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Text-node annotation, context analysis and segmentation.
/// </summary>
public sealed partial class PinyinBookService : IPinyinBookService
{
    private PinyinAnnotationResult AnnotateTextNode(
        PinyinSegment segment,
        PinyinBookOptions options,
        CancellationToken cancellationToken)
    {
        var textNode = segment.TextNode;
        var value = segment.OriginalText;
        var parent = textNode.Parent;
        if (parent is null || value.Length == 0)
            return new PinyinAnnotationResult([], 0, [], string.Empty);

        var analysis = segment.Context.Analysis ??= AnalyzeContext(segment.Context.Text, options, cancellationToken);
        var nodes = new List<XNode>();
        var reviewTargets = new List<PinyinReviewTarget>();
        var annotatedCharacters = 0;
        var cursor = 0;
        while (cursor < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsHanCharacter(value[cursor]))
            {
                var plainStart = cursor++;
                while (cursor < value.Length && !IsHanCharacter(value[cursor])) cursor++;
                nodes.Add(new XText(value[plainStart..cursor]));
                continue;
            }

            var characterIndex = cursor++;
            var character = value[characterIndex];
            var contextIndex = segment.ContextOffset + characterIndex;
            var pinyin = analysis.Pinyins[contextIndex];
            if (string.IsNullOrWhiteSpace(pinyin) || !_pinyinEngine.ContainsChar(character))
            {
                nodes.Add(new XText(character.ToString()));
                continue;
            }

            var ns = parent.Name.Namespace;
            var ruby = new XElement(ns + "ruby",
                new XText(character.ToString()), new XElement(ns + "rt", pinyin));
            nodes.Add(ruby);
            if (analysis.Decisions[contextIndex] is { Candidates.Count: > 1 } decision)
            {
                var context = ExtractReviewContext(segment.Context.Text, contextIndex);
                reviewTargets.Add(new PinyinReviewTarget(
                    segment.Index, characterIndex, ruby, character, pinyin,
                    decision.Candidates, context.Text, context.CharacterIndex));
            }
            annotatedCharacters++;
        }

        textNode.ReplaceWith(nodes);
        return new PinyinAnnotationResult(
            nodes, annotatedCharacters, reviewTargets, CreateAnnotatedDisplayText(nodes));
    }

    private PinyinContextAnalysis AnalyzeContext(
        string text,
        PinyinBookOptions options,
        CancellationToken cancellationToken)
    {
        var pinyins = _pinyinEngine.ToPinyinList(text, options.OutputStyle, cancellationToken);
        if (pinyins.Length != text.Length)
            throw new InvalidDataException("注音引擎返回的字符位置与原文不一致。");
        var decisions = new LocalPinyinDecision?[text.Length];
        var cursor = 0;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsHanCharacter(text[cursor]))
            {
                cursor++;
                continue;
            }
            var start = cursor++;
            while (cursor < text.Length && IsHanCharacter(text[cursor])) cursor++;
            var runDecisions = AnalyzeHanRun(text[start..cursor], pinyins[start..cursor]);
            for (var index = 0; index < runDecisions.Count; index++)
                decisions[start + index] = runDecisions[index];
        }
        return new PinyinContextAnalysis(pinyins, decisions);
    }

    private static IReadOnlyList<PinyinSegment> GetPinyinSegments(XElement body, string entryName)
    {
        var segments = new List<PinyinSegment>();
        var pending = new List<XText>();
        void FlushContext()
        {
            if (pending.Count == 0) return;
            var context = new PinyinTextContext(string.Concat(pending.Select(node => node.Value)));
            var offset = 0;
            foreach (var node in pending)
            {
                if (node.Value.Any(IsHanCharacter))
                    segments.Add(new PinyinSegment(-1, entryName, node, node.Value, context, offset));
                offset += node.Value.Length;
            }
            pending.Clear();
        }

        void Walk(XNode node)
        {
            if (node is XText text)
            {
                pending.Add(text);
                return;
            }
            if (node is not XElement element) return;
            var name = element.Name.LocalName;
            if (ShouldSkipElement(element)
                || name.Equals("br", StringComparison.OrdinalIgnoreCase)
                || name.Equals("hr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                FlushContext();
                return;
            }
            var block = BlockElementNames.Contains(name);
            if (block) FlushContext();
            foreach (var child in element.Nodes()) Walk(child);
            if (block) FlushContext();
        }

        Walk(body);
        FlushContext();
        return segments;
    }

    private IReadOnlyList<LocalPinyinDecision> AnalyzeHanRun(
        string hanText,
        IReadOnlyList<string> pinyins)
    {
        var decisions = new List<LocalPinyinDecision>(hanText.Length);
        var cursor = 0;
        while (cursor < hanText.Length)
        {
            var phraseLength = _phraseDictionary.FindLongestMatch(
                hanText,
                cursor,
                out _);
            if (phraseLength > 1)
            {
                for (var offset = 0; offset < phraseLength && cursor + offset < hanText.Length; offset++)
                {
                    var pinyin = cursor + offset < pinyins.Count
                        ? pinyins[cursor + offset]
                        : string.Empty;
                    decisions.Add(new LocalPinyinDecision(
                        pinyin,
                        Array.Empty<string>(),
                        FromPhraseDictionary: true));
                }

                cursor += phraseLength;
                continue;
            }

            var character = hanText[cursor];
            var candidates = _pinyinEngine.LookupChar(character)
                .Select(NormalizeToneMarkedPinyin)
                .Where(candidate => candidate.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var currentPinyin = cursor < pinyins.Count ? pinyins[cursor] : string.Empty;
            decisions.Add(new LocalPinyinDecision(
                currentPinyin,
                candidates,
                FromPhraseDictionary: false));
            cursor++;
        }

        while (decisions.Count < hanText.Length)
        {
            var index = decisions.Count;
            decisions.Add(new LocalPinyinDecision(
                index < pinyins.Count ? pinyins[index] : string.Empty,
                Array.Empty<string>(),
                FromPhraseDictionary: false));
        }

        return decisions;
    }
}
