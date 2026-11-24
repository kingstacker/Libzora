using Avalonia.Threading;

namespace Libzora;

public sealed partial class NativePdfReaderHost
{
    private long? _presentationFrameRequest;
    public event Action<long>? PresentationFrameRendered;

    public void RequestPresentationFrame(long revision)
    {
        if (_disposed) return;
        _presentationFrameRequest = revision;
        InvalidateVisual();
    }

    private void NotifyPresentationFrameDrawn(bool targetPageDrawn)
    {
        if (!targetPageDrawn || _disposed || _presentationFrameRequest is not { } revision
            || Bounds.Width < 2 || Bounds.Height < 2 || Math.Abs(DeviceScaling - _layoutDpi) > 0.001) return;
        var layout = _layout;
        var bounds = Bounds;
        var version = _version;
        var request = _layoutRequest;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _presentationFrameRequest != revision || version != _version
                || request != _layoutRequest || !ReferenceEquals(layout, _layout) || bounds != Bounds) return;
            _presentationFrameRequest = null;
            PresentationFrameRendered?.Invoke(revision);
        }, DispatcherPriority.Background);
    }
}
