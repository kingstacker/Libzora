using Libzora.Infrastructure;
using Libzora.Platform.Common;
using Xunit;

namespace Libzora.Platform.Common.Tests;

public sealed class AppleICloudSyncFileSystemTests
{
    [Fact]
    public void NativeCoordinationPreservesManagedExceptionsAndSupportsReadWriteAndIdempotentDelete()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var directory = Path.Combine(Path.GetTempPath(), "libzora-icloud-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fileSystem = new AppleICloudSyncFileSystem();
            var path = Path.Combine(directory, "fixture.bin");
            fileSystem.Coordinate(path, ICloudSyncFileAccess.Write, actual =>
            {
                File.WriteAllText(actual, "native coordinated fixture");
                return true;
            }, CancellationToken.None);
            Assert.True(fileSystem.ItemExists(path));
            Assert.Equal("native coordinated fixture", fileSystem.Coordinate(path, ICloudSyncFileAccess.Read,
                File.ReadAllText, CancellationToken.None));
            var expected = new InvalidOperationException("managed fixture failure");
            var thrown = Assert.Throws<InvalidOperationException>(() => fileSystem.Coordinate<int>(path,
                ICloudSyncFileAccess.Read, _ => throw expected, CancellationToken.None));
            Assert.Same(expected, thrown);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => fileSystem.Coordinate(path,
                ICloudSyncFileAccess.Read, _ => true, cancelled.Token));
            fileSystem.Coordinate(path, ICloudSyncFileAccess.Delete, actual =>
            {
                fileSystem.DeleteItem(actual);
                return true;
            }, CancellationToken.None);
            fileSystem.DeleteItem(path);
            Assert.False(fileSystem.ItemExists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OrdinaryLocalFilesAreNotAcknowledgedAsICloudUploadsAndWaitingCanBeCancelled()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var path = Path.Combine(Path.GetTempPath(), "libzora-icloud-unuploaded-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllTextAsync(path, "local fixture, not an iCloud item");
            var fileSystem = new AppleICloudSyncFileSystem();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fileSystem.EnsureUploadedAsync(path, cancellation.Token));
        }
        finally { File.Delete(path); }
    }
}
