using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// One unbreakable laid-out unit: a shaped CJK character, a Latin word, a
/// space, or an atomic vertical run. Produced once per block and consumed by
/// both the horizontal and the vertical composer.
/// </summary>
internal sealed record LayoutCell
{
    public required ushort[] Glyphs { get; init; }
    /// <summary>Per-glyph pen positions relative to the cell origin.</summary>
    public required float[] GlyphX { get; init; }
    public required float[] GlyphY { get; init; }
    /// <summary>
    /// Per-glyph horizontal shift applied to squeeze punctuation. Painting uses
    /// the shifted <see cref="GlyphX"/>; hit-testing adds this back so the
    /// visible cell keeps its half-width rectangle.
    /// </summary>
    public float[]? GlyphInkShiftX { get; init; }
    public required float Advance { get; init; }
    /// <summary>Publisher tracking after a horizontal cell, omitted at a line's end.</summary>
    public float TrailingLetterSpacing { get; init; }
    public float Ascent { get; init; }
    public float Descent { get; init; }
    public required string FontPath { get; init; }
    public required float FontSize { get; init; }
    /// <summary>Global offset into ChapterContent.BodyText, or -1 for non-text cells.</summary>
    public required int TextStart { get; init; }
    public required int TextLength { get; init; }
    /// <summary>Original Unicode text represented by this cell, including combining marks.</summary>
    public string Text { get; init; } = string.Empty;
    public int[] Clusters { get; init; } = Array.Empty<int>();
    public required TypesetInlineStyle Style { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    public string? FootnoteText { get; init; }
    /// <summary>Small pronunciation text to paint above or beside this base cell.</summary>
    public string? RubyText { get; init; }
    /// <summary>Annotation shaped once at a size determined by the base font, not syllable length.</summary>
    public LayoutCell? RubyAnnotation { get; init; }
    /// <summary>Measured base width before reserving room for its annotation.</summary>
    public float RubyBaseAdvance { get; init; }
    public float RubyLeadingPadding { get; init; }
    public bool ContinuesRubyBase { get; init; }
    public bool IsNonBreakingSpace => Text is "\u00A0" or "\u202F";
    /// <summary>Latin letters are shaped as a word, never spread apart to justify a mixed line.</summary>
    public bool IsLatinWord { get; init; }
    public bool JustifyAsUnit => FootnoteMarker || RubyAnnotation is not null || ContinuesRubyBase || IsLatinWord;
    /// <summary>Resolved raster for an inline image; null for text cells.</summary>
    public string? ImagePath { get; init; }
    /// <summary>Physical width of the unrotated inline image.</summary>
    public float ImageWidth { get; init; }
    /// <summary>Physical height of the unrotated inline image.</summary>
    public float ImageHeight { get; init; }
    /// <summary>Vertical mode: paint the inline image as a sideways object.</summary>
    public bool ImageSideways { get; init; }
    public bool IsSpace { get; init; }
    /// <summary>English word split across a line; the trailing hyphen is display-only.</summary>
    public bool SyntheticHyphen { get; init; }
    public bool IsLineBreak { get; init; }
    /// <summary>Vertical mode: shaped horizontally, painted rotated 90° clockwise.</summary>
    public bool Sideways { get; init; }
    /// <summary>Vertical mode: combined digits centered inside one upright cell.</summary>
    public bool Combined { get; init; }
    /// <summary>
    /// Vertical mode: this punctuation has no usable vertical presentation
    /// glyph and must be painted as a quarter-turned horizontal glyph.
    /// </summary>
    public bool VerticalRotation { get; init; }
    /// <summary>
    /// A footnote reference is one compact superscript unit. Keeping the
    /// complete marker (for example, "[3]") in one cell prevents its brackets
    /// from being paginated and aligned as independent body characters.
    /// </summary>
    public bool FootnoteMarker { get; init; }
    /// <summary>The cell's single character when it is exactly one char, else null.</summary>
    public char? Character { get; init; }
    /// <summary>Superscript cells (footnote markers, sup) shift toward the line start edge.</summary>
    public bool Superscript => Style.Superscript;
    /// <summary>Subscript cells are scaled down and shifted below the baseline.</summary>
    public bool Subscript => Style.Subscript;
}

/// <summary>Shared context for the two composers.</summary>
internal sealed class ComposerContext
{
    public required TypesetLayoutOptions Options { get; init; }
    public required TypesetFontLibrary Fonts { get; init; }
    public required GlyphShaper Shaper { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public int? PageStartTextOffset { get; init; }
    public bool PageStartConsumed { get; set; }

    public bool StartsAnchoredPage(LayoutCell cell) => !PageStartConsumed
        && PageStartTextOffset is { } offset
        && cell.TextStart >= 0 && cell.TextLength > 0
        && cell.TextStart <= offset && offset < cell.TextStart + cell.TextLength;

    public string MainFont => Fonts.MainFontPath;

    public float LetterSpacing => Options.LetterSpacingEm * Options.BaseFontSize;
    public float RubyGap => Math.Max(1f, Options.BaseFontSize * 0.06f);

    public static bool IsHeadingNavigationTarget(ContentBlock block, IReadOnlySet<string>? navigationFragments)
    {
        if (navigationFragments is not null)
        {
            return (!string.IsNullOrWhiteSpace(block.ElementId) && navigationFragments.Contains(block.ElementId))
                || block.FragmentIds.Any(id =>
                    !block.FootnoteReferenceFragmentIds.Contains(id) && navigationFragments.Contains(id));
        }

        return !string.IsNullOrWhiteSpace(block.ElementId)
            || block.FragmentIds.Any(id => !block.FootnoteReferenceFragmentIds.Contains(id));
    }
}

/// <summary>
/// Accumulates placed content and closes pages. Both composers push their
/// absolute-geometry output here so text-offset bookkeeping lives in one place.
/// </summary>
internal sealed class PageBuilder
{
    private readonly TypesetLayoutOptions _options;
    private readonly List<LayoutPage> _pages = new();
    private LayoutPage _current;
    private bool _used;

    /// <summary>
    /// Stops composition once the just-closed page reaches this character
    /// offset. The reader uses it to paint the first screen of a very large
    /// chapter without waiting for the whole chapter.
    /// </summary>
    public int? StopAfterTextOffset { get; init; }

    public PageBuilder(TypesetLayoutOptions options)
    {
        _options = options;
        _current = MakePage();
    }

    public IReadOnlyList<LayoutPage> Pages => _pages;

    public LayoutPage Current => _current;

    public int CurrentPageIndex => _current.Index;

    /// <summary>True once visible content has been placed on the open page.</summary>
    public bool CurrentPageHasContent => _used;

    public Dictionary<string, int> Fragments { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, float> FragmentTops { get; } = new(StringComparer.Ordinal);

    public void RecordFragment(string? elementId, float? top = null)
    {
        if (!string.IsNullOrEmpty(elementId) && !Fragments.ContainsKey(elementId))
        {
            Fragments[elementId] = _current.Index;
            if (top is { } y) FragmentTops[elementId] = y;
        }
    }

    public void RecordFragments(IEnumerable<string> fragmentIds, float? top = null)
    {
        foreach (var fragmentId in fragmentIds)
        {
            RecordFragment(fragmentId, top);
        }
    }

    public void NextIfUsed()
    {
        if (_used)
        {
            Next();
        }
    }

    public void Next()
    {
        _pages.Add(_current);
        var truncated = StopAfterTextOffset is { } stop && _current.TextEndOffset >= stop;
        _current = MakePage();
        _used = false;
        if (truncated)
        {
            throw new CompositionTruncatedException();
        }
    }

    public void Finish(bool endOfChapter)
    {
        if (_used || _pages.Count == 0)
        {
            _pages.Add(_current);
            _used = false;
        }

        // Trailing empty spacer paragraphs (<p><br/></p>) at the end of a chapter can
        // spill onto an extra page with no visible glyphs or artwork; drop trailing
        // blank pages when earlier pages already carry content.
        while (_pages.Count > 1 && IsVisuallyBlank(_pages[^1]))
        {
            _pages.RemoveAt(_pages.Count - 1);
        }

        if (_pages.Count > 0)
        {
            var last = _pages[^1];
            _pages[^1] = new LayoutPage
            {
                Index = last.Index,
                WritingMode = last.WritingMode,
                Width = last.Width,
                Height = last.Height,
                InsetHorizontal = last.InsetHorizontal,
                InsetVertical = last.InsetVertical,
                TextStartOffset = last.TextStartOffset,
                TextEndOffset = last.TextEndOffset,
                EndOfChapter = endOfChapter,
                Runs = last.Runs,
                Images = last.Images,
                Tables = last.Tables,
                Decorations = last.Decorations,
                HotZones = last.HotZones,
                DebugBoxes = last.DebugBoxes,
            };
        }
    }

    public void AddRun(PlacedRun run)
    {
        _current.Runs.Add(run);
        _used = true;
        if (run.TextLength > 0 && run.TextStart >= 0)
        {
            TrackText(run.TextStart, run.TextStart + run.TextLength);
        }
    }

    public void AddImage(PlacedImage image)
    {
        _current.Images.Add(image);
        _used = true;
    }

    public void AddTable(PlacedTable table)
    {
        _current.Tables.Add(table);
        _used = true;
    }

    public void AddDecoration(PlacedRect rect)
    {
        _current.Decorations.Add(rect);
        _used = true;
    }

    private static bool IsVisuallyBlank(LayoutPage page) =>
        page.Images.Count == 0
        && page.Tables.Count == 0
        && page.Decorations.Count == 0
        && !page.Runs.Any(run => run.Glyphs.Length > 0);

    public void AddDebugBox(TypesetDebugBox box) => _current.DebugBoxes.Add(box);

    public void AddHotZone(PlacedHotZone zone)
    {
        // A link is shaped as several cells (for example, the three
        // characters in "[1]"). Keep one hit target for the visible run so
        // pointer release and footnote hover do not depend on which glyph was
        // hit. Do not merge across a line/column break: wrapped links still
        // need one independent target per visible fragment.
        if (_current.HotZones.LastOrDefault() is { } previous
            && string.Equals(previous.Href, zone.Href, StringComparison.Ordinal)
            && previous.Kind == zone.Kind
            && string.Equals(previous.FootnoteText, zone.FootnoteText, StringComparison.Ordinal)
            && string.Equals(previous.Marker, zone.Marker, StringComparison.Ordinal)
            && AreAdjacentOnFlowAxis(previous.Rect, zone.Rect))
        {
            _current.HotZones[^1] = new PlacedHotZone
            {
                Kind = previous.Kind,
                Rect = new SKRect(
                    Math.Min(previous.Rect.Left, zone.Rect.Left),
                    Math.Min(previous.Rect.Top, zone.Rect.Top),
                    Math.Max(previous.Rect.Right, zone.Rect.Right),
                    Math.Max(previous.Rect.Bottom, zone.Rect.Bottom)),
                Href = previous.Href,
                FootnoteText = previous.FootnoteText,
                Marker = previous.Marker,
            };
            return;
        }

        _current.HotZones.Add(zone);
    }

    private bool AreAdjacentOnFlowAxis(SKRect previous, SKRect current)
    {
        const float epsilon = 0.75f;
        if (_options.WritingMode == TypesetWritingMode.HorizontalTb)
        {
            return Math.Abs(previous.Top - current.Top) <= epsilon
                && Math.Abs(previous.Bottom - current.Bottom) <= epsilon
                && current.Left <= previous.Right + epsilon
                && previous.Left <= current.Right + epsilon;
        }

        return Math.Abs(previous.Left - current.Left) <= epsilon
            && Math.Abs(previous.Right - current.Right) <= epsilon
            && current.Top <= previous.Bottom + epsilon
            && previous.Top <= current.Bottom + epsilon;
    }

    public void MarkUsed() => _used = true;

    public void TrackText(int start, int end)
    {
        if (start < 0 || end <= start)
        {
            return;
        }

        if (_current.TextStartOffset < 0 || start < _current.TextStartOffset)
        {
            _current.TextStartOffset = start;
        }

        if (_current.TextEndOffset < 0 || end > _current.TextEndOffset)
        {
            _current.TextEndOffset = end;
        }
    }

    private LayoutPage MakePage() => new()
    {
        Index = _pages.Count,
        WritingMode = _options.WritingMode,
        Width = _options.ViewportWidth,
        Height = _options.ViewportHeight,
        InsetHorizontal = _options.InsetHorizontal,
        InsetVertical = _options.InsetVertical,
    };
}

/// <summary>Internal control-flow signal for an intentional partial layout.</summary>
internal sealed class CompositionTruncatedException : Exception
{
    public CompositionTruncatedException()
        : base("Composition stopped after the requested text offset.")
    {
    }
}
