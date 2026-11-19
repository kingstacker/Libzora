using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Path roots, copying, hashing and eject support.
/// </summary>
public sealed partial class KindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
            return Task.Run(() => WpdKindleAccess.CloseDeviceSession(device, cancellationToken), cancellationToken);
        return Task.Run(() => EjectDrive(device.RootPath, cancellationToken), cancellationToken);
    }

    private static string GetDocumentsRoot(KindleDevice device)
    {
        return ReaderDevicePaths.ResolveDirectory(device.RootPath, device.Profile.BooksDirectory);
    }

    private static string GetResourceRoot(KindleDevice device, KindleResourceKind kind)
    {
        var resourceRoot = ReaderDevicePaths.ResolveDirectory(device.RootPath, device.Profile.ResourceDirectory(kind));
        if (Directory.Exists(resourceRoot) && (File.GetAttributes(resourceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("设备资源目录不能是链接或联接点。");
        return resourceRoot;
    }

    private static string GetClippingsPath(KindleDevice device)
    {
        var documents = GetDocumentsRoot(device);
        var path = Path.GetFullPath(Path.Combine(documents, "My Clippings.txt"));
        EnsureUnderRoot(path, documents);
        return path;
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("设备路径不在允许的目录范围内。");
    }

    private static async Task CopyAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action = "正在发送")
    {
        await CopyCoreAsync(source, target, total, progress, cancellationToken, action, hash: null);
    }

    private static async Task<string> CopyAndHashAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action = "正在发送")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyCoreAsync(source, target, total, progress, cancellationToken, action, hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyCoreAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action,
        IncrementalHash? hash)
    {
        await using var input = await OpenFileStreamAsync(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        await using var output = await OpenFileStreamAsync(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash?.AppendData(buffer, 0, read);
            copied += read;
            progress?.Report(new TransferProgress(copied, total, $"{action} {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenFileStreamAsync(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<FileStream> OpenFileStreamAsync(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        CancellationToken cancellationToken)
    {
        IOException? lastSharingException = null;
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, mode, access, share, 128 * 1024, options);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                lastSharingException = exception;
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }

        throw lastSharingException ?? new IOException($"无法打开传输文件：{path}");
    }

    private static async Task MoveFileAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task TryDeleteAsync(string path)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException exception)
            {
                // Cleanup must never replace the real transfer exception. A
                // just-created file can briefly be scanned by Explorer or an
                // antivirus process, so give that handle time to close.
                if (!IsSharingViolation(exception) || attempt + 1 >= FileOperationRetryAttempts) return;
                await Task.Delay(FileOperationRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33;
    }

    private static string GetUniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string GetSafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "book.bin" : value;
    }

    private static string GetVolumeSerial(string root)
    {
        return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0)
            ? serial.ToString("X8")
            : root.TrimEnd('\\');
    }

    private static void EjectDrive(string root, CancellationToken cancellationToken)
    {
        var driveLetter = root.TrimEnd('\\').TrimEnd('/');
        if (driveLetter.Length < 2) throw new InvalidOperationException("无法确定设备盘符。");
        var handle = CreateFile(
            $@"\\.\{driveLetter}",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw CreateEjectIOException("无法打开设备卷", Marshal.GetLastWin32Error());

        try
        {
            // A volume can be briefly busy while Explorer closes enumeration
            // handles. Locking also flushes cached writes before removal.
            var locked = false;
            var lockError = 0;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                locked = DeviceIoControl(
                    handle,
                    FsctlLockVolume,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);
                if (locked) break;
                lockError = Marshal.GetLastWin32Error();
                Thread.Sleep(150);
            }
            if (!locked)
                throw CreateEjectIOException("设备正在被程序占用，无法锁定卷", lockError);

            if (!DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw CreateEjectIOException("无法卸载设备卷", Marshal.GetLastWin32Error());

            // Clear any removable-media lock before asking the storage stack
            // to eject. Some USB mass-storage drivers require this explicitly.
            var allowRemoval = (byte)0;
            DeviceIoControl(
                handle,
                IoctlStorageMediaRemoval,
                ref allowRemoval,
                1,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);

            if (!DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw CreateEjectIOException("Windows 存储驱动拒绝弹出设备", Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(handle); }
    }

    private static IOException CreateEjectIOException(string operation, int errorCode)
    {
        var detail = errorCode == 0 ? "未知 Windows 错误" : new Win32Exception(errorCode).Message;
        return new IOException($"{operation}：{detail}（错误 {errorCode}）。");
    }
}
