using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS;
using Xunit;

namespace Libzora.Platform.Common.Tests;

public sealed class MacOSMtpDetectionTests
{
    [Fact]
    public async Task EarlyEmptyResultExpiresBeforeTheNextIdlePoll()
    {
        using var scope = new DetectionScope();
        var clock = new DetectionClock();
        var reader = Reader("READY");
        var requests = 0;
        var service = new MtpKindleDeviceService(scope.Paths, new BookMetadataService(),
            _ => ++requests == 1 ? [] : [reader], clock);

        Assert.Empty(await service.DetectDevicesAsync());
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Empty(await service.DetectDevicesAsync());
        Assert.Equal(1, requests);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Same(reader, Assert.Single(await service.DetectDevicesAsync()));
        Assert.Equal(2, requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(reader, Assert.Single(await service.DetectDevicesAsync()));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task UsbChangeDuringDetectionDiscardsTheOldTopology()
    {
        using var scope = new DetectionScope();
        var oldReader = Reader("OLD");
        var newReader = Reader("NEW");
        var requests = 0;
        MtpKindleDeviceService service = null!;
        service = new MtpKindleDeviceService(scope.Paths, new BookMetadataService(), _ =>
        {
            if (++requests != 1) return [newReader];
            service.InvalidateDetectionCache();
            return [oldReader];
        }, new DetectionClock());

        Assert.Same(newReader, Assert.Single(await service.DetectDevicesAsync()));
        Assert.Same(newReader, Assert.Single(await service.DetectDevicesAsync()));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task UsbChangeImmediatelyInvalidatesSuccessfulDetection()
    {
        using var scope = new DetectionScope();
        var requests = 0;
        var reader = Reader("REMOVED");
        var service = new MtpKindleDeviceService(scope.Paths, new BookMetadataService(),
            _ => ++requests == 1 ? [reader] : [], new DetectionClock());

        Assert.Single(await service.DetectDevicesAsync());
        service.InvalidateDetectionCache();
        Assert.Empty(await service.DetectDevicesAsync());
        Assert.Equal(2, requests);
    }

    private static KindleDevice Reader(string serial) => new()
    {
        RootPath = "mtp://1/1", VolumeSerial = "mtp:" + serial,
        Transport = KindleTransport.Mtp, IsReady = true
    };

    private sealed class DetectionClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class DetectionScope : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "libzora-mtp-tests", Guid.NewGuid().ToString("N")));
        public void Dispose() => Directory.Delete(Paths.Root, recursive: true);
    }
}
