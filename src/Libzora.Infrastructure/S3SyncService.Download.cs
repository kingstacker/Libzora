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
/// Remote snapshot download, compression and decoding.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private static bool ContainsEntityAtOrBefore(
        S3SyncSnapshot snapshot,
        string versionKey,
        DateTimeOffset deletedAt)
    {
        var separator = versionKey.IndexOf(':');
        if (separator <= 0) return false;
        var type = versionKey[..separator];
        var rawKey = versionKey[(separator + 1)..];
        if (type == "book" && Guid.TryParse(rawKey, out var bookId))
        {
            return snapshot.Books.Any(row => row.Id == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.Files.Any(row => row.BookId == bookId && row.ModifiedAt <= deletedAt)
                || snapshot.Annotations.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.BookReflections.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.Progress.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.Bookmarks.Any(row => row.BookId == bookId && row.CreatedAt <= deletedAt)
                || snapshot.Layouts.Any(row => row.BookId == bookId && LayoutSyncVersion(row) <= deletedAt)
                || snapshot.ReadingStats.Any(row => row.BookId == bookId && row.UpdatedAt <= deletedAt)
                || snapshot.CollectionItems.Any(row => row.BookId == bookId && row.AddedAt <= deletedAt);
        }
        if (type == "file" && Guid.TryParse(rawKey, out var fileId))
        {
            return snapshot.Files.Any(row => row.Id == fileId && row.ModifiedAt <= deletedAt)
                || snapshot.Annotations.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt)
                || snapshot.Progress.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt)
                || snapshot.Bookmarks.Any(row => row.BookFileId == fileId && row.CreatedAt <= deletedAt)
                || snapshot.Layouts.Any(row => row.BookFileId == fileId && LayoutSyncVersion(row) <= deletedAt)
                || snapshot.ReadingStats.Any(row => row.BookFileId == fileId && row.UpdatedAt <= deletedAt);
        }

        return GetSnapshotEntityVersions(snapshot).TryGetValue(versionKey, out var liveVersion)
            && liveVersion <= deletedAt;
    }

    private async Task<List<S3SyncSnapshot>> DownloadRemoteSnapshotsAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        IReadOnlyList<SyncObjectMetadata> snapshotObjects,
        string storageIdentity,
        string localDeviceId,
        bool skipOwnSnapshot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var keysToRead = snapshotObjects
            .Where(item => !skipOwnSnapshot || !IsSnapshotKeyForDevice(item.Key, settings, localDeviceId))
            .ToArray();
        var cachePaths = keysToRead.Select(item => SnapshotCachePath(settings, storageIdentity, item)).ToArray();
        var results = new S3SyncSnapshot?[keysToRead.Length];
        var processed = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, keysToRead.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = EffectiveConcurrency(settings),
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var cachePath = cachePaths[index];
                byte[]? payload = null;
                if (cachePath is not null && File.Exists(cachePath))
                {
                    try { payload = await File.ReadAllBytesAsync(cachePath, token); }
                    catch (IOException) { payload = null; }
                }
                if (payload is null)
                {
                    payload = await DownloadObjectBytesAsync(client, keysToRead[index].Key, token);
                    if (cachePath is not null)
                    {
                        Directory.CreateDirectory(SnapshotCacheDirectory);
                        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllBytesAsync(temporary, payload, token);
                        File.Move(temporary, cachePath, overwrite: true);
                    }
                }
                var snapshot = await DecodeSnapshotAsync(payload, settings.EncryptionKey, token);
                ValidateSnapshotCompatibility(snapshot);
                ReaderReadingDataReset.Validate(snapshot.ReadingDataReset);
                if (!Guid.TryParse(snapshot.DeviceId, out var parsedDeviceId))
                    throw new InvalidDataException("同步快照缺少有效的设备 ID。");
                snapshot.DeviceId = parsedDeviceId.ToString("N");
                snapshot.Tombstones ??= [];
                results[index] = snapshot;
                progress?.Report(UiText.Get("已读取 {0}/{1} 台设备的快照…",
                    Interlocked.Increment(ref processed), keysToRead.Length));
            });
        PruneSnapshotCache(storageIdentity, cachePaths.Where(path => path is not null)!, keysToRead.Select(item => item.Key));
        return results.Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToList();
    }

    private static bool IsSnapshotKeyForDevice(
        string key,
        S3SyncSettings settings,
        string deviceId)
    {
        var suffix = "/snapshot.bin";
        if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var marker = $"{settings.Prefix}/devices/";
        if (!key.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;
        var keyDeviceId = key[marker.Length..^suffix.Length];
        return string.Equals(
            NormalizeKnownDeviceId(keyDeviceId),
            NormalizeDeviceId(deviceId),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> DownloadObjectBytesAsync(
        ISyncObjectStore client,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await client.OpenReadAsync(key, cancellationToken);
        if (response.ContentLength > MaxSnapshotBytes)
            throw new InvalidDataException("云端同步快照过大，已拒绝读取。");

        using var memory = new MemoryStream(
            response.ContentLength is > 0 and <= int.MaxValue
                ? (int)response.ContentLength
                : 64 * 1024);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await response.ResponseStream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (memory.Length > MaxSnapshotBytes - read)
                throw new InvalidDataException("云端同步快照过大，已拒绝读取。");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return memory.ToArray();
    }

    private static async Task<byte[]> CompressSnapshotAsync(
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        EnsureSnapshotSize(json.Length);
        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            await gzip.WriteAsync(json, cancellationToken);
        var compressed = output.ToArray();
        EnsureSnapshotSize(compressed.Length);
        return compressed;
    }

    internal static void EnsureSnapshotSize(long length, long limit = MaxSnapshotBytes)
    {
        if (length > limit)
            throw new InvalidDataException(UiText.Get(
                "同步快照超过大小限制（{0} MB）。请减少书籍数量后重试。",
                Math.Max(1, limit / (1024 * 1024))));
    }

    private async Task<S3SyncSnapshot> DecodeSnapshotAsync(
        byte[] payload,
        string encryptionKey,
        CancellationToken cancellationToken)
    {
        if (HasMagic(payload) && encryptionKey.Length == 0)
            throw new InvalidDataException("云端同步对象已加密，请配置相同的加密密钥。");
        if (!HasMagic(payload) && encryptionKey.Length > 0)
            throw new InvalidDataException(UiText.Get("此同步目录尚未加密。请保持原加密配置，或改用新的同步子目录创建加密同步目录。"));

        var compressed = HasMagic(payload)
            ? UnprotectPayloadWithCache(payload, encryptionKey)
            : payload;
        await using var input = new MemoryStream(compressed, writable: false);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var limited = new SnapshotReadLimitStream(gzip, MaxSnapshotBytes);
        var snapshot = await JsonSerializer.DeserializeAsync<S3SyncSnapshot>(limited, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("云端同步快照为空。");
        AdoptLegacyRatingFields(snapshot);
        return snapshot;
    }

    private sealed class SnapshotReadLimitStream(Stream inner, long maximumBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytesRead;
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Track(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer)
            => Track(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => Track(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Track(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value < 0) return -1;
            Track(1);
            return value;
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Track(int read)
        {
            if (read <= 0) return read;
            if (_bytesRead > maximumBytes - read)
                throw new InvalidDataException("云端同步快照过大，已拒绝读取。");
            _bytesRead += read;
            return read;
        }
    }
}
