using Avalonia.Threading;

namespace Libzora;

public sealed partial class NativeReaderHost
{
    private long? _presentationFrameRequest;
    public event Action<long>? PresentationFrameRendered;

    public void RequestPresentationFrame(long revision)
    {
        if (_disposed) return;
        _presentationFrameRequest = revision;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    private bool HasCurrentPresentationLayout => !_disposed && !_composePending
        && (_relayoutCts is null || _backgroundCompletionActive) && _relayoutTimer?.IsEnabled != true
        && _content is not null && _layout is { Pages.Count: > 0 } && _options is not null
        && Bounds.Width >= 2 && Bounds.Height >= 2
        && Math.Abs(ComposePageWidth(Bounds.Width) - _options.ViewportWidth) < 0.01
        && Math.Abs(Bounds.Height - _options.ViewportHeight) < 0.01;

    private void NotifyPresentationFrameDrawn()
    {
        if (_presentationFrameRequest is not { } revision || !HasCurrentPresentationLayout) return;
        var layout = _layout;
        var navigation = _navigationVersion;
        var bounds = Bounds;
        // Never publish SwiftUI state synchronously inside an Avalonia render.
        Dispatcher.UIThread.Post(() =>
        {
            if (_presentationFrameRequest != revision || !HasCurrentPresentationLayout
                || !ReferenceEquals(layout, _layout) || navigation != _navigationVersion || bounds != Bounds) return;
            _presentationFrameRequest = null;
            PresentationFrameRendered?.Invoke(revision);
        }, DispatcherPriority.Loaded);
    }
}
