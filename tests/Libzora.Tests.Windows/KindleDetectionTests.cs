using Libzora.Core;
using Libzora.Platform.Windows;

namespace Libzora.Tests.Windows;

public sealed class KindleDetectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task MassStorageDevicesAreReturnedWhenWpdDetectionTimesOut()
    {
        var disk = new KindleDevice
        {
            RootPath = @"C:\fake-reader",
            VolumeSerial = "DISK-1",
            IsReady = true
        };
        var wpd = new ControlledWpdDetection();
        var service = CreateService(wpd, _ => [disk]);

        var devices = await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(disk, Assert.Single(devices));
        Assert.Equal(1, wpd.StartCount);
        wpd.CompleteAll();
    }

    [Fact]
    public async Task WpdDetectionFaultStillReturnsMassStorageDevices()
    {
        var disk = new KindleDevice
        {
            RootPath = @"C:\fake-reader",
            VolumeSerial = "DISK-FAULT",
            IsReady = true
        };
        var service = new KindleDeviceService(
            paths: null,
            metadata: new StubMetadata(),
            formatConverter: null,
            detectWpdDevicesAsync: _ => Task.FromException<IReadOnlyList<KindleDevice>>(
                new IOException("WPD enumeration failed")),
            detectMassStorageDevices: _ => [disk],
            wpdDetectionTimeout: TestTimeout);

        var devices = await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(disk, Assert.Single(devices));
    }

    [Fact]
    public async Task TimedOutWpdDetectionIsRetriedAndCanReconnect()
    {
        var disk = new KindleDevice { RootPath = @"C:\fake-reader", VolumeSerial = "DISK-2" };
        var wpdDevice = new KindleDevice
        {
            RootPath = @"\\?\wpd-reader",
            VolumeSerial = @"\\?\wpd-reader",
            Transport = KindleTransport.Wpd
        };
        var hungProbe = new TaskCompletionSource<IReadOnlyList<KindleDevice>>();
        var starts = 0;
        var service = new KindleDeviceService(
            paths: null,
            metadata: new StubMetadata(),
            formatConverter: null,
            detectWpdDevicesAsync: _ => Interlocked.Increment(ref starts) == 1
                ? hungProbe.Task
                : Task.FromResult<IReadOnlyList<KindleDevice>>([wpdDevice]),
            detectMassStorageDevices: _ => [disk],
            wpdDetectionTimeout: TestTimeout);

        var first = await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(disk, Assert.Single(first));

        // The first task is still blocked, but a replug must be able to start
        // a fresh native probe instead of waiting on the dead handle forever.
        var second = await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, starts);
        Assert.Contains(second, candidate => ReferenceEquals(candidate, disk));
        Assert.Contains(second, candidate => ReferenceEquals(candidate, wpdDevice));
        hungProbe.TrySetResult([]);
    }

    [Fact]
    public async Task RepeatedTimeoutsDoNotAccumulateUnboundedWpdDetections()
    {
        var disk = new KindleDevice { RootPath = @"C:\fake-reader", VolumeSerial = "DISK-3" };
        var wpd = new ControlledWpdDetection();
        var service = CreateService(wpd, _ => [disk]);

        for (var refresh = 0; refresh < 5; refresh++)
            await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // One active probe plus one retry; further refreshes must not spawn
        // another native detection while both are still blocked.
        Assert.Equal(2, wpd.StartCount);

        // When one abandoned probe eventually completes, a slot is freed and
        // the next refresh can probe again (USB reconnect path).
        wpd.CompleteOne([]);
        await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, wpd.StartCount);
        wpd.CompleteAll();
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOneLiveWpdProbe()
    {
        var disk = new KindleDevice { RootPath = @"C:\fake-reader", VolumeSerial = "DISK-5" };
        var wpd = new ControlledWpdDetection();
        var service = CreateService(wpd, _ => [disk]);

        var first = service.DetectDevicesAsync();
        var second = service.DetectDevicesAsync();

        // Both refreshes must reuse the same in-flight native probe instead
        // of starting a second Shell/WPD enumeration.
        Assert.Equal(1, wpd.StartCount);
        wpd.CompleteAll();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, devices => Assert.Same(disk, Assert.Single(devices)));
    }

    [Fact]
    public async Task SuccessfulWpdDetectionIsMergedWithMassStorageDevices()
    {
        var disk = new KindleDevice { RootPath = @"C:\fake-reader", VolumeSerial = "DISK-4" };
        var wpdDevice = new KindleDevice
        {
            RootPath = @"\\?\wpd-reader",
            VolumeSerial = @"\\?\wpd-reader",
            Transport = KindleTransport.Wpd
        };
        var service = new KindleDeviceService(
            paths: null,
            metadata: new StubMetadata(),
            formatConverter: null,
            detectWpdDevicesAsync: _ => Task.FromResult<IReadOnlyList<KindleDevice>>([wpdDevice]),
            detectMassStorageDevices: _ => [disk],
            wpdDetectionTimeout: TestTimeout);

        var devices = await service.DetectDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, candidate => ReferenceEquals(candidate, disk));
        Assert.Contains(devices, candidate => ReferenceEquals(candidate, wpdDevice));
    }

    private static KindleDeviceService CreateService(
        ControlledWpdDetection wpd,
        Func<CancellationToken, IReadOnlyList<KindleDevice>> massStorage) =>
        new(
            paths: null,
            metadata: new StubMetadata(),
            formatConverter: null,
            detectWpdDevicesAsync: wpd.DetectAsync,
            detectMassStorageDevices: massStorage,
            wpdDetectionTimeout: TestTimeout);

    private sealed class ControlledWpdDetection
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<IReadOnlyList<KindleDevice>>> _pending = [];

        public int StartCount { get; private set; }

        public Task<IReadOnlyList<KindleDevice>> DetectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<IReadOnlyList<KindleDevice>>();
            lock (_gate)
            {
                _pending.Add(completion);
                StartCount++;
            }
            return completion.Task;
        }

        public void CompleteOne(IReadOnlyList<KindleDevice> devices)
        {
            TaskCompletionSource<IReadOnlyList<KindleDevice>>? completion;
            lock (_gate)
            {
                completion = _pending.Count > 0 ? _pending[0] : null;
                if (completion is not null) _pending.RemoveAt(0);
            }
            completion?.TrySetResult(devices);
        }

        public void CompleteAll()
        {
            TaskCompletionSource<IReadOnlyList<KindleDevice>>[] pending;
            lock (_gate)
            {
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var completion in pending)
                completion.TrySetResult([]);
        }
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BookMetadata());
    }
}
