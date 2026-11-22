using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Libzora;

/// <summary>
/// Horizontal drag-scrolling, edge rubber-band overscroll, and bounce-back
/// animation for the expanded series strip.
/// </summary>
public partial class MainWindow
{
    private const double SeriesShelfDragThreshold = 6d;
    private const double SeriesShelfMaxOverscroll = 112d;
    private const double SeriesShelfOverscrollResistance = 0.55d;

    private bool _seriesShelfDragScrollAttached;
    private bool _seriesShelfDragPressed;
    private bool _seriesShelfDragActive;
    private bool _seriesShelfSuppressTileClick;
    private Point _seriesShelfDragStartPoint;
    private double _seriesShelfDragStartOffsetX;
    private double _seriesShelfDragStartVirtualOverscrollX;
    private double _seriesShelfDragDeadzoneX;
    private double _seriesShelfOverscrollX;
    private double _seriesShelfBounceStartX;
    private double _seriesShelfBounceDurationMs;
    private DispatcherTimer? _seriesShelfBounceTimer;
    private Stopwatch? _seriesShelfBounceStopwatch;

    private void EnsureSeriesShelfDragScrollAttached()
    {
        if (_seriesShelfDragScrollAttached) return;
        _seriesShelfDragScrollAttached = true;
        SeriesShelfScrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            SeriesShelfScrollViewer_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        SeriesShelfScrollViewer.AddHandler(
            InputElement.PointerMovedEvent,
            SeriesShelfScrollViewer_PointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        SeriesShelfScrollViewer.AddHandler(
            InputElement.PointerReleasedEvent,
            SeriesShelfScrollViewer_PointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        SeriesShelfScrollViewer.PointerCaptureLost += SeriesShelfScrollViewer_PointerCaptureLost;
    }

    private void ResetSeriesShelfDragState()
    {
        StopSeriesShelfBounceAnimation();
        _seriesShelfDragPressed = false;
        _seriesShelfDragActive = false;
        _seriesShelfSuppressTileClick = false;
        _seriesShelfDragStartVirtualOverscrollX = 0d;
        _seriesShelfDragDeadzoneX = 0d;
        SetSeriesShelfOverscroll(0d);
        SeriesShelfScrollViewer.Cursor = Cursor.Default;
    }

    private void SeriesShelfScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!SeriesShelfOverlay.IsVisible
            || !e.GetCurrentPoint(SeriesShelfScrollViewer).Properties.IsLeftButtonPressed)
            return;

        StopSeriesShelfBounceAnimation();
        _seriesShelfDragPressed = true;
        _seriesShelfDragActive = false;
        _seriesShelfSuppressTileClick = false;
        _seriesShelfDragStartPoint = e.GetPosition(SeriesShelfScrollViewer);
        _seriesShelfDragStartOffsetX = SeriesShelfScrollViewer.Offset.X;
        _seriesShelfDragStartVirtualOverscrollX = UnapplySeriesShelfRubberBand(_seriesShelfOverscrollX);
        _seriesShelfDragDeadzoneX = 0d;
    }

    private void SeriesShelfScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seriesShelfDragPressed || !SeriesShelfOverlay.IsVisible) return;
        if (!e.GetCurrentPoint(SeriesShelfScrollViewer).Properties.IsLeftButtonPressed)
        {
            FinishSeriesShelfDrag(e.Pointer, suppressClick: false);
            return;
        }

        var current = e.GetPosition(SeriesShelfScrollViewer);
        var dx = current.X - _seriesShelfDragStartPoint.X;
        var maxX = Math.Max(0d, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        if (!_seriesShelfDragActive)
        {
            if (Math.Abs(dx) < SeriesShelfDragThreshold || SeriesShelfItemsHost.Children.Count == 0)
                return;
            _seriesShelfDragActive = true;
            _seriesShelfSuppressTileClick = true;
            _seriesShelfPendingCenterIndex = null;
            _seriesShelfDragDeadzoneX = Math.CopySign(SeriesShelfDragThreshold, dx);
            SeriesShelfScrollViewer.Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(SeriesShelfScrollViewer);
        }

        var effectiveDx = dx - _seriesShelfDragDeadzoneX;
        var rawTargetX = _seriesShelfDragStartOffsetX - _seriesShelfDragStartVirtualOverscrollX - effectiveDx;
        var clampedX = Math.Clamp(rawTargetX, 0d, maxX);
        var dampedOverscroll = ApplySeriesShelfRubberBand(clampedX - rawTargetX);

        SeriesShelfScrollViewer.Offset = new Vector(clampedX, 0d);
        SetSeriesShelfOverscroll(dampedOverscroll);
        e.Handled = true;
    }

    private void SeriesShelfScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seriesShelfDragPressed) return;
        var wasDragging = _seriesShelfDragActive;
        FinishSeriesShelfDrag(e.Pointer, suppressClick: wasDragging);
        if (wasDragging)
            e.Handled = true;
    }

    private void SeriesShelfScrollViewer_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_seriesShelfDragPressed && !_seriesShelfDragActive) return;
        FinishSeriesShelfDrag(pointer: null, suppressClick: _seriesShelfDragActive);
    }

    private void FinishSeriesShelfDrag(IPointer? pointer, bool suppressClick)
    {
        _seriesShelfDragPressed = false;
        _seriesShelfDragActive = false;
        if (!suppressClick)
            _seriesShelfSuppressTileClick = false;
        SeriesShelfScrollViewer.Cursor = Cursor.Default;
        if (pointer is not null && ReferenceEquals(pointer.Captured, SeriesShelfScrollViewer))
            pointer.Capture(null);

        if (Math.Abs(_seriesShelfOverscrollX) > 0.25d)
            StartSeriesShelfBounceBack();
        else
            SetSeriesShelfOverscroll(0d);
    }

    private void SeriesShelfScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _seriesShelfPendingCenterIndex = null;
        if (e.Handled || Math.Abs(e.Delta.Y) <= Math.Abs(e.Delta.X) || e.Delta.Y == 0)
            return;
        var maxX = Math.Max(0, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        if (maxX <= 0) return;
        StopSeriesShelfBounceAnimation();
        SetSeriesShelfOverscroll(0d);
        var current = SeriesShelfScrollViewer.Offset;
        var targetX = Math.Clamp(current.X - e.Delta.Y * 48, 0, maxX);
        SeriesShelfScrollViewer.Offset = new Vector(targetX, current.Y);
        e.Handled = true;
    }

    private static double ApplySeriesShelfRubberBand(double rawOverscroll)
    {
        if (Math.Abs(rawOverscroll) <= 0.001d) return 0d;
        var distance = Math.Abs(rawOverscroll);
        var damped = (distance * SeriesShelfOverscrollResistance * SeriesShelfMaxOverscroll)
            / (SeriesShelfMaxOverscroll + SeriesShelfOverscrollResistance * distance);
        return Math.CopySign(damped, rawOverscroll);
    }

    private static double UnapplySeriesShelfRubberBand(double dampedOverscroll)
    {
        if (Math.Abs(dampedOverscroll) <= 0.001d) return 0d;
        var magnitude = Math.Min(Math.Abs(dampedOverscroll), SeriesShelfMaxOverscroll - 1d);
        var raw = (SeriesShelfMaxOverscroll * magnitude)
            / (SeriesShelfOverscrollResistance * (SeriesShelfMaxOverscroll - magnitude));
        return Math.CopySign(raw, dampedOverscroll);
    }

    private void SetSeriesShelfOverscroll(double overscrollX)
    {
        if (Math.Abs(overscrollX) <= 0.05d)
        {
            _seriesShelfOverscrollX = 0d;
            SeriesShelfItemsHost.RenderTransform = null;
            return;
        }

        _seriesShelfOverscrollX = overscrollX;
        if (SeriesShelfItemsHost.RenderTransform is TranslateTransform translate)
            translate.X = overscrollX;
        else
            SeriesShelfItemsHost.RenderTransform = new TranslateTransform(overscrollX, 0d);
    }

    private void StartSeriesShelfBounceBack()
    {
        if (Math.Abs(_seriesShelfOverscrollX) <= 0.25d)
        {
            SetSeriesShelfOverscroll(0d);
            return;
        }

        _seriesShelfBounceStartX = _seriesShelfOverscrollX;
        _seriesShelfBounceDurationMs = Math.Clamp(180d + Math.Abs(_seriesShelfBounceStartX) * 1.6d, 200d, 320d);
        _seriesShelfBounceStopwatch = Stopwatch.StartNew();
        if (_seriesShelfBounceTimer is null)
        {
            _seriesShelfBounceTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _seriesShelfBounceTimer.Tick += SeriesShelfBounceTimer_Tick;
        }

        _seriesShelfBounceTimer.Start();
    }

    private void StopSeriesShelfBounceAnimation()
    {
        _seriesShelfBounceTimer?.Stop();
        _seriesShelfBounceStopwatch?.Stop();
    }

    private void SeriesShelfBounceTimer_Tick(object? sender, EventArgs e)
    {
        if (!SeriesShelfOverlay.IsVisible || _seriesShelfBounceStopwatch is null)
        {
            StopSeriesShelfBounceAnimation();
            SetSeriesShelfOverscroll(0d);
            return;
        }

        var progress = Math.Clamp(_seriesShelfBounceStopwatch.Elapsed.TotalMilliseconds / _seriesShelfBounceDurationMs, 0d, 1d);
        if (progress >= 1d)
        {
            StopSeriesShelfBounceAnimation();
            SetSeriesShelfOverscroll(0d);
            return;
        }

        var eased = 1d - Math.Pow(1d - progress, 4d);
        SetSeriesShelfOverscroll(_seriesShelfBounceStartX * (1d - eased));
    }
}
