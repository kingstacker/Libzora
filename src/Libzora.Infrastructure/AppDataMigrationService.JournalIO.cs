using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Journal persistence, validation and cleanup.
/// </summary>
public sealed partial class AppDataMigrationService
{
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
}
