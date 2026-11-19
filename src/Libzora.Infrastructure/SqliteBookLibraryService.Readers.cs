using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Row readers and lookup helpers.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    private static Book ReadBook(SqliteDataReader reader)
    {
        return new Book
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.GetString(1),
            Authors = reader.GetString(2),
            Series = reader.IsDBNull(3) ? null : reader.GetString(3),
            SeriesIndex = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
            Tags = reader.GetString(6),
            Category = reader.GetString(7),
            IsFavorite = reader.GetInt64(8) != 0,
            ReadingStatus = Enum.IsDefined(typeof(LibraryReadingStatus), reader.GetInt32(9))
                ? (LibraryReadingStatus)reader.GetInt32(9)
                : LibraryReadingStatus.Unread,
            CoverPath = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12)),
            Publisher = reader.IsDBNull(13) ? null : reader.GetString(13),
            PublishDate = reader.IsDBNull(14) ? null : reader.GetString(14),
            Isbn = reader.IsDBNull(15) ? null : reader.GetString(15),
            PageCount = reader.IsDBNull(16) ? null : reader.GetString(16),
            Binding = reader.IsDBNull(17) ? null : reader.GetString(17),
            OnlineRating = reader.IsDBNull(18) ? null : reader.GetDouble(18),
            OnlineRatingCount = reader.IsDBNull(19) ? null : reader.GetInt32(19)
        };
    }

    private static async Task<List<BookFile>> ReadFilesAsync(SqliteConnection connection, Guid bookId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, BookId, Format, RelativePath, Size, Sha256 FROM BookFiles WHERE BookId = $bookId ORDER BY Format;";
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        var files = new List<BookFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new BookFile
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                Format = reader.GetString(2),
                RelativePath = reader.GetString(3),
                Size = reader.GetInt64(4),
                Sha256 = reader.GetString(5)
            });
        }
        return files;
    }

    private static async Task<Dictionary<Guid, List<BookFile>>> ReadFilesByBookAsync(
        SqliteConnection connection,
        IEnumerable<Guid> bookIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<BookFile>>();
        foreach (var batch in bookIds.Chunk(500))
        {
            var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$book{index}").ToArray();
            command.CommandText = $"""
                SELECT Id, BookId, Format, RelativePath, Size, Sha256
                FROM BookFiles
                WHERE BookId IN ({string.Join(", ", parameters)})
                ORDER BY BookId, Format;
                """;
            for (var index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bookId = Guid.Parse(reader.GetString(1));
                if (!result.TryGetValue(bookId, out var files))
                {
                    files = [];
                    result[bookId] = files;
                }
                files.Add(new BookFile
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    BookId = bookId,
                    Format = reader.GetString(2),
                    RelativePath = reader.GetString(3),
                    Size = reader.GetInt64(4),
                    Sha256 = reader.GetString(5)
                });
            }
        }
        return result;
    }

    private static async Task<Dictionary<Guid, List<Guid>>> ReadCollectionIdsByBookAsync(
        SqliteConnection connection,
        IEnumerable<Guid> bookIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<Guid>>();
        foreach (var batch in bookIds.Chunk(500))
        {
            var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$book{index}").ToArray();
            command.CommandText = $"""
                SELECT BookId, CollectionId
                FROM BookCollectionItems
                WHERE BookId IN ({string.Join(", ", parameters)});
                """;
            for (var index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bookId = Guid.Parse(reader.GetString(0));
                if (!result.TryGetValue(bookId, out var collectionIds))
                {
                    collectionIds = [];
                    result[bookId] = collectionIds;
                }
                collectionIds.Add(Guid.Parse(reader.GetString(1)));
            }
        }
        return result;
    }

    private static async Task<Book?> FindBookByHashAsync(SqliteConnection connection, string hash, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Id, b.Title, b.Authors, b.Series, b.SeriesIndex, b.Description, b.Tags, b.Category, b.IsFavorite, b.ReadingStatus, b.CoverPath, b.CreatedAt, b.UpdatedAt,
                   b.Publisher, b.PublishDate, b.Isbn, b.PageCount, b.Binding, b.OnlineRating, b.OnlineRatingCount
            FROM Books b INNER JOIN BookFiles f ON f.BookId = b.Id WHERE f.Sha256 = $hash LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<Book?> FindBookByTitleAuthorsAsync(SqliteConnection connection, string title, string authors, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, OnlineRating, OnlineRatingCount
            FROM Books WHERE Title = $title COLLATE NOCASE AND Authors = $authors COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$authors", authors);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<Book?> ReadBookByIdAsync(
        SqliteConnection connection,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, OnlineRating, OnlineRatingCount
            FROM Books WHERE Id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", bookId.ToString());
        Book? book;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            book = ReadBook(reader);
        }
        book.Files = await ReadFilesAsync(connection, book.Id, cancellationToken);
        book.CollectionIds = (await ReadCollectionIdsByBookAsync(connection, [book.Id], cancellationToken))
            .GetValueOrDefault(book.Id) ?? [];
        return book;
    }

    private static async Task<bool> BookExistsAsync(
        SqliteConnection connection,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Books WHERE Id = $bookId);";
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task<BookFile?> FindFileByHashAsync(
        SqliteConnection connection,
        string hash,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, BookId, Format, RelativePath, Size, Sha256 FROM BookFiles WHERE Sha256 = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new BookFile
        {
            Id = Guid.Parse(reader.GetString(0)),
            BookId = Guid.Parse(reader.GetString(1)),
            Format = reader.GetString(2),
            RelativePath = reader.GetString(3),
            Size = reader.GetInt64(4),
            Sha256 = reader.GetString(5)
        };
    }

    private static async Task InsertBookAsync(
        SqliteConnection connection,
        Book book,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Books (Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                               Publisher, PublishDate, Isbn, PageCount, Binding, OnlineRating, OnlineRatingCount)
            VALUES ($id, $title, $authors, $series, $seriesIndex, $description, $tags, $category, $isFavorite, $readingStatus, $coverPath, $createdAt, $updatedAt,
                    $publisher, $publishDate, $isbn, $pageCount, $binding, $onlineRating, $onlineRatingCount);
            """;
        AddBookParameters(command, book);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBookRowAsync(
        SqliteConnection connection,
        Book book,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Books SET Title=$title, Authors=$authors, Series=$series, SeriesIndex=$seriesIndex, Description=$description,
                Tags=$tags, Category=$category, IsFavorite=$isFavorite, ReadingStatus=$readingStatus,
                CoverPath=$coverPath, UpdatedAt=$updatedAt, Publisher=$publisher, PublishDate=$publishDate,
                Isbn=$isbn, PageCount=$pageCount, Binding=$binding, OnlineRating=$onlineRating,
                OnlineRatingCount=$onlineRatingCount WHERE Id=$id;
            """;
        AddBookParameters(command, book);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddBookParameters(SqliteCommand command, Book book)
    {
        command.Parameters.AddWithValue("$id", book.Id.ToString());
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$authors", book.Authors);
        command.Parameters.AddWithValue("$series", (object?)book.Series ?? DBNull.Value);
        command.Parameters.AddWithValue("$seriesIndex", (object?)book.SeriesIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)book.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$publisher", (object?)book.Publisher ?? DBNull.Value);
        command.Parameters.AddWithValue("$publishDate", (object?)book.PublishDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$isbn", (object?)book.Isbn ?? DBNull.Value);
        command.Parameters.AddWithValue("$pageCount", (object?)book.PageCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$binding", (object?)book.Binding ?? DBNull.Value);
        command.Parameters.AddWithValue("$onlineRating", (object?)book.OnlineRating ?? DBNull.Value);
        command.Parameters.AddWithValue("$onlineRatingCount", (object?)book.OnlineRatingCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", book.Tags ?? string.Empty);
        command.Parameters.AddWithValue("$category", book.Category ?? string.Empty);
        command.Parameters.AddWithValue("$isFavorite", book.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$readingStatus", (int)book.ReadingStatus);
        command.Parameters.AddWithValue("$coverPath", (object?)book.CoverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", book.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", book.UpdatedAt.ToString("O"));
    }

    private static async Task EnsureBookProductivityColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(Books);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));

        // Older libraries named the online rating columns after the site the
        // data originally came from. Rename them in place so existing values
        // survive the upgrade.
        await RenameLegacyColumnsAsync(connection, existing, "RatingCount", "OnlineRatingCount", cancellationToken);
        await RenameLegacyColumnsAsync(connection, existing, "Rating", "OnlineRating", cancellationToken);

        foreach (var definition in new[]
        {
            (Name: "Category", Sql: "ALTER TABLE Books ADD COLUMN Category TEXT NOT NULL DEFAULT '';"),
            (Name: "IsFavorite", Sql: "ALTER TABLE Books ADD COLUMN IsFavorite INTEGER NOT NULL DEFAULT 0;"),
            (Name: "ReadingStatus", Sql: "ALTER TABLE Books ADD COLUMN ReadingStatus INTEGER NOT NULL DEFAULT 0;"),
            (Name: "Publisher", Sql: "ALTER TABLE Books ADD COLUMN Publisher TEXT NULL;"),
            (Name: "PublishDate", Sql: "ALTER TABLE Books ADD COLUMN PublishDate TEXT NULL;"),
            (Name: "Isbn", Sql: "ALTER TABLE Books ADD COLUMN Isbn TEXT NULL;"),
            (Name: "PageCount", Sql: "ALTER TABLE Books ADD COLUMN PageCount TEXT NULL;"),
            (Name: "Binding", Sql: "ALTER TABLE Books ADD COLUMN Binding TEXT NULL;"),
            (Name: "OnlineRating", Sql: "ALTER TABLE Books ADD COLUMN OnlineRating REAL NULL;"),
            (Name: "OnlineRatingCount", Sql: "ALTER TABLE Books ADD COLUMN OnlineRatingCount INTEGER NULL;")
        })
        {
            if (existing.Contains(definition.Name)) continue;
            var alter = connection.CreateCommand();
            alter.CommandText = definition.Sql;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Renames the single legacy column that ends with <paramref name="suffix"/>
    /// to its current name. The Books table only ever had one such column per
    /// suffix, so the match is unambiguous.
    /// </summary>
}
