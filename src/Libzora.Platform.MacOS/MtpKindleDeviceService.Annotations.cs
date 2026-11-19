using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS.Mtp;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Kindle devices that expose MTP instead of USB mass storage. macOS has no
/// MTP stack of its own, so this service talks to libmtp (the same library
/// calibre uses on macOS).
/// </summary>

/// <summary>
/// Clippings and annotation database mutation.
/// </summary>
public sealed partial class MtpKindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    public async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default,
        int maxItems = int.MaxValue)
    {
        if (!device.Profile.SupportsClippings) return [];

        // Account databases carry highlights and notes that never appear in
        // My Clippings.txt; the text file remains the fallback for firmware
        // without a readable database.
        try
        {
            var databaseNotes = await ReadAnnotationDatabaseClippingsAsync(
                device, cancellationToken, maxItems).ConfigureAwait(false);
            if (databaseNotes.Count > 0) return databaseNotes;
        }
        catch (Exception exception) when (IsAnnotationDatabaseFailure(exception))
        {
            Debug.WriteLine($"MTP annotation database unavailable: {exception.Message}");
        }

        var text = await ReadClippingsTextAsync(device, cancellationToken).ConfigureAwait(false);
        return text is null ? [] : KindleClippingsParser.Parse(text, maxItems);
    }

    public Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default) =>
        DeleteClippingsAsync(device, [clippingId], cancellationToken);

    public async Task DeleteClippingsAsync(
        KindleDevice device,
        IReadOnlyCollection<string> clippingIds,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.CanDeleteNotes)
            throw new NotSupportedException(UiText.Get("当前设备的笔记仅支持读取和导出。"));
        ArgumentNullException.ThrowIfNull(clippingIds);
        if (clippingIds.Count == 0) return;
        if (clippingIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("设备笔记标识无效。", nameof(clippingIds));
        // Rows in the account database are read-only here; only the text file
        // can be rewritten safely without a full annotation sync transaction.
        if (clippingIds.Any(id => id.StartsWith("kindle-db:", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException(UiText.Get("Kindle 账户数据库中的批注当前仅支持读取和导出。"));
        var ids = clippingIds.ToHashSet(StringComparer.Ordinal);
        var text = await ReadClippingsTextAsync(device, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Kindle 上不存在 My Clippings.txt。");
        var clippings = KindleClippingsParser.Parse(text);
        if (!ids.IsSubsetOf(clippings.Select(item => item.Id)))
            throw new FileNotFoundException("一个或多个设备划线笔记不存在。");
        var updated = KindleClippingsParser.BuildDocument(clippings.Where(item => !ids.Contains(item.Id)));
        var temporary = Path.Combine(Path.GetTempPath(), "Libzora", $"clippings-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
            await WithSessionAsync(device, session =>
            {
                session.Upload(
                    device.Profile.BooksDirectory,
                    temporary,
                    "My Clippings.txt",
                    cancellationToken: cancellationToken);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(
        KindleDevice device,
        CancellationToken cancellationToken = default)
    {
        if (!device.Profile.SupportsClippings) return null;
        var stagingDirectory = CreateStagingDirectory("annotation-snapshot");
        try
        {
            var local = await WithSessionAsync(device, session =>
            {
                var databases = FindAnnotationDatabases(session);
                if (databases.Count == 0) return null;
                if (databases.Count > 1)
                    throw new NotSupportedException(UiText.Get("Kindle 上存在多个账户批注数据库，无法确认当前账户，已停止同步。笔记仍可查看和导出。"));
                var database = databases[0];
                RejectAnnotationSideFiles(session, database);
                var destination = Path.Combine(stagingDirectory, AnnotationFileName);
                session.Download(database, destination, cancellationToken: cancellationToken);
                return new LocalAnnotationDatabase(
                    database.RelativePath.Replace('\\', '/'),
                    destination,
                    FindSdrFolders(session, device));
            }, cancellationToken).ConfigureAwait(false);
            if (local is null) return null;

            KindleAnnotationMountedWriter.ValidateSnapshotSource(local.LocalPath);
            var records = await KindleAnnotationDatabaseReader.ReadRecordsAsync(
                local.LocalPath, 100_000, cancellationToken, requireCompleteSnapshot: true).ConfigureAwait(false);
            var titleResolver = KindleAnnotationBookTitleResolver.Create(local.SdrFolders);
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
            await using var stream = File.OpenRead(local.LocalPath);
            var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            return new KindleAnnotationSnapshot(local.DatabaseRelativePath, sha256, snapshotRecords);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken = default) =>
        ApplyAnnotationMutationsAsync(device, snapshot, mutations, cancellationToken, null);

    public async Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
        KindleDevice device,
        KindleAnnotationSnapshot snapshot,
        IReadOnlyList<KindleAnnotationMutation> mutations,
        CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress)
    {
        progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
        if (!device.Profile.SupportsClippings)
            throw new NotSupportedException(UiText.Get("当前设备暂不支持回写 Kindle 批注。"));
        if (mutations.Count == 0)
            return new KindleAnnotationWriteResult(0, snapshot.Sha256, snapshot.Sha256, null, null);
        ValidateAnnotationDatabasePath(snapshot.DatabaseRelativePath);

        var deviceKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(device.Identity))).ToLowerInvariant()[..16];
        var backupDirectory = Path.Combine(
            _paths.Backups,
            "KindleAnnotations",
            deviceKey,
            DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        var workingDirectory = Path.Combine(backupDirectory, "working");
        Directory.CreateDirectory(workingDirectory);
        var workingPath = Path.Combine(workingDirectory, AnnotationFileName);
        var localBackupPath = Path.Combine(backupDirectory, AnnotationFileName);
        var candidatePath = Path.Combine(backupDirectory, "ksdk_annotation_v1.candidate.db");
        var readbackDirectory = Path.Combine(workingDirectory, "readback");
        Directory.CreateDirectory(readbackDirectory);
        try
        {
            // Snapshot the database again and prove it still matches the scan.
            await WithSessionAsync(device, session =>
            {
                var database = FindAnnotationDatabase(session, snapshot.DatabaseRelativePath)
                    ?? throw new IOException("设备批注数据库路径已经变化。");
                RejectAnnotationSideFiles(session, database);
                session.Download(database, workingPath, cancellationToken: cancellationToken);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            await VerifyHashAsync(
                workingPath, snapshot.Sha256,
                "Kindle 批注数据库在检测后已变化，请重新检测。", cancellationToken).ConfigureAwait(false);
            File.Copy(workingPath, localBackupPath, overwrite: false);

            var newHash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(
                workingPath,
                candidatePath,
                mutations,
                cancellationToken,
                progress).ConfigureAwait(false);

            var deviceBackup = await WithSessionAsync(device, session =>
                InstallAnnotationDatabase(
                    session,
                    snapshot.DatabaseRelativePath,
                    snapshot.Sha256,
                    candidatePath,
                    newHash,
                    localBackupPath,
                    readbackDirectory,
                    progress,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            return new KindleAnnotationWriteResult(
                mutations.Count,
                snapshot.Sha256,
                newHash,
                localBackupPath,
                deviceBackup);
        }
        finally
        {
            TryDelete(candidatePath);
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static string InstallAnnotationDatabase(
        MtpDeviceSession session,
        string databaseRelativePath,
        string expectedOriginalHash,
        string candidatePath,
        string candidateHash,
        string localBackupPath,
        string readbackDirectory,
        IProgress<KindleAnnotationSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = FindAnnotationDatabase(session, databaseRelativePath)
            ?? throw new IOException("设备批注数据库路径已经变化。");
        RejectAnnotationSideFiles(session, database);
        var parentDirectory = database.RelativePath[..database.RelativePath.LastIndexOf('/')];

        var currentReadback = Path.Combine(readbackDirectory, "current.db");
        session.Download(database, currentReadback, cancellationToken: cancellationToken);
        VerifyHash(currentReadback, expectedOriginalHash,
            "设备批注数据库在同步期间发生变化，已取消安装。");

        // Upload the untouched database and the candidate under temporary
        // names first, so both are proven readable before the original is
        // replaced. This mirrors the WPD transport.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stageName = $"{AnnotationFileName}.libzora-sync-new-{stamp}";
        var backupName = $"{AnnotationFileName}.libzora-backup-{stamp}";

        progress?.Report(new(KindleAnnotationSyncStage.BackingUp));
        session.Upload(parentDirectory, localBackupPath, backupName, cancellationToken: cancellationToken);
        var backupReadback = Path.Combine(readbackDirectory, "backup.db");
        session.Download(
            session.Find($"{parentDirectory}/{backupName}")
                ?? throw new IOException("设备未确认写入批注备份。"),
            backupReadback,
            cancellationToken: cancellationToken);
        VerifyHash(backupReadback, expectedOriginalHash, "设备批注备份校验失败。");

        progress?.Report(new(KindleAnnotationSyncStage.PreparingWrite));
        session.Upload(parentDirectory, candidatePath, stageName, cancellationToken: cancellationToken);
        var stageReadback = Path.Combine(readbackDirectory, "stage.db");
        session.Download(
            session.Find($"{parentDirectory}/{stageName}")
                ?? throw new IOException("设备未确认写入批注快照。"),
            stageReadback,
            cancellationToken: cancellationToken);
        VerifyHash(stageReadback, candidateHash, "设备批注快照校验失败。");

        // Last freshness check immediately before deleting the original.
        database = FindAnnotationDatabase(session, databaseRelativePath)
            ?? throw new IOException("设备批注数据库已消失，已取消安装。");
        RejectAnnotationSideFiles(session, database);
        session.Download(database, currentReadback, cancellationToken: cancellationToken);
        VerifyHash(currentReadback, expectedOriginalHash,
            "设备批注数据库在同步期间发生变化，已取消安装。");

        progress?.Report(new(KindleAnnotationSyncStage.WritingToDevice));
        session.Delete(database);
        try
        {
            session.Upload(parentDirectory, candidatePath, AnnotationFileName, cancellationToken: cancellationToken);
            var installed = FindAnnotationDatabase(session, databaseRelativePath)
                ?? throw new IOException("设备未确认安装批注数据库。");
            var installedReadback = Path.Combine(readbackDirectory, "installed.db");
            session.Download(installed, installedReadback, cancellationToken: cancellationToken);
            VerifyHash(installedReadback, candidateHash, "设备批注数据库安装后校验失败。");
        }
        catch
        {
            // Never leave the device with a missing or partially written
            // database: the verified byte copy goes back under its own name.
            try
            {
                session.Upload(parentDirectory, localBackupPath, AnnotationFileName);
            }
            catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Restoring the Kindle annotation database failed: {restoreFailure.Message}");
            }
            throw;
        }

        try
        {
            if (session.Find($"{parentDirectory}/{stageName}") is { } staged) session.Delete(staged);
        }
        catch (IOException exception)
        {
            // Leaving a verified staging file behind cannot invalidate a
            // database that already passed its readback check.
            Debug.WriteLine($"Removing the staged annotation database failed: {exception.Message}");
        }

        progress?.Report(new(KindleAnnotationSyncStage.Verifying));
        return $"{parentDirectory}/{backupName}";
    }

    private async Task<IReadOnlyList<KindleClipping>> ReadAnnotationDatabaseClippingsAsync(
        KindleDevice device,
        CancellationToken cancellationToken,
        int maxItems)
    {
        if (maxItems <= 0) return [];
        var stagingDirectory = CreateStagingDirectory("annotation-database-read");
        try
        {
            var databases = await WithSessionAsync(
                device, FindAnnotationDatabases, cancellationToken).ConfigureAwait(false);
            if (databases.Count == 0) return [];
            var sdrFolders = await WithSessionAsync(
                device, session => FindSdrFolders(session, device), cancellationToken).ConfigureAwait(false);
            var titleResolver = KindleAnnotationBookTitleResolver.Create(sdrFolders);
            var result = new List<KindleClipping>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < databases.Count && result.Count < maxItems; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var database = databases[index];
                var localPath = Path.Combine(stagingDirectory, $"annotation-{index}.db");
                await WithSessionAsync(device, session =>
                {
                    session.Download(database, localPath, cancellationToken: cancellationToken);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
                var notes = await KindleAnnotationDatabaseReader.ReadClippingsAsync(
                    localPath,
                    titleResolver.Resolve,
                    maxItems - result.Count,
                    cancellationToken).ConfigureAwait(false);
                foreach (var note in notes)
                    if (ids.Add(note.Id)) result.Add(note);
            }
            return result;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static IReadOnlyList<MtpEntry> FindAnnotationDatabases(MtpDeviceSession session)
    {
        var databases = session.Enumerate(AnnotationRoot)
            .Where(entry => !entry.IsFolder
                && entry.Name.Equals(AnnotationFileName, StringComparison.OrdinalIgnoreCase))
            .Take(32)
            .ToArray();
        var accounts = databases
            .Where(entry => IsAccountDatabase(entry.RelativePath))
            .ToArray();
        return accounts.Length > 0 ? accounts : databases;
    }

    private static MtpEntry? FindAnnotationDatabase(MtpDeviceSession session, string relativePath)
    {
        var entry = session.Find(relativePath);
        return entry is not null
            && !entry.IsFolder
            && entry.Name.Equals(AnnotationFileName, StringComparison.OrdinalIgnoreCase)
            ? entry
            : null;
    }

    private static IReadOnlyList<string> FindSdrFolders(MtpDeviceSession session, KindleDevice device) =>
        session.List(device.Profile.BooksDirectory)
            .Where(entry => entry.IsFolder
                && entry.Name.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name)
            .ToArray();

    private static bool IsAccountDatabase(string relativePath) =>
        relativePath.Split('/', '\\')
            .Any(segment => segment.StartsWith("amzn1.account.", StringComparison.OrdinalIgnoreCase));

    private static void RejectAnnotationSideFiles(MtpDeviceSession session, MtpEntry database)
    {
        var directory = database.RelativePath[..database.RelativePath.LastIndexOf('/')];
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (session.Find($"{directory}/{database.Name}{suffix}") is not null)
                throw new IOException("设备批注数据库带有 SQLite 侧文件，暂不能安全同步。");
        }
    }

    private static void ValidateAnnotationDatabasePath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (!normalized.StartsWith(AnnotationRoot + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(segment => segment is "." or "..")
            || !Path.GetFileName(normalized).Equals(AnnotationFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kindle 批注数据库路径无效。");
    }

    private static void VerifyHash(string path, string expected, string message)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }

    private static async Task VerifyHashAsync(
        string path, string expected, string message, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException(message);
    }

    private static bool IsAnnotationDatabaseFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or TimeoutException
            or Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException;

    private static void UploadThumbnail(
        MtpDeviceSession session,
        KindleThumbnail thumbnail,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(
            Path.GetTempPath(), "Libzora", $"thumbnail-{Guid.NewGuid():N}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            File.WriteAllBytes(temporary, thumbnail.JpegBytes);
            session.Upload(ThumbnailDirectory, temporary, thumbnail.FileName, cancellationToken: cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void DeleteSidecarFolder(MtpDeviceSession session, string directory, string fileName)
    {
        var sidecarName = Path.ChangeExtension(fileName, ".sdr");
        var sidecarPath = string.IsNullOrEmpty(directory) ? sidecarName : $"{directory}/{sidecarName}";
        if (session.Find(sidecarPath) is { IsFolder: true } sidecar)
            session.DeleteTree(sidecar);
    }

    private static string CreateStagingDirectory(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "Libzora", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
