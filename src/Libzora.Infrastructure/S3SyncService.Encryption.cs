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
/// Payload and stream encryption/decryption.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private byte[] ProtectPayloadForSync(byte[] value, string passphrase)
    {
        if (_encryptionSession is { } session
            && string.Equals(session.Passphrase, passphrase, StringComparison.Ordinal))
            return ProtectPayload(value, passphrase, session.Salt, session.Key);
        return ProtectPayload(value, passphrase);
    }

    private static byte[] ProtectPayload(byte[] value, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        var key = DeriveEncryptionKey(passphrase, salt);
        return ProtectPayload(value, passphrase, salt, key);
    }

    private static byte[] ProtectPayload(
        byte[] value,
        string _,
        byte[] salt,
        byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceBytes);
        var ciphertext = new byte[value.Length];
        var tag = new byte[EncryptionTagBytes];
        using var aes = new AesGcm(key, EncryptionTagBytes);
        aes.Encrypt(nonce, value, ciphertext, tag, EncryptionMagic);

        var result = new byte[
            EncryptionMagic.Length
            + EncryptionSaltBytes
            + EncryptionNonceBytes
            + EncryptionTagBytes
            + ciphertext.Length];
        var offset = 0;
        EncryptionMagic.CopyTo(result, offset);
        offset += EncryptionMagic.Length;
        salt.CopyTo(result, offset);
        offset += EncryptionSaltBytes;
        nonce.CopyTo(result, offset);
        offset += EncryptionNonceBytes;
        tag.CopyTo(result, offset);
        offset += EncryptionTagBytes;
        ciphertext.CopyTo(result, offset);
        return result;
    }

    private static byte[] UnprotectPayload(byte[] value, string passphrase) =>
        UnprotectPayload(value, salt => DeriveEncryptionKey(passphrase, salt));

    private byte[] UnprotectPayloadWithCache(byte[] value, string passphrase) =>
        UnprotectPayload(value, salt => GetOrDeriveEncryptionKey(passphrase, salt));

    private static byte[] UnprotectPayload(
        byte[] value,
        Func<byte[], byte[]> keyFactory)
    {
        var minimum = EncryptionMagic.Length
            + EncryptionSaltBytes
            + EncryptionNonceBytes
            + EncryptionTagBytes;
        if (value.Length < minimum || !HasMagic(value))
            throw new InvalidDataException("云端同步对象的加密头无效。");

        var offset = EncryptionMagic.Length;
        var salt = value.AsSpan(offset, EncryptionSaltBytes).ToArray();
        offset += EncryptionSaltBytes;
        var nonce = value.AsSpan(offset, EncryptionNonceBytes).ToArray();
        offset += EncryptionNonceBytes;
        var tag = value.AsSpan(offset, EncryptionTagBytes).ToArray();
        offset += EncryptionTagBytes;
        var ciphertext = value.AsSpan(offset).ToArray();
        var key = keyFactory(salt);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, EncryptionTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, EncryptionMagic);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("云端同步加密密钥不匹配，或同步对象已损坏。", exception);
        }
    }

    private async Task EncryptFileToPathAsync(
        string sourcePath,
        string targetPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BlobEncryptionChunkBytes,
            useAsync: true);
        await EncryptStreamToPathAsync(input, targetPath, passphrase, cancellationToken);
    }

    private Task EncryptStreamToPathAsync(
        Stream input, string targetPath, string passphrase, CancellationToken cancellationToken) =>
        EncryptPaddedBlobToPathAsync(input, targetPath, passphrase, cancellationToken);

    private async Task DecryptBlobToPathAsync(
        Stream encryptedStream,
        string targetPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[StreamingEncryptionMagic.Length];
        await ReadExactlyAsync(encryptedStream, prefix, cancellationToken);
        if (prefix.AsSpan().SequenceEqual(PaddedEncryptionMagic))
        {
            await DecryptPaddedBlobToPathAsync(encryptedStream, targetPath, passphrase, cancellationToken);
            return;
        }
        if (!prefix.AsSpan().SequenceEqual(StreamingEncryptionMagic))
        {
            // Objects written by older versions use the one-shot format. Keep
            // them readable while using the streaming format for new uploads.
            await using var legacyPayload = new MemoryStream();
            await legacyPayload.WriteAsync(prefix, cancellationToken);
            await encryptedStream.CopyToAsync(legacyPayload, cancellationToken);
            var plaintext = UnprotectPayloadWithCache(legacyPayload.ToArray(), passphrase);
            await using var legacyOutput = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                useAsync: true);
            await legacyOutput.WriteAsync(plaintext, cancellationToken);
            return;
        }

        var salt = new byte[EncryptionSaltBytes];
        await ReadExactlyAsync(encryptedStream, salt, cancellationToken);
        var chunkSize = await ReadInt32Async(encryptedStream, cancellationToken);
        if (chunkSize is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("云端同步对象的分块大小无效。");

        var key = GetOrDeriveEncryptionKey(passphrase, salt);
        using var aes = new AesGcm(key, EncryptionTagBytes);
        await using var output = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            Math.Min(chunkSize, 1024 * 128),
            useAsync: true);
        var chunkIndex = 0;
        while (true)
        {
            var length = await ReadInt32Async(encryptedStream, cancellationToken);
            if (length == 0) break;
            if (length < 0 || length > chunkSize)
                throw new InvalidDataException("云端同步对象的分块长度无效。");

            var nonce = new byte[EncryptionNonceBytes];
            var tag = new byte[EncryptionTagBytes];
            var ciphertext = new byte[length];
            var plaintext = new byte[length];
            await ReadExactlyAsync(encryptedStream, nonce, cancellationToken);
            await ReadExactlyAsync(encryptedStream, tag, cancellationToken);
            await ReadExactlyAsync(encryptedStream, ciphertext, cancellationToken);
            try
            {
                aes.Decrypt(
                    nonce,
                    ciphertext,
                    tag,
                    plaintext,
                    BuildChunkAssociatedData(chunkIndex++));
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("云端同步加密密钥不匹配，或同步对象已损坏。", exception);
            }
            await output.WriteAsync(plaintext, cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
    }

    private (byte[] Salt, byte[] Key) GetEncryptionMaterial(string passphrase)
    {
        if (_encryptionSession is { } session
            && string.Equals(session.Passphrase, passphrase, StringComparison.Ordinal))
            return (session.Salt, session.Key);
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        return (salt, GetOrDeriveEncryptionKey(passphrase, salt));
    }

    private EncryptionSession CreateEncryptionSession(string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(EncryptionSaltBytes);
        return new EncryptionSession(
            passphrase,
            salt,
            GetOrDeriveEncryptionKey(passphrase, salt));
    }

    private static byte[] BuildChunkAssociatedData(int chunkIndex)
    {
        var result = new byte[StreamingEncryptionMagic.Length + sizeof(int)];
        StreamingEncryptionMagic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(StreamingEncryptionMagic.Length),
            chunkIndex);
        return result;
    }

    private static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, bytes, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new InvalidDataException("云端同步对象提前结束。");
            offset += read;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private byte[] GetOrDeriveEncryptionKey(string passphrase, byte[] salt)
    {
        var passphraseFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)));
        var cacheKey = Convert.ToBase64String(salt) + "\u001f" + passphraseFingerprint;
        if (_derivedEncryptionKeys.TryGetValue(cacheKey, out var cached))
            return cached;
        var derived = DeriveEncryptionKey(passphrase, salt);
        _derivedEncryptionKeys[cacheKey] = derived;
        if (_derivedEncryptionKeys.Count > 128)
            _derivedEncryptionKeys.Clear();
        return derived;
    }

    private static byte[] DeriveEncryptionKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            EncryptionIterations,
            HashAlgorithmName.SHA256,
            EncryptionKeyBytes);

    private static bool HasMagic(byte[] value) =>
        value.Length >= EncryptionMagic.Length
        && value.AsSpan(0, EncryptionMagic.Length).SequenceEqual(EncryptionMagic);

    private async Task<string> GetCachedFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("同步文件不存在。", fullPath);

        if (_fileHashCache.TryGetValue(fullPath, out var cached)
            && cached.Length == info.Length
            && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            return cached.Sha256;

        var hash = await Hashing.Sha256Async(fullPath, cancellationToken);
        _fileHashCache[fullPath] = new CachedFileHash(
            info.Length,
            info.LastWriteTimeUtc,
            hash);
        // Do not allow a long-running process to retain an unbounded path
        // cache. A fresh capture will simply repopulate evicted entries.
        if (_fileHashCache.Count > 4096)
            _fileHashCache.Clear();
        return hash;
    }
}
