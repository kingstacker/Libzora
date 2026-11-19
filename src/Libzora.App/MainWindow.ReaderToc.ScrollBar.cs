using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Libzora;

/// <summary>
/// TOC scroll bar attach and auto-hide.
/// </summary>
public partial class MainWindow
{
    private void QueueReaderTocScrollBarAttach()
    {
        if (_readerTocScrollBarAttachPending) return;
        _readerTocScrollBarAttachPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _readerTocScrollBarAttachPending = false;
            AttachReaderTocScrollBar();
        }, DispatcherPriority.Loaded);
    }

    private void AttachReaderTocScrollBar()
    {
        var scrollViewer = FindDescendants<ScrollViewer>(ReaderTocList).FirstOrDefault();
        var scrollBar = FindDescendants<ScrollBar>(ReaderTocList)
            .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical);
        if (scrollViewer is null || scrollBar is null) return;

        if (ReferenceEquals(_readerTocScrollViewer, scrollViewer)
            && ReferenceEquals(_readerTocScrollBar, scrollBar))
        {
            return;
        }

        DetachReaderTocScrollBar();
        _readerTocScrollViewer = scrollViewer;
        _readerTocScrollBar = scrollBar;
        scrollViewer.Classes.Add(ReaderTocScrollClass);
        scrollViewer.Classes.Remove(ReaderTocScrollingClass);
        scrollViewer.ScrollChanged += ReaderTocScrollViewer_ScrollChangedForBar;
        scrollBar.PointerEntered += ReaderTocScrollBar_PointerEntered;
        scrollBar.PointerExited += ReaderTocScrollBar_PointerExited;
    }

    private void DetachReaderTocScrollBar()
    {
        if (_readerTocScrollViewer is { } scrollViewer)
        {
            scrollViewer.ScrollChanged -= ReaderTocScrollViewer_ScrollChangedForBar;
            scrollViewer.Classes.Remove(ReaderTocScrollClass);
            scrollViewer.Classes.Remove(ReaderTocScrollingClass);
        }

        if (_readerTocScrollBar is { } scrollBar)
        {
            scrollBar.PointerEntered -= ReaderTocScrollBar_PointerEntered;
            scrollBar.PointerExited -= ReaderTocScrollBar_PointerExited;
        }

        _readerTocScrollViewer = null;
        _readerTocScrollBar = null;
        _readerTocScrollBarHideTimer?.Stop();
    }

    private void ReaderTocScrollViewer_ScrollChangedForBar(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y != 0) ShowReaderTocScrollBar();
        if (e.OffsetDelta.Y != 0 && _readerTocScrollViewer is { } scrollViewer && scrollViewer.Offset.Y <= 0.5)
            ScheduleReaderTocTopSpacerRepair();
    }

    private void ReaderTocScrollBar_PointerEntered(object? sender, PointerEventArgs e)
    {
        ShowReaderTocScrollBar();
    }

    private void ReaderTocScrollBar_PointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleReaderTocScrollBarHide();
    }

    private void ShowReaderTocScrollBar()
    {
        if (_readerTocScrollViewer is not { } scrollViewer) return;
        scrollViewer.Classes.Add(ReaderTocScrollingClass);
        EnsureReaderTocScrollBarHideTimer().Stop();
        EnsureReaderTocScrollBarHideTimer().Start();
    }

    private void ScheduleReaderTocScrollBarHide()
    {
        if (_readerTocScrollBar is not { IsPointerOver: false }
            || _readerTocScrollViewer is null)
        {
            return;
        }

        EnsureReaderTocScrollBarHideTimer().Stop();
        EnsureReaderTocScrollBarHideTimer().Start();
    }

    private DispatcherTimer EnsureReaderTocScrollBarHideTimer()
    {
        if (_readerTocScrollBarHideTimer is not null) return _readerTocScrollBarHideTimer;
        _readerTocScrollBarHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ReaderTocScrollBarIdleHideMs)
        };
        _readerTocScrollBarHideTimer.Tick += (_, _) =>
        {
            _readerTocScrollBarHideTimer!.Stop();
            if (_readerTocScrollViewer is not { } scrollViewer) return;
            if (_readerTocScrollBar is { IsPointerOver: true })
                scrollViewer.Classes.Add(ReaderTocScrollingClass);
            else
                scrollViewer.Classes.Remove(ReaderTocScrollingClass);
        };
        return _readerTocScrollBarHideTimer;
    }
}
