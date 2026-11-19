using System.Globalization;
using System.Text;

namespace Libzora.Layout;

/// <summary>
/// English-US typographic break positions, using the bundled Liang patterns.
/// Positions address the original word; no characters are inserted or removed.
/// </summary>
public static class EnglishHyphenator
{
    private const int LeftMinimum = 2;
    private const int RightMinimum = 3;
    private const int MaximumWordLength = 128;
    private static readonly Lazy<PatternData> Data = new(LoadData);

    public static int[] GetBreakpoints(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        // Do not split short pronouns, initials, identifiers, contractions,
        // URLs, existing hyphenated compounds or non-English spellings.
        if (word.Length < LeftMinimum + RightMinimum || word.Length > MaximumWordLength
            || !word.All(char.IsAsciiLetter) || word.All(char.IsAsciiLetterUpper)) return [];

        var lowercase = word.ToLowerInvariant();
        var data = Data.Value;
        if (data.Exceptions.TryGetValue(lowercase, out var exception))
            return exception.Where(point => point >= LeftMinimum && point <= word.Length - RightMinimum).ToArray();

        var padded = "." + lowercase + ".";
        Span<byte> scores = stackalloc byte[padded.Length + 1];
        scores.Clear();
        for (var start = 0; start < padded.Length; start++)
        {
            var node = data.Root;
            for (var index = start; index < padded.Length; index++)
            {
                if (node.Children is null || !node.Children.TryGetValue(padded[index], out var child)) break;
                node = child;
                if (node.Scores is not { } weights) continue;
                for (var offset = 0; offset < weights.Length; offset++)
                    scores[start + offset] = Math.Max(scores[start + offset], weights[offset]);
            }
        }

        var result = new List<int>();
        for (var point = LeftMinimum; point <= word.Length - RightMinimum; point++)
            if ((scores[point + 1] & 1) != 0) result.Add(point);
        return result.ToArray();
    }

    /// <summary>
    /// Extended hyphenation breakpoints for real prose tokens: supports
    /// accented Latin letters (e.g. "coöperation"), trailing hyphen prefixes
    /// ("extra-"), and possessive/contraction stems ("government’s") while
    /// keeping the stem boundary safe.
    /// </summary>
    public static int[] GetBreakpointsForToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length < LeftMinimum + RightMinimum || token.Length > MaximumWordLength)
        {
            return [];
        }

        if (token.All(char.IsAsciiLetter))
        {
            return GetBreakpoints(token);
        }

        // Trim a trailing hyphen emitted by Itemize for compound prefixes
        // ("extra-" -> stem "extra"), or an English possessive/contraction
        // suffix ("government's" -> stem "government", "wouldn’t" -> "wouldn").
        var stemEnd = token.Length;
        var hasTrailingSuffix = false;
        if (token[^1] is '-' or '–' or '—')
        {
            stemEnd = token.Length - 1;
            hasTrailingSuffix = true;
        }
        else
        {
            var apostrophe = token.LastIndexOfAny(['\'', '’']);
            if (apostrophe > 0 && apostrophe < token.Length - 1)
            {
                var suffixLen = token.Length - apostrophe - 1;
                if (suffixLen <= 2 && token.AsSpan(apostrophe + 1).ToString().All(char.IsAsciiLetter))
                {
                    stemEnd = apostrophe;
                    hasTrailingSuffix = true;
                }
            }
        }

        if (stemEnd < LeftMinimum + RightMinimum)
        {
            return [];
        }

        // Map each text element in the stem to an ASCII letter when possible
        // (stripping combining diacritics via FormD) so "coöperation" or
        // "résumé" can match the English pattern trie and map back to exact
        // UTF-16 offsets.
        var asciiLetters = new StringBuilder(stemEnd);
        var indexMap = new List<int>(stemEnd + 1);
        var allUpper = true;
        var offset = 0;
        while (offset < stemEnd)
        {
            var element = StringInfo.GetNextTextElement(token, offset);
            var mapped = MapToAsciiLetter(element);
            if (mapped == '\0')
            {
                return [];
            }

            if (!char.IsAsciiLetterUpper(mapped))
            {
                allUpper = false;
            }

            indexMap.Add(offset);
            asciiLetters.Append(mapped);
            offset += element.Length;
        }

        indexMap.Add(stemEnd);
        if (allUpper || asciiLetters.Length < LeftMinimum + RightMinimum)
        {
            return [];
        }

        var stemPoints = GetBreakpoints(asciiLetters.ToString());
        if (stemPoints.Length == 0)
        {
            return [];
        }

        // When a trailing hyphen or apostrophe suffix follows the stem, keep
        // at least 2 stem letters after the break so a line never starts with
        // only "s" or "-".
        var maxLetterIndex = hasTrailingSuffix
            ? asciiLetters.Length - 2
            : asciiLetters.Length - RightMinimum;
        var mappedPoints = new List<int>(stemPoints.Length);
        foreach (var point in stemPoints)
        {
            if (point >= LeftMinimum && point <= maxLetterIndex && point < indexMap.Count)
            {
                mappedPoints.Add(indexMap[point]);
            }
        }

        return mappedPoints.ToArray();
    }

    private static char MapToAsciiLetter(string element)
    {
        if (element.Length == 1 && char.IsAsciiLetter(element[0]))
        {
            return element[0];
        }

        var decomposed = element.Normalize(NormalizationForm.FormD);
        char letter = '\0';
        foreach (var c in decomposed)
        {
            if (char.IsAsciiLetter(c))
            {
                if (letter != '\0') return '\0';
                letter = c;
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                return '\0';
            }
        }

        return letter;
    }

    private static PatternData LoadData()
    {
        var root = new PatternNode();
        using (var patterns = OpenResource("patterns"))
        {
            while (patterns.ReadLine() is { } pattern)
            {
                if (pattern.Length == 0) continue;
                var letters = new StringBuilder();
                var scores = new List<byte> { 0 };
                foreach (var character in pattern)
                {
                    if (char.IsAsciiDigit(character)) scores[^1] = (byte)(character - '0');
                    else
                    {
                        letters.Append(character);
                        scores.Add(0);
                    }
                }

                var node = root;
                foreach (var character in letters.ToString())
                {
                    node.Children ??= new Dictionary<char, PatternNode>();
                    if (!node.Children.TryGetValue(character, out var child))
                        node.Children[character] = child = new PatternNode();
                    node = child;
                }
                node.Scores ??= new byte[scores.Count];
                for (var index = 0; index < scores.Count; index++)
                    node.Scores[index] = Math.Max(node.Scores[index], scores[index]);
            }
        }

        var exceptions = new Dictionary<string, int[]>(StringComparer.Ordinal);
        using (var source = OpenResource("exceptions"))
        {
            while (source.ReadLine() is { } spelling)
            {
                if (spelling.Length == 0) continue;
                var points = new List<int>();
                var offset = 0;
                foreach (var character in spelling)
                {
                    if (character == '-') points.Add(offset);
                    else offset++;
                }
                exceptions[spelling.Replace("-", "", StringComparison.Ordinal)] = points.ToArray();
            }
        }
        // The upstream metadata documents de-mo-c-rat as a known pattern bug.
        exceptions["democrat"] = [3, 4];
        return new PatternData(root, exceptions);
    }

    private static StreamReader OpenResource(string name) => new(
        typeof(EnglishHyphenator).Assembly.GetManifestResourceStream("Libzora.Hyphenation.en-us." + name)
        ?? throw new InvalidOperationException("The bundled English hyphenation data is missing."), Encoding.UTF8);

    private sealed class PatternNode
    {
        public Dictionary<char, PatternNode>? Children { get; set; }
        public byte[]? Scores { get; set; }
    }

    private sealed record PatternData(PatternNode Root, Dictionary<string, int[]> Exceptions);
}
