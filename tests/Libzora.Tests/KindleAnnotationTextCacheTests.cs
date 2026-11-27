using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed partial class KindleAnnotationSyncServiceTests
{
    private static KindleClipping DisplayClipping(Scenario scenario) => new()
    {
        Id = "kindle-db:device-highlight", BookTitle = scenario.Book.Title,
        Type = KindleClippingType.Highlight, SourceBook = DeviceHighlight(scenario).Book,
        StartPosition = 1537, EndPosition = 1558, IsReadOnly = true, Content = "划线原文待定位"
    };

    private static void VersionDeviceBook(Scenario scenario)
    {
        scenario.DeviceBook.ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
        scenario.DeviceBook.Size = new FileInfo(scenario.Azw3).Length;
    }

    [Fact]
    public async Task DisplayAndAnalysisShareTextButSynchronizationReadsFreshEdition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, [DeviceHighlight(s)]);
            var clip = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            Assert.Equal("你好，世界。😀", clip.Content);
            await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, fake.ExportCount);
            await s.Sync.SynchronizeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ConcurrentTextRequestsShareOneTransferAndFollowerCancellationDoesNotCancelOwner()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = Fake(s, []);
            fake.BeforeExport = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
            var cache = new KindleAnnotationBookTextCache();
            var first = cache.ReadAsync(s.Device, s.DeviceBook, fake, true, CancellationToken.None);
            await entered.Task;
            using var cancel = new CancellationTokenSource();
            var canceled = cache.ReadAsync(s.Device, s.DeviceBook, fake, true, cancel.Token);
            var follower = cache.ReadAsync(s.Device, s.DeviceBook, fake, true, CancellationToken.None);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            release.SetResult();
            Assert.Same((await first).Text, (await follower).Text);
            Assert.Equal(1, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PersistedDisplayTextIsReusedOnlyForUnchangedRecordAndFileVersion()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, []);
            var old = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [old], fake);
            var paths = new AppPaths(Path.Combine(root, "app"));
            await new KindleDeviceAuxiliaryCacheStore(paths).SaveAsync(s.Device.Identity, new() { Clippings = [old] });
            var saved = (await new KindleDeviceAuxiliaryCacheStore(paths).GetAsync(s.Device.Identity))!.Clippings;
            var current = DisplayClipping(s);
            KindleAnnotationClippingTextResolver.RestoreCachedText(s.Device, [s.DeviceBook], [current], saved);
            // A restarted reader has no raw text cache, but needs no device copy to display this row.
            await KindleAnnotationClippingTextResolver.ResolveAsync(s.Device, [s.DeviceBook], [current], fake);
            Assert.Equal(1, fake.ExportCount);
            Assert.Equal(old.Content, current.Content);

            var changed = DisplayClipping(s);
            changed.StartPosition = 1546;
            KindleAnnotationClippingTextResolver.RestoreCachedText(s.Device, [s.DeviceBook], [changed], saved);
            Assert.True(KindleAnnotationClippingTextResolver.NeedsResolution(changed));
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [changed], fake);
            Assert.Equal("世界。😀", changed.Content);
            Assert.Equal(1, fake.ExportCount);

            s.DeviceBook.ModifiedAt = s.DeviceBook.ModifiedAt!.Value.AddMinutes(1);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [current], fake);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task UnrelatedUnversionedBookDoesNotInvalidateResolvedEdition()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, []);
            var clip = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            var unrelated = new KindleBook { RelativePath = "documents/other.kfx", Format = "kfx" };
            await KindleAnnotationClippingTextResolver.ResolveAsync(s.Device, [s.DeviceBook, unrelated], [clip], fake);
            Assert.True(clip.IsTextResolved);
            Assert.Equal(1, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task PersistedTextCannotBeReusedForAnotherDevice()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, []);
            var clip = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            var other = new KindleDevice { RootPath = "other", VolumeSerial = "OTHER", Transport = KindleTransport.Wpd };
            await s.Sync.ResolveClippingTextAsync(other, [s.DeviceBook], [clip], fake);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task MissingIdentityFailureSurvivesRefreshUntilInventoryChanges()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, []);
            var clip = DisplayClipping(s);
            clip.SourceBook = new("missing", "foreign", "foreign:12345678");
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            Assert.False(clip.IsTextResolved);
            Assert.False(KindleAnnotationClippingTextResolver.NeedsResolution(clip));
            var saved = JsonSerializer.Deserialize<KindleClipping>(JsonSerializer.Serialize(clip))!;
            await KindleAnnotationClippingTextResolver.ResolveAsync(s.Device, [s.DeviceBook], [saved], fake);
            Assert.Equal(1, fake.ExportCount);
            s.DeviceBook.ModifiedAt = s.DeviceBook.ModifiedAt!.Value.AddMinutes(1);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [saved], fake);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task UnsupportedEditionIsCachedButExplicitRetryReadsAgain()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, []);
            fake.ExportFailure = new NotSupportedException("Unsupported fixture");
            var clip = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            await s.Sync.AnalyzeAsync(s.Device, [s.DeviceBook], fake);
            Assert.Equal(1, fake.ExportCount);
            s.Sync.RetryUnresolvedClippingText();
            KindleAnnotationClippingTextResolver.RestoreCachedText(s.Device, [s.DeviceBook], [clip], [clip], retryUnresolved: true);
            fake.ExportFailure = null;
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            Assert.True(clip.IsTextResolved);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task TransientReadFailureIsNotPersistedAndNextLoadRetries()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var fake = Fake(s, []);
            fake.ExportFailure = new IOException("Transient disconnection");
            var clip = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            Assert.Null(clip.TextResolutionStamp);
            Assert.True(KindleAnnotationClippingTextResolver.NeedsResolution(clip));
            fake.ExportFailure = null;
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            Assert.True(clip.IsTextResolved);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task UnversionedBooksAreNotReusedAfterReadCompletes()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            var fake = Fake(s, []);
            var clip = DisplayClipping(s);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            await s.Sync.ResolveClippingTextAsync(s.Device, [s.DeviceBook], [clip], fake);
            Assert.Equal(2, fake.ExportCount);
            Assert.Null(clip.TextResolutionStamp);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CanceledOwnerDoesNotPoisonFutureRead()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var s = await Setup(root);
            VersionDeviceBook(s);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = Fake(s, []);
            fake.BeforeExport = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
            var cache = new KindleAnnotationBookTextCache();
            using var cancel = new CancellationTokenSource();
            var read = cache.ReadAsync(s.Device, s.DeviceBook, fake, true, cancel.Token);
            await entered.Task;
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            fake.BeforeExport = null;
            Assert.NotNull((await cache.ReadAsync(s.Device, s.DeviceBook, fake, true, CancellationToken.None)).Text);
            Assert.Equal(2, fake.ExportCount);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
