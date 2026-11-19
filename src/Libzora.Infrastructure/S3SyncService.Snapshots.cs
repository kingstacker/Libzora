using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Snapshot keys, cache and upload.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private static async Task MoveFileWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        // Antivirus/indexers can briefly hold files on Windows. A short retry
        // keeps a successful sync from failing on a transient sharing
        // violation.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < 3
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(25 * (attempt + 1), cancellationToken);
            }
        }
    }

    private static string SnapshotKey(S3SyncSettings settings, string deviceId) =>
        $"{settings.Prefix}/devices/{deviceId}/snapshot.bin";

    /// <summary>Snapshots older than this are republished even when unchanged, well before peers treat the device as retired.</summary>
    private static readonly TimeSpan SnapshotRepublishInterval = TimeSpan.FromDays(30);

    private static async Task<List<SyncObjectMetadata>> ListSnapshotObjectsAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var items = await client.ListObjectsAsync($"{settings.Prefix}/devices/", cancellationToken);
        return items.Where(item => item.Key.EndsWith("/snapshot.bin", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string SnapshotContentHash(S3SyncSnapshot snapshot)
    {
        var created = snapshot.CreatedAt;
        try
        {
            snapshot.CreatedAt = default;
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions)));
        }
        finally { snapshot.CreatedAt = created; }
    }

    private string SnapshotCacheDirectory => Path.Combine(_paths.Data, "s3-sync-snapshot-cache");

    /// <summary>
    /// Remote snapshots are cached as the encrypted bytes the store returned, keyed by object key and modification
    /// time. Providers without reliable modification metadata are never served from the cache.
    /// </summary>
    private string? SnapshotCachePath(S3SyncSettings settings, string storageIdentity, SyncObjectMetadata item) =>
        !ProviderCapabilities(settings).ReliableLastModified || item.LastModified is not { } modified ? null
            : Path.Combine(SnapshotCacheDirectory, SnapshotCacheStem(storageIdentity, item.Key) + "-" + modified.UtcTicks + ".bin");

    private static string SnapshotCacheStem(string storageIdentity, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storageIdentity + "\n" + key))).ToLowerInvariant();

    private void PruneSnapshotCache(string storageIdentity, IEnumerable<string> keepPaths, IEnumerable<string> listedKeys)
    {
        if (!Directory.Exists(SnapshotCacheDirectory)) return;
        var keep = keepPaths.ToHashSet(StringComparer.Ordinal);
        var stems = listedKeys.Select(key => SnapshotCacheStem(storageIdentity, key)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(SnapshotCacheDirectory, "*.bin"))
        {
            var name = Path.GetFileName(file);
            var stem = name.Split('-')[0];
            // Drop superseded versions of listed keys; entries of other storages expire after 30 days unused.
            if (keep.Contains(file)) continue;
            if (stems.Contains(stem) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<List<string>> ListSnapshotKeysAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var keys = await client.ListKeysAsync($"{settings.Prefix}/devices/", cancellationToken);
        return keys.Where(key => key.EndsWith("/snapshot.bin", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<string?> UploadLocalObjectsAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        S3SyncSnapshot snapshot,
        IReadOnlySet<string> remoteOnlyFileHashes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var objects = new List<LocalSyncObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new ConcurrentBag<string>();
        var invalid = new ConcurrentBag<string>();

        foreach (var file in snapshot.Files)
        {
            if (!IsSha256(file.Sha256))
            {
                invalid.Add(UiText.Get("本地文件“{0}”缺少有效的 SHA-256 校验值，已跳过上传。", file.FileName));
                continue;
            }
            var relativePath = snapshot.LocalFilePaths.GetValueOrDefault(file.Id)
                ?? Path.Combine("library", file.BookId.ToString("N"), file.FileName);
            var path = ResolveDataPath(relativePath);
            if (path is null || !File.Exists(path))
            {
                // A remote-only file is deliberately represented in the local
                // database before its bytes arrive. Do not turn that expected
                // absence into a failed upload warning.
                if (remoteOnlyFileHashes.Contains(file.Sha256)) continue;
                missing.Add(path ?? relativePath);
                continue;
            }
            if (seen.Add(file.Sha256))
                objects.Add(new LocalSyncObject(file.Sha256, path, "application/octet-stream"));
        }

        foreach (var book in snapshot.Books.Concat(snapshot.ReadingMaterialBooks))
        {
            if (!IsSha256(book.CoverHash) || string.IsNullOrWhiteSpace(book.CoverFileName)) continue;
            var relativePath = book.LocalCoverPath
                ?? snapshot.LocalCoverPaths.GetValueOrDefault(book.Id)
                ?? Path.Combine("covers", book.CoverFileName);
            var path = ResolveDataPath(relativePath);
            if (path is null || !File.Exists(path))
            {
                missing.Add(path ?? relativePath);
                continue;
            }
            if (seen.Add(book.CoverHash!))
                objects.Add(new LocalSyncObject(book.CoverHash!, path, "image/*"));
        }

        // Objects are content-addressed and immutable. Listing the object
        // prefix once avoids one HEAD round-trip for every file. If a backend
        // does not allow listing this prefix, fall back to the existing HEAD
        // check so synchronization remains compatible.
        var existingKeys = await TryListBlobKeysAsync(client, settings, cancellationToken);

        var uploaded = 0;
        await Parallel.ForEachAsync(
            objects,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = EffectiveConcurrency(settings),
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                try
                {
                    await UploadObjectIfMissingAsync(client, settings, item, existingKeys, token);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    missing.Add(item.Path);
                }
                catch (InvalidDataException exception)
                {
                    invalid.Add(UiText.Get("本地文件“{0}”校验失败：{1}", Path.GetFileName(item.Path), UiText.Localize(exception.Message)));
                }
                var count = Interlocked.Increment(ref uploaded);
                if (count == objects.Count || count % 10 == 0)
                    progress?.Report(UiText.Get("已处理 {0}/{1} 个同步对象…", count, objects.Count));
            });

        if (!missing.IsEmpty)
            invalid.Add(UiText.Get("有 {0} 个本地文件缺失，已跳过上传。", missing.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        return invalid.IsEmpty ? null : string.Join(" ", invalid.Distinct());
    }

    private async Task UploadObjectIfMissingAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        LocalSyncObject item,
        ConcurrentDictionary<string, byte>? existingKeys,
        CancellationToken cancellationToken)
    {
        var key = BlobKey(settings, item.Hash);
        // Concurrent first syncs may have created independent random naming salts.
        // Read/deduplicate every retained salt, then publish new objects under the
        // same canonical namespace on all devices. No salt can orphan an object.
        foreach (var candidate in BlobKeys(settings, item.Hash))
        {
            if (existingKeys is not null ? existingKeys.ContainsKey(candidate)
                : await client.ExistsAsync(candidate, cancellationToken)) return;
        }

        // Hold the same input stream through validation and upload. On Windows
        // the sharing mode also prevents a concurrent edit or removal.
        await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, useAsync: true);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!string.Equals(actualHash, item.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.Get("本地文件内容与 SHA-256 校验值不匹配，已跳过上传。"));
        input.Position = 0;

        string? temporaryEncryptedPath = null;
        try
        {
            if (settings.EncryptionKey.Length == 0)
            {
                await client.PutAsync(key, input, item.ContentType, cancellationToken, actualHash);
            }
            else
            {
                temporaryEncryptedPath = Path.Combine(
                    Path.GetTempPath(),
                    $"libzora-sync-{Guid.NewGuid():N}.bin");
                await EncryptStreamToPathAsync(
                    input,
                    temporaryEncryptedPath,
                    settings.EncryptionKey,
                    cancellationToken);
                await using var encrypted = new FileStream(temporaryEncryptedPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
                await client.PutAsync(key, encrypted, item.ContentType, cancellationToken);
            }
            existingKeys?.TryAdd(key, 0);
        }
        finally
        {
            if (temporaryEncryptedPath is not null)
                TryDeleteTemporaryFile(temporaryEncryptedPath);
        }
    }

    private async Task<ConcurrentDictionary<string, byte>?> TryListBlobKeysAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        CancellationToken cancellationToken)
    {
        // An eventual store may list a complete local file whose cloud upload
        // is still pending. ExistsAsync must confirm publication before a new
        // snapshot can reference it; a listing is not a safe deduplication gate.
        if (!ProviderCapabilities(settings).ReliableListing) return null;
        var keys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var prefix in BlobPrefixes(settings))
                foreach (var key in await client.ListKeysAsync(prefix, cancellationToken)) keys.TryAdd(key, 0);
            return keys;
        }
        catch (SyncListingUnavailableException)
        {
            return null;
        }
    }

    private async Task UploadSnapshotAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var compressed = await CompressSnapshotAsync(snapshot, cancellationToken);
        var payload = settings.EncryptionKey.Length == 0
            ? compressed
            : ProtectPayloadForSync(compressed, settings.EncryptionKey);
        EnsureSnapshotSize(payload.Length);
        using var stream = new MemoryStream(payload, writable: false);
        await client.PutAsync(SnapshotKey(settings, snapshot.DeviceId), stream,
            "application/octet-stream", cancellationToken);
    }
}
