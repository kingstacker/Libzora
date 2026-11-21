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
/// Message emission, scroll bar and coordinate mapping.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private void Emit(object message)
    {
        WebMessageReceived?.Invoke(
            this,
            new ReaderWebMessageReceivedEventArgs(JsonSerializer.Serialize(message)));
    }

    private void EmitScroll()
    {
        SyncScrollBar();
        var state = GetScrollState();
        // Scroll mode scrolls vertically like a web page, so the position is
        // reported as scrollTop; paginated modes keep the horizontal bridge
        // convention.
        var scrollMode = IsScroll;
        Emit(new
        {
            type = "scroll",
            top = scrollMode ? state.Position : 0.0,
            left = scrollMode ? 0.0 : state.Position,
            scrollWidth = state.ScrollWidth,
            scrollHeight = state.ScrollHeight,
            clientWidth = state.ClientWidth,
            clientHeight = state.ClientHeight,
            fragment = GetCurrentNavigationFragment(),
        });
    }

    /// <summary>
    /// Translates a control-space point into the composed page under it and
    /// that page's local coordinates. Single-page presents the current page;
    /// the spread splits the viewport into halves; scroll mode stacks pages
    /// vertically under the scroll offset.
    /// </summary>
    private (int Page, SKPoint Local)? MapPointToPage(Point position)
    {
        if (_layout is null || _layout.Pages.Count == 0)
        {
            return null;
        }

        if (_mangaZoom > 1.01)
        {
            position = new Point(
                (position.X + _mangaPan.X) / _mangaZoom,
                (position.Y + _mangaPan.Y) / _mangaZoom);
        }

        switch (_presentation)
        {
            case ReaderPresentation.Spread:
            {
                var half = Math.Max(1, Bounds.Width / 2);
                var right = position.X >= half;
                var secondPage = _settings.MangaMode && _settings.MangaRightToLeft ? !right : right;
                var page = Math.Min(_pageIndex + (secondPage ? 1 : 0), _layout.Pages.Count - 1);
                var localX = right ? position.X - half : position.X;
                return (page, new SKPoint((float)localX, (float)position.Y));
            }
            case ReaderPresentation.Scroll:
            {
                var pageHeight = Math.Max(1, Bounds.Height);
                var contentY = Math.Max(0, _scrollOffset) + position.Y;
                var page = Math.Clamp((int)(contentY / pageHeight), 0, _layout.Pages.Count - 1);
                return (page, new SKPoint((float)position.X, (float)(contentY - page * pageHeight)));
            }
            default:
                return (_pageIndex, new SKPoint((float)position.X, (float)position.Y));
        }
    }

    /// <summary>Control-space origin of one composed page in the viewport.</summary>
    private Point PageOrigin(int page)
    {
        switch (_presentation)
        {
            case ReaderPresentation.Spread:
            {
                var half = Math.Max(1, Bounds.Width / 2);
                var slot = page - _pageIndex;
                if (_settings.MangaMode && _settings.MangaRightToLeft)
                    return new Point(slot <= 0 ? half : 0, 0);
                return new Point(Math.Max(0, slot) * half, 0);
            }
            case ReaderPresentation.Scroll:
                return new Point(0, page * Math.Max(1, Bounds.Height) - Math.Max(0, _scrollOffset));
            default:
                return new Point(0, 0);
        }
    }

    private int VisibleFirstPageIndex =>
        IsScroll ? Math.Clamp((int)(Math.Max(0, _scrollOffset) / Math.Max(1, Bounds.Height)), 0, Math.Max(0, PageCount - 1)) : _pageIndex;

    /// <summary>
    /// Keeps the continuous-mode scrollbar in step with the offset. Runs on
    /// every scroll report; state changes that would invalidate layout are
    /// deferred to a dispatcher pass because sync can be requested from
    /// arrange, where invalidations are illegal.
    /// </summary>
    private void SyncScrollBar()
    {
        var desiredVisible = IsScroll && _layout is not null && _layout.Pages.Count > 0 && MaxScrollOffset > 0.5;
        if (_scrollBar.IsVisible != desiredVisible
            || Math.Abs(_scrollBar.Maximum - MaxScrollOffset) > 0.5)
        {
            _ = Dispatcher.UIThread.InvokeAsync(ApplyScrollBarState, DispatcherPriority.Background);
            return;
        }

        if (desiredVisible)
        {
            ApplyScrollBarState();
        }
    }

    private void ApplyScrollBarState()
    {
        if (_disposed)
        {
            return;
        }

        var visible = IsScroll && _layout is not null && _layout.Pages.Count > 0 && MaxScrollOffset > 0.5;
        _scrollBar.IsVisible = visible;
        if (!visible)
        {
            return;
        }

        _suppressScrollBarSync = true;
        try
        {
            _scrollBar.Maximum = MaxScrollOffset;
            _scrollBar.ViewportSize = Math.Max(1, Bounds.Height);
            _scrollBar.LargeChange = Math.Max(1, Bounds.Height);
            _scrollBar.SmallChange = 48;
            _scrollBar.Value = Math.Clamp(_scrollOffset, 0, MaxScrollOffset);
        }
        finally
        {
            _suppressScrollBarSync = false;
        }
    }

    // The scrollbar is a visual child of this control, so its pointer input
    // bubbles through here; the reading-surface handlers must ignore it.
    private bool IsOverScrollBar(Point position) =>
        IsScroll && _scrollBar.IsVisible && position.X >= Bounds.Width - 20;

    protected override Size MeasureOverride(Size availableSize)
    {
        _scrollBar.Measure(new Size(12, Math.Max(0, availableSize.Height)));
        return base.MeasureOverride(availableSize);
    }
}
