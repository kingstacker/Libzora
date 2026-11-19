namespace Libzora.Layout;

public sealed class CssHints
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Center { get; init; }
    public bool AlignRight { get; init; }
    /// <summary>The publisher asked for ragged-right text (`text-align: left`).</summary>
    public bool NoJustify { get; init; }
    public bool NoWrap { get; init; }
    /// <summary>Publisher requested preformatted whitespace (`white-space: pre` / `pre-wrap`).</summary>
    public bool? PreserveWhitespace { get; init; }
    /// <summary>Publisher requested a monospace family (`font-family: monospace`).</summary>
    public bool? Monospace { get; init; }
    /// <summary>Publisher vertical-align; null leaves the element's default unchanged.</summary>
    public bool? Superscript { get; init; }
    public bool? Subscript { get; init; }
    /// <summary>Publisher display:none; null means unspecified.</summary>
    public bool? DisplayNone { get; init; }
    /// <summary>Publisher visibility:hidden/collapse; null means inherited.</summary>
    public bool? VisibilityHidden { get; init; }
    /// <summary>Distinguishes explicit visibility:inherit from no declaration.</summary>
    public bool VisibilitySpecified { get; init; }
    /// <summary>Whether either independent CSS property hides this element.</summary>
    public bool Hidden => DisplayNone == true || VisibilityHidden == true;
    /// <summary>text-decoration; null means unspecified.</summary>
    public bool? Underline { get; init; }
    public bool? Strikeout { get; init; }
    /// <summary>Publisher letter-spacing in em; NaN keeps the reader default.</summary>
    public float LetterSpacingEm { get; init; } = float.NaN;
    public TypesetTextCasing? Casing { get; init; }
    public bool? SmallCaps { get; init; }
    /// <summary>Resolved list-style-type token, or null when unspecified.</summary>
    public string? ListStyleType { get; init; }
    /// <summary>-1 means unspecified, 0 means none, positive values limit digits; int.MaxValue means all.</summary>
    public int VerticalTextCombineLimit { get; init; } = -1;
    public TypesetVerticalOrientation? VerticalTextOrientation { get; init; }
    public float TextIndentEm { get; init; } = float.NaN;
    public float FontSizeFactor { get; init; } = float.NaN;
    /// <summary>Percentage width as a 0..1 factor, primarily for images.</summary>
    public float ImageWidthFactor { get; init; } = float.NaN;
    /// <summary>Font-relative image height in em units, primarily for inline glyph images.</summary>
    public float ImageHeightEm { get; init; } = float.NaN;
    /// <summary>Publisher `line-height` as a multiple of the font size; NaN means unspecified.</summary>
    public float LineHeightFactor { get; init; } = float.NaN;
    public float MarginTopEm { get; init; } = float.NaN;
    public float MarginBottomEm { get; init; } = float.NaN;
    public float MarginLeftEm { get; init; } = float.NaN;
    public float MarginRightEm { get; init; } = float.NaN;
    public float PaddingLeftEm { get; init; } = float.NaN;
    public float PaddingRightEm { get; init; } = float.NaN;
    /// <summary>Visible border widths in em; NaN means no border was declared.</summary>
    public float BorderTopEm { get; init; } = float.NaN;
    public float BorderRightEm { get; init; } = float.NaN;
    public float BorderBottomEm { get; init; } = float.NaN;
    public float BorderLeftEm { get; init; } = float.NaN;

    public static readonly CssHints None = new();
}

/// <summary>
/// A deliberately small CSS reader: no cascade, no inheritance, no
/// combinators. Single compound selectors (tag, .class, #id) and one-level
/// class descendants are kept, and only the declarations the engine's v1
/// subset understands. Later rules override earlier ones per property, which
/// mirrors publisher stylesheets closely enough for emphasis, centering and
/// nowrap while staying deterministic.
/// </summary>
public sealed partial class MicroCss
{
    private readonly List<(string Selector, Dictionary<string, string> Declarations)> _rules = new();

    public static MicroCss Parse(params string[] cssTexts)
    {
        var css = new MicroCss();
        foreach (var text in cssTexts)
        {
            css.Append(text);
        }

        return css;
    }

    public void Append(string? cssText)
    {
        if (string.IsNullOrWhiteSpace(cssText))
        {
            return;
        }

        var body = StripComments(cssText);
        var ruleStart = 0;
        while (true)
        {
            var open = body.IndexOf('{', ruleStart);
            if (open < 0)
            {
                break;
            }

            var close = body.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            var selectorText = body[ruleStart..open].Trim();
            var declarationText = body[(open + 1)..close];
            ruleStart = close + 1;

            // Selector groups share one declaration block: `td, th { … }`
            // must apply to every simple selector, not just the first.
            var selectors = selectorText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsSimpleCompoundSelector)
                .ToArray();
            if (selectors.Length == 0)
            {
                continue;
            }

            var declarations = ParseDeclarations(declarationText);
            foreach (var selector in selectors)
            {
                if (declarations.Count > 0)
                {
                    _rules.Add((selector, declarations));
                }
            }
        }
    }

    public CssHints Resolve(string tag, IReadOnlyCollection<string> classes, string? id) =>
        Resolve(tag, classes, id, Array.Empty<string>());

    public CssHints Resolve(
        string tag,
        IReadOnlyCollection<string> classes,
        string? id,
        IReadOnlyCollection<string> ancestorClasses)
    {
        var bold = false;
        var italic = false;
        var center = false;
        var alignRight = false;
        var noJustify = false;
        var noWrap = false;
        bool? superscript = null;
        bool? subscript = null;
        bool? displayNone = null;
        bool? visibilityHidden = null;
        var visibilitySpecified = false;
        bool? underline = null;
        bool? strikeout = null;
        bool? preserveWhitespace = null;
        bool? monospace = null;
        var letterSpacing = float.NaN;
        TypesetTextCasing? casing = null;
        bool? smallCaps = null;
        string? listStyleType = null;
        var verticalTextCombineLimit = -1;
        TypesetVerticalOrientation? verticalTextOrientation = null;
        var indent = float.NaN;
        var sizeFactor = float.NaN;
        var imageWidthFactor = float.NaN;
        var imageHeightEm = float.NaN;
        var lineHeightFactor = float.NaN;
        var marginTop = float.NaN;
        var marginBottom = float.NaN;
        var marginLeft = float.NaN;
        var marginRight = float.NaN;
        var paddingLeft = float.NaN;
        var paddingRight = float.NaN;
        var borderTop = float.NaN;
        var borderRight = float.NaN;
        var borderBottom = float.NaN;
        var borderLeft = float.NaN;
        var matched = false;

        foreach (var (selector, declarations) in _rules)
        {
            if (!Matches(selector, tag, classes, id, ancestorClasses))
            {
                continue;
            }

            matched = true;
            foreach (var (name, value) in declarations)
            {
                switch (name)
                {
                    case "font-weight":
                        bold = value is "bold" or "bolder" || (int.TryParse(value, out var weight) && weight >= 600);
                        break;
                    case "font-style":
                        italic = value == "italic" || value == "oblique";
                        break;
                    case "text-align":
                        center = value == "center";
                        alignRight = value == "right";
                        noJustify = value is "left" or "start";
                        break;
                    case "white-space":
                        noWrap = value == "nowrap" || value == "pre";
                        if (value is "pre" or "pre-wrap" or "break-spaces")
                        {
                            preserveWhitespace = true;
                        }
                        else if (value is "normal" or "nowrap")
                        {
                            preserveWhitespace = false;
                        }
                        break;
                    case "font-family":
                        if (value.Contains("monospace", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("courier", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("menlo", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("consolas", StringComparison.OrdinalIgnoreCase))
                        {
                            monospace = true;
                        }
                        break;
                    case "vertical-align":
                        if (value is "super" or "sub" or "baseline")
                        {
                            superscript = value == "super";
                            subscript = value == "sub";
                        }
                        break;
                    case "display":
                        displayNone = value is "none";
                        break;
                    case "visibility":
                        if (value is "hidden" or "collapse" or "visible" or "initial" or "inherit" or "unset")
                        {
                            visibilitySpecified = true;
                            visibilityHidden = value switch
                            {
                                "hidden" or "collapse" => true,
                                "visible" or "initial" => false,
                                _ => null,
                            };
                        }
                        break;
                    case "text-decoration":
                    case "text-decoration-line":
                        if (value.Contains("none", StringComparison.Ordinal))
                        {
                            underline = false;
                            strikeout = false;
                        }
                        else
                        {
                            if (value.Contains("underline", StringComparison.Ordinal)) underline = true;
                            if (value.Contains("line-through", StringComparison.Ordinal)) strikeout = true;
                        }
                        break;
                    case "letter-spacing":
                        letterSpacing = value == "normal" ? 0f : ParseLengthEm(value);
                        break;
                    case "text-transform":
                        casing = value switch
                        {
                            "uppercase" => TypesetTextCasing.Uppercase,
                            "lowercase" => TypesetTextCasing.Lowercase,
                            "capitalize" => TypesetTextCasing.Capitalize,
                            "none" => TypesetTextCasing.None,
                            _ => casing,
                        };
                        break;
                    case "font-variant":
                    case "font-variant-caps":
                        if (value.Contains("small-caps", StringComparison.Ordinal)) smallCaps = true;
                        else if (value is "normal" or "none") smallCaps = false;
                        break;
                    case "list-style-type":
                        listStyleType = ParseListStyleType(value) ?? listStyleType;
                        break;
                    case "list-style":
                        listStyleType = ParseListStyleShorthand(value) ?? listStyleType;
                        break;
                    case "text-combine-upright":
                    case "-webkit-text-combine":
                        verticalTextCombineLimit = ParseTextCombineLimit(value);
                        break;
                    case "text-orientation":
                        verticalTextOrientation = ParseVerticalOrientation(value);
                        break;
                    case "text-indent":
                        indent = ParseEm(value);
                        break;
                    case "font-size":
                        sizeFactor = ParseEmOrPercent(value);
                        break;
                    case "width":
                        imageWidthFactor = ParsePercent(value);
                        break;
                    case "height":
                        var parsedHeight = ParseEm(value);
                        if (parsedHeight > 0f)
                        {
                            imageHeightEm = parsedHeight;
                        }
                        break;
                    case "line-height":
                        lineHeightFactor = ParseLineHeightFactor(value);
                        break;
                    case "margin-top":
                        marginTop = ParseLengthEm(value);
                        break;
                    case "margin-bottom":
                        marginBottom = ParseLengthEm(value);
                        break;
                    case "margin-left":
                        marginLeft = ParseLengthEm(value);
                        break;
                    case "margin-right":
                        marginRight = ParseLengthEm(value);
                        break;
                    case "padding-left":
                        paddingLeft = ParseLengthEm(value);
                        break;
                    case "padding-right":
                        paddingRight = ParseLengthEm(value);
                        break;
                    case "margin":
                    case "padding":
                        var box = ParseBoxShorthand(value);
                        if (name == "margin")
                        {
                            marginTop = box.Top;
                            marginRight = box.Right;
                            marginBottom = box.Bottom;
                            marginLeft = box.Left;
                        }
                        else
                        {
                            paddingLeft = box.Left;
                            paddingRight = box.Right;
                        }

                        break;
                    case "border":
                        var border = ParseBorderSide(value);
                        if (!float.IsNaN(border))
                        {
                            borderTop = border;
                            borderRight = border;
                            borderBottom = border;
                            borderLeft = border;
                        }

                        break;
                    case "border-top":
                    case "border-right":
                    case "border-bottom":
                    case "border-left":
                        var sideWidth = ParseBorderSide(value);
                        if (!float.IsNaN(sideWidth))
                        {
                            if (name == "border-top")
                            {
                                borderTop = sideWidth;
                            }
                            else if (name == "border-right")
                            {
                                borderRight = sideWidth;
                            }
                            else if (name == "border-bottom")
                            {
                                borderBottom = sideWidth;
                            }
                            else
                            {
                                borderLeft = sideWidth;
                            }
                        }

                        break;
                    case "border-width":
                        var borderBox = ParseBoxShorthand(value);
                        if (!float.IsNaN(borderBox.Top))
                        {
                            borderTop = ClampBorderWidth(borderBox.Top);
                            borderRight = ClampBorderWidth(borderBox.Right);
                            borderBottom = ClampBorderWidth(borderBox.Bottom);
                            borderLeft = ClampBorderWidth(borderBox.Left);
                        }

                        break;
                    case "border-top-width":
                        borderTop = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-right-width":
                        borderRight = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-bottom-width":
                        borderBottom = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-left-width":
                        borderLeft = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-style":
                        ApplyBorderStyle(value, ref borderTop, ref borderRight, ref borderBottom, ref borderLeft);
                        break;
                    case "border-top-style":
                        ApplyBorderStyleSide(value, ref borderTop);
                        break;
                    case "border-right-style":
                        ApplyBorderStyleSide(value, ref borderRight);
                        break;
                    case "border-bottom-style":
                        ApplyBorderStyleSide(value, ref borderBottom);
                        break;
                    case "border-left-style":
                        ApplyBorderStyleSide(value, ref borderLeft);
                        break;
                }
            }
        }

        return matched
            ? new CssHints
            {
                Bold = bold,
                Italic = italic,
                Center = center,
                AlignRight = alignRight,
                NoJustify = noJustify,
                NoWrap = noWrap,
                PreserveWhitespace = preserveWhitespace,
                Monospace = monospace,
                Superscript = superscript,
                Subscript = subscript,
                DisplayNone = displayNone,
                VisibilityHidden = visibilityHidden,
                VisibilitySpecified = visibilitySpecified,
                Underline = underline,
                Strikeout = strikeout,
                LetterSpacingEm = letterSpacing,
                Casing = casing,
                SmallCaps = smallCaps,
                ListStyleType = listStyleType,
                VerticalTextCombineLimit = verticalTextCombineLimit,
                VerticalTextOrientation = verticalTextOrientation,
                TextIndentEm = indent,
                FontSizeFactor = sizeFactor,
                ImageWidthFactor = imageWidthFactor,
                ImageHeightEm = imageHeightEm,
                LineHeightFactor = lineHeightFactor,
                MarginTopEm = marginTop,
                MarginBottomEm = marginBottom,
                MarginLeftEm = marginLeft,
                MarginRightEm = marginRight,
                PaddingLeftEm = paddingLeft,
                PaddingRightEm = paddingRight,
                BorderTopEm = borderTop,
                BorderRightEm = borderRight,
                BorderBottomEm = borderBottom,
                BorderLeftEm = borderLeft,
            }
            : CssHints.None;
    }
}
