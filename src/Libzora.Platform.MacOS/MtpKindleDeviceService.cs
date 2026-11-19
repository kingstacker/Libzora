using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS.Mtp;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Kindle devices that expose MTP instead of USB mass storage. macOS has no
/// MTP stack of its own, so this service talks to libmtp (the same library
/// calibre uses on macOS).
/// </summary>

/// <summary>
/// Constants, fields, construction, detection and caches.
/// </summary>
public sealed partial class MtpKindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    private const ushort AmazonVendorId = 0x1949;
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private const string AnnotationRoot = "system/ksdk/.annotations";
    private const string AnnotationFileName = "ksdk_annotation_v1.db";
    private const string ThumbnailDirectory = "system/thumbnails";
    // Opening a session reads the model, serial and storage from the device;
    // doing that on every 3 s poll is both slow and needless. USB change
    // notifications invalidate this cache immediately, so a longer lifetime is
    // safe and keeps libmtp from being opened/closed continuously.
    private static readonly TimeSpan DetectionCacheLifetime = TimeSpan.FromSeconds(10);
    // An arrival notification can precede usable MTP storage. Never hide that
    // reader for another 10 seconds just because the first attempt was early.
    private static readonly TimeSpan EmptyDetectionCacheLifetime = TimeSpan.FromMilliseconds(200);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly AppPaths _paths;
    private readonly IMetadataService _metadata;
    private readonly KindleScanCacheStore _scanCache;
    private readonly IBookFormatConverter _formatConverter;
    private readonly string _coverCacheDirectory;
    private readonly object _detectionSync = new();
    private readonly MtpAttachmentTracker _attachmentTracker = new();
    private readonly Func<CancellationToken, IReadOnlyList<KindleDevice>> _detectDevices;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly bool _detectionSupported = MtpDeviceSession.IsSupported;
    private IReadOnlyList<KindleDevice>? _detectionCache;
    private IReadOnlyList<KindleDevice>? _lastDetected;
    private DateTimeOffset _detectionCacheAt;
    private int _detectionGeneration;

    public MtpKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        IBookFormatConverter? formatConverter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _scanCache = new KindleScanCacheStore(paths);
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _detectDevices = Detect;
        _coverCacheDirectory = Path.Combine(paths.Covers, "kindle-mtp");
        Directory.CreateDirectory(_coverCacheDirectory);
    }

    internal MtpKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        Func<CancellationToken, IReadOnlyList<KindleDevice>> detectDevices,
        TimeProvider timeProvider) : this(paths, metadata)
    {
        _detectDevices = detectDevices ?? throw new ArgumentNullException(nameof(detectDevices));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _detectionSupported = true;
    }

    private IReadOnlyList<KindleDevice>? GetCachedDetection()
    {
        lock (_detectionSync)
        {
            if (_detectionCache is null) return null;
            var lifetime = _detectionCache.Count == 0 ? EmptyDetectionCacheLifetime : DetectionCacheLifetime;
            return _timeProvider.GetUtcNow() - _detectionCacheAt < lifetime ? _detectionCache : null;
        }
    }

    public async Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_detectionSupported) return [];
        cancellationToken.ThrowIfCancellationRequested();
        var cached = GetCachedDetection();
        if (cached is not null) return cached;

        // libmtp allows one session at a time, so detection shares the gate
        // with transfers. A scan or transfer holds it for as long as it runs;
        // the UI polls every few seconds with a 15 s timeout, so waiting here
        // reported a healthy device as "detection timed out" during any long
        // transfer. While the device is busy it is evidently still attached:
        // return the last result and let the next poll after the transfer
        // detect for real.
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<KindleDevice>? lastDetected;
            lock (_detectionSync) lastDetected = _lastDetected;
            if (lastDetected is not null) return lastDetected;
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            // Another detector may have populated the cache while this caller
            // waited for the gate. Do not open a second identical session.
            cached = GetCachedDetection();
            if (cached is not null) return cached;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int generation;
                lock (_detectionSync) generation = _detectionGeneration;
                var devices = await Task.Run(() => _detectDevices(cancellationToken), cancellationToken).ConfigureAwait(false);
                lock (_detectionSync)
                {
                    // A USB change during a native probe invalidates its result,
                    // not just its cache. Retry against the new topology rather
                    // than resurrecting the old device in the UI or busy path.
                    if (generation != _detectionGeneration) continue;
                    _lastDetected = devices;
                    _detectionCache = devices;
                    _detectionCacheAt = _timeProvider.GetUtcNow();
                    return devices;
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Checks raw USB topology only. Unlike <see cref="DetectDevicesAsync"/>
    /// this never opens an MTP session, so it is safe while the reader has
    /// been "stopped" and is still plugged in.
    /// </summary>
    public Task<bool> IsDeviceAttachedAsync(CancellationToken cancellationToken = default) =>
        IsDeviceAttachedAsync(null, cancellationToken);

    /// <summary>
    /// Identity-aware variant used to release the manual-disconnect latch.
    /// The stopped reader is matched against the USB roots observed for its
    /// serial; no session is opened. Identities that cannot be resolved to
    /// exactly one root fail closed so another Kindle cannot release the
    /// latch by being plugged in.
    /// </summary>
    public async Task<bool> IsDeviceAttachedAsync(
        string? deviceIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!MtpDeviceSession.IsSupported) return false;
        // Raw enumeration must not race an open libmtp session. When another
        // operation is running, assume the reader is still attached and leave
        // the manual-disconnect latch alone.
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return true;
        try
        {
            return await Task.Run(() =>
            {
                var roots = MtpDeviceSession.Detect()
                    .Where(raw => raw.VendorId == AmazonVendorId)
                    .Select(MtpDeviceIdentity.RootPath)
                    .ToArray();
                return _attachmentTracker.IsAttached(deviceIdentity, roots);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public void InvalidateDetectionCache()
    {
        lock (_detectionSync)
        {
            _detectionGeneration++;
            _detectionCache = null;
            // The USB topology changed, so a result cached from the previous
            // connection is no longer a safe answer when the libmtp gate is
            // busy. Let the next detection wait for the gate and report the
            // current state instead of resurrecting an unplugged device.
            _lastDetected = null;
        }
    }
}
