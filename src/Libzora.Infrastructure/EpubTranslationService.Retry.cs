using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Single-segment translation and retry.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private async Task<string> TranslateSegmentAsync(
        TranslationSegment segment,
        TranslationRequestContext context,
        CancellationToken cancellationToken)
    {
        var markup = segment.Markup;
        if (!markup.HasMarkers || markup.RequestText.Length <= GetSingleRequestCharacterLimit(context.Settings.Provider))
        {
            var translated = await TranslateTextWithRetryAsync(
                markup.RequestText, context, cancellationToken, cacheParts: !markup.HasMarkers);
            if (markup.IsValidTranslation(translated)) return translated;
            if (!markup.HasMarkers)
                throw new InvalidDataException("翻译结果包含无法写入 EPUB 的字符，请重试。");
        }

        // Providers that strip markup markers cannot safely align a translated
        // sentence with links/emphasis. Translate its text runs independently
        // and retain the original local element boundaries instead.
        var builder = new StringBuilder();
        foreach (var part in markup.Parts())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.IsMarker || !ContainsTranslatableCharacters(part.Text))
            {
                builder.Append(part.Text);
                continue;
            }
            var text = part.Text.Trim();
            var leading = part.Text.Length - part.Text.TrimStart().Length;
            var trailing = part.Text.Length - part.Text.TrimEnd().Length;
            builder.Append(part.Text.AsSpan(0, leading));
            builder.Append(await TranslateTextWithRetryAsync(text, context, cancellationToken));
            if (trailing > 0) builder.Append(part.Text.AsSpan(part.Text.Length - trailing));
        }
        var result = builder.ToString();
        if (!markup.IsValidTranslation(result))
            throw new InvalidDataException("翻译结果的行内结构校验失败，请重试。");
        return result;
    }

    private async Task<string> TranslateTextWithRetryAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken,
        bool cacheParts = true)
    {
        var settings = context.Settings;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (IsSameLanguage(settings.SourceLanguage, settings.TargetLanguage)) return text;
        if (cacheParts && context.Parts.TryGetValue(text, out var completedPart)) return completedPart;

        async Task<string> TranslatePartAsync(string part)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cacheParts && context.Parts.TryGetValue(part, out var cached)) return cached;
            var value = await TranslateSingleWithRetryAsync(part, context, cancellationToken);
            if (cacheParts)
            {
                context.Parts[part] = value;
                context.CacheWriter.AppendPart(part, value);
            }
            return value;
        }

        var maxLength = GetSingleRequestCharacterLimit(settings.Provider);
        var hasSavedPlan = cacheParts && context.SplitPlans.TryGetValue(text, out _);
        if (hasSavedPlan || text.Length > maxLength)
        {
            var parts = hasSavedPlan ? context.SplitPlans[text] : SplitText(text, maxLength);
            if (cacheParts && !hasSavedPlan && parts.Count > 1)
            {
                context.SplitPlans[text] = parts;
                context.CacheWriter.AppendSplitPlan(text, parts);
            }
            var translatedParts = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                // Keep the previous provider's successful chunk boundaries when
                // switching engines. Only unfinished oversized chunks need splitting.
                translatedParts.Add(parts.Count > 1 && part.Length > maxLength
                    ? await TranslateTextWithRetryAsync(part, context, cancellationToken, cacheParts)
                    : await TranslatePartAsync(part));
            }
            return string.Join(" ", translatedParts.Where(part => part.Length > 0));
        }

        return await TranslatePartAsync(text);
    }

    private async Task<string> TranslateSingleWithRetryAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var translated = await TranslateSingleAsync(text, context, cancellationToken);
                if (string.IsNullOrWhiteSpace(translated))
                    throw new InvalidDataException("翻译服务返回了空文本。 ");
                XmlConvert.VerifyXmlChars(translated);
                return translated.Trim();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (attempt >= MaxRetryCount) break;
                await Task.Delay(TimeSpan.FromMilliseconds(350 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{GetProviderDisplayName(settings.Provider)} 翻译失败：{lastFailure?.Message ?? "未知错误"}",
            lastFailure);
    }

    private Task<string> TranslateSingleAsync(string text, TranslationRequestContext context,
        CancellationToken cancellationToken) => TranslateWithAiAsync(text, context, cancellationToken);

    private async Task<string> TranslateWithAiAsync(
        string text,
        TranslationRequestContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        context.AiSettings ??= await _aiSettingsStore.LoadAsync(cancellationToken);
        if (!context.AiSettings.IsConfigured)
            throw new InvalidOperationException("请先在“阅读 → AI 设置”中配置 AI 服务、模型和 API Key。 ");

        var source = TranslationLanguageCatalog.Find(settings.SourceLanguage).DisplayName;
        var target = TranslationLanguageCatalog.Find(settings.TargetLanguage).DisplayName;
        var instructions =
            $"你是专业的书籍翻译引擎。请将文本从{source}翻译成{target}。"
            + "只输出译文，不要解释、不要加引号、不要添加标题。"
            + "必须原样保留形如 __LIBZORA_SEG_0__、__LIBZORA_INLINE_0__ 的标记及其顺序；标记之间的正文需要翻译。";
        await _aiRequestsPerMinuteLimiter.WaitAsync(
            settings.AiRequestsPerMinute,
            cancellationToken);
        return await _aiChatClient.CompleteAsync(
            context.AiSettings,
            instructions,
            text,
            Array.Empty<AiConversationTurn>(),
            cancellationToken);
    }
}
