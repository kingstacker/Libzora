using Libzora.Core;
using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task DeleteDuringOnDemandDownloadDoesNotResurrectFileOrChangeRemoteState()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        // Keep the lazy-download path active instead of fetching bytes during sync.
        b.Settings = b.Settings with { DownloadBookFilesOnSync = false };
        var source = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        // Keep a second local format so deleting the remote-only format uses
        // the per-file trash path instead of deleting the whole book.
        await AddLifecycleSecondFormatAsync(b, source.BookId);

        var remoteBook = Assert.Single(await b.Library.SearchAsync());
        var remoteFile = Assert.Single(remoteBook.Files, file => file.Format == "epub");
        var targetPath = Path.Combine(b.Paths.Data, remoteFile.RelativePath);
        Assert.False(File.Exists(targetPath));
        Assert.Equal(1, (await b.Service.GetStatusAsync(b.Id, b.Settings)).PendingFileDownloadCount);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bucket.BeforeGet = async (key, cancellationToken) =>
        {
            if (!key.Contains("/objects/", StringComparison.Ordinal)) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        Task<string>? download = null;
        try
        {
            download = b.Service.EnsureBookFileDownloadedAsync(b.Id, b.Settings, remoteFile);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await b.Library.DeleteFileAsync(source.BookId, remoteFile.Id);
            var trashItem = Assert.Single(await b.Library.GetTrashItemsAsync());
            Assert.Equal(LibraryTrashItemKind.File, trashItem.Kind);

            release.TrySetResult();
            await Assert.ThrowsAsync<SyncedBookFileChangedException>(
                () => download!.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.False(File.Exists(targetPath));
            // The aborted download must not clear the remote-only marker.
            Assert.Equal(1, (await b.Service.GetStatusAsync(b.Id, b.Settings)).PendingFileDownloadCount);
            var bookDirectory = Path.GetDirectoryName(targetPath)!;
            Assert.Empty(Directory.Exists(bookDirectory)
                ? Directory.GetFiles(bookDirectory, "*.part")
                : Array.Empty<string>());

            // The recycle-bin entry stays restorable because the deleted path
            // was not re-created by the in-flight download.
            await b.Library.RestoreTrashItemAsync(trashItem.Id);
            Assert.Equal(2, Assert.Single(await b.Library.SearchAsync()).Files.Count);
            Assert.False(File.Exists(targetPath));
        }
        finally
        {
            release.TrySetResult();
            if (download is not null)
            {
                try { await download; }
                catch { /* Observe the worker before disposing the store. */ }
            }
            bucket.BeforeGet = null;
        }
    }
}
