using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Libzora.Core;

public sealed record BookReflectionCitationRecord(
    Guid Id,
    Guid BookId,
    string BookTitle,
    string ChapterLabel,
    string SelectedText,
    string Note,
    DateTimeOffset UpdatedAt)
{
    public string DisplayText => string.IsNullOrWhiteSpace(SelectedText)
        ? string.IsNullOrWhiteSpace(Note) ? UiText.Get("未命名批注") : Note
        : SelectedText;

    public string SearchText => string.Join('\n', BookTitle, ChapterLabel, SelectedText, Note);
}

/// <summary>
/// Shared Markdown normalization, preview extraction, citation formatting and
/// filtering for book-level reflections across desktop and mobile heads.
/// </summary>
public static class BookReflectionMarkdown
{
    private static readonly Regex ImageLinePattern = new(
        @"^\s*!\[(?<alt>[^\]]*)\]\((?<source>[^)\s\r\n]+)(?:\s*=\s*(?<width>\d+)(?:x(?<height>\d+))?)?\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex InlineImageTokenPattern = new(
        @"!\[[^\]\r\n]*\]\([^\)\r\n]*\)",
        RegexOptions.Compiled);

    public static string NormalizeMarkdown(string? markdown)
    {
        var normalized = markdown ?? string.Empty;
        for (var attempt = 0; attempt < 3 && LooksLikeNestedJsonString(normalized); attempt++)
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<string>(normalized);
                if (decoded is null || decoded == normalized)
                    break;
                normalized = decoded;
            }
            catch (JsonException)
            {
                break;
            }
        }
        return normalized;
    }

    public static string RemoveImagesForPreview(string? markdown)
    {
        var normalized = NormalizeMarkdown(markdown).ReplaceLineEndings("\n");
        var lines = normalized.Split('\n');
        var preview = new StringBuilder(normalized.Length);

        foreach (var line in lines)
        {
            if (ImageLinePattern.IsMatch(line))
                continue;

            preview.AppendLine(InlineImageTokenPattern.Replace(line, string.Empty));
        }

        return preview.ToString().Trim();
    }

    public static string BuildPreviewMarkdown(string? markdown, int maxLength = 480)
    {
        var normalized = RemoveImagesForPreview(markdown)
            .Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "…";
    }

    public static IReadOnlyList<string> ExtractPlainPreviewLines(string? markdown)
    {
        var preview = RemoveImagesForPreview(markdown);
        if (string.IsNullOrWhiteSpace(preview))
            return [];

        var lines = new List<string>();
        foreach (var raw in preview.ReplaceLineEndings("\n").Split('\n'))
        {
            var text = raw.Trim();
            text = Regex.Replace(text, @"^#{1,6}\s*", string.Empty);
            text = Regex.Replace(text, @"^>\s?", string.Empty);
            text = Regex.Replace(text, @"^[-*+]\s+", string.Empty);
            text = Regex.Replace(text, @"^\d+[.)]\s+", string.Empty);
            text = Regex.Replace(text, @"(\*\*|__|~~|`)", string.Empty);
            text = Regex.Replace(text, @"\[(?<label>[^\]]+)\]\([^)]*\)", "${label}");
            lines.Add(text);
        }
        return lines;
    }

    public static string GetFirstPreviewLine(string? markdown)
    {
        foreach (var line in ExtractPlainPreviewLines(markdown))
        {
            if (line.Length > 0)
                return line;
        }
        return string.Empty;
    }

    public static string GetPlainPreviewText(string? markdown) =>
        string.Join("\n", ExtractPlainPreviewLines(markdown)).Trim();

    public static int CountCharacters(string? markdown)
    {
        var withoutImages = RemoveImagesForPreview(markdown);
        if (withoutImages.Length == 0)
            return 0;

        var total = 0;
        foreach (var line in withoutImages.ReplaceLineEndings("\n").Split('\n'))
        {
            var plain = line.TrimStart();
            if (Regex.Match(plain, @"^#{1,6}\s+(?<rest>.*)$") is { Success: true } heading)
                plain = heading.Groups["rest"].Value;
            else if (Regex.Match(plain, @"^>\s?(?<rest>.*)$") is { Success: true } quote)
                plain = quote.Groups["rest"].Value;
            else if (Regex.Match(plain, @"^[-*+]\s+(?<rest>.*)$") is { Success: true } unordered)
                plain = unordered.Groups["rest"].Value;
            else if (Regex.Match(plain, @"^\d+[.)]\s+(?<rest>.*)$") is { Success: true } ordered)
                plain = ordered.Groups["rest"].Value;

            plain = Regex.Replace(plain, @"\*\*([^*\r\n]+)\*\*", "$1");
            plain = Regex.Replace(plain, @"\[([^\]\r\n]+)\]\([^)\r\n]+\)", "$1");
            total += plain.Length;
        }
        return total;
    }

    public static string FormatCitationSource(string? bookTitle)
    {
        var title = NormalizeCitationBookTitle(bookTitle);
        if (title.Length == 0)
            return string.Empty;

        var decoratedTitle = title.StartsWith("《", StringComparison.Ordinal)
            && title.EndsWith("》", StringComparison.Ordinal)
            ? title
            : $"《{title}》";
        return $"--{decoratedTitle}";
    }

    public static string FormatCitationQuoteBlock(string quoteText, string? bookTitle)
    {
        var trimmed = (quoteText ?? string.Empty).Trim().ReplaceLineEndings("\n");
        if (trimmed.Length == 0)
            return string.Empty;

        var quoted = string.Join("\n", trimmed.Split('\n').Select(line => $"> {line}"));
        var source = FormatCitationSource(bookTitle);
        return source.Length > 0 ? $"{quoted}\n>\n> {source}" : quoted;
    }

    public static string ResolveCitationChapterLabel(string? chapterPath)
    {
        var path = chapterPath ?? string.Empty;
        if (path.StartsWith("pdf:page:", StringComparison.OrdinalIgnoreCase))
        {
            var pageText = path["pdf:page:".Length..];
            return int.TryParse(pageText, out var page) && page > 0
                ? UiText.Get("第 {0} 页", page)
                : "PDF";
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var name = Path.GetFileNameWithoutExtension(normalized);
        return string.IsNullOrWhiteSpace(name) ? UiText.Get("未指定章节") : name;
    }

    public static IReadOnlyList<BookReflectionCitationRecord> FilterCitations(
        IEnumerable<BookReflectionCitationRecord> citations,
        string? query,
        string? currentBookTitle = null)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        return citations
            .Where(citation => normalizedQuery.Length == 0
                || citation.SearchText.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(citation => IsSameBook(citation.BookTitle, currentBookTitle))
            .ThenByDescending(citation => citation.UpdatedAt)
            .ToArray();
    }

    public static bool IsSameBook(string? citationBookTitle, string? currentBookTitle)
    {
        var citationTitle = NormalizeBookTitleForMatch(citationBookTitle);
        var currentTitle = NormalizeBookTitleForMatch(currentBookTitle);
        return currentTitle.Length > 0
            && string.Equals(citationTitle, currentTitle, StringComparison.CurrentCultureIgnoreCase);
    }

    public static string NormalizeCitationBookTitle(string? value) =>
        (value ?? string.Empty).Trim().ReplaceLineEndings(" ");

    private static string NormalizeBookTitleForMatch(string? title)
    {
        var normalized = title?.Trim() ?? string.Empty;
        return normalized.Length >= 2
            && normalized[0] == '《'
            && normalized[^1] == '》'
            ? normalized[1..^1].Trim()
            : normalized;
    }

    private static bool LooksLikeNestedJsonString(string value) =>
        value.Length >= 2
        && value[0] == '"'
        && value[^1] == '"'
        && (value.Contains("\\n", StringComparison.Ordinal)
            || value.Contains("\\r", StringComparison.Ordinal)
            || value.Contains("\\\"", StringComparison.Ordinal)
            || value.Contains("\\\\", StringComparison.Ordinal));
}
