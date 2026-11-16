using System.IO.Compression;
using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportWaitsForATransientDatabaseLockAndCopiesCommittedData(bool commit)
    {
        var root = TestHelpers.CreateTempDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<AppBackupExportResult>? export = null;
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await SetBackupKeyAsync(paths);
            using var writer = await OpenExclusivelyLockedBackupDatabaseAsync(paths);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new TestHelpers.InlineProgress<AppBackupProgress>(_ => started.TrySetResult());
            var backupPath = Path.Combine(root, "snapshot.libzora");
            export = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector())
                .ExportAsync(backupPath, cancellation.Token, progress);

            await started.Task.WaitAsync(cancellation.Token);
            await Task.WhenAny(export, Task.Delay(200, cancellation.Token));
            Assert.False(export.IsCompleted, "A transient database lock must not fail the backup immediately.");
            using (var release = writer.CreateCommand())
            {
                release.CommandText = commit ? "COMMIT;" : "ROLLBACK;";
                await release.ExecuteNonQueryAsync(cancellation.Token);
            }
            await export.WaitAsync(cancellation.Token);

            using var archive = await OpenBackupArchiveAsync(backupPath);
            var snapshotPath = Path.Combine(root, "snapshot.db");
            archive.GetEntry("database/libzora.db")!.ExtractToFile(snapshotPath);
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await snapshot.OpenAsync(cancellation.Token);
            using var query = snapshot.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM BackupProbe;";
            Assert.Equal(commit ? 2L : 1L, await query.ExecuteScalarAsync(cancellation.Token));
        }
        finally
        {
            cancellation.Cancel();
            if (export is not null)
            {
                try { await export; }
                catch { /* Observe the worker before removing its staging directory. */ }
            }
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task CancellingExportWhileDatabaseIsLockedPreservesPreviousBackup()
    {
        var root = TestHelpers.CreateTempDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<AppBackupExportResult>? export = null;
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await SetBackupKeyAsync(paths);
            using var writer = await OpenExclusivelyLockedBackupDatabaseAsync(paths);
            var backupPath = Path.Combine(root, "retained.libzora");
            await File.WriteAllTextAsync(backupPath, "previous backup");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new TestHelpers.InlineProgress<AppBackupProgress>(_ => started.TrySetResult());
            export = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector())
                .ExportAsync(backupPath, cancellation.Token, progress);

            await started.Task.WaitAsync(cancellation.Token);
            await Task.WhenAny(export, Task.Delay(200, cancellation.Token));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);

            Assert.Equal("previous backup", await File.ReadAllTextAsync(backupPath));
            Assert.Empty(Directory.GetDirectories(paths.Data, ".libzora-export-*"));
            Assert.Empty(Directory.GetFiles(root, "retained.libzora.*.tmp"));
        }
        finally
        {
            cancellation.Cancel();
            if (export is not null)
            {
                try { await export; }
                catch { /* Observe the worker before removing its staging directory. */ }
            }
            TestHelpers.TryDelete(root);
        }
    }

    private static async Task<SqliteConnection> OpenExclusivelyLockedBackupDatabaseAsync(AppPaths paths)
    {
        paths.EnsureDirectories();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            // Rollback-journal mode makes the exclusive transaction block the
            // backup reader, unlike the deliberately concurrent WAL regression.
            command.CommandText = """
                CREATE TABLE Books (Id TEXT PRIMARY KEY);
                CREATE TABLE BookFiles (Id TEXT PRIMARY KEY);
                CREATE TABLE BackupProbe (Value INTEGER);
                INSERT INTO BackupProbe VALUES (1);
                BEGIN EXCLUSIVE;
                INSERT INTO BackupProbe VALUES (2);
                """;
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
