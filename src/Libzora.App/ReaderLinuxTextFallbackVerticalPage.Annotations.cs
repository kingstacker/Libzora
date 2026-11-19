using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Libzora;

/// <summary>
/// Annotation underlines and line styles.
/// </summary>
public sealed partial class ReaderLinuxTextFallbackVerticalPage : Control
{
    private void DrawAnnotationUnderlines(DrawingContext context)
    {
        if (AnnotationRanges is not { Count: > 0 } || _cells is not { } cells) return;
        var sourceLength = (Text ?? string.Empty).Length;
        foreach (var annotation in AnnotationRanges)
        {
            if (annotation.Style == "marker") continue;
            var color = ParseAnnotationColor(annotation.Color);
            var brush = new SolidColorBrush(color);
            foreach (var rect in EnumerateAnnotationRects(annotation, cells, sourceLength))
            {
                // The vertical counterpart of the baseline underline runs along
                // the right edge of the column band, where Chinese emphasis
                // lines traditionally sit.
                var x = rect.Right - Math.Min(2, Math.Max(0.75, rect.Width / 3));
                switch (annotation.Style)
                {
                    case "dotted":
                        DrawDottedVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                    case "wavy":
                        DrawWavyVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                    case "double":
                        DrawSolidVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        DrawSolidVerticalLine(context, brush, x - 2.5, rect.Top, rect.Bottom);
                        break;
                    case "dashed":
                        DrawDashedVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                    default:
                        DrawSolidVerticalLine(context, brush, x, rect.Top, rect.Bottom);
                        break;
                }
            }
        }
    }

    private IEnumerable<Rect> EnumerateAnnotationRects(
        MainWindow.ReaderLinuxTextFallbackAnnotationRange annotation,
        List<Cell> cells,
        int sourceLength)
    {
        var start = Math.Clamp(annotation.Start, 0, sourceLength);
        var end = Math.Clamp(start + annotation.Length, start, sourceLength);
        foreach (var cell in cells)
        {
            if (cell.Offset >= end || cell.Offset + cell.Length <= start) continue;
            yield return cell.Bounds;
        }
    }

    private static Color ParseAnnotationColor(string value)
    {
        try { return Color.Parse(value); }
        catch { return Colors.Black; }
    }

    private static void DrawSolidVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        if (bottom <= top) return;
        context.DrawLine(new Pen(brush, 1.6), new Point(x, top), new Point(x, bottom));
    }

    private static void DrawDashedVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        if (bottom <= top) return;
        const double dash = 4.5;
        const double gap = 3.5;
        var pen = new Pen(brush, 1.6);
        for (var y = top; y < bottom; y += dash + gap)
            context.DrawLine(pen, new Point(x, y), new Point(x, Math.Min(bottom, y + dash)));
    }

    private static void DrawDottedVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        // Filled dots instead of DashStyle.Dot: some GTK/Skia combinations
        // render that pattern as an empty stroke, the same renderer quirk
        // documented on the horizontal annotation path.
        const double radius = 1.15;
        const double spacing = 4.25;
        var first = top + radius;
        var last = bottom - radius;
        if (last < first) first = (top + bottom) / 2;
        for (var y = first; y <= last + 0.01; y += spacing)
            context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
    }

    private static void DrawWavyVerticalLine(
        DrawingContext context,
        IBrush brush,
        double x,
        double top,
        double bottom)
    {
        if (bottom <= top) return;
        var pen = new Pen(brush, 1.6) { LineCap = PenLineCap.Round };
        const double wavelength = 8;
        const double amplitude = 1.5;
        const double step = 1.5;
        var previous = new Point(x, top);
        for (var y = top + step; y < bottom; y += step)
        {
            var phase = (y - top) / wavelength * Math.PI * 2;
            var current = new Point(x + Math.Sin(phase) * amplitude, y);
            context.DrawLine(pen, previous, current);
            previous = current;
        }

        var endPhase = (bottom - top) / wavelength * Math.PI * 2;
        context.DrawLine(pen, previous, new Point(x + Math.Sin(endPhase) * amplitude, bottom));
    }
}
