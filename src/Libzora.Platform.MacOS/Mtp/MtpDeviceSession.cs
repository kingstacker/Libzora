using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Libzora.Platform.MacOS.Mtp;

internal sealed record MtpRawDevice(
    uint BusLocation,
    byte DevNum,
    ushort VendorId,
    ushort ProductId,
    byte[] Template);

internal sealed record MtpStorageInfo(uint Id, string Description, long FreeBytes, long TotalBytes);

internal sealed class MtpEntry
{
    public required uint ItemId { get; init; }
    public required uint ParentId { get; init; }
    public required uint StorageId { get; init; }
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required bool IsFolder { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
}

/// <summary>
/// One open libmtp session against a device. libmtp is not thread-safe, so a
/// session must never be used concurrently; the device service serializes all
/// access with a global gate.
/// </summary>
internal sealed class MtpDeviceSession : IDisposable
{
    private const int MaximumDepth = 16;
    private static readonly int RawDeviceSize = Marshal.SizeOf<LibMtp.RawDevice>();
    private static int _initialized;

    private static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0) return;
        LibMtp.LIBMTP_Init();
        Volatile.Write(ref _initialized, 1);
    }

    private readonly IntPtr _device;
    private readonly IntPtr _rawDevice;
    private readonly MtpDirectoryListingCache _children = new();
    private bool _disposed;

    private MtpDeviceSession(IntPtr device, IntPtr rawDevice, MtpRawDevice raw)
    {
        _device = device;
        _rawDevice = rawDevice;
        Raw = raw;
        Model = LibMtp.ToOwnedString(LibMtp.LIBMTP_Get_Modelname(device));
        Manufacturer = LibMtp.ToOwnedString(LibMtp.LIBMTP_Get_Manufacturername(device));
        SerialNumber = LibMtp.ToOwnedString(LibMtp.LIBMTP_Get_Serialnumber(device));
        if (string.IsNullOrWhiteSpace(Model)) Model = "Kindle";

        if (LibMtp.LIBMTP_Get_Storage(device, 0) < 0) Throw("无法读取设备存储信息");
        var storages = new List<MtpStorageInfo>();
        for (var storage = Marshal.ReadIntPtr(device, LibMtp.DeviceStorageOffset);
             storage != IntPtr.Zero;
             storage = Marshal.ReadIntPtr(storage, 56))
        {
            var info = Marshal.PtrToStructure<LibMtp.DeviceStorage>(storage);
            storages.Add(new MtpStorageInfo(
                info.Id,
                LibMtp.ToString(info.StorageDescription),
                (long)info.FreeSpaceInBytes,
                (long)info.MaxCapacity));
        }
        if (storages.Count == 0) Throw("设备没有可用的存储");
        Storages = storages;
        PrimaryStorage = storages.OrderByDescending(storage => storage.TotalBytes).First();
    }

    public MtpRawDevice Raw { get; }
    public string Model { get; }
    public string Manufacturer { get; }
    public string SerialNumber { get; }
    public IReadOnlyList<MtpStorageInfo> Storages { get; }
    public MtpStorageInfo PrimaryStorage { get; }

    public static bool IsSupported => OperatingSystem.IsMacOS();

    public static IReadOnlyList<MtpRawDevice> Detect()
    {
        EnsureInitialized();
        var result = LibMtp.LIBMTP_Detect_Raw_Devices(out var devices, out var count);
#if DEBUG
        if (result != LibMtp.ErrorNone && count > 0)
            Console.Error.WriteLine($"MTP raw detection: result={(int)result} count={count}.");
#endif
        _ = result;
        if (devices == IntPtr.Zero) return [];
        try
        {
            // libmtp can report a USB-layer error for a hotplug race while it
            // still fills the device list. The count is authoritative: dropping
            // a populated list made a reconnected Kindle look absent until the
            // app was restarted.
            if (count <= 0) return [];
            var raws = new List<MtpRawDevice>(count);
            for (var index = 0; index < count; index++)
            {
                var pointer = devices + index * RawDeviceSize;
                var raw = Marshal.PtrToStructure<LibMtp.RawDevice>(pointer);
                var template = new byte[RawDeviceSize];
                Marshal.Copy(pointer, template, 0, RawDeviceSize);
                raws.Add(new MtpRawDevice(
                    raw.BusLocation,
                    raw.DevNum,
                    raw.Entry.VendorId,
                    raw.Entry.ProductId,
                    template));
            }
            return raws;
        }
        finally
        {
            LibMtp.LIBMTP_FreeMemory(devices);
        }
    }

    public static MtpDeviceSession Open(MtpRawDevice raw)
    {
        // libmtp keeps a reference to the raw device descriptor for the life
        // of the session, so the copy must outlive LIBMTP_Open_Raw_Device.
        var native = Marshal.AllocHGlobal(raw.Template.Length);
        Marshal.Copy(raw.Template, 0, native, raw.Template.Length);
        var device = LibMtp.LIBMTP_Open_Raw_Device_Uncached(native);
        if (device == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(native);
            throw new IOException("无法打开 MTP 设备。");
        }
        try
        {
            return new MtpDeviceSession(device, native, raw);
        }
        catch
        {
            LibMtp.LIBMTP_Release_Device(device);
            Marshal.FreeHGlobal(native);
            throw;
        }
    }

    public IReadOnlyList<MtpEntry> List(string relativeDirectory)
    {
        var segments = Split(relativeDirectory);
        var parent = Resolve(segments);
        return parent is null ? [] : Children(parent.Value.StorageId, parent.Value.ParentId, parent.Value.RelativePath);
    }

    public MtpEntry? Find(string relativePath)
    {
        var segments = Split(relativePath);
        if (segments.Length == 0) return null;
        var parent = Resolve(segments[..^1]);
        if (parent is null) return null;
        return Children(parent.Value.StorageId, parent.Value.ParentId, parent.Value.RelativePath)
            .FirstOrDefault(entry => entry.Name.Equals(segments[^1], StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<MtpEntry> Enumerate(string relativeDirectory)
    {
        var segments = Split(relativeDirectory);
        var root = Resolve(segments);
        if (root is null) return [];
        var results = new List<MtpEntry>();
        EnumerateChildren(root.Value.StorageId, root.Value.ParentId, root.Value.RelativePath, 0, results);
        return results;
    }

    public MtpEntry EnsureDirectory(string relativeDirectory)
    {
        var segments = Split(relativeDirectory);
        var storageId = PrimaryStorage.Id;
        uint parentId = LibMtp.FilesAndFoldersRoot;
        var parentPath = string.Empty;
        foreach (var segment in segments)
        {
            var currentPath = parentPath.Length == 0 ? segment : $"{parentPath}/{segment}";
            var existing = Children(storageId, parentId, parentPath)
                .FirstOrDefault(entry => entry.IsFolder && entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                parentId = existing.ItemId;
                parentPath = existing.RelativePath;
                continue;
            }
            var created = LibMtp.LIBMTP_Create_Folder(_device, segment, parentId, storageId);
            if (created == 0) Throw($"无法创建设备文件夹 {currentPath}");
            Invalidate(storageId, parentId);
            parentId = created;
            parentPath = currentPath;
        }
        return new MtpEntry
        {
            ItemId = parentId,
            ParentId = 0,
            StorageId = storageId,
            Name = segments.Length == 0 ? string.Empty : segments[^1],
            RelativePath = Normalize(relativeDirectory),
            IsFolder = true
        };
    }

    public void Download(
        MtpEntry entry,
        string destinationPath,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsFolder) throw new InvalidOperationException("不能直接下载文件夹。");
        ClearErrors();
        // Always install a callback: libmtp calls it periodically, and a
        // non-zero return aborts an in-flight transfer. Without the callback
        // cancellation could only take effect after the whole file moved.
        LibMtp.ProgressCallback callback = (sent, total, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return -1;
            progress?.Invoke((long)sent, (long)total);
            return 0;
        };
        var result = LibMtp.LIBMTP_Get_File_To_File(
            _device,
            entry.ItemId,
            destinationPath,
            callback,
            IntPtr.Zero);
        if (result >= 0) return;
        if (cancellationToken.IsCancellationRequested)
        {
            try { File.Delete(destinationPath); } catch { }
            ClearErrors();
            throw new OperationCanceledException(cancellationToken);
        }
        Throw($"无法从设备读取 {entry.RelativePath}");
    }

    /// <summary>
    /// Uploads <paramref name="sourcePath"/> as <paramref name="fileName"/>.
    /// An existing file with that name is replaced only after the new bytes
    /// are fully on the device: the upload goes to a temporary name first, so
    /// a failed or cancelled transfer leaves the original untouched. The
    /// replacement itself is handled by <see cref="MtpFileReplacement"/>,
    /// which preserves the previous bytes when the firmware cannot rename or
    /// when the final installation fails.
    /// </summary>
    public MtpEntry Upload(
        string relativeDirectory,
        string sourcePath,
        string fileName,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = EnsureDirectory(relativeDirectory);
        var existing = FindChild(directory, fileName);
        if (existing is null)
            return UploadNew(directory, sourcePath, fileName, progress, cancellationToken);

        // The suffix is not a Kindle book extension, so the device never
        // indexes the staged copy.
        var temporaryName = $"{fileName}.libzora-part-{Guid.NewGuid().ToString("N")[..8]}";
        var staged = UploadNew(directory, sourcePath, temporaryName, progress, cancellationToken);
        try
        {
            return MtpFileReplacement.Commit(
                existing,
                staged,
                temporaryName,
                sourcePath,
                fileName,
                cancellationToken,
                new UploadReplacementOperations(this, directory));
        }
        catch (OperationCanceledException)
        {
            // Commit can observe cancellation before it starts moving objects;
            // clean up the staged copy so a cancelled replace does not leave a
            // permanent temporary object on the reader. Once Commit has begun
            // it either restores the original or reports the surviving copies.
            try { Delete(staged); }
            catch (IOException) { }
            throw;
        }
    }

    private MtpEntry? FindChild(MtpEntry directory, string fileName) =>
        Children(directory.StorageId, directory.ItemId, directory.RelativePath)
            .FirstOrDefault(entry => !entry.IsFolder && entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    private MtpEntry UploadNew(
        MtpEntry directory,
        string sourcePath,
        string fileName,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearErrors();
        var size = new FileInfo(sourcePath).Length;
        var file = LibMtp.LIBMTP_new_file_t();
        if (file == IntPtr.Zero) throw new IOException("无法创建设备文件结构。");
        try
        {
            // libmtp takes ownership of the filename pointer and frees it with
            // LIBMTP_destroy_file_t.
            var info = new LibMtp.File
            {
                ItemId = 0,
                ParentId = directory.ItemId,
                StorageId = directory.StorageId,
                FileName = Marshal.StringToCoTaskMemUTF8(fileName),
                FileSize = (ulong)size,
                ModificationDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileType = LibMtp.FileTypeUnknown,
                Next = IntPtr.Zero
            };
            Marshal.StructureToPtr(info, file, false);
            LibMtp.ProgressCallback callback = (sent, total, _) =>
            {
                if (cancellationToken.IsCancellationRequested) return -1;
                progress?.Invoke((long)sent, (long)total);
                return 0;
            };
            var result = LibMtp.LIBMTP_Send_File_From_File(
                _device,
                sourcePath,
                file,
                callback,
                IntPtr.Zero);
            if (result < 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // libmtp may have created a partial object before the
                    // callback aborted the transfer. Remove it so a retry does
                    // not leave an unreadable half-book on the device.
                    Invalidate(directory.StorageId, directory.ItemId);
                    try
                    {
                        if (FindChild(directory, fileName) is { } partial) Delete(partial);
                    }
                    catch (Exception cleanupFailure) when (cleanupFailure is IOException or InvalidOperationException)
                    {
                        // The transfer already failed; a cleanup failure must
                        // not hide the cancellation.
                    }
                    ClearErrors();
                    throw new OperationCanceledException(cancellationToken);
                }
                Throw($"无法写入设备文件 {fileName}");
            }
        }
        finally
        {
            LibMtp.LIBMTP_destroy_file_t(file);
        }

        Invalidate(directory.StorageId, directory.ItemId);
        var uploaded = FindChild(directory, fileName);
        if (uploaded is null)
            throw new IOException($"设备未确认写入文件 {fileName}");
        // Some firmware reports 0 until the object is re-read; only a known,
        // mismatching size is treated as a failed transfer.
        if (uploaded.Size > 0 && uploaded.Size != size)
            throw new IOException($"设备文件大小校验失败：{fileName}");
        return uploaded;
    }

    public void Delete(MtpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ClearErrors();
        if (LibMtp.LIBMTP_Delete_Object(_device, entry.ItemId) != 0)
            Throw($"无法删除设备文件 {entry.RelativePath}");
        Invalidate(entry.StorageId, entry.ParentId);
    }

    /// <summary>
    /// Deletes a folder and everything below it. PTP device support for
    /// recursive <c>DeleteObject</c> is inconsistent, so children are removed
    /// explicitly before the folder itself.
    /// </summary>
    public void DeleteTree(MtpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsFolder)
        {
            Delete(entry);
            return;
        }
        foreach (var child in Children(entry.StorageId, entry.ItemId, entry.RelativePath).ToArray())
        {
            if (child.IsFolder) DeleteTree(child);
            else Delete(child);
        }
        if (LibMtp.LIBMTP_Delete_Object(_device, entry.ItemId) != 0)
            Throw($"无法删除设备文件夹 {entry.RelativePath}");
        Invalidate(entry.StorageId, entry.ParentId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LibMtp.LIBMTP_Release_Device(_device);
        Marshal.FreeHGlobal(_rawDevice);
    }

    private void EnumerateChildren(uint storageId, uint parentId, string relativePath, int depth, List<MtpEntry> results)
    {
        if (depth > MaximumDepth) return;
        foreach (var entry in Children(storageId, parentId, relativePath))
        {
            results.Add(entry);
            if (entry.IsFolder)
                EnumerateChildren(entry.StorageId, entry.ItemId, entry.RelativePath, depth + 1, results);
        }
    }

    private (uint StorageId, uint ParentId, string RelativePath)? Resolve(string[] segments)
    {
        var storageId = PrimaryStorage.Id;
        uint parentId = LibMtp.FilesAndFoldersRoot;
        var relativePath = string.Empty;
        foreach (var segment in segments)
        {
            var match = Children(storageId, parentId, relativePath)
                .FirstOrDefault(entry => entry.IsFolder && entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            storageId = match.StorageId;
            parentId = match.ItemId;
            relativePath = match.RelativePath;
        }
        return (storageId, parentId, relativePath);
    }

    private IReadOnlyList<MtpEntry> Children(uint storageId, uint parentId, string relativeParentPath)
    {
        return _children.GetOrLoad((storageId, parentId), () =>
        {
            ClearErrors();
            var list = LibMtp.LIBMTP_Get_Files_And_Folders(_device, storageId, parentId);
            try
            {
                if (list == IntPtr.Zero)
                {
                    var errors = LibMtp.ReadErrorStack(_device);
                    ClearErrors();
                    // A null list is only an empty directory when libmtp did
                    // not record an error. A failed listing throws here and is
                    // therefore never cached as empty.
                    return MtpDirectoryListing.ResolveEmpty(errors, relativeParentPath);
                }
                return ParseChildren(list, relativeParentPath);
            }
            finally
            {
                // libmtp has no list destructor; every node is released individually.
                var reference = list;
                while (reference != IntPtr.Zero)
                {
                    var next = Marshal.ReadIntPtr(reference, LibMtp.FileNextOffset);
                    LibMtp.LIBMTP_destroy_file_t(reference);
                    reference = next;
                }
            }
        });
    }

    private static IReadOnlyList<MtpEntry> ParseChildren(IntPtr list, string relativeParentPath)
    {
        var entries = new List<MtpEntry>();
        var reference = list;
        while (reference != IntPtr.Zero)
        {
            var next = Marshal.ReadIntPtr(reference, LibMtp.FileNextOffset);
            var file = Marshal.PtrToStructure<LibMtp.File>(reference);
            var name = LibMtp.ToString(file.FileName);
            if (name.Length > 0)
            {
                entries.Add(new MtpEntry
                {
                    ItemId = file.ItemId,
                    ParentId = file.ParentId,
                    StorageId = file.StorageId,
                    Name = name,
                    RelativePath = Normalize($"{relativeParentPath}/{name}"),
                    IsFolder = file.FileType == LibMtp.FileTypeFolder,
                    Size = (long)file.FileSize,
                    ModifiedAt = file.ModificationDate > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(file.ModificationDate)
                        : null
                });
            }
            reference = next;
        }
        return entries;
    }

    private void Invalidate(uint storageId, uint parentId) => _children.Invalidate((storageId, parentId));

    private sealed class UploadReplacementOperations : IMtpFileReplacementOperations
    {
        private readonly MtpDeviceSession _session;
        private readonly MtpEntry _directory;

        public UploadReplacementOperations(MtpDeviceSession session, MtpEntry directory)
        {
            _session = session;
            _directory = directory;
        }

        public MtpEntry? Find(string fileName) => _session.FindChild(_directory, fileName);

        public bool Rename(MtpEntry entry, string newFileName)
        {
            _session.ClearErrors();
            var result = LibMtp.LIBMTP_Set_Object_Filename(_session._device, entry.ItemId, newFileName);
            _session.Invalidate(entry.StorageId, entry.ParentId);
            // The directory entry that FindChild uses must be refreshed even
            // if the object did not report its parent id consistently.
            _session.Invalidate(_directory.StorageId, _directory.ItemId);
            return result == 0;
        }

        public void Delete(MtpEntry entry) => _session.Delete(entry);

        public MtpEntry Upload(
            string sourcePath,
            string fileName,
            Action<long, long>? progress,
            CancellationToken cancellationToken) =>
            _session.UploadNew(_directory, sourcePath, fileName, progress, cancellationToken);

        public string? CreateRecoveryCopy(MtpEntry entry)
        {
            var recoveryDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "mtp-recovery");
            var path = Path.Combine(recoveryDirectory, $"{Guid.NewGuid():N}.recovery");
            try
            {
                Directory.CreateDirectory(recoveryDirectory);
                _session.Download(entry, path);
                // Some firmware reports 0 until the object is re-read; only a
                // known, mismatching size means the recovery copy is not safe.
                if (entry.Size > 0 && new FileInfo(path).Length != entry.Size)
                {
                    TryDeleteFile(path);
                    return null;
                }
                return path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDeleteFile(path);
                return null;
            }
        }

        public void DeleteRecoveryCopy(string path) => TryDeleteFile(path);

        public void Log(string message) => Debug.WriteLine(message);

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Throw(string message)
    {
        var errors = LibMtp.ReadErrorStack(_device);
        ClearErrors();
        var detail = errors.Count == 0 ? string.Empty : $" ({string.Join("; ", errors)})";
        throw new IOException(message + detail);
    }

    // libmtp appends to a per-device error stack and never clears it; without
    // this, a later failure would also report every earlier, unrelated error.
    private void ClearErrors() => LibMtp.LIBMTP_Clear_Errorstack(_device);

    private static string[] Split(string? relativePath) =>
        (relativePath ?? string.Empty)
        .Replace('\\', '/')
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string relativePath) =>
        string.Join('/', Split(relativePath));
}
