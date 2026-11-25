using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Manages local pre-write backups of Kindle's ksdk_annotation_v1.db so users
/// can inspect and roll back to any previous snapshot safely.
/// </summary>
public static class KindleAnnotationBackupStore
{
    public const int MaxRetainedBackups = 3;
    private const string BackupFileName = "ksdk_annotation_v1.db";
    private const string ManifestFileName = "backup.json";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true
    };

    public static string ComputeDeviceKey(KindleDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(device.Identity))).ToLowerInvariant()[..16];
    }

    public static void SaveManifest(
        string localBackupPath,
        KindleDevice device,
        string databaseRelativePath,
        string sha256,
        string? deviceBackupPath = null,
        string? reason = null,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(localBackupPath) || !File.Exists(localBackupPath)) return;
        var directory = Path.GetDirectoryName(localBackupPath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        var info = new FileInfo(localBackupPath);
        var manifest = new BackupManifest(
            databaseRelativePath.Replace('\\', '/'),
            sha256.ToLowerInvariant(),
            createdAt ?? DateTimeOffset.UtcNow,
            device.Name,
            info.Length,
            deviceBackupPath?.Replace('\\', '/'),
            reason);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson), Encoding.UTF8);
        var deviceRoot = Path.GetDirectoryName(directory);
        if (!string.IsNullOrWhiteSpace(deviceRoot))
            PruneOldBackups(deviceRoot, MaxRetainedBackups);
    }

    public static void PruneOldBackups(string deviceBackupRoot, int maxRetained = MaxRetainedBackups)
    {
        if (maxRetained <= 0 || !Directory.Exists(deviceBackupRoot)) return;
        var directories = Directory.EnumerateDirectories(deviceBackupRoot)
            .Select(dir => (Dir: dir, CreatedAt: ReadBackupTimestamp(dir)))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => Path.GetFileName(item.Dir), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var stale in directories.Skip(maxRetained))
        {
            try { Directory.Delete(stale.Dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static IReadOnlyList<KindleAnnotationBackupEntry> ListBackups(AppPaths paths, KindleDevice device)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(device);
        var root = Path.Combine(paths.Backups, "KindleAnnotations", ComputeDeviceKey(device));
        if (!Directory.Exists(root)) return [];

        var entries = new List<KindleAnnotationBackupEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var dbPath = Path.Combine(directory, BackupFileName);
            if (!File.Exists(dbPath)) continue;
            var info = new FileInfo(dbPath);
            var manifest = TryReadManifest(directory);
            var createdAt = manifest?.CreatedAt
                ?? ParseFolderTimestamp(Path.GetFileName(directory))
                ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            entries.Add(new KindleAnnotationBackupEntry(
                dbPath,
                manifest?.DatabaseRelativePath ?? string.Empty,
                manifest?.Sha256 ?? string.Empty,
                createdAt,
                manifest?.DeviceName ?? device.Name,
                info.Length,
                manifest?.DeviceBackupPath,
                manifest?.Reason));
        }

        return entries
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => Path.GetFileName(Path.GetDirectoryName(entry.LocalBackupPath)), StringComparer.OrdinalIgnoreCase)
            .Take(MaxRetainedBackups)
            .ToArray();
    }

    private static DateTimeOffset ReadBackupTimestamp(string directory)
    {
        var manifest = TryReadManifest(directory);
        if (manifest is not null) return manifest.CreatedAt;
        if (ParseFolderTimestamp(Path.GetFileName(directory)) is { } parsed) return parsed;
        var dbPath = Path.Combine(directory, BackupFileName);
        return File.Exists(dbPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(dbPath), TimeSpan.Zero)
            : new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
    }

    private static BackupManifest? TryReadManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Proves that a local backup file is a clean, readable, uncorrupted SQLite
    /// database (and matches its recorded SHA-256 when known) before restoring.
    /// </summary>
    public static async Task<string> ValidateBackupFileAsync(
        string backupFilePath,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
            throw new FileNotFoundException("找不到指定的 Kindle 批注备份文件。", backupFilePath);
        KindleAnnotationMountedWriter.ValidateSnapshotSource(backupFilePath);

        string actualHash;
        await using (var stream = File.OpenRead(backupFilePath))
        {
            actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kindle 批注备份文件校验失败（SHA-256 不一致），已取消回退。");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupFilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Kindle 批注备份数据库完整性校验失败：{result}");

        return actualHash;
    }

    private static DateTimeOffset? ParseFolderTimestamp(string? name) =>
        DateTime.TryParseExact(
            name,
            "yyyyMMdd-HHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? new DateTimeOffset(parsed)
            : null;

    private sealed record BackupManifest(
        string DatabaseRelativePath,
        string Sha256,
        DateTimeOffset CreatedAt,
        string DeviceName,
        long FileSizeBytes,
        string? DeviceBackupPath,
        string? Reason);
}
