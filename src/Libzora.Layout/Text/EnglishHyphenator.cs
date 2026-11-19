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
