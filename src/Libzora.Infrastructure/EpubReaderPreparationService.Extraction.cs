using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Libzora.Infrastructure;

/// <summary>
/// Safe ZIP extraction and entry copying.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private sealed record ManifestItem(string? Id, string? Href, string? MediaType, string? Properties);

    private static async Task ExtractSafelyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExtractWithZipArchiveAsync(epubPath, destinationRoot, cancellationToken);
            return;
        }
        catch (InvalidDataException)
        {
            // Some publisher and store pipelines ship EPUBs whose end-of-
            // central-directory record disagrees with the actual entry count.
            // The archive itself is readable, so fall back rather than
            // rejecting the book outright.
        }

        try
        {
            await ExtractFromCentralDirectoryAsync(epubPath, destinationRoot, cancellationToken);
            return;
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            // The central directory itself is unusable. Local file headers
            // are self-describing, so read the members sequentially.
        }

        await ExtractSequentiallyAsync(epubPath, destinationRoot, cancellationToken);
    }

    private static async Task ExtractSequentiallyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = new ICSharpCode.SharpZipLib.Zip.ZipInputStream(input);
        var extracted = 0;
        var entryCount = 0;
        long expandedBytes = 0;
        while (archive.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > MaximumEpubEntries)
                throw new EpubExtractionLimitException("EPUB 包含过多文件，已拒绝解压。");
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            expandedBytes += await CopyEpubEntryAsync(archive, output, entry.Size, expandedBytes, cancellationToken);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidDataException("EPUB 压缩包已损坏，无法读取。");
    }

    private static async Task ExtractWithZipArchiveAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEpubEntries)
            throw new EpubExtractionLimitException("EPUB 包含过多文件，已拒绝解压。");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.FullName)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (entry.Length > MaximumEpubEntryBytes
                || entry.Length > MaximumEpubExpandedBytes - expandedBytes)
                throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            expandedBytes += await CopyEpubEntryAsync(source, output, entry.Length, expandedBytes, cancellationToken);
        }
    }

    private static async Task ExtractFromCentralDirectoryAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        // SharpZipLib addresses entries through the central directory, so a
        // single unreadable member (a corrupt embedded font, a truncated
        // image) can be skipped instead of aborting the whole book.
        using var archive = new ICSharpCode.SharpZipLib.Zip.ZipFile(epubPath);
        var extracted = 0;
        var entryCount = 0;
        long expandedBytes = 0;
        foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in archive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > MaximumEpubEntries)
                throw new EpubExtractionLimitException("EPUB 包含过多文件，已拒绝解压。");
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (entry.Size > MaximumEpubEntryBytes
                || entry.Size > MaximumEpubExpandedBytes - expandedBytes)
                throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                await using var source = archive.GetInputStream(entry);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                expandedBytes += await CopyEpubEntryAsync(source, output, entry.Size, expandedBytes, cancellationToken);
                extracted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception
                is ICSharpCode.SharpZipLib.SharpZipBaseException
                or InvalidDataException
                or NotSupportedException)
            {
                // Leave nothing half-written: a truncated XHTML page would
                // otherwise reach the sanitizer as plausible-looking markup.
                TryDeleteFile(destination);
            }
        }

        if (extracted == 0)
            throw new InvalidDataException("EPUB 压缩包已损坏，无法读取。");
    }

    private static async Task<long> CopyEpubEntryAsync(
        Stream source,
        Stream destination,
        long declaredLength,
        long expandedBeforeEntry,
        CancellationToken cancellationToken)
    {
        var maximumEntryRemaining = MaximumEpubEntryBytes;
        var maximumPackageRemaining = MaximumEpubExpandedBytes - expandedBeforeEntry;
        if (declaredLength > maximumEntryRemaining || declaredLength > maximumPackageRemaining)
            throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            copied += read;
            if (copied > maximumEntryRemaining || copied > maximumPackageRemaining)
                throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return copied;
    }

    private sealed class EpubExtractionLimitException(string message) : IOException(message);

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
