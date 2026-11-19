using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS.Mtp;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Kindle devices that expose MTP instead of USB mass storage. macOS has no
/// MTP stack of its own, so this service talks to libmtp (the same library
/// calibre uses on macOS).
/// </summary>

/// <summary>
/// Sending, removing and exporting books.
/// </summary>
public sealed partial class MtpKindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        _scanCache.GetCachedBooksAsync(device.Identity, cancellationToken);

    // Enriched progress reports carry only the books changed in that batch
    // (at most eight), so the complete list is the scan's return value.
    public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        ScanBooksProgressivelyAsync(device, progress: null, cancellationToken);

    public async Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var identity = device.Identity;
        var cached = await _scanCache.GetDeviceEntriesAsync(identity, cancellationToken).ConfigureAwait(false);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(device, cached, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

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
                convertedDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "mtp-send", Guid.NewGuid().ToString("N"));
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
                bookFile = new BookFile
                {
                    Id = bookFile.Id,
                    BookId = bookFile.BookId,
                    Format = "azw3",
                    RelativePath = Path.GetFileName(convertedPath),
                    Size = new FileInfo(convertedPath).Length,
                    Sha256 = await Hashing.Sha256Async(convertedPath, cancellationToken).ConfigureAwait(false)
                };
                sourcePath = convertedPath;
            }

            if (!device.Profile.SupportsBookFile(sourcePath))
                throw new NotSupportedException(UiText.Get("当前设备不支持此书籍格式。"));

            // The filesystem transports refuse to replace a device copy when
            // the source no longer matches the hash recorded at import. MTP
            // previously only compared sizes, so same-length corruption could
            // overwrite a valid device copy.
            if (!string.IsNullOrWhiteSpace(bookFile.Sha256))
            {
                var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
                if (!sourceHash.Equals(bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("书籍文件校验失败，源文件可能已损坏。请重新导入后再发送。");
            }

            var fileName = KindleTransferPolicy.CreateSafeFileName(
                Path.GetFileNameWithoutExtension(sourcePath),
                Path.GetExtension(sourcePath),
                bookFile.Id);
            var total = new FileInfo(sourcePath).Length;
            // Kindle bookshelves use the EXTH ASIN/CDE pair for cover names.
            // Books without an ASIN simply have no device thumbnail.
            var thumbnail = device.Profile.UsesKindleThumbnails
                ? await KindleThumbnailService.CreateAsync(
                    sourcePath, _metadata, cancellationToken, coverOverridePath).ConfigureAwait(false)
                : null;

            await WithSessionAsync(device, session =>
            {
                var folder = session.EnsureDirectory(device.Profile.BooksDirectory);
                var bookPath = $"{folder.RelativePath}/{fileName}";
                var isNewBook = session.Find(bookPath) is null;
                session.Upload(
                    folder.RelativePath,
                    sourcePath,
                    fileName,
                    (sent, size) => progress?.Report(new TransferProgress(sent, size, "正在发送到设备")),
                    cancellationToken);
                if (thumbnail is not null)
                {
                    progress?.Report(new TransferProgress(total, total, "正在同步设备书架封面"));
                    UploadThumbnail(session, thumbnail, cancellationToken);
                }
                if (isNewBook)
                    DeleteSidecarFolder(session, device.Profile.BooksDirectory, fileName);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            progress?.Report(new TransferProgress(total, total, "已发送"));
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

    public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath))
            throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        return WithSessionAsync(device, session =>
        {
            var entry = session.Find(book.RelativePath)
                ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
            var separator = entry.RelativePath.LastIndexOf('/');
            var directory = separator > 0 ? entry.RelativePath[..separator] : string.Empty;
            session.Delete(entry);
            // Reading progress, bookmarks and annotations live in the matching
            // .sdr folder; leaving it behind makes Amazon's device view think
            // the book is still installed.
            DeleteSidecarFolder(session, directory, entry.Name);
            return true;
        }, cancellationToken);
    }

    public async Task<string> ExportBookAsync(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.IsBookPath(book.RelativePath))
            throw new InvalidOperationException(UiText.Get("设备书籍路径无效。"));
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destination = UniqueDestination(destinationRoot, SafeFileName(book.FileName));
        var temporary = destination + ".libzora-part";
        try
        {
            await WithSessionAsync(device, session =>
            {
                var entry = session.Find(book.RelativePath)
                    ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
                session.Download(entry, temporary,
                    (sent, total) => progress?.Report(new TransferProgress(sent, total, "正在导出")),
                    cancellationToken);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            TryDelete(temporary);
        }
    }
}
