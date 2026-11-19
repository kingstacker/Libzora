using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Lays a chapter out as vertical-rl pages: each column is one line of text,
/// ordinary characters flow top-to-bottom inside a fixed 1-em cell grid, and
/// sideways runs use their measured flow extent. Columns advance right-to-left.
/// The coordinate mapping is owned by this engine — there is no writing-mode
/// engine underneath to fight.
///
/// Grid rules carried over from the WebKit reader: one line pitch per column,
/// flush paragraph starts, headings bold and centered with one empty column
/// before and after, and opening punctuation never ending a column. Text is
/// always kept inside the content box; a full column continues in the next
/// column instead of hanging punctuation into the bottom page margin.
/// </summary>
internal sealed partial class VerticalComposer
{
    private readonly record struct ColumnPlacement(
        LayoutCell Cell,
        float FlowAdvance,
        float TopHang);

    private readonly ComposerContext _context;
    private readonly CellFactory _cells;
    private readonly PageBuilder _pages;
    private readonly Dictionary<(string FontPath, float FontSize, ushort GlyphId), SKRect> _glyphBoundsCache = new();
    private float _cursorX;
    private float _rubySideWidth;
    private float _rubyBaseWidth;

    public VerticalComposer(ComposerContext context, CellFactory cells, PageBuilder pages)
    {
        _context = context;
        _cells = cells;
        _pages = pages;
        _cursorX = context.Options.ViewportWidth - context.Options.InsetHorizontal;
    }

    private float ColumnPitch => Math.Max(_context.Options.BodyLineHeight, _rubyBaseWidth + _rubySideWidth);

    private float BodyColumnWidth => ColumnPitch - _rubySideWidth;

    private float CellPitch => _context.Options.BaseFontSize + _context.LetterSpacing;

    private int ColumnsPerPage => Math.Max(1, (int)Math.Floor(_context.Options.ContentWidth / ColumnPitch));

    private float ContentLeft => _context.Options.InsetHorizontal;

    private float ContentRight => _context.Options.ViewportWidth - _context.Options.InsetHorizontal;

    private float ContentTop => _context.Options.InsetVertical;

    private float ContentHeight => _context.Options.ContentHeight;

    public void Compose(ChapterContent content)
    {
        var previousBlockWasHeading = false;
        var previousBlockWasParagraph = false;
        var blockIndex = 0;
        foreach (var block in content.Blocks)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            _rubySideWidth = 0f;
            _rubyBaseWidth = 0f;
            switch (block.Kind)
            {
                case BlockKind.Image:
                    PlaceImageBlock(block);
                    break;
                case BlockKind.Rule:
                    PlaceRule(block);
                    break;
                default:
                    PlaceTextBlock(block, previousBlockWasHeading, previousBlockWasParagraph,
                        blockIndex + 1 < content.Blocks.Count && content.Blocks[blockIndex + 1].Kind == BlockKind.Paragraph);
                    break;
            }

            previousBlockWasHeading = block.Kind == BlockKind.Heading;
            previousBlockWasParagraph = block.Kind == BlockKind.Paragraph;
            blockIndex++;
        }
    }
}
