using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Journal recovery and source fingerprints.
/// </summary>
public sealed partial class AppDataMigrationService
{
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

        // The old root remained authoritative until app-root.json was
        // published, so the user (or another process) may have added books
        // after the copy finished. Re-publish only if the source still matches
        // the snapshot the journal describes; otherwise discard this target
        // and copy the current library again.
        if (!string.IsNullOrWhiteSpace(journal.SourceFingerprint)
            && Directory.Exists(journal.SourceRoot)
            && !SourceMatchesFingerprint(journal.SourceRoot, journal.SourceFingerprint, cancellationToken))
        {
            progress?.Report(new AppDataMigrationProgress(0.05, "旧数据目录在迁移后已发生变化，正在重新迁移…"));
            return null;
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
        MarkRetiredRoots(journal.SourceRoot, target);
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

    private static void MarkRetiredRoots(string retiredRoot, string activeRoot)
    {
        foreach (var root in EnumerateLockRoots(retiredRoot).Distinct(PathComparer))
            AppRootConfiguration.MarkMigrated(root, activeRoot);
    }

    internal static string ComputeSourceFingerprint(string sourceRoot, CancellationToken cancellationToken) =>
        ComputeSourceFingerprint(CollectPayloadFiles(sourceRoot, stageRoot: null, cancellationToken));

    private static string ComputeSourceFingerprint(IEnumerable<PayloadFile> files)
    {
        // Volatile caches are rewritten by ordinary browsing and would make
        // every recovery look stale. Everything else uses size + timestamp,
        // which is enough to detect imports, edits and deletions.
        var builder = new StringBuilder();
        foreach (var file in files
                     .Where(file => !IsVolatilePayloadFile(file.RelativePath))
                     .OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath)
                .Append('|')
                .Append(file.Length)
                .Append('|')
                .Append(file.LastWriteTimeUtc.Ticks)
                .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool SourceMatchesFingerprint(
        string sourceRoot,
        string expectedFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            var actual = ComputeSourceFingerprint(
                CollectPayloadFiles(sourceRoot, stageRoot: null, cancellationToken));
            return string.Equals(actual, expectedFingerprint, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            // A vanished transient file must not make recovery publish a stale
            // target; treat an unreadable source as changed.
            return false;
        }
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
}
