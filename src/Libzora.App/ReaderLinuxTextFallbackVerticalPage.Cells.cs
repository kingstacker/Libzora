using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Cell building and glyph drawing.
/// </summary>
public sealed partial class ReaderLinuxTextFallbackVerticalPage : Control
{
    private List<Cell> BuildCells()
    {
        var cells = new List<Cell>();
        var text = Text ?? string.Empty;
        if (text.Length == 0 || Bounds.Width < 1 || Bounds.Height < 1)
            return cells;

        var charAdvance = Math.Max(1, FontSize * CharAdvanceRatio);
        var columnPitch = Math.Max(FontSize, LineHeight);
        var charsPerColumn = Math.Max(1, (int)Math.Floor(Bounds.Height / charAdvance));
        var rowIndex = 0;
        var columnIndex = 0;
        var paragraphIndentPending = ParagraphIndent && StartsParagraph;
        var titleEnd = ChapterTitleLength > 0 ? ChapterTitleStart + ChapterTitleLength : -1;
        var footnoteIndex = 0;
        var titleColumnStart = -1;

        // A chapter heading centers itself vertically inside its column,
        // matching the centered heading style of the horizontal pages.
        void CenterCompletedTitleColumn()
        {
            if (titleColumnStart < 0)
            {
                return;
            }

            var count = cells.Count - titleColumnStart;
            if (count > 1 && count < charsPerColumn)
            {
                var offset = Math.Floor((Bounds.Height - count * charAdvance) / 2d);
                if (offset > 0)
                {
                    for (var index = titleColumnStart; index < cells.Count; index++)
                    {
                        var shifted = cells[index];
                        shifted.Bounds = shifted.Bounds.WithY(shifted.Bounds.Y + offset);
                        cells[index] = shifted;
                    }
                }
            }
            titleColumnStart = -1;
        }

        var units = ReaderLinuxVerticalTextUnits.Tokenize(text);
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            if (unit.IsLineBreak)
            {
                CenterCompletedTitleColumn();
                rowIndex = 0;
                columnIndex++;
                paragraphIndentPending = ParagraphIndent;
                continue;
            }

            if (paragraphIndentPending)
            {
                // Keep the first line of each paragraph two glyph cells down
                // from the logical inline start, the vertical equivalent of
                // text-indent: 2em. Clamp only degenerate tiny test surfaces;
                // normal reading pages have ample room for both rows.
                rowIndex = Math.Min(
                    ParagraphIndentRows,
                    Math.Max(0, charsPerColumn - 1));
                paragraphIndentPending = false;
            }

            if (ReaderLinuxVerticalTextUnits.ShouldBreakBefore(
                    units,
                    text,
                    index,
                    rowIndex,
                    charsPerColumn))
            {
                CenterCompletedTitleColumn();
                rowIndex = 0;
                columnIndex++;
            }

            var visualRows = ReaderLinuxVerticalTextUnits.GetVisualRows(unit, charsPerColumn);

            if (columnPitch * (columnIndex + 1) > Bounds.Width + 0.5) break;

            if (unit.Offset >= ChapterTitleStart
                && unit.Offset < titleEnd
                && titleColumnStart < 0)
                titleColumnStart = cells.Count;

            var bandLeft = Bounds.Width - (columnIndex + 1) * columnPitch;
            var character = text[unit.Offset];
            var isFootnoteMarker = character == MainWindow.ReaderLinuxTextFallbackFootnoteMarker;
            cells.Add(new Cell
            {
                Offset = unit.Offset,
                Length = unit.Length,
                Bounds = new Rect(
                    bandLeft,
                    rowIndex * charAdvance,
                    Math.Max(1, columnPitch),
                    Math.Max(charAdvance, visualRows * charAdvance)),
                Combined = unit.IsCombined,
                Rotated = unit.IsSidewaysRun
                    || !unit.IsCombined && !IsVerticalUpright(character),
                IsFootnote = isFootnoteMarker,
                FootnoteIndex = isFootnoteMarker ? footnoteIndex++ : -1
            });
            rowIndex += visualRows;
        }

        CenterCompletedTitleColumn();
        return cells;
    }

    private static bool IsVerticalUpright(char character) =>
        character is >= '0' and <= '9'              // keep Arabic digits on the CJK cell grid
        or >= '\u2E80' and <= '\u9FFF'              // CJK strokes, kana, ideographs, CJK punctuation
        or >= '\uF900' and <= '\uFAFF'              // compatibility ideographs
        or >= '\uFF00';                             // fullwidth and halfwidth forms

    public override void Render(DrawingContext context)
    {
        var text = Text ?? string.Empty;
        if (text.Length == 0) return;
        _cells ??= BuildCells();
        if (_cells.Count == 0) return;

        var selectionStart = Math.Min(SelectionStart, SelectionEnd);
        var selectionEnd = Math.Max(SelectionStart, SelectionEnd);
        var backing = InvertedSelectionBackground ?? Brushes.Black;
        var invertedForeground = InvertedSelectionForeground ?? Brushes.White;
        var markerRanges = CollectMarkerRanges();
        var bodyTypeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
        var footnoteTypeface = new Typeface(
            FontFamily,
            FontStyle.Normal,
            FontWeight.Bold,
            FontStretch.Normal);

        foreach (var cell in _cells)
        {
            var character = text[cell.Offset];
            var selected = cell.Offset < selectionEnd
                && cell.Offset + cell.Length > selectionStart;
            var marker = markerRanges.FirstOrDefault(range =>
                cell.Offset < range.End && cell.Offset + cell.Length > range.Start);
            var inverted = selected || marker.Inverted;
            if (selected)
                context.FillRectangle(backing, cell.Bounds);
            else if (marker.Background is { } markerBackground)
                context.FillRectangle(markerBackground, cell.Bounds);
            var brush = inverted ? invertedForeground : Foreground ?? Brushes.Black;

            if (character == MainWindow.ReaderLinuxTextFallbackFootnoteMarker)
            {
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(28, 96, 96, 96)),
                    cell.Bounds);
                DrawCenteredGlyph(
                    context,
                    cell,
                    CreateFormatted(UiText.Get("注"), footnoteTypeface, brush),
                    brush);
                continue;
            }

            var glyphText = cell.Length > 1
                ? text.Substring(cell.Offset, cell.Length)
                : character.ToString();
            var glyph = CreateFormatted(glyphText, bodyTypeface, brush);
            if (cell.Combined)
            {
                // A compact horizontal run is still one logical vertical
                // cell. Fit its actual ink bounds, rather than its advance
                // width, so a percent sign or a decimal cannot bleed into
                // the next row/column.
                DrawGlyphInCell(context, cell, glyph, brush, rotation: 0, compressWidth: true);
            }
            else if (cell.Rotated)
            {
                // Latin and other horizontal-only scripts turn 90° clockwise,
                // the mixed-orientation behavior of CSS vertical writing.
                // Rotate the glyph around its own cell center. Composing a
                // translation with a rotation here rotates the cell's
                // position as well, which makes a run such as "100" leave
                // its CJK column and appear as a detached horizontal row.
                DrawGlyphInCell(
                    context,
                    cell,
                    glyph,
                    brush,
                    rotation: Math.PI / 2,
                    compressWidth: false);
            }
            else if (IsPairedPunctuation(character))
            {
                // Horizontal fallback shaping has no OpenType vertical bracket
                // form. Keep the bracket inside the same centred one-em cell
                // as Han, digits and punctuation. A per-bracket Y offset makes
                // the opening and closing margins visibly asymmetric.
                DrawGlyphInCell(
                    context,
                    cell,
                    glyph,
                    brush,
                    rotation: 0,
                    compressWidth: false,
                    maximumScale: 0.78);
            }
            else
            {
                DrawCenteredGlyph(context, cell, glyph, brush);
            }
        }

        DrawAnnotationUnderlines(context);
    }

    // FormattedText's advance metrics include font leading and alignment
    // space. Geometry.Bounds is the actual ink rectangle, which is the only
    // reliable basis for centering mixed-orientation glyphs in a publication
    // cell. The clip is intentional: a bad font overhang must never paint
    // over its neighbor.
    private static void DrawGlyphInCell(
        DrawingContext context,
        Cell cell,
        FormattedText glyph,
        IBrush brush,
        double rotation,
        bool compressWidth,
        double maximumScale = 1)
    {
        if (glyph.BuildGeometry(new Point(0, 0)) is not { } geometry)
            return;

        var sourceBounds = geometry.Bounds;
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
            return;

        var inset = Math.Min(1.25, Math.Min(cell.Bounds.Width, cell.Bounds.Height) * 0.06);
        var targetWidth = Math.Max(1, cell.Bounds.Width - inset * 2);
        var targetHeight = Math.Max(1, cell.Bounds.Height - inset * 2);
        double scaleX;
        double scaleY;
        if (rotation != 0)
        {
            // A 90-degree rotation swaps the source width and height. Keep
            // the scale uniform so punctuation does not become distorted.
            var scale = Math.Min(
                maximumScale,
                Math.Min(
                    targetWidth / Math.Max(1, sourceBounds.Height),
                    targetHeight / Math.Max(1, sourceBounds.Width)));
            scaleX = scale;
            scaleY = scale;
        }
        else if (compressWidth)
        {
            scaleX = Math.Min(maximumScale, targetWidth / Math.Max(1, sourceBounds.Width));
            scaleY = Math.Min(maximumScale, targetHeight / Math.Max(1, sourceBounds.Height));
        }
        else
        {
            var scale = Math.Min(
                maximumScale,
                Math.Min(
                    targetWidth / Math.Max(1, sourceBounds.Width),
                    targetHeight / Math.Max(1, sourceBounds.Height)));
            scaleX = scale;
            scaleY = scale;
        }

        var center = cell.Bounds.Center;
        var transform = Matrix.CreateTranslation(center.X, center.Y)
            * Matrix.CreateRotation(rotation)
            * Matrix.CreateScale(scaleX, scaleY)
            * Matrix.CreateTranslation(-sourceBounds.Center.X, -sourceBounds.Center.Y);
        using var clip = context.PushClip(cell.Bounds);
        using var placement = context.PushTransform(transform);
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawCenteredGlyph(
        DrawingContext context,
        Cell cell,
        FormattedText glyph,
        IBrush brush)
        => DrawGlyphInCell(context, cell, glyph, brush, rotation: 0, compressWidth: false);

    private static bool IsPairedPunctuation(char character) => character is
        '（' or '）' or '《' or '》' or '〈' or '〉' or '【' or '】'
        or '〔' or '〕' or '［' or '］' or '｛' or '｝'
        or '“' or '”' or '‘' or '’' or '「' or '」' or '『' or '』';

    private FormattedText CreateFormatted(string value, Typeface typeface, IBrush brush) => new(
        value,
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        typeface,
        FontSize,
        brush);

    private List<(int Start, int End, IBrush Background, bool Inverted)> CollectMarkerRanges()
    {
        var ranges = new List<(int Start, int End, IBrush Background, bool Inverted)>();
        if (AnnotationRanges is not { Count: > 0 }) return ranges;
        var sourceLength = (Text ?? string.Empty).Length;
        foreach (var annotation in AnnotationRanges)
        {
            if (annotation.Style != "marker") continue;
            var start = Math.Clamp(annotation.Start, 0, sourceLength);
            var end = Math.Clamp(annotation.Start + annotation.Length, start, sourceLength);
            if (end <= start) continue;
            var color = ParseAnnotationColor(annotation.Color);
            var inverted = color == Colors.Black;
            IBrush background = inverted
                ? InvertedSelectionBackground ?? Brushes.Black
                : new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
            ranges.Add((start, end, background, inverted));
        }

        return ranges;
    }
}
