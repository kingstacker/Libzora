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
/// Text splitting, language checks and reporting.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private static int GetBatchCharacterLimit(BookTranslationProvider provider) => AiRequestCharacterLimit;
    private static int GetTranslationConcurrency(BookTranslationProvider provider) => 2;
    private static int GetSingleRequestCharacterLimit(BookTranslationProvider provider) => AiRequestCharacterLimit;

    private static IReadOnlyList<string> SplitText(string text, int maxLength)
    {
        var parts = new List<string>();
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maxLength);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOfAny(['\n', '。', '！', '？', '!', '?', '.', '；', ';', ' '], end - 1, end - start);
                if (boundary > start + maxLength / 3) end = boundary + 1;
                var offset = Array.BinarySearch(boundaries, end);
                if (offset < 0)
                {
                    offset = ~offset;
                    end = offset > 0 && boundaries[offset - 1] > start
                        ? boundaries[offset - 1]
                        : offset < boundaries.Length ? boundaries[offset] : text.Length;
                }
            }
            parts.Add(text[start..end].Trim());
            start = end;
        }
        return parts.Where(part => part.Length > 0).ToArray();
    }

    private static bool ContainsTranslatableCharacters(string text) =>
        text.EnumerateRunes().Any(Rune.IsLetter);

    private static bool IsSameLanguage(string source, string target) =>
        TranslationLanguageCatalog.Find(source).Code.Equals(
            TranslationLanguageCatalog.Find(target).Code,
            StringComparison.OrdinalIgnoreCase)
        || (source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && target.Equals("auto", StringComparison.OrdinalIgnoreCase));

    private static string CreateTranslationCacheKey(BookTranslationSettings settings, string text) =>
        $"{settings.Provider}\u001F{settings.SourceLanguage}\u001F{settings.TargetLanguage}\u001F{text}";

    private static string CreateMarker(int index) =>
        $"__LIBZORA_SEG_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}__";

    private static string CreateSegmentKey(string entryName, int ordinal) =>
        $"{entryName}\u001F{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string GetProviderDisplayName(BookTranslationProvider provider) => "模型翻译";

    private static string GetOutputModeLabel(BookTranslationOutputMode mode) =>
        mode == BookTranslationOutputMode.Bilingual ? "双语对照" : "单翻译";

    private static string CreateOutputPath(
        string directory,
        string sourcePath,
        string suffix,
        char separator = '-')
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character)).Trim();
        if (safe.Length == 0) safe = "书籍";
        if (safe.Length > 120) safe = safe[..120].TrimEnd();
        return Path.Combine(directory, $"{safe}{separator}{suffix}.epub");
    }

    private static bool IsXhtmlEntry(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeArchiveEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith('/')
            || name.StartsWith('\\')
            || Path.IsPathFullyQualified(name))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");
        var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。 ");
    }

    private static void Report(
        IProgress<BookTranslationProgress>? progress,
        BookTranslationProgress value) => progress?.Report(value);

    private static void ReportSegmentStateCore(
        IProgress<BookTranslationProgress>? progress,
        TranslationSegment segment,
        int processed,
        int total,
        long processedCharacters,
        long totalCharacters,
        BookTranslationSegmentStatus status,
        string translatedText,
        string processFlow,
        string? errorMessage,
        TranslationCacheWriter cacheWriter)
    {
        var update = new BookTranslationSegmentProgress(
            segment.Index,
            segment.Key.Split('\u001F')[0],
            segment.Text,
            segment.Markup.DisplayText(translatedText),
            status,
            processFlow,
            errorMessage);
        cacheWriter.Append(update, segment.Markup.RequestText, translatedText);
        Report(progress, new BookTranslationProgress(
            "正在翻译",
            segment.Key.Split('\u001F')[0],
            processed,
            total,
            processedCharacters,
            totalCharacters,
            update));
    }
}
