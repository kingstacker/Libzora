using Avalonia;

namespace Libzora;

/// <summary>Page geometry is independent of the bounded cache of rendered regions.</summary>
internal sealed class PdfReaderLayout
{
    internal const double Margin = 16;
    internal const double Gap = 18;
    public Rect[] Pages { get; }
    /// <summary>Visible frame for each page; matches <see cref="Pages"/> unless auto-crop trims outer margins.</summary>
    public Rect[] Frames { get; }
    public Size Extent { get; }
    public PdfReaderDisplayMode Mode { get; }
    public int FirstPage { get; }
    public int LastPage { get; }

    public PdfReaderLayout(IReadOnlyList<Size> sizes, Size viewport, int pageNumber,
        PdfReaderDisplayMode mode, PdfReaderFitMode fit, double zoom, double dpi, int rotation = 0,
        IReadOnlyList<Rect?>? cropBoxes = null)
    {
        Mode = mode;
        Pages = new Rect[sizes.Count];
        Frames = new Rect[sizes.Count];
        if (sizes.Count == 0) { Extent = viewport; return; }
        pageNumber = Math.Clamp(pageNumber, 1, sizes.Count);
        FirstPage = mode == PdfReaderDisplayMode.Continuous ? 1
            : mode == PdfReaderDisplayMode.TwoPage ? (pageNumber - 1) / 2 * 2 + 1 : pageNumber;
        LastPage = mode == PdfReaderDisplayMode.Continuous ? sizes.Count
            : Math.Min(sizes.Count, FirstPage + (mode == PdfReaderDisplayMode.TwoPage ? 1 : 0));
        var availableWidth = Math.Max(1, viewport.Width - Margin * 2);
        var availableHeight = Math.Max(1, viewport.Height - Margin * 2);
        double Snap(double value) => Math.Round(value * dpi) / dpi;
        Size PageSize(int index)
        {
            var size = sizes[index];
            return rotation % 180 == 0 ? size : new(size.Height, size.Width);
        }
        Rect RotatedCrop(int index)
        {
            if (cropBoxes is null || index < 0 || index >= cropBoxes.Count || cropBoxes[index] is not { } raw)
                return new(0, 0, 1, 1);
            var r = new Rect(
                Math.Clamp(raw.X, 0, 0.35),
                Math.Clamp(raw.Y, 0, 0.35),
                Math.Clamp(raw.Width, 0.35, 1 - Math.Clamp(raw.X, 0, 0.35)),
                Math.Clamp(raw.Height, 0.35, 1 - Math.Clamp(raw.Y, 0, 0.35)));
            return rotation switch
            {
                90 => new(1 - r.Bottom, r.Left, r.Height, r.Width),
                180 => new(1 - r.Right, 1 - r.Bottom, r.Width, r.Height),
                270 => new(r.Top, 1 - r.Right, r.Height, r.Width),
                _ => r,
            };
        }
        var extentWidth = viewport.Width;
        var extentHeight = viewport.Height;
        if (mode == PdfReaderDisplayMode.TwoPage)
        {
            var left = PageSize(FirstPage - 1);
            var right = PageSize(Math.Min(sizes.Count - 1, FirstPage));
            var leftCrop = RotatedCrop(FirstPage - 1);
            var rightCrop = RotatedCrop(Math.Min(sizes.Count - 1, FirstPage));
            var leftVisW = left.Width * leftCrop.Width;
            var leftVisH = left.Height * leftCrop.Height;
            var rightVisW = right.Width * rightCrop.Width;
            var rightVisH = right.Height * rightCrop.Height;
            var scale = Math.Max(1, availableWidth - Gap) / (leftVisW + rightVisW);
            if (fit == PdfReaderFitMode.Page) scale = Math.Min(scale, availableHeight / Math.Max(leftVisH, rightVisH));
            scale *= zoom;
            var totalWidth = (leftVisW + rightVisW) * scale + Gap;
            var x = Math.Max(Margin, (viewport.Width - totalWidth) / 2);
            var y = Math.Max(Margin, (viewport.Height - Math.Max(leftVisH, rightVisH) * scale) / 2);
            var leftFrame = new Rect(Snap(x), Snap(y), Snap(leftVisW * scale), Snap(leftVisH * scale));
            Frames[FirstPage - 1] = leftFrame;
            Pages[FirstPage - 1] = new(
                Snap(leftFrame.X - leftCrop.X * left.Width * scale),
                Snap(leftFrame.Y - leftCrop.Y * left.Height * scale),
                Snap(left.Width * scale),
                Snap(left.Height * scale));
            if (LastPage > FirstPage)
            {
                var rightFrame = new Rect(Snap(x + leftVisW * scale + Gap), Snap(y), Snap(rightVisW * scale), Snap(rightVisH * scale));
                Frames[LastPage - 1] = rightFrame;
                Pages[LastPage - 1] = new(
                    Snap(rightFrame.X - rightCrop.X * right.Width * scale),
                    Snap(rightFrame.Y - rightCrop.Y * right.Height * scale),
                    Snap(right.Width * scale),
                    Snap(right.Height * scale));
            }
            extentWidth = Math.Max(extentWidth, x + totalWidth + Margin);
            extentHeight = Math.Max(extentHeight, y + Math.Max(leftVisH, rightVisH) * scale + Margin);
        }
        else
        {
            var y = Margin;
            for (var page = FirstPage; page <= LastPage; page++)
            {
                var size = PageSize(page - 1);
                var crop = RotatedCrop(page - 1);
                var visW = size.Width * crop.Width;
                var visH = size.Height * crop.Height;
                var scale = availableWidth / visW;
                if (fit == PdfReaderFitMode.Page) scale = Math.Min(scale, availableHeight / visH);
                var frameWidth = Math.Max(1 / dpi, Snap(visW * scale * zoom));
                var frameHeight = Math.Max(1 / dpi, Snap(visH * scale * zoom));
                var fullWidth = Math.Max(1 / dpi, Snap(size.Width * scale * zoom));
                var fullHeight = Math.Max(1 / dpi, Snap(size.Height * scale * zoom));
                var x = Math.Max(Margin, (viewport.Width - frameWidth) / 2);
                if (mode == PdfReaderDisplayMode.SinglePage) y = Math.Max(Margin, (viewport.Height - frameHeight) / 2);
                var frame = new Rect(Snap(x), Snap(y), frameWidth, frameHeight);
                Frames[page - 1] = frame;
                Pages[page - 1] = new(
                    Snap(frame.X - crop.X * fullWidth),
                    Snap(frame.Y - crop.Y * fullHeight),
                    fullWidth,
                    fullHeight);
                extentWidth = Math.Max(extentWidth, x + frameWidth + Margin);
                extentHeight = Math.Max(extentHeight, y + frameHeight + Margin);
                y += frameHeight + Gap;
            }
        }
        Extent = new(Snap(extentWidth), Snap(extentHeight));
    }

    public int PageAt(double y)
    {
        if (Mode != PdfReaderDisplayMode.Continuous || Frames.Length == 0) return FirstPage;
        var low = 0;
        var high = Frames.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Frames[middle].Bottom < y) low = middle + 1; else high = middle;
        }
        return low + 1;
    }

    public IEnumerable<int> VisiblePages(Rect viewport)
    {
        var first = Mode == PdfReaderDisplayMode.Continuous ? PageAt(viewport.Top) : FirstPage;
        for (var page = first; page <= LastPage; page++)
        {
            var bounds = Frames[page - 1];
            if (Mode == PdfReaderDisplayMode.Continuous && bounds.Top > viewport.Bottom) yield break;
            if (bounds.Intersects(viewport)) yield return page;
        }
    }
}
