using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>
/// Cross-platform reader implementation for devices exposed as a mounted
/// filesystem. Windows keeps its WPD implementation for MTP-only devices.
/// </summary>
public sealed partial class MassStorageKindleDeviceService : IKindleDeviceService
{
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private const int FileOperationRetryAttempts = 30;
    private const int FileOperationRetryDelayMilliseconds = 100;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".prc", ".kfx"
    };

    private readonly KindleScanCacheStore _scanCache;
    private readonly AppPaths _paths;
    private readonly IMetadataService _metadata;
    private readonly IBookFormatConverter _formatConverter;
    private readonly string _coverCacheDirectory;
    private readonly IReadOnlyList<string> _mountRoots;
    private readonly Func<KindleDevice, CancellationToken, Task> _eject;

    public MassStorageKindleDeviceService(
        AppPaths paths,
        IMetadataService metadata,
        IEnumerable<string>? mountRoots = null,
        Func<KindleDevice, CancellationToken, Task>? eject = null,
        IBookFormatConverter? formatConverter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _scanCache = new KindleScanCacheStore(paths);
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _coverCacheDirectory = Path.Combine(paths.Covers, "kindle");
        Directory.CreateDirectory(_coverCacheDirectory);
        _mountRoots = (mountRoots ?? DefaultMountRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        _eject = eject ?? ((_, _) => throw new PlatformNotSupportedException("This platform head has no safe-eject implementation."));
    }

    public Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<KindleDevice>>(() => DetectDevices(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        ScanBooksProgressivelyAsync(device, null, cancellationToken);

    public Task<IReadOnlyList<KindleBook>> GetCachedBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) =>
        _scanCache.GetCachedBooksAsync(device.Identity, cancellationToken);

    public async Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(
        KindleDevice device,
        IProgress<KindleScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!Directory.Exists(device.RootPath))
            throw new IOException("设备已断开，请重新连接并解锁设备后重试。");
        var documents = GetDocumentsRoot(device);
        if (!Directory.Exists(documents)) return [];

        var books = await Task.Run(() =>
        {
            var enumerated = new List<KindleBook>();
            foreach (var path in EnumerateFiles(documents))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var withinDocuments = Path.GetRelativePath(documents, path);
                if (!device.Profile.IsBookPath(withinDocuments)) continue;
                try
                {
                    var info = new FileInfo(path);
                    var book = new KindleBook
                    {
                        RelativePath = Path.GetRelativePath(device.RootPath, path),
                        Format = info.Extension.TrimStart('.').ToLowerInvariant(),
                        Size = info.Length,
                        ModifiedAt = info.LastWriteTimeUtc
                    };
                    SetFallbackMetadata(book);
                    enumerated.Add(book);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return enumerated;
        }, cancellationToken).ConfigureAwait(false);
        var cached = await _scanCache.GetDeviceEntriesAsync(device.Identity, cancellationToken).ConfigureAwait(false);
        var entries = new List<KindleScanCacheEntry>(books.Count);

        progress?.Report(new KindleScanProgress(
            KindleScanStage.Enumerated,
            books.Select(CloneBook).ToArray(),
            [],
            0,
            books.Count));

        var visible = new List<KindleBook>(books.Count);
        var processed = 0;
        var changed = new List<KindleBook>(8);
        var removed = new List<string>(8);
        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveWithinRoot(device.RootPath, book.RelativePath, documents);
            var isDictionary = false;
            try
            {
                if (cached.TryGetValue(book.RelativePath.Replace('/', '\\').TrimStart('\\'), out var entry)
                    && entry.Matches(book.Size, book.ModifiedAt)
                    && (entry.CoverPath is null || File.Exists(entry.CoverPath)))
                {
                    isDictionary = entry.IsDictionary;
                    book.Format = entry.Format;
                    book.Title = entry.Title;
                    book.Authors = entry.Authors;
                    book.Sha256 = entry.Sha256;
                    book.CoverPath = entry.CoverPath;
                }
                else
                {
                    isDictionary = await KindleBookClassifier.IsDictionaryAsync(source, cancellationToken).ConfigureAwait(false);
                    if (!isDictionary)
                    {
                        book.Sha256 = await Hashing.Sha256Async(source, cancellationToken).ConfigureAwait(false);
                        await EnrichBookAsync(device, book, source, cancellationToken).ConfigureAwait(false);
                    }
                }
                entries.Add(new KindleScanCacheEntry
                {
                    DeviceIdentity = device.Identity, RelativePath = book.RelativePath,
                    Size = book.Size, ModifiedAt = book.ModifiedAt, Format = book.Format,
                    Title = book.Title, Authors = book.Authors, Sha256 = book.Sha256,
                    CoverPath = book.CoverPath, IsDictionary = isDictionary
                });
                if (!isDictionary) visible.Add(book);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                visible.Add(book);
            }

            processed++;
            if (isDictionary) removed.Add(book.RelativePath);
            else changed.Add(CloneBook(book));
            if (changed.Count + removed.Count >= 8 || processed == books.Count)
            {
                progress?.Report(new KindleScanProgress(
                    KindleScanStage.Enriched, changed.ToArray(), removed.ToArray(), processed, books.Count));
                changed.Clear();
                removed.Clear();
            }
        }
        try { await _scanCache.ReplaceDeviceEntriesAsync(device.Identity, entries, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return visible;
    }
}
