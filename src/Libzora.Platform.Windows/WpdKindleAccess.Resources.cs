using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Device resource scan, send, copy and removal.
/// </summary>
internal static partial class WpdKindleAccess
{
    public static void SendResource(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath)) throw new InvalidDataException("设备资源格式不受支持。");
        var source = new FileInfo(sourcePath);
        var rootRelative = device.Profile.ResourceDirectory(kind);
        string? targetRelativePath = null;
        var completed = false;
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            dynamic targetRoot = FindOrCreateFolderPath(storage, rootRelative, cancellationToken);
            var finalName = GetUniqueFileName(targetRoot, source.Name);
            targetRelativePath = $"{rootRelative}\\{finalName}";
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TransferProgress(0, source.Length, $"正在发送 {source.Name}"));
            WpdNativeTransfer.SendFile(
                device.RootPath,
                GetWpdObjectId(targetRoot),
                sourcePath,
                finalName,
                progress,
                cancellationToken);
            WaitForStorageItem(device, targetRelativePath, source.Length, progress, source.Name, cancellationToken);
            progress?.Report(new TransferProgress(source.Length, source.Length, $"已发送 {finalName}"));
            completed = true;
        }
        catch (COMException exception)
        {
            throw new IOException("MTP 资源传输失败，请确认设备仍保持连接。", exception);
        }
        finally
        {
            Release(shell);
            if (!completed && targetRelativePath is not null) TryRemoveStorageItem(device, targetRelativePath);
        }
    }

    public static void CopyResourceToLocal(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("不能导出设备文件夹。");
            WpdNativeTransfer.CopyFileToLocal(
                device.RootPath,
                GetWpdObjectId(item),
                destinationPath,
                resource.Size,
                cancellationToken,
                overwrite: true);
        }
        catch (COMException exception)
        {
            throw new IOException("无法从 MTP 设备导出资源。", exception);
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

    public static void RemoveResource(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken)
    {
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("不能删除设备文件夹。");
            cancellationToken.ThrowIfCancellationRequested();
            ShellFileOperation.DeletePermanently((object)item);
            var startedAt = DateTime.UtcNow;
            while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadStorageItemState(device, resource.RelativePath).Exists) return;
                Thread.Sleep(250);
            }
            throw new TimeoutException("等待设备删除资源超时。");
        }
        catch (COMException exception)
        {
            throw new IOException("无法删除 MTP 设备资源。", exception);
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

    public static string ReadClippingsText(KindleDevice device, CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "clippings-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? item = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, @"documents\My Clippings.txt");
            if (item is null) return string.Empty;
            var size = ReadInt64Property(item, "System.Size");
            var localPath = Path.Combine(stagingDirectory, "My Clippings.txt");
            WpdNativeTransfer.CopyFileToLocal(
                device.RootPath,
                GetWpdObjectId(item),
                localPath,
                size,
                cancellationToken);
            using var reader = new StreamReader(localPath, System.Text.Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (COMException exception)
        {
            throw new IOException("无法读取 MTP Kindle 的 My Clippings.txt。", exception);
        }
        finally
        {
            Release(item);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
            try { Directory.Delete(stagingDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
