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
/// Range selection, search and speech highlight.
/// </summary>
public sealed partial class NativePdfReaderHost
{
    public void SelectRange(int start, int end)
    {
        _selectionRanges = null;
        var text = PageContent?.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        _selectionPage = PageNumber;
        _selectionStart = start;
        _selectionEnd = end;
        InvalidateVisual();
        EmitSelection();
    }

    /// <summary>
    /// Selects the character or word under a long press. Highlights keep their
    /// tap behavior, and the press must land on a real glyph.
    /// </summary>
    public bool SelectWordAt(double x, double y)
    {
        var point = new Point(x, y);
        var page = PageAtPoint(point);
        if (page == 0 || page != PageNumber || _pointAnnotationMode || _handToolMode
            || AnnotationAt(point) is not null)
        {
            return false;
        }

        var content = GetPageContent(page);
        if (content is null || string.IsNullOrEmpty(content.Text))
        {
            return false;
        }

        var pageBounds = GetPageBounds(page);
        var local = GetPageTransform(pageBounds).Invert().Transform(point);
        var text = content.Text;
        PdfTextGlyph? best = null;
        var bestDistance = double.MaxValue;
        foreach (var glyph in content.Glyphs)
        {
            if (glyph.Length <= 0 || glyph.Offset < 0 || glyph.Offset >= text.Length)
            {
                continue;
            }

            var length = Math.Min(glyph.Length, text.Length - glyph.Offset);
            if (MemoryExtensions.IsWhiteSpace(text.AsSpan(glyph.Offset, length)))
            {
                continue;
            }

            var rect = GetLocalGlyphBounds(page, glyph.Bounds, pageBounds);
            var dx = Math.Max(0, Math.Max(rect.Left - local.X, local.X - rect.Right));
            var dy = Math.Max(0, Math.Max(rect.Top - local.Y, local.Y - rect.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            best = glyph;
            bestDistance = distance;
        }

        if (best is null || bestDistance > 256) return false;

        var (start, end) = ReaderWordSelection.GetRange(text, best.Offset);
        if (end <= start) return false;
        SelectRange(start, end);
        _wordSelectionActive = true;
        return true;
    }

    private void RebuildSearch(string query)
    {
        var previousIndex = _searchPage == PageNumber ? _searchIndex : 0;
        _searchQuery = query;
        _searchPage = PageNumber;
        _search.Clear();
        _searchIndex = -1;
        var text = PageContent?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return;
        for (var start = 0; start < text.Length;)
        {
            var match = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (match < 0) break;
            _search.Add((match, query.Length));
            start = match + query.Length;
        }
        if (_search.Count > 0) _searchIndex = Math.Clamp(previousIndex, 0, _search.Count - 1);
    }

    public (int Count, int Index) Find(string query, int? offset = null)
    {
        _searchIndex = 0;
        RebuildSearch(query);
        if (_search.Count > 0)
        {
            _searchIndex = offset is { } target ? Math.Max(0, _search.FindIndex(hit => hit.Start >= target)) : 0;
            ScrollToOffset(_search[_searchIndex].Start);
        }
        InvalidateVisual();
        return (_search.Count, _searchIndex);
    }

    public (int Count, int Index) NextSearch(int direction)
    {
        if (_search.Count > 0)
        {
            _searchIndex = (_searchIndex + Math.Sign(direction) + _search.Count) % _search.Count;
            ScrollToOffset(_search[_searchIndex].Start);
            InvalidateVisual();
        }
        return (_search.Count, _searchIndex);
    }

    public void ClearSearch() { _searchQuery = string.Empty; _search.Clear(); _searchIndex = -1; InvalidateVisual(); }
    public void SetSpeechHighlight(int start, int length)
    {
        _speechHighlight = (PageNumber, start, length);
        ScrollToOffset(start);
        InvalidateVisual();
    }
    public void ClearSpeechHighlight() { _speechHighlight = null; InvalidateVisual(); }

    public void ScrollToSearchHit(int index)
    {
        if (_search.Count == 0) return;
        _searchIndex = (index % _search.Count + _search.Count) % _search.Count;
        ScrollToOffset(_search[_searchIndex].Start);
        InvalidateVisual();
    }

    public void ScrollToOffset(int offset)
    {
        var glyph = PageContent?.Glyphs.FirstOrDefault(item => item.Offset + item.Length > offset);
        if (glyph is null || _layout is null) return;
        var page = _layout.Pages[PageNumber - 1];
        var bounds = GetLocalGlyphBounds(PageNumber, glyph.Bounds, page).TransformToAABB(GetPageTransform(page));
        SetPan(new(bounds.X - Viewport.Width * 0.2, bounds.Y - Viewport.Height * 0.25), trackPage: false);
    }
}
