using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS.Mtp;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Detection and book scanning internals.
/// </summary>
public sealed partial class MtpKindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    private sealed record LocalAnnotationDatabase(
        string DatabaseRelativePath,
        string LocalPath,
        IReadOnlyList<string> SdrFolders);

    public async Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        // MTP sessions are opened per operation and released immediately, so
        // there is no mounted volume to unmount. Wait for any in-flight
        // transfer to finish and invalidate the detection cache so the sidebar
        // reflects the released PTP session instead of a stale device entry.
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InvalidateDetectionCache();
        }
        finally
        {
            Gate.Release();
        }
    }

    private IReadOnlyList<KindleDevice> Detect(CancellationToken cancellationToken)
    {
        IReadOnlyList<MtpRawDevice> rawDevices;
        try
        {
            rawDevices = MtpDeviceSession.Detect();
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or BadImageFormatException
                                              or TypeInitializationException)
        {
            // Debug builds and source checkouts do not bundle libmtp. MTP is an
            // optional transport on macOS, so treat a missing native library as
            // "no MTP devices" instead of turning device detection into an error
            // in the sidebar. Packaged builds ship the dylib next to the app.
            Debug.WriteLine($"MTP unavailable: {exception.Message}");
            return [];
        }

        var devices = new List<KindleDevice>();
        var observed = new List<(string Identity, string RootPath)>();
        foreach (var raw in rawDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (raw.VendorId != AmazonVendorId) continue;
            try
            {
                using var session = MtpDeviceSession.Open(raw);
                var identity = MtpDeviceIdentity.Describe(session.SerialNumber, session.Model);
                var rootPath = MtpDeviceIdentity.RootPath(raw);
                observed.Add((identity, rootPath));
                devices.Add(new KindleDevice
                {
                    Profile = ReaderDeviceProfiles.Kindle,
                    RootPath = rootPath,
                    VolumeSerial = identity,
                    Name = session.Model,
                    TotalBytes = session.PrimaryStorage.TotalBytes,
                    FreeBytes = session.PrimaryStorage.FreeBytes,
                    IsReady = true,
                    Transport = KindleTransport.Mtp
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
#if DEBUG
                Console.Error.WriteLine(
                    $"MTP device {raw.VendorId:X4}:{raw.ProductId:X4} unavailable: {exception.Message}");
#endif
                Debug.WriteLine($"MTP device {raw.VendorId:X4}:{raw.ProductId:X4} unavailable: {exception.Message}");
            }
        }
        _attachmentTracker.Observe(observed);
        return devices;
    }

    private IReadOnlyList<KindleBook> Scan(
        KindleDevice device,
        IReadOnlyDictionary<string, KindleScanCacheEntry> cached,
        IProgress<KindleScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var session = OpenSession(device);
        var documents = device.Profile.BooksDirectory;
        var books = new List<KindleBook>();
        foreach (var entry in session.Enumerate(documents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsFolder || !device.Profile.IsBookPath(entry.RelativePath)) continue;
            var book = new KindleBook
            {
                RelativePath = entry.RelativePath,
                Format = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant(),
                Size = entry.Size,
                ModifiedAt = entry.ModifiedAt,
                IsManagedByLibzora = false
            };
            SetFallbackMetadata(book);
            books.Add(book);
        }

        progress?.Report(new KindleScanProgress(
            KindleScanStage.Enumerated,
            books.Select(CloneBook).ToArray(),
            [],
            0,
            books.Count));

        var entries = new List<KindleScanCacheEntry>(books.Count);
        var visible = new List<KindleBook>(books.Count);
        var changed = new List<KindleBook>(8);
        var removed = new List<string>(8);
        var processed = 0;
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "mtp-scan");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var book in books)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                        var remote = session.Find(book.RelativePath);
                        if (remote is not null && book.Size <= MaximumMetadataFileSize)
                        {
                            var staged = Path.Combine(
                                stagingDirectory,
                                $"{Guid.NewGuid():N}{Path.GetExtension(book.FileName)}");
                            try
                            {
                                session.Download(remote, staged, cancellationToken: cancellationToken);
                                isDictionary = KindleBookClassifier.IsDictionaryAsync(staged, cancellationToken)
                                    .GetAwaiter().GetResult();
                                if (!isDictionary)
                                {
                                    book.Sha256 = Hashing.Sha256Async(staged, cancellationToken)
                                        .GetAwaiter().GetResult();
                                    var metadata = _metadata.ReadMetadataAsync(staged, cancellationToken)
                                        .GetAwaiter().GetResult();
                                    if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
                                    if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
                                    book.CoverPath = WriteCover(book.Sha256, metadata);
                                    // Sideloaded books often lack an embedded
                                    // cover, but the device shelf thumbnail is
                                    // still available next to the book.
                                    if (string.IsNullOrWhiteSpace(book.CoverPath))
                                        book.CoverPath = TryReadDeviceThumbnail(
                                            session, book, staged, stagingDirectory, cancellationToken);
                                }
                            }
                            finally
                            {
                                TryDelete(staged);
                            }
                        }
                    }

                    entries.Add(new KindleScanCacheEntry
                    {
                        DeviceIdentity = device.Identity,
                        RelativePath = book.RelativePath,
                        Size = book.Size,
                        ModifiedAt = book.ModifiedAt,
                        Format = book.Format,
                        Title = book.Title,
                        Authors = book.Authors,
                        Sha256 = book.Sha256,
                        CoverPath = book.CoverPath,
                        IsDictionary = isDictionary
                    });
                    if (!isDictionary) visible.Add(book);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    visible.Add(book);
                }

                processed++;
                if (isDictionary) removed.Add(book.RelativePath);
                else changed.Add(CloneBook(book));
                if (changed.Count + removed.Count >= 8 || processed == books.Count)
                {
                    progress?.Report(new KindleScanProgress(
                        KindleScanStage.Enriched,
                        changed.ToArray(),
                        removed.ToArray(),
                        processed,
                        books.Count));
                    changed.Clear();
                    removed.Clear();
                }
            }

            try
            {
                _scanCache.ReplaceDeviceEntriesAsync(device.Identity, entries, cancellationToken)
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

            return visible;
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
