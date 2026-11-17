namespace Libzora.Platform.MacOS.Mtp;

/// <summary>
/// Native operations needed to replace one file safely. The transaction logic
/// lives in <see cref="MtpFileReplacement"/> and is unit tested with an
/// in-memory implementation, so no real reader is required.
/// </summary>
internal interface IMtpFileReplacementOperations
{
    MtpEntry? Find(string fileName);
    bool Rename(MtpEntry entry, string newFileName);
    void Delete(MtpEntry entry);
    MtpEntry Upload(
        string sourcePath,
        string fileName,
        Action<long, long>? progress,
        CancellationToken cancellationToken);
    string? CreateRecoveryCopy(MtpEntry entry);
    void DeleteRecoveryCopy(string path);
    void Log(string message);
}

/// <summary>
/// Replaces an existing file while protecting the previous bytes.
///
/// Firmware that supports object renaming gets the cheap path: the old object
/// is renamed to a backup name, the staged new object takes the formal name,
/// and only then is the backup removed. Firmware without rename support gets
/// a conservative path: the old bytes are first downloaded as a local
/// recovery copy, the old object is deleted, and the new object is uploaded.
///
/// If any step fails, the old object is restored under its formal name when
/// possible. If that is not possible, the old object is left under its backup
/// name, or the local recovery copy is kept and named in the error. The error
/// never claims the bytes were lost when a recoverable copy still exists.
/// </summary>
internal static class MtpFileReplacement
{
    public static MtpEntry Commit(
        MtpEntry existing,
        MtpEntry staged,
        string stagedName,
        string sourcePath,
        string fileName,
        CancellationToken cancellationToken,
        IMtpFileReplacementOperations operations)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(operations);
        cancellationToken.ThrowIfCancellationRequested();

        var backupName = $"{fileName}.libzora-old-{Guid.NewGuid():N}";
        var renameSucceeded = TryRename(operations, existing, backupName);
        var backup = operations.Find(backupName);
        if (backup is null || backup.ItemId != existing.ItemId)
        {
            if (renameSucceeded)
            {
                // The rename was reported as successful but cannot be
                // verified. Do not delete or overwrite anything else in that
                // state.
                throw new IOException(
                    $"无法确认原文件 {fileName} 已安全改名备份，已停止替换；若文件名已变化，原文件仍保存在设备上，文件名为 {backupName}。");
            }

            var original = operations.Find(fileName);
            if (original is null || original.ItemId != existing.ItemId)
                throw new IOException($"无法确认原文件 {fileName} 的状态，已停止替换；请重新连接设备后检查文件。");
            return CommitOnFirmwareWithoutRename(
                existing, staged, stagedName, sourcePath, fileName, cancellationToken, operations);
        }
        // Some firmware reports a failure even though the rename happened;
        // finding the backup object under our name verifies the move.

        TryRename(operations, staged, fileName);
        var installed = operations.Find(fileName);
        if (installed is not null && installed.ItemId == staged.ItemId)
        {
            TryDeleteEntry(operations, backup);
            return installed;
        }

        // The new object did not take the formal name. Put the old object
        // back before reporting the failure.
        TryRename(operations, backup, fileName);
        var restored = operations.Find(fileName);
        if (restored is not null && restored.ItemId == existing.ItemId)
        {
            TryDeleteEntry(operations, staged);
            throw new IOException($"替换 {fileName} 失败，原文件已恢复为原名。");
        }

        throw new IOException(
            $"替换 {fileName} 失败，无法自动恢复原文件名。原文件仍安全保存在设备上，文件名为 {backupName}；新副本临时文件为 {stagedName}。");
    }

    private static MtpEntry CommitOnFirmwareWithoutRename(
        MtpEntry existing,
        MtpEntry staged,
        string stagedName,
        string sourcePath,
        string fileName,
        CancellationToken cancellationToken,
        IMtpFileReplacementOperations operations)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? recoveryPath = null;
        try
        {
            recoveryPath = operations.CreateRecoveryCopy(existing);
        }
        catch (Exception exception)
        {
            operations.Log($"Creating a local recovery copy of {fileName} failed: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(recoveryPath))
        {
            TryDeleteEntry(operations, staged);
            throw new IOException(
                $"设备不支持文件重命名，且无法为原文件 {fileName} 创建安全恢复副本，已取消替换并保留原文件。");
        }

        try
        {
            operations.Delete(existing);
        }
        catch (Exception exception)
        {
            // The delete reported a failure, but the object may have been
            // removed before the error. Keep the recovery copy and the staged
            // new object rather than guessing and discarding the only safe
            // copies.
            throw new IOException(
                $"无法确认原文件 {fileName} 是否仍然存在，已停止替换。原文件的本地恢复副本位于 {recoveryPath}；"
                + $"新副本临时文件为 {stagedName}。请检查设备文件列表后再重试。",
                exception);
        }

        try
        {
            var installed = operations.Upload(sourcePath, fileName, null, cancellationToken);
            operations.DeleteRecoveryCopy(recoveryPath);
            TryDeleteEntry(operations, staged);
            return installed;
        }
        catch (Exception uploadFailure)
        {
            // A failed upload can leave a partial object under the formal
            // name. Remove it before restoring the old bytes, otherwise the
            // restore would collide with an unreadable half-written file.
            TryDeleteNamedEntry(operations, fileName);

            Exception? restoreFailure = null;
            try
            {
                // Restoring the old bytes must not be skipped because the user
                // cancelled after the original had already been removed.
                operations.Upload(recoveryPath, fileName, null, CancellationToken.None);
            }
            catch (Exception exception)
            {
                restoreFailure = exception;
            }

            if (restoreFailure is null)
            {
                operations.DeleteRecoveryCopy(recoveryPath);
                TryDeleteEntry(operations, staged);
                if (uploadFailure is OperationCanceledException canceled)
                {
                    throw new OperationCanceledException(
                        $"替换 {fileName} 已取消；原文件已恢复。", canceled, canceled.CancellationToken);
                }
                throw new IOException($"替换 {fileName} 失败；原文件已恢复。", uploadFailure);
            }

            // Keep the local recovery copy and the verified staged object.
            // The message names both anchors instead of claiming data loss.
            throw new IOException(
                $"替换 {fileName} 失败，且自动恢复原文件未成功。设备上仍保留新副本临时文件 {stagedName}，"
                + $"原文件的本地恢复副本位于 {recoveryPath}。请保持设备连接并重试。",
                new AggregateException(uploadFailure, restoreFailure));
        }
    }

    private static bool TryRename(
        IMtpFileReplacementOperations operations,
        MtpEntry entry,
        string newFileName)
    {
        try
        {
            return operations.Rename(entry, newFileName);
        }
        catch (Exception exception)
        {
            operations.Log($"Renaming {entry.RelativePath} to {newFileName} failed: {exception.Message}");
            return false;
        }
    }

    private static void TryDeleteEntry(
        IMtpFileReplacementOperations operations,
        MtpEntry entry)
    {
        try
        {
            operations.Delete(entry);
        }
        catch (Exception exception)
        {
            operations.Log($"Removing {entry.RelativePath} failed: {exception.Message}");
        }
    }

    private static void TryDeleteNamedEntry(
        IMtpFileReplacementOperations operations,
        string fileName)
    {
        try
        {
            if (operations.Find(fileName) is { } entry) operations.Delete(entry);
        }
        catch (Exception exception)
        {
            operations.Log($"Removing a partial {fileName} failed: {exception.Message}");
        }
    }
}
