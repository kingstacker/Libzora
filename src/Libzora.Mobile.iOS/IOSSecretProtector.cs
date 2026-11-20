using System.Security.Cryptography;
using Foundation;
using Security;
using Libzora.Core;

namespace Libzora.iOSHead;

/// <summary>
/// AES-GCM encryption backed by a non-synchronizing, device-only Keychain key.
/// Existing sandbox keys are migrated without changing protected secret bytes.
/// </summary>
/// <remarks>
/// Deliberately standalone rather than deriving from <c>AesGcmSecretProtector</c>:
/// that base uses a named <see cref="Mutex"/>, which iOS does not guarantee, and
/// the protected format never travels between devices anyway.
/// </remarks>
public sealed class IOSSecretProtector : ISecretProtector
{
    private static readonly object Gate = new();
    private static readonly byte[] Header = [0x4C, 0x5A, 0x53, 0x01];
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyDirectory;
    private readonly string _keyPath;

    public IOSSecretProtector(string keyDirectory)
    {
        _keyDirectory = keyDirectory;
        _keyPath = Path.Combine(keyDirectory, "secret.key");
    }

    public byte[] Protect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return [];

        var key = GetOrCreateKey();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var output = new byte[Header.Length + NonceSize + TagSize + value.Length];
            Header.CopyTo(output, 0);
            nonce.CopyTo(output, Header.Length);
            var tag = output.AsSpan(Header.Length + NonceSize, TagSize);
            var ciphertext = output.AsSpan(Header.Length + NonceSize + TagSize);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, value, ciphertext, tag, Header);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Unprotect(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return [];
        if (value.Length < Header.Length + NonceSize + TagSize
            || !value.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            throw new CryptographicException("The protected secret has an unsupported format.");
        }

        var key = GetOrCreateKey(createIfMissing: false);
        try
        {
            var nonce = value.AsSpan(Header.Length, NonceSize);
            var tag = value.AsSpan(Header.Length + NonceSize, TagSize);
            var ciphertext = value.AsSpan(Header.Length + NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Header);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] GetOrCreateKey(bool createIfMissing = true)
    {
        lock (Gate)
        {
            // Exclude the old directory even when migration cannot finish, e.g.
            // while Keychain access is temporarily unavailable.
            if (Directory.Exists(_keyDirectory))
                NSFileManager.SetSkipBackupAttribute(_keyDirectory, true);
            return IOSSecretKeyMigration.GetOrCreate(ReadDeviceKey, SaveDeviceKey,
                ReadLegacyKey, () => File.Delete(_keyPath), createIfMissing);
        }
    }

    private byte[]? ReadLegacyKey()
    {
        if (!File.Exists(_keyPath)) return null;
        try { return Convert.FromBase64String(File.ReadAllText(_keyPath).Trim()); }
        catch (FormatException)
        {
            // A corrupt legacy key only invalidates secrets it protected.
            return null;
        }
    }

    private static SecRecord KeyRecord() => new(SecKind.GenericPassword)
    {
        Service = NSBundle.MainBundle.BundleIdentifier ?? "com.kingstacker.libzora",
        Account = "sandbox-master-key-v1",
        Synchronizable = false
    };

    private static byte[]? ReadDeviceKey()
    {
        using var query = KeyRecord();
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.ItemNotFound) return null;
        if (status != SecStatusCode.Success || data is null)
            throw new SecretStoreUnavailableException($"Unable to access the device Keychain: {status}.");
        return data.ToArray();
    }

    private static void SaveDeviceKey(byte[] key)
    {
        using var record = KeyRecord();
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        using var data = NSData.FromArray(key);
        record.ValueData = data;
        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
            throw new SecretStoreUnavailableException($"Unable to store the device Keychain key: {status}.");
    }
}
