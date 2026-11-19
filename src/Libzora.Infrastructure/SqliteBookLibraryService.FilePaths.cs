using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Data path resolution, rename planning and file moves.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    public string GetAbsoluteFilePath(BookFile file) => ResolveDataPath(file.RelativePath);

    private string ResolveDataPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_paths.Data, relativePath));
        var dataRoot = Path.GetFullPath(_paths.Data + Path.DirectorySeparatorChar);
        if (!full.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("书籍路径不在应用数据目录内。");
        return full;
    }

    private IReadOnlyList<FileRenamePlan> BuildBookRenamePlan(Book book, string title)
    {
        var sourcePaths = book.Files
            .Select(file => ResolveDataPath(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<FileRenamePlan>(book.Files.Count);

        foreach (var file in book.Files
                     .OrderBy(item => item.Format, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Id))
        {
            var sourcePath = ResolveDataPath(file.RelativePath);
            var directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("书籍文件目录无效。");

            var desiredName = BookFileNamePolicy.CreateRenamedFileName(
                Path.GetFileName(sourcePath),
                title);
            var targetPath = GetAvailableRenameTarget(
                directory,
                desiredName,
                sourcePaths,
                plannedTargets);
            plannedTargets.Add(targetPath);
            plan.Add(new FileRenamePlan(
                file,
                sourcePath,
                targetPath,
                Path.GetRelativePath(_paths.Data, targetPath)));
        }

        return plan;
    }

    private static string GetAvailableRenameTarget(
        string directory,
        string desiredName,
        IReadOnlySet<string> sourcePaths,
        IReadOnlySet<string> plannedTargets)
    {
        var stem = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);
        for (var index = 1; ; index++)
        {
            var name = index == 1 ? desiredName : $"{stem} ({index}){extension}";
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (plannedTargets.Contains(path)) continue;
            if ((File.Exists(path) || Directory.Exists(path)) && !sourcePaths.Contains(path)) continue;
            return path;
        }
    }

    private static void MoveFilesToTemporaryPaths(
        IReadOnlyList<FileRenamePlan> plan,
        ICollection<FileRenameState> moves,
        CancellationToken cancellationToken)
    {
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(item.SourcePath, item.TargetPath, StringComparison.Ordinal)) continue;
            if (!File.Exists(item.SourcePath)) continue;

            string temporaryPath;
            do
            {
                temporaryPath = item.SourcePath + $".libzora-rename-{Guid.NewGuid():N}.tmp";
            }
            while (File.Exists(temporaryPath) || Directory.Exists(temporaryPath));

            File.Move(item.SourcePath, temporaryPath);
            moves.Add(new FileRenameState(item, temporaryPath));
        }
    }

    private static void CompleteFileMoves(
        IReadOnlyList<FileRenameState> moves,
        CancellationToken cancellationToken)
    {
        foreach (var move in moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(move.TemporaryPath, move.Plan.TargetPath);
            move.TargetMoved = true;
        }
    }

    private static void RestoreFileMoves(IEnumerable<FileRenameState> moves)
    {
        foreach (var move in moves.Reverse())
        {
            try
            {
                if (move.TargetMoved && File.Exists(move.Plan.TargetPath))
                    File.Move(move.Plan.TargetPath, move.Plan.SourcePath);
                else if (File.Exists(move.TemporaryPath))
                    File.Move(move.TemporaryPath, move.Plan.SourcePath);
            }
            catch
            {
                // Preserve the original rename/database exception. The next
                // startup can still expose any unusual filesystem residue.
            }
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.CreateFunction<string?, string?>(
            "unicode_lower",
            static value => value?.ToLowerInvariant(),
            isDeterministic: true);
        using var pragma = connection.CreateCommand();
        // synchronous=NORMAL is the recommended WAL pairing: it keeps commits
        // durable across application crashes while removing an fsync per write.
        // The page cache and in-memory temp store keep large library scans and
        // sorts off the disk.
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -65536;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static IEnumerable<string> ExpandInputFiles(IEnumerable<string> paths)
    {
        foreach (var input in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (File.Exists(input) && SupportedExtensions.Contains(Path.GetExtension(input)))
            {
                yield return Path.GetFullPath(input);
                continue;
            }

            if (Directory.Exists(input))
            {
                foreach (var file in EnumerateSupportedFiles(input)
                    .Where(x => SupportedExtensions.Contains(Path.GetExtension(x))))
                    yield return Path.GetFullPath(file);
            }
        }
    }

    private static IEnumerable<string> EnumerateSupportedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
                yield return file;

            string[] children;
            try { children = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
    }

    private static long GetFileLengthSafe(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static string GetUniqueFileName(string directory, string originalName)
    {
        var safe = SanitizeFileName(originalName);
        var candidate = Path.Combine(directory, safe);
        if (!File.Exists(candidate)) return safe;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        for (var index = 2; ; index++)
        {
            var name = $"{stem} ({index}){extension}";
            if (!File.Exists(Path.Combine(directory, name))) return name;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        var result = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "book.bin" : result;
    }

    private static string NormalizeCoverExtension(string? extension)
    {
        var value = extension?.Trim().ToLowerInvariant() ?? ".jpg";
        return value is ".jpg" or ".jpeg" or ".png" or ".webp" ? value : ".jpg";
    }

    private async Task CopyFileAsync(string source, string target, long fileLength, long completed, long total, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new TransferProgress(completed + copied, total, $"正在复制 {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
        if (copied != fileLength) throw new IOException("复制后的文件大小不一致。");
    }
}
