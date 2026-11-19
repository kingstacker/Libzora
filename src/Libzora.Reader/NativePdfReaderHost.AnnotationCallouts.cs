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
/// Annotation modes, anchors and callouts.
/// </summary>
public sealed partial class NativePdfReaderHost
{
    public void SetPointAnnotationMode(bool enabled)
    {
        _pointAnnotationMode = enabled;
        if (enabled) _handToolMode = false;
        if (enabled)
        {
            ClearSelection();
        }
        Cursor = new Cursor(enabled
            ? StandardCursorType.Cross
            : _handToolMode ? StandardCursorType.Hand : StandardCursorType.Arrow);
        InvalidateVisual();
    }

    public void SetHandToolMode(bool enabled)
    {
        _handToolMode = enabled;
        if (enabled)
        {
            _pointAnnotationMode = false;
            ClearSelection();
        }
        _pointerStart = null;
        _pointerAnnotationHit = null;
        _isPanning = false;
        Cursor = new Cursor(_pointAnnotationMode
            ? StandardCursorType.Cross
            : enabled ? StandardCursorType.Hand : StandardCursorType.Arrow);
        InvalidateVisual();
    }

    public Point? GetPdfPointPosition(ReaderAnnotation annotation)
    {
        if (!ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var x, out var y)
            || !annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(annotation.ChapterPath[(annotation.ChapterPath.LastIndexOf(':') + 1)..], out var page)
            || page < 1
            || page > PageCount)
            return null;
        var bounds = GetPageBounds(page);
        var size = GetUnrotatedPageSize(bounds);
        var local = new Point(
            x * size.Width,
            y * size.Height);
        return GetPageTransform(bounds).Transform(local);
    }

    public bool TryGetAnnotationAnchor(ReaderAnnotation annotation, out int pageNumber, out Point anchor)
    {
        pageNumber = 0;
        anchor = default;
        if (!annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(annotation.ChapterPath[(annotation.ChapterPath.LastIndexOf(':') + 1)..], out pageNumber)
            || pageNumber < 1
            || pageNumber > PageCount)
            return false;

        if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out _, out _)
            && GetPdfPointPosition(annotation) is { } point)
        {
            anchor = point;
            return true;
        }

        if (GetTextAnnotationAnchor(pageNumber, annotation.StartOffset, annotation.EndOffset, annotation.Fragment) is not { } textAnchor)
            return false;
        anchor = textAnchor;
        return true;
    }

    public Point? GetTextAnnotationAnchor(int pageNumber, int start, int end, string? fragment = null)
    {
        var bounds = PdfTextRanges.Read(fragment, start, end).SelectMany(r => GetLocalRangeBounds(pageNumber, r.Start, r.End)).ToArray();
        if (bounds.Length == 0) return null;
        var last = bounds[^1];
        var point = last.Height > last.Width * 2
            ? new Point(last.Right + 2, last.Bottom)
            : new Point(last.Right, last.Bottom + 2);
        return GetPageTransform(GetPageBounds(pageNumber)).Transform(point);
    }

    public bool TryGetAnnotationCalloutBounds(int pageNumber, Point anchor, Size preferredSize, out Rect bounds,
        Func<double, double>? measureHeight = null)
    {
        bounds = default;
        if (_layout is null || pageNumber < 1 || pageNumber > PageCount
            || preferredSize.Width <= 0 || preferredSize.Height <= 0)
            return false;

        const double inset = 8;
        const double gap = 18;
        var viewport = new Rect(Bounds.Size).Deflate(inset);
        if (viewport.Height < 64) return false;
        var page = GetPageBounds(pageNumber);
        // A clamped card must never outlive its anchor in the visible page.
        if (!page.Contains(anchor) || !new Rect(Viewport).Contains(anchor)) return false;
        var left = Math.Max(viewport.Left, page.Right + gap);
        var width = Math.Min(preferredSize.Width, viewport.Right - left);
        if (width < 144) return false;
        var height = Math.Min(measureHeight?.Invoke(width) ?? preferredSize.Height, viewport.Height);
        bounds = new Rect(left,
            Math.Clamp(anchor.Y - height / 2, viewport.Top, viewport.Bottom - height),
            width, height);
        var candidate = bounds;
        if (VisiblePageNumbers.Where(number => number != pageNumber)
            .Any(number => GetPageBounds(number).Intersects(candidate))) return false;
        return true;
    }

    private readonly List<(ReaderAnnotation Annotation, Action<Rect> Moved, Func<double, double> MeasureHeight)> _additionalAnnotationCallouts = [];
    private readonly List<(Point Anchor, Rect Bounds)> _additionalAnnotationConnectors = [];

    public void SetAdditionalAnnotationCallouts(
        IEnumerable<(ReaderAnnotation Annotation, Action<Rect> Moved, Func<double, double> MeasureHeight)> callouts)
    {
        _additionalAnnotationCallouts.Clear();
        _additionalAnnotationCallouts.AddRange(callouts);
        UpdateAnnotationCallout();
        InvalidateVisual();
    }

    public void DismissAnnotationCallout(Guid? additionalId = null)
    {
        if (additionalId is { } id)
            _additionalAnnotationCallouts.RemoveAll(item => item.Annotation.Id == id);
        else _annotationCalloutDismissed = true;
        UpdateAnnotationCallout();
        InvalidateVisual();
    }

    private bool _annotationCalloutVisible;
    private bool _annotationCalloutDismissed;
    private Size _annotationCalloutSize;
    private Func<double, double>? _annotationCalloutMeasureHeight;
    private Action<Rect>? _annotationCalloutMoved;
    private int _annotationCalloutPage;
    private Point _annotationCalloutPagePoint;
    private Size _annotationCalloutPageSize;
    private Size _annotationCalloutViewportSize;
    private int _annotationCalloutRotation;

    public void SetAnnotationCallout(Point anchor, Rect bounds, Action<Rect>? moved = null, int pageNumber = 0,
        Size? preferredSize = null, Func<double, double>? measureHeight = null)
    {
        _annotationCalloutPage = pageNumber > 0 ? pageNumber : PageNumber;
        var page = GetPageBounds(_annotationCalloutPage);
        var size = GetUnrotatedPageSize(page);
        var local = GetPageTransform(page).Invert().Transform(anchor);
        // Keep the anchor in document space, never in stale screen coordinates.
        _annotationCalloutPagePoint = new(local.X / size.Width, local.Y / size.Height);
        _annotationCalloutPageSize = page.Size;
        _annotationCalloutViewportSize = Bounds.Size;
        _annotationCalloutRotation = Rotation;
        _annotationCallout = (anchor, bounds);
        _annotationCalloutSize = preferredSize ?? bounds.Size;
        _annotationCalloutMeasureHeight = measureHeight;
        _annotationCalloutDismissed = false;
        _annotationCalloutMoved = moved;
        UpdateAnnotationCallout();
        InvalidateVisual();
    }

    private void UpdateAnnotationCallout()
    {
        if (_annotationCallout is not { } callout || _layout is null
            || _annotationCalloutPage < 1 || _annotationCalloutPage > PageCount) return;
        var page = GetPageBounds(_annotationCalloutPage);
        var size = GetUnrotatedPageSize(page);
        var anchor = GetPageTransform(page).Transform(new Point(
            _annotationCalloutPagePoint.X * size.Width,
            _annotationCalloutPagePoint.Y * size.Height));
        _annotationCalloutVisible = TryGetAnnotationCalloutBounds(
            _annotationCalloutPage, anchor, _annotationCalloutSize, out var bounds, _annotationCalloutMeasureHeight);
        _annotationCalloutVisible &= !_annotationCalloutDismissed;
        if (!_annotationCalloutVisible) bounds = callout.Bounds;
        _annotationCalloutPageSize = page.Size;
        _annotationCalloutViewportSize = Bounds.Size;
        _annotationCalloutRotation = Rotation;
        _annotationCallout = (anchor, bounds);
        _additionalAnnotationConnectors.Clear();
        var cards = new List<(Point Anchor, Rect Bounds, Action<Rect>? Moved, bool Primary)>();
        if (_annotationCalloutVisible) cards.Add((anchor, bounds, _annotationCalloutMoved, true));
        else _annotationCalloutMoved?.Invoke(default);
        foreach (var item in _additionalAnnotationCallouts)
        {
            if (TryGetAnnotationAnchor(item.Annotation, out var number, out var point)
                && TryGetAnnotationCalloutBounds(number, point, new Size(220, 68), out var cardBounds, item.MeasureHeight))
                cards.Add((point, cardBounds, item.Moved, false));
            else item.Moved(default);
        }
        cards.Sort((a, b) => a.Anchor.Y.CompareTo(b.Anchor.Y));
        var available = new Rect(Viewport).Deflate(8);
        // Preserve each note's measured height whenever the cards fit. Only
        // share the available height when necessary; each card can then scroll.
        var heightBudget = Math.Max(cards.Count, available.Height - 8 * (cards.Count - 1));
        var totalHeight = cards.Sum(card => card.Bounds.Height);
        var minimumHeight = cards.Count == 0 ? 0 : Math.Min(64, heightBudget / cards.Count);
        var minimumTotal = cards.Sum(card => Math.Min(minimumHeight, card.Bounds.Height));
        var heights = cards.Select(card => totalHeight <= heightBudget ? card.Bounds.Height
            : Math.Min(minimumHeight, card.Bounds.Height)
                + Math.Max(0, card.Bounds.Height - minimumHeight)
                    * Math.Max(0, heightBudget - minimumTotal) / Math.Max(1, totalHeight - minimumTotal)).ToArray();
        var remainingHeight = heights.Sum() + 8 * Math.Max(0, cards.Count - 1);
        var nextTop = available.Top;
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var height = heights[index];
            var top = Math.Clamp(card.Anchor.Y - height / 2, nextTop,
                Math.Max(nextTop, available.Bottom - remainingHeight));
            var positioned = cards.Count == 1 ? card.Bounds
                : new Rect(card.Bounds.X, top, card.Bounds.Width, height);
            nextTop = positioned.Bottom + 8;
            remainingHeight -= height + 8;
            if (card.Primary) _annotationCallout = (card.Anchor, positioned);
            else _additionalAnnotationConnectors.Add((card.Anchor, positioned));
            card.Moved?.Invoke(positioned);
        }
    }

    public void ClearAnnotationCallout()
    {
        foreach (var item in _additionalAnnotationCallouts) item.Moved(default);
        _additionalAnnotationCallouts.Clear();
        _additionalAnnotationConnectors.Clear();
        if (_annotationCallout is null) return;
        _annotationCallout = null;
        _annotationCalloutMoved = null;
        _annotationCalloutMeasureHeight = null;
        InvalidateVisual();
    }

    public void ScrollToPdfPoint(double x, double y)
    {
        if (_layout is null || PageNumber < 1 || PageNumber > _layout.Pages.Length)
            return;
        var page = GetPageBounds(PageNumber);
        var size = GetUnrotatedPageSize(page);
        var local = new Point(
            Math.Clamp(x, 0, 1) * size.Width,
            Math.Clamp(y, 0, 1) * size.Height);
        var position = GetPageTransform(page).Transform(local);
        SetPan(new(
            _pan.X + position.X - Viewport.Width * 0.2,
            _pan.Y + position.Y - Viewport.Height * 0.25),
            trackPage: false);
    }
}
