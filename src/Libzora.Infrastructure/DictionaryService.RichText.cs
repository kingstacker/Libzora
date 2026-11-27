using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

public sealed partial class DictionaryService
{
    private static async Task SaveRichEntriesAsync(string path, Dictionary<string, string>? entries,
        string sourcePath, CancellationToken token)
    {
        if (entries is null) return;
        var destination = Path.ChangeExtension(path, "rich.db");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var css = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "*.css").Order(StringComparer.Ordinal).Take(32))
            if (new FileInfo(file).Length <= 2 * 1024 * 1024)
                css.AppendLine(await File.ReadAllTextAsync(file, token).ConfigureAwait(false));
        var cssText = css.ToString();

        await Task.Run(() =>
        {
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
                    command.CommandText = "CREATE TABLE Entries(Term TEXT PRIMARY KEY COLLATE NOCASE,Html TEXT NOT NULL); CREATE TABLE Metadata(Css TEXT NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT OR REPLACE INTO Entries VALUES($term,$html)";
                    var term = command.Parameters.Add("$term", SqliteType.Text);
                    var html = command.Parameters.Add("$html", SqliteType.Text);
                    command.Prepare();
                    var count = 0;
                    foreach (var entry in entries)
                    {
                        if ((++count & 1023) == 0) token.ThrowIfCancellationRequested();
                        term.Value = entry.Key;
                        html.Value = entry.Value;
                        command.ExecuteNonQuery();
                    }
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO Metadata VALUES($css)";
                    command.Parameters.AddWithValue("$css", cssText);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token).ConfigureAwait(false);
    }

    private static async Task<(string? Html, string? Css)> ReadRichEntryAsync(string path, string term, CancellationToken token)
    {
        var richPath = Path.ChangeExtension(path, "rich.db");
        if (!File.Exists(richPath)) return (null, null);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = richPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Html FROM Entries WHERE Term=$term";
        command.Parameters.AddWithValue("$term", term);
        var html = await command.ExecuteScalarAsync(token) as string;
        command.CommandText = "SELECT Css FROM Metadata LIMIT 1";
        command.Parameters.Clear();
        return (html, await command.ExecuteScalarAsync(token) as string);
    }
}
