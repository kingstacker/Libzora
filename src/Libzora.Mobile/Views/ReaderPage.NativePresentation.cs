using Libzora.Core;

namespace Libzora.Mobile.Views;

public partial class ReaderPage
{
    private bool _nativePresentationFrameReady;
    private long _nativePresentationFrameRevision;

    public long NativePresentationFrameRevision => _nativePresentationFrameRevision;

    private void AttachNativePresentationFrameHost()
    {
        if (_usesNativeFrontend && _readerHost is IReaderPresentationFrameHost host)
            host.PresentationFrameRendered += OnNativePresentationFrameRendered;
    }

    private void RequestNativePresentationFrame()
    {
        if (!_usesNativeFrontend || !_ready || _disposed) return;
        _nativePresentationFrameReady = false;
        var revision = ++_nativePresentationFrameRevision;
        // The renderer validates its arranged viewport before reporting the
        // draw. Request it now so a covered iOS surface is not starved behind
        // native animation work at Avalonia's Background dispatcher priority.
        if (_readerHost is IReaderPresentationFrameHost host) host.RequestPresentationFrame(revision);
        NotifyNativeState();
    }

    private void OnNativePresentationFrameRendered(long revision)
    {
        if (_disposed || !_ready || revision != _nativePresentationFrameRevision) return;
        _nativePresentationFrameReady = true;
        NotifyNativeState();
    }
}
