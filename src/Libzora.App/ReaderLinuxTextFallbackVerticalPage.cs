using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Vertical-writing (vertical-rl) page surface for the Linux text fallback
/// reader. Characters stack top-to-bottom inside columns that advance
/// right-to-left, mirroring CSS writing-mode:vertical-rl with mixed glyph
/// orientation: CJK stays upright while Latin runs rotate 90° clockwise.
///
/// The control owns pointer drag selection, annotation decorations and
/// footnote marker cells so the fallback reader keeps its selection toolbar,
/// highlights and note features in vertical mode — SelectableTextBlock has no
/// vertical text layout underneath.
/// </summary>
public sealed partial class ReaderLinuxTextFallbackVerticalPage : Control
{
    // Leading applied to each character cell along the column axis. A bare
    // font-size grid reads cramped for CJK; a small positive leading matches
    // the visual rhythm of the horizontal pages.
    private const double CharAdvanceRatio = 1.08;
    private const int ParagraphIndentRows = 2;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, string?>(nameof(Text));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, double>(nameof(FontSize), 16d);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, FontFamily>(nameof(FontFamily));

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, FontWeight>(
            nameof(FontWeight),
            FontWeight.Normal);

    /// <summary>Column pitch — the distance between adjacent column origins.</summary>
    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, double>(nameof(LineHeight), 28d);

    public static readonly StyledProperty<bool> ParagraphIndentProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, bool>(
            nameof(ParagraphIndent),
            true);

    public static readonly StyledProperty<bool> StartsParagraphProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, bool>(
            nameof(StartsParagraph),
            true);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<int> ChapterTitleStartProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(ChapterTitleStart), -1);

    public static readonly StyledProperty<int> ChapterTitleLengthProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(ChapterTitleLength));

    public static readonly StyledProperty<IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>?> AnnotationRangesProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>?>(
            nameof(AnnotationRanges));

    public static readonly StyledProperty<IBrush?> InvertedSelectionForegroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IBrush?>(nameof(InvertedSelectionForeground));

    public static readonly StyledProperty<IBrush?> InvertedSelectionBackgroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, IBrush?>(nameof(InvertedSelectionBackground));

    public static readonly StyledProperty<int> SelectionStartProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(SelectionStart));

    public static readonly StyledProperty<int> SelectionEndProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackVerticalPage, int>(nameof(SelectionEnd));

    private struct Cell
    {
        public int Offset;
        public int Length;
        public Rect Bounds;
        public bool Combined;
        public bool Rotated;
        public bool IsFootnote;
        public int FootnoteIndex;
    }

    private List<Cell>? _cells;
    private bool _draggingSelection;
    private int _selectionAnchor;

    /// <summary>Raised after a drag produced a non-empty selection.</summary>
    public event EventHandler? SelectionCommitted;

    /// <summary>
    /// Raised when a tap lands on a footnote marker cell; the payload carries
    /// the footnote href and the pointer position in control coordinates.
    /// </summary>
    public event EventHandler<(string Href, Point Position)>? FootnoteActivated;

    /// <summary>Raised when a tap lands outside any footnote marker cell.</summary>
    public event EventHandler? FootnoteDismissed;

    /// <summary>
    /// Resolves a marker cell's sequential index (in page order) to its href.
    /// Wired by the reader window from the rendered page item's footnotes.
    /// </summary>
    public Func<int, string?>? FootnoteHrefResolver { get; set; }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public bool ParagraphIndent
    {
        get => GetValue(ParagraphIndentProperty);
        set => SetValue(ParagraphIndentProperty, value);
    }

    /// <summary>
    /// Whether the first visual column on this page begins a paragraph. A
    /// page can start in the middle of a long paragraph after pagination.
    /// </summary>
    public bool StartsParagraph
    {
        get => GetValue(StartsParagraphProperty);
        set => SetValue(StartsParagraphProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public int ChapterTitleStart
    {
        get => GetValue(ChapterTitleStartProperty);
        set => SetValue(ChapterTitleStartProperty, value);
    }

    public int ChapterTitleLength
    {
        get => GetValue(ChapterTitleLengthProperty);
        set => SetValue(ChapterTitleLengthProperty, value);
    }

    public IReadOnlyList<MainWindow.ReaderLinuxTextFallbackAnnotationRange>? AnnotationRanges
    {
        get => GetValue(AnnotationRangesProperty);
        set => SetValue(AnnotationRangesProperty, value);
    }

    public IBrush? InvertedSelectionForeground
    {
        get => GetValue(InvertedSelectionForegroundProperty);
        set => SetValue(InvertedSelectionForegroundProperty, value);
    }

    public IBrush? InvertedSelectionBackground
    {
        get => GetValue(InvertedSelectionBackgroundProperty);
        set => SetValue(InvertedSelectionBackgroundProperty, value);
    }

    public int SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public string SelectedText
    {
        get
        {
            var text = Text ?? string.Empty;
            var start = Math.Min(SelectionStart, SelectionEnd);
            var end = Math.Max(SelectionStart, SelectionEnd);
            if (start < 0 || end > text.Length || end <= start) return string.Empty;
            return text[start..end].Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
        }
    }

    /// <summary>
    /// Shared grid metrics for pagination and rendering. The paginator must
    /// use these exact numbers so page boundaries land on column starts.
    /// </summary>
    public static (int CharsPerColumn, int ColumnsPerPage) ComputeGrid(
        double width,
        double height,
        double fontSize,
        double lineHeight)
    {
        var charAdvance = Math.Max(1, fontSize * CharAdvanceRatio);
        var columnPitch = Math.Max(fontSize, lineHeight);
        return (
            Math.Max(1, (int)Math.Floor(Math.Max(1, height) / charAdvance)),
            Math.Max(1, (int)Math.Floor(Math.Max(1, width) / columnPitch)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty
            || change.Property == FontSizeProperty
            || change.Property == LineHeightProperty
            || change.Property == ParagraphIndentProperty
            || change.Property == StartsParagraphProperty
            || change.Property == ChapterTitleStartProperty
            || change.Property == ChapterTitleLengthProperty)
        {
            _cells = null;
        }
        InvalidateVisual();
    }
}
