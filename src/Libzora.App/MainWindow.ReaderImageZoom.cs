using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Libzora;

/// <summary>
/// Full-viewport lightbox for double-clicked illustrations inside the desktop reader.
/// </summary>
public partial class MainWindow
{
    private const double MinReaderImageZoom = 1.0;
    private const double MaxReaderImageZoom = 5.0;
    private const double DoubleClickReaderImageZoom = 2.0;

    private Bitmap? _readerZoomBitmap;
    private ScaleTransform? _readerImageZoomScaleTransform;
    private TranslateTransform? _readerImageZoomTranslateTransform;
    private double _readerImageZoomScale = 1.0;
    private Point _readerImageZoomOffset;
    private bool _readerImageZoomPressed;
    private bool _readerImageZoomPressedOnImage;
    private bool _readerImageZoomDragging;
    private Point _readerImageZoomPressPoint;
    private Point _readerImageZoomStartOffset;

    private void OpenReaderImageZoom(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        Bitmap? decoded = null;
        try
        {
            decoded = NativeReaderHost.DecodePreviewBitmap(path);
            decoded ??= new Bitmap(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            decoded?.Dispose();
            return;
        }

        if (decoded is null)
            return;

        HideReaderSelectionPopup();
        HideReaderFootnotePopup();
        HideReaderAnnotationHoverPopup();
        ReaderLayoutSettingsPopup.IsOpen = false;

        _readerZoomBitmap?.Dispose();
        _readerZoomBitmap = decoded;
        EnsureReaderImageZoomTransforms();
        ReaderImageZoomImage.Source = decoded;
        ResetReaderImageZoom();
        ReaderImageZoomOverlay.IsVisible = true;
        ReaderImageZoomOverlay.Focus();
    }

    private void CloseReaderImageZoom()
    {
        if (ReaderImageZoomOverlay is null)
            return;

        var wasVisible = ReaderImageZoomOverlay.IsVisible;
        ReaderImageZoomOverlay.IsVisible = false;
        ReaderImageZoomImage.Source = null;
        _readerZoomBitmap?.Dispose();
        _readerZoomBitmap = null;
        _readerImageZoomPressed = false;
        _readerImageZoomDragging = false;
        _readerImageZoomPressedOnImage = false;
        ResetReaderImageZoom();
        if (wasVisible && ReaderRoot.IsVisible)
            FocusCurrentReaderHost();
    }

    private void EnsureReaderImageZoomTransforms()
    {
        if (_readerImageZoomScaleTransform is not null && _readerImageZoomTranslateTransform is not null)
            return;

        _readerImageZoomScaleTransform = new ScaleTransform(1, 1);
        _readerImageZoomTranslateTransform = new TranslateTransform(0, 0);
        ReaderImageZoomImage.RenderTransform = new TransformGroup
        {
            Children = { _readerImageZoomScaleTransform, _readerImageZoomTranslateTransform }
        };
    }

    private void ResetReaderImageZoom()
    {
        _readerImageZoomScale = MinReaderImageZoom;
        _readerImageZoomOffset = default;
        ApplyReaderImageZoomTransform();
    }

    private void SetReaderImageZoom(double targetScale, Point? viewportAnchor = null)
    {
        var nextScale = Math.Clamp(targetScale, MinReaderImageZoom, MaxReaderImageZoom);
        if (Math.Abs(nextScale - MinReaderImageZoom) < 0.01)
        {
            ResetReaderImageZoom();
            return;
        }

        var oldScale = _readerImageZoomScale;
        if (viewportAnchor is { } anchor && oldScale > 0 && ReaderImageZoomImage.Bounds.Width > 0)
        {
            var bounds = ReaderImageZoomImage.Bounds;
            var center = new Point(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
            var relativeAnchor = anchor - center;
            var factor = nextScale / oldScale;
            _readerImageZoomOffset = new Point(
                relativeAnchor.X - (relativeAnchor.X - _readerImageZoomOffset.X) * factor,
                relativeAnchor.Y - (relativeAnchor.Y - _readerImageZoomOffset.Y) * factor);
        }

        _readerImageZoomScale = nextScale;
        _readerImageZoomOffset = ClampReaderImageZoomOffset(_readerImageZoomOffset);
        ApplyReaderImageZoomTransform();
    }

    private Point ClampReaderImageZoomOffset(Point offset)
    {
        if (_readerImageZoomScale <= MinReaderImageZoom + 0.01)
            return default;

        var imageBounds = ReaderImageZoomImage.Bounds;
        var viewportBounds = ReaderImageZoomViewport.Bounds;
        var maxX = Math.Max(0, (imageBounds.Width * _readerImageZoomScale - viewportBounds.Width) / 2.0 + 48);
        var maxY = Math.Max(0, (imageBounds.Height * _readerImageZoomScale - viewportBounds.Height) / 2.0 + 48);
        return new Point(
            Math.Clamp(offset.X, -maxX, maxX),
            Math.Clamp(offset.Y, -maxY, maxY));
    }

    private void ApplyReaderImageZoomTransform()
    {
        EnsureReaderImageZoomTransforms();
        _readerImageZoomScaleTransform!.ScaleX = _readerImageZoomScale;
        _readerImageZoomScaleTransform.ScaleY = _readerImageZoomScale;
        _readerImageZoomTranslateTransform!.X = _readerImageZoomOffset.X;
        _readerImageZoomTranslateTransform.Y = _readerImageZoomOffset.Y;
        ReaderImageZoomLabel.Text = $"{Math.Round(_readerImageZoomScale * 100):0}%";
        ReaderImageZoomViewport.Cursor = new Cursor(_readerImageZoomScale > 1.01
            ? StandardCursorType.SizeAll
            : StandardCursorType.Arrow);
    }

    private bool IsPointOverZoomImage(Point viewportPoint)
    {
        var bounds = ReaderImageZoomImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var center = new Point(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
        var halfWidth = bounds.Width * _readerImageZoomScale / 2.0;
        var halfHeight = bounds.Height * _readerImageZoomScale / 2.0;
        var left = center.X + _readerImageZoomOffset.X - halfWidth;
        var right = center.X + _readerImageZoomOffset.X + halfWidth;
        var top = center.Y + _readerImageZoomOffset.Y - halfHeight;
        var bottom = center.Y + _readerImageZoomOffset.Y + halfHeight;
        return viewportPoint.X >= left && viewportPoint.X <= right
            && viewportPoint.Y >= top && viewportPoint.Y <= bottom;
    }

    private void ReaderImageZoomViewport_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(ReaderImageZoomViewport);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var position = point.Position;
        var overImage = IsPointOverZoomImage(position);
        if (e.ClickCount >= 2 && overImage)
        {
            SetReaderImageZoom(
                _readerImageZoomScale > 1.05 ? MinReaderImageZoom : DoubleClickReaderImageZoom,
                position);
            _readerImageZoomPressed = false;
            _readerImageZoomDragging = false;
            e.Handled = true;
            return;
        }

        _readerImageZoomPressed = true;
        _readerImageZoomPressedOnImage = overImage;
        _readerImageZoomDragging = false;
        _readerImageZoomPressPoint = position;
        _readerImageZoomStartOffset = _readerImageZoomOffset;
        if (_readerImageZoomScale > 1.01 && overImage)
            e.Pointer.Capture(ReaderImageZoomViewport);
        e.Handled = true;
    }

    private void ReaderImageZoomViewport_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_readerImageZoomPressed || !_readerImageZoomPressedOnImage || _readerImageZoomScale <= 1.01)
            return;

        var delta = e.GetPosition(ReaderImageZoomViewport) - _readerImageZoomPressPoint;
        if (!_readerImageZoomDragging && delta.X * delta.X + delta.Y * delta.Y >= 16)
            _readerImageZoomDragging = true;

        if (!_readerImageZoomDragging)
            return;

        _readerImageZoomOffset = ClampReaderImageZoomOffset(_readerImageZoomStartOffset + delta);
        ApplyReaderImageZoomTransform();
        e.Handled = true;
    }

    private void ReaderImageZoomViewport_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_readerImageZoomPressed)
            return;

        var wasDragging = _readerImageZoomDragging;
        var clickedOutsideImage = !_readerImageZoomPressedOnImage
            && !IsPointOverZoomImage(e.GetPosition(ReaderImageZoomViewport));
        _readerImageZoomPressed = false;
        _readerImageZoomDragging = false;
        _readerImageZoomPressedOnImage = false;
        if (Equals(e.Pointer.Captured, ReaderImageZoomViewport))
            e.Pointer.Capture(null);

        if (!wasDragging && clickedOutsideImage)
            CloseReaderImageZoom();

        e.Handled = true;
    }

    private void ReaderImageZoomViewport_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        var factor = Math.Pow(1.18, e.Delta.Y);
        SetReaderImageZoom(_readerImageZoomScale * factor, e.GetPosition(ReaderImageZoomViewport));
        e.Handled = true;
    }

    private void ReaderImageZoomOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReaderImageZoomOverlay.IsVisible)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                CloseReaderImageZoom();
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                SetReaderImageZoom(_readerImageZoomScale * 1.25);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                SetReaderImageZoom(_readerImageZoomScale / 1.25);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                ResetReaderImageZoom();
                e.Handled = true;
                break;
        }
    }

    private void ReaderImageZoomInButton_Click(object? sender, RoutedEventArgs e) =>
        SetReaderImageZoom(_readerImageZoomScale * 1.25);

    private void ReaderImageZoomOutButton_Click(object? sender, RoutedEventArgs e) =>
        SetReaderImageZoom(_readerImageZoomScale / 1.25);

    private void ReaderImageZoomResetButton_Click(object? sender, RoutedEventArgs e) =>
        ResetReaderImageZoom();

    private void ReaderImageZoomCloseButton_Click(object? sender, RoutedEventArgs e) =>
        CloseReaderImageZoom();
}
