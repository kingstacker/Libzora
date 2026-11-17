using Libzora.Core;
using Libzora.Platform.MacOS.Mtp;
using Xunit;

namespace Libzora.Platform.Common.Tests;

public sealed class MacOSMtpIdentityTests
{
    [Fact]
    public void SerialIdentityMatchesOnlyTheSameReader()
    {
        var device = Device("mtp:B0D123");

        Assert.True(MtpDeviceIdentity.Matches(device, "B0D123", "Kindle"));
        Assert.True(MtpDeviceIdentity.Matches(device, "b0d123", "Kindle Paperwhite"));
        Assert.False(MtpDeviceIdentity.Matches(device, "B0D999", "Kindle"));
        Assert.True(MtpDeviceIdentity.CanReconnectByIdentity(device));
    }

    [Fact]
    public void ModelFallbackCannotBeUsedToReidentifyAfterReplug()
    {
        var device = Device("mtp:model:Kindle Scribe");

        Assert.False(MtpDeviceIdentity.CanReconnectByIdentity(device));
        Assert.True(MtpDeviceIdentity.Matches(device, null, "Kindle Scribe"));
        Assert.True(MtpDeviceIdentity.Matches(device, "  ", "Kindle Scribe"));
        Assert.False(MtpDeviceIdentity.Matches(device, null, "Kindle"));
    }

    [Fact]
    public void RootOnlyIdentityRequiresTheExactTopology()
    {
        var device = new KindleDevice
        {
            RootPath = "mtp://1/7",
            Transport = KindleTransport.Mtp
        };

        Assert.True(MtpDeviceIdentity.Matches(device, "ignored", "Kindle", "mtp://1/7"));
        Assert.False(MtpDeviceIdentity.Matches(device, "ignored", "Kindle", "mtp://1/8"));
        Assert.False(MtpDeviceIdentity.Matches(device, "ignored", "Kindle"));
    }

    [Fact]
    public void ReplacingStoppedReaderWithAnotherKindleReleasesTheLatch()
    {
        var tracker = new MtpAttachmentTracker();
        tracker.Observe(new[] { ("mtp:A", "mtp://1/1"), ("mtp:B", "mtp://1/2") });

        // A was stopped and unplugged; B is still attached on another port.
        Assert.False(tracker.IsAttached("mtp:A", new[] { "mtp://1/2" }));
        Assert.True(tracker.IsAttached("mtp:B", new[] { "mtp://1/2" }));
    }

    [Fact]
    public void UnknownIdentityFailsClosedWhileAnotherKindleIsAttached()
    {
        var tracker = new MtpAttachmentTracker();
        Assert.True(tracker.IsAttached("mtp:unknown", new[] { "mtp://1/2" }));
    }

    [Fact]
    public void NoKindleAtAllIsDefinitelyDetached()
    {
        var tracker = new MtpAttachmentTracker();
        Assert.False(tracker.IsAttached("mtp:unknown", Array.Empty<string>()));
        Assert.False(tracker.IsAttached(null, Array.Empty<string>()));
    }

    [Fact]
    public void AmbiguousModelIdentityFailsClosed()
    {
        var tracker = new MtpAttachmentTracker();
        tracker.Observe(new[]
        {
            ("mtp:model:Kindle", "mtp://1/1"),
            ("mtp:model:Kindle", "mtp://1/2")
        });

        Assert.True(tracker.IsAttached("mtp:model:Kindle", new[] { "mtp://1/2" }));
    }

    [Fact]
    public void ReplugObservationReplacesTheRememberedRoot()
    {
        var tracker = new MtpAttachmentTracker();
        tracker.Observe(new[] { ("mtp:A", "mtp://1/1") });
        tracker.Observe(new[] { ("mtp:A", "mtp://1/9") });

        Assert.True(tracker.IsAttached("mtp:A", new[] { "mtp://1/9" }));
        Assert.False(tracker.IsAttached("mtp:A", new[] { "mtp://1/1" }));
    }

    [Fact]
    public void RootPathIdentityMatchesRawTopologyOnly()
    {
        var tracker = new MtpAttachmentTracker();
        Assert.True(tracker.IsAttached("mtp://1/9", new[] { "mtp://1/9" }));
        Assert.False(tracker.IsAttached("mtp://1/9", new[] { "mtp://1/8" }));
    }

    [Fact]
    public void RawRootPathFormatIsStable()
    {
        var raw = new MtpRawDevice(7, 3, 0x1949, 0x0001, []);
        Assert.Equal("mtp://7/3", MtpDeviceIdentity.RootPath(raw));
    }

    private static KindleDevice Device(string identity) => new()
    {
        RootPath = "mtp://1/1",
        VolumeSerial = identity,
        Transport = KindleTransport.Mtp
    };
}
