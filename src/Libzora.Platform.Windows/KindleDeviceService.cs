using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Constants, fields, construction and device detection.
/// </summary>
public sealed partial class KindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private const int FileOperationRetryAttempts = 30;
    private const int FileOperationRetryDelayMilliseconds = 100;
    // A timed-out Shell/WPD enumeration cannot be cancelled: its native call
    // may never return after the reader disappears. Allow one retry (the
    // reconnection path) but never let every refresh accumulate another dead
    // detection task.
    private const int MaximumOutstandingWpdDetections = 2;
    private static readonly TimeSpan DefaultWpdDetectionTimeout = TimeSpan.FromSeconds(12);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".prc", ".kfx"
    };
    private readonly IMetadataService _metadata;
    private readonly IBookFormatConverter _formatConverter;
    private readonly AppPaths? _paths;
    private readonly string? _coverCacheDirectory;
    private readonly KindleScanCacheStore? _scanCache;
    private readonly Func<CancellationToken, Task<IReadOnlyList<KindleDevice>>> _detectWpdDevicesAsync;
    private readonly Func<CancellationToken, IReadOnlyList<KindleDevice>> _detectMassStorageDevices;
    private readonly TimeSpan _wpdDetectionTimeout;
    private readonly object _wpdDetectionSync = new();
    // Serializes live native probes: a probe that has not been declared hung
    // holds this gate until it completes. A timed-out probe releases the gate
    // when it is abandoned so exactly one replacement can be attempted.
    private readonly SemaphoreSlim _wpdDetectionGate = new(1, 1);
    private readonly List<WpdDetectionAttempt> _outstandingWpdDetections = [];
    private WpdDetectionAttempt? _wpdDetectionAttempt;

    public KindleDeviceService()
        : this(null, new BookMetadataService(), new BookFormatConversionService())
    {
    }

    public KindleDeviceService(
        AppPaths? paths,
        IMetadataService metadata,
        IBookFormatConverter? formatConverter = null)
        : this(
            paths,
            metadata,
            formatConverter,
            static cancellationToken => Task.Run(
                () => WpdKindleAccess.DetectDevices(cancellationToken),
                CancellationToken.None),
            DetectMassStorageDevices,
            DefaultWpdDetectionTimeout)
    {
    }

    internal KindleDeviceService(
        AppPaths? paths,
        IMetadataService metadata,
        IBookFormatConverter? formatConverter,
        Func<CancellationToken, Task<IReadOnlyList<KindleDevice>>> detectWpdDevicesAsync,
        Func<CancellationToken, IReadOnlyList<KindleDevice>> detectMassStorageDevices,
        TimeSpan wpdDetectionTimeout)
    {
        _paths = paths;
        _metadata = metadata;
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _detectWpdDevicesAsync = detectWpdDevicesAsync;
        _detectMassStorageDevices = detectMassStorageDevices;
        _wpdDetectionTimeout = wpdDetectionTimeout;
        _coverCacheDirectory = paths is null ? null : Path.Combine(paths.Covers, "kindle");
        _scanCache = paths is null ? null : new KindleScanCacheStore(paths);
        if (_coverCacheDirectory is not null) Directory.CreateDirectory(_coverCacheDirectory);
    }

    public async Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        var wpdAttempt = GetOrStartWpdDetectionAttempt();
        var massStorageDevices = await Task.Run(
                () => _detectMassStorageDevices(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        var devices = new List<KindleDevice>(massStorageDevices);
        if (wpdAttempt is null) return devices;

        IReadOnlyList<KindleDevice> wpdDevices;
        try
        {
            wpdDevices = await wpdAttempt.Task
                .WaitAsync(_wpdDetectionTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A native Shell/WPD enumeration can block forever on a reader
            // that disappeared mid-unplug. Do not turn that into a failed
            // refresh: the mounted mass-storage devices are still valid, and
            // the probe is abandoned below so the next refresh can retry
            // (for example after the user plugs the reader back in).
            return devices;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A WPD/Shell enumeration can also fail outright (reader
            // unplugged mid-enumeration, COM or IO error). The mounted
            // volumes detected above are still valid, so a failed WPD probe
            // must not turn the whole refresh into an error.
            SoftwareLog.ErrorWithoutMessage("kindle.detect.wpd", exception);
            return devices;
        }
        finally
        {
            lock (_wpdDetectionSync)
            {
                // Never leave a timed-out or cancelled detection cached. After
                // a USB unplug a Shell/WPD call can block on the vanished
                // device; reusing that task would make every later refresh
                // wait on the same dead handle, so replugging the reader
                // appeared to do nothing until the app restarted.
                if (ReferenceEquals(_wpdDetectionAttempt, wpdAttempt))
                    _wpdDetectionAttempt = null;
            }

            // A call that is still running after this caller stops waiting is
            // abandoned, not killed (the COM call cannot be cancelled).
            // Releasing the gate lets the next refresh start exactly one
            // replacement probe; the abandoned call remains counted against
            // the bounded outstanding-probe limit. When the task completed on
            // its own, its continuation already released the gate.
            if (!wpdAttempt.Task.IsCompleted) wpdAttempt.ReleaseGate();
        }

        devices.AddRange(wpdDevices.Where(wpd => devices.All(disk =>
            !string.Equals(disk.Identity, wpd.Identity, StringComparison.OrdinalIgnoreCase))));
        return devices;
    }

    private WpdDetectionAttempt? GetOrStartWpdDetectionAttempt()
    {
        lock (_wpdDetectionSync)
        {
            if (_wpdDetectionAttempt is { } current && !current.Task.IsCompleted)
                return current;

            // Timed-out detections cannot be cancelled. Prune the ones that
            // eventually returned and cap the still-running ones so repeated
            // refreshes cannot accumulate an unbounded number of native COM
            // calls. When the cap is reached the mass-storage result is still
            // returned, and a later completion frees a slot for a new probe.
            _outstandingWpdDetections.RemoveAll(static attempt => attempt.Task.IsCompleted);
            if (_outstandingWpdDetections.Count >= MaximumOutstandingWpdDetections)
                return null;

            // At most one live native probe runs at a time. The gate is
            // released only when that probe completes or when its caller
            // abandons it after a timeout, so ordinary refreshes cannot
            // overlap two healthy Shell/WPD enumerations.
            if (!_wpdDetectionGate.Wait(0)) return null;

            Task<IReadOnlyList<KindleDevice>> task;
            try
            {
                task = _detectWpdDevicesAsync(CancellationToken.None);
            }
            catch
            {
                _wpdDetectionGate.Release();
                throw;
            }

            var attempt = new WpdDetectionAttempt(task, _wpdDetectionGate);
            _wpdDetectionAttempt = attempt;
            _outstandingWpdDetections.Add(attempt);
            _ = task.ContinueWith(
                completed =>
                {
                    lock (_wpdDetectionSync) _outstandingWpdDetections.Remove(attempt);
                    // Observe faults even when no caller is waiting anymore
                    // (for example after a timeout).
                    _ = completed.Exception;
                    attempt.ReleaseGate();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return attempt;
        }
    }

    /// <summary>
    /// One native WPD probe plus ownership of the serialization gate. The gate
    /// is released exactly once: when the probe completes, or when its caller
    /// gives up waiting on a hung native call.
    /// </summary>
    private sealed class WpdDetectionAttempt
    {
        private readonly SemaphoreSlim _gate;
        private int _gateReleased;

        public WpdDetectionAttempt(
            Task<IReadOnlyList<KindleDevice>> task,
            SemaphoreSlim gate)
        {
            Task = task;
            _gate = gate;
        }

        public Task<IReadOnlyList<KindleDevice>> Task { get; }

        public void ReleaseGate()
        {
            if (Interlocked.Exchange(ref _gateReleased, 1) == 0) _gate.Release();
        }
    }

    /// <summary>
    /// Checks whether a reader is still physically attached without opening a
    /// WPD session or touching a mounted volume. The UI uses this to release
    /// its "stopped access" latch after the reader is unplugged.
    /// </summary>
    public Task<bool> IsDeviceAttachedAsync(CancellationToken cancellationToken = default) =>
        IsDeviceAttachedAsync(null, cancellationToken);

    public Task<bool> IsDeviceAttachedAsync(string? deviceIdentity, CancellationToken cancellationToken = default) =>
        Task.Run(() => IsDeviceAttached(deviceIdentity, cancellationToken), cancellationToken);

    private static bool IsDeviceAttached(string? deviceIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drives = DetectMassStorageDevices(cancellationToken);
        if (!string.IsNullOrWhiteSpace(deviceIdentity))
        {
            // The stopped identity is either a volume serial (mounted USB
            // disk) or a WPD Plug and Play device path. Match that exact
            // device so an unrelated phone or flash drive cannot hold the
            // latch open.
            if (drives.Any(drive =>
                    string.Equals(drive.Identity, deviceIdentity, StringComparison.OrdinalIgnoreCase)))
                return true;
            return WpdAttachmentProbe.IsDeviceAttached(deviceIdentity, cancellationToken);
        }

        return drives.Count > 0 || WpdAttachmentProbe.HasAnyDevice(cancellationToken);
    }

    private static List<KindleDevice> DetectMassStorageDevices(CancellationToken cancellationToken)
    {
        var devices = new List<KindleDevice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                var profile = ReaderDeviceProfiles.DetectMounted(drive.RootDirectory.FullName, drive.VolumeLabel);
                if (profile is null) continue;
                devices.Add(new KindleDevice
                {
                    RootPath = drive.RootDirectory.FullName,
                    Profile = profile,
                    VolumeSerial = GetVolumeSerial(drive.RootDirectory.FullName),
                    Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? profile.DisplayName : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsReady = true
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return devices;
    }
}
