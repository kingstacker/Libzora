namespace Libzora.Platform.MacOS.Mtp;

/// <summary>
/// Interprets the result of <c>LIBMTP_Get_Files_And_Folders</c>. libmtp
/// returns a null list both for an empty directory and for a native failure,
/// so the error stack decides which one this is. A failed listing must never
/// be cached as "this directory is empty".
/// </summary>
internal static class MtpDirectoryListing
{
    public static IReadOnlyList<MtpEntry> ResolveEmpty(
        IReadOnlyList<string> nativeErrors,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(nativeErrors);
        if (nativeErrors.Count == 0) return Array.Empty<MtpEntry>();

        var detail = string.Join(
            "; ",
            nativeErrors.Where(error => !string.IsNullOrWhiteSpace(error)));
        if (detail.Length == 0) detail = "libmtp 未提供错误详情";
        var path = string.IsNullOrWhiteSpace(relativePath) ? "/" : relativePath;
        throw new IOException($"无法读取设备目录 {path} ({detail})");
    }
}

/// <summary>
/// Per-session directory cache. The loader is invoked outside the cache and
/// only its successful result is stored, so a native listing error propagates
/// and is retried on the next call instead of masquerading as an empty folder.
/// </summary>
internal sealed class MtpDirectoryListingCache
{
    private readonly Dictionary<(uint StorageId, uint ParentId), IReadOnlyList<MtpEntry>> _entries = [];

    public IReadOnlyList<MtpEntry> GetOrLoad(
        (uint StorageId, uint ParentId) key,
        Func<IReadOnlyList<MtpEntry>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (_entries.TryGetValue(key, out var cached)) return cached;
        var loaded = load();
        _entries[key] = loaded;
        return loaded;
    }

    public void Invalidate((uint StorageId, uint ParentId) key) => _entries.Remove(key);
}
