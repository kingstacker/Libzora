using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Libzora.Core;

/// <summary>
/// The metadata one shelf item exposes for series inference. Device books have
/// no explicit series column, so title and file-name markers are accepted too.
/// </summary>
public sealed record BookSeriesCandidate(
    string Id,
    string Title,
    string Authors,
    string? Series = null,
    double? SeriesIndex = null,
    IReadOnlyList<string>? FileTitles = null);

public sealed record BookSeriesMember(string Id, string Title, double? Index);

/// <summary>
/// A stackable series in shelf order. <see cref="Members"/> starts with the
/// front volume and then follows series position, matching how the iOS shelf
/// promotes the most recently opened volume.
/// </summary>
public sealed record BookSeriesStack(
    string Key,
    string Title,
    IReadOnlyList<BookSeriesMember> Members);

/// <summary>
/// Groups shelf items that belong to the same series. Explicit series metadata
/// wins over title/file-name volume markers; a bare trailing number is never
/// treated as a volume because it may be a year or edition.
/// </summary>
public static class BookSeriesGrouping
{
    // Require an explicit volume marker. CJK markers are escaped so the UI
    // string linter ignores the regex source.
    private static readonly Regex VolumePattern = new(
        "^(.+?)(?:[\\s._:\\-–—]*\\u7B2C\\s*([0-9]+)\\s*[\\u5377\\u518A\\u518C]|[\\s._:\\-–—]+(?:vol(?:ume)?\\.?|book)\\s*([0-9]+))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly record struct VolumeHint(string Name, double Index);

    private readonly record struct Candidate(
        BookSeriesCandidate Book,
        string Name,
        string NameKey,
        string AuthorKey,
        double? Index,
        bool Tagged);

    public static IReadOnlyList<BookSeriesStack> Stacks(IEnumerable<BookSeriesCandidate> books)
    {
        ArgumentNullException.ThrowIfNull(books);
        var candidates = new List<Candidate>();
        foreach (var book in books)
            if (Create(book) is { } candidate)
                candidates.Add(candidate);

        var explicitAuthors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!candidate.Tagged) continue;
            if (!explicitAuthors.TryGetValue(candidate.NameKey, out var authors))
                explicitAuthors[candidate.NameKey] = authors = new HashSet<string>(StringComparer.Ordinal);
            authors.Add(candidate.AuthorKey);
        }

        var groups = new Dictionary<(string Name, string Authors), List<Candidate>>();
        foreach (var candidate in candidates)
        {
            // Explicit series metadata can describe a multi-author collection.
            // Author disambiguation is only for inference; an inferred volume
            // joins an explicit collection for a known author.
            var explicitSeries = candidate.Tagged
                || explicitAuthors.TryGetValue(candidate.NameKey, out var authors)
                    && authors.Contains(candidate.AuthorKey);
            var key = explicitSeries
                ? (candidate.NameKey, string.Empty)
                : (candidate.NameKey, candidate.AuthorKey);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = [];
            group.Add(candidate);
        }

        var result = new List<BookSeriesStack>(groups.Count);
        foreach (var ((name, authors), group) in groups)
        {
            if (group.Count < 2) continue;
            var hasTag = group.Any(candidate => candidate.Tagged);
            var distinctIndices = group
                .Where(candidate => candidate.Index is not null)
                .Select(candidate => candidate.Index!.Value)
                .Distinct()
                .Count();
            if (!hasTag && distinctIndices <= 1) continue;

            var ordered = group
                .OrderBy(candidate => candidate.Index ?? double.PositiveInfinity)
                .ThenBy(candidate => candidate.Book.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate.Book.Id, StringComparer.Ordinal)
                .Select(candidate => new BookSeriesMember(
                    candidate.Book.Id,
                    candidate.Book.Title,
                    candidate.Index))
                .ToArray();
            var title = group
                .Where(candidate => candidate.Tagged)
                .Select(candidate => candidate.Name)
                .FirstOrDefault()
                ?? group[0].Name;
            result.Add(new BookSeriesStack($"{name}\u001f{authors}", title, ordered));
        }
        return result;
    }

    private static Candidate? Create(BookSeriesCandidate book)
    {
        var tag = (book.Series ?? string.Empty).Trim();
        var tagged = tag.Length > 0;
        var titleVolume = Volume(book.Title);

        VolumeHint? fileVolume = null;
        if (book.FileTitles is { Count: > 0 })
        {
            var volumes = new List<VolumeHint>();
            foreach (var fileTitle in book.FileTitles)
                if (Volume(fileTitle) is { } volume)
                    volumes.Add(volume);
            // Copies in several formats sometimes carry different names. Use
            // the file name only when every marked file agrees.
            if (volumes.Count > 0)
            {
                var first = volumes[0];
                var consistent = volumes.All(volume =>
                    volume.Index == first.Index
                    && Normalize(volume.Name) == Normalize(first.Name));
                if (consistent) fileVolume = first;
            }
        }

        var hint = titleVolume ?? fileVolume;
        var name = tagged ? tag : hint?.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var nameKey = Normalize(name);
        double? index = book.SeriesIndex is { } seriesIndex && double.IsFinite(seriesIndex)
            ? seriesIndex
            : hint is { } inferred && Normalize(inferred.Name) == nameKey
                ? inferred.Index
                : null;
        return new Candidate(book, name.Trim(), nameKey, Normalize(book.Authors), index, tagged);
    }

    private static VolumeHint? Volume(string value)
    {
        var title = value.Normalize(NormalizationForm.FormKC).Trim();
        var match = VolumePattern.Match(title);
        if (!match.Success) return null;
        var name = match.Groups[1].Value.Trim();
        var number = match.Groups[2].Success
            ? match.Groups[2].Value
            : match.Groups[3].Value;
        if (name.Length == 0
            || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var index)
            || !double.IsFinite(index))
            return null;
        return new VolumeHint(name, index);
    }

    public static string ExplicitSeriesKey(string seriesName) =>
        $"{Normalize(seriesName ?? string.Empty)}\u001f";

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return string.Join(' ', normalized.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
    }
}
