using System.Globalization;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Shapes inline items into layout cells. Font fallback: when the main font
/// produces a .notdef glyph for a unit, the unit is reshaped with the first
/// registered fallback that covers it.
/// </summary>
internal sealed partial class CellFactory
{
    private readonly ComposerContext _context;
    private readonly Dictionary<(string FontPath, float Size), (float Ascent, float Descent)> _metricsCache = new();
    private readonly Dictionary<(string FontPath, float Size, ushort Glyph), float> _apostropheAdvanceCache = new();
    // A compose has fixed fonts and spacing. Reuse immutable glyph arrays for
    // repeated characters/words, while assigning source offsets per occurrence.
    private readonly Dictionary<(string Text, float Size, float Spacing, bool Latin, bool PublisherTracking, string PreferredFont, bool PreserveWs), LayoutCell> _cellCache = new();

    public CellFactory(ComposerContext context)
    {
        _context = context;
    }

    public float BodyLineHeight => _context.Options.BodyLineHeight;

    /// <summary>Shapes one already-extracted unit string as an unbreakable cell.</summary>
    public LayoutCell ShapeCell(
        string unitText,
        int globalTextStart,
        TypesetInlineStyle style,
        string? linkHref,
        string? footnoteHref,
        bool vertical,
        string? footnoteText = null,
        bool footnoteMarker = false,
        float? fontSizeOverride = null,
        bool preferLatinFont = false)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        // Vertical text lives on a fixed 1-em column grid; a publisher font
        // size would spill into neighbouring columns, so it only scales
        // horizontal runs.
        // `default(TypesetInlineStyle)` predates this field and has zero here;
        // treat non-positive values as the normal size.
        var styleScale = style.FontSizeFactor > 0f ? style.FontSizeFactor : 1f;
        var cssAlreadyScaledDown = !vertical && style.FontSizeFactor > 0f && style.FontSizeFactor < 0.99f;
        var fontSize = fontSizeOverride
            ?? _context.Options.BaseFontSize
                * (vertical ? 1f : Math.Clamp(styleScale, 0.5f, 3f));
        if (fontSizeOverride is null && style.Superscript)
        {
            // Footnote references use a compact marker size. Other <sup>
            // content keeps the slightly larger superscript scale so ordinary
            // mathematical/text superscripts do not change unexpectedly, unless
            // publisher CSS already scaled the element below 1em.
            if (footnoteMarker && !vertical)
            {
                fontSize = Math.Min(fontSize, _context.Options.BaseFontSize * 0.55f);
            }
            else if (!cssAlreadyScaledDown)
            {
                fontSize *= footnoteMarker ? 0.70f : 0.75f;
            }
        }
        else if (fontSizeOverride is null && style.Subscript && !cssAlreadyScaledDown)
        {
            fontSize *= 0.75f;
        }

        var naturalLatin = unitText.Any(TypesetText.IsLatinLetter) && !unitText.Any(TypesetText.IsCjk)
            && !vertical && _context.Options.NaturalLatinTypography
            && _context.Options.WritingMode == TypesetWritingMode.HorizontalTb;
        var letterSpacing = !float.IsNaN(style.LetterSpacingEm)
            ? style.LetterSpacingEm * fontSize
            : naturalLatin || style.Monospace
                ? 0f
                : _context.LetterSpacing * (fontSize / _context.Options.BaseFontSize);

        var preferredFont = ResolvePreferredFont(unitText, style, vertical, preferLatinFont);
        if (fontSizeOverride is null
            && !vertical
            && !style.Monospace
            && _context.Fonts.LatinFontPath is { } pairedLatin
            && preferredFont.StartsWith(pairedLatin, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pairedLatin, _context.MainFont, StringComparison.OrdinalIgnoreCase))
        {
            fontSize *= _context.Fonts.GetLatinOpticalScale(pairedLatin);
        }
        var publisherTracking = !vertical && !float.IsNaN(style.LetterSpacingEm);
        var cacheable = unitText.Length <= 64;
        var key = (unitText, fontSize, letterSpacing, naturalLatin, publisherTracking, preferredFont, style.PreserveWhitespace);
        if (cacheable && _cellCache.TryGetValue(key, out var cached))
            return cached with
            {
                TextStart = globalTextStart, Style = style, LinkHref = linkHref,
                FootnoteHref = footnoteHref, FootnoteText = footnoteText, FootnoteMarker = footnoteMarker
            };

        // HTML collapses ordinary line breaks and runs of whitespace. Keep
        // the original UTF-16 length for offset mapping, but shape whitespace
        // as one regular space so a newline never turns into a .notdef box.
        var shapeText = !style.PreserveWhitespace && unitText.All(TypesetText.IsSpace)
            ? " "
            : unitText == "\t"
                ? "    "
                : unitText;
        _ = _context.Shaper.TryShapeWithFallback(
            shapeText,
            fontSize,
            vertical: false,
            preferredFontPath: preferredFont,
            out var fontPath,
            out var shaped);

        var (ascent, descent) = GetMetrics(fontPath, fontSize);
        var latinWord = unitText.Any(TypesetText.IsLatinLetter) && !unitText.Any(TypesetText.IsCjk);
        // naturalLatin and letterSpacing were resolved before the cache lookup.

        var glyphCount = shaped.GlyphIds.Length;
        var glyphX = new float[glyphCount];
        var glyphY = new float[glyphCount];
        float pen = 0f;
        for (var i = 0; i < glyphCount; i++)
        {
            glyphX[i] = pen + shaped.OffsetsX[i];
            glyphY[i] = shaped.OffsetsY[i];
            var glyphAdvance = shaped.Advances[i];
            // CJK faces can give a word's curly apostrophe a full-em box.
            // Keep the original glyph and cluster; remove only its trailing
            // blank, leaving already-proportional fonts and vertical text alone.
            var cluster = shaped.Clusters[i];
            if (naturalLatin && glyphAdvance >= fontSize * 0.8f
                && (uint)cluster < (uint)unitText.Length && unitText[cluster] == '’')
            {
                glyphAdvance = Math.Min(glyphAdvance,
                    GetApostropheAdvance(fontPath, fontSize, shaped.GlyphIds[i]) + Math.Max(0f, shaped.OffsetsX[i]));
            }
            pen += glyphAdvance + letterSpacing;
        }

        // Keep internal tracking in the measured ink width. Publisher tracking
        // after the cell must survive CJK's single-glyph itemization; the line
        // composer removes that final gap only at an actual line boundary.
        var advance = glyphCount > 0 ? pen - letterSpacing : 0f;
        var trailingSpacing = publisherTracking && glyphCount > 0 ? letterSpacing : 0f;

        // Most CJK faces (including the bundled Song face) ship no `palt` or
        // `halt`, so full-width punctuation keeps its empty half-cell. Squeeze
        // closing marks by their trailing blank and opening marks by their
        // leading blank, the same half-width result those features produce.
        // Vertical cells keep their full pitch: the empty half is part of the
        // column rhythm there.
        float[]? glyphInkShiftX = null;
        if (!vertical
            && fontSizeOverride is null
            && glyphCount == 1
            && Math.Abs(advance - fontSize) <= fontSize * 0.03f)
        {
            var squeeze = fontSize * 0.5f;
            if (TypesetText.IsCompressibleClosingPunctuation(unitText))
            {
                advance -= squeeze;
            }
            else if (TypesetText.IsCompressibleOpeningPunctuation(unitText))
            {
                glyphInkShiftX = new float[glyphCount];
                for (var glyphIndex = 0; glyphIndex < glyphX.Length; glyphIndex++)
                {
                    glyphX[glyphIndex] -= squeeze;
                    glyphInkShiftX[glyphIndex] = squeeze;
                }

                advance -= squeeze;
            }
        }

        var result = new LayoutCell
        {
            Glyphs = shaped.GlyphIds,
            GlyphX = glyphX,
            GlyphY = glyphY,
            Advance = advance + trailingSpacing,
            TrailingLetterSpacing = trailingSpacing,
            Ascent = ascent,
            Descent = descent,
            FontPath = fontPath,
            FontSize = fontSize,
            TextStart = globalTextStart,
            TextLength = unitText.Length,
            Text = unitText,
            IsLatinWord = latinWord,
            Clusters = shaped.Clusters,
            GlyphInkShiftX = glyphInkShiftX,
            Style = style,
            LinkHref = linkHref,
            FootnoteHref = footnoteHref,
            FootnoteText = footnoteText,
            FootnoteMarker = footnoteMarker,
            Character = unitText.Length == 1 && !TypesetText.IsSpace(unitText[0]) ? unitText[0] : null,
        };
        if (cacheable && _cellCache.Count < 4096) _cellCache.TryAdd(key, result);
        return result;
    }

    /// <summary>
    /// Shapes one breakable horizontal text unit into one or more cells. When
    /// no single face covers every character, the unit is split at grapheme
    /// boundaries so each element can use the face that covers it. Without
    /// this, a Latin word containing an emoji or a rare symbol would fall back
    /// as a whole and render the entire word as .notdef boxes.
    /// </summary>
    private string ResolvePreferredFont(
        string unitText,
        TypesetInlineStyle style,
        bool vertical,
        bool preferLatinFont)
    {
        var baseFont = _context.MainFont;
        if (style.Monospace && _context.Fonts.EnsureMonospaceFont() is { } mono)
        {
            baseFont = mono;
        }
        else if (!vertical
            && _context.Fonts.LatinFontPath is { } latinFont
            && ShouldUseLatinFont(unitText, preferLatinFont))
        {
            baseFont = latinFont;
        }

        if (style.Bold || style.Italic)
        {
            return _context.Fonts.ResolveStyledFont(baseFont, style.Bold, style.Italic);
        }

        return baseFont;
    }

    private static bool ShouldUseLatinFont(string unitText, bool inLatinProse)
    {
        if (unitText.Length == 0 || unitText.Any(TypesetText.IsCjk))
        {
            return false;
        }

        if (inLatinProse)
        {
            return true;
        }

        // In CJK/mixed prose, route Latin words, digits and pure ASCII
        // punctuation to the paired Latin face while leaving CJK curly quotes
        // and fullwidth punctuation on the primary CJK face.
        foreach (var c in unitText)
        {
            if (TypesetText.IsLatinLetter(c) || char.IsAsciiDigit(c) || (c is >= '!' and <= '~'))
            {
                return true;
            }
        }

        return false;
    }

    public List<LayoutCell> ShapeTextUnit(
        string unitText,
        int globalTextStart,
        TypesetInlineStyle style,
        string? linkHref,
        string? footnoteHref,
        bool preferLatinFont = false,
        string? footnoteText = null)
    {
        var whole = ShapeCell(
            unitText, globalTextStart, style, linkHref, footnoteHref, vertical: false, footnoteText: footnoteText, preferLatinFont: preferLatinFont);
        if (!whole.Glyphs.Contains((ushort)0))
        {
            return [whole];
        }

        var starts = StringInfo.ParseCombiningCharacters(unitText);
        if (starts.Length <= 1)
        {
            return [whole];
        }

        var cells = new List<LayoutCell>(starts.Length);
        for (var index = 0; index < starts.Length; index++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var offset = starts[index];
            var length = (index + 1 < starts.Length ? starts[index + 1] : unitText.Length) - offset;
            cells.Add(ShapeCell(
                unitText.Substring(offset, length),
                globalTextStart + offset,
                style,
                linkHref,
                footnoteHref,
                vertical: false,
                footnoteText: footnoteText,
                preferLatinFont: preferLatinFont));
        }

        return cells;
    }

    private LayoutCell ShapeRubyCell(LayoutCell baseCell)
    {
        var rubyText = baseCell.RubyText;
        if (string.IsNullOrWhiteSpace(rubyText))
        {
            throw new ArgumentException("The base cell does not contain ruby text.", nameof(baseCell));
        }

        var style = baseCell.Style with
        {
            Bold = false,
            Italic = false,
            Underline = false,
            Strikeout = false,
            Superscript = false,
            NoWrap = true,
        };
        // Keep equal-size bases' annotations at the same readable font size.
        // Longer syllables need more layout space, not a smaller font.
        var fontSize = Math.Clamp(baseCell.FontSize * 0.60f, 8f, 14f);
        var annotation = ShapeCell(
            rubyText,
            globalTextStart: -1,
            style,
            linkHref: null,
            footnoteHref: null,
            vertical: false,
            fontSizeOverride: fontSize);

        // Font-wide ascent/descent do not always contain hinted accents or
        // combining marks. Reserve the actual positioned glyph ink as well,
        // using the same Skia font settings as the painter on each platform.
        using var font = new SKFont(_context.Fonts.GetTypeface(annotation.FontPath), fontSize);
        _ = font.GetGlyphWidths(annotation.Glyphs.AsSpan(), out var bounds, null);
        var left = 0f;
        var right = annotation.Advance - annotation.TrailingLetterSpacing;
        var ascent = annotation.Ascent;
        var descent = annotation.Descent;
        for (var index = 0; index < bounds.Length; index++)
        {
            if (bounds[index].IsEmpty) continue;
            left = Math.Min(left, annotation.GlyphX[index] + bounds[index].Left);
            right = Math.Max(right, annotation.GlyphX[index] + bounds[index].Right);
            ascent = Math.Max(ascent, -annotation.GlyphY[index] - bounds[index].Top);
            descent = Math.Max(descent, annotation.GlyphY[index] + bounds[index].Bottom);
        }

        return annotation with
        {
            GlyphX = annotation.GlyphX.Select(x => x - left).ToArray(),
            Advance = right - left,
            TrailingLetterSpacing = 0f,
            Ascent = ascent,
            Descent = descent,
        };
    }
}
