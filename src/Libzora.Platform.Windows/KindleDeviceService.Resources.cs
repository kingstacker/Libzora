using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Device resource scan, send, export and removal.
/// </summary>
public sealed partial class KindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public async Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(kind)) return [];
        if (device.Transport == KindleTransport.Wpd)
            return await Task.Run(() => WpdKindleAccess.ScanResources(device, kind, cancellationToken), cancellationToken);

        var root = GetResourceRoot(device, kind);
        if (!Directory.Exists(root)) return [];
        var resources = new List<KindleDeviceResource>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!device.Profile.SupportsResourceFile(kind, path)) continue;
            try
            {
                var info = new FileInfo(path);
                resources.Add(new KindleDeviceResource
                {
                    Kind = kind,
                    RelativePath = Path.GetRelativePath(device.RootPath, path),
                    Size = info.Length,
                    Sha256 = await Hashing.Sha256Async(path, cancellationToken),
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return resources.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task SendResourceAsync(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath))
            throw new InvalidDataException(UiText.Get("当前设备不支持此资源格式。"));
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.SendResource(device, kind, sourcePath, progress, cancellationToken), cancellationToken);
            return;
        }

        var root = GetResourceRoot(device, kind);
        Directory.CreateDirectory(root);
        var fileName = GetSafeFileName(Path.GetFileName(sourcePath));
        var destination = GetUniqueDestination(root, fileName);
        // A unique temporary name prevents a stale partial file from a
        // previous interrupted transfer from blocking the next import.
        var temporary = $"{destination}.{Guid.NewGuid():N}.libzora-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            // Hash the exact bytes while the source stream is already open.
            // Re-opening a dragged font here races Explorer/font preview and
            // was the cause of the sharing-violation error shown in the UI.
            var sourceHash = await CopyAndHashAsync(sourcePath, temporary, total, progress, cancellationToken);
            var targetHash = await HashFileAsync(temporary, cancellationToken);
            if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备资源未写入。");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
        }
        finally
        {
            await TryDeleteAsync(temporary);
        }
    }

    public async Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("导出路径无效。"));
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.CopyResourceToLocal(device, resource, destination, cancellationToken), cancellationToken);
            return;
        }

        var root = GetResourceRoot(device, resource.Kind);
        var source = Path.GetFullPath(Path.Combine(device.RootPath, resource.RelativePath));
        EnsureUnderRoot(source, root);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不允许读取设备目录中的链接文件。");
        // Publish a sibling temporary file only after the whole copy succeeds.
        // Opening the final path with FileMode.Create truncated an existing
        // export before a device read failure or cancellation could occur.
        var temporary = $"{destination}.{Guid.NewGuid():N}.libzora-part";
        try
        {
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
        }
        finally
        {
            await TryDeleteAsync(temporary);
        }
    }

    public async Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.RemoveResource(device, resource, cancellationToken), cancellationToken);
            return;
        }
        var root = GetResourceRoot(device, resource.Kind);
        var path = Path.GetFullPath(Path.Combine(device.RootPath, resource.RelativePath));
        EnsureUnderRoot(path, root);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("不允许删除设备目录中的链接文件。");
        if (File.Exists(path)) await DeleteFileAsync(path, cancellationToken);
    }
}
