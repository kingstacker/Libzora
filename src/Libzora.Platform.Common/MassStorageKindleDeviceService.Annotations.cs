using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>
/// Annotation snapshots and mutations.
/// </summary>
public sealed partial class MassStorageKindleDeviceService : IKindleDeviceService
{
    public async Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!device.Profile.SupportsClippings) return null;
        var databases = KindleAnnotationDeviceFiles.FindMountedDatabases(device.RootPath, cancellationToken);
        if (databases.Count == 0) return null;
        if (databases.Count > 1)
            throw new NotSupportedException(UiText.Get("Kindle 上存在多个账户批注数据库，无法确认当前账户，已停止同步。笔记仍可查看和导出。"));
        var database = databases[0];
        KindleAnnotationMountedWriter.ValidateSnapshotSource(database);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "annotation-snapshot", Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stagingDirectory);
        else Directory.CreateDirectory(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // Read records and their hash from the same immutable copy. Reading
            // rows from the live file and hashing it later can mix two versions.
            var snapshotPath = Path.Combine(stagingDirectory, "ksdk_annotation_v1.db");
            File.Copy(database, snapshotPath);
            KindleAnnotationMountedWriter.ValidateSnapshotSource(snapshotPath);
            var records = await KindleAnnotationDatabaseReader.ReadRecordsAsync(
                snapshotPath,
                maxItems: 100_000,
                cancellationToken,
                requireCompleteSnapshot: true);
            var titleResolver = KindleAnnotationBookTitleResolver.Create(
                KindleAnnotationDeviceFiles.FindSdrFolderNames(
                    GetDocumentsRoot(device),
                    cancellationToken));
            var snapshotRecords = records.Select(record => new KindleAnnotationRecord(
                record.Id,
                new KindleAnnotationBookIdentity(
                    record.Book.BookId,
                    record.Book.Asin,
                    record.Book.Guid),
                record.Dataset,
                record.Type,
                record.Start,
                record.End,
                record.CreatedAt?.ToUnixTimeMilliseconds() ?? 0,
                record.UpdatedAt?.ToUnixTimeMilliseconds() ?? 0,
                record.Note,
                record.SerializedPayload,
                titleResolver.Resolve(record.Book) ?? string.Empty)).ToArray();
            await using var stream = File.OpenRead(snapshotPath);
            var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return new KindleAnnotationSnapshot(
                Path.GetRelativePath(device.RootPath, database).Replace('\\', '/'),
                sha256,
                snapshotRecords);
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken = default) =>
        ApplyAnnotationMutationsAsync(device, snapshot, mutations, cancellationToken, null);

    public async Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device, KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations, CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress)
    {
        progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
        EnsureMassStorage(device);
        if (!device.Profile.SupportsClippings)
            throw new NotSupportedException(UiText.Get("当前设备暂不支持回写 Kindle 批注。"));
        if (mutations.Count == 0)
            return new KindleAnnotationWriteResult(0, snapshot.Sha256, snapshot.Sha256, null, null);

        var source = Path.GetFullPath(
            Path.Combine(device.RootPath, snapshot.DatabaseRelativePath));
        var annotationRoot = Path.GetFullPath(
            Path.Combine(device.RootPath, "system", "ksdk", ".annotations"));
        if (!source.StartsWith(
                annotationRoot.TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(source))
            throw new IOException("设备批注数据库路径已经变化。");

        var deviceKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(device.Identity))).ToLowerInvariant()[..16];
        var backupDirectory = Path.Combine(
            _paths.Backups,
            "KindleAnnotations",
            deviceKey,
            DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        var localBackup = Path.Combine(backupDirectory, "ksdk_annotation_v1.db");
        var working = Path.Combine(backupDirectory, "working.db");
        var candidate = Path.Combine(backupDirectory, "candidate.db");
        try
        {
            File.Copy(source, localBackup, overwrite: false);
            File.Copy(source, working, overwrite: false);
            await using (var stream = File.OpenRead(working))
            {
                var currentHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!currentHash.Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Kindle 批注数据库在检测后已变化，请重新检测。");
            }

            var newHash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(
                working,
                candidate,
                mutations,
                cancellationToken, progress);
            var installed = await KindleAnnotationMountedWriter.ApplyAsync(
                source, snapshot.Sha256, localBackup, mutations, cancellationToken, progress);
            return new KindleAnnotationWriteResult(
                mutations.Count,
                snapshot.Sha256,
                installed.Hash,
                localBackup,
                installed.DeviceBackup);
        }
        finally
        {
            foreach (var path in new[] { working, candidate })
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public Task DeleteClippingAsync(
        KindleDevice device,
        string clippingId,
        CancellationToken cancellationToken = default)
        => DeleteClippingsAsync(device, [clippingId], cancellationToken);

    public async Task DeleteClippingsAsync(
        KindleDevice device,
        IReadOnlyCollection<string> clippingIds,
        CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        if (!device.Profile.CanDeleteNotes)
            throw new NotSupportedException(UiText.Get("当前设备的笔记仅支持读取和导出。"));
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        if (clippingIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("设备笔记标识无效。", nameof(clippingIds));
        if (clippingIds.Any(id => id.StartsWith("kindle-db:", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException(UiText.Get("Kindle 账户数据库中的批注当前仅支持读取和导出。"));
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        var path = GetClippingsPath(device);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。", path);
        string currentText;
        using (var reader = new StreamReader(path, Encoding.UTF8, true))
            currentText = await reader.ReadToEndAsync(cancellationToken);
        var clippings = KindleClippingsParser.Parse(currentText);
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个设备划线笔记不存在。");
        var updated = KindleClippingsParser.BuildDocument(clippings.Where(item => !ids.Contains(item.Id)));
        var temporary = path + ".libzora-part";
        var backup = path + ".libzora-backup";
        try
        {
            File.Copy(path, backup, true);
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(true), cancellationToken);
            File.Move(temporary, path, true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup)) File.Copy(backup, path, true);
            throw;
        }
        finally
        {
            TryDelete(temporary);
            TryDelete(backup);
        }
    }

    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        EnsureMassStorage(device);
        cancellationToken.ThrowIfCancellationRequested();
        return _eject(device, cancellationToken);
    }
}
