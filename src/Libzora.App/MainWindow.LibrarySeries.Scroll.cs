using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Libzora;

/// <summary>
/// Horizontal drag-scrolling, velocity-based momentum inertia, edge rubber-band
/// overscroll, and bounce-back animation for the expanded series strip.
/// </summary>
public partial class MainWindow
{
    private const double SeriesShelfDragThreshold = 6d;
    private const double SeriesShelfMaxOverscroll = 112d;
    private const double SeriesShelfOverscrollResistance = 0.55d;
    private const double SeriesShelfVelocityWindowSeconds = 0.10d;
    private const double SeriesShelfReleaseIdleTimeoutSeconds = 0.09d;
    private const double SeriesShelfMinInertiaVelocity = 80d;
    private const double SeriesShelfMaxInertiaVelocity = 4500d;
    private const double SeriesShelfStopVelocity = 18d;
    private const double SeriesShelfDecelerationRate = 4.2d;
    private const double SeriesShelfEdgeDampingRate = 18d;
    private const double SeriesShelfEdgeStopVelocity = 40d;

    private readonly List<(double PointerX, long TimestampTicks)> _seriesShelfVelocitySamples = new(16);
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
    private DispatcherTimer? _seriesShelfInertiaTimer;
    private Stopwatch? _seriesShelfInertiaStopwatch;
    private double _seriesShelfInertiaLastSeconds;
    private double _seriesShelfInertiaVelocityX;
    private double _seriesShelfInertiaRawOverscrollX;

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
        StopSeriesShelfScrollAnimations();
        _seriesShelfVelocitySamples.Clear();
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

        StopSeriesShelfScrollAnimations();
        _seriesShelfVelocitySamples.Clear();
        _seriesShelfDragPressed = true;
        _seriesShelfDragActive = false;
        _seriesShelfSuppressTileClick = false;
        _seriesShelfDragStartPoint = e.GetPosition(SeriesShelfScrollViewer);
        _seriesShelfDragStartOffsetX = SeriesShelfScrollViewer.Offset.X;
        _seriesShelfDragStartVirtualOverscrollX = UnapplySeriesShelfRubberBand(_seriesShelfOverscrollX);
        _seriesShelfDragDeadzoneX = 0d;
        RecordSeriesShelfVelocitySample(_seriesShelfDragStartPoint.X, Stopwatch.GetTimestamp());
    }

    private void SeriesShelfScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seriesShelfDragPressed || !SeriesShelfOverlay.IsVisible) return;
        if (!e.GetCurrentPoint(SeriesShelfScrollViewer).Properties.IsLeftButtonPressed)
        {
            FinishSeriesShelfDrag(e.Pointer, suppressClick: false, allowInertia: false);
            return;
        }

        var current = e.GetPosition(SeriesShelfScrollViewer);
        var nowTicks = Stopwatch.GetTimestamp();
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

        RecordSeriesShelfVelocitySample(current.X, nowTicks);

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
        FinishSeriesShelfDrag(e.Pointer, suppressClick: wasDragging, allowInertia: wasDragging);
        if (wasDragging)
            e.Handled = true;
    }

    private void SeriesShelfScrollViewer_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_seriesShelfDragPressed && !_seriesShelfDragActive) return;
        FinishSeriesShelfDrag(pointer: null, suppressClick: _seriesShelfDragActive, allowInertia: _seriesShelfDragActive);
    }

    private void FinishSeriesShelfDrag(IPointer? pointer, bool suppressClick, bool allowInertia)
    {
        var releaseVelocityX = allowInertia
            ? ComputeSeriesShelfReleaseVelocity(_seriesShelfVelocitySamples, Stopwatch.GetTimestamp(), Stopwatch.Frequency)
            : 0d;
        _seriesShelfVelocitySamples.Clear();
        _seriesShelfDragPressed = false;
        _seriesShelfDragActive = false;
        if (!suppressClick)
            _seriesShelfSuppressTileClick = false;
        SeriesShelfScrollViewer.Cursor = Cursor.Default;
        if (pointer is not null && ReferenceEquals(pointer.Captured, SeriesShelfScrollViewer))
            pointer.Capture(null);

        if (Math.Abs(_seriesShelfOverscrollX) > 0.25d)
        {
            StartSeriesShelfBounceBack();
            return;
        }

        SetSeriesShelfOverscroll(0d);
        var maxX = Math.Max(0d, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        if (maxX > 0d && Math.Abs(releaseVelocityX) >= SeriesShelfMinInertiaVelocity)
            StartSeriesShelfInertia(releaseVelocityX);
    }

    private void SeriesShelfScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _seriesShelfPendingCenterIndex = null;
        if (e.Handled || Math.Abs(e.Delta.Y) <= Math.Abs(e.Delta.X) || e.Delta.Y == 0)
            return;
        var maxX = Math.Max(0, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        if (maxX <= 0) return;
        StopSeriesShelfScrollAnimations();
        SetSeriesShelfOverscroll(0d);
        var current = SeriesShelfScrollViewer.Offset;
        var targetX = Math.Clamp(current.X - e.Delta.Y * 48, 0, maxX);
        SeriesShelfScrollViewer.Offset = new Vector(targetX, current.Y);
        e.Handled = true;
    }

    private void RecordSeriesShelfVelocitySample(double pointerX, long timestampTicks)
    {
        _seriesShelfVelocitySamples.Add((pointerX, timestampTicks));
        var cutoffTicks = timestampTicks - (long)(SeriesShelfVelocityWindowSeconds * Stopwatch.Frequency);
        while (_seriesShelfVelocitySamples.Count > 2 && _seriesShelfVelocitySamples[0].TimestampTicks < cutoffTicks)
            _seriesShelfVelocitySamples.RemoveAt(0);
    }

    internal static double ComputeSeriesShelfReleaseVelocity(
        IReadOnlyList<(double PointerX, long TimestampTicks)> samples,
        long releaseTicks,
        long tickFrequency)
    {
        if (samples.Count < 2 || tickFrequency <= 0) return 0d;
        var last = samples[^1];
        var idleSeconds = (releaseTicks - last.TimestampTicks) / (double)tickFrequency;
        if (idleSeconds > SeriesShelfReleaseIdleTimeoutSeconds) return 0d;

        var windowStartTicks = last.TimestampTicks - (long)(SeriesShelfVelocityWindowSeconds * tickFrequency);
        var first = samples[0];
        for (var i = 0; i < samples.Count - 1; i++)
        {
            if (samples[i].TimestampTicks >= windowStartTicks)
            {
                first = samples[i];
                break;
            }
        }

        var dt = (last.TimestampTicks - first.TimestampTicks) / (double)tickFrequency;
        if (dt <= 0.002d) return 0d;
        // Dragging the pointer left (negative pointer delta) advances scroll offset forward (positive velocity).
        var velocity = -(last.PointerX - first.PointerX) / dt;
        return Math.Clamp(velocity, -SeriesShelfMaxInertiaVelocity, SeriesShelfMaxInertiaVelocity);
    }

    internal static (double OffsetX, double RawOverscrollX, double VelocityX, bool ShouldBounceBack, bool Finished)
        StepSeriesShelfInertia(double offsetX, double rawOverscrollX, double velocityX, double maxX, double dt)
    {
        dt = Math.Clamp(dt, 0.001d, 0.05d);
        if (Math.Abs(rawOverscrollX) > 0.001d)
        {
            var dampedVelocity = velocityX * Math.Exp(-SeriesShelfEdgeDampingRate * dt);
            var nextRawOverscroll = rawOverscrollX - dampedVelocity * dt;
            var crossedZero = Math.Sign(nextRawOverscroll) != Math.Sign(rawOverscrollX);
            if (crossedZero || Math.Abs(dampedVelocity) < SeriesShelfEdgeStopVelocity)
                return (Math.Clamp(offsetX, 0d, maxX), rawOverscrollX, 0d, ShouldBounceBack: true, Finished: false);
            return (Math.Clamp(offsetX, 0d, maxX), nextRawOverscroll, dampedVelocity, ShouldBounceBack: false, Finished: false);
        }

        var nextVelocity = velocityX * Math.Exp(-SeriesShelfDecelerationRate * dt);
        var nextOffset = offsetX + nextVelocity * dt;
        if (nextOffset < 0d || nextOffset > maxX)
        {
            var clampedX = Math.Clamp(nextOffset, 0d, maxX);
            var overscroll = clampedX - nextOffset;
            var edgeVelocity = nextVelocity * 0.45d;
            if (Math.Abs(edgeVelocity) < SeriesShelfEdgeStopVelocity)
                return (clampedX, overscroll, 0d, ShouldBounceBack: Math.Abs(overscroll) > 0.25d, Finished: Math.Abs(overscroll) <= 0.25d);
            return (clampedX, overscroll, edgeVelocity, ShouldBounceBack: false, Finished: false);
        }

        if (Math.Abs(nextVelocity) < SeriesShelfStopVelocity)
            return (nextOffset, 0d, 0d, ShouldBounceBack: false, Finished: true);

        return (nextOffset, 0d, nextVelocity, ShouldBounceBack: false, Finished: false);
    }

    private void StartSeriesShelfInertia(double initialVelocityX)
    {
        StopSeriesShelfScrollAnimations();
        _seriesShelfInertiaVelocityX = Math.Clamp(initialVelocityX, -SeriesShelfMaxInertiaVelocity, SeriesShelfMaxInertiaVelocity);
        _seriesShelfInertiaRawOverscrollX = 0d;
        _seriesShelfInertiaLastSeconds = 0d;
        _seriesShelfInertiaStopwatch = Stopwatch.StartNew();
        if (_seriesShelfInertiaTimer is null)
        {
            _seriesShelfInertiaTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(12)
            };
            _seriesShelfInertiaTimer.Tick += SeriesShelfInertiaTimer_Tick;
        }
        _seriesShelfInertiaTimer.Start();
    }

    private void StopSeriesShelfInertiaAnimation()
    {
        _seriesShelfInertiaTimer?.Stop();
        _seriesShelfInertiaStopwatch?.Stop();
        _seriesShelfInertiaVelocityX = 0d;
        _seriesShelfInertiaRawOverscrollX = 0d;
    }

    private void StopSeriesShelfScrollAnimations()
    {
        StopSeriesShelfInertiaAnimation();
        StopSeriesShelfBounceAnimation();
    }

    private void SeriesShelfInertiaTimer_Tick(object? sender, EventArgs e)
    {
        if (!SeriesShelfOverlay.IsVisible || _seriesShelfInertiaStopwatch is null)
        {
            StopSeriesShelfInertiaAnimation();
            SetSeriesShelfOverscroll(0d);
            return;
        }

        var elapsedSeconds = _seriesShelfInertiaStopwatch.Elapsed.TotalSeconds;
        var dt = elapsedSeconds - _seriesShelfInertiaLastSeconds;
        _seriesShelfInertiaLastSeconds = elapsedSeconds;
        if (dt <= 0d) return;

        var maxX = Math.Max(0d, SeriesShelfScrollViewer.Extent.Width - SeriesShelfScrollViewer.Viewport.Width);
        var currentOffset = SeriesShelfScrollViewer.Offset.X;
        var step = StepSeriesShelfInertia(currentOffset, _seriesShelfInertiaRawOverscrollX, _seriesShelfInertiaVelocityX, maxX, dt);

        SeriesShelfScrollViewer.Offset = new Vector(step.OffsetX, 0d);
        _seriesShelfInertiaRawOverscrollX = step.RawOverscrollX;
        _seriesShelfInertiaVelocityX = step.VelocityX;
        SetSeriesShelfOverscroll(ApplySeriesShelfRubberBand(step.RawOverscrollX));

        if (step.ShouldBounceBack)
        {
            StopSeriesShelfInertiaAnimation();
            StartSeriesShelfBounceBack();
            return;
        }

        if (step.Finished)
        {
            StopSeriesShelfInertiaAnimation();
            SetSeriesShelfOverscroll(0d);
        }
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
