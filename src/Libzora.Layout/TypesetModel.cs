using SkiaSharp;

namespace Libzora.Layout;

public enum TypesetWritingMode
{
    HorizontalTb,
    VerticalRl,
}

public enum BlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Blockquote,
    Image,
    Rule,
    Table,
}

public enum InlineKind
{
    Text,
    Ruby,
    LineBreak,
    Image,
    FootnoteMarker,
}

public enum TypesetVerticalOrientation
{
    Mixed,
    Upright,
    Sideways,
}

/// <summary>Publisher `text-transform` applied to the visible text only.</summary>
public enum TypesetTextCasing
{
    None,
    Uppercase,
    Lowercase,
    Capitalize,
}

public enum HotZoneKind
{
    Link,
    FootnoteMarker,
}

/// <summary>
/// Resolved inline emphasis. The engine has no cascade; the loader resolves
/// everything up front so layout and paint are pure functions of these values.
/// </summary>
public readonly record struct TypesetInlineStyle(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikeout = false,
    bool Superscript = false,
    bool NoWrap = false)
{
    /// <summary>
    /// Null means that the document did not specify text-combine-upright;
    /// the reader's publication default is two digits.
    /// </summary>
    public int? VerticalTextCombineLimit { get; init; }

    /// <summary>Null means mixed orientation, subject to the reader default.</summary>
    public TypesetVerticalOrientation? VerticalTextOrientation { get; init; }

    /// <summary>
    /// Publisher `font-size` as a multiple of the body size. One is the
    /// default; headings and `<span style="font-size:...">` scale from here.
    /// </summary>
    public float FontSizeFactor { get; init; } = 1f;

    /// <summary>Subscript content sits below the baseline and is scaled down.</summary>
    public bool Subscript { get; init; }

    /// <summary>Publisher letter-spacing in em; NaN keeps the reader default.</summary>
    public float LetterSpacingEm { get; init; } = float.NaN;

    /// <summary>Publisher text-transform; display only, the offset stream is unchanged.</summary>
    public TypesetTextCasing Casing { get; init; }

    /// <summary>Small-caps renders lowercase letters as smaller capitals.</summary>
    public bool SmallCaps { get; init; }

    /// <summary>Use a monospace typeface for code/preformatted spans.</summary>
    public bool Monospace { get; init; }

    /// <summary>Preserve spaces and explicit line breaks (preformatted blocks).</summary>
    public bool PreserveWhitespace { get; init; }

    /// <summary>True for a CSS <c>float: left</c> initial letter (drop cap).</summary>
    public bool FloatLeft { get; init; }
}

/// <summary>
/// One piece of inline content in document order. Non-text items still carry
/// <see cref="TextStart"/> == -1; text items carry the offset of their first
/// character in <see cref="ChapterContent.BodyText"/>, the same coordinate
/// space the annotations and whole-book search jumps persist.
/// </summary>
public sealed class InlineItem
{
    public InlineKind Kind { get; init; } = InlineKind.Text;
    public string Text { get; init; } = string.Empty;
    public int TextStart { get; init; } = -1;
    public TypesetInlineStyle Style { get; init; }
    public string? ImagePath { get; init; }
    /// <summary>Requested inline image height in em units, when the EPUB provides one.</summary>
    public float? ImageHeightEm { get; init; }
    /// <summary>Requested image width as a fraction of the content width.</summary>
    public float? ImageWidthFactor { get; init; }
    /// <summary>Decorative quote artwork uses a readable font-relative cap on wide pages.</summary>
    public bool DecorativeQuote { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    /// <summary>Inline note text for publishers that encode a footnote in an image alt attribute.</summary>
    public string? FootnoteText { get; init; }
    /// <summary>Original alt/title description carried by a standalone image block.</summary>
    public string? AltText { get; init; }
    /// <summary>The small pronunciation text rendered above or beside a ruby base.</summary>
    public string? RubyText { get; init; }
    /// <summary>Ghost text is part of the offset stream but never rendered.</summary>
    public bool Ghost { get; init; }
}

/// <summary>
/// A resolved block (paragraph, heading, image, ...). Block-level metrics are
/// expressed in body-line units so both writing modes share one grid.
/// </summary>
public sealed class ContentBlock
{
    public BlockKind Kind { get; init; } = BlockKind.Paragraph;
    public string? ElementId { get; init; }
    /// <summary>All fragment ids contained by this block, including inline anchors.</summary>
    public IReadOnlyList<string> FragmentIds { get; init; } = Array.Empty<string>();
    /// <summary>Reference anchors remain navigable but must not force a new page.</summary>
    public IReadOnlyList<string> FootnoteReferenceFragmentIds { get; init; } = Array.Empty<string>();
    public TypesetInlineStyle Style { get; init; }
    public bool Center { get; init; }
    public bool AlignRight { get; init; }
    public bool Justify { get; init; }
    public float TextIndentEm { get; init; }
    /// <summary>True when publisher CSS explicitly specified text-indent on this block.</summary>
    public bool ExplicitTextIndent { get; init; }
    public float SpaceBeforeLines { get; init; }
    public float SpaceAfterLines { get; init; }
    /// <summary>Visible marker generated from list-style-type, e.g. "1." or "•".</summary>
    public string? ListMarker { get; init; }
    /// <summary>One for a top-level list, increasing for nested lists.</summary>
    public int ListLevel { get; init; }
    /// <summary>
    /// Publisher `line-height` as a multiple of the body font size. Zero keeps
    /// the reader's own line height, so user settings still win for unstyled
    /// books.
    /// </summary>
    public float LineHeightFactor { get; init; }
    /// <summary>Publisher margins/padding, in em of the body font size.</summary>
    public float MarginTopEm { get; init; }
    public float MarginBottomEm { get; init; }
    public float MarginLeftEm { get; init; }
    public float MarginRightEm { get; init; }
    public float PaddingLeftEm { get; init; }
    public float PaddingRightEm { get; init; }
    /// <summary>Optional left border width in em (e.g. styled blockquotes).</summary>
    public float BorderLeftEm { get; init; }
    /// <summary>Heading level 1..6 for h1..h6 blocks, 0 otherwise.</summary>
    public int HeadingLevel { get; init; }
    /// <summary>Force a page break before this block (CSS page-break-before / break-before).</summary>
    public bool PageBreakBefore { get; init; }
    /// <summary>Force a page break after this block (CSS page-break-after / break-after).</summary>
    public bool PageBreakAfter { get; init; }
    /// <summary>Avoid splitting this block from the start of the following block (headings, figure images, break-after: avoid).</summary>
    public bool KeepWithNext { get; init; }
    /// <summary>True for figcaption / caption blocks paired with figures.</summary>
    public bool IsCaption { get; init; }
    /// <summary>
    /// Structured rows and cells. <see cref="Items"/> still carries the same
    /// text flattened, which vertical typesetting (and any host that does not
    /// understand tables) falls back to.
    /// </summary>
    public ContentTable? Table { get; init; }
    public List<InlineItem> Items { get; init; } = new();
}

public sealed class ContentTable
{
    public required IReadOnlyList<ContentTableRow> Rows { get; init; }
}

public sealed class ContentTableRow
{
    public required IReadOnlyList<ContentTableCell> Cells { get; init; }
    public bool Header { get; init; }
}

public sealed class ContentTableCell
{
    public required IReadOnlyList<InlineItem> Items { get; init; }
    public int ColSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;
    public bool Header { get; init; }
    /// <summary>Visible border widths in em; zero means no border on that side.</summary>
    public float BorderTopEm { get; init; }
    public float BorderRightEm { get; init; }
    public float BorderBottomEm { get; init; }
    public float BorderLeftEm { get; init; }

    public bool HasBorder =>
        BorderTopEm > 0f || BorderRightEm > 0f || BorderBottomEm > 0f || BorderLeftEm > 0f;
}

/// <summary>
/// A chapter reduced to typed blocks. <see cref="BodyText"/> is the verbatim
/// concatenation of every body text node, including footnote definitions, so
/// character offsets stay compatible with annotations that were captured
/// against the live document's textContent.
/// </summary>
public sealed class ChapterContent
{
    public required string ChapterPath { get; init; }
    public required string BodyText { get; init; }
    public required IReadOnlyList<ContentBlock> Blocks { get; init; }
    /// <summary>Pronunciation text to exclude from searches without changing source offsets.</summary>
    public IReadOnlyList<(int Start, int Length)> RubyAnnotationRanges { get; init; } = [];
    /// <summary>Element ids that must still answer fragment navigation.</summary>
    public IReadOnlySet<string> FragmentIds { get; init; } = new HashSet<string>();
    /// <summary>Text offsets at which visible fragment ids begin, when they have one.</summary>
    public IReadOnlyDictionary<string, int> FragmentTextOffsets { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed class TypesetLayoutOptions
{
    public TypesetWritingMode WritingMode { get; init; } = TypesetWritingMode.HorizontalTb;
    /// <summary>Base body font size in device-independent pixels (1rem).</summary>
    public float BaseFontSize { get; init; } = 17f;
    public float LineHeight { get; init; } = 1.8f;
    public float LetterSpacingEm { get; init; } = 0.012f;
    public bool ParagraphIndent { get; init; } = true;
    /// <summary>Extra spacing between body paragraphs, independent of line height.</summary>
    public float ParagraphSpacingEm { get; init; }
    /// <summary>Keep consecutive paragraph lines at the normal line pitch.</summary>
    public bool ParagraphSpacingMatchesLineHeight { get; init; }
    public bool OverridePublisherLineHeight { get; init; }
    public bool OverridePublisherParagraphSpacing { get; init; }
    /// <summary>Latin body prose uses a ragged right edge, natural tracking and a calmer line pitch.</summary>
    public bool NaturalLatinTypography { get; init; }
    /// <summary>
    /// Break long English words at syllable boundaries when the whole word
    /// does not fit, adding a display-only hyphen. Off by default so callers
    /// that need the raw word grid opt in explicitly.
    /// </summary>
    public bool HyphenateEnglish { get; init; }
    public bool AvoidWidowsAndOrphans { get; init; } = false;
    /// <summary>Left-align horizontal body prose instead of justifying both edges.</summary>
    public bool AlignLeft { get; init; }
    /// <summary>Insert a subtle visual gap between adjacent CJK and Latin/numeric runs.</summary>
    public bool CjkLatinSpacing { get; init; } = true;
    /// <summary>Allow eligible closing CJK punctuation to hang slightly into the trailing margin.</summary>
    public bool HangingPunctuation { get; init; } = true;
    /// <summary>Edge-to-edge comic/manga image layout.</summary>
    public bool MangaMode { get; init; }
    /// <summary>Trim outer white scan margins on comic/manga pages.</summary>
    public bool MangaAutoCrop { get; init; }
    /// <summary>Seamless vertical strip stacking (Webtoon) with zero inter-panel gap.</summary>
    public bool SeamlessImageScroll { get; init; }
    /// <summary>Continuous vertical scroll layout for horizontal prose without inter-page gaps or page breaks.</summary>
    public bool ContinuousScroll { get; init; }
    public float ViewportWidth { get; init; } = 800f;
    public float ViewportHeight { get; init; } = 600f;
    public float InsetHorizontal { get; init; } = 24f;
    public float InsetVertical { get; init; } = 24f;
    /// <summary>Optional bottom-only override; the top inset remains InsetVertical.</summary>
    public float? InsetBottom { get; init; }
    /// <summary>
    /// Collect per-cell diagnostic frames. Off by default: a vertical chapter
    /// otherwise allocates a debug box for every glyph even though the user
    /// never enabled the overlay.
    /// </summary>
    public bool CollectDebugBoxes { get; init; }
    /// <summary>
    /// Optional set of TOC navigation fragment ids for the current spine
    /// document. When supplied, only headings targeting one of these anchors
    /// (or carrying an explicit CSS page-break) force a new page; internal ids
    /// such as Kindle <c>aid-...</c> attributes stay inline as subheadings.
    /// </summary>
    public IReadOnlySet<string>? NavigationFragments { get; init; }

    public float BodyLineHeight => BaseFontSize * LineHeight;
    public float EffectiveInsetBottom => InsetBottom ?? InsetVertical;
    public float ContentWidth => Math.Max(1f, ViewportWidth - InsetHorizontal * 2f);
    public float ContentHeight => Math.Max(1f, ViewportHeight - (InsetVertical + EffectiveInsetBottom));
}

public enum DecorationKind
{
    Underline,
    Strikeout,
    BlockquoteBar,
    Rule,
    Selection,
    Highlight,
    SearchMark,
}

public sealed class PlacedRect
{
    public required DecorationKind Kind { get; init; }
    public required SKRect Rect { get; init; }
    /// <summary>Global body-text offset this decoration starts at, or -1 for structural rules.</summary>
    public int TextStart { get; init; } = -1;
    public int TextLength { get; init; }
}

/// <summary>
/// A persisted reader annotation projected onto the visible page. The layout
/// package deliberately receives only geometry and paint information here so
/// the native painter does not depend on the application's data layer.
/// </summary>
public sealed class TypesetAnnotationOverlay
{
    public required IReadOnlyList<SKRect> Bands { get; init; }
    public required string Style { get; init; }
    public required SKColor Color { get; init; }
}

public enum TypesetDebugBoxKind
{
    HanCell,
    CompatibilityCell,
    Glyph,
}

/// <summary>One diagnostic frame emitted by the native vertical composer.</summary>
public readonly record struct TypesetDebugBox(SKRect Rect, TypesetDebugBoxKind Kind);

public sealed class PlacedImage
{
    public required string Path { get; init; }
    public required SKRect Rect { get; init; }
    /// <summary>Normalized 0..1 sub-rectangle of the source image when cropped or vertically sliced.</summary>
    public SKRect? SourceCrop { get; init; }
    /// <summary>Clockwise rotation applied by the painter, used by vertical inline formulas.</summary>
    public float RotationDegrees { get; init; }
    /// <summary>
    /// True for a standalone figure that owns its space. Inline glyph images
    /// (formulas, icons) keep this false so a tap does not open a viewer.
    /// </summary>
    public bool IsBlock { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    public string? FootnoteText { get; init; }
}

/// <summary>
/// One visible segment of a table on a page. A long table split by page
/// breaks produces one segment per page so pointer hit testing can always
/// resolve the block the user touched.
/// </summary>
public sealed class PlacedTable
{
    public required SKRect Rect { get; init; }
    public required ContentBlock Block { get; init; }
}

public sealed class PlacedHotZone
{
    public required HotZoneKind Kind { get; init; }
    public required SKRect Rect { get; init; }
    public required string Href { get; init; }
    public string? FootnoteText { get; init; }
    /// <summary>Visible reference text ("3", "[3]", "注") for the footnote preview title.</summary>
    public string? Marker { get; init; }
}

/// <summary>
/// A shaped glyph run placed on a page. Positions are relative to
/// <see cref="OriginX"/>/<see cref="OriginY"/> so the painter can rotate the
/// whole run for sideways vertical text. Glyph ids index the font file named
/// by <see cref="FontPath"/> (HarfBuzz and Skia agree on indices for the same
/// file).
/// </summary>
public sealed record PlacedRun
{
    public required string FontPath { get; init; }
    public required float FontSize { get; init; }
    public required ushort[] Glyphs { get; init; }
    /// <summary>Per-glyph pen positions relative to the run origin.</summary>
    public required float[] X { get; init; }
    public required float[] Y { get; init; }
    /// <summary>
    /// Per-glyph shift that was applied to squeeze punctuation. Hit-testing
    /// subtracts it so selection rectangles describe the visible half-cell
    /// rather than the shifted pen position.
    /// </summary>
    public float[]? InkShiftX { get; init; }
    public float OriginX { get; init; }
    public float OriginY { get; init; }
    /// <summary>Font ascent/descent of the run, used for selection geometry.</summary>
    public float Ascent { get; init; }
    public float Descent { get; init; }
    /// <summary>Advance along the flow axis (width horizontally, height vertically).</summary>
    public float FlowAdvance { get; init; }
    /// <summary>Vertical mode only: the run is shaped horizontally and painted rotated 90° clockwise.</summary>
    public bool Sideways { get; init; }
    public bool SyntheticBold { get; init; }
    /// <summary>Uniform paint scale for combined (tate-chu-yoko) digit cells.</summary>
    public float Scale { get; init; } = 1f;
    /// <summary>
    /// Width of an upright vertical cell in the cross-flow direction. Zero
    /// keeps the legacy glyph-bounds behavior used by horizontal and sideways
    /// runs.
    /// </summary>
    public float CellWidth { get; init; }
    /// <summary>
    /// Vertical mode only: horizontal centre of the owning text column and the
    /// uniform selection band width matching the column's text size rather than
    /// the full inter-column line pitch.
    /// </summary>
    public float SelectionCenterX { get; init; }
    public float SelectionWidth { get; init; }
    public required int TextStart { get; init; }
    public required int TextLength { get; init; }
    /// <summary>Per-glyph offset from <see cref="TextStart"/> into the chapter text.</summary>
    public int[] Clusters { get; init; } = Array.Empty<int>();
    /// <summary>This run ends with a synthetic hyphen inserted by English hyphenation.</summary>
    public bool SyntheticHyphen { get; init; }
    public TypesetInlineStyle Style { get; init; }
}

/// <summary>One laid-out page. Immutable once produced.</summary>
public sealed class LayoutPage
{
    public required int Index { get; init; }
    public required TypesetWritingMode WritingMode { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required float InsetHorizontal { get; init; }
    public required float InsetVertical { get; init; }
    /// <summary>Global body-text offset of the first text on the page (-1 when the page has no text).</summary>
    public int TextStartOffset { get; set; } = -1;
    /// <summary>Exclusive end offset, or -1 when the page has no text.</summary>
    public int TextEndOffset { get; set; } = -1;
    public bool EndOfChapter { get; init; }
    public List<PlacedRun> Runs { get; init; } = new();
    public List<PlacedImage> Images { get; init; } = new();
    public List<PlacedTable> Tables { get; init; } = new();
    public List<PlacedRect> Decorations { get; init; } = new();
    public List<PlacedHotZone> HotZones { get; init; } = new();
    public List<TypesetDebugBox> DebugBoxes { get; init; } = new();
}

public sealed class ChapterLayout
{
    public required IReadOnlyList<LayoutPage> Pages { get; init; }
    public required int BodyTextLength { get; init; }
    /// <summary>Element id → page index for fragment navigation.</summary>
    public required IReadOnlyDictionary<string, int> FragmentPages { get; init; }
    public required TypesetLayoutOptions Options { get; init; }
    /// <summary>
    /// False when composition intentionally stopped at an early page so the
    /// first screen could be shown; the host then completes the chapter in the
    /// background. Page ranges and fragment maps only cover the composed prefix.
    /// </summary>
    public bool IsComplete { get; init; } = true;
}
