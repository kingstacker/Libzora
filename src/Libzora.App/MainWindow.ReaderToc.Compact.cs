using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Compact TOC panel and scroll indicators.
/// </summary>
public partial class MainWindow
{
    private void ReaderTocCompactExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(false);
    }

    private bool _readerCompactPointerPressedHandlerRegistered;

    private void ReaderTocCompactPanel_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_readerCompactPointerPressedHandlerRegistered) return;
        // The ScrollViewer can mark the pointer press as handled while starting
        // its own scroll gesture, so subscribe with handledEventsToo like the
        // WinUI reference's AddHandler call.
        ReaderTocCompactScrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderTocCompactScrollViewer_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _readerCompactPointerPressedHandlerRegistered = true;
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        _readerCompactPointerActive = true;
        _readerCompactPointerY = Math.Clamp(
            e.GetPosition(ReaderTocCompactScrollViewer).Y,
            0,
            ReaderTocCompactScrollViewer.Bounds.Height);
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerExited(object? sender, PointerEventArgs e)
    {
        _readerCompactPointerActive = false;
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ReaderTocCompactScrollViewer).Properties.IsLeftButtonPressed) return;
        var pointerY = e.GetPosition(ReaderTocCompactScrollViewer).Y;
        ReaderTocMarker? closestMarker = null;
        var closestDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.DataContext is not ReaderTocMarker marker || button.Bounds.Height <= 0) continue;
            try
            {
                var markerCenter = button
                    .TranslatePoint(new Point(0, button.Bounds.Height / 2), ReaderTocCompactScrollViewer)?
                    .Y ?? 0;
                var distance = Math.Abs(markerCenter - pointerY);
                if (distance >= closestDistance) continue;
                closestDistance = distance;
                closestMarker = marker;
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (closestMarker is null) return;
        NavigateToReaderTocItem(closestMarker.Item);
        e.Handled = true;
    }

    private void ReaderTocCompactScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01) return;
        var scrollViewer = ReaderTocCompactScrollViewer;
        var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var baseOffset = _readerCompactScrollAnimating
            ? _readerCompactScrollTarget
            : scrollViewer.Offset.Y;
        _readerCompactScrollStart = scrollViewer.Offset.Y;
        _readerCompactScrollTarget = Math.Clamp(
            baseOffset - delta * 120 * 0.45,
            0,
            maximum);
        _readerCompactScrollStartedAt = DateTimeOffset.UtcNow;
        _readerCompactScrollAnimating = true;
        EnsureReaderCompactScrollTimer();
        _readerCompactScrollTimer!.Start();
        e.Handled = true;
    }

    private void EnsureReaderCompactScrollTimer()
    {
        if (_readerCompactScrollTimer is not null) return;
        _readerCompactScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _readerCompactScrollTimer.Tick += ReaderCompactScrollTimer_Tick;
    }

    private void ReaderCompactScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_readerCompactScrollAnimating || !ReaderTocCompactPanel.IsVisible)
        {
            StopReaderCompactScrollAnimation();
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _readerCompactScrollStartedAt).TotalMilliseconds
                / ReaderCompactScrollAnimationDurationMs,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var offset = _readerCompactScrollStart
            + (_readerCompactScrollTarget - _readerCompactScrollStart) * eased;
        ReaderTocCompactScrollViewer.Offset = new Vector(0, offset);
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();

        if (progress >= 1) StopReaderCompactScrollAnimation();
    }

    private void StopReaderCompactScrollAnimation()
    {
        _readerCompactScrollAnimating = false;
        _readerCompactScrollTimer?.Stop();
    }

    private void UpdateReaderCompactScrollIndicators()
    {
        if (ReaderTocCompactUpIndicator is null || ReaderTocCompactDownIndicator is null) return;

        const double edgeTolerance = 0.5d;
        var offset = ReaderTocCompactScrollViewer.Offset.Y;
        var maximum = Math.Max(0, ReaderTocCompactScrollViewer.Extent.Height - ReaderTocCompactScrollViewer.Viewport.Height);
        ReaderTocCompactUpIndicator.Text = offset <= edgeTolerance ? "\u25B3" : "\u25B2";
        ReaderTocCompactDownIndicator.Text = offset >= maximum - edgeTolerance ? "\u25BD" : "\u25BC";
    }

    private void UpdateReaderCompactMarkerWave()
    {
        if (ReaderTocCompactScrollViewer is null
            || ReaderTocCompactList is null
            || ReaderTocCompactScrollViewer.Bounds.Height <= 0)
        {
            return;
        }

        Border? hoveredMarker = null;
        EpubReaderNavigationItem? hoveredItem = null;
        var hoveredDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.Content is not Border marker || button.Bounds.Height <= 0) continue;
            var markerData = button.DataContext as ReaderTocMarker;
            if (markerData is not null)
            {
                var isCurrent = _readerCompactSelectedTarget is not null
                    && markerData.Item.Target.Equals(
                        _readerCompactSelectedTarget,
                        StringComparison.OrdinalIgnoreCase);
                marker.Background = markerData.GetFill(isCurrent);
            }

            try
            {
                if (!_readerCompactPointerActive)
                {
                    SetReaderCompactMarkerWidth(marker, ReaderCompactMarkerMinimumWidth);
                    continue;
                }

                var markerCenter = button
                    .TranslatePoint(new Point(0, button.Bounds.Height / 2), ReaderTocCompactScrollViewer)?
                    .Y ?? 0;
                var normalizedDistance = Math.Clamp(
                    Math.Abs(markerCenter - _readerCompactPointerY) / ReaderCompactMarkerWaveRadius,
                    0,
                    1);
                var distance = Math.Abs(markerCenter - _readerCompactPointerY);
                var wave = Math.Sin((1 - normalizedDistance) * Math.PI / 2);
                SetReaderCompactMarkerWidth(
                    marker,
                    ReaderCompactMarkerMinimumWidth
                        + (ReaderCompactMarkerMaximumWidth - ReaderCompactMarkerMinimumWidth) * wave);
                if (distance < hoveredDistance)
                {
                    hoveredDistance = distance;
                    hoveredMarker = marker;
                    hoveredItem = markerData?.Item;
                }
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (hoveredMarker is not null)
            hoveredMarker.Background = ReaderPalette.For(CurrentReaderAppearance.Theme).AccentBrush;
        if (!_readerSliderPreviewVisible)
        {
            if (_readerCompactPointerActive)
                UpdateReaderCompactHoverLabel(hoveredMarker, hoveredItem);
            else
                HideReaderCompactHoverLabel();
        }
    }
}
