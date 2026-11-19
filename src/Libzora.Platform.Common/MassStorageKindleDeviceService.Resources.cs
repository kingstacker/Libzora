using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>
/// Device resources and clippings.
/// </summary>
public sealed partial class MassStorageKindleDeviceService : IKindleDeviceService
{
    public async Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(
        KindleDevice device,
        KindleResourceKind kind,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!device.Profile.SupportsResource(kind)) return [];
        var root = GetResourceRoot(device, kind);
        if (!Directory.Exists(root)) return [];
        var resources = new List<KindleDeviceResource>();
        foreach (var path in EnumerateFiles(root))
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
        EnsureMassStorage(device);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("待发送的设备资源不存在。", sourcePath);
        if (!device.Profile.SupportsResourceFile(kind, sourcePath))
            throw new InvalidDataException(UiText.Get("当前设备不支持此资源格式。"));
        var root = GetResourceRoot(device, kind);
        Directory.CreateDirectory(root);
        var destination = GetUniqueDestination(root, SafeFileName(Path.GetFileName(sourcePath)));
        var temporary = $"{destination}.{Guid.NewGuid():N}.libzora-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            var sourceHash = await CopyAndHashAsync(sourcePath, temporary, total, progress, cancellationToken);
            var targetHash = await HashFileAsync(temporary, cancellationToken);
            if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备资源未写入。");
            await MoveFileAsync(temporary, destination, overwrite: true, cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task ExportResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var source = ResolveWithinRoot(device.RootPath, resource.RelativePath, GetResourceRoot(device, resource.Kind));
        RejectLink(source);
        if (!File.Exists(source)) throw new FileNotFoundException("设备资源不存在。", resource.RelativePath);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("导出路径无效。"));
        // Write to a sibling temporary file and publish only after the whole
        // copy succeeded. Cancelling or hitting a read error must leave an
        // existing destination untouched instead of truncating it.
        var temporary = $"{destination}.{Guid.NewGuid():N}.libzora-part";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task RemoveResourceAsync(
        KindleDevice device,
        KindleDeviceResource resource,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!device.Profile.TryGetResourcePath(resource.Kind, resource.RelativePath, out _))
            throw new InvalidOperationException("设备资源路径无效。");
        var path = ResolveWithinRoot(device.RootPath, resource.RelativePath, GetResourceRoot(device, resource.Kind));
        RejectLink(path);
        if (File.Exists(path)) await DeleteFileAsync(path, cancellationToken);
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue)
    {
        EnsureMassStorage(device);
        if (!device.Profile.SupportsClippings)
            return device.Profile.SupportsKoboNotes
                ? await KoboNotesReader.ReadAsync(Path.Combine(device.RootPath, ".kobo", "KoboReader.sqlite"), maxItems, cancellationToken)
                : [];

        try
        {
            var databaseNotes = await ReadAnnotationDatabasesAsync(device, cancellationToken, maxItems);
            if (databaseNotes.Count > 0) return databaseNotes;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException)
        {
            // Older firmware and partially copied snapshots must fall back to
            // My Clippings.txt without preventing the device from being used.
        }

        var path = GetClippingsPath(device);
        if (!File.Exists(path)) return [];
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        return await KindleClippingsParser.ParseAsync(reader, maxItems, cancellationToken);
    }

    private static async Task<IReadOnlyList<KindleClipping>> ReadAnnotationDatabasesAsync(
        KindleDevice device,
        CancellationToken cancellationToken,
        int maxItems)
    {
        if (maxItems <= 0) return [];
        var databases = KindleAnnotationDeviceFiles.FindMountedDatabases(device.RootPath, cancellationToken);
        if (databases.Count == 0) return [];
        var documents = GetDocumentsRoot(device);
        var titleResolver = KindleAnnotationBookTitleResolver.Create(
            KindleAnnotationDeviceFiles.FindSdrFolderNames(documents, cancellationToken));
        var result = new List<KindleClipping>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = maxItems - result.Count;
            if (remaining <= 0) break;
            var notes = await KindleAnnotationDatabaseReader.ReadClippingsAsync(
                database,
                titleResolver.Resolve,
                remaining,
                cancellationToken);
            foreach (var note in notes)
                if (ids.Add(note.Id)) result.Add(note);
        }
        return result;
    }
}
