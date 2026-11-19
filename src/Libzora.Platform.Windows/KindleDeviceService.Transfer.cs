using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Sending, exporting and removing books.
/// </summary>
public sealed partial class KindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public async Task SendBookAsync(
        KindleDevice device,
        BookFile bookFile,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? coverOverridePath = null)
    {
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
        var thumbnail = device.Profile.UsesKindleThumbnails
            ? await KindleThumbnailService.CreateAsync(sourcePath, _metadata, cancellationToken, coverOverridePath)
            : null;
        if (device.Transport == KindleTransport.Wpd)
        {
            var wpdFileName = KindleTransferPolicy.CreateSafeFileName(
                Path.GetFileNameWithoutExtension(sourcePath),
                Path.GetExtension(sourcePath),
                bookFile.Id);
            await Task.Run(
                () => WpdKindleAccess.SendBook(device, sourcePath, wpdFileName, thumbnail, progress, cancellationToken),
                cancellationToken);
            return;
        }
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
            if (!string.Equals(hash, bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备上的文件未被替换。");
            if (!File.Exists(destination)) KindleReadingState.DeleteForRemovedBook(destination);
            File.Move(temporary, destination, true);
            if (thumbnail is not null)
            {
                progress?.Report(new TransferProgress(total, total, "正在同步设备书架封面"));
                await WriteMassStorageThumbnailAsync(device, thumbnail, cancellationToken);
            }
        }
        finally
        {
            await TryDeleteAsync(temporary);
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

    private static async Task WriteMassStorageThumbnailAsync(
        KindleDevice device,
        KindleThumbnail thumbnail,
        CancellationToken cancellationToken)
    {
        var deviceRoot = Path.GetFullPath(device.RootPath);
        var thumbnailDirectory = Path.GetFullPath(Path.Combine(deviceRoot, "system", "thumbnails"));
        EnsureUnderRoot(thumbnailDirectory, deviceRoot);
        Directory.CreateDirectory(thumbnailDirectory);
        var target = Path.GetFullPath(Path.Combine(thumbnailDirectory, thumbnail.FileName));
        EnsureUnderRoot(target, thumbnailDirectory);
        var temporary = target + ".libzora-part";
        try
        {
            await File.WriteAllBytesAsync(temporary, thumbnail.JpegBytes, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            await TryDeleteAsync(temporary);
        }
    }

    public async Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath)) throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (device.Transport == KindleTransport.Wpd)
        {
            progress?.Report(new TransferProgress(0, book.Size, $"正在从设备读取 {book.FileName}"));
            var exportedPath = await Task.Run(
                () => WpdKindleAccess.CopyBookToLocal(device, book, destinationRoot, cancellationToken),
                cancellationToken);
            progress?.Report(new TransferProgress(book.Size, book.Size, $"已读取 {book.FileName}"));
            return exportedPath;
        }

        var documents = GetDocumentsRoot(device);
        var sourcePath = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
        EnsureUnderRoot(sourcePath, documents);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);

        var destinationPath = GetUniqueDestination(destinationRoot, GetSafeFileName(book.FileName));
        var temporaryPath = destinationPath + ".libzora-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            await CopyAsync(sourcePath, temporaryPath, total, progress, cancellationToken, "正在导出");
            File.Move(temporaryPath, destinationPath, true);
            return destinationPath;
        }
        finally
        {
            await TryDeleteAsync(temporaryPath);
        }
    }

    public async Task RemoveBookAsync(
        KindleDevice device,
        KindleBook book,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath)) throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.RemoveBook(device, book, cancellationToken), cancellationToken);
            return;
        }

        var documents = GetDocumentsRoot(device);
        var path = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
        EnsureUnderRoot(path, documents);
        if (!File.Exists(path)) throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不能删除链接形式的设备文件。");

        // Remove the book first. A failed or locked book file must not take
        // the .sdr reading state with it; clear that state only after the book
        // is actually gone so a retry can still resume at the old position.
        File.Delete(path);
        KindleReadingState.DeleteForRemovedBook(path);
    }
}
