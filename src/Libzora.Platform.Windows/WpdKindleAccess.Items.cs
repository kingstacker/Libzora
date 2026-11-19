using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Shell item lookup and state helpers.
/// </summary>
internal static partial class WpdKindleAccess
{
    private static dynamic CreateShell()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new PlatformNotSupportedException("Windows Shell 不可用。");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法启动 Windows Shell。");
    }

    private static dynamic? FindDevice(dynamic shell, string shellPath)
    {
        dynamic computer = shell.NameSpace(MyComputerShellFolder);
        dynamic items = computer.Items();
        for (var index = 0; index < (int)items.Count; index++)
        {
            dynamic item = items.Item(index);
            if (string.Equals(Convert.ToString(item.Path), shellPath, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private static dynamic? FindFirstStorage(dynamic device)
    {
        dynamic? children = null;
        try
        {
            children = device.GetFolder.Items();
            for (var index = 0; index < (int)children.Count; index++)
            {
                dynamic child = children.Item(index);
                if ((bool)child.IsFolder) return child;
            }
            return null;
        }
        finally
        {
            Release(children);
        }
    }

    private static dynamic? FindChild(dynamic parent, string name)
    {
        dynamic? children = null;
        try
        {
            children = parent.GetFolder.Items();
            for (var index = 0; index < (int)children.Count; index++)
            {
                dynamic child = children.Item(index);
                if (string.Equals(GetItemFileName(child), name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
            return null;
        }
        finally
        {
            Release(children);
        }
    }

    private static string GetItemFileName(dynamic item)
    {
        // Shell Name is a display label and can omit known file extensions.
        // Use the actual filename for format detection and relative-path lookup.
        try
        {
            string? fileName = Convert.ToString(item.ExtendedProperty("System.FileName"));
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        catch (COMException) { }
        return Convert.ToString(item.Name) ?? string.Empty;
    }

    private static string GetUniqueLocalDestination(string directory, string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        var destination = Path.Combine(directory, safeName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var index = 2; ; index++)
        {
            destination = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(destination)) return destination;
        }
    }

    private static string GetWpdObjectId(dynamic shellItem)
    {
        var value = shellItem.ExtendedProperty($"{{{WpdObjectPropertySet}}} 2");
        var objectId = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(objectId))
            throw new IOException("无法读取设备目标目录的 WPD 对象 ID。");
        return objectId;
    }

    private static dynamic? FindItemByRelativePath(dynamic root, string relativePath)
    {
        dynamic? current = root;
        foreach (var segment in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = current is null ? null : FindChild(current, segment);
            if (current is null) return null;
        }
        return current;
    }

    private static dynamic FindOrCreateFolderPath(dynamic storage, string relativePath, CancellationToken cancellationToken)
    {
        dynamic current = storage;
        foreach (var segment in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic? child = FindChild(current, segment);
            if (child is null)
            {
                dynamic folder = current.GetFolder;
                folder.NewFolder(segment);
                var startedAt = DateTime.UtcNow;
                while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(10))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    child = FindChild(current, segment);
                    if (child is not null) break;
                    Thread.Sleep(200);
                }
            }
            if (child is null || !(bool)child.IsFolder)
                throw new IOException($"无法创建设备目录：{relativePath}");
            current = child;
        }
        return current;
    }

    private static string GetUniqueFileName(dynamic folderItem, string fileName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic children = folderItem.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
            names.Add(Convert.ToString(children.Item(index).Name) ?? string.Empty);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? fileName : $"{stem} ({index}){extension}";
            if (!names.Contains(candidate) && !names.Contains(candidate + ".libzora-part"))
                return candidate;
        }
    }

    private static long WaitForItem(
        KindleDevice device,
        string name,
        long expectedSize,
        TimeSpan timeout,
        IProgress<TransferProgress>? progress,
        string displayName,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadDocumentItemState(device, name);
            if (state.Exists)
            {
                var size = state.Size;
                progress?.Report(new TransferProgress(Math.Min(size, expectedSize), expectedSize, $"正在发送 {displayName}"));
                if (size == expectedSize) return size;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("等待设备完成文件写入超时。");
    }

    private static (bool Exists, long Size) ReadDocumentItemState(KindleDevice device, string relativePath)
    {
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? documents = storage is null ? null : FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            dynamic? item = documents is null ? null : FindItemByRelativePath(documents, relativePath);
            if (item is null) return (false, 0);
            long size = ReadInt64Property(item, "System.Size");
            return (true, size);
        }
        catch (COMException)
        {
            return (false, 0);
        }
        finally
        {
            Release(shell);
        }
    }

    private static bool DocumentItemExists(KindleDevice device, string relativePath) =>
        ReadDocumentItemState(device, relativePath).Exists;

    private static void WaitForStorageItem(
        KindleDevice device,
        string relativePath,
        long expectedSize,
        IProgress<TransferProgress>? progress,
        string displayName,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(2 + expectedSize / (100d * 1024 * 1024), 2, 30));
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadStorageItemState(device, relativePath);
            if (state.Exists)
            {
                progress?.Report(new TransferProgress(Math.Min(state.Size, expectedSize), expectedSize, $"正在发送 {displayName}"));
                if (state.Size == expectedSize) return;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("等待设备完成资源写入超时。");
    }

    private static (bool Exists, long Size) ReadStorageItemState(KindleDevice device, string relativePath)
    {
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath);
            storage = kindle is null ? null : FindFirstStorage(kindle);
            item = storage is null ? null : FindItemByRelativePath(storage, relativePath);
            return item is null ? (false, 0L) : (true, (long)ReadInt64Property(item, "System.Size"));
        }
        catch (COMException)
        {
            return (false, 0);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    private static void TryRemoveStorageItem(KindleDevice device, string relativePath)
    {
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? item = storage is null ? null : FindItemByRelativePath(storage, relativePath);
            if (item is not null && !(bool)item.IsFolder) ShellFileOperation.DeletePermanently((object)item);
        }
        catch (COMException) { }
        finally { Release(shell); }
    }

    private static void TryRemoveDocumentItem(KindleDevice device, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0) return;

        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? documents = storage is null ? null : FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            dynamic? item = documents is null ? null : FindChild(documents, name);
            if (item is not null && !(bool)item.IsFolder)
            {
                ShellFileOperation.DeletePermanently((object)item);
            }
        }
        catch (COMException) { }
        finally
        {
            Release(shell);
        }
    }

    private static long ReadInt64Property(dynamic item, string propertyName)
    {
        try { return ConvertToInt64(item.ExtendedProperty(propertyName)); }
        catch (COMException) { return 0; }
    }

    private static DateTimeOffset? ReadDateTimeOffsetProperty(dynamic item, string propertyName)
    {
        try
        {
            var value = item.ExtendedProperty(propertyName);
            if (value is DateTime dateTime) return new DateTimeOffset(dateTime.ToUniversalTime());
            return DateTimeOffset.TryParse((string?)Convert.ToString(value), out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (COMException) { return null; }
    }

    private static long ConvertToInt64(object? value)
    {
        try { return Convert.ToInt64(value); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void FlushReleasedComObjects()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
