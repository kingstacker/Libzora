using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>The failed import could not be rolled back; startup recovery must run before more writes.</summary>
public sealed class AppBackupRollbackException(string message, Exception innerException) : IOException(message, innerException);

/// <summary>
/// Creates and restores a portable Libzora backup package, encrypted with the local sync key.
///
/// The package deliberately contains only non-secret settings. API keys, sync
/// credentials, the client-side encryption key and SMTP passwords stay
/// protected by the platform on the current machine and are never written to an
/// export file.
/// </summary>
/// <summary>Coarse backup progress for a progress bar; fraction is 0..1.</summary>
public sealed record AppBackupProgress(double Fraction, string Message);
public sealed partial class AppBackupService
{
    public const string FileExtension = ".libzora";

    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "database/libzora.db";
    private const string DatabaseWalEntryName = "database/libzora.db-wal";
    private const string DatabaseShmEntryName = "database/libzora.db-shm";
    private const string SettingsEntryName = "settings/settings.json";
    private const string ReadingMaterialCoversDirectory = "reading-material-covers";
    private const int CurrentFormatVersion = 1;
    private const string BackupFormat = "LibzoraBackup";
    private const string AiSettingsPath = "ai-settings.json";
    private const string KindleEmailSettingsPath = "kindle-email-settings.json";
    private const string S3SyncSettingsPath = "s3-sync-settings.json";
    internal const string PendingSyncBaselineResetFileName = ".libzora-sync-baseline-reset";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppPaths _paths;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly S3SyncSettingsStore _s3SyncSettingsStore;

    public AppBackupService(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _aiSettingsStore = new AiSettingsStore(paths, protector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, protector);
        _s3SyncSettingsStore = new S3SyncSettingsStore(paths, protector);
    }

    public async Task<AppBackupExportResult> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default,
        IProgress<AppBackupProgress>? progress = null)
    {
        var encryptionKey = await RequireEncryptionKeyAsync(cancellationToken);
        void Report(double fraction, string message) =>
            progress?.Report(new AppBackupProgress(fraction, message));
        Report(0, "正在读取书库…");
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await RecoverPendingImportsAsync(_paths, cancellationToken);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("请选择备份文件的保存位置。", nameof(destinationPath));

        _paths.EnsureDirectories();
        destinationPath = Path.GetFullPath(destinationPath);
        EnsureBackupOutsideDataDirectory(destinationPath);

        var stagingRoot = CreateWorkingDirectory(".libzora-export-");
        var databaseSnapshotPath = Path.Combine(stagingRoot, "libzora.db");
        var pendingPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await CreateDatabaseSnapshotAsync(databaseSnapshotPath, cancellationToken);
            await NormalizeSnapshotPathsAsync(databaseSnapshotPath, cancellationToken);
            Report(0.06, "正在整理书籍文件…");
            var summary = await ReadLibrarySummaryAsync(databaseSnapshotPath, cancellationToken);
            var settings = await BuildExportSettingsAsync(cancellationToken);
            var manifest = new BackupManifest
            {
                Format = BackupFormat,
                Version = CurrentFormatVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                BookCount = summary.BookCount,
                FileCount = summary.FileCount
            };

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            // Listing first gives the progress bar a denominator; a file added
            // mid-export is simply left for the next backup.
            var sources = new (string Directory, string Prefix)[]
            {
                (_paths.Library, "library"),
                (_paths.Covers, "covers"),
                (Path.Combine(_paths.Data, ReadingMaterialCoversDirectory), ReadingMaterialCoversDirectory),
                (_paths.Trash, "trash")
            };
            var files = sources
                .SelectMany(source => EnumerateExportFiles(source.Directory)
                    .Select(path => (Path: path, Entry: $"{source.Prefix}/{RelativeEntryPath(source.Directory, path)}")))
                .ToList();
            var totalBytes = files.Sum(file => new FileInfo(file.Path).Length)
                + new FileInfo(databaseSnapshotPath).Length;
            var processedBytes = 0L;
            void ReportFile(long length)
            {
                processedBytes += length;
                Report(0.1 + 0.85 * Math.Min(1, totalBytes <= 0 ? 1 : (double)processedBytes / totalBytes),
                    "正在打包书籍文件…");
            }

            await using (var output = new FileStream(
                pendingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            await using (var encrypted = BackupEncryption.CreateWriter(output, encryptionKey))
            using (var archive = new ZipArchive(encrypted, ZipArchiveMode.Create, leaveOpen: true))
            {
                await AddJsonEntryAsync(archive, ManifestEntryName, manifest, cancellationToken);
                ReportFile(await AddFileEntryAsync(archive, databaseSnapshotPath, DatabaseEntryName, cancellationToken));
                // Mark even an empty directory so imports can distinguish it from older backups.
                archive.CreateEntry(ReadingMaterialCoversDirectory + "/");
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReportFile(await AddFileEntryAsync(archive, file.Path, file.Entry, cancellationToken));
                }
                await AddJsonEntryAsync(archive, SettingsEntryName, settings, cancellationToken);
            }
            Report(0.97, "正在写入加密备份文件…");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pendingPath, destinationPath, overwrite: true);
            Report(1, "备份已完成。");
            return new AppBackupExportResult(
                manifest.BookCount,
                manifest.FileCount,
                new FileInfo(destinationPath).Length);
        }
        finally
        {
            TryDeleteFile(pendingPath);
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task<AppBackupImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default,
        IProgress<AppBackupProgress>? progress = null)
    {
        var encryptionKey = await RequireEncryptionKeyAsync(cancellationToken);
        void Report(double fraction, string message) =>
            progress?.Report(new AppBackupProgress(fraction, message));
        Report(0, "正在读取备份文件…");
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await RecoverPendingImportsAsync(_paths, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("请选择要导入的备份文件。", nameof(sourcePath));

        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("备份文件不存在。", sourcePath);

        _paths.EnsureDirectories();
        var stagingRoot = CreateWorkingDirectory(".libzora-import-");
        try
        {
            var decryptedPath = Path.Combine(stagingRoot, "decrypted-archive.zip");
            Report(0.01, "正在使用同步密钥验证并解密备份…");
            await using (var encrypted = File.OpenRead(sourcePath))
            await using (var decrypted = new FileStream(decryptedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await BackupEncryption.DecryptAsync(encrypted, decrypted, encryptionKey, cancellationToken);
            BackupManifest manifest;
            BackupSettings? backupSettings;
            bool hasReadingMaterialCovers;
            using (var archive = ZipFile.OpenRead(decryptedPath))
            {
                manifest = await ReadManifestAsync(archive, cancellationToken);
                ValidateManifest(manifest);
                hasReadingMaterialCovers = archive.Entries.Any(entry =>
                {
                    var name = NormalizeEntryName(entry.FullName);
                    return name.Equals(ReadingMaterialCoversDirectory, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(ReadingMaterialCoversDirectory + "/", StringComparison.OrdinalIgnoreCase);
                });
                var totalBytes = archive.Entries
                    .Where(entry => IsSupportedEntry(NormalizeEntryName(entry.FullName)))
                    .Sum(entry => entry.Length);
                var extractedBytes = 0L;
                await ExtractSupportedEntriesAsync(archive, stagingRoot, cancellationToken, length =>
                {
                    extractedBytes += length;
                    Report(0.05 + 0.75 * Math.Min(1, totalBytes <= 0 ? 1 : (double)extractedBytes / totalBytes),
                        "正在解压备份…");
                });
            }
            File.Delete(decryptedPath);
            Report(0.82, "正在检查备份数据…");

            var stagedDatabasePath = Path.Combine(stagingRoot, DatabaseEntryName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stagedDatabasePath))
                throw new InvalidDataException("备份包缺少书库数据库。 ");

            var summary = await ValidateDatabaseAsync(stagedDatabasePath, cancellationToken);
            await NormalizeSnapshotPathsAsync(stagedDatabasePath, cancellationToken);
            await using (var staged = new SqliteConnection($"Data Source={stagedDatabasePath};Pooling=False"))
            {
                await staged.OpenAsync(cancellationToken);
                await ReaderRecoveryEpoch.RotateForImportAsync(staged, cancellationToken);
            }
            backupSettings = await ReadBackupSettingsAsync(
                Path.Combine(stagingRoot, SettingsEntryName.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken);

            var currentAiSettings = await _aiSettingsStore.LoadAsync(cancellationToken);
            var currentKindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
            var currentS3Settings = await _s3SyncSettingsStore.LoadAsync(cancellationToken);
            var importedAiSettings = BuildImportedAiSettings(backupSettings?.Ai, currentAiSettings);
            var importedKindleEmailSettings = BuildImportedKindleEmailSettings(
                backupSettings?.KindleEmail,
                currentKindleEmailSettings);
            var importedS3Settings = BuildImportedS3Settings(backupSettings?.S3, currentS3Settings);

            var stagedLibraryPath = Path.Combine(stagingRoot, "library");
            var stagedCoversPath = Path.Combine(stagingRoot, "covers");
            var stagedReadingMaterialCoversPath = Path.Combine(stagingRoot, ReadingMaterialCoversDirectory);
            var stagedTrashPath = Path.Combine(stagingRoot, "trash");
            Directory.CreateDirectory(stagedLibraryPath);
            Directory.CreateDirectory(stagedCoversPath);
            if (hasReadingMaterialCovers) Directory.CreateDirectory(stagedReadingMaterialCoversPath);
            Directory.CreateDirectory(stagedTrashPath);

            var baselineResetMarker = Path.Combine(_paths.Data, PendingSyncBaselineResetFileName);
            var previousBaselineMarker = File.Exists(baselineResetMarker)
                ? await File.ReadAllTextAsync(baselineResetMarker, cancellationToken) : null;
            var rollbackRoot = CreateWorkingDirectory(".libzora-rollback-");
            var journal = new ImportJournal
            {
                StagingDirectory = Path.GetFileName(stagingRoot),
                OriginalPaths = ManagedImportPaths.Where(name => PathExists(Path.Combine(_paths.Data, name))).ToList(),
                ReplaceReadingMaterialCovers = hasReadingMaterialCovers,
                PreviousBaselineResetMarker = previousBaselineMarker
            };
            var retainRollback = false;
            var importCommitted = false;
            var replacementStarted = false;
            try
            {
                await WriteImportJournalAsync(rollbackRoot, journal, cancellationToken);
                // If the process stops after replacing the library but before
                // the sync baseline is reset, the next sync sees this marker
                // and completes the reset before detecting deletions.
                await File.WriteAllTextAsync(
                    baselineResetMarker,
                    DateTimeOffset.UtcNow.ToString("O"),
                    cancellationToken);
                await CreateDatabaseSnapshotAsync(
                    Path.Combine(rollbackRoot, "libzora.db"),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                journal.Phase = ImportPhase.Replacing;
                await WriteImportJournalAsync(rollbackRoot, journal, cancellationToken);
                replacementStarted = true;
                Report(0.9, "正在还原书库…");
                MoveCurrentDataToRollback(rollbackRoot, journal);
                Directory.Move(stagedLibraryPath, _paths.Library);
                Directory.Move(stagedCoversPath, _paths.Covers);
                if (hasReadingMaterialCovers)
                    Directory.Move(stagedReadingMaterialCoversPath, Path.Combine(_paths.Data, ReadingMaterialCoversDirectory));
                Directory.Move(stagedTrashPath, _paths.Trash);
                await ReplaceDatabaseFromSnapshotAsync(stagedDatabasePath, cancellationToken);

                await _aiSettingsStore.SaveAsync(importedAiSettings, cancellationToken);
                await _kindleEmailSettingsStore.SaveAsync(importedKindleEmailSettings, cancellationToken);
                await _s3SyncSettingsStore.SaveAsync(
                    currentS3Settings.DeviceId,
                    importedS3Settings,
                    cancellationToken);
                journal.Phase = ImportPhase.Committed;
                await WriteImportJournalAsync(rollbackRoot, journal, cancellationToken);
                importCommitted = true;
                Report(1, "备份已恢复。");

                return new AppBackupImportResult(
                    summary.BookCount,
                    summary.FileCount,
                    importedAiSettings,
                    importedKindleEmailSettings)
                {
                    S3Settings = importedS3Settings
                };
            }
            catch (Exception importException)
            {
                try
                {
                    if (!importCommitted && replacementStarted)
                    {
                        // Cancellation of the import must never cancel recovery.
                        await RestoreImportAsync(_paths, rollbackRoot, journal, CancellationToken.None);
                    }
                    RestoreBaselineResetMarker(_paths, journal);
                }
                catch (Exception rollbackException)
                {
                    retainRollback = true;
                    throw new AppBackupRollbackException(
                        $"备份导入失败，自动恢复未完成。恢复副本已保留在：{rollbackRoot}",
                        new AggregateException(importException, rollbackException));
                }
                throw;
            }
            finally
            {
                if (!retainRollback) TryDeleteDirectory(rollbackRoot);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task EnsureEncryptionConfiguredAsync(CancellationToken cancellationToken = default) =>
        _ = await RequireEncryptionKeyAsync(cancellationToken);

    private async Task<string> RequireEncryptionKeyAsync(CancellationToken cancellationToken)
    {
        var stored = await _s3SyncSettingsStore.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(stored.Settings.EncryptionKey)) throw new SyncEncryptionKeyRequiredException();
        return stored.Settings.EncryptionKey;
    }

    private async Task<BackupSettings> BuildExportSettingsAsync(CancellationToken cancellationToken)
    {
        var ai = await _aiSettingsStore.LoadAsync(cancellationToken);
        var kindleEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        var s3 = await _s3SyncSettingsStore.LoadAsync(cancellationToken);
        return new BackupSettings
        {
            Ai = new BackupAiSettings
            {
                Provider = ai.Provider,
                BaseUrl = ai.BaseUrl,
                Model = ai.Model
            },
            KindleEmail = new BackupKindleEmailSettings
            {
                KindleEmailAddress = kindleEmail.KindleEmailAddress,
                SenderEmailAddress = kindleEmail.SenderEmailAddress,
                SmtpHost = kindleEmail.SmtpHost,
                SmtpPort = kindleEmail.SmtpPort,
                SmtpUsername = kindleEmail.SmtpUsername,
                EnableSsl = kindleEmail.EnableSsl
            },
            S3 = new BackupS3Settings
            {
                Provider = s3.Settings.Provider,
                ProviderChosenByUser = s3.Settings.ProviderChosenByUser,
                Enabled = s3.Settings.Enabled,
                AutomaticSyncEnabled = s3.Settings.AutomaticSyncEnabled,
                IntervalMinutes = s3.Settings.IntervalMinutes,
                Endpoint = s3.Settings.Endpoint,
                WebDavEndpoint = s3.Settings.WebDavEndpoint,
                WebDavAuthenticationRequired = s3.Settings.WebDavUsername.Length > 0,
                Bucket = s3.Settings.Bucket,
                Region = s3.Settings.Region,
                PathStyle = s3.Settings.PathStyle,
                SkipTlsVerify = s3.Settings.SkipTlsVerify,
                TimeoutSeconds = s3.Settings.TimeoutSeconds,
                ConcurrentRequests = s3.Settings.ConcurrentRequests,
                Prefix = s3.Settings.Prefix,
                EncryptionEnabled = !string.IsNullOrWhiteSpace(s3.Settings.EncryptionKey)
            }
        };
    }
}

public sealed record AppBackupExportResult(int BookCount, int FileCount, long ArchiveSize);

public sealed record AppBackupImportResult(
    int BookCount,
    int FileCount,
    AiConnectionSettings AiSettings,
    KindleEmailSettings KindleEmailSettings)
{
    public S3SyncSettings? S3Settings { get; init; }
}
