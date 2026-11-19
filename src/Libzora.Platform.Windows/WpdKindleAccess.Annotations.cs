using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Clippings and annotation database replacement.
/// </summary>
internal static partial class WpdKindleAccess
{
    public static (
        IReadOnlyList<string> Databases,
        IReadOnlyList<string> SdrFolders) FindAnnotationMetadata(
        KindleDevice device,
        CancellationToken cancellationToken)
    {
        const string databaseName = "ksdk_annotation_v1.db";
        var databases = new List<string>();
        var sdrFolders = new List<string>();
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? annotationRoot = null;
        dynamic? documents = null;
        try
        {
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            annotationRoot = FindItemByRelativePath(storage, @"system\ksdk\.annotations");
            if (annotationRoot is not null && (bool)annotationRoot.IsFolder)
            {
                var folders = new Stack<(object Folder, string RelativePath, int Depth)>();
                folders.Push((annotationRoot.GetFolder, "system\\ksdk\\.annotations", 0));
                while (folders.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = folders.Pop();
                    dynamic? folder = null;
                    dynamic? children = null;
                    try
                    {
                        folder = entry.Folder;
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
                                    if (entry.Depth < 4)
                                        folders.Push((child.GetFolder, relativePath, entry.Depth + 1));
                                    continue;
                                }
                                if (name.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
                                    databases.Add(relativePath);
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

            documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory);
            if (documents is not null && (bool)documents.IsFolder)
            {
                dynamic? children = null;
                try
                {
                    children = documents.GetFolder.Items();
                    for (var index = 0; index < (int)children.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        dynamic? child = null;
                        try
                        {
                            child = children.Item(index);
                            if (!(bool)child.IsFolder) continue;
                            var name = GetItemFileName(child);
                            if (name.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase))
                                sdrFolders.Add(name);
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
                }
            }
        }
        catch (COMException exception)
        {
            throw new IOException("无法读取 Kindle 账户批注数据库。", exception);
        }
        finally
        {
            Release(documents);
            Release(annotationRoot);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }

        var accountDatabases = databases
            .Where(path => path.Split('\\', '/').Any(segment =>
                segment.StartsWith("amzn1.account.", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return (
            accountDatabases.Length > 0 ? accountDatabases : databases.ToArray(),
            sdrFolders);
    }

    private static readonly SemaphoreSlim AnnotationWriteGate = new(1, 1);

    public static string ReplaceAnnotationDatabase(
        KindleDevice device, string relativePath, string expectedOriginalSha256,
        string candidatePath, string localBackupPath, CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        AnnotationWriteGate.Wait(cancellationToken);
        try
        {
            return ReplaceAnnotationDatabaseCore(device, relativePath, expectedOriginalSha256,
                candidatePath, localBackupPath, cancellationToken, progress);
        }
        finally { AnnotationWriteGate.Release(); }
    }

    private static string ReplaceAnnotationDatabaseCore(
        KindleDevice device,
        string relativePath,
        string expectedOriginalSha256,
        string candidatePath,
        string localBackupPath,
        CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.Replace('/', '\\').StartsWith(
                @"system\ksdk\.annotations\",
                StringComparison.OrdinalIgnoreCase)
            || relativePath.Split(['/', '\\']).Any(segment => segment is ".." or ".")
            || !Path.GetFileName(relativePath.Replace('/', '\\')).Equals(
                "ksdk_annotation_v1.db",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kindle 批注数据库路径无效。");
        if (!File.Exists(candidatePath))
            throw new FileNotFoundException("Kindle 批注候选文件不存在。", candidatePath);

        static string Hash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var fileName = Path.GetFileName(relativePath.Replace('/', '\\'));
        var stageName = fileName + ".libzora-sync-new-" + timestamp;
        var backupName = fileName + ".libzora-backup-" + timestamp;
        var readbackDirectory = Path.GetDirectoryName(Path.GetFullPath(localBackupPath))!;
        var currentReadback = localBackupPath + ".current-readback-" + timestamp;
        var candidateHash = Hash(candidatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localBackupPath))!);

        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? parent = null;
        var targetDeleteAttempted = false;
        var installationVerified = false;
        IProgress<TransferProgress>? Transfer(KindleAnnotationSyncStage stage) =>
            progress is null ? null : new KindleAnnotationTransferProgress(progress, stage);
        try
        {
            progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
            shell = CreateShell();
            kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            var normalized = relativePath.Replace('/', '\\').Trim('\\');
            var parentPath = normalized[..normalized.LastIndexOf('\\')];
            parent = FindItemByRelativePath(storage, parentPath)
                ?? throw new IOException("设备批注数据库目录不存在。");
            if (!(bool)parent.IsFolder)
                throw new IOException("设备批注数据库父路径不是文件夹。");

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                if (ChildExists(parent, fileName + suffix))
                    throw new IOException("设备批注数据库仍带 SQLite 侧文件，暂不能安全替换。");
            }
            if (ChildExists(parent, stageName))
                throw new IOException("设备上存在未清理的批注同步暂存文件，请先检查设备。");

            dynamic current = FindChild(parent, fileName)
                ?? throw new FileNotFoundException("设备批注数据库不存在。", relativePath);
            var currentId = GetWpdObjectId(current);
            try
            {
                WpdNativeTransfer.CopyFileToLocal(
                    device.RootPath,
                    currentId,
                    currentReadback,
                    ReadInt64Property(current, "System.Size"),
                    cancellationToken);
            }
            finally
            {
                Release(current);
            }
            if (!Hash(currentReadback).Equals(
                    expectedOriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("设备批注数据库在同步准备期间已变化，请重新检测。");
            if (!File.Exists(localBackupPath))
                File.Copy(currentReadback, localBackupPath, overwrite: false);
            if (!Hash(localBackupPath).Equals(expectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("本地 Kindle 批注原始备份哈希不一致。");

            var parentId = GetWpdObjectId(parent);
            WpdNativeTransfer.SendFile(
                device.RootPath,
                parentId,
                candidatePath,
                stageName,
                Transfer(KindleAnnotationSyncStage.PreparingWrite),
                cancellationToken);
            WaitForAnnotationStorageItem(
                device,
                $"{parentPath}\\{stageName}",
                new FileInfo(candidatePath).Length,
                cancellationToken);
            progress?.Report(new(KindleAnnotationSyncStage.Verifying));
            CopyAnnotationStorageFileAndVerify(
                device,
                $"{parentPath}\\{stageName}",
                readbackDirectory,
                candidateHash,
                cancellationToken);

            WpdNativeTransfer.SendFile(
                device.RootPath,
                parentId,
                localBackupPath,
                backupName,
                Transfer(KindleAnnotationSyncStage.BackingUp),
                cancellationToken);
            WaitForAnnotationStorageItem(
                device,
                $"{parentPath}\\{backupName}",
                new FileInfo(localBackupPath).Length,
                cancellationToken);
            CopyAnnotationStorageFileAndVerify(
                device,
                $"{parentPath}\\{backupName}",
                readbackDirectory,
                expectedOriginalSha256,
                cancellationToken);

            dynamic beforeDelete = FindChild(parent, fileName)
                ?? throw new IOException("设备原批注数据库已消失。");
            var beforeDeleteId = GetWpdObjectId(beforeDelete);
            try
            {
                File.Delete(currentReadback);
                WpdNativeTransfer.CopyFileToLocal(
                    device.RootPath,
                    beforeDeleteId,
                    currentReadback,
                    ReadInt64Property(beforeDelete, "System.Size"),
                    cancellationToken);
            }
            finally
            {
                Release(beforeDelete);
            }
            if (!Hash(currentReadback).Equals(
                    expectedOriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("设备批注数据库在同步期间发生变化，已取消安装。");
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (ChildExists(parent, fileName + suffix))
                    throw new IOException("设备批注数据库在同步期间出现 SQLite 侧文件，已取消安装。");

            targetDeleteAttempted = true;
            progress?.Report(new(KindleAnnotationSyncStage.WritingToDevice));
            WpdNativeTransfer.DeleteObject(device.RootPath, beforeDeleteId, cancellationToken);
            for (var attempt = 0; attempt < 100 && ChildExists(parent, fileName); attempt++)
                Thread.Sleep(100);
            if (ChildExists(parent, fileName))
                throw new IOException("设备未释放旧批注数据库，无法安装新快照。");

            WpdNativeTransfer.SendFile(
                device.RootPath,
                parentId,
                candidatePath,
                fileName,
                Transfer(KindleAnnotationSyncStage.WritingToDevice),
                cancellationToken);
            WaitForAnnotationStorageItem(
                device,
                normalized,
                new FileInfo(candidatePath).Length,
                cancellationToken);
            progress?.Report(new(KindleAnnotationSyncStage.Verifying));
            CopyAnnotationStorageFileAndVerify(
                device,
                normalized,
                readbackDirectory,
                candidateHash,
                cancellationToken);

            installationVerified = true;

            dynamic? staged = FindChild(parent, stageName);
            if (staged is not null)
            {
                try
                {
                    // Cleanup failure cannot roll back a verified installation.
                    try { WpdNativeTransfer.DeleteObject(device.RootPath,
                        GetWpdObjectId(staged), CancellationToken.None); }
                    catch (Exception exception) when (exception is IOException or COMException) { }
                }
                finally
                {
                    Release(staged);
                }
            }
            return $"{parentPath}\\{backupName}";
        }
        catch (Exception failure)
        {
            Exception? restoreFailure = null;
            if (targetDeleteAttempted && !installationVerified
                && File.Exists(localBackupPath)
                && parent is not null
                && Hash(localBackupPath).Equals(
                    expectedOriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    RestoreOriginalAnnotationDatabase(
                        device,
                        parent,
                        relativePath.Replace('/', '\\').Trim('\\'),
                        localBackupPath,
                        expectedOriginalSha256,
                        candidateHash,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    restoreFailure = exception;
                }
            }
            if (restoreFailure is not null)
            {
                throw new AggregateException(
                    $"Kindle 批注同步失败，且自动恢复失败。原始备份：{localBackupPath}",
                    failure,
                    restoreFailure);
            }
            if (failure is COMException com)
                throw new IOException("Kindle 批注数据库替换失败，已尝试恢复原文件。", com);
            throw;
        }
        finally
        {
            if (!targetDeleteAttempted && parent is not null)
            {
                // Only the unique staging file owned by this attempt is removed.
                dynamic? staged = null;
                try
                {
                    staged = FindChild(parent, stageName);
                    if (staged is not null)
                        WpdNativeTransfer.DeleteObject(device.RootPath, GetWpdObjectId(staged), CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or COMException) { }
                finally { Release(staged); }
            }
            try { File.Delete(currentReadback); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Release(parent);
            Release(storage);
            Release(kindle);
            Release(shell);
            FlushReleasedComObjects();
        }
    }

    private static bool ChildExists(dynamic parent, string name)
    {
        dynamic? child = FindChild(parent, name);
        try { return child is not null; }
        finally { Release(child); }
    }

    private static void WaitForAnnotationStorageItem(
        KindleDevice device,
        string relativePath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadStorageItemState(device, relativePath);
            if (state.Exists && state.Size == expectedSize) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException("等待设备批注数据库写入完成超时。");
    }

    private static void CopyAnnotationStorageFileAndVerify(
        KindleDevice device,
        string relativePath,
        string destinationDirectory,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var session = new WpdSession(device);
        var path = CopyStorageFileToLocal(
            session,
            device,
            relativePath,
            destinationDirectory,
            cancellationToken);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"设备文件读回哈希不一致：{Path.GetFileName(relativePath)}");
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RestoreOriginalAnnotationDatabase(
        KindleDevice device,
        dynamic parent,
        string relativePath,
        string localBackupPath,
        string expectedSha256,
        string candidateSha256,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(relativePath.Replace('/', '\\'));
        dynamic? current = FindChild(parent, fileName);
        if (current is not null)
        {
            var currentId = GetWpdObjectId(current);
            var observed = localBackupPath + ".recovery-current-" + Guid.NewGuid().ToString("N");
            try
            {
                WpdNativeTransfer.CopyFileToLocal(device.RootPath, currentId, observed,
                    ReadInt64Property(current, "System.Size"), cancellationToken);
            }
            finally { Release(current); }
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(observed)));
            if (hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return;
            if (!hash.Equals(candidateSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"设备批注在恢复前已变化，已保留当前内容，拒绝覆盖。当前副本：{observed}");
            WpdNativeTransfer.DeleteObject(device.RootPath, currentId, cancellationToken);
            for (var attempt = 0; attempt < 100 && ChildExists(parent, fileName); attempt++)
                Thread.Sleep(100);
            if (ChildExists(parent, fileName))
                throw new IOException("无法清理未完成的设备批注数据库。");
        }

        WpdNativeTransfer.SendFile(
            device.RootPath,
            GetWpdObjectId(parent),
            localBackupPath,
            fileName,
            null,
            cancellationToken);
        WaitForAnnotationStorageItem(
            device,
            relativePath,
            new FileInfo(localBackupPath).Length,
            cancellationToken);
        CopyAnnotationStorageFileAndVerify(
            device,
            relativePath,
            Path.GetDirectoryName(Path.GetFullPath(localBackupPath))!,
            expectedSha256,
            cancellationToken);
    }
}
