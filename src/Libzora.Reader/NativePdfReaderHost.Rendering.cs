using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Page transforms, hit testing and drawing.
/// </summary>
public sealed partial class NativePdfReaderHost
{
    private Size GetUnrotatedPageSize(Rect page) => Rotation % 180 == 0 ? page.Size : new(page.Height, page.Width);
    private Matrix GetPageTransform(Rect page) => Rotation switch
    {
        90 => new(0, 1, -1, 0, page.Right, page.Top),
        180 => new(-1, 0, 0, -1, page.Right, page.Bottom),
        270 => new(0, -1, 1, 0, page.Left, page.Bottom),
        _ => new(1, 0, 0, 1, page.Left, page.Top)
    };

    private Rect GetLocalGlyphBounds(int pageNumber, PdfTextBounds box, Rect page)
    {
        var size = GetUnrotatedPageSize(page);
        return new(
            box.X * size.Width,
            box.Y * size.Height,
            box.Width * size.Width,
            box.Height * size.Height);
    }

    private Point GetNormalizedPagePoint(int pageNumber, Point point)
    {
        var page = GetPageBounds(pageNumber);
        var local = GetPageTransform(page).Invert().Transform(point);
        var size = GetUnrotatedPageSize(page);
        return new(
            Math.Clamp(local.X / Math.Max(1, size.Width), 0, 1),
            Math.Clamp(local.Y / Math.Max(1, size.Height), 0, 1));
    }

    private void EmitPointAnnotation(int pageNumber, Point point)
    {
        var normalized = GetNormalizedPagePoint(pageNumber, point);
        _pointAnnotationMode = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        ClearSelection();
        Emit(new
        {
            type = "pdfPointAnnotation",
            page = pageNumber,
            pageX = normalized.X,
            pageY = normalized.Y,
            x = Math.Clamp(point.X, 0, Bounds.Width),
            y = Math.Clamp(point.Y, 0, Bounds.Height)
        });
    }

    public IReadOnlyList<Rect> GetRangeBounds(int start, int end) => GetRangeBounds(PageNumber, start, end);
    public IReadOnlyList<Rect> GetRangeBounds(int pageNumber, int start, int end)
    {
        var transform = GetPageTransform(GetPageBounds(pageNumber));
        return GetLocalRangeBounds(pageNumber, start, end).Select(rect => rect.TransformToAABB(transform)).ToArray();
    }

    private IReadOnlyList<Rect> GetLocalRangeBounds(int pageNumber, int start, int end)
    {
        var content = GetPageContent(pageNumber);
        if (content is null || end <= start) return [];
        var page = GetPageBounds(pageNumber);
        var rectangles = new List<Rect>();
        foreach (var glyph in content.Glyphs)
        {
            if (glyph.Offset >= end) break;
            if (glyph.Offset + glyph.Length <= start) continue;
            var rect = GetLocalGlyphBounds(pageNumber, glyph.Bounds, page);
            if (rectangles.Count > 0)
            {
                var previous = rectangles[^1];
                var sameLine = Math.Abs(previous.Center.Y - rect.Center.Y) < Math.Max(previous.Height, rect.Height) * 0.45
                    && rect.Left - previous.Right < Math.Max(previous.Height, rect.Height) * 2 && rect.Left >= previous.Left - 1;
                var sameVerticalLine = Math.Abs(previous.Center.X - rect.Center.X) < Math.Max(previous.Width, rect.Width) * 0.4
                    && rect.Top - previous.Bottom < Math.Max(previous.Width, rect.Width) * 2 && rect.Top >= previous.Top;
                if (sameLine || sameVerticalLine) { rectangles[^1] = previous.Union(rect); continue; }
            }
            rectangles.Add(rect);
        }
        return rectangles;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(_palette.Sidebar), new Rect(Bounds.Size));
        if (_layout is null) return;
        if (Math.Abs(DeviceScaling - _layoutDpi) > 0.001) ScheduleRender();
        var visiblePages = VisiblePageNumbers.ToArray();
        // Paint every shadow below every sheet. When pages are close together,
        // a later page's shadow must never tint an earlier page's paper.
        foreach (var pageNumber in visiblePages)
            DrawPageShadow(context, GetVisiblePageFrame(pageNumber));

        foreach (var pageNumber in visiblePages)
        {
            var page = GetPageBounds(pageNumber);
            var frame = GetVisiblePageFrame(pageNumber);
            context.DrawRectangle(new SolidColorBrush(_palette.Page), new Pen(new SolidColorBrush(_palette.Border), 0.5), frame);
            using var clip = context.PushClip(frame);
            if (_pages.TryGetValue(pageNumber, out var cached))
            {
                if (cached.PreviewBitmap is { } preview && cached.PreviewRotation == Rotation && cached.PreviewPalette == _palette)
                {
                    context.DrawImage(preview, new Rect(preview.Size), page);
                }
                if (cached.Bitmap is { } bitmap && cached.Raster is { } raster
                    && raster.Rotation == Rotation && cached.Palette == _palette)
                {
                    var target = new Rect(
                        page.X + (double)raster.Left / raster.PagePixelWidth * page.Width,
                        page.Y + (double)raster.Top / raster.PagePixelHeight * page.Height,
                        (double)raster.Width / raster.PagePixelWidth * page.Width,
                        (double)raster.Height / raster.PagePixelHeight * page.Height);
                    context.DrawImage(bitmap, new Rect(bitmap.Size), target);
                }
            }
            // Rotate the original annotation geometry as a whole, including
            // underline direction, so a 180° turn does not move lines across text.
            using var transform = context.PushTransform(GetPageTransform(page));
            foreach (var annotation in AnnotationsForPage(pageNumber))
            {
                var color = Color.TryParse(annotation.Color, out var parsed) ? parsed : Colors.Black;
                if (color == Colors.Black && _palette != ReaderPalette.For(ReaderTheme.Classic)) color = _palette.Ink;
                if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var pointX, out var pointY))
                {
                    var size = GetUnrotatedPageSize(page);
                    var point = new Point(pointX * size.Width, pointY * size.Height);
                    DrawPointAnnotation(context, point, color);
                    if (_selectedAnnotationId == annotation.Id)
                        context.DrawEllipse(
                            new SolidColorBrush(Color.FromArgb(28, 66, 133, 210)),
                            SelectionPen(),
                            point, 8.5, 8.5);
                    continue;
                }
                if (annotation.EndOffset <= annotation.StartOffset) continue;
                var isSelected = _selectedAnnotationId == annotation.Id;
                foreach (var rect in GetAnnotationLocalBounds(pageNumber, annotation))
                {
                    DrawAnnotation(context, rect, annotation.UnderlineStyle, color);
                    if (isSelected)
                        context.DrawRectangle(
                            new SolidColorBrush(Color.FromArgb(24, 66, 133, 210)),
                            SelectionPen(),
                            rect.Inflate(2));
                }
            }
            if (_searchPage == pageNumber)
                for (var index = 0; index < _search.Count; index++)
                    foreach (var rect in GetLocalRangeBounds(pageNumber, _search[index].Start, _search[index].Start + _search[index].Length))
                        context.FillRectangle(new SolidColorBrush(Color.FromArgb(index == _searchIndex ? (byte)140 : (byte)70, 255, 200, 50)), rect.Inflate(1));
            if (_speechHighlight is { } speech && speech.Page == pageNumber)
                foreach (var rect in GetLocalRangeBounds(pageNumber, speech.Start, speech.Start + speech.Length))
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(65, 100, 160, 95)), rect.Inflate(1));
            if (_selectionPage == pageNumber)
            {
                var selection = new GeometryGroup { FillRule = FillRule.NonZero };
                foreach (var rect in (_selectionRanges ?? [new(_selectionStart, _selectionEnd)])
                    .SelectMany(range => GetLocalRangeBounds(pageNumber, range.Start, range.End)))
                    selection.Children.Add(new RectangleGeometry(rect.Inflate(1)));
                if (selection.Children.Count > 0)
                {
                    // Invert the original page pixels, preserving PDF glyphs and antialiasing.
                    // Draw once through the union so overlapping line boxes never invert twice.
                    using var selectionClip = context.PushGeometryClip(selection);
                    using var inversion = context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Difference });
                    context.DrawImage(SelectionInversionPixel.Value, selection.Bounds);
                }
            }
        }

        if (_annotationCalloutVisible && _annotationCallout is { } callout)
            DrawAnnotationCalloutConnector(context, callout.Anchor, callout.Bounds, _palette.Ink);
        foreach (var additional in _additionalAnnotationConnectors)
            DrawAnnotationCalloutConnector(context, additional.Anchor, additional.Bounds, _palette.Ink);
    }

    private static void DrawAnnotationCalloutConnector(DrawingContext context, Point anchor, Rect bounds, Color color)
    {
        var connector = new Pen(
            new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B)),
            1.35,
            DashStyle.Dash);
        Point endpoint;
        Point firstBend;
        Point secondBend;
        if (anchor.X <= bounds.Left)
        {
            endpoint = new(bounds.Left, FindCalloutPort(anchor.Y, bounds.Top, bounds.Bottom));
            // Turn only in the page gutter, never vertically through body text.
            var elbowX = endpoint.X - 9;
            firstBend = new(elbowX, anchor.Y);
            secondBend = new(elbowX, endpoint.Y);
        }
        else if (anchor.X >= bounds.Right)
        {
            endpoint = new(bounds.Right, FindCalloutPort(anchor.Y, bounds.Top, bounds.Bottom));
            var elbowX = anchor.X + (endpoint.X - anchor.X) * 0.68;
            firstBend = new(elbowX, anchor.Y);
            secondBend = new(elbowX, endpoint.Y);
        }
        else if (anchor.Y <= bounds.Top)
        {
            endpoint = new(FindCalloutPort(anchor.X, bounds.Left, bounds.Right), bounds.Top);
            var elbowY = anchor.Y + (endpoint.Y - anchor.Y) * 0.68;
            firstBend = new(anchor.X, elbowY);
            secondBend = new(endpoint.X, elbowY);
        }
        else
        {
            endpoint = new(FindCalloutPort(anchor.X, bounds.Left, bounds.Right), bounds.Bottom);
            var elbowY = anchor.Y + (endpoint.Y - anchor.Y) * 0.68;
            firstBend = new(anchor.X, elbowY);
            secondBend = new(endpoint.X, elbowY);
        }

        context.DrawLine(connector, anchor, firstBend);
        context.DrawLine(connector, firstBend, secondBend);
        context.DrawLine(connector, secondBend, endpoint);
        context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(color), 1), anchor, 2, 2);
    }

    private static double FindCalloutPort(double anchor, double start, double end)
    {
        var margin = Math.Min(22, (end - start) / 3);
        var minimum = start + margin;
        var maximum = end - margin;
        var port = Math.Clamp(anchor, minimum, maximum);
        if (Math.Abs(port - anchor) >= 18) return port;

        var upper = Math.Clamp((start + end) / 2 - 24, minimum, maximum);
        var lower = Math.Clamp((start + end) / 2 + 24, minimum, maximum);
        return Math.Abs(upper - anchor) >= Math.Abs(lower - anchor) ? upper : lower;
    }

    private static void DrawAnnotation(DrawingContext context, Rect rect, string style, Color color)
    {
        if (style == "marker")
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)), rect.Inflate(1));
            return;
        }
        var brush = new SolidColorBrush(color);
        var vertical = rect.Height > rect.Width * 2;
        var first = vertical ? new Point(rect.Right + 2, rect.Top) : new Point(rect.Left, rect.Bottom + 2);
        var last = vertical ? new Point(first.X, rect.Bottom) : new Point(rect.Right, first.Y);
        var pen = new Pen(brush, 1.4, style == "dashed" ? DashStyle.Dash : style == "dotted" ? DashStyle.Dot : null);
        if (style == "wavy")
        {
            var length = vertical ? rect.Height : rect.Width;
            var previous = first;
            for (double along = 2; along <= length + 2; along += 2)
            {
                var distance = Math.Min(along, length);
                var wave = Math.Sin(distance * Math.PI / 4) * 1.5;
                var next = vertical ? new Point(first.X + wave, first.Y + distance) : new Point(first.X + distance, first.Y + wave);
                context.DrawLine(pen, previous, next);
                previous = next;
            }
        }
        else
        {
            context.DrawLine(pen, first, last);
            if (style == "double")
            {
                var shift = vertical ? new Vector(3, 0) : new Vector(0, 3);
                context.DrawLine(pen, first + shift, last + shift);
            }
        }
    }

    // Marching ants: the dash offset is advanced by _selectionDashTimer.
    private Pen SelectionPen() => new(
        new SolidColorBrush(Color.FromArgb(235, 66, 133, 210)),
        1.2,
        new DashStyle([4, 3], _selectionDashOffset));

    // A drawing context has no blur, so use narrow linear gradients around
    // the page. Their spread stays inside the reader's page gutter, avoiding
    // the stacked dark bands made by overlapping page-sized rectangles.
    private void DrawPageShadow(DrawingContext context, Rect page)
    {
        const double spread = 7;
        const byte edgeAlpha = 28;

        context.DrawRectangle(CreatePageShadowGradient(horizontal: false, darkAtStart: false, edgeAlpha), null,
            new Rect(page.Left, page.Top - spread, page.Width, spread));
        context.DrawRectangle(CreatePageShadowGradient(horizontal: false, darkAtStart: true, edgeAlpha), null,
            new Rect(page.Left, page.Bottom, page.Width, spread));
        context.DrawRectangle(CreatePageShadowGradient(horizontal: true, darkAtStart: false, edgeAlpha), null,
            new Rect(page.Left - spread, page.Top, spread, page.Height));
        context.DrawRectangle(CreatePageShadowGradient(horizontal: true, darkAtStart: true, edgeAlpha), null,
            new Rect(page.Right, page.Top, spread, page.Height));

        context.DrawRectangle(CreatePageShadowCornerGradient(edgeAlpha, 1, 1), null,
            new Rect(page.Left - spread, page.Top - spread, spread, spread));
        context.DrawRectangle(CreatePageShadowCornerGradient(edgeAlpha, 0, 1), null,
            new Rect(page.Right, page.Top - spread, spread, spread));
        context.DrawRectangle(CreatePageShadowCornerGradient(edgeAlpha, 1, 0), null,
            new Rect(page.Left - spread, page.Bottom, spread, spread));
        context.DrawRectangle(CreatePageShadowCornerGradient(edgeAlpha, 0, 0), null,
            new Rect(page.Right, page.Bottom, spread, spread));
    }

    private static LinearGradientBrush CreatePageShadowGradient(bool horizontal, bool darkAtStart, byte edgeAlpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = horizontal
                ? new RelativePoint(0, 0.5, RelativeUnit.Relative)
                : new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0.5, RelativeUnit.Relative)
                : new RelativePoint(0.5, 1, RelativeUnit.Relative)
        };
        SetPageShadowStops(brush, edgeAlpha, darkAtStart);
        return brush;
    }

    private static RadialGradientBrush CreatePageShadowCornerGradient(byte edgeAlpha, double centerX, double centerY)
    {
        var corner = new RelativePoint(centerX, centerY, RelativeUnit.Relative);
        var brush = new RadialGradientBrush
        {
            Center = corner,
            GradientOrigin = corner,
            RadiusX = new RelativeScalar(1, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(1, RelativeUnit.Relative)
        };
        SetPageShadowStops(brush, edgeAlpha, darkAtStart: true);
        return brush;
    }

    private static void SetPageShadowStops(GradientBrush brush, byte edgeAlpha, bool darkAtStart)
    {
        var transparent = Color.FromArgb(0, 0, 0, 0);
        var soft = Color.FromArgb((byte)(edgeAlpha * 0.3), 0, 0, 0);
        var middle = Color.FromArgb((byte)(edgeAlpha * 0.7), 0, 0, 0);
        var edge = Color.FromArgb(edgeAlpha, 0, 0, 0);
        brush.GradientStops.Add(new GradientStop(darkAtStart ? edge : transparent, 0));
        brush.GradientStops.Add(new GradientStop(darkAtStart ? middle : soft, 0.4));
        brush.GradientStops.Add(new GradientStop(darkAtStart ? soft : middle, 0.8));
        brush.GradientStops.Add(new GradientStop(darkAtStart ? transparent : edge, 1));
    }

    private static void DrawPointAnnotation(DrawingContext context, Point point, Color color)
    {
        var brush = new SolidColorBrush(color == Colors.Black
            ? Color.FromArgb(235, 45, 45, 45)
            : Color.FromArgb(235, color.R, color.G, color.B));
        context.DrawEllipse(brush, null, point, 5, 5);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White), 1), point, 3.2, 3.2);
    }

    private int PageAtPoint(Point point) => VisiblePageNumbers.FirstOrDefault(page => GetPageBounds(page).Contains(point));
    private int HitOffset(int pageNumber, Point point, bool nearest, bool readingOrder = false)
    {
        var content = GetPageContent(pageNumber);
        if (content is null || !nearest && !GetPageBounds(pageNumber).Contains(point)) return -1;
        var page = GetPageBounds(pageNumber);
        point = GetPageTransform(page).Invert().Transform(point);
        PdfTextGlyph? best = null;
        var bestDistance = double.MaxValue;
        foreach (var glyph in content.Glyphs)
        {
            var rect = GetLocalGlyphBounds(pageNumber, glyph.Bounds, page);
            var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            best = glyph;
            bestDistance = distance;
        }
        if (best is null || !nearest && bestDistance > 64) return -1;
        var bestBounds = GetLocalGlyphBounds(pageNumber, best.Bounds, page);
        var after = (point.X - bestBounds.Center.X) * best.AdvanceX
            + (point.Y - bestBounds.Center.Y) * best.AdvanceY > 0;
        return readingOrder && _pages.TryGetValue(pageNumber, out var cached)
            ? cached.SelectionLayout.IndexAtOffset(best.Offset) + (after ? 1 : 0)
            : best.Offset + (after ? best.Length : 0);
    }
}
