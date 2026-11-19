using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Device detection and session release.
/// </summary>
internal static partial class WpdKindleAccess
{
    public static void ReleaseDeviceSessions(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Shell.Application exposes WPD objects through COM wrappers. Releasing
        // their final references closes the WPD session, matching calibre's MTP
        // eject semantics without placing the physical device in pending-eject.
        FlushReleasedComObjects();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static void CloseDeviceSession(KindleDevice device, CancellationToken cancellationToken)
    {
        ReleaseDeviceSessions(cancellationToken);
        WpdSessionCloser.CloseSession(device.RootPath, cancellationToken);
        FlushReleasedComObjects();
    }

    public static IReadOnlyList<KindleDevice> DetectDevices(CancellationToken cancellationToken)
    {
        // The PnP manager can establish absence without opening Shell storage
        // objects or forcing a full GC. This keeps mass-storage discovery and
        // short hotplug retries cheap when no MTP device is attached. An
        // unavailable manager reports "unknown" and still takes the full path.
        if (!WpdAttachmentProbe.HasAnyDevice(cancellationToken)) return [];
        var devices = new List<KindleDevice>();
        dynamic? shell = null;
        dynamic? computer = null;
        dynamic? items = null;
        try
        {
            shell = CreateShell();
            computer = shell.NameSpace(MyComputerShellFolder);
            items = computer.Items();
            for (var index = 0; index < (int)items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? item = null;
                dynamic? storage = null;
                try
                {
                    item = items.Item(index);
                    if (!(bool)item.IsFolder) continue;
                    string name = Convert.ToString(item.Name) ?? string.Empty;
                    string shellPath = Convert.ToString(item.Path) ?? string.Empty;
                    // WPD shell entries contain their native device path. Local disks
                    // and unrelated virtual folders must not be probed as readers.
                    if (!shellPath.Contains(@"\\?\", StringComparison.OrdinalIgnoreCase)) continue;
                    storage = FindFirstStorage(item);
                    if (storage is null) continue;
                    bool HasDirectory(string relativePath)
                    {
                        dynamic? folder = null;
                        try
                        {
                            folder = FindItemByRelativePath(storage, relativePath);
                            return folder is not null && (bool)folder.IsFolder;
                        }
                        finally { Release(folder); }
                    }
                    var profile = ReaderDeviceProfiles.Detect(name, HasDirectory, shellPath);
                    if (profile is null) continue;
                    devices.Add(new KindleDevice
                    {
                        Profile = profile,
                        RootPath = shellPath,
                        VolumeSerial = shellPath,
                        Name = string.IsNullOrWhiteSpace(name) ? profile.DisplayName : name,
                        TotalBytes = ReadInt64Property(storage, "System.Capacity"),
                        FreeBytes = ReadInt64Property(storage, "System.FreeSpace"),
                        IsReady = true,
                        Transport = KindleTransport.Wpd
                    });
                }
                catch (COMException)
                {
                    // A locked or disconnected device must not hide other readers.
                }
                finally
                {
                    Release(storage);
                    Release(item);
                }
            }
        }
        catch (COMException)
        {
            return devices;
        }
        finally
        {
            Release(items);
            Release(computer);
            Release(shell);
            FlushReleasedComObjects();
        }
        return devices;
    }

    public static IReadOnlyList<KindleBook> ScanBooks(
        KindleDevice device,
        IReadOnlySet<string> supportedExtensions,
        CancellationToken cancellationToken)
    {
        var books = new List<KindleBook>();
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            if (kindle is null) throw new IOException("设备已断开，请重新连接并解锁设备后重试。");
            dynamic? storage = FindFirstStorage(kindle);
            if (storage is null) throw new IOException("无法读取设备存储，请确认设备已解锁后重试。");
            dynamic? documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            if (documents is null) return books;

            var folders = new Stack<(object Folder, string RelativePath)>();
            folders.Push((documents.GetFolder, string.Empty));
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = folders.Pop();
                dynamic folder = entry.Folder;
                dynamic children = folder.Items();
                for (var index = 0; index < (int)children.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dynamic child = children.Item(index);
                    var name = GetItemFileName(child);
                    var relativePath = string.IsNullOrEmpty(entry.RelativePath)
                        ? name
                        : $"{entry.RelativePath}\\{name}";
                    if ((bool)child.IsFolder)
                    {
                        var isRootDictionaryFolder = string.IsNullOrEmpty(entry.RelativePath)
                            && name.Equals("dictionaries", StringComparison.OrdinalIgnoreCase);
                        if (!name.StartsWith('.') && !name.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase)
                            && !name.Equals("system", StringComparison.OrdinalIgnoreCase)
                            && !name.Equals("fonts", StringComparison.OrdinalIgnoreCase) && !isRootDictionaryFolder)
                            folders.Push((child.GetFolder, relativePath));
                        continue;
                    }

                    var extension = Path.GetExtension(name);
                    if (!supportedExtensions.Contains(extension) || !device.Profile.IsBookPath(relativePath)) continue;
                    books.Add(new KindleBook
                    {
                        RelativePath = relativePath,
                        Format = extension.TrimStart('.').ToLowerInvariant(),
                        Size = ReadInt64Property(child, "System.Size"),
                        ModifiedAt = ReadDateTimeOffsetProperty(child, "System.DateModified"),
                        Sha256 = string.Empty,
                        IsManagedByLibzora = false
                    });
                }
            }
        }
        catch (COMException exception)
        {
            throw new IOException("设备书库读取中断，请确认连接并解锁设备后重试。", exception);
        }
        finally
        {
            Release(shell);
            FlushReleasedComObjects();
        }
        return books.OrderBy(book => book.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<KindleDeviceResource> ScanResources(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken)
    {
        var resources = new List<KindleDeviceResource>();
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? resourceRoot = null;
        Stack<(object Folder, string RelativePath)>? folders = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath);
            storage = kindle is null ? null : FindFirstStorage(kindle);
            var rootRelative = device.Profile.ResourceDirectory(kind);
            resourceRoot = storage is null ? null : FindItemByRelativePath(storage, rootRelative);
            if (resourceRoot is null || !(bool)resourceRoot.IsFolder) return resources;

            folders = new Stack<(object Folder, string RelativePath)>();
            folders.Push((resourceRoot.GetFolder, rootRelative));
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = folders.Pop();
                dynamic? folder = entry.Folder;
                dynamic? children = null;
                try
                {
                    children = folder.Items();
                    for (var index = 0; index < (int)children.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        dynamic? child = null;
                        try
                        {
                            child = children.Item(index);
                            var name = GetItemFileName(child);
                            var relativePath = $"{entry.RelativePath}\\{name}";
                            if ((bool)child.IsFolder)
                            {
                                folders.Push((child.GetFolder, relativePath));
                                continue;
                            }
                            if (!device.Profile.SupportsResourceFile(kind, name)) continue;
                            resources.Add(new KindleDeviceResource
                            {
                                Kind = kind,
                                RelativePath = relativePath,
                                Size = ReadInt64Property(child, "System.Size")
                            });
                        }
                        finally
                        {
                            Release(child);
                        }
                    }
                }
                finally
                {
                    Release(children);
                    Release(folder);
                }
            }
        }
        catch (COMException exception)
        {
            throw new IOException("无法读取 MTP 设备资源目录。", exception);
        }
        finally
        {
            if (folders is not null)
            {
                while (folders.Count > 0)
                    Release(folders.Pop().Folder);
            }
            Release(resourceRoot);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
        return resources.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
