using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>
/// Device creation, enrichment and thumbnails.
/// </summary>
public sealed partial class MassStorageKindleDeviceService : IKindleDeviceService
{
    private IReadOnlyList<KindleDevice> DetectDevices(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(PathComparer);
        foreach (var mountRoot in _mountRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(mountRoot)) continue;
            if (ReaderDeviceProfiles.DetectMounted(mountRoot) is not null) roots.Add(mountRoot);
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(mountRoot))
                    if (ReaderDeviceProfiles.DetectMounted(directory) is not null) roots.Add(Path.GetFullPath(directory));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady) continue;
                var root = Path.GetFullPath(drive.RootDirectory.FullName);
                var knownMount = _mountRoots.Any(mount => IsWithinOrEqual(root, mount));
                if ((drive.DriveType == DriveType.Removable || knownMount)
                    && ReaderDeviceProfiles.DetectMounted(root, drive.VolumeLabel) is not null) roots.Add(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return roots.Select(CreateDevice).OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static KindleDevice CreateDevice(string root)
    {
        var name = new DirectoryInfo(root).Name;
        long total = 0;
        long free = 0;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) name = drive.VolumeLabel;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var profile = ReaderDeviceProfiles.DetectMounted(root, name);
        if (profile is null || profile.Family == ReaderDeviceFamily.Generic)
            profile = ReaderDeviceProfiles.DetectMounted(root) ?? profile;
        return new KindleDevice
        {
            Profile = profile ?? throw new IOException("设备书籍目录已不可用。"),
            RootPath = Path.GetFullPath(root),
            VolumeSerial = Path.GetFullPath(root),
            Name = string.IsNullOrWhiteSpace(name) ? "阅读设备" : name,
            TotalBytes = total,
            FreeBytes = free,
            IsReady = true,
            Transport = KindleTransport.MassStorage
        };
    }

    private async Task EnrichBookAsync(KindleDevice device, KindleBook book, string source, CancellationToken cancellationToken)
    {
        if (book.Size <= 0 || book.Size > MaximumMetadataFileSize) return;
        try
        {
            var metadata = await _metadata.ReadMetadataAsync(source, cancellationToken);
            if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
            if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
            var coverBytes = metadata.CoverBytes;
            var coverExtension = metadata.CoverExtension;
            if (device.Profile.UsesKindleThumbnails && coverBytes is not { Length: > 0 })
            {
                // Some Kindle books keep the cover only in the device
                // thumbnail cache (for example encrypted KFX files).
                var thumbnailName = await KindleThumbnailService.ReadThumbnailFileNameAsync(source, cancellationToken);
                if (!string.IsNullOrWhiteSpace(thumbnailName))
                {
                    var thumbnailPath = ResolveWithinRoot(
                        device.RootPath,
                        Path.Combine("system", "thumbnails", thumbnailName),
                        device.RootPath);
                    if (File.Exists(thumbnailPath))
                    {
                        coverBytes = await File.ReadAllBytesAsync(thumbnailPath, cancellationToken);
                        coverExtension = ".jpg";
                    }
                }
            }

            if (coverBytes is { Length: > 0 })
            {
                var extension = coverExtension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                var identity = $"{device.Identity}\n{book.RelativePath}\n{book.Size}\n{book.ModifiedAt?.UtcTicks ?? 0}";
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                var coverPath = Path.Combine(_coverCacheDirectory, key + extension);
                await File.WriteAllBytesAsync(coverPath, coverBytes, cancellationToken);
                book.CoverPath = coverPath;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
    }

    private static async Task WriteThumbnailAsync(KindleDevice device, KindleThumbnail thumbnail, CancellationToken cancellationToken)
    {
        var deviceRoot = Path.GetFullPath(device.RootPath);
        var directory = Path.GetFullPath(Path.Combine(deviceRoot, "system", "thumbnails"));
        EnsureUnderRoot(directory, deviceRoot);
        Directory.CreateDirectory(directory);
        var target = ResolveWithinRoot(directory, thumbnail.FileName, directory);
        var temporary = target + ".libzora-part";
        try
        {
            await File.WriteAllBytesAsync(temporary, thumbnail.JpegBytes, cancellationToken);
            File.Move(temporary, target, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }
}
