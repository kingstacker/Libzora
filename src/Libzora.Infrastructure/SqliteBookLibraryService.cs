using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Constants, fields, construction and schema initialisation.
/// </summary>
public sealed partial class SqliteBookLibraryService : IBookLibraryService
{
    private const int DetailedImportResultLimit = 1_000;

    private static readonly JsonSerializerOptions TrashJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".txt"
    };

    private readonly AppPaths _paths;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly IMetadataService _metadata;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly SemaphoreSlim _pdfCoverGate = new(1, 1);
    private readonly object _trashRecoveryLogGate = new();

    internal const string PurgeStagingFolderName = "purge-staging";
    internal const string PurgeManifestFileName = "manifest.json";
    internal const string PurgeBodyFileName = "body";
    internal const string PurgeCoverFilePrefix = "cover";

    public event EventHandler<LocalDataChangedEventArgs>? DataChanged;

    public SqliteBookLibraryService(AppPaths paths, IMetadataService metadata)
    {
        _paths = paths;
        _appSettingsStore = new AppSettingsStore(paths);
        _metadata = metadata;
    }

    private void NotifyDataChanged()
    {
        try
        {
            DataChanged?.Invoke(
                this,
                new LocalDataChangedEventArgs(LocalDataChangeKind.Library));
        }
        catch
        {
            // A notification must never turn a completed local write into a
            // failed library operation while the UI is shutting down.
        }
    }

    private bool ShouldDeleteAnnotationsWhenDeletingBook() =>
        AppSettings.Normalize(_appSettingsStore.LoadSynchronously()).DeleteAnnotationsWhenDeletingBook;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.Database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // WAL already allows one writer with many readers; a shared cache adds
        // table-level locking on top and makes concurrent readers contend.
        Cache = SqliteCacheMode.Private,
        DefaultTimeout = 30
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await AppBackupService.RecoverPendingImportsAsync(_paths, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Books (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Authors TEXT NOT NULL,
                Series TEXT NULL,
                SeriesIndex REAL NULL,
                Description TEXT NULL,
                Publisher TEXT NULL,
                PublishDate TEXT NULL,
                Isbn TEXT NULL,
                PageCount TEXT NULL,
                Binding TEXT NULL,
                OnlineRating REAL NULL,
                OnlineRatingCount INTEGER NULL,
                Tags TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '',
                IsFavorite INTEGER NOT NULL DEFAULT 0,
                ReadingStatus INTEGER NOT NULL DEFAULT 0,
                CoverPath TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BookFiles (
                Id TEXT PRIMARY KEY,
                BookId TEXT NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
                Format TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                Sha256 TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS IX_Books_TitleAuthors ON Books(Title, Authors);
            CREATE INDEX IF NOT EXISTS IX_Books_TitleAuthors_NoCase ON Books(Title COLLATE NOCASE, Authors COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Books_UpdatedAt ON Books(UpdatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Books_CreatedAt ON Books(CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_BookFiles_BookId ON BookFiles(BookId);
            CREATE INDEX IF NOT EXISTS IX_BookFiles_BookId_Format ON BookFiles(BookId, Format);
            CREATE TABLE IF NOT EXISTS BookCollections (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_BookCollections_UpdatedAt ON BookCollections(UpdatedAt DESC);
            CREATE TABLE IF NOT EXISTS BookCollectionItems (
                CollectionId TEXT NOT NULL REFERENCES BookCollections(Id) ON DELETE CASCADE,
                BookId TEXT NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (CollectionId, BookId)
            );
            CREATE INDEX IF NOT EXISTS IX_BookCollectionItems_BookId ON BookCollectionItems(BookId);
            CREATE TABLE IF NOT EXISTS LibraryTrash (
                Id TEXT PRIMARY KEY,
                Kind INTEGER NOT NULL,
                BookId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Format TEXT NULL,
                Size INTEGER NOT NULL DEFAULT 0,
                DeletedAt TEXT NOT NULL,
                BookJson TEXT NULL,
                FileJson TEXT NULL,
                BookFileId TEXT NULL,
                TrashPath TEXT NOT NULL,
                OriginalPath TEXT NOT NULL,
                TrashCoverPath TEXT NULL,
                OriginalCoverPath TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LibraryTrash_DeletedAt ON LibraryTrash(DeletedAt DESC);
            CREATE TABLE IF NOT EXISTS ReadingMaterialBooks (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                CoverPath TEXT NULL
            );
            CREATE TRIGGER IF NOT EXISTS PreserveReadingMaterialBook BEFORE DELETE ON Books
            BEGIN
                INSERT OR REPLACE INTO ReadingMaterialBooks(Id, Title, CoverPath)
                VALUES(OLD.Id, OLD.Title, OLD.CoverPath);
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ReadingMaterialBookSchema.EnsureAsync(connection, cancellationToken);
        await BookFileSyncVersion.EnsureAsync(connection, cancellationToken);
        await EnsureLibraryTrashBookFileIdColumnAsync(connection, cancellationToken);
        await EnsureBookCollectionUpdatedAtColumnAsync(connection, cancellationToken);
        await EnsureBookProductivityColumnsAsync(connection, cancellationToken);
        // These columns are added by the migration above, so their indexes
        // must be created afterwards: a legacy database does not have them
        // when the initial schema script runs.
        using (var productivityIndexes = connection.CreateCommand())
        {
            productivityIndexes.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_Books_ReadingStatus ON Books(ReadingStatus, UpdatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_Books_IsFavorite ON Books(IsFavorite, UpdatedAt DESC);
                """;
            await productivityIndexes.ExecuteNonQueryAsync(cancellationToken);
        }
        await EnsureSummaryCacheVersionsAsync(connection, cancellationToken);
        await EnsureDefaultCollectionAsync(connection, cancellationToken);
        await ReaderAnnotationCascade.EnsureAsync(connection, cancellationToken);
        await ReaderReadingHistory.EnsureAsync(connection, cancellationToken);
        await RecoverPendingPurgeStagingAsync(connection, cancellationToken);
        await PreserveTrashDisplayInfosAsync(connection, cancellationToken);
    }

    private async Task PreserveTrashDisplayInfosAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var entries = new List<(Guid Id, string Title, string? Cover)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT BookId, Title, TrashCoverPath FROM LibraryTrash WHERE Kind = 0;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Guid.TryParse(reader.GetString(0), out var id))
                    entries.Add((id, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        foreach (var entry in entries)
        {
            var cover = PreserveReadingMaterialCover(entry.Id, entry.Cover);
            using var save = connection.CreateCommand();
            save.CommandText = "INSERT INTO ReadingMaterialBooks(Id, Title, CoverPath) VALUES($id,$title,$cover) ON CONFLICT(Id) DO UPDATE SET CoverPath=COALESCE(excluded.CoverPath,ReadingMaterialBooks.CoverPath);";
            save.Parameters.AddWithValue("$id", entry.Id.ToString());
            save.Parameters.AddWithValue("$title", entry.Title);
            save.Parameters.AddWithValue("$cover", (object?)cover ?? DBNull.Value);
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private string? PreserveReadingMaterialCover(Guid bookId, string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath)) return null;
        var source = ResolveDataPath(coverPath);
        if (!File.Exists(source)) return null;
        var destination = Path.Combine(_paths.Data, "reading-material-covers", bookId.ToString("N") + Path.GetExtension(source));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, true);
        return Path.GetRelativePath(_paths.Data, destination);
    }

    private static async Task EnsureLibraryTrashBookFileIdColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(LibraryTrash);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        if (columns.Contains("BookFileId")) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE LibraryTrash ADD COLUMN BookFileId TEXT NULL;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureBookCollectionUpdatedAtColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(BookCollections);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        if (columns.Contains("UpdatedAt")) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE BookCollections ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
        using var backfill = connection.CreateCommand();
        backfill.CommandText = "UPDATE BookCollections SET UpdatedAt = CreatedAt WHERE UpdatedAt = '';";
        await backfill.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task DeletePurgeReaderRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        string parameterName,
        Guid id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue(parameterName, id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The "未收藏" collection is a system view: a book belongs to it exactly
    // when it has no membership in any user-created collection. Normalize the
    // rows here both for new imports and for databases created by the previous
    // implementation, where a book could appear in both places.
    private async Task EnsureDefaultCollectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var collectionId = await GetCollectionIdByNameAsync(
            connection,
            BookLibraryDefaults.UncollectedCollectionName,
            cancellationToken);
        if (collectionId is null)
        {
            collectionId = Guid.NewGuid();
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO BookCollections (Id, Name, CreatedAt, UpdatedAt)
                VALUES ($id, $name, $createdAt, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$id", collectionId.Value.ToString());
            insert.Parameters.AddWithValue("$name", BookLibraryDefaults.UncollectedCollectionName);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var normalize = connection.CreateCommand();
        normalize.CommandText = """
            DELETE FROM BookCollectionItems
            WHERE CollectionId = $defaultCollectionId
              AND EXISTS (
                  SELECT 1
                  FROM BookCollectionItems other
                  WHERE other.BookId = BookCollectionItems.BookId
                    AND other.CollectionId <> $defaultCollectionId
              );

            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT $defaultCollectionId, b.Id, $addedAt
            FROM Books b
            WHERE NOT EXISTS (
                SELECT 1
                FROM BookCollectionItems custom
                WHERE custom.BookId = b.Id
                  AND custom.CollectionId <> $defaultCollectionId
            );
            """;
        normalize.Parameters.AddWithValue("$defaultCollectionId", collectionId.Value.ToString());
        normalize.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        await normalize.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> GetCollectionIdByNameAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id FROM BookCollections WHERE Name = $name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string id && Guid.TryParse(id, out var parsed) ? parsed : null;
    }
}
