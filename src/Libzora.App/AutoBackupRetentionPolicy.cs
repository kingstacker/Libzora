using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Automatic backups live in the same folder as user exports, so freshness
/// checks and retention must only ever consider files Libzora itself created.
/// A manually exported backup must never be deleted by the retention setting.
/// </summary>
internal static class AutoBackupRetentionPolicy
{
    public const string FileNamePrefix = "Libzora-auto-";
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(20);

    public static bool IsAutomaticBackup(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(AppBackupService.FileExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsFresh(DateTime lastWriteUtc, DateTime nowUtc) =>
        nowUtc - lastWriteUtc < MinimumInterval;

    /// <summary>Automatic backups, newest first.</summary>
    public static IReadOnlyList<FileInfo> SelectAutomatic(IEnumerable<FileInfo> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Where(file => IsAutomaticBackup(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Automatic backups beyond the configured retention count.</summary>
    public static IReadOnlyList<FileInfo> SelectExpired(
        IEnumerable<FileInfo> files,
        int retention)
    {
        ArgumentNullException.ThrowIfNull(files);
        // Normalize already clamps this; keep the policy safe on its own.
        var keep = Math.Max(1, retention);
        return SelectAutomatic(files).Skip(keep).ToList();
    }
}
