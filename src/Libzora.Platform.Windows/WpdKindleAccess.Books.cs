using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Windows;

/// <summary>
/// Book copy, removal, send and thumbnails.
/// </summary>
internal static partial class WpdKindleAccess
{
    public static void ReplaceClippingsText(KindleDevice device, string text, CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "clippings-write", Guid.NewGuid().ToString("N"));
        var originalDirectory = Path.Combine(stagingDirectory, "original");
        var updatedDirectory = Path.Combine(stagingDirectory, "updated");
        Directory.CreateDirectory(originalDirectory);
        Directory.CreateDirectory(updatedDirectory);
        var originalPath = Path.Combine(originalDirectory, "My Clippings.txt");
        var updatedPath = Path.Combine(updatedDirectory, "My Clippings.txt");
        File.WriteAllText(updatedPath, text, new System.Text.UTF8Encoding(true));
        dynamic? shell = null;
        dynamic? documents = null;
        dynamic? item = null;
        var documentsObjectId = string.Empty;
        var originalCopied = false;
        var deleteAttempted = false;
        var replacementVerified = false;
        var preserveOriginalCopy = false;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath) ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle) ?? throw new IOException("无法读取设备内部存储。");
            item = FindItemByRelativePath(storage, @"documents\My Clippings.txt")
                ?? throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。");
            documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory) ?? throw new IOException("设备上不存在 documents 目录。");
            documentsObjectId = GetWpdObjectId(documents);
            var originalSize = ReadInt64Property(item, "System.Size");
            WpdNativeTransfer.CopyFileToLocal(
                device.RootPath,
                GetWpdObjectId(item),
                originalPath,
                originalSize,
                cancellationToken);
            originalCopied = true;

            cancellationToken.ThrowIfCancellationRequested();
            deleteAttempted = true;
            ShellFileOperation.DeletePermanently((object)item);
            var deleteStarted = DateTime.UtcNow;
            while (DateTime.UtcNow - deleteStarted < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadStorageItemState(device, @"documents\My Clippings.txt").Exists) break;
                Thread.Sleep(250);
            }
            if (ReadStorageItemState(device, @"documents\My Clippings.txt").Exists)
                throw new TimeoutException("等待 Kindle 更新 My Clippings.txt 超时。");

            WpdNativeTransfer.SendFile(
                device.RootPath,
                documentsObjectId,
                updatedPath,
                "My Clippings.txt",
                null,
                cancellationToken);
            WaitForStorageItem(device, @"documents\My Clippings.txt", new FileInfo(updatedPath).Length, null, "My Clippings.txt", cancellationToken);
            replacementVerified = true;
        }
        catch (Exception exception) when (exception is COMException or IOException or TimeoutException or OperationCanceledException)
        {
            if (originalCopied && deleteAttempted && !replacementVerified
                && !string.IsNullOrWhiteSpace(documentsObjectId))
            {
                try
                {
                    var current = ReadStorageItemState(device, @"documents\My Clippings.txt");
                    if (current.Exists)
                    {
                        TryRemoveStorageItem(device, @"documents\My Clippings.txt");
                        var cleanupStarted = DateTime.UtcNow;
                        while (DateTime.UtcNow - cleanupStarted < TimeSpan.FromSeconds(20)
                            && ReadStorageItemState(device, @"documents\My Clippings.txt").Exists)
                        {
                            Thread.Sleep(250);
                        }
                        if (ReadStorageItemState(device, @"documents\My Clippings.txt").Exists)
                            throw new TimeoutException("无法清理未完成的 My Clippings.txt，原始副本需要保留。");
                    }

                    WpdNativeTransfer.SendFile(
                        device.RootPath,
                        documentsObjectId,
                        originalPath,
                        "My Clippings.txt",
                        null,
                        CancellationToken.None);
                    WaitForStorageItem(device, @"documents\My Clippings.txt", new FileInfo(originalPath).Length, null, "My Clippings.txt", CancellationToken.None);
                }
                catch
                {
                    preserveOriginalCopy = true;
                }
            }
            if (preserveOriginalCopy)
                throw new IOException(
                    $"MTP 更新 My Clippings.txt 失败，原始副本已保留在：{originalPath}",
                    exception);
            if (exception is COMException com) throw new IOException("无法更新 MTP Kindle 的 My Clippings.txt。", com);
            throw;
        }
        finally
        {
            Release(item);
            Release(documents);
            Release(shell);
            FlushReleasedComObjects();
            if (!preserveOriginalCopy)
            {
                try { Directory.Delete(stagingDirectory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string CopyBookToLocal(
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var session = new WpdSession(device);
        return CopyBookToLocal(session, device, book, destinationDirectory, cancellationToken);
    }

    public static string CopyBookToLocal(
        WpdSession session,
        KindleDevice device,
        KindleBook book,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        dynamic? item = null;
        try
        {
            item = session.FindItem(Path.Combine(device.Profile.BooksDirectory, book.RelativePath));
            var destinationPath = GetUniqueLocalDestination(destinationDirectory, book.FileName);
            try
            {
                WpdNativeTransfer.CopyFileToLocal(
                    device.RootPath,
                    GetWpdObjectId(item),
                    destinationPath,
                    book.Size,
                    cancellationToken);
            }
            catch (COMException)
            {
                // Some Kindle firmware exposes the object through Shell but
                // rejects WPD_RESOURCE_DEFAULT. IFileOperation uses the same
                // Shell object and remains compatible with that firmware.
                ShellFileOperation.CopyToLocal((object)item, destinationPath, book.Size, cancellationToken);
            }
            return destinationPath;
        }
        catch (COMException exception)
        {
            throw new IOException("无法从 MTP 设备读取书籍。", exception);
        }
        finally
        {
            WpdSession.ReleaseItem(item);
        }
    }

    public static string CopyStorageFileToLocal(
        KindleDevice device,
        string relativePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var session = new WpdSession(device);
        return CopyStorageFileToLocal(session, device, relativePath, destinationDirectory, cancellationToken);
    }

    public static string CopyStorageFileToLocal(
        WpdSession session,
        KindleDevice device,
        string relativePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException("设备文件路径无效。");

        Directory.CreateDirectory(destinationDirectory);
        dynamic? item = null;
        try
        {
            item = session.FindItem(relativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("设备目标不能是文件夹。");
            var fileName = Path.GetFileName(relativePath);
            var destinationPath = GetUniqueLocalDestination(destinationDirectory, fileName);
            var expectedSize = ReadInt64Property(item, "System.Size");
            try
            {
                WpdNativeTransfer.CopyFileToLocal(
                    device.RootPath,
                    GetWpdObjectId(item),
                    destinationPath,
                    expectedSize,
                    cancellationToken);
            }
            catch (COMException)
            {
                ShellFileOperation.CopyToLocal((object)item, destinationPath, expectedSize, cancellationToken);
            }
            return destinationPath;
        }
        catch (COMException exception)
        {
            throw new IOException("无法从 MTP 设备读取书籍封面。", exception);
        }
        finally
        {
            WpdSession.ReleaseItem(item);
        }
    }

    public static void RemoveBook(
        KindleDevice device,
        KindleBook book,
        CancellationToken cancellationToken)
    {
        if (!device.Profile.IsBookPath(book.RelativePath))
            throw new InvalidOperationException("设备书籍路径无效。");
        if (string.IsNullOrWhiteSpace(book.RelativePath)
            || book.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException("设备文件路径无效。");

        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            dynamic? documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory)
                ?? throw new IOException("设备上不存在 documents 目录。");
            dynamic? item = FindItemByRelativePath(documents, book.RelativePath)
                ?? throw new FileNotFoundException("设备书籍不存在。", book.RelativePath);
            if ((bool)item.IsFolder) throw new InvalidOperationException("不能删除设备文件夹。");

            cancellationToken.ThrowIfCancellationRequested();
            // Delete the book first and only clear its .sdr reading state once
            // the device confirms the book is gone. Deleting state up front
            // would irreversibly lose reading position and annotations when
            // the second step fails (a locked book, a disconnect mid-delete).
            ShellFileOperation.DeletePermanently((object)item);
            var startedAt = DateTime.UtcNow;
            while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentItemExists(device, book.RelativePath))
                {
                    dynamic? readingState = FindItemByRelativePath(
                        documents,
                        Path.ChangeExtension(book.RelativePath, ".sdr"));
                    try
                    {
                        if (readingState is not null && (bool)readingState.IsFolder)
                            WpdNativeTransfer.DeleteObject(
                                device.RootPath,
                                GetWpdObjectId(readingState),
                                cancellationToken);
                    }
                    catch (Exception exception) when (exception is COMException or IOException)
                    {
                        // The book is already gone; a stale .sdr must not turn
                        // a completed removal into a reported failure.
                    }
                    finally { Release(readingState); }
                    return;
                }
                Thread.Sleep(250);
            }
            throw new TimeoutException("等待设备删除文件超时。");
        }
        catch (COMException exception)
        {
            throw new IOException("无法删除 MTP 设备上的书籍。", exception);
        }
        finally
        {
            Release(shell);
        }
    }

    public static void SendBook(
        KindleDevice device,
        string sourcePath,
        string finalName,
        KindleThumbnail? thumbnail,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var extension = Path.GetExtension(finalName);
        var transferName = $"libzora-transfer-{Guid.NewGuid():N}{extension}";
        var backupName = $"libzora-backup-{Guid.NewGuid():N}{extension}";
        var transferCompleted = false;
        var previousBookRenamed = false;
        var previousBookRenameStarted = false;
        var preserveTransfer = false;
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("设备已断开连接。");
            dynamic? storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取设备内部存储。");
            dynamic? documents = FindItemByRelativePath(storage, device.Profile.BooksDirectory)
                ?? throw new IOException("设备上不存在 documents 目录。");

            if (Path.GetFileName(finalName) != finalName
                || string.IsNullOrWhiteSpace(finalName)
                || !Path.GetExtension(finalName).Equals(sourceInfo.Extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("设备目标文件名无效。");

            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new TransferProgress(0, sourceInfo.Length, $"正在发送 {sourceInfo.Name}"));
            WpdNativeTransfer.SendFile(
                device.RootPath,
                GetWpdObjectId(documents),
                sourcePath,
                transferName,
                progress,
                cancellationToken);

            var timeout = TimeSpan.FromMinutes(Math.Clamp(2 + sourceInfo.Length / (100d * 1024 * 1024), 2, 30));
            WaitForItem(
                device,
                transferName,
                sourceInfo.Length,
                timeout,
                progress,
                sourceInfo.Name,
                cancellationToken);
            var finalSize = WaitForItem(
                device,
                transferName,
                sourceInfo.Length,
                TimeSpan.FromSeconds(15),
                progress,
                sourceInfo.Name,
                cancellationToken);
            if (finalSize != sourceInfo.Length)
                throw new IOException("设备文件大小校验失败。");
            if (thumbnail is not null)
            {
                progress?.Report(new TransferProgress(sourceInfo.Length, sourceInfo.Length, "正在同步设备书架封面"));
                var stagedThumbnail = Path.Combine(stagingDirectory, thumbnail.FileName);
                File.WriteAllBytes(stagedThumbnail, thumbnail.JpegBytes);
                UploadBookThumbnail(
                    device,
                    storage,
                    stagedThumbnail,
                    thumbnail.FileName,
                    thumbnail.JpegBytes.LongLength,
                    cancellationToken);
            }

            var existing = FindChild(documents, finalName);
            if (existing is null)
            {
                dynamic? orphanState = FindChild(documents, Path.ChangeExtension(finalName, ".sdr"));
                try
                {
                    if (orphanState is not null && (bool)orphanState.IsFolder)
                        WpdNativeTransfer.DeleteObject(device.RootPath, GetWpdObjectId(orphanState), cancellationToken);
                }
                finally { Release(orphanState); }
            }
            if (existing is not null)
            {
                try
                {
                    if ((bool)existing.IsFolder)
                        throw new InvalidOperationException("设备上同名目标是文件夹，无法替换书籍。");
                    previousBookRenameStarted = true;
                    ShellFileOperation.RenamePermanently((object)existing, backupName);
                    previousBookRenamed = true;
                }
                finally
                {
                    Release(existing);
                }
            }

            RenameDocumentItem(documents, device, transferName, finalName);
            transferCompleted = true;
            WaitForStorageItem(
                device,
                Path.Combine(device.Profile.BooksDirectory, finalName),
                sourceInfo.Length,
                null,
                finalName,
                cancellationToken);

            if (previousBookRenamed)
            {
                // The verified new copy is now in place. A cleanup failure
                // may leave a backup on-device, but cannot remove the new book.
                TryRemoveDocumentItem(device, backupName);
                previousBookRenamed = false;
            }
            progress?.Report(new TransferProgress(sourceInfo.Length, sourceInfo.Length, $"已发送 {finalName}"));
        }
        catch (Exception exception)
        {
            if (!previousBookRenamed && previousBookRenameStarted)
                previousBookRenamed = ReadStorageItemState(
                    device,
                    Path.Combine(device.Profile.BooksDirectory, backupName)).Exists;
            if (previousBookRenamed)
            {
                try
                {
                    TryRemoveDocumentItem(device, finalName);
                    RenameDocumentItem(null, device, backupName, finalName);
                    previousBookRenamed = false;
                }
                catch
                {
                    // Keep both the old backup and the staged/new copy when
                    // the device does not permit rollback after an interrupted
                    // rename. The user can recover either complete file.
                    preserveTransfer = true;
                }
            }
            if (exception is COMException com)
                throw new IOException("MTP 传输失败，请确认设备仍保持连接。", com);
            throw;
        }
        finally
        {
            Release(shell);
            if (!transferCompleted && !preserveTransfer)
                TryRemoveDocumentItem(device, transferName);
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RenameDocumentItem(dynamic? documents, KindleDevice device, string sourceName, string targetName)
    {
        dynamic? shell = null;
        dynamic? kindle = null;
        dynamic? storage = null;
        dynamic? folder = null;
        dynamic? item = null;
        try
        {
            if (documents is null)
            {
                shell = CreateShell();
                kindle = FindDevice(shell, device.RootPath)
                    ?? throw new IOException("设备已断开连接。");
                storage = FindFirstStorage(kindle)
                    ?? throw new IOException("无法读取设备内部存储。");
                folder = FindItemByRelativePath(storage, device.Profile.BooksDirectory)
                    ?? throw new IOException("设备上不存在 documents 目录。");
            }
            item = FindChild(documents ?? folder, sourceName)
                ?? throw new FileNotFoundException("MTP 设备上找不到待重命名的书籍。", sourceName);
            if ((bool)item.IsFolder)
                throw new InvalidOperationException("不能重命名设备上的文件夹来替换书籍。");
            ShellFileOperation.RenamePermanently((object)item, targetName);
        }
        finally
        {
            Release(item);
            Release(folder);
            Release(storage);
            Release(kindle);
            Release(shell);
        }
    }

    private static void UploadBookThumbnail(
        KindleDevice device,
        dynamic storage,
        string stagedThumbnail,
        string thumbnailFileName,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (thumbnailFileName.IndexOfAny(['\\', '/']) >= 0)
            throw new InvalidOperationException("设备缩略图文件名无效。");

        dynamic thumbnailFolder = FindOrCreateFolderPath(storage, @"system\thumbnails", cancellationToken);
        dynamic? existing = FindChild(thumbnailFolder, thumbnailFileName);
        if (existing is not null)
        {
            if ((bool)existing.IsFolder)
                throw new InvalidOperationException("设备缩略图目标不能是文件夹。");
            ShellFileOperation.DeletePermanently((object)existing);
            var deleteStartedAt = DateTime.UtcNow;
            var relativePath = $@"system\thumbnails\{thumbnailFileName}";
            while (DateTime.UtcNow - deleteStartedAt < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadStorageItemState(device, relativePath).Exists) break;
                Thread.Sleep(250);
            }
            if (ReadStorageItemState(device, relativePath).Exists)
                throw new TimeoutException("等待设备更新旧封面超时。");
        }

        WpdNativeTransfer.SendFile(
            device.RootPath,
            GetWpdObjectId(thumbnailFolder),
            stagedThumbnail,
            thumbnailFileName,
            null,
            cancellationToken);
        WaitForStorageItem(
            device,
            $@"system\thumbnails\{thumbnailFileName}",
            expectedSize,
            null,
            thumbnailFileName,
            cancellationToken);
    }
}
