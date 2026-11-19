using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>
/// Sending, removing and exporting books.
/// </summary>
public sealed partial class MassStorageKindleDeviceService : IKindleDeviceService
{
    public async Task SendBookAsync(
        KindleDevice device,
        BookFile bookFile,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? coverOverridePath = null)
    {
        EnsureMassStorage(device);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        string? convertedDirectory = null;
        try
        {
            if (DeviceTransferPolicy.RequiresKindleConversion(device.Profile, bookFile))
            {
                convertedDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "usb-send", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(convertedDirectory);
                var displayTitle = Path.GetFileNameWithoutExtension(sourcePath);
                var convertedPath = Path.Combine(
                    convertedDirectory,
                    KindleTransferPolicy.CreateSafeFileName(displayTitle, ".azw3"));
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    convertedPath,
                    cancellationToken: cancellationToken,
                    metadata: new FormatConversionMetadata(displayTitle, string.Empty, coverOverridePath));
                var convertedInfo = new FileInfo(convertedPath);
                bookFile = new BookFile
                {
                    Id = bookFile.Id,
                    BookId = bookFile.BookId,
                    Format = "azw3",
                    RelativePath = Path.GetFileName(convertedPath),
                    Size = convertedInfo.Length,
                    Sha256 = await Hashing.Sha256Async(convertedPath, cancellationToken)
                };
                sourcePath = convertedPath;
            }

        if (!device.Profile.SupportsBookFile(sourcePath))
            throw new NotSupportedException(UiText.Get("当前设备不支持此书籍格式。"));
        var documents = GetDocumentsRoot(device);
        Directory.CreateDirectory(documents);
        var fileName = KindleTransferPolicy.CreateSafeFileName(
            Path.GetFileNameWithoutExtension(sourcePath),
            Path.GetExtension(sourcePath),
            bookFile.Id);
        // Re-sending a book replaces the existing copy instead of creating a
        // "title (2).azw3" duplicate, so updated covers and metadata actually
        // reach the book's existing Kindle entry.
        var destination = Path.Combine(documents, fileName);
        var temporary = destination + ".libzora-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            await CopyAsync(sourcePath, temporary, total, progress, cancellationToken);
            var hash = await Hashing.Sha256Async(temporary, cancellationToken);
            if (!string.IsNullOrWhiteSpace(bookFile.Sha256)
                && !hash.Equals(bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备上的文件未被替换。");
            if (!File.Exists(destination)) KindleReadingState.DeleteForRemovedBook(destination);
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);

            var thumbnail = device.Profile.UsesKindleThumbnails
                ? await KindleThumbnailService.CreateAsync(sourcePath, _metadata, cancellationToken, coverOverridePath)
                : null;
            if (thumbnail is not null)
                await WriteThumbnailAsync(device, thumbnail, cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
        }
        finally
        {
            if (convertedDirectory is not null)
            {
                try { Directory.Delete(convertedDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath)) throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveWithinRoot(device.RootPath, book.RelativePath, GetDocumentsRoot(device));
        RejectLink(path);
        if (!File.Exists(path)) throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
        // Remove the book first. A failed or locked book file must not take
        // the reading state down with it: the .sdr is only cleared once the
        // book is actually gone, so a failed removal leaves the book usable.
        File.Delete(path);
        // The book is already gone. A stale or link-shaped .sdr must not turn
        // a successful removal into an error the user cannot act on.
        try
        {
            KindleReadingState.DeleteForRemovedBook(path);
        }
        catch (IOException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Reading state could not be removed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Reading state could not be removed: {exception.Message}");
        }
        await Task.CompletedTask;
    }

    public async Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath)) throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        EnsureMassStorage(device);
        var source = ResolveWithinRoot(device.RootPath, book.RelativePath, GetDocumentsRoot(device));
        RejectLink(source);
        if (!File.Exists(source)) throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destination = GetUniqueDestination(destinationRoot, SafeFileName(book.FileName));
        var temporary = destination + ".libzora-part";
        try
        {
            await CopyAsync(source, temporary, new FileInfo(source).Length, progress, cancellationToken, "正在导出");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
            return destination;
        }
        finally
        {
            TryDelete(temporary);
        }
    }
}
