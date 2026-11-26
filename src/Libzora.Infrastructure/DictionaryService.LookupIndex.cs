using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

public sealed partial class DictionaryService
{
    private readonly SemaphoreSlim _lookupIndexGate = new(1, 1);

    private Task BuildLookupIndexAsync(string path, IReadOnlyDictionary<string, string> entries, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            var info = new FileInfo(path);
            var stamp = $"2:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            var indexPath = Path.ChangeExtension(path, "lookup.db");
            await _lookupIndexGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                WriteLookupIndex(indexPath, stamp, entries, token);
            }
            finally { _lookupIndexGate.Release(); }
        }, token);
    }

    private static void WriteLookupIndex(string indexPath, string stamp, IReadOnlyDictionary<string, string> entries, CancellationToken token)
    {
        var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
        var temporary = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temporary, Pooling = false }.ToString()))
            {
                connection.Open();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -65536;";
                pragma.ExecuteNonQuery();

                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE Metadata(Stamp TEXT); CREATE TABLE Lookup(TermKey BLOB PRIMARY KEY,Term TEXT NOT NULL,Definition TEXT NOT NULL);";
                command.ExecuteNonQuery();

                command.CommandText = "INSERT OR IGNORE INTO Lookup VALUES($key,$term,$definition)";
                var keyParam = command.Parameters.Add("$key", SqliteType.Blob);
                var termParam = command.Parameters.Add("$term", SqliteType.Text);
                var definitionParam = command.Parameters.Add("$definition", SqliteType.Text);
                command.Prepare();
                var count = 0;
                foreach (var entry in entries)
                {
                    if ((++count & 1023) == 0) token.ThrowIfCancellationRequested();
                    keyParam.Value = compareInfo.GetSortKey(entry.Key, CompareOptions.IgnoreCase).KeyData;
                    termParam.Value = entry.Key;
                    definitionParam.Value = entry.Value;
                    command.ExecuteNonQuery();
                }
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO Metadata VALUES($stamp)";
                command.Parameters.AddWithValue("$stamp", stamp);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            File.Move(temporary, indexPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Keep the portable JSON as the source of truth. This disposable index makes each
    // lookup independent of dictionary size, without retaining entire books in memory.
    private async Task<(string Term, string Definition)?> LookupIndexedAsync(string path, string term, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
            byte[] Key(string value) => compareInfo.GetSortKey(value, CompareOptions.IgnoreCase).KeyData;
            var info = new FileInfo(path);
            var stamp = $"2:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            var indexPath = Path.ChangeExtension(path, "lookup.db");
            await _lookupIndexGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var needsRebuild = !File.Exists(indexPath);
                if (!needsRebuild)
                {
                    using var checkConn = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource = indexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    try
                    {
                        checkConn.Open();
                        using var checkCmd = checkConn.CreateCommand();
                        checkCmd.CommandText = "SELECT Stamp FROM Metadata LIMIT 1";
                        needsRebuild = checkCmd.ExecuteScalar() as string != stamp;
                    }
                    catch (SqliteException) { needsRebuild = true; }
                }

                if (needsRebuild)
                {
                    await using var stream = File.OpenRead(path);
                    var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, token).ConfigureAwait(false);
                    WriteLookupIndex(indexPath, stamp, entries ?? [], token);
                }

                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = indexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Term,Definition FROM Lookup WHERE TermKey=$key";
                command.Parameters.AddWithValue("$key", Key(term));
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? ((string Term, string Definition)?)(reader.GetString(0), reader.GetString(1)) : null;
            }
            finally { _lookupIndexGate.Release(); }
        }, token).ConfigureAwait(false);
    }
}
