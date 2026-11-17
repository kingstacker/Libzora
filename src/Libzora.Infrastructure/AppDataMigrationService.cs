using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>Coarse progress for a data-root migration; fraction is 0..1.</summary>
public sealed record AppDataMigrationProgress(double Fraction, string Message);

/// <summary>Result of a completed migration. The app must be restarted to use the new root.</summary>
public sealed record AppDataMigrationResult(
    string SourceRoot,
    string TargetRoot,
    int BookCount,
    int FileCount,
    long CopiedBytes,
    bool RestartRequired);

/// <summary>
/// Moves an installation's complete data roots to a new directory.
///
/// The migration never publishes app-root.json until a full, startable copy
/// has been written and verified in the target directory. The old root is left
/// untouched on failure or cancellation. Protected local settings and secret
/// stores are copied as files; this is a same-machine move, not a portable
/// backup, so credentials remain decryptable by the platform secret store.
/// </summary>
public sealed class AppDataMigrationService
{
    internal const string JournalFileName = ".libzora-migration-state.json";
    internal const string StageDirectoryPrefix = ".libzora-migration-stage-";
    private const int JournalVersion = 1;
    private const string LegacyPackageFileName = ".libzora-migration.libzora";

    // Browser sessions are per installation and can be recreated; every
    // library/settings/plugin payload that cannot be regenerated is included.
    private static readonly string[] PayloadDirectoryNames =
        ["data", "regions", "backups", "plugins", "plugin-data"];

    internal static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AppPaths _sourcePaths;
    private readonly string _sourceRoot;
    private readonly string _configurationDirectory;
    private readonly string _activeDataRelativePath;

    public AppDataMigrationService(AppPaths sourcePaths, string? configurationDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        _sourcePaths = sourcePaths;
        _sourceRoot = ResolvePhysicalPath(sourcePaths.InstallationRoot);
        _configurationDirectory = Path.GetFullPath(configurationDirectory ?? AppContext.BaseDirectory);
        _activeDataRelativePath = Path.GetRelativePath(
            _sourceRoot,
            ResolvePhysicalPath(sourcePaths.Data)).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.IsPathRooted(_activeDataRelativePath)
            || _activeDataRelativePath.StartsWith("../", StringComparison.Ordinal)
            || _activeDataRelativePath == "..")
            throw new InvalidOperationException("当前地区数据目录不在 Libzora 安装根目录内，无法迁移。");
    }

    public async Task<AppDataMigrationResult> MigrateAsync(
        string targetRoot,
        IProgress<AppDataMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
            throw new ArgumentException("请选择新的数据目录。", nameof(targetRoot));

        var sourceRoot = _sourceRoot;
        var target = ResolvePhysicalPath(Path.GetFullPath(targetRoot));
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException("当前 Libzora 数据目录不存在，无法迁移。");
        if (PathsEqual(sourceRoot, target))
            throw new InvalidOperationException("所选目录已经是当前数据目录。");
        if (IsNested(sourceRoot, target) || IsNested(target, sourceRoot))
            throw new InvalidOperationException("目标目录不能位于当前数据目录内部，也不能包含当前数据目录。");

        // Writers such as font and dictionary imports do not take the
        // AppDataProcessLock. Mark the source as migrating so those entry
        // points fail fast instead of racing the copy. Register both the
        // physical and the lexical installation roots: on macOS /var is a
        // symlink to /private/var, and AppPaths may hold either spelling.
        using var migrationState = DataRootMigrationState.Begin(
            sourceRoot,
            _sourcePaths.InstallationRoot);

        progress?.Report(new AppDataMigrationProgress(0, "正在检查迁移目标…"));
        Directory.CreateDirectory(target);

        var journalPath = Path.Combine(target, JournalFileName);
        var legacyPath = AppRootConfiguration.MigrationBackupPath(target);
        MigrationJournal? journal = null;
        var stagePath = string.Empty;
        var movedEntries = new List<string>();
        var committed = false;
        try
        {
            var existing = await ReadJournalAsync(journalPath, cancellationToken);
            if (existing is not null)
            {
                ValidateJournal(existing, sourceRoot, target, journalPath);
                var recovered = await TryCompleteJournalAsync(
                    existing, target, journalPath, legacyPath, progress, cancellationToken);
                if (recovered is not null) return recovered;
                CleanupJournalArtifacts(target, journalPath, existing);
            }

            TryDeleteFile(journalPath + ".tmp");
            CleanupOrphanedStageDirectories(target, legacyPath, journalPath);
            EnsureTargetContainsOnlyLegacyPackage(target, legacyPath, journalPath);

            journal = new MigrationJournal
            {
                Phase = MigrationPhase.Preparing,
                SourceRoot = sourceRoot,
                TargetRoot = target,
                StageDirectory = StageDirectoryPrefix + Guid.NewGuid().ToString("N"),
                ActiveDataRelativePath = _activeDataRelativePath
            };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            stagePath = Path.Combine(target, journal.StageDirectory);
            Directory.CreateDirectory(stagePath);

            var leases = new List<AppDataProcessLock>();
            try
            {
                foreach (var root in EnumerateLockRoots(sourceRoot)
                             .Distinct(PathComparer)
                             .OrderBy(path => path, PathComparer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    leases.Add(await AppDataProcessLock.AcquireAsync(
                        new AppPaths(root, sourceRoot), cancellationToken));
                }

                journal.Phase = MigrationPhase.Copying;
                await WriteJournalAsync(journalPath, journal, cancellationToken);
                var copied = await CopyPayloadAsync(
                    sourceRoot, stagePath, journal, progress, cancellationToken);
                journal.CopiedBytes = copied.CopiedBytes;

                cancellationToken.ThrowIfCancellationRequested();
                journal.Phase = MigrationPhase.Verifying;
                await WriteJournalAsync(journalPath, journal, cancellationToken);
                progress?.Report(new AppDataMigrationProgress(0.9, "正在校验迁移数据…"));
                var (bookCount, fileCount) = await VerifyPayloadAsync(
                    stagePath,
                    journal.CopiedDatabasePaths,
                    copied.Files.Select(file => file.RelativePath).ToList(),
                    journal.ActiveDataRelativePath,
                    cancellationToken);
                journal.BookCount = bookCount;
                journal.FileCount = fileCount;

                // Font/dictionary/settings writes bypass the process lock. If
                // any payload changed or appeared while we were copying, the
                // target is not a complete snapshot; abort before publishing
                // app-root.json so the old root remains authoritative.
                EnsurePayloadUnchanged(
                    copied.Files,
                    CollectPayloadFiles(sourceRoot, stageRoot: null, cancellationToken),
                    journal.CopiedDatabasePaths);

                journal.PayloadEntries = PayloadDirectoryNames
                    .Where(name => Directory.Exists(Path.Combine(stagePath, name)))
                    .ToList();
                journal.Phase = MigrationPhase.Committing;
                await WriteJournalAsync(journalPath, journal, cancellationToken);
                progress?.Report(new AppDataMigrationProgress(0.96, "正在切换数据目录…"));
                foreach (var name in journal.PayloadEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(target, name);
                    if (PathExists(destination))
                        throw new IOException($"目标目录中已存在 {name}，无法完成迁移。");
                    Directory.Move(Path.Combine(stagePath, name), destination);
                    movedEntries.Add(name);
                }

                journal.Phase = MigrationPhase.Committed;
                await WriteJournalAsync(journalPath, journal, cancellationToken);
                // This is the only point where the active root changes. A crash
                // before this line leaves the old root in use.
                AppRootConfiguration.Save(_configurationDirectory, target, sourceRoot);
                committed = true;
                TryDeleteFile(legacyPath);
                CleanupCompletedArtifacts(target, journalPath, stagePath);
                progress?.Report(new AppDataMigrationProgress(1, "迁移完成，重启 Libzora 后生效。"));
                return new AppDataMigrationResult(
                    sourceRoot,
                    target,
                    journal.BookCount,
                    journal.FileCount,
                    journal.CopiedBytes,
                    RestartRequired: true);
            }
            finally
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                    leases[index].Dispose();
            }
        }
        catch
        {
            // A foreign/unreadable journal or target conflict must not make us
            // delete anything. Only clean artifacts this call created.
            if (!committed && (journal is not null
                               || !string.IsNullOrEmpty(stagePath)
                               || movedEntries.Count > 0))
                CleanupFailedMigration(target, stagePath, movedEntries, journalPath);
            throw;
        }
    }

    /// <summary>
    /// Resolves symlinks in every existing path component. The result is used
    /// for nesting checks and as the durable target path in app-root.json, so
    /// two spellings of the same directory cannot bypass validation.
    /// </summary>
    internal static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : null;
                if (info?.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (IOException)
            {
                // A dangling link cannot be followed; keep the lexical path so
                // the later directory checks report a useful error.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return current;
    }

    private async Task<AppDataMigrationResult?> TryCompleteJournalAsync(
        MigrationJournal journal,
        string target,
        string journalPath,
        string legacyPath,
        IProgress<AppDataMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (journal.Phase is not (MigrationPhase.Committing or MigrationPhase.Committed))
            return null;

        // A crash can happen between moving individual top-level payloads.
        // Verify only after every promised payload entry is present.
        if (journal.PayloadEntries.Any(name => !PathExists(Path.Combine(target, name))))
        {
            if (journal.Phase == MigrationPhase.Committing) return null;
            throw new InvalidDataException(
                "上次迁移已标记完成，但目标数据目录不完整。旧数据目录仍然保留，请选择新的空目录重试。");
        }

        try
        {
            progress?.Report(new AppDataMigrationProgress(0.9, "正在校验上次迁移的数据…"));
            var (bookCount, fileCount) = await VerifyPayloadAsync(
                target,
                journal.CopiedDatabasePaths,
                copiedFiles: null,
                journal.ActiveDataRelativePath,
                cancellationToken);
            journal.BookCount = bookCount;
            journal.FileCount = fileCount;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SqliteException)
        {
            if (journal.Phase == MigrationPhase.Committing)
            {
                // The move was interrupted. The journal proves this target was
                // ours and had no user data, so restarting from the old root is safe.
                return null;
            }
            throw new InvalidDataException(
                "上次迁移已标记完成，但目标数据校验失败。旧数据目录仍然保留，请选择新的空目录重试。",
                exception);
        }

        AppRootConfiguration.Save(_configurationDirectory, target, journal.SourceRoot);
        TryDeleteFile(legacyPath);
        CleanupCompletedArtifacts(target, journalPath, Path.Combine(target, journal.StageDirectory));
        progress?.Report(new AppDataMigrationProgress(1, "迁移完成，重启 Libzora 后生效。"));
        return new AppDataMigrationResult(
            journal.SourceRoot,
            target,
            journal.BookCount,
            journal.FileCount,
            journal.CopiedBytes,
            RestartRequired: true);
    }

    private static IEnumerable<string> EnumerateLockRoots(string sourceRoot)
    {
        yield return sourceRoot;
        var regions = Path.Combine(sourceRoot, "regions");
        if (!Directory.Exists(regions)) yield break;
        foreach (var region in Directory.EnumerateDirectories(regions))
            yield return region;
    }

    private async Task<(long CopiedBytes, IReadOnlyList<PayloadFile> Files)> CopyPayloadAsync(
        string sourceRoot,
        string stageRoot,
        MigrationJournal journal,
        IProgress<AppDataMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = CollectPayloadFiles(sourceRoot, stageRoot, cancellationToken);

        var totalBytes = files.Sum(file => file.Length);
        var copiedBytes = 0L;
        progress?.Report(new AppDataMigrationProgress(0.03, "正在复制数据…"));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(
                stageRoot,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var destinationDirectory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new IOException($"迁移文件路径无效：{file.RelativePath}");
            Directory.CreateDirectory(destinationDirectory);

            if (await IsSqliteDatabaseAsync(file.SourcePath, cancellationToken))
            {
                await SnapshotDatabaseAsync(file.SourcePath, destination, cancellationToken);
                if (!journal.CopiedDatabasePaths.Contains(file.RelativePath, StringComparer.Ordinal))
                    journal.CopiedDatabasePaths.Add(file.RelativePath);
            }
            else
            {
                await CopyFileAsync(file.SourcePath, destination, cancellationToken);
            }

            if (!File.Exists(destination))
                throw new IOException($"迁移文件复制不完整：{file.RelativePath}");
            copiedBytes += file.Length;
            if (totalBytes > 0)
            {
                progress?.Report(new AppDataMigrationProgress(
                    0.03 + 0.85 * Math.Min(1, (double)copiedBytes / totalBytes),
                    "正在复制数据…"));
            }
        }
        return (copiedBytes, files);
    }

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

    private static async Task<(int BookCount, int FileCount)> VerifyPayloadAsync(
        string root,
        IReadOnlyCollection<string> copiedDatabases,
        IReadOnlyCollection<string>? copiedFiles,
        string activeDataRelativePath,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in copiedDatabases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var databasePath = Path.Combine(
                root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(databasePath))
                throw new InvalidDataException($"迁移后的数据库缺失：{relativePath}");
        }

        var requiredLibraries = copiedDatabases
            .Where(path => path.EndsWith("/libzora.db", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "libzora.db", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (requiredLibraries.Length == 0)
            throw new InvalidDataException("迁移数据中缺少书库数据库。");

        var activeDatabase = Path.Combine(
            root,
            activeDataRelativePath.Replace('/', Path.DirectorySeparatorChar),
            "libzora.db");
        if (!File.Exists(activeDatabase))
            throw new InvalidDataException("迁移后的主书库数据库缺失。");

        if (copiedFiles is not null)
        {
            foreach (var relativePath in copiedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(
                    root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    throw new InvalidDataException($"迁移后的数据文件缺失：{relativePath}");
            }
        }

        var (bookCount, fileCount) = (0, 0);
        foreach (var database in FindLibraryDatabases(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = await VerifyDatabaseAsync(database, cancellationToken);
            if (PathsEqual(database, activeDatabase)) (bookCount, fileCount) = counts;
        }
        return (bookCount, fileCount);
    }

    private static IEnumerable<string> FindLibraryDatabases(string root)
    {
        var active = Path.Combine(root, "data", "libzora.db");
        if (File.Exists(active)) yield return active;
        var regions = Path.Combine(root, "regions");
        if (!Directory.Exists(regions)) yield break;
        foreach (var region in Directory.EnumerateDirectories(regions))
        {
            var database = Path.Combine(region, "data", "libzora.db");
            if (File.Exists(database)) yield return database;
        }
    }

    private static async Task<(int BookCount, int FileCount)> VerifyDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException($"迁移后的数据库无法读取：{databasePath}", exception);
        }

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"迁移后的数据库完整性校验失败：{databasePath}");
        }

        if (!await TableExistsAsync(connection, "Books", cancellationToken)
            || !await TableExistsAsync(connection, "BookFiles", cancellationToken))
            return (0, 0);

        var books = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM Books;";
            books = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        var files = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM BookFiles;";
            files = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        return (books, files);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<MigrationJournal?> ReadJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath)) return null;
        try
        {
            await using var stream = new FileStream(
                journalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<MigrationJournal>(
                       stream, JournalJsonOptions, cancellationToken)
                   ?? throw new InvalidDataException($"迁移状态文件为空：{journalPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"迁移状态文件无法读取：{journalPath}", exception);
        }
    }

    private static async Task WriteJournalAsync(
        string journalPath,
        MigrationJournal journal,
        CancellationToken cancellationToken)
    {
        var temporaryPath = journalPath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, JournalJsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static void ValidateJournal(
        MigrationJournal journal,
        string sourceRoot,
        string targetRoot,
        string journalPath)
    {
        var stageName = journal.StageDirectory ?? string.Empty;
        if (journal.Version != JournalVersion
            || !Enum.IsDefined(journal.Phase)
            || string.IsNullOrWhiteSpace(stageName)
            || !stageName.StartsWith(StageDirectoryPrefix, StringComparison.Ordinal)
            || stageName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || !PathsEqual(journal.SourceRoot, sourceRoot)
            || !PathsEqual(journal.TargetRoot, targetRoot)
            || journal.PayloadEntries is null
            || journal.PayloadEntries.Any(name => !PayloadDirectoryNames.Contains(name, StringComparer.Ordinal))
            || journal.CopiedDatabasePaths is null
            || string.IsNullOrWhiteSpace(journal.ActiveDataRelativePath)
            || journal.ActiveDataRelativePath.StartsWith("../", StringComparison.Ordinal)
            || journal.ActiveDataRelativePath == "..")
        {
            throw new InvalidDataException($"目标目录中的迁移状态无效：{journalPath}");
        }
    }

    private static void CleanupOrphanedStageDirectories(
        string target,
        string legacyPath,
        string journalPath)
    {
        // A stage directory is always preceded by a journal write and the
        // journal is removed only after its stage. If a journal was manually
        // removed or cleanup failed after journal deletion, a stage can be the
        // only leftover. Delete such an orphan only when the target contains
        // nothing else except our known artifacts; otherwise leave every entry
        // untouched and let the user-data check below report a clear message.
        var stages = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            if (PathsEqual(entry, legacyPath)
                || PathsEqual(entry, journalPath)
                || PathsEqual(entry, journalPath + ".tmp"))
                continue;

            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0
                && name.StartsWith(StageDirectoryPrefix, StringComparison.Ordinal))
            {
                stages.Add(entry);
                continue;
            }

            // Unknown content means this may be a real user directory. Do not
            // delete anything; EnsureTargetContainsOnlyLegacyPackage will
            // refuse the target without modifying it.
            return;
        }

        foreach (var stage in stages)
            TryDeleteDirectory(stage);
    }

    private static void EnsureTargetContainsOnlyLegacyPackage(
        string target,
        string legacyPath,
        string journalPath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            if (PathsEqual(entry, legacyPath)) continue;
            var name = Path.GetFileName(entry);
            if (PathsEqual(entry, journalPath))
                throw new InvalidOperationException("目标目录包含未完成的迁移状态，请重新选择目录。");
            throw new InvalidOperationException($"目标目录已有数据（{name}），请选择一个空目录。");
        }
    }

    private static void CleanupJournalArtifacts(
        string target,
        string journalPath,
        MigrationJournal journal)
    {
        foreach (var name in journal.PayloadEntries ?? [])
        {
            if (!PayloadDirectoryNames.Contains(name, StringComparer.Ordinal)) continue;
            TryDeletePath(Path.Combine(target, name));
        }
        var stageName = Path.GetFileName(journal.StageDirectory ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(stageName)
            && stageName.StartsWith(StageDirectoryPrefix, StringComparison.Ordinal)
            && string.Equals(stageName, journal.StageDirectory, StringComparison.Ordinal))
        {
            TryDeleteDirectory(Path.Combine(target, stageName));
        }
        TryDeleteFile(journalPath);
        TryDeleteFile(journalPath + ".tmp");
    }

    private static void CleanupCompletedArtifacts(
        string target,
        string journalPath,
        string stagePath)
    {
        if (!string.IsNullOrWhiteSpace(stagePath)
            && Path.GetFileName(stagePath).StartsWith(StageDirectoryPrefix, StringComparison.Ordinal))
            TryDeleteDirectory(stagePath);
        TryDeleteFile(journalPath);
        TryDeleteFile(journalPath + ".tmp");
    }

    private void CleanupFailedMigration(
        string target,
        string stagePath,
        IReadOnlyCollection<string> movedEntries,
        string journalPath)
    {
        foreach (var name in movedEntries)
        {
            if (!PayloadDirectoryNames.Contains(name, StringComparer.Ordinal)) continue;
            var movedPath = Path.Combine(target, name);
            if (!PathExists(movedPath)) continue;
            var stageDestination = string.IsNullOrWhiteSpace(stagePath)
                ? null
                : Path.Combine(stagePath, name);
            try
            {
                if (stageDestination is not null
                    && Directory.Exists(stagePath)
                    && !PathExists(stageDestination))
                {
                    Directory.Move(movedPath, stageDestination);
                    continue;
                }
            }
            catch (IOException)
            {
            }
            TryDeletePath(movedPath);
        }
        if (!string.IsNullOrWhiteSpace(stagePath))
            TryDeleteDirectory(stagePath);
        TryDeleteFile(journalPath);
        TryDeleteFile(journalPath + ".tmp");
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(
            TrimSeparators(left),
            TrimSeparators(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsNested(string parent, string child) =>
        TrimSeparators(child).StartsWith(
            TrimSeparators(parent) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string TrimSeparators(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private static void TryDeletePath(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PayloadFile(
        string SourcePath,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);

    internal enum MigrationPhase
    {
        Preparing,
        Copying,
        Verifying,
        Committing,
        Committed
    }

    internal sealed class MigrationJournal
    {
        public int Version { get; set; } = JournalVersion;
        public MigrationPhase Phase { get; set; }
        public string SourceRoot { get; set; } = string.Empty;
        public string TargetRoot { get; set; } = string.Empty;
        public string StageDirectory { get; set; } = string.Empty;
        public string ActiveDataRelativePath { get; set; } = "data";
        public List<string> PayloadEntries { get; set; } = [];
        public List<string> CopiedDatabasePaths { get; set; } = [];
        public int BookCount { get; set; }
        public int FileCount { get; set; }
        public long CopiedBytes { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
