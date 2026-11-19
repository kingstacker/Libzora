using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Book scanning, caches, covers and sidecars.
/// </summary>
public sealed partial class KindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{

    public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default) =>
        ScanBooksProgressivelyAsync(device, progress: null, cancellationToken);

    public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        _scanCache?.GetCachedBooksAsync(device.Identity, cancellationToken) ?? Task.FromResult<IReadOnlyList<KindleBook>>([]);

    public async Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var enumerated = device.Transport == KindleTransport.Wpd
            ? await Task.Run(
                () => WpdKindleAccess.ScanBooks(device,
                    device.Profile.BookFormats.Select(format => "." + format).ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken),
                cancellationToken).ConfigureAwait(false)
            : await Task.Run(() => EnumerateMassStorageBooks(device, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var book in enumerated) SetFallbackMetadata(book);

        var cachedEntries = _scanCache is null
            ? new Dictionary<string, KindleScanCacheEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, KindleScanCacheEntry>(
                await _scanCache.GetDeviceEntriesAsync(device.Identity, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        var currentEntries = new List<KindleScanCacheEntry>(enumerated.Count);
        var visibleBooks = new List<KindleBook>(enumerated.Count);
        var pending = new List<KindleBook>();

        foreach (var book in enumerated)
        {
            var key = NormalizeDevicePath(book.RelativePath);
            if (cachedEntries.TryGetValue(key, out var cached)
                && cached.Matches(book.Size, book.ModifiedAt)
                // A successful scan may legitimately find no cover. Cache that
                // outcome too, rather than copying the entire MTP book again.
                && (cached.CoverPath is null || File.Exists(cached.CoverPath)))
            {
                ApplyCachedBook(book, cached);
                currentEntries.Add(cached);
                if (cached.IsDictionary) continue;
                visibleBooks.Add(book);
                continue;
            }

            visibleBooks.Add(book);
            pending.Add(book);
        }

        progress?.Report(new KindleScanProgress(
            KindleScanStage.Enumerated,
            visibleBooks.Select(CloneBook).ToArray(),
            [],
            enumerated.Count - pending.Count,
            enumerated.Count));

        var changed = new List<KindleBook>();
        var removed = new List<string>();
        var processed = enumerated.Count - pending.Count;
        WpdKindleAccess.WpdSession? wpdSession = null;
        try
        {
        if (device.Transport == KindleTransport.Wpd && pending.Count > 0)
            wpdSession = new WpdKindleAccess.WpdSession(device);
        foreach (var book in pending)
        {
            var stopWpdEnrichment = false;
            cancellationToken.ThrowIfCancellationRequested();
            var isDictionary = false;
            var cacheable = true;
            using var enrichmentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            enrichmentTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var enrichment = Task.Run(async () =>
            {
                if (device.Transport == KindleTransport.Wpd)
                {
                    isDictionary = await EnrichWpdBookAsync(device, book, wpdSession, enrichmentTimeout.Token);
                }
                else
                {
                    var path = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
                    isDictionary = await KindleBookClassifier.IsDictionaryAsync(path, enrichmentTimeout.Token);
                    if (!isDictionary)
                    {
                        book.Sha256 = await Hashing.Sha256Async(path, enrichmentTimeout.Token);
                        await EnrichBookAsync(device, book, path, enrichmentTimeout.Token);
                    }
                }
            }, cancellationToken);
            try
            {
                await enrichment;
            }
            catch (OperationCanceledException) when (
                enrichmentTimeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // A single book's enrichment timed out; keep the fallback card.
                cacheable = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
            {
                // Keep the quickly enumerated fallback card when enrichment fails.
                cacheable = false;
                // A Kindle-side disconnect first surfaces as a WPD I/O failure. Do
                // not continue reopening the device for every remaining book.
                stopWpdEnrichment = device.Transport == KindleTransport.Wpd;
            }

            processed++;
            if (cacheable) currentEntries.Add(CreateCacheEntry(device, book, isDictionary));
            if (isDictionary)
            {
                visibleBooks.RemoveAll(candidate => candidate.RelativePath.Equals(
                    book.RelativePath,
                    StringComparison.OrdinalIgnoreCase));
                removed.Add(book.RelativePath);
            }
            else
            {
                changed.Add(book);
            }

            if (changed.Count + removed.Count >= 8 || processed == enumerated.Count)
            {
                progress?.Report(new KindleScanProgress(
                    KindleScanStage.Enriched,
                    changed.ToArray(),
                    removed.ToArray(),
                    processed,
                    enumerated.Count));
                changed.Clear();
                removed.Clear();
            }
            if (stopWpdEnrichment) break;
        }
        }
        finally
        {
            wpdSession?.Dispose();
        }

        if (_scanCache is not null)
        {
            try
            {
                await _scanCache.ReplaceDeviceEntriesAsync(device.Identity, currentEntries, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (TimeoutException)
            {
                // The cache write must never block the scan result.
            }
        }
        return visibleBooks;
    }

    private static IReadOnlyList<KindleBook> EnumerateMassStorageBooks(
        KindleDevice device,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(device.RootPath))
            throw new IOException("设备已断开，请重新连接并解锁设备后重试。");
        var documents = GetDocumentsRoot(device);
        if (!Directory.Exists(documents)) return [];
        var books = new List<KindleBook>();
        foreach (var path in Directory.EnumerateFiles(documents, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentsRelativePath = Path.GetRelativePath(documents, path);
            if (!device.Profile.IsBookPath(documentsRelativePath)) continue;
            try
            {
                var info = new FileInfo(path);
                books.Add(new KindleBook
                {
                    RelativePath = Path.GetRelativePath(device.RootPath, path),
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc,
                    IsManagedByLibzora = false
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return books;
    }

    private static void ApplyCachedBook(KindleBook book, KindleScanCacheEntry cached)
    {
        book.Format = cached.Format;
        book.Sha256 = cached.Sha256;
        book.Title = cached.Title;
        book.Authors = cached.Authors;
        book.CoverPath = cached.CoverPath is not null && File.Exists(cached.CoverPath)
            ? cached.CoverPath
            : null;
    }

    private static KindleScanCacheEntry CreateCacheEntry(
        KindleDevice device,
        KindleBook book,
        bool isDictionary) => new()
    {
        DeviceIdentity = device.Identity,
        RelativePath = book.RelativePath,
        Size = book.Size,
        ModifiedAt = book.ModifiedAt,
        Format = book.Format,
        Sha256 = book.Sha256,
        Title = book.Title,
        Authors = book.Authors,
        CoverPath = book.CoverPath,
        IsDictionary = isDictionary
    };

    private static KindleBook CloneBook(KindleBook book) => new()
    {
        RelativePath = book.RelativePath,
        Title = book.Title,
        Authors = book.Authors,
        Format = book.Format,
        Size = book.Size,
        Sha256 = book.Sha256,
        CoverPath = book.CoverPath,
        ModifiedAt = book.ModifiedAt,
        IsManagedByLibzora = book.IsManagedByLibzora
    };

    private static string NormalizeDevicePath(string path) => path.Replace('/', '\\').TrimStart('\\');

    private static bool IsDictionaryPath(string relativePath)
    {
        var firstSeparator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var firstSegment = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
        return firstSegment.Equals("dictionaries", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<bool> EnrichWpdBookAsync(
        KindleDevice device,
        KindleBook book,
        WpdKindleAccess.WpdSession? session,
        CancellationToken cancellationToken)
    {
        SetFallbackMetadata(book);
        if (_coverCacheDirectory is null || book.Size <= 0 || book.Size > MaximumMetadataFileSize) return false;
        if (book.Format is not ("epub" or "pdf" or "mobi" or "azw" or "azw3" or "kfx")) return false;
        // A sidecar from a previous scan already owns the hash, title,
        // authors and cover for this exact size+mtime. Skip the MTP copy.
        if (TryApplyCachedWpdMetadata(device, book, out var cachedIsDictionary))
            return cachedIsDictionary;
        var cachedCover = FindCachedCover(device, book);

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "metadata", Guid.NewGuid().ToString("N"));
        try
        {
            var localPath = session is not null
                ? WpdKindleAccess.CopyBookToLocal(session, device, book, stagingDirectory, cancellationToken)
                : await Task.Run(
                    () => WpdKindleAccess.CopyBookToLocal(device, book, stagingDirectory, cancellationToken),
                    cancellationToken);
            book.Sha256 = await Hashing.Sha256Async(localPath, cancellationToken);
            var isDictionary = await KindleBookClassifier.IsDictionaryAsync(localPath, cancellationToken);
            if (isDictionary)
            {
                WriteWpdSidecar(device, book, isDictionary: true);
                return true;
            }
            if (cachedCover is not null)
            {
                book.CoverPath = cachedCover;
                WriteWpdSidecar(device, book, isDictionary: false);
                return false;
            }
            await EnrichBookAsync(device, book, localPath, cancellationToken);
            if (device.Profile.UsesKindleThumbnails && string.IsNullOrWhiteSpace(book.CoverPath))
            {
                var thumbnailName = await KindleThumbnailService.ReadThumbnailFileNameAsync(localPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(thumbnailName))
                {
                    var thumbnailPath = session is not null
                        ? WpdKindleAccess.CopyStorageFileToLocal(
                            session, device, $@"system\thumbnails\{thumbnailName}", stagingDirectory, cancellationToken)
                        : WpdKindleAccess.CopyStorageFileToLocal(
                            device, $@"system\thumbnails\{thumbnailName}", stagingDirectory, cancellationToken);
                    var coverPath = Path.Combine(_coverCacheDirectory, GetCoverCacheKey(device, book) + ".jpg");
                    File.Copy(thumbnailPath, coverPath, overwrite: true);
                    book.CoverPath = coverPath;
                }
            }
            WriteWpdSidecar(device, book, isDictionary: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
        {
            // A missing cover must not hide an otherwise readable device book.
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private async Task EnrichBookAsync(
        KindleDevice device,
        KindleBook book,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        SetFallbackMetadata(book);
        try
        {
            var metadata = await _metadata.ReadMetadataAsync(sourcePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
            if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
            if (metadata.CoverBytes is { Length: > 0 } && _coverCacheDirectory is not null)
            {
                var extension = metadata.CoverExtension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? ".png"
                    : ".jpg";
                var coverPath = Path.Combine(_coverCacheDirectory, GetCoverCacheKey(device, book) + extension);
                await File.WriteAllBytesAsync(coverPath, metadata.CoverBytes, cancellationToken);
                book.CoverPath = coverPath;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // File metadata is best effort; filename, format and size remain available.
        }
    }

    private string? FindCachedCover(KindleDevice device, KindleBook book)
    {
        if (_coverCacheDirectory is null) return null;
        var key = GetCoverCacheKey(device, book);
        foreach (var extension in new[] { ".jpg", ".png" })
        {
            var path = Path.Combine(_coverCacheDirectory, key + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string GetCoverCacheKey(KindleDevice device, KindleBook book)
    {
        var identity = $"{device.Identity}\n{book.RelativePath}\n{book.Size}\n{book.ModifiedAt?.UtcTicks ?? 0}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private sealed class WpdBookSidecar
    {
        public string? Sha256 { get; set; }
        public string? Title { get; set; }
        public string? Authors { get; set; }
        public bool IsDictionary { get; set; }
    }

    private string? GetWpdSidecarPath(KindleDevice device, KindleBook book) =>
        _coverCacheDirectory is null
            ? null
            : Path.Combine(_coverCacheDirectory, GetCoverCacheKey(device, book) + ".meta.json");

    private bool TryApplyCachedWpdMetadata(
        KindleDevice device,
        KindleBook book,
        out bool isDictionary)
    {
        isDictionary = false;
        var path = GetWpdSidecarPath(device, book);
        if (path is null || !File.Exists(path)) return false;
        try
        {
            var sidecar = JsonSerializer.Deserialize<WpdBookSidecar>(File.ReadAllText(path));
            if (sidecar is null) return false;
            book.Sha256 = sidecar.Sha256 ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(sidecar.Title)) book.Title = sidecar.Title.Trim();
            if (!string.IsNullOrWhiteSpace(sidecar.Authors)) book.Authors = sidecar.Authors.Trim();
            book.CoverPath = FindCachedCover(device, book);
            isDictionary = sidecar.IsDictionary;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void WriteWpdSidecar(KindleDevice device, KindleBook book, bool isDictionary)
    {
        var path = GetWpdSidecarPath(device, book);
        if (path is null) return;
        try
        {
            var sidecar = new WpdBookSidecar
            {
                Sha256 = book.Sha256,
                Title = book.Title,
                Authors = book.Authors,
                IsDictionary = isDictionary
            };
            File.WriteAllText(path, JsonSerializer.Serialize(sidecar));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The sidecar only accelerates the next scan.
        }
    }

    private static void SetFallbackMetadata(KindleBook book)
    {
        var fileName = Path.GetFileNameWithoutExtension(book.RelativePath);
        var identifierSeparator = fileName.LastIndexOf('_');
        if (identifierSeparator > 0)
        {
            var suffix = fileName[(identifierSeparator + 1)..];
            if (suffix.Length == 32 && suffix.All(Uri.IsHexDigit)) fileName = fileName[..identifierSeparator];
        }
        book.Title = fileName.Replace('_', ' ').Trim();
        book.Authors = "未知作者";
    }
}
