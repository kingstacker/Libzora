using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Row inserts, purge staging and schema helpers.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    private static async Task RenameLegacyColumnsAsync(
        SqliteConnection connection,
        HashSet<string> existing,
        string suffix,
        string target,
        CancellationToken cancellationToken)
    {
        if (existing.Contains(target)) return;
        var legacy = existing.FirstOrDefault(name =>
            name.Length > suffix.Length
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && !name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (legacy is null) return;
        var rename = connection.CreateCommand();
        rename.CommandText = $"ALTER TABLE Books RENAME COLUMN \"{legacy.Replace("\"", "\"\"")}\" TO \"{target}\";";
        await rename.ExecuteNonQueryAsync(cancellationToken);
        existing.Remove(legacy);
        existing.Add(target);
    }

    private static async Task InsertFileAsync(
        SqliteConnection connection,
        BookFile file,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256, SyncUpdatedAt) VALUES ($id, $bookId, $format, $relativePath, $size, $sha256, $syncUpdatedAt);";
        command.Parameters.AddWithValue("$id", file.Id.ToString());
        command.Parameters.AddWithValue("$bookId", file.BookId.ToString());
        command.Parameters.AddWithValue("$format", file.Format);
        command.Parameters.AddWithValue("$relativePath", file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$sha256", file.Sha256);
        command.Parameters.AddWithValue("$syncUpdatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTrashEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LibraryTrash
                (Id, Kind, BookId, Title, Format, Size, DeletedAt, BookJson, FileJson, BookFileId,
                 TrashPath, OriginalPath, TrashCoverPath, OriginalCoverPath)
            VALUES
                ($id, $kind, $bookId, $title, $format, $size, $deletedAt, $bookJson, $fileJson, $bookFileId,
                 $trashPath, $originalPath, $trashCoverPath, $originalCoverPath);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$bookId", entry.BookId.ToString());
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$format", (object?)entry.Format ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", entry.Size);
        command.Parameters.AddWithValue("$deletedAt", entry.DeletedAt.ToString("O"));
        command.Parameters.AddWithValue("$bookJson", (object?)entry.BookJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileJson", (object?)entry.FileJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$bookFileId", (object?)entry.BookFileId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$trashPath", entry.TrashPath);
        command.Parameters.AddWithValue("$originalPath", entry.OriginalPath);
        command.Parameters.AddWithValue("$trashCoverPath", (object?)entry.TrashCoverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$originalCoverPath", (object?)entry.OriginalCoverPath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteTrashEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM LibraryTrash WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TrashEntry?> ReadTrashEntryAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, BookId, Title, Format, Size, DeletedAt, BookJson, FileJson, BookFileId,
                   TrashPath, OriginalPath, TrashCoverPath, OriginalCoverPath
            FROM LibraryTrash WHERE Id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!Guid.TryParse(reader.GetString(0), out var parsedId)
            || !Guid.TryParse(reader.GetString(2), out var bookId)
            || !Enum.IsDefined(typeof(LibraryTrashItemKind), reader.GetInt32(1)))
            return null;
        return new TrashEntry
        {
            Id = parsedId,
            Kind = (LibraryTrashItemKind)reader.GetInt32(1),
            BookId = bookId,
            Title = reader.GetString(3),
            Format = reader.IsDBNull(4) ? null : reader.GetString(4),
            Size = reader.GetInt64(5),
            DeletedAt = DateTimeOffset.Parse(reader.GetString(6)),
            BookJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            FileJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            BookFileId = !reader.IsDBNull(9) && Guid.TryParse(reader.GetString(9), out var bookFileId)
                ? bookFileId
                : null,
            TrashPath = reader.GetString(10),
            OriginalPath = reader.GetString(11),
            TrashCoverPath = reader.IsDBNull(12) ? null : reader.GetString(12),
            OriginalCoverPath = reader.IsDBNull(13) ? null : reader.GetString(13)
        };
    }

    private async Task RecoverPendingPurgeStagingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(_paths.Trash, PurgeStagingFolderName);
        if (!Directory.Exists(stagingRoot)) return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RecoverPurgeStagingDirectoryAsync(connection, directory, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLibraryTrashRecovery("扫描永久清理暂存目录失败。", exception);
        }
    }

    private async Task RecoverPurgeStagingDirectoryAsync(
        SqliteConnection connection,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(stagingDirectory, PurgeManifestFileName);
        if (!File.Exists(manifestPath))
        {
            LogLibraryTrashRecovery($"永久清理暂存目录缺少清单：{stagingDirectory}");
            return;
        }

        LibraryTrashPurgeManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LibraryTrashPurgeManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                TrashJsonOptions);
        }
        catch (JsonException exception)
        {
            LogLibraryTrashRecovery($"永久清理清单损坏，已跳过：{manifestPath}", exception);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogLibraryTrashRecovery($"读取永久清理清单失败，已跳过：{manifestPath}", exception);
            return;
        }

        if (!IsValidPurgeManifest(manifest, stagingDirectory, out var validationError))
        {
            LogLibraryTrashRecovery($"永久清理清单无效，已跳过：{manifestPath}；{validationError}");
            return;
        }

        var entry = await ReadTrashEntryAsync(connection, manifest!.TrashItemId, cancellationToken);
        if (entry is null)
        {
            TryDeletePurgeStagingDirectory(stagingDirectory, "数据库已提交后的启动恢复");
            return;
        }

        var originalBody = ResolveDataPath(manifest.TrashPath);
        var originalCover = string.IsNullOrWhiteSpace(manifest.TrashCoverPath)
            ? null
            : ResolveDataPath(manifest.TrashCoverPath);
        var stagedBody = GetPurgeStagedBodyPath(stagingDirectory);
        var stagedCover = originalCover is null
            ? null
            : GetPurgeStagedCoverPath(stagingDirectory, originalCover);

        var coverRestored = RestoreStagedPathIfPresent(stagedCover, originalCover, "封面");
        var bodyRestored = RestoreStagedPathIfPresent(stagedBody, originalBody, "书籍内容");
        if (coverRestored && bodyRestored)
            TryDeletePurgeStagingDirectory(stagingDirectory, "数据库未提交时的启动恢复");
        else
            LogLibraryTrashRecovery($"永久清理暂存内容未能完全恢复：{stagingDirectory}");
    }

    private bool IsValidPurgeManifest(
        LibraryTrashPurgeManifest? manifest,
        string stagingDirectory,
        out string error)
    {
        error = string.Empty;
        if (manifest is null)
        {
            error = "清单为空";
            return false;
        }
        if (manifest.Version != 1)
        {
            error = $"不支持的版本 {manifest.Version}";
            return false;
        }
        if (manifest.TrashItemId == Guid.Empty
            || !Guid.TryParseExact(Path.GetFileName(stagingDirectory), "N", out var directoryId)
            || directoryId != manifest.TrashItemId)
        {
            error = "回收站 ID 与目录不一致";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.TrashPath))
        {
            error = "书籍内容路径为空";
            return false;
        }

        try
        {
            _ = ResolveDataPath(manifest.TrashPath);
            if (!string.IsNullOrWhiteSpace(manifest.TrashCoverPath))
                _ = ResolveDataPath(manifest.TrashCoverPath);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    private string GetPurgeStagingDirectory(Guid trashItemId) =>
        Path.Combine(_paths.Trash, PurgeStagingFolderName, trashItemId.ToString("N"));

    private static string GetPurgeStagedBodyPath(string stagingDirectory) =>
        Path.Combine(stagingDirectory, PurgeBodyFileName);

    private static string GetPurgeStagedCoverPath(string stagingDirectory, string originalCoverPath) =>
        Path.Combine(stagingDirectory, PurgeCoverFilePrefix + Path.GetExtension(originalCoverPath));

    private static async Task WritePurgeManifestAsync(
        string stagingDirectory,
        LibraryTrashPurgeManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var manifestPath = Path.Combine(stagingDirectory, PurgeManifestFileName);
        var temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            SettingsFile.Publish(temporaryPath, manifestPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private bool RestoreStagedPurgePath(
        string? stagedPath,
        string? originalPath,
        string label,
        bool wasMoved)
    {
        if (!wasMoved) return true;
        return RestoreStagedPathIfPresent(stagedPath, originalPath, label);
    }

    private bool RestoreStagedPathIfPresent(
        string? stagedPath,
        string? originalPath,
        string label)
    {
        if (stagedPath is null || originalPath is null
            || (!File.Exists(stagedPath) && !Directory.Exists(stagedPath)))
            return true;

        try
        {
            if (File.Exists(originalPath) || Directory.Exists(originalPath))
            {
                LogLibraryTrashRecovery($"{label}恢复目标已存在，暂存内容保持在：{stagedPath}");
                return false;
            }
            MovePath(stagedPath, originalPath);
            return true;
        }
        catch (Exception exception)
        {
            LogLibraryTrashRecovery($"{label}暂存内容恢复失败：{stagedPath} -> {originalPath}", exception);
            return false;
        }
    }

    private void RollbackMovedPath(
        string? movedSource,
        string? rollbackTarget,
        bool wasMoved,
        string operation)
    {
        if (!wasMoved || movedSource is null || rollbackTarget is null
            || (!File.Exists(movedSource) && !Directory.Exists(movedSource)))
            return;

        try
        {
            MovePath(movedSource, rollbackTarget);
        }
        catch (Exception exception)
        {
            LogLibraryTrashRecovery($"{operation}回滚失败：{movedSource} -> {rollbackTarget}", exception);
        }
    }

    private bool TryDeletePurgeStagingDirectory(string stagingDirectory, string operation)
    {
        try
        {
            DeletePath(stagingDirectory);
            return true;
        }
        catch (Exception exception)
        {
            LogLibraryTrashRecovery($"{operation}失败，将在下次启动时继续处理：{stagingDirectory}", exception);
            return false;
        }
    }

    private void LogLibraryTrashRecovery(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            var line = $"[{DateTimeOffset.UtcNow:O}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            line += Environment.NewLine;
            lock (_trashRecoveryLogGate)
            {
                File.AppendAllText(
                    Path.Combine(_paths.Logs, "library-trash-recovery.log"),
                    line,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Recovery logging must never replace the original data error.
        }
    }

    private static void EnsureMoveTargetAvailable(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"恢复目标已存在：{path}");
    }

    private static bool MovePath(string source, string destination)
    {
        if (!File.Exists(source) && !Directory.Exists(source)) return false;
        EnsureMoveTargetAvailable(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source))
            File.Move(source, destination);
        else
            Directory.Move(source, destination);
        return true;
    }

    private static void MovePathBack(string source, string destination)
    {
        try
        {
            if (File.Exists(source) || Directory.Exists(source))
                MovePath(source, destination);
        }
        catch
        {
            // Keep the original exception from the database operation. A
            // subsequent startup can still expose the manifest for recovery.
        }
    }

    private static void DeletePath(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private sealed record FileRenamePlan(
        BookFile File,
        string SourcePath,
        string TargetPath,
        string TargetRelativePath);

    private sealed class FileRenameState(FileRenamePlan plan, string temporaryPath)
    {
        public FileRenamePlan Plan { get; } = plan;
        public string TemporaryPath { get; } = temporaryPath;
        public bool TargetMoved { get; set; }
    }

    private sealed class TrashEntry
    {
        public Guid Id { get; init; }
        public LibraryTrashItemKind Kind { get; init; }
        public Guid BookId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Format { get; init; }
        public long Size { get; init; }
        public DateTimeOffset DeletedAt { get; init; }
        public string? BookJson { get; init; }
        public string? FileJson { get; init; }
        public Guid? BookFileId { get; init; }
        public string TrashPath { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public string? TrashCoverPath { get; init; }
        public string? OriginalCoverPath { get; init; }
    }
}
