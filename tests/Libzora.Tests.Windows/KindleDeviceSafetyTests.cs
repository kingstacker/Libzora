using Libzora.Core;
using Libzora.Platform.Windows;

namespace Libzora.Tests.Windows;

public sealed class KindleDeviceSafetyTests
{
    [Fact]
    public async Task FailedBookDeletionKeepsReadingStateForTheRetry()
    {
        // FileShare is advisory on Unix, so only Windows reliably blocks the
        // delete while the handle is open. The test project targets Windows.
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateTemporaryDirectory();
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var bookPath = Path.Combine(documents, "locked.azw3");
        await File.WriteAllTextAsync(bookPath, "book");
        var stateDirectory = Path.Combine(documents, "locked.sdr");
        Directory.CreateDirectory(stateDirectory);
        var positionPath = Path.Combine(stateDirectory, "position.json");
        await File.WriteAllTextAsync(positionPath, "78");

        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var book = new KindleBook
            {
                RelativePath = Path.Combine("documents", "locked.azw3")
            };

            using (new FileStream(bookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // FileShare.Read denies delete sharing, so DeleteFile fails
                // with a sharing violation (IOException) while the file is
                // open, exactly like a book in use on the device.
                var failure = await Record.ExceptionAsync(
                    () => service.RemoveBookAsync(device, book));

                Assert.True(
                    failure is IOException or UnauthorizedAccessException,
                    $"Expected a sharing violation, got {failure?.GetType().Name ?? "success"}.");
                Assert.True(File.Exists(bookPath));
                Assert.Equal("78", await File.ReadAllTextAsync(positionPath));
            }

            await service.RemoveBookAsync(device, book);

            Assert.False(File.Exists(bookPath));
            Assert.False(Directory.Exists(stateDirectory));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CancelledResourceExportLeavesExistingDestinationUntouched()
    {
        var root = CreateTemporaryDirectory();
        var fonts = Path.Combine(root, "fonts");
        Directory.CreateDirectory(fonts);
        var source = Path.Combine(fonts, "reader.ttf");
        await File.WriteAllBytesAsync(source, new byte[1024 * 1024]);
        var destinationDirectory = Path.Combine(root, "export");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "reader.ttf");
        await File.WriteAllTextAsync(destination, "existing export");

        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var resource = new KindleDeviceResource
            {
                Kind = KindleResourceKind.Font,
                RelativePath = Path.Combine("fonts", "reader.ttf"),
                Size = 1024 * 1024
            };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.ExportResourceAsync(device, resource, destination, cancellation.Token));

            Assert.Equal("existing export", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.EnumerateFiles(
                destinationDirectory,
                "*.libzora-part",
                SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ResourceExportPublishesOnlyAfterTheCompleteCopy()
    {
        var root = CreateTemporaryDirectory();
        var fonts = Path.Combine(root, "fonts");
        Directory.CreateDirectory(fonts);
        var content = new byte[512 * 1024];
        Random.Shared.NextBytes(content);
        var source = Path.Combine(fonts, "reader.ttf");
        await File.WriteAllBytesAsync(source, content);
        var destinationDirectory = Path.Combine(root, "export");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "reader.ttf");
        await File.WriteAllTextAsync(destination, "stale export");

        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var resource = new KindleDeviceResource
            {
                Kind = KindleResourceKind.Font,
                RelativePath = Path.Combine("fonts", "reader.ttf"),
                Size = content.Length
            };

            await service.ExportResourceAsync(device, resource, destination);

            Assert.Equal(content, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.EnumerateFiles(
                destinationDirectory,
                "*.libzora-part",
                SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LibzoraTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
