using System.Buffers;
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
    private const int InlineSanitizeThresholdBytes = 4 * 1024 * 1024;

    private sealed record ManifestItem(string? Id, string? Href, string? MediaType, string? Properties);

    private static async Task<bool> ExtractSafelyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExtractWithZipArchiveAsync(epubPath, destinationRoot, cancellationToken);
            return true;
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
            return false;
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            // The central directory itself is unusable. Local file headers
            // are self-describing, so read the members sequentially.
        }

        await ExtractSequentiallyAsync(epubPath, destinationRoot, cancellationToken);
        return false;
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
        using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEpubEntries)
            throw new EpubExtractionLimitException("EPUB 包含过多文件，已拒绝解压。");

        var normalizedRoot = NormalizeRootPath(destinationRoot);
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var createdDirectories = new HashSet<string>(pathComparer);
        var batch = new List<(string Destination, byte[] Buffer, int Length, bool IsCss)>(64);
        var batchDestinations = new HashSet<string>(pathComparer);
        var oversizedHtmlFiles = new List<string>();
        var oversizedCssFiles = new List<string>();
        long batchBytes = 0;
        long expandedBytes = 0;
        var copyBuffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.FullName)) continue;

                var destination = ResolveContainedPathNormalized(normalizedRoot, destinationRoot, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    EnsureDirectoryExists(destination, createdDirectories);
                    continue;
                }
                if (entry.Length > MaximumEpubEntryBytes
                    || entry.Length > MaximumEpubExpandedBytes - expandedBytes)
                    throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

                EnsureDirectoryExists(Path.GetDirectoryName(destination)!, createdDirectories);

                var extension = Path.GetExtension(destination);
                var isCss = extension.Equals(".css", StringComparison.OrdinalIgnoreCase);
                var isHtmlOrXml = !isCss && (
                    extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase));

                if ((isCss || isHtmlOrXml) && entry.Length is >= 0 and <= InlineSanitizeThresholdBytes)
                {
                    using var source = entry.Open();
                    var (buffer, length, exceededInline) = ReadEntryForInlineSanitize(
                        source,
                        destination,
                        entry.Length,
                        expandedBytes,
                        copyBuffer,
                        cancellationToken);
                    if (exceededInline)
                    {
                        expandedBytes += length;
                        if (isCss) oversizedCssFiles.Add(destination);
                        else oversizedHtmlFiles.Add(destination);
                        continue;
                    }

                    expandedBytes += length;
                    if (!batchDestinations.Add(destination)
                        || batch.Count >= 64
                        || batchBytes >= 4 * 1024 * 1024)
                    {
                        try
                        {
                            await FlushSanitizeBatchAsync(batch, destinationRoot, cancellationToken);
                            batchDestinations.Clear();
                            batchBytes = 0;
                            batchDestinations.Add(destination);
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            throw;
                        }
                    }

                    batch.Add((destination, buffer, length, isCss));
                    batchBytes += length;
                    continue;
                }

                using (var source = entry.Open())
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: false))
                {
                    expandedBytes += CopyEpubEntrySync(source, output, entry.Length, expandedBytes, copyBuffer, cancellationToken);
                }

                if (isCss) oversizedCssFiles.Add(destination);
                else if (isHtmlOrXml) oversizedHtmlFiles.Add(destination);
            }

            if (batch.Count > 0)
            {
                await FlushSanitizeBatchAsync(batch, destinationRoot, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
            foreach (var pending in batch)
                ArrayPool<byte>.Shared.Return(pending.Buffer);
            batch.Clear();
        }

        if (oversizedHtmlFiles.Count > 0)
        {
            await Parallel.ForEachAsync(
                oversizedHtmlFiles,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = GetSanitizeConcurrency()
                },
                async (path, token) => await SanitizeHtmlFileSafelyAsync(path, destinationRoot, token));
        }

        if (oversizedCssFiles.Count > 0)
        {
            await Parallel.ForEachAsync(
                oversizedCssFiles,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = GetSanitizeConcurrency()
                },
                async (path, token) => await SanitizeCssFileAsync(path, destinationRoot, token));
        }
    }

    private static int GetSanitizeConcurrency() => Math.Clamp(Environment.ProcessorCount, 2, 8);

    private static void EnsureDirectoryExists(string directory, HashSet<string> createdDirectories)
    {
        if (createdDirectories.Add(directory))
            Directory.CreateDirectory(directory);
    }

    private static (byte[] Buffer, int Length, bool ExceededInline) ReadEntryForInlineSanitize(
        Stream source,
        string destination,
        long declaredLength,
        long expandedBeforeEntry,
        byte[] copyBuffer,
        CancellationToken cancellationToken)
    {
        var maximumEntryRemaining = MaximumEpubEntryBytes;
        var maximumPackageRemaining = MaximumEpubExpandedBytes - expandedBeforeEntry;
        if (declaredLength > maximumEntryRemaining || declaredLength > maximumPackageRemaining)
            throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

        var initialCapacity = declaredLength > 0
            ? (int)Math.Min(InlineSanitizeThresholdBytes, declaredLength + 64)
            : 4096;
        var buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        var totalRead = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var available = buffer.Length - totalRead;
                if (available == 0)
                {
                    if (buffer.Length >= InlineSanitizeThresholdBytes)
                    {
                        var remaining = source.Read(copyBuffer, 0, copyBuffer.Length);
                        if (remaining == 0) break;

                        long copied = totalRead + remaining;
                        if (copied > maximumEntryRemaining || copied > maximumPackageRemaining)
                            throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

                        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: false);
                        output.Write(buffer, 0, totalRead);
                        output.Write(copyBuffer, 0, remaining);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = [];
                        copied += CopyEpubEntrySync(source, output, -1, expandedBeforeEntry + copied, copyBuffer, cancellationToken);
                        return ([], (int)Math.Min(int.MaxValue, copied), true);
                    }

                    var nextSize = Math.Min(InlineSanitizeThresholdBytes, buffer.Length * 2);
                    var grown = ArrayPool<byte>.Shared.Rent(nextSize);
                    Buffer.BlockCopy(buffer, 0, grown, 0, totalRead);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                    available = buffer.Length - totalRead;
                }

                var read = source.Read(buffer, totalRead, available);
                if (read == 0) break;
                totalRead += read;
                if (totalRead > maximumEntryRemaining || totalRead > maximumPackageRemaining)
                    throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");
            }

            return (buffer, totalRead, false);
        }
        catch
        {
            if (buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static async Task FlushSanitizeBatchAsync(
        List<(string Destination, byte[] Buffer, int Length, bool IsCss)> batch,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        var items = batch.ToArray();
        batch.Clear();
        try
        {
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = GetSanitizeConcurrency()
                },
                (item, token) =>
                {
                    if (item.IsCss)
                        SanitizeCssBytesToFile(item.Buffer, item.Length, item.Destination, cacheRoot);
                    else
                        SanitizeHtmlBytesToFile(item.Buffer, item.Length, item.Destination, cacheRoot, token);
                    return ValueTask.CompletedTask;
                });
        }
        finally
        {
            foreach (var item in items)
                ArrayPool<byte>.Shared.Return(item.Buffer);
        }
    }

    private static long CopyEpubEntrySync(
        Stream source,
        Stream destination,
        long declaredLength,
        long expandedBeforeEntry,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var maximumEntryRemaining = MaximumEpubEntryBytes;
        var maximumPackageRemaining = MaximumEpubExpandedBytes - expandedBeforeEntry;
        if (declaredLength > maximumEntryRemaining || declaredLength > maximumPackageRemaining)
            throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");

        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            copied += read;
            if (copied > maximumEntryRemaining || copied > maximumPackageRemaining)
                throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");
            destination.Write(buffer, 0, read);
        }
        return copied;
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

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class EpubExtractionLimitException(string message) : IOException(message);

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
