using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Clippings and annotation snapshot mutation.
/// </summary>
public sealed partial class KindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue)
    {
        if (!device.Profile.SupportsClippings)
        {
            if (!device.Profile.SupportsKoboNotes) return [];
            if (device.Transport == KindleTransport.MassStorage)
                return await KoboNotesReader.ReadAsync(Path.Combine(device.RootPath, ".kobo", "KoboReader.sqlite"), maxItems, cancellationToken);
            return [];
        }

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
            or JsonException
            or TimeoutException)
        {
            // Keep the legacy My Clippings.txt path available on firmware that
            // does not expose a readable account database.
        }

        string text;
        if (device.Transport == KindleTransport.Wpd)
            text = await Task.Run(() => WpdKindleAccess.ReadClippingsText(device, cancellationToken), cancellationToken);
        else
        {
            var path = GetClippingsPath(device);
            if (!File.Exists(path)) return [];
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            if (maxItems != int.MaxValue)
                return await KindleClippingsParser.ParseAsync(reader, maxItems, cancellationToken);
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        return KindleClippingsParser.Parse(text, maxItems);
    }

    private static async Task<IReadOnlyList<KindleClipping>> ReadAnnotationDatabasesAsync(
        KindleDevice device,
        CancellationToken cancellationToken,
        int maxItems)
    {
        if (maxItems <= 0) return [];
        IReadOnlyList<string> databases;
        IReadOnlyList<string> sdrFolders;
        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            "Libzora",
            "annotation-database-read",
            Guid.NewGuid().ToString("N"));
        try
        {
            if (device.Transport == KindleTransport.Wpd)
            {
                (databases, sdrFolders) = await Task.Run(
                    () => WpdKindleAccess.FindAnnotationMetadata(device, cancellationToken),
                    cancellationToken);
            }
            else
            {
                databases = KindleAnnotationDeviceFiles.FindMountedDatabases(device.RootPath, cancellationToken);
                sdrFolders = KindleAnnotationDeviceFiles.FindSdrFolderNames(
                    GetDocumentsRoot(device),
                    cancellationToken);
            }
            if (databases.Count == 0) return [];

            var titleResolver = KindleAnnotationBookTitleResolver.Create(sdrFolders);
            var result = new List<KindleClipping>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < databases.Count && result.Count < maxItems; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string localPath;
                if (device.Transport == KindleTransport.Wpd)
                {
                    Directory.CreateDirectory(stagingDirectory);
                    localPath = await Task.Run(
                        () => WpdKindleAccess.CopyStorageFileToLocal(
                            device,
                            databases[index],
                            stagingDirectory,
                            cancellationToken),
                        cancellationToken);
                }
                else
                {
                    localPath = databases[index];
                }

                var notes = await KindleAnnotationDatabaseReader.ReadClippingsAsync(
                    localPath,
                    titleResolver.Resolve,
                    maxItems - result.Count,
                    cancellationToken);
                foreach (var note in notes)
                    if (ids.Add(note.Id)) result.Add(note);
            }
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsClippings) return null;
        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            "Libzora",
            "annotation-snapshot",
            Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> databases;
            IReadOnlyList<string> sdrFolders;
            if (device.Transport == KindleTransport.Wpd)
            {
                (databases, sdrFolders) = await Task.Run(
                    () => WpdKindleAccess.FindAnnotationMetadata(device, cancellationToken),
                    cancellationToken);
            }
            else
            {
                databases = KindleAnnotationDeviceFiles.FindMountedDatabases(device.RootPath, cancellationToken);
                sdrFolders = KindleAnnotationDeviceFiles.FindSdrFolderNames(
                    GetDocumentsRoot(device),
                    cancellationToken);
            }
            if (databases.Count == 0) return null;

            Directory.CreateDirectory(stagingDirectory);
            var databaseRelativePath = databases[0];
            var localPath = device.Transport == KindleTransport.Wpd
                ? await Task.Run(
                    () => WpdKindleAccess.CopyStorageFileToLocal(
                        device,
                        databaseRelativePath,
                        stagingDirectory,
                        cancellationToken),
                    cancellationToken)
                : databaseRelativePath;
            var records = await KindleAnnotationDatabaseReader.ReadRecordsAsync(
                localPath,
                maxItems: 100_000,
                cancellationToken);
            var titleResolver = KindleAnnotationBookTitleResolver.Create(sdrFolders);
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
            await using var stream = File.OpenRead(localPath);
            var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return new KindleAnnotationSnapshot(
                databaseRelativePath.Replace('\\', '/'),
                sha256,
                snapshotRecords);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
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
        if (!device.Profile.SupportsClippings)
            throw new NotSupportedException(UiText.Get("当前设备暂不支持回写 Kindle 批注。"));
        if (_paths is null)
            throw new NotSupportedException(UiText.Get("当前运行环境没有可用的 Kindle 批注备份目录。"));
        if (mutations.Count == 0)
            return new KindleAnnotationWriteResult(0, snapshot.Sha256, snapshot.Sha256, null, null);

        var deviceKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(device.Identity))).ToLowerInvariant()[..16];
        var backupDirectory = Path.Combine(
            _paths.Backups,
            "KindleAnnotations",
            deviceKey,
            DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        var localBackupPath = Path.Combine(backupDirectory, "ksdk_annotation_v1.db");
        var workingDirectory = Path.Combine(backupDirectory, "working");
        Directory.CreateDirectory(workingDirectory);
        var workingPath = Path.Combine(workingDirectory, "ksdk_annotation_v1.db");
        var candidatePath = Path.Combine(backupDirectory, "ksdk_annotation_v1.candidate.db");
        try
        {
            if (device.Transport == KindleTransport.Wpd)
            {
                await Task.Run(() =>
                {
                    using var session = new WpdKindleAccess.WpdSession(device);
                    var copied = WpdKindleAccess.CopyStorageFileToLocal(
                        session,
                        device,
                        snapshot.DatabaseRelativePath,
                        workingDirectory,
                        cancellationToken);
                    if (!string.Equals(copied, workingPath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("设备批注数据库快照文件名异常。");
                }, cancellationToken);
            }
            else
            {
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
                File.Copy(source, workingPath, overwrite: false);
                File.Copy(source, localBackupPath, overwrite: false);
            }

            await using (var stream = File.OpenRead(workingPath))
            {
                var currentHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!currentHash.Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Kindle 批注数据库在检测后已变化，请重新检测。");
            }
            if (device.Transport == KindleTransport.Wpd)
                File.Copy(workingPath, localBackupPath, overwrite: false);
            var newHash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(
                workingPath,
                candidatePath,
                mutations,
                cancellationToken, progress);

            if (device.Transport == KindleTransport.Wpd)
            {
                var deviceBackup = await Task.Run(
                    () => WpdKindleAccess.ReplaceAnnotationDatabase(
                        device,
                        snapshot.DatabaseRelativePath,
                        snapshot.Sha256,
                        candidatePath,
                        localBackupPath,
                        cancellationToken, progress),
                    cancellationToken);
                KindleAnnotationBackupStore.SaveManifest(
                    localBackupPath,
                    device,
                    snapshot.DatabaseRelativePath,
                    snapshot.Sha256,
                    deviceBackup,
                    reason: "sync");
                return new KindleAnnotationWriteResult(
                    mutations.Count,
                    snapshot.Sha256,
                    newHash,
                    localBackupPath,
                    deviceBackup);
            }

            var currentPath = Path.GetFullPath(
                Path.Combine(device.RootPath, snapshot.DatabaseRelativePath));
            var installed = await KindleAnnotationMountedWriter.ApplyAsync(
                currentPath, snapshot.Sha256, localBackupPath, mutations, cancellationToken, progress);
            KindleAnnotationBackupStore.SaveManifest(
                localBackupPath,
                device,
                snapshot.DatabaseRelativePath,
                snapshot.Sha256,
                installed.DeviceBackup,
                reason: "sync");
            return new KindleAnnotationWriteResult(mutations.Count, snapshot.Sha256,
                installed.Hash, localBackupPath, installed.DeviceBackup);
        }
        finally
        {
            foreach (var path in new[] { workingPath, candidatePath })
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<KindleAnnotationRestoreResult> RestoreAnnotationDatabaseAsync(
        KindleDevice device,
        KindleAnnotationBackupEntry backup,
        CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(backup);
        progress?.Report(new(KindleAnnotationSyncStage.PreparingWrite));
        if (!device.Profile.SupportsClippings)
            throw new NotSupportedException(UiText.Get("当前设备暂不支持回退 Kindle 批注备份。"));

        var backupHash = await KindleAnnotationBackupStore.ValidateBackupFileAsync(
            backup.LocalBackupPath, backup.Sha256, cancellationToken);

        var databaseRelativePath = backup.DatabaseRelativePath;
        if (string.IsNullOrWhiteSpace(databaseRelativePath))
        {
            if (device.Transport == KindleTransport.Wpd)
            {
                var (databases, _) = await Task.Run(
                    () => WpdKindleAccess.FindAnnotationMetadata(device, cancellationToken),
                    cancellationToken);
                if (databases.Count != 1)
                    throw new NotSupportedException(UiText.Get("无法唯一确认目标 Kindle 批注数据库路径，已取消回退。"));
                databaseRelativePath = databases[0].Replace('\\', '/');
            }
            else
            {
                var databases = KindleAnnotationDeviceFiles.FindMountedDatabases(device.RootPath, cancellationToken);
                if (databases.Count != 1)
                    throw new NotSupportedException(UiText.Get("无法唯一确认目标 Kindle 批注数据库路径，已取消回退。"));
                databaseRelativePath = Path.GetRelativePath(device.RootPath, databases[0]).Replace('\\', '/');
            }
        }

        var deviceKey = KindleAnnotationBackupStore.ComputeDeviceKey(device);
        var backupDirectory = Path.Combine(
            _paths.Backups,
            "KindleAnnotations",
            deviceKey,
            DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        var preRestoreLocalBackup = Path.Combine(backupDirectory, "ksdk_annotation_v1.db");

        if (device.Transport == KindleTransport.Wpd)
        {
            progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
            await Task.Run(() =>
            {
                using var session = new WpdKindleAccess.WpdSession(device);
                var copied = WpdKindleAccess.CopyStorageFileToLocal(
                    session, device, databaseRelativePath, backupDirectory, cancellationToken);
                if (!string.Equals(copied, preRestoreLocalBackup, StringComparison.OrdinalIgnoreCase))
                    File.Move(copied, preRestoreLocalBackup, overwrite: true);
            }, cancellationToken);

            string previousHash;
            await using (var stream = File.OpenRead(preRestoreLocalBackup))
            {
                previousHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }

            var preRestoreDeviceBackup = await Task.Run(
                () => WpdKindleAccess.ReplaceAnnotationDatabase(
                    device,
                    databaseRelativePath,
                    previousHash,
                    backup.LocalBackupPath,
                    preRestoreLocalBackup,
                    cancellationToken,
                    progress),
                cancellationToken);
            KindleAnnotationBackupStore.SaveManifest(
                preRestoreLocalBackup,
                device,
                databaseRelativePath,
                previousHash,
                preRestoreDeviceBackup,
                reason: "pre-restore");
            progress?.Report(new(KindleAnnotationSyncStage.Completed, 1, 1));
            return new KindleAnnotationRestoreResult(
                backup.LocalBackupPath,
                backupHash,
                previousHash,
                preRestoreLocalBackup,
                preRestoreDeviceBackup);
        }

        var target = Path.GetFullPath(Path.Combine(device.RootPath, databaseRelativePath));
        var annotationRoot = Path.GetFullPath(Path.Combine(device.RootPath, "system", "ksdk", ".annotations"));
        if (!target.StartsWith(annotationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target))
            throw new IOException("设备批注数据库路径不存在或已变化。");
        KindleAnnotationMountedWriter.ValidateSnapshotSource(target);

        progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
        File.Copy(target, preRestoreLocalBackup, overwrite: false);
        string mountedPreviousHash;
        await using (var stream = File.OpenRead(preRestoreLocalBackup))
        {
            mountedPreviousHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var mountedDeviceBackup = target + ".libzora-backup-" + stamp;
        var staged = target + ".libzora-restore-new-" + stamp;
        try
        {
            File.Copy(preRestoreLocalBackup, mountedDeviceBackup, overwrite: false);
            KindleAnnotationBackupStore.SaveManifest(
                preRestoreLocalBackup,
                device,
                databaseRelativePath,
                mountedPreviousHash,
                mountedDeviceBackup,
                reason: "pre-restore");

            progress?.Report(new(KindleAnnotationSyncStage.WritingToDevice));
            File.Copy(backup.LocalBackupPath, staged, overwrite: false);
            File.Move(staged, target, overwrite: true);

            progress?.Report(new(KindleAnnotationSyncStage.Verifying));
            await using (var readback = File.OpenRead(target))
            {
                var installedHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(readback, cancellationToken)).ToLowerInvariant();
                if (!installedHash.Equals(backupHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("回退后读回校验不一致。");
            }
            progress?.Report(new(KindleAnnotationSyncStage.Completed, 1, 1));
            return new KindleAnnotationRestoreResult(
                backup.LocalBackupPath,
                backupHash,
                mountedPreviousHash,
                preRestoreLocalBackup,
                mountedDeviceBackup);
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
        if (!device.Profile.CanDeleteNotes)
            throw new NotSupportedException(UiText.Get("当前设备的笔记仅支持读取和导出。"));
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        if (clippingIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("设备笔记标识无效。", nameof(clippingIds));
        if (clippingIds.Any(id => id.StartsWith("kindle-db:", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException(UiText.Get("Kindle 账户数据库中的批注当前仅支持读取和导出。"));
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
        {
            var current = await Task.Run(() => WpdKindleAccess.ReadClippingsText(device, cancellationToken), cancellationToken);
            var clippings = KindleClippingsParser.Parse(current);
            EnsureClippingsExist(clippings, ids);
            var updated = KindleClippingsParser.BuildDocument(clippings.Where(item => !ids.Contains(item.Id)));
            await Task.Run(() => WpdKindleAccess.ReplaceClippingsText(device, updated, cancellationToken), cancellationToken);
            return;
        }

        var path = GetClippingsPath(device);
        if (!File.Exists(path)) throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。", path);
        string currentText;
        using (var reader = new StreamReader(path, Encoding.UTF8, true))
            currentText = await reader.ReadToEndAsync(cancellationToken);
        var currentClippings = KindleClippingsParser.Parse(currentText);
        EnsureClippingsExist(currentClippings, ids);
        var updatedText = KindleClippingsParser.BuildDocument(currentClippings.Where(item => !ids.Contains(item.Id)));
        var temporary = path + ".libzora-part";
        var backup = path + ".libzora-backup";
        try
        {
            File.Copy(path, backup, true);
            await File.WriteAllTextAsync(temporary, updatedText, new UTF8Encoding(true), cancellationToken);
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
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    private static void EnsureClippingsExist(IReadOnlyList<KindleClipping> clippings, HashSet<string> ids)
    {
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个设备划线笔记不存在。");
    }
}
