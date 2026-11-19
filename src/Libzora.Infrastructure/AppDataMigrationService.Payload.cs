using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Payload collection, copying and database snapshot.
/// </summary>
public sealed partial class AppDataMigrationService
{
    private static List<PayloadFile> CollectPayloadFiles(
        string sourceRoot,
        string? stageRoot,
        CancellationToken cancellationToken)
    {
        var files = new List<PayloadFile>();
        foreach (var name in PayloadDirectoryNames)
        {
            var sourceDirectory = Path.Combine(sourceRoot, name);
            if (!Directory.Exists(sourceDirectory)) continue;
            CollectPayloadFiles(sourceDirectory, name, stageRoot, files, cancellationToken);
        }
        return files;
    }

    private static void CollectPayloadFiles(
        string sourceDirectory,
        string relativeDirectory,
        string? stageRoot,
        List<PayloadFile> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stageRoot is not null)
        {
            var destinationDirectory = Path.Combine(
                stageRoot,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(destinationDirectory);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            var relativePath = relativeDirectory + "/" + name;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"数据目录包含符号链接，无法安全迁移：{relativePath}");

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (ShouldSkipDirectory(name) || IsVolatilePayloadDirectory(relativePath)) continue;
                CollectPayloadFiles(entry, relativePath, stageRoot, files, cancellationToken);
            }
            else if (!ShouldSkipFile(name))
            {
                var info = new FileInfo(entry);
                files.Add(new PayloadFile(entry, relativePath, info.Length, info.LastWriteTimeUtc));
            }
        }
    }

    private static void EnsurePayloadUnchanged(
        IReadOnlyList<PayloadFile> expected,
        IReadOnlyList<PayloadFile> actual,
        IReadOnlyCollection<string> snapshotDatabases)
    {
        var current = actual.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        foreach (var file in expected)
        {
            // Regenerable caches may be touched or recreated by browsing while
            // the copy is running. They are copied best-effort; a stale cache
            // is harmless.
            if (IsVolatilePayloadFile(file.RelativePath)) continue;
            if (!current.TryGetValue(file.RelativePath, out var now))
                throw new IOException(
                    $"迁移期间数据文件被删除或移走：{file.RelativePath}。"
                    + "请停止导入或插件写入后重试。");
            // SQLite files are copied through the backup API, which can touch
            // headers/WAL state while taking its consistent snapshot. Writers
            // to them are serialized by AppDataProcessLock, so timestamp drift
            // here is our own read, not an external mutation.
            if (snapshotDatabases.Contains(file.RelativePath, StringComparer.Ordinal)) continue;
            if (now.Length != file.Length || now.LastWriteTimeUtc != file.LastWriteTimeUtc)
                throw new IOException(
                    $"迁移期间数据文件发生变化：{file.RelativePath}。"
                    + "请停止导入或插件写入后重试。");
        }

        if (actual.Count == expected.Count) return;
        var known = expected.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var added = actual.FirstOrDefault(file =>
            !known.Contains(file.RelativePath) && !IsVolatilePayloadFile(file.RelativePath));
        if (added is null) return;
        throw new IOException(
            $"迁移期间新增了数据文件：{added.RelativePath}。"
            + "请停止导入或插件写入后重试。");
    }

    private static bool IsVolatilePayloadDirectory(string relativePath) =>
        // Diagnostics are not part of the library snapshot and are written
        // continuously by the running process.
        string.Equals(relativePath, "data/logs", StringComparison.OrdinalIgnoreCase);

    private static bool IsVolatilePayloadFile(string relativePath) =>
        relativePath.StartsWith("data/covers/", StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith("data/reader-cache/", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipDirectory(string name) =>
        name.StartsWith(".libzora-export-", StringComparison.Ordinal)
        || name.StartsWith(".libzora-import-", StringComparison.Ordinal)
        || name.StartsWith(".libzora-rollback-", StringComparison.Ordinal)
        || name.StartsWith(StageDirectoryPrefix, StringComparison.Ordinal);

    private static bool ShouldSkipFile(string name) =>
        name == ".libzora-data.lock"
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".part.plain", StringComparison.OrdinalIgnoreCase)
        // SQLite databases are copied through the backup API. Copying stale
        // sidecars next to the snapshot would replay or corrupt old WAL data.
        || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".db-journal", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsSqliteDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous);
        var header = new byte[16];
        var read = await stream.ReadAsync(header.AsMemory(), cancellationToken);
        return read == header.Length && header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            81920, FileOptions.Asynchronous);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await source.CopyToAsync(destination, 81920, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        try { File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task SnapshotDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        TryDeleteFile(destinationPath);
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        // SQLite's backup API reads a consistent committed view including WAL
        // contents. Copying only libzora.db would silently lose the active
        // WAL, so every SQLite file in the payload uses this path.
        var retryTimer = Stopwatch.StartNew();
        var retryTimeout = TimeSpan.FromSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retryTimer.Elapsed >= retryTimeout) throw;
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
