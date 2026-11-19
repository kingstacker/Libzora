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
/// AI polyphone review and pinyin tone conversion.
/// </summary>
public sealed partial class PinyinBookService : IPinyinBookService
{
    private static PinyinReviewContext ExtractReviewContext(string value, int characterIndex)
    {
        if (value.Length <= MaxAiReviewContextCharacters)
            return new PinyinReviewContext(value, characterIndex);
        var half = MaxAiReviewContextCharacters / 2;
        var start = Math.Clamp(characterIndex - half, 0, Math.Max(0, value.Length - MaxAiReviewContextCharacters));
        var end = Math.Min(value.Length, start + MaxAiReviewContextCharacters);
        if (start > 0 && char.IsSurrogatePair(value, start - 1)) start--;
        if (end < value.Length && char.IsSurrogatePair(value, end - 1)) end++;
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < value.Length ? "…" : string.Empty;
        return new PinyinReviewContext(prefix + value[start..end] + suffix, characterIndex - start + prefix.Length);
    }

    private async Task<string?> ReviewPinyinAsync(
        IReadOnlyList<PinyinReviewTarget> targets,
        PinyinBookOptions options,
        Action<string, string, double> report,
        Action<IReadOnlyList<PinyinReviewTarget>, double> persistBatch,
        CancellationToken cancellationToken)
    {
        if (!options.EnableAiReview) return null;
        var pendingTargets = targets.Where(target => !target.Reviewed).ToArray();
        if (pendingTargets.Length == 0) return null;
        if (_aiChatClient is null || _aiSettings is null || !_aiSettings.IsConfigured)
            return "AI 未配置";

        var requests = pendingTargets
            .GroupBy(CreateReviewKey, StringComparer.Ordinal)
            .Select((group, index) =>
            {
                var first = group.First();
                return new PinyinReviewRequest(
                    index + 1, first.Character, NormalizeToneMarkedPinyin(first.CurrentPinyin),
                    first.Candidates, first.Context, first.ContextCharacterIndex, group.ToArray());
            })
            .ToArray();

        for (var offset = 0; offset < requests.Length; offset += options.AiReviewBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = requests.Skip(offset).Take(options.AiReviewBatchSize).ToArray();
            var batchEnd = offset + batch.Length;
            report("正在复核疑问拼音",
                $"正在请求 AI：{offset + 1}-{batchEnd} / {requests.Length} 项",
                70 + offset * 20d / requests.Length);

            string answer;
            try
            {
                answer = await _aiChatClient.CompleteAsync(
                    _aiSettings, AiReviewInstructions, BuildAiReviewQuestion(batch),
                    Array.Empty<AiConversationTurn>(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return $"AI 请求失败：{exception.Message}";
            }

            if (!TryReadAiReview(answer, batch, out var decisions))
                return "AI 返回格式无法识别";

            var checkedTargets = new List<PinyinReviewTarget>();
            var missingDecisions = false;
            foreach (var request in batch)
            {
                if (!decisions.TryGetValue(request.Id, out var selectedPinyin))
                {
                    missingDecisions = true;
                    continue;
                }
                foreach (var target in request.Targets)
                {
                    if (selectedPinyin is not null)
                    {
                        var rt = target.Ruby.Elements().First(element =>
                            element.Name.LocalName.Equals("rt", StringComparison.OrdinalIgnoreCase));
                        rt.Value = ApplyPinyinStyle(selectedPinyin, options.OutputStyle);
                    }
                    target.Reviewed = true;
                    checkedTargets.Add(target);
                }
            }

            // Commit each successful target before another request/cancellation.
            // A paragraph may span several batches; its index alone is insufficient.
            var percentage = 70 + batchEnd * 20d / requests.Length;
            if (checkedTargets.Count > 0) persistBatch(checkedTargets, percentage);
            if (missingDecisions)
                return "AI 返回缺少有效的复核结果，未完成项已保存。";
            report("正在复核疑问拼音", $"已处理 {batchEnd:N0} / {requests.Length:N0} 项", percentage);
        }
        return null;
    }

    private static string BuildAiReviewQuestion(IReadOnlyList<PinyinReviewRequest> requests)
    {
        var payload = requests.Select(request => new
        {
            id = request.Id,
            character = request.Character.ToString(),
            context = request.Context.Insert(request.ContextCharacterIndex + 1, "⟧")
                .Insert(request.ContextCharacterIndex, "⟦"),
            current = request.CurrentPinyin,
            candidates = request.Candidates
        });
        return "请判断下面这些汉字在各自上下文中的普通话拼音。"
            + "只判断 context 中 ⟦⟧ 标出的那个字；同一句中其他位置的同字可能读音不同。"
            + "每项只能从 candidates 中选择一个；无法确定时 pinyin 返回 null。"
            + "不要改写句子，不要添加候选之外的读音。只返回 JSON 数组："
            + Environment.NewLine
            + JsonSerializer.Serialize(payload);
    }

    private bool TryReadAiReview(
        string answer,
        IReadOnlyList<PinyinReviewRequest> requests,
        out Dictionary<int, string?> decisions)
    {
        decisions = new Dictionary<int, string?>();
        var json = ExtractJsonPayload(answer);
        if (json.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root : FindReviewItems(root);
            if (items.ValueKind != JsonValueKind.Array) return false;
            var requestById = requests.ToDictionary(request => request.Id);
            foreach (var item in items.EnumerateArray())
            {
                if (!TryReadReviewId(item, out var id)
                    || !requestById.TryGetValue(id, out var request)
                    || !item.TryGetProperty("pinyin", out var pinyinValue))
                    continue;
                if (pinyinValue.ValueKind == JsonValueKind.Null)
                {
                    // Explicit uncertainty is a reviewed result; omitted ids or
                    // invalid pronunciations must still be retried.
                    decisions[id] = null;
                    continue;
                }
                if (pinyinValue.ValueKind != JsonValueKind.String) continue;
                var selected = NormalizeToneMarkedPinyin(pinyinValue.GetString() ?? string.Empty);
                var allowed = request.Candidates.FirstOrDefault(candidate =>
                    NormalizeToneMarkedPinyin(candidate).Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (allowed is not null) decisions[id] = NormalizeToneMarkedPinyin(allowed);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement FindReviewItems(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return default;
        foreach (var propertyName in new[] { "items", "results", "decisions" })
        {
            if (root.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        return default;
    }

    private static bool TryReadReviewId(JsonElement item, out int id)
    {
        id = 0;
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (!item.TryGetProperty("id", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out id);
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out id);
    }

    private static string ExtractJsonPayload(string value)
    {
        var answer = value.Trim();
        var fencedStart = answer.IndexOf("```", StringComparison.Ordinal);
        if (fencedStart >= 0)
        {
            var contentStart = answer.IndexOf('\n', fencedStart);
            var contentEnd = answer.IndexOf("```", contentStart >= 0 ? contentStart + 1 : fencedStart + 3, StringComparison.Ordinal);
            if (contentStart >= 0 && contentEnd > contentStart)
                answer = answer[(contentStart + 1)..contentEnd].Trim();
        }

        var arrayStart = answer.IndexOf('[');
        var arrayEnd = answer.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
            return answer[arrayStart..(arrayEnd + 1)];

        var objectStart = answer.IndexOf('{');
        var objectEnd = answer.LastIndexOf('}');
        return objectStart >= 0 && objectEnd > objectStart
            ? answer[objectStart..(objectEnd + 1)]
            : string.Empty;
    }

    private static string CreateReviewKey(PinyinReviewTarget target) =>
        $"{target.Character}\u001F{NormalizeToneMarkedPinyin(target.CurrentPinyin)}\u001F"
        + $"{target.Context}\u001F{target.ContextCharacterIndex}\u001F{string.Join('\u001E', target.Candidates)}";

    private static string NormalizeToneMarkedPinyin(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Replace("u:", "ü", StringComparison.Ordinal)
            .Replace("v", "ü", StringComparison.Ordinal)
            .Where(character => !char.IsWhiteSpace(character))
            .ToArray());
        if (normalized.Any(char.IsDigit))
            normalized = ConvertToneNumberToMarked(normalized);
        return normalized.Trim().Trim('`', '"', '\'', '。', '，', ',', '.');
    }

    private static string ApplyPinyinStyle(string toneMarkedPinyin, PinyinBookOutputStyle style) => style switch
    {
        PinyinBookOutputStyle.ToneNumber => ConvertToneMarkedToNumber(toneMarkedPinyin),
        PinyinBookOutputStyle.NoTone => RemoveToneMarks(toneMarkedPinyin),
        _ => toneMarkedPinyin
    };

    private static string ConvertToneNumberToMarked(string value)
    {
        var tone = 5;
        var baseText = value;
        if (value.Length > 0 && value[^1] is >= '1' and <= '5')
        {
            tone = value[^1] - '0';
            baseText = value[..^1];
        }

        baseText = RemoveToneMarks(baseText);
        if (tone is < 1 or > 4) return baseText;

        var placement = FindToneVowelIndex(baseText);
        if (placement < 0) return baseText;
        var vowel = baseText[placement];
        return baseText[..placement]
            + (ToneMarkByBaseAndTone.TryGetValue((vowel, tone), out var marked)
                ? marked.ToString()
                : vowel.ToString())
            + baseText[(placement + 1)..];
    }

    private static string ConvertToneMarkedToNumber(string value)
    {
        var tone = 5;
        var builder = new StringBuilder(value.Length + 1);
        foreach (var character in value)
        {
            if (ToneMarks.TryGetValue(character, out var toneMark))
            {
                tone = toneMark.Tone;
                builder.Append(toneMark.Base);
            }
            else builder.Append(character);
        }

        if (tone is >= 1 and <= 4) builder.Append(tone);
        return builder.ToString();
    }

    private static string RemoveToneMarks(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (ToneMarks.TryGetValue(character, out var toneMark))
            {
                builder.Append(toneMark.Base);
                continue;
            }

            if (character is >= '1' and <= '5') continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static int FindToneVowelIndex(string value)
    {
        var a = value.IndexOf('a');
        if (a >= 0) return a;
        var e = value.IndexOf('e');
        if (e >= 0) return e;
        var ou = value.IndexOf("ou", StringComparison.Ordinal);
        if (ou >= 0) return ou;

        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (value[index] is 'a' or 'e' or 'i' or 'o' or 'u' or 'ü')
                return index;
        }
        return -1;
    }


    private const string AiReviewInstructions = "你是中文普通话拼音校对器。只处理指定的多音字，严格遵守用户给出的候选读音。";
}
