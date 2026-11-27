using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

public partial class MainWindow
{
    private Task? _readingMaterialsRefreshTask;
    private bool _readingMaterialsRefreshPending;
    private readonly HashSet<string> _kindleClippingReloadRequested = new(StringComparer.Ordinal);
    private readonly HashSet<string> _kindleClippingRetryRequested = new(StringComparer.Ordinal);
    private CancellationTokenSource? _kindleClippingReadCancellation;

    // UI entry points share one load, including its device read and task record.
    private Task RefreshReadingMaterialsAsync()
    {
        if (_readingMaterialsRefreshTask is not { IsCompleted: false })
            _readingMaterialsRefreshTask = RefreshReadingMaterialsBatchAsync();
        return _readingMaterialsRefreshTask;
    }

    private async Task RefreshReadingMaterialsBatchAsync()
    {
        // Publish the shared task before any synchronous completion/re-entrancy.
        await Task.Yield();
        do
        {
            _readingMaterialsRefreshPending = false;
            await RefreshReadingMaterialsCoreAsync();
        } while (_readingMaterialsRefreshPending && !_lifetimeCancellation.IsCancellationRequested);
    }

    private void RequestKindleClippingReload(KindleDevice device, bool retryUnresolved = false)
    {
        // Keep the previous rows for exact-version reuse after reading the latest database.
        _kindleClippingReloadRequested.Add(device.Identity);
        if (retryUnresolved) _kindleClippingRetryRequested.Add(device.Identity);
        if (_readingMaterialsRefreshTask is { IsCompleted: false }) _readingMaterialsRefreshPending = true;
    }

    private async Task<IReadOnlyList<KindleClipping>> ReadKindleClippingsForDisplayAsync(KindleDevice device)
    {
        var kindle = _kindle!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _kindleClippingReadCancellation = cancellation;
        IReadOnlyList<KindleClipping> clippings = [];
        var reload = _kindleClippingReloadRequested.Remove(device.Identity);
        var retry = _kindleClippingRetryRequested.Remove(device.Identity);
        try
        {
            // Progressive shelf scans must finish before their versions can validate persisted text.
            if (_deviceBookScanTask is { IsCompleted: false } scan) await scan.WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_deviceEjectInProgress || !IsCurrentDevice(device)) throw new OperationCanceledException(cancellation.Token);
            var books = DeviceBooks.Select(card => card.Book).ToArray();
            await TrackDeviceOperationAsync(async () =>
            {
                var previous = _deviceClippingCache.GetValueOrDefault(device.Identity)
                    ?? (await _kindleAuxiliaryCacheStore.GetAsync(device.Identity, cancellation.Token))?.Clippings
                    ?? [];
                if (!reload && _deviceClippingCache.TryGetValue(device.Identity, out var cached)
                    && !cached.Any(clip => clip.IsReadOnly && clip.Type == KindleClippingType.Highlight && clip.SourceBook is null))
                    clippings = cached;
                else
                    clippings = await kindle.ReadClippingsAsync(device, cancellation.Token, MaxReadingMaterials + 1);

                KindleAnnotationClippingTextResolver.RestoreCachedText(device, books, clippings, previous, retry);
                if (retry) _kindleAnnotationSync.RetryUnresolvedClippingText();
                if (clippings.Any(KindleAnnotationClippingTextResolver.NeedsResolution))
                {
                    await RunKindleAnnotationTaskAsync(
                        T("读取 Kindle 划线原文"), cancellable: true,
                        async (progress, token) =>
                        {
                            await _kindleAnnotationSync.ResolveClippingTextAsync(device, books, clippings, kindle, token, progress);
                            return true;
                        }, cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentDevice(device)) throw new OperationCanceledException(cancellation.Token);
                _deviceClippingCache[device.Identity] = clippings;
                await PersistDeviceAuxiliaryCacheAsync(device);
            });
            return clippings;
        }
        catch
        {
            // A canceled/failed reload must still read fresh records on the next attempt.
            if (reload) _kindleClippingReloadRequested.Add(device.Identity);
            if (retry) _kindleClippingRetryRequested.Add(device.Identity);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_kindleClippingReadCancellation, cancellation)) _kindleClippingReadCancellation = null;
        }
    }
}
