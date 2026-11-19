using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Library verification and table checks.
/// </summary>
public sealed partial class AppDataMigrationService
{
    private static async Task<(int BookCount, int FileCount)> VerifyPayloadAsync(
        string root,
        IReadOnlyCollection<string> copiedDatabases,
        IReadOnlyCollection<string>? copiedFiles,
        string activeDataRelativePath,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in copiedDatabases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var databasePath = Path.Combine(
                root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(databasePath))
                throw new InvalidDataException($"迁移后的数据库缺失：{relativePath}");
        }

        var requiredLibraries = copiedDatabases
            .Where(path => path.EndsWith("/libzora.db", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "libzora.db", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (requiredLibraries.Length == 0)
            throw new InvalidDataException("迁移数据中缺少书库数据库。");

        var activeDatabase = Path.Combine(
            root,
            activeDataRelativePath.Replace('/', Path.DirectorySeparatorChar),
            "libzora.db");
        if (!File.Exists(activeDatabase))
            throw new InvalidDataException("迁移后的主书库数据库缺失。");

        if (copiedFiles is not null)
        {
            foreach (var relativePath in copiedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(
                    root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    throw new InvalidDataException($"迁移后的数据文件缺失：{relativePath}");
            }
        }

        var (bookCount, fileCount) = (0, 0);
        foreach (var database in FindLibraryDatabases(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = await VerifyDatabaseAsync(database, cancellationToken);
            if (PathsEqual(database, activeDatabase)) (bookCount, fileCount) = counts;
        }
        return (bookCount, fileCount);
    }

    private static IEnumerable<string> FindLibraryDatabases(string root)
    {
        var active = Path.Combine(root, "data", "libzora.db");
        if (File.Exists(active)) yield return active;
        var regions = Path.Combine(root, "regions");
        if (!Directory.Exists(regions)) yield break;
        foreach (var region in Directory.EnumerateDirectories(regions))
        {
            var database = Path.Combine(region, "data", "libzora.db");
            if (File.Exists(database)) yield return database;
        }
    }

    private static async Task<(int BookCount, int FileCount)> VerifyDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException($"迁移后的数据库无法读取：{databasePath}", exception);
        }

        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"迁移后的数据库完整性校验失败：{databasePath}");
        }

        if (!await TableExistsAsync(connection, "Books", cancellationToken)
            || !await TableExistsAsync(connection, "BookFiles", cancellationToken))
            return (0, 0);

        var books = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM Books;";
            books = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        var files = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM BookFiles;";
            files = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        return (books, files);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
