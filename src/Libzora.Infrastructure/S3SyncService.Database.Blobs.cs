using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Blob staging, download and local file checks.
/// </summary>
public sealed partial class S3SyncService
{
    private static async Task<List<LocalDuplicateFile>> ReadLocalDuplicateFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid bookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var files = new List<LocalDuplicateFile>();
        var command = commandCache.Get(
            "SELECT Id, RelativePath, Sha256 FROM BookFiles WHERE BookId = $bookId;");
        AddParameter(command, "$bookId", bookId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new LocalDuplicateFile(
                ParseGuid(reader.GetString(0), "BookFiles.Id"),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return files;
    }

    private static async Task MergeDuplicateFileRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceFileId,
        Guid targetFileId,
        Guid canonicalBookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        await ReaderReadingHistory.MergeFilesAsync(connection, transaction, sourceFileId, targetFileId, canonicalBookId, cancellationToken);
        await ReadingTimeSyncTracker.MergeFilesAsync(connection, transaction, sourceFileId, targetFileId, cancellationToken);
        var moveAnnotations = commandCache.Get(
            """
            UPDATE ReaderAnnotations
            SET BookId = $canonical, BookFileId = $target
            WHERE BookFileId = $source;
            """);
        AddParameter(moveAnnotations, "$canonical", canonicalBookId.ToString());
        AddParameter(moveAnnotations, "$target", targetFileId.ToString());
        AddParameter(moveAnnotations, "$source", sourceFileId.ToString());
        await moveAnnotations.ExecuteNonQueryAsync(cancellationToken);

        var moveBookmarks = commandCache.Get(
            """
            UPDATE ReaderBookmarks
            SET BookId = $canonical, BookFileId = $target
            WHERE BookFileId = $source;
            """);
        AddParameter(moveBookmarks, "$canonical", canonicalBookId.ToString());
        AddParameter(moveBookmarks, "$target", targetFileId.ToString());
        AddParameter(moveBookmarks, "$source", sourceFileId.ToString());
        await moveBookmarks.ExecuteNonQueryAsync(cancellationToken);

        await MergeVersionedFileRowAsync(
            connection,
            transaction,
            "ReaderProgress",
            sourceFileId,
            targetFileId,
            canonicalBookId,
            commandCache,
            cancellationToken,
            "ChapterPath",
            "Fragment",
            "ChapterIndex",
            "ScrollPosition",
            "ProgressPercent",
            "FlowMode",
            "UpdatedAt");
        var moveSyncedMarkers = commandCache.Get(
            """
            UPDATE OR IGNORE ReaderSyncedPositionMarkers
            SET BookId = $canonical, BookFileId = $target
            WHERE BookFileId = $source;
            DELETE FROM ReaderSyncedPositionMarkers
            WHERE BookFileId = $source;
            """);
        AddParameter(moveSyncedMarkers, "$canonical", canonicalBookId.ToString());
        AddParameter(moveSyncedMarkers, "$target", targetFileId.ToString());
        AddParameter(moveSyncedMarkers, "$source", sourceFileId.ToString());
        await moveSyncedMarkers.ExecuteNonQueryAsync(cancellationToken);
        await MergeVersionedFileRowAsync(
            connection,
            transaction,
            "ReaderLayoutSettings",
            sourceFileId,
            targetFileId,
            canonicalBookId,
            commandCache,
            cancellationToken,
            "FontScale",
            "LineHeight",
            "MaxWidth",
            "BodyPadding",
            "FontFamily",
            "FlowMode",
            "VerticalWriting",
            "TwoPageMode",
            "ParagraphIndent",
            "UpdatedAt");
        var preserveCompletion = commandCache.Get("""
            UPDATE ReaderReadingStats SET HasFinished=MAX(HasFinished,COALESCE(
                (SELECT HasFinished FROM ReaderReadingStats WHERE BookFileId=$source),0))
            WHERE BookFileId=$target;
            """);
        AddParameter(preserveCompletion, "$source", sourceFileId.ToString());
        AddParameter(preserveCompletion, "$target", targetFileId.ToString());
        await preserveCompletion.ExecuteNonQueryAsync(cancellationToken);
        await MergeVersionedFileRowAsync(
            connection,
            transaction,
            "ReaderReadingStats",
            sourceFileId,
            targetFileId,
            canonicalBookId,
            commandCache,
            cancellationToken,
            "CumulativeSeconds",
            "ListeningSeconds",
            "HasFinished",
            "ProgressPercent",
            "CompletedChapters",
            "TotalChapters",
            "UpdatedAt");
        await ReadingTimeSyncTracker.RecordCurrentTotalAsync(connection, transaction, targetFileId, cancellationToken);

        var moveSessions = commandCache.Get(
            """
            UPDATE ReaderReadingSessions
            SET BookId = $canonical, BookFileId = $target
            WHERE BookFileId = $source;
            """);
        AddParameter(moveSessions, "$canonical", canonicalBookId.ToString());
        AddParameter(moveSessions, "$target", targetFileId.ToString());
        AddParameter(moveSessions, "$source", sourceFileId.ToString());
        await moveSessions.ExecuteNonQueryAsync(cancellationToken);

        // Parsed content is a rebuildable cache. Dropping the duplicate file's
        // chunks avoids violating the unique position index on the retained ID.
        var deleteChunks = commandCache.Get(
            "DELETE FROM BookContentChunks WHERE BookFileId = $source;");
        AddParameter(deleteChunks, "$source", sourceFileId.ToString());
        await deleteChunks.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MergeVersionedFileRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        Guid sourceFileId,
        Guid targetFileId,
        Guid canonicalBookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken,
        params string[] columns)
    {
        var columnList = string.Join(", ", columns);
        var assignments = string.Join(
            ", ",
            columns.Select(column => column == "HasFinished"
                ? $"HasFinished = MAX(HasFinished,(SELECT HasFinished FROM {table} WHERE BookFileId=$source))"
                : $"{column} = (SELECT {column} FROM {table} WHERE BookFileId = $source)"));

        var copy = commandCache.Get(
            $"""
            INSERT OR IGNORE INTO {table} (BookFileId, BookId, {columnList})
            SELECT $target, $canonical, {columnList}
            FROM {table}
            WHERE BookFileId = $source;
            """);
        AddParameter(copy, "$target", targetFileId.ToString());
        AddParameter(copy, "$canonical", canonicalBookId.ToString());
        AddParameter(copy, "$source", sourceFileId.ToString());
        await copy.ExecuteNonQueryAsync(cancellationToken);

        var update = commandCache.Get(
            $"""
            UPDATE {table}
            SET BookId = $canonical, {assignments}
            WHERE BookFileId = $target
              AND EXISTS (SELECT 1 FROM {table} WHERE BookFileId = $source)
              AND julianday((SELECT UpdatedAt FROM {table} WHERE BookFileId = $source))
                  > julianday(UpdatedAt);
            """);
        AddParameter(update, "$target", targetFileId.ToString());
        AddParameter(update, "$canonical", canonicalBookId.ToString());
        AddParameter(update, "$source", sourceFileId.ToString());
        await update.ExecuteNonQueryAsync(cancellationToken);

        var delete = commandCache.Get(
            $"DELETE FROM {table} WHERE BookFileId = $source;");
        AddParameter(delete, "$source", sourceFileId.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateFileBookReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        Guid bookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        foreach (var statement in new[]
        {
            "UPDATE ReaderAnnotations SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderProgress SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderSyncedPositionMarkers SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderBookmarks SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderLayoutSettings SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderReadingStats SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE ReaderReadingSessions SET BookId = $bookId WHERE BookFileId = $fileId;",
            "UPDATE BookContentChunks SET BookId = $bookId WHERE BookFileId = $fileId;"
        })
        {
            var command = commandCache.Get(statement);
            AddParameter(command, "$bookId", bookId.ToString());
            AddParameter(command, "$fileId", fileId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> IsRelativeDataPathReferencedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction,
            """
            SELECT EXISTS(
                SELECT 1 FROM BookFiles
                WHERE lower(replace(RelativePath, '\', '/')) = lower(replace($path, '\', '/'))
                UNION ALL
                SELECT 1 FROM Books
                WHERE lower(replace(CoverPath, '\', '/')) = lower(replace($path, '\', '/'))
                UNION ALL SELECT 1 FROM ReadingMaterialBooks
                WHERE lower(replace(CoverPath, '\', '/')) = lower(replace($path, '\', '/')));
            """);
        AddParameter(command, "$path", relativePath);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private async Task RemoveReferencedScheduledPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ICollection<string> paths,
        CancellationToken cancellationToken)
    {
        var candidates = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return;

        using var command = CreateCommand(connection, transaction,
            """
            SELECT EXISTS(
                SELECT 1 FROM BookFiles
                WHERE lower(replace(RelativePath, '\', '/')) = lower(replace($path, '\', '/'))
                UNION ALL
                SELECT 1 FROM Books
                WHERE lower(replace(CoverPath, '\', '/')) = lower(replace($path, '\', '/')));
            """);
        var unreferenced = new List<string>(candidates.Length);
        foreach (var path in candidates)
        {
            var relative = Path.GetRelativePath(_paths.Data, path);
            AddParameter(command, "$path", relative);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                unreferenced.Add(path);
        }
        paths.Clear();
        foreach (var path in unreferenced)
            paths.Add(path);
    }

    private void TryDeleteEmptyParentDirectories(string filePath)
    {
        var libraryRoot = Path.GetFullPath(_paths.Library)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var coversRoot = Path.GetFullPath(_paths.Covers)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrWhiteSpace(directory)
            && !string.Equals(directory, libraryRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(directory, coversRoot, StringComparison.OrdinalIgnoreCase))
        {
            var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!normalized.StartsWith(libraryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith(coversRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                break;
            try
            {
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
                else
                    break;
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
            directory = Path.GetDirectoryName(directory);
        }
    }

    private string GetAvailableFileName(
        string? sourceName,
        string? hash,
        string? format,
        Guid bookId,
        IReadOnlySet<string> plannedRelativePaths)
    {
        var name = SanitizeFileName(sourceName);
        if (name.Length == 0)
            name = $"{(IsSha256(hash) ? hash![..12] : Guid.NewGuid().ToString("N"))}.{format?.Trim().TrimStart('.') ?? "bin"}";

        var relative = Path.Combine("library", bookId.ToString("N"), name);
        var path = ResolveDataPath(relative);
        var planned = plannedRelativePaths.Contains(relative);
        if (path is null || (!File.Exists(path) && !planned)) return name;

        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var suffix = IsSha256(hash) ? hash![..12] : Guid.NewGuid().ToString("N")[..12];
        var candidate = $"{stem}-{suffix}{extension}";
        var attempt = 1;
        while (true)
        {
            var candidateRelative = Path.Combine("library", bookId.ToString("N"), candidate);
            var candidatePath = ResolveDataPath(candidateRelative);
            if (candidatePath is not null
                && !File.Exists(candidatePath)
                && !plannedRelativePaths.Contains(candidateRelative))
                return candidate;
            candidate = $"{stem}-{suffix}-{attempt++}{extension}";
        }
    }

    private static string SanitizeFileName(string? sourceName)
    {
        var name = Path.GetFileName((sourceName ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        if (name.Length == 0 || name is "." or "..") return string.Empty;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length <= 180 ? name : name[..180];
    }

    private static string NormalizeCoverExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).Trim().ToLowerInvariant();
        return extension is ".png" or ".webp" or ".gif" or ".jpeg" or ".jpg" ? extension : ".jpg";
    }

    private async Task<bool> EnsureLocalBlobAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        string hash,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(hash))
            throw new InvalidDataException("同步文件缺少有效的 SHA-256 校验值。");

        if (File.Exists(targetPath))
        {
            try
            {
                var existingHash = await GetCachedFileHashAsync(targetPath, cancellationToken);
                if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (IOException)
            {
            }
        }

        var staged = await DownloadBlobToStagingAsync(
            client, settings, hash, targetPath, cancellationToken);
        try
        {
            PublishStagedSyncBlob(staged.Path, targetPath);
            progress?.Report(UiText.Get("已下载 {0}", Path.GetFileName(targetPath)));
        }
        finally
        {
            TryDeleteSyncPartialFile(staged.Path);
        }
        return true;
    }

    /// <summary>
    /// Downloads and verifies one content-addressed blob into a staging file
    /// next to the target. The caller decides when to publish it, so network
    /// I/O never has to run while the library writer lease is held.
    /// </summary>
    private async Task<StagedSyncBlob> DownloadBlobToStagingAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        string hash,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(hash))
            throw new InvalidDataException("同步文件缺少有效的 SHA-256 校验值。");

        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("同步文件目标目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = targetPath + $".sync-{hash.ToLowerInvariant()}.part";
        var decryptedPath = temporaryPath + ".plain";
        var keepPartial = true;
        string? stagedPath = null;
        try
        {
            if (settings.EncryptionKey.Length == 0 && File.Exists(temporaryPath))
            {
                var partialHash = await Hashing.Sha256Async(temporaryPath, cancellationToken);
                if (partialHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    stagedPath = temporaryPath;
                    keepPartial = false;
                    return new StagedSyncBlob(stagedPath);
                }
            }

            var partialLength = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
            using var response = await OpenBlobReadAsync(
                client,
                settings,
                hash,
                cancellationToken,
                partialLength);
            if (partialLength > 0 && !response.IsPartialResponse)
            {
                TryDeleteSyncPartialFile(temporaryPath);
                partialLength = 0;
            }

            await using (var output = new FileStream(
                temporaryPath,
                partialLength > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                useAsync: true))
            {
                await response.ResponseStream.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var remoteLength = response.TotalLength;
            var receivedLength = new FileInfo(temporaryPath).Length;
            if (remoteLength is > 0 && receivedLength != remoteLength.Value)
            {
                if (receivedLength < remoteLength.Value)
                    throw new TimeoutException("远端文件传输中断，已保存部分数据；下次同步会从断点继续。");
                keepPartial = false;
                throw new InvalidDataException("远端文件长度超过响应声明，已拒绝安装。");
            }

            var candidatePath = temporaryPath;
            if (settings.EncryptionKey.Length > 0)
            {
                await using var encryptedInput = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 128,
                    useAsync: true);
                try
                {
                    await DecryptBlobToPathAsync(
                        encryptedInput,
                        decryptedPath,
                        settings.EncryptionKey,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
                {
                    keepPartial = false;
                    throw;
                }
                candidatePath = decryptedPath;
            }

            var actualHash = await Hashing.Sha256Async(candidatePath, cancellationToken);
            if (!string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                keepPartial = false;
                throw new InvalidDataException("下载后的同步文件校验值不匹配。");
            }

            stagedPath = candidatePath;
            keepPartial = false;
            if (settings.EncryptionKey.Length > 0)
                TryDeleteSyncPartialFile(temporaryPath);
            return new StagedSyncBlob(stagedPath);
        }
        finally
        {
            if (stagedPath is null)
            {
                TryDeleteSyncPartialFile(decryptedPath);
                if (!keepPartial) TryDeleteSyncPartialFile(temporaryPath);
            }
        }
    }

    private static void PublishStagedSyncBlob(string stagedPath, string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("同步文件目标目录无效。");
        Directory.CreateDirectory(directory);
        File.Move(stagedPath, targetPath, overwrite: true);
    }

    private async Task<bool> BookFileMatchesAsync(
        BookFile expected,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.BookId, f.RelativePath, f.Sha256
            FROM BookFiles f
            JOIN Books b ON b.Id = f.BookId
            WHERE f.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", expected.Id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        if (!Guid.TryParse(reader.GetString(0), out var bookId) || bookId != expected.BookId) return false;
        if (!string.Equals(reader.GetString(2), expected.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var currentTarget = ResolveDataPath(reader.GetString(1));
        return currentTarget is not null && PathsEqual(currentTarget, targetPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private sealed record StagedSyncBlob(string Path);

    private static void TryDeleteSyncPartialFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
