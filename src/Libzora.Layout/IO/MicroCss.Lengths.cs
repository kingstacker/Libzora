using System.Text;

namespace Libzora.Layout;

/// <summary>
/// Length, percentage and line-height parsing.
/// </summary>
public sealed partial class MicroCss
{
    private static float ParseEm(string value)
    {
        value = value.Trim();
        if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(value[..^2], System.Globalization.CultureInfo.InvariantCulture, out var em))
        {
            return em;
        }

        return 0f;
    }

    private static float ParseEmOrPercent(string value)
    {
        value = value.Trim();
        if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(value[..^2], System.Globalization.CultureInfo.InvariantCulture, out var em))
        {
            return em;
        }

        if (value.EndsWith('%')
            && float.TryParse(value[..^1], System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f;
        }

        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 16f);
        }

        if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 12f);
        }

        return float.NaN;
    }

    /// <summary>
    /// Parses a CSS length into em units relative to the body font (1em =
    /// 16px = 12pt). Unsupported units, `auto` and percentages (whose
    /// containing block is not known here) become NaN.
    /// </summary>
    private static float ParseLengthEm(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return float.NaN;
        }

        if (value == "0" || value == "0.0")
        {
            return 0f;
        }

        if (value.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^3], 1f);
        }

        if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f);
        }

        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 16f);
        }

        if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 12f);
        }

        return float.NaN;
    }

    private static float ParseNumber(string text, float scale)
    {
        return float.TryParse(
            text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number)
            ? number * scale
            : float.NaN;
    }

    private static float ParseLineHeightFactor(string value)
    {
        value = value.Trim();
        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return float.NaN;
        }

        float factor;
        if (value.EndsWith('%'))
        {
            factor = ParseNumber(value[..^1], 0.01f);
        }
        else if (value.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^3], 1f);
        }
        else if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^2], 1f);
        }
        else if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^2], 1f / 16f);
        }
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^2], 1f / 12f);
        }
        else
        {
            factor = ParseNumber(value, 1f);
        }

        // Publishers occasionally emit pathological values (0, 40) meant for
        // another medium. Keep a readable band and treat the rest as absent.
        return factor is >= 1f and <= 4f ? factor : float.NaN;
    }

    private readonly record struct BoxEdges(float Top, float Right, float Bottom, float Left)
    {
        public static BoxEdges Unspecified { get; } =
            new(float.NaN, float.NaN, float.NaN, float.NaN);
    }

    private static BoxEdges ParseBoxShorthand(string value)
    {
        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            return BoxEdges.Unspecified;
        }

        var lengths = new float[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            lengths[index] = ParseLengthEm(parts[index]);
            if (float.IsNaN(lengths[index]))
            {
                return BoxEdges.Unspecified;
            }
        }

        return lengths.Length switch
        {
            1 => new BoxEdges(lengths[0], lengths[0], lengths[0], lengths[0]),
            2 => new BoxEdges(lengths[0], lengths[1], lengths[0], lengths[1]),
            3 => new BoxEdges(lengths[0], lengths[1], lengths[2], lengths[1]),
            _ => new BoxEdges(lengths[0], lengths[1], lengths[2], lengths[3]),
        };
    }

    private static float ParsePercent(string value)
    {
        value = value.Trim();
        if (value.EndsWith('%')
            && float.TryParse(value[..^1], System.Globalization.CultureInfo.InvariantCulture, out var percent)
            && percent > 0f)
        {
            return percent / 100f;
        }

        return float.NaN;
    }

    private static string StripComments(string css)
    {
        var builder = new StringBuilder(css.Length);
        var index = 0;
        while (index < css.Length)
        {
            var open = css.IndexOf("/*", index, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(css[index..]);
                break;
            }

            builder.Append(css[index..open]);
            var close = css.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            index = close + 2;
        }

        return builder.ToString();
    }
}
