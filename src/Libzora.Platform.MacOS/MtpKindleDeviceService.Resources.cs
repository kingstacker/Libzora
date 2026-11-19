using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS.Mtp;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Device fonts and dictionaries.
/// </summary>
public sealed partial class MtpKindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(kind)) return Task.FromResult<IReadOnlyList<KindleDeviceResource>>([]);
        var directory = device.Profile.ResourceDirectory(kind);
        return WithSessionAsync<IReadOnlyList<KindleDeviceResource>>(device, session =>
        {
            return session.List(directory)
                .Where(entry => !entry.IsFolder
                    && device.Profile.SupportsResourceFile(kind, entry.RelativePath))
                .Select(entry => new KindleDeviceResource
                {
                    Kind = kind,
                    RelativePath = entry.RelativePath,
                    Size = entry.Size,
                    ModifiedAt = entry.ModifiedAt
                })
                .OrderBy(resource => resource.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    public Task SendResourceAsync(
        KindleDevice device,
        KindleResourceKind kind,
        string sourcePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(kind))
            throw new NotSupportedException(UiText.Get("当前设备暂不支持此资源管理功能。"));
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("资源文件不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath))
            throw new NotSupportedException(UiText.Get("当前设备不支持此资源格式。"));
        var directory = device.Profile.ResourceDirectory(kind);
        var fileName = SafeFileName(Path.GetFileName(sourcePath));
        return WithSessionAsync(device, session =>
        {
            session.Upload(directory, sourcePath, fileName,
                (sent, total) => progress?.Report(new TransferProgress(sent, total, "正在发送资源")),
                cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        var temporary = $"{destination}.{Guid.NewGuid():N}.libzora-part";
        return WithSessionAsync(device, session =>
        {
            var entry = session.Find(resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            try
            {
                // libmtp opens the destination with truncation. A failed or
                // cancelled transfer must not destroy the user's existing
                // file, so download beside it and publish only on success.
                session.Download(entry, temporary, cancellationToken: cancellationToken);
                File.Move(temporary, destination, overwrite: true);
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }, cancellationToken);
    }

    public Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsResource(resource.Kind))
            throw new NotSupportedException(UiText.Get("当前设备暂不支持此资源管理功能。"));
        return WithSessionAsync(device, session =>
        {
            var entry = session.Find(resource.RelativePath)
                ?? throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
            session.Delete(entry);
            return true;
        }, cancellationToken);
    }
}
