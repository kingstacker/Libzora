using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Search highlights and navigation fragments.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private static int CommonSuffixLength(string left, string right)
    {
        var count = 0;
        while (count < left.Length
            && count < right.Length
            && string.Compare(
                left,
                left.Length - count - 1,
                right,
                right.Length - count - 1,
                1,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            count++;
        }

        return count;
    }

    public void SetSearchHighlights(List<(int Start, int Length)>? hits, int? focusIndex)
    {
        _searchHits = hits;
        _focusSearchHit = focusIndex;
        if (focusIndex is { } index && hits is { Count: > 0 } && index >= 0 && index < hits.Count)
        {
            ScrollToOffset(hits[index].Start);
        }

        _bitmapDirty = true;
        InvalidateVisual();
    }

    public void ScrollToSearchHit(int index)
    {
        if (_searchHits is { Count: > 0 } && index >= 0 && index < _searchHits.Count)
        {
            if (_focusSearchHit != index)
            {
                // Moving the focus restyles the current hit (inverted) and
                // the previous one (muted), so repaint even when the page
                // does not change.
                _focusSearchHit = index;
                _bitmapDirty = true;
                InvalidateVisual();
            }

            ScrollToOffset(_searchHits[index].Start);
        }
    }

    public void SetNavigationFragments(IEnumerable<string> fragments)
    {
        _navigationFragments = fragments.Select(NormalizeFragment)
            .Where(fragment => fragment.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        _navigationFragmentsConfigured = true;
    }

    private IReadOnlySet<string>? SnapshotNavigationFragments() =>
        _navigationFragmentsConfigured
            ? new HashSet<string>(_navigationFragments, StringComparer.Ordinal)
            : null;

    public string? GetCurrentNavigationFragment()
    {
        if (_layout is null || _layout.Pages.Count == 0 || _content is null) return null;

        if (_navigationTarget is { } target)
        {
            if (ReferenceEquals(target.Layout, _layout) && target.Page == _pageIndex
                && Math.Abs(target.ScrollOffset - _scrollOffset) < 0.001
                && _navigationFragments.Contains(target.Fragment, StringComparer.Ordinal))
                return target.Fragment;
            _navigationTarget = null;
        }

        // Follow the leading visible text, including a partially scrolled
        // page. Later anchors on this page or the second page of a spread
        // must not move the TOC ahead of the reader.
        var position = CaptureViewportReadingAnchor();
        string? current = null;
        var currentPage = -1;
        var currentOffset = -1;
        foreach (var fragment in _navigationFragments)
        {
            var page = _layout.GetPageIndexOfFragment(fragment);
            if (page < 0 || page > position.PageIndex) continue;
            var offset = _content.FragmentTextOffsets.TryGetValue(fragment, out var textOffset) ? textOffset : -1;
            if (page == position.PageIndex && position.TextOffset >= 0 && offset > position.TextOffset) continue;
            if (page < currentPage || page == currentPage && offset < currentOffset) continue;
            current = fragment;
            currentPage = page;
            currentOffset = offset;
        }
        return current;
    }

    public ReaderContentPosition? CaptureContentPosition()
    {
        if (_disposed || _composePending || _layout is not { Pages.Count: > 0 }
            || !_navigationTask.IsCompletedSuccessfully || !_navigationTask.Result) return null;
        var anchor = ViewportReadingPositionMatches()
            ? _viewportReadingAnchor ?? CaptureViewportReadingAnchor()
            : CaptureViewportReadingAnchor();
        return new ReaderContentPosition
        {
            TextOffset = anchor.TextOffset,
            ImageIndex = anchor.ImageIndex,
            PageIndex = anchor.PageIndex,
            RunY = anchor.RunY,
            SliceFraction = anchor.SliceFraction
        }.WithTextAnchor(_content?.BodyText);
    }

    public bool RestoreContentPosition(ReaderContentPosition? position)
    {
        _navigationTarget = null;
        if (_disposed || ReaderContentPosition.Validate(position) is not { } valid) return false;
        _pendingFragment = null;
        _pendingSeekToEnd = false;
        _pendingScrollOffset = null;
        _pendingRestoreOffset = null;
        _pendingRestorePage = null;
        _pendingRestoreRatio = null;
        _pendingReadingAnchor = ToViewportReadingAnchor(valid);
        ApplyPendingPosition();
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
        return true;
    }

    public bool IsContentPositionVisible(ReaderContentPosition? position)
    {
        if (_disposed || _composePending || _layout is not { Pages.Count: > 0 }
            || ReaderContentPosition.Validate(position) is not { } valid) return false;
        var (page, image) = ResolveViewportReadingAnchor(ToViewportReadingAnchor(valid));
        if (!IsScroll) return page >= _pageIndex && page <= _pageIndex + (IsSpread ? 1 : 0);
        var rect = image?.Rect ?? _layout.GetCharRect(page, valid.TextOffset);
        var y = page * _layout.Pages[0].Height + (rect?.Top ?? valid.SliceFraction * _layout.Pages[0].Height);
        var bottom = rect is { } bounds ? y + bounds.Height : y;
        return bottom >= _scrollOffset && y < _scrollOffset + Bounds.Height;
    }

    public (double Position, double Ratio, double ScrollWidth, double ScrollHeight, double ClientWidth, double ClientHeight) GetScrollState()
    {
        var viewportWidth = Math.Max(1, Bounds.Width);
        var viewportHeight = Math.Max(1, Bounds.Height);
        if (IsScroll)
        {
            var total = viewportHeight * Math.Max(1, PageCount);
            var maximum = Math.Max(0, total - viewportHeight);
            var top = Math.Clamp(_scrollOffset, 0, maximum);
            return (top, maximum > 0 ? Math.Clamp(top / maximum, 0, 1) : 0, viewportWidth, total, viewportWidth, viewportHeight);
        }

        if (Vertical)
        {
            var start = _layout is not null
                && _pageIndex >= 0
                && _pageIndex < _layout.Pages.Count
                && _layout.Pages[_pageIndex].TextStartOffset >= 0
                ? _layout.Pages[_pageIndex].TextStartOffset
                : -1;
            var total = Math.Max(1, _layout?.BodyTextLength ?? 1);
            var ratio = start >= 0 ? Math.Clamp((double)start / total, 0, 1)
                : PageCount > 1 ? (double)_pageIndex / (PageCount - 1) : 0;
            return (Math.Max(0, start), ratio, total, viewportHeight, viewportWidth, viewportHeight);
        }

        var stride = Math.Max(1, ComposePageWidth(viewportWidth));
        var scrollWidth = stride * Math.Max(1, PageCount);
        var left = _pageIndex * stride;
        var maxLeft = Math.Max(0, scrollWidth - stride);
        return (left, maxLeft > 0 ? Math.Clamp(left / maxLeft, 0, 1) : 0, scrollWidth, viewportHeight, stride, viewportHeight);
    }

    private double PixelScrollPosition
    {
        get
        {
            if (IsScroll)
            {
                return _scrollOffset;
            }

            if (Vertical)
            {
                return GetScrollState().Position;
            }

            return CurrentPage * ComposePageWidth(Math.Max(1, Bounds.Width));
        }
    }
}
