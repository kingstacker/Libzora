namespace Libzora.Layout;

/// <summary>
/// Inline style and selector parsing.
/// </summary>
public sealed partial class MicroCss
{
    private static TypesetVerticalOrientation? ParseVerticalOrientation(string value) =>
        value switch
        {
            "upright" => TypesetVerticalOrientation.Upright,
            "sideways" => TypesetVerticalOrientation.Sideways,
            "mixed" => TypesetVerticalOrientation.Mixed,
            _ => null,
        };

    /// <summary>Parses one inline style="" attribute into hints.</summary>
    public static CssHints ParseInlineStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return CssHints.None;
        }

        var css = new MicroCss();
        var declarations = ParseDeclarations(style);
        if (declarations.Count == 0)
        {
            return CssHints.None;
        }

        css._rules.Add(("*", declarations));
        return css.Resolve("*", Array.Empty<string>(), null);
    }

    private static bool Matches(
        string selector,
        string tag,
        IReadOnlyCollection<string> classes,
        string? id,
        IReadOnlyCollection<string> ancestorClasses)
    {
        var parts = selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            // The current EPUB uses selectors such as ".yinhao_r img" for
            // decorative quote assets. The ancestor side is intentionally
            // class-only; unsupported structural selectors are rejected when
            // the stylesheet is parsed.
            if (parts[0].Length < 2 || parts[0][0] != '.'
                || !ancestorClasses.Contains(parts[0][1..]))
            {
                return false;
            }

            return MatchesCompound(parts[1], tag, classes, id);
        }

        return parts.Length == 1 && MatchesCompound(parts[0], tag, classes, id);
    }

    private static bool MatchesCompound(
        string selector,
        string tag,
        IReadOnlyCollection<string> classes,
        string? id)
    {
        foreach (var part in SplitCompound(selector))
        {
            switch (part[0])
            {
                case '.':
                    if (!classes.Contains(part[1..]))
                    {
                        return false;
                    }

                    break;
                case '#':
                    if (!string.Equals(id, part[1..], StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!string.Equals(part, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static IEnumerable<string> SplitCompound(string selector)
    {
        // "p.calibre5" -> ["p", ".calibre5"]; ".c" -> [".c"]; "b#id.x" -> ["b", "#id", ".x"]
        var i = 0;
        while (i < selector.Length)
        {
            var start = i;
            if (selector[i] == '.' || selector[i] == '#')
            {
                i++;
            }

            while (i < selector.Length && selector[i] != '.' && selector[i] != '#')
            {
                i++;
            }

            yield return selector[start..i];
        }
    }

    private static bool IsSimpleCompoundSelector(string selector)
    {
        var parts = selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && parts[0].StartsWith('.')
            && IsCompoundSelector(parts[1]))
        {
            return true;
        }

        return parts.Length == 1 && IsCompoundSelector(parts[0]);
    }

    private static bool IsCompoundSelector(string selector) =>
        selector.Length > 0
        && !selector.Contains('>')
        && !selector.Contains('+')
        && !selector.Contains('~')
        && !selector.Contains('[')
        && !selector.Contains(':')
        && !selector.Contains('*');

    private static Dictionary<string, string> ParseDeclarations(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim();
            // `!important` is not part of the value. With no cascade beyond
            // document order here, the flag rarely changes the winner, but
            // leaving it attached breaks keyword comparisons such as
            // `font-weight: bold !important`.
            var bang = value.LastIndexOf('!');
            if (bang >= 0
                && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..bang].TrimEnd();
            }

            if (name.Length > 0 && value.Length > 0)
            {
                // Later declarations in a rule win, as in CSS (`display:
                // -webkit-box; display: flex`).
                result[name] = value;
            }
        }

        return result;
    }
}
