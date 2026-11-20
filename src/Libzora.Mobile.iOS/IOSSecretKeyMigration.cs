using System.Security.Cryptography;

namespace Libzora.iOSHead;

/// <summary>Moves a legacy key only after the device store has retained the exact bytes.</summary>
internal static class IOSSecretKeyMigration
{
    internal const int KeySize = 32;

    internal static byte[] GetOrCreate(Func<byte[]?> readDeviceKey, Action<byte[]> saveDeviceKey,
        Func<byte[]?> readLegacyKey, Action removeLegacyKey, bool createIfMissing)
    {
        var existing = readDeviceKey();
        if (existing is not null)
        {
            Validate(existing);
            try
            {
                var legacyCopy = readLegacyKey();
                try
                {
                    if (legacyCopy is not null && !CryptographicOperations.FixedTimeEquals(existing, legacyCopy))
                        throw new CryptographicException("The legacy and device keys do not match; neither key was replaced.");
                }
                finally { if (legacyCopy is not null) CryptographicOperations.ZeroMemory(legacyCopy); }
                removeLegacyKey();
                return existing;
            }
            catch { CryptographicOperations.ZeroMemory(existing); throw; }
        }
        var legacy = readLegacyKey();
        if (legacy is null && !createIfMissing)
            throw new CryptographicException("The device key is no longer available.");
        var key = legacy ?? RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            Validate(key);
            saveDeviceKey(key);
            var retained = readDeviceKey();
            try
            {
                if (retained is null || !CryptographicOperations.FixedTimeEquals(key, retained))
                    throw new CryptographicException("The device store did not retain the key.");
            }
            finally { if (retained is not null) CryptographicOperations.ZeroMemory(retained); }
            removeLegacyKey();
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static void Validate(byte[] key)
    {
        if (key.Length == KeySize) return;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("The device key has an invalid length.");
    }
}
