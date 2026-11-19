using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Constants and shell-property definitions.
/// </summary>
internal static partial class WpdKindleAccess
{
    private const int MyComputerShellFolder = 17;
    private static readonly Guid WpdObjectPropertySet = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");

    /// <summary>
    /// One Shell/device/storage session reused for a whole scan. Opening the
    /// WPD device and walking to the storage object is the expensive part of
    /// every per-book copy; the previous code did it once per book and forced
    /// two full GCs afterwards.
    /// </summary>
    internal sealed class WpdSession : IDisposable
    {
        private readonly dynamic _shell;
        private readonly dynamic _device;
        private readonly dynamic _storage;
        private readonly Dictionary<string, dynamic> _folderCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public WpdSession(KindleDevice device)
        {
            _shell = CreateShell();
            _device = FindDevice(_shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            _storage = FindFirstStorage(_device)
                ?? throw new IOException("无法读取设备内部存储。");
        }

        public dynamic FindItem(string relativePath) =>
            TryFindItem(relativePath) ?? throw new FileNotFoundException("设备文件不存在。", relativePath);

        public dynamic? TryFindItem(string relativePath)
        {
            var normalized = relativePath.Replace('/', '\\').Trim('\\');
            if (normalized.Length == 0) return _storage;

            var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            dynamic current = _storage;
            var prefix = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                prefix = prefix.Length == 0 ? segments[index] : $"{prefix}\\{segments[index]}";
                if (_folderCache.TryGetValue(prefix, out var cached))
                {
                    current = cached;
                    continue;
                }

                var child = FindChild(current, segments[index]);
                if (child is null) return null;
                // Folders are stable for the scan; files are released after
                // each copy, so never keep their RCW in the cache.
                if (index < segments.Length - 1 || (bool)child.IsFolder)
                    _folderCache[prefix] = child;
                current = child;
            }
            return current;
        }

        public static void ReleaseItem(object? item) => Release(item);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(_storage);
            Release(_device);
            Release(_shell);
            // One finalizer flush per scan, not per book.
            FlushReleasedComObjects();
        }
    }
}
