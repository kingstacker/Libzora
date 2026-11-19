using System.Text;

namespace Libzora.Layout;

/// <summary>
/// Border widths and list-style parsing.
/// </summary>
public sealed partial class MicroCss
{
    private static float ClampBorderWidth(float width) =>
        float.IsNaN(width) ? float.NaN : Math.Clamp(width, 0f, 0.5f);

    /// <summary>
    /// Parses `border`/`border-top` shorthand such as `1px solid #000` or
    /// `none`. Returns the visible width in em, 0 for an explicit none, and
    /// NaN when the declaration cannot be understood.
    /// </summary>
    private static float ParseBorderSide(string value)
    {
        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return float.NaN;
        }

        var hasStyle = false;
        var hidden = false;
        var width = float.NaN;
        foreach (var part in parts)
        {
            var token = part.ToLowerInvariant();
            if (token is "none" or "hidden")
            {
                hidden = true;
            }
            else if (IsBorderStyle(token))
            {
                hasStyle = true;
            }
            else if (!IsBorderColor(token))
            {
                var parsed = ParseLengthEm(token);
                if (!float.IsNaN(parsed))
                {
                    width = parsed;
                }
            }
        }

        if (hidden)
        {
            return 0f;
        }

        if (!hasStyle && float.IsNaN(width))
        {
            // A bare colour or `initial`/`inherit`: CSS keeps style none.
            return IsBorderColor(value.Trim().ToLowerInvariant()) ? 0f : float.NaN;
        }

        return ClampBorderWidth(float.IsNaN(width) ? 1f / 16f : width);
    }

    private static bool IsBorderStyle(string token) =>
        token is "solid" or "dashed" or "dotted" or "double"
            or "groove" or "ridge" or "inset" or "outset";

    private static bool IsBorderColor(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (token[0] == '#'
            || token.StartsWith("rgb", StringComparison.Ordinal)
            || token.StartsWith("hsl", StringComparison.Ordinal)
            || token.StartsWith("var(", StringComparison.Ordinal))
        {
            return true;
        }

        return token is "transparent" or "currentcolor" or "initial" or "inherit"
            or "unset" or "revert"
            or "black" or "white" or "gray" or "grey" or "red" or "green" or "blue"
            or "silver" or "maroon" or "navy" or "olive" or "purple" or "teal"
            or "yellow" or "aqua" or "fuchsia" or "lime" or "orange";
    }

    private static void ApplyBorderStyle(
        string value,
        ref float top,
        ref float right,
        ref float bottom,
        ref float left)
    {
        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            return;
        }

        static bool Visible(string token) => !(token is "none" or "hidden");

        var topVisible = Visible(parts[0]);
        var rightVisible = Visible(parts.Length > 1 ? parts[1] : parts[0]);
        var bottomVisible = Visible(parts.Length > 2 ? parts[2] : parts[0]);
        var leftVisible = Visible(parts.Length > 3 ? parts[3] : parts.Length > 1 ? parts[1] : parts[0]);

        top = StyleWidth(top, topVisible);
        right = StyleWidth(right, rightVisible);
        bottom = StyleWidth(bottom, bottomVisible);
        left = StyleWidth(left, leftVisible);
    }

    private static void ApplyBorderStyleSide(string value, ref float width)
    {
        var token = value.Trim().ToLowerInvariant();
        width = StyleWidth(width, token is not ("none" or "hidden"));
    }

    private static float StyleWidth(float current, bool visible)
    {
        if (!visible)
        {
            return 0f;
        }

        return float.IsNaN(current) || current <= 0f ? 1f / 16f : current;
    }

    private static string? ParseListStyleType(string value)
    {
        var token = value.Trim().ToLowerInvariant();
        return token is "none" or "disc" or "circle" or "square" or "decimal"
            or "decimal-leading-zero" or "lower-alpha" or "upper-alpha"
            or "lower-latin" or "upper-latin" or "lower-roman" or "upper-roman"
            or "cjk-ideographic" or "cjk-decimal" or "chinese-counting"
            ? token : null;
    }

    private static string? ParseListStyleShorthand(string value)
    {
        // `list-style: inside none` and `list-style: decimal outside` are the
        // common forms. The position token is irrelevant to the reflow engine.
        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var style = ParseListStyleType(token);
            if (style is not null) return style;
        }
        return null;
    }

    private static int ParseTextCombineLimit(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0] == "none")
        {
            return 0;
        }

        if (parts[0] == "all")
        {
            return int.MaxValue;
        }

        if (parts[0] == "digits")
        {
            return parts.Length > 1 && int.TryParse(parts[1], out var limit)
                ? Math.Clamp(limit, 2, 8)
                : 2;
        }

        return -1;
    }
}
