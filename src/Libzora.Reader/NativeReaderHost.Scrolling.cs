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
/// Scrolling, seeking, page turns and annotations setup.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private static ReaderPresentation DerivePresentation(ReaderLayoutSettings settings) =>
        settings.VerticalWriting
            ? ReaderPresentation.Single
            : settings.FlowMode == 0
                ? ReaderPresentation.Scroll
                : settings.TwoPageMode ? ReaderPresentation.Spread : ReaderPresentation.Single;

    /// <summary>Chapter-boundary positioning: the first page or the last full page.</summary>
    public void SeekToBoundary(bool toEnd)
    {
        _navigationTarget = null;
        if (_layout is null || _layout.Pages.Count == 0)
        {
            _pendingSeekToEnd = toEnd;
            return;
        }

        if (IsScroll)
        {
            if (toEnd && IsContinuousProseScroll)
            {
                var pageHeight = Math.Max(1.0, Bounds.Height);
                SetScrollOffset(Math.Max(0, ComputeContinuousContentHeight() + 48.0 - pageHeight));
            }
            else
            {
                SetScrollOffset(toEnd ? MaxScrollOffset : 0);
            }
            return;
        }

        _pageIndex = toEnd ? Math.Max(0, _layout.Pages.Count - 1) : 0;
        if (IsSpread)
        {
            _pageIndex -= _pageIndex % 2;
        }

        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
    }

    public void ScrollToOffset(int offset)
    {
        _navigationTarget = null;
        if (_layout is null)
        {
            _pendingRestoreOffset = offset;
            return;
        }

        var page = _layout.GetPageIndexOfOffset(offset);
        if (IsScroll)
        {
            SetScrollOffset(ComputeScrollOffsetForTextOffset(page, offset));
            return;
        }

        if (IsSpread && page > 0)
        {
            page -= page % 2;
        }

        if (page >= 0 && page != _pageIndex)
        {
            _pageIndex = page;
            _bitmapDirty = true;
            InvalidateVisual();
            EmitScroll();
        }
    }

    public async Task RevealFootnoteAsync(string fragmentId)
    {
        if (Source is not { IsFile: true } source || _disposed) return;
        fragmentId = NormalizeFragment(fragmentId);
        if (fragmentId != _loadedFootnoteFragment
            && !await NavigateCore(source.LocalPath, fragmentId)) return;
        ScrollToFragment(fragmentId);
    }

    public void ScrollToFragment(string fragmentId)
    {
        _navigationTarget = null;
        fragmentId = NormalizeFragment(fragmentId);
        if (_layout is null)
        {
            _pendingFragment = fragmentId;
            return;
        }

        var page = _layout.GetPageIndexOfFragment(fragmentId);
        if (IsScroll)
        {
            SetScrollOffset(Math.Max(0, page) * Math.Max(1, Bounds.Height));
            return;
        }

        if (IsSpread && page > 0)
        {
            page -= page % 2;
        }

        if (page >= 0)
        {
            if (page != _pageIndex)
            {
                _pageIndex = page;
                _bitmapDirty = true;
                InvalidateVisual();
            }
            // The clicked heading may be below the first line or on the
            // second sheet of this spread. Keep that explicit destination
            // until the viewport moves, even if no physical page turn occurs.
            _navigationTarget = (_layout, _pageIndex, _scrollOffset, fragmentId);
            EmitScroll();
        }
    }

    /// <summary>
    /// Seeks by a persisted horizontal pixel scroll (page stride × index).
    /// In vertical writing the persisted value is a character offset instead;
    /// in horizontal scroll mode it is the absolute content offset.
    /// </summary>
    public void SeekToPixelScroll(double pixelScroll)
    {
        _navigationTarget = null;
        if (IsScroll)
        {
            SetScrollOffset(pixelScroll);
            return;
        }

        if (Vertical)
        {
            ScrollToOffset((int)Math.Max(0, pixelScroll));
            return;
        }

        _pendingRestorePage = PageFromPixelScroll(pixelScroll);
        ApplyPendingPosition();
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
    }

    public void SeekToRatio(double ratio)
    {
        _navigationTarget = null;
        if (_layout is null || _layout.Pages.Count == 0)
        {
            return;
        }

        if (IsScroll)
        {
            SetScrollOffset(Math.Clamp(ratio, 0, 1) * MaxScrollOffset);
            return;
        }

        var page = (int)Math.Clamp(Math.Round(ratio * (_layout.Pages.Count - 1)), 0, _layout.Pages.Count - 1);
        if (IsSpread)
        {
            page -= page % 2;
        }

        if (page != _pageIndex)
        {
            _pageIndex = page;
            _bitmapDirty = true;
            InvalidateVisual();
            EmitScroll();
        }
    }

    /// <summary>True when the page turn stays inside the chapter.</summary>
    public bool CanTurn(int direction)
    {
        if (IsScroll)
        {
            // Continuous mode scrolls instead of turning.
            return false;
        }

        if (_layout is null || _layout.Pages.Count == 0)
        {
            return false;
        }

        var target = _pageIndex + direction * PageStep;
        return target >= 0 && target < _layout.Pages.Count;
    }

    /// <summary>Performs an in-chapter page turn. Returns false at a chapter edge.</summary>
    public bool TurnPage(int direction)
    {
        if (!CanTurn(direction))
        {
            return false;
        }

        ResetMangaZoom();
        _pageIndex += direction * PageStep;
        if (IsSpread)
        {
            _pageIndex -= _pageIndex % 2;
        }

        _bitmapDirty = true;
        InvalidateVisual();
        if (_settings.MangaMode) PrefetchVisibleImages();
        EmitScroll();
        return true;
    }

    /// <summary>Scrolls the continuous surface by wheel/keyboard pixels.</summary>
    public void ScrollByPixel(double delta)
    {
        if (!IsScroll)
        {
            return;
        }

        ScrollBarAutoHide.Show(_scrollBar);
        var hasNextPreview = IsContinuousProseScroll && _scrollNextChapterLayout is { Pages.Count: > 0 };
        var rawNext = _scrollOffset + delta;
        var next = hasNextPreview ? ClampLiveScrollOffset(rawNext) : ClampScrollOffset(rawNext);
        var edge = next <= 0 && delta < 0
            ? -1
            : (hasNextPreview ? rawNext >= ContinuousNextChapterStartY : next >= MaxScrollOffset) && delta > 0
                ? 1
                : 0;
        var carryOffset = edge > 0 && hasNextPreview
            ? Math.Max(0.0, rawNext - ContinuousNextChapterStartY)
            : 0.0;
        var carryUpDelta = edge < 0 && IsContinuousProseScroll
            ? Math.Max(4.0, -rawNext)
            : 0.0;

        if (_activeHandoffDirection == 1 && rawNext > next && delta > 0)
        {
            _activeHandoffOverflowDown += rawNext - next;
        }
        else if (_activeHandoffDirection == -1 && rawNext < 0 && delta < 0)
        {
            _activeHandoffCarryUp = Math.Min(ScrollPageHeight * 1.5, _activeHandoffCarryUp + (-rawNext));
        }

        SetScrollOffset(next, allowNextChapterRunway: hasNextPreview);
        if (edge != 0 && _activeHandoffDirection != edge)
        {
            // Chapter edge reached while scrolling: hand the direction to the
            // host pipeline so the next/previous chapter takes over, exactly
            // like the WebView continuous reader's edge report.
            Emit(new { type = "continuousEdge", direction = edge, carryOffset, carryUpDelta });
        }
    }

    private void SetScrollOffset(double value, bool allowNextChapterRunway = false)
    {
        var next = allowNextChapterRunway ? ClampLiveScrollOffset(value) : ClampScrollOffset(value);
        if (Math.Abs(next - _scrollOffset) < 0.01)
        {
            _scrollOffset = next;
            return;
        }

        _scrollOffset = next;
        _bitmapDirty = true;
        InvalidateVisual();
        if (_settings.MangaMode)
        {
            var slice = (int)(next / Math.Max(1, Bounds.Height));
            if (slice != _lastPrefetchedSlice)
            {
                _lastPrefetchedSlice = slice;
                PrefetchVisibleImages();
            }
        }
        ScheduleScrollEmit();
    }

    /// <summary>
    /// Scroll input can arrive dozens of times per second. Serializing and
    /// dispatching every event made the UI thread do JSON + TOC work between
    /// frames; coalesce to ~30 fps and always emit the final resting position.
    /// </summary>
    private void ScheduleScrollEmit()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        if ((now - _lastScrollEmitUtc).TotalMilliseconds >= 33)
        {
            _lastScrollEmitUtc = now;
            EmitScroll();
            return;
        }

        if (_scrollEmitTimer is null)
        {
            _scrollEmitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _scrollEmitTimer.Tick += (_, _) =>
            {
                _scrollEmitTimer?.Stop();
                _lastScrollEmitUtc = DateTime.UtcNow;
                EmitScroll();
            };
        }
        _scrollEmitTimer.Stop();
        _scrollEmitTimer.Start();
    }
}
