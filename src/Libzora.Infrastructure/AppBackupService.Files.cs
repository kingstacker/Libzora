using System.IO.Compression;
using System.Text.Json;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Rollback, entry enumeration and path helpers.
/// </summary>
public sealed partial class AppBackupService
{
    private void MoveCurrentDataToRollback(string rollbackRoot, ImportJournal journal)
    {
        foreach (var name in ManagedImportPaths)
        {
            if (name == ReadingMaterialCoversDirectory && !journal.ReplaceReadingMaterialCovers) continue;
            var source = Path.Combine(_paths.Data, name);
            if (!File.Exists(source) && !Directory.Exists(source)) continue;
            var backup = Path.Combine(rollbackRoot, name);
            MoveIfExists(source, backup);
        }
    }

    /// <summary>Files that belong in a backup; partial downloads are skipped.</summary>
    private static IEnumerable<string> EnumerateExportFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            : [];

    private static string RelativeEntryPath(string directory, string filePath) =>
        Path.GetRelativePath(directory, filePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static async Task<long> AddFileEntryAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, 81920, cancellationToken);
        return source.Length;
    }

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task ExtractSupportedEntriesAsync(
        ZipArchive archive,
        string stagingRoot,
        CancellationToken cancellationToken,
        Action<long>? onEntryExtracted = null)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = NormalizeEntryName(entry.FullName);
            if (normalizedName.Length == 0 || normalizedName.EndsWith('/')) continue;
            if (!IsSupportedEntry(normalizedName)) continue;

            var destinationPath = Path.GetFullPath(Path.Combine(
                stagingRoot,
                normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            EnsureContainedPath(stagingRoot, destinationPath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await source.CopyToAsync(destination, 81920, cancellationToken);
            onEntryExtracted?.Invoke(entry.Length);
        }
    }

    private static bool IsSupportedEntry(string entryName) =>
        entryName.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(DatabaseEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(DatabaseWalEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(DatabaseShmEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.Equals(SettingsEntryName, StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("library/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("covers/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith(ReadingMaterialCoversDirectory + "/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("trash/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEntryName(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("备份包包含无效路径。 ");
        return string.Join('/', segments);
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (!string.Equals(manifest.Format, BackupFormat, StringComparison.Ordinal))
            throw new InvalidDataException("这不是 Libzora 备份包。 ");
        if (manifest.Version != CurrentFormatVersion)
            throw new InvalidDataException($"备份包版本 {manifest.Version} 不受当前版本支持。 ");
        if (manifest.BookCount < 0 || manifest.FileCount < 0)
            throw new InvalidDataException("备份包清单中的数量无效。 ");
    }

    private string CreateWorkingDirectory(string prefix)
    {
        // Directory.Move cannot cross volumes. Keep staging and rollback next
        // to the managed directories, even when data lives on another drive.
        var path = Path.Combine(_paths.Data, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private void EnsureBackupOutsideDataDirectory(string path)
    {
        var dataRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(_paths.Data));
        if (path.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份文件请保存到应用数据目录以外。 ");
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var rootWithSeparator = EnsureTrailingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份包包含越界路径。 ");
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: false);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeletePath(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup is best effort and must not hide the actual backup error.
        }
    }

    private sealed class BackupManifest
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int BookCount { get; set; }
        public int FileCount { get; set; }
    }

    private sealed class BackupSettings
    {
        public BackupAiSettings? Ai { get; set; }
        public BackupKindleEmailSettings? KindleEmail { get; set; }
        public BackupS3Settings? S3 { get; set; }
    }

    private sealed class BackupAiSettings
    {
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
    }

    private sealed class BackupKindleEmailSettings
    {
        public string? KindleEmailAddress { get; set; }
        public string? SenderEmailAddress { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public string? SmtpUsername { get; set; }
        public bool EnableSsl { get; set; } = true;
    }

    private sealed class BackupS3Settings
    {
        public SyncProvider Provider { get; set; }
        public bool ProviderChosenByUser { get; set; }
        public bool Enabled { get; set; }
        public bool AutomaticSyncEnabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 30;
        public string? Endpoint { get; set; }
        public string? WebDavEndpoint { get; set; }
        public bool WebDavAuthenticationRequired { get; set; }
        public string? Bucket { get; set; }
        public string? Region { get; set; }
        public bool PathStyle { get; set; }
        public bool SkipTlsVerify { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        public int ConcurrentRequests { get; set; } = 4;
        public string? Prefix { get; set; }
        public bool EncryptionEnabled { get; set; }
    }
}
