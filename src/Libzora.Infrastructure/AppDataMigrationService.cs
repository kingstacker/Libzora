using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed partial class AppDataMigrationService
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

                // Record what the snapshot actually contains. If the process
                // dies after the move but before app-root.json is published,
                // recovery may only adopt this target when the old root still
                // matches; otherwise the user has kept using it and the copy
                // is now stale.
                journal.SourceFingerprint = ComputeSourceFingerprint(copied.Files);
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
                MarkRetiredRoots(sourceRoot, target);
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
}
