using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>The failed import could not be rolled back; startup recovery must run before more writes.</summary>

/// <summary>
/// Snapshot creation, replacement, manifests and import.
/// </summary>
public sealed partial class AppBackupService
{
    private async Task CreateDatabaseSnapshotAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.Database))
            throw new InvalidDataException("书库数据库不存在，无法导出。 ");

        await CopyDatabaseAsync(_paths.Database, destinationPath, cancellationToken);
    }

    private Task ReplaceDatabaseFromSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken) =>
        CopyDatabaseAsync(snapshotPath, _paths.Database, cancellationToken);

    private static async Task CopyDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        // SQLite's backup API reads a consistent committed view, including WAL
        // contents, without copying live journal/shared-memory files. Unlike
        // commands, Microsoft.Data.Sqlite's backup API does not retry BUSY or
        // LOCKED when another connection briefly holds a database lock.
        var retryTimer = Stopwatch.StartNew();
        var retryTimeout = TimeSpan.FromSeconds(source.DefaultTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                source.BackupDatabase(destination);
                break;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retryTimer.Elapsed >= retryTimeout) throw;
                await Task.Delay(100, cancellationToken);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task NormalizeSnapshotPathsAsync(string databasePath, CancellationToken cancellationToken)
    {
        // The package can move between Windows, Linux and macOS. Normalize
        // only the staged database, including paths embedded in trash records.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var (table, pathColumns, jsonColumns) in new[]
        {
            ("Books", new[] { "CoverPath" }, Array.Empty<string>()),
            ("BookFiles", new[] { "RelativePath" }, Array.Empty<string>()),
            ("ReadingMaterialBooks", new[] { "CoverPath" }, Array.Empty<string>()),
            ("LibraryTrash", new[] { "TrashPath", "OriginalPath", "TrashCoverPath", "OriginalCoverPath" }, new[] { "BookJson", "FileJson" })
        })
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = $"PRAGMA table_info(\"{table}\");";
                using var reader = await schema.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
            }
            foreach (var column in pathColumns.Where(columns.Contains))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = replace(\"{column}\", '\\', '/') WHERE instr(\"{column}\", '\\') > 0;";
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var column in jsonColumns.Where(columns.Contains))
            {
                var records = new List<(string Id, string Json)>();
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = $"SELECT Id, \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL;";
                    using var reader = await read.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken)) records.Add((reader.GetString(0), reader.GetString(1)));
                }
                foreach (var (id, json) in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonNode? node;
                    try { node = JsonNode.Parse(json); }
                    catch (JsonException exception) { throw new InvalidDataException("备份包中的回收站记录无法读取。", exception); }
                    if (!NormalizeJsonPaths(node)) continue;
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = $json WHERE Id = $id;";
                    update.Parameters.AddWithValue("$json", node!.ToJsonString());
                    update.Parameters.AddWithValue("$id", id);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        transaction.Commit();
    }

    private static bool NormalizeJsonPaths(JsonNode? node)
    {
        var changed = false;
        if (node is JsonObject record)
        {
            foreach (var (name, value) in record.ToArray())
            {
                if ((name.Equals("RelativePath", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("CoverPath", StringComparison.OrdinalIgnoreCase))
                    && value is JsonValue scalar && scalar.TryGetValue<string>(out var path) && path.Contains('\\'))
                {
                    record[name] = path.Replace('\\', '/');
                    changed = true;
                }
                else changed |= NormalizeJsonPaths(value);
            }
        }
        else if (node is JsonArray items)
            foreach (var item in items) changed |= NormalizeJsonPaths(item);
        return changed;
    }

    private static async Task<(int BookCount, int FileCount)> ReadLibrarySummaryAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return (0, 0);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Books), (SELECT COUNT(*) FROM BookFiles);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<(int BookCount, int FileCount)> ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var integrityResult = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份包中的书库数据库校验失败。 ");

            return await ReadLibrarySummaryAsync(databasePath, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("备份包中的书库数据库无法读取。 ", exception);
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("这不是有效的 Libzora 备份包。 ");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("备份包清单为空。 ");
    }

    private static async Task<BackupSettings?> ReadBackupSettingsAsync(
        string settingsPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath)) return null;
        try
        {
            await using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<BackupSettings>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("备份包中的设置无法读取。 ", exception);
        }
    }

    private static AiConnectionSettings BuildImportedAiSettings(
        BackupAiSettings? imported,
        AiConnectionSettings current)
    {
        if (imported is null) return current.Clone();

        var provider = imported.Provider?.Trim().ToLowerInvariant() ?? "deepseek";
        if (provider is not ("deepseek" or "openai" or "custom")) provider = "custom";
        var defaults = AiConnectionSettings.GetDefaults(provider);
        return new AiConnectionSettings
        {
            Provider = provider,
            BaseUrl = string.IsNullOrWhiteSpace(imported.BaseUrl) ? defaults.BaseUrl : imported.BaseUrl.Trim(),
            Model = AiConnectionSettings.NormalizeModel(
                provider,
                string.IsNullOrWhiteSpace(imported.Model) ? defaults.Model : imported.Model),
            ApiKey = current.ApiKey
        };
    }

    private static KindleEmailSettings BuildImportedKindleEmailSettings(
        BackupKindleEmailSettings? imported,
        KindleEmailSettings current)
    {
        if (imported is null) return current.Clone();

        return KindleEmailSettings.Normalize(new KindleEmailSettings
        {
            KindleEmailAddress = imported.KindleEmailAddress ?? string.Empty,
            SenderEmailAddress = imported.SenderEmailAddress ?? string.Empty,
            SmtpHost = imported.SmtpHost ?? string.Empty,
            SmtpPort = imported.SmtpPort,
            SmtpUsername = imported.SmtpUsername ?? string.Empty,
            SmtpPassword = current.SmtpPassword,
            EnableSsl = imported.EnableSsl
        });
    }

    private static S3SyncSettings BuildImportedS3Settings(
        BackupS3Settings? imported,
        S3SyncStoredSettings current)
    {
        if (imported is null) return current.Settings;

        var currentSettings = current.Settings;
        if (imported.Provider == SyncProvider.AccountServer)
            // A historical official-cloud backup neither rebinds storage nor
            // overwrites an existing custom connection, its secrets or sync key.
            return S3SyncSettings.Normalize(currentSettings with { Enabled = false });

        var credentialsReady = imported.Provider switch
        {
            SyncProvider.WebDav => currentSettings.Provider == SyncProvider.WebDav
                && currentSettings.WebDavEndpoint == S3SyncSettings.Normalize(new S3SyncSettings { WebDavEndpoint = imported.WebDavEndpoint ?? string.Empty }).WebDavEndpoint
                && (!imported.WebDavAuthenticationRequired || currentSettings.WebDavUsername.Length > 0),
            // Portable backups cannot establish which system Apple account is
            // signed in. An iCloud restore always requires an explicit enable.
            SyncProvider.ICloud => false,
            SyncProvider.S3 => !string.IsNullOrWhiteSpace(currentSettings.AccessKey)
                && !string.IsNullOrWhiteSpace(currentSettings.SecretKey),
            _ => false
        };
        var encryptionReady = !imported.EncryptionEnabled
            || !string.IsNullOrWhiteSpace(currentSettings.EncryptionKey);
        // A restored custom connection is an explicit choice; older packages
        // without the flag are inferred from the connection they carry.
        var importedCustom = imported.Provider is SyncProvider.S3 or SyncProvider.WebDav or SyncProvider.ICloud
            && (imported.ProviderChosenByUser || imported.Provider is SyncProvider.WebDav or SyncProvider.ICloud
                || !string.IsNullOrWhiteSpace(imported.Endpoint) || !string.IsNullOrWhiteSpace(imported.Bucket));
        return S3SyncSettings.Normalize(currentSettings with
        {
            Provider = imported.Provider,
            ProviderChosenByUser = importedCustom || currentSettings.ProviderChosenByUser,
            // A portable package does not contain credentials. Keep an
            // imported endpoint/configuration visible, but avoid enabling an
            // automatic sync that cannot authenticate on a new machine.
            Enabled = imported.Enabled && credentialsReady && encryptionReady,
            AutomaticSyncEnabled = imported.AutomaticSyncEnabled,
            IntervalMinutes = imported.IntervalMinutes,
            Endpoint = imported.Endpoint ?? string.Empty,
            // Older S3-only backups do not carry a WebDAV field.
            WebDavEndpoint = imported.WebDavEndpoint ?? currentSettings.WebDavEndpoint,
            Bucket = imported.Bucket ?? string.Empty,
            Region = imported.Region ?? "us-east-1",
            PathStyle = imported.PathStyle,
            SkipTlsVerify = imported.SkipTlsVerify,
            TimeoutSeconds = imported.TimeoutSeconds,
            ConcurrentRequests = imported.ConcurrentRequests,
            Prefix = imported.Prefix ?? "libzora"
        });
    }
}
