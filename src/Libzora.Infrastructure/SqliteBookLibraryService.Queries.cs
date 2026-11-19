using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Search, filtering, paging and collection summaries.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    public async Task<IReadOnlyList<Book>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var text = query?.Trim() ?? string.Empty;
        var pinyinQuery = PinyinSearchIndex.NormalizeQuery(text);
        var usePinyinSearch = text.Length > 0 && pinyinQuery.Length > 0;
        var command = connection.CreateCommand();
        command.CommandText = usePinyinSearch
            ? """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, OnlineRating, OnlineRatingCount
            FROM Books
            ORDER BY UpdatedAt DESC, Title COLLATE NOCASE;
            """
            : """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, OnlineRating, OnlineRatingCount
            FROM Books
            WHERE $query = '' OR Title LIKE $like OR Authors LIKE $like OR Tags LIKE $like OR Series LIKE $like
            ORDER BY UpdatedAt DESC, Title COLLATE NOCASE;
            """;
        if (!usePinyinSearch)
        {
            command.Parameters.AddWithValue("$query", text);
            command.Parameters.AddWithValue("$like", $"%{text}%");
        }

        var books = new List<Book>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var book = ReadBook(reader);
                cancellationToken.ThrowIfCancellationRequested();
                if (!usePinyinSearch || MatchesLibrarySearch(book, text, pinyinQuery))
                    books.Add(book);
            }
        }

        var filesByBook = await ReadFilesByBookAsync(connection, books.Select(book => book.Id), cancellationToken);
        var collectionsByBook = await ReadCollectionIdsByBookAsync(
            connection,
            books.Select(book => book.Id),
            cancellationToken);
        foreach (var book in books)
        {
            book.Files = filesByBook.GetValueOrDefault(book.Id) ?? [];
            book.CollectionIds = collectionsByBook.GetValueOrDefault(book.Id) ?? [];
        }

        return books;
    }

    public async Task<int> GetBookCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Books;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<Book?> GetBookAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadBookByIdAsync(connection, bookId, cancellationToken);
    }

    private async Task<LibraryFilterOptions> GetFilterOptionsCoreAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var authors = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var tags = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var categories = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        var bookCommand = connection.CreateCommand();
        bookCommand.CommandText = "SELECT Authors, Tags, Category FROM Books;";
        await using (var reader = await bookCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                AddFilterValues(authors, reader.IsDBNull(0) ? null : reader.GetString(0));
                AddFilterValues(tags, reader.IsDBNull(1) ? null : reader.GetString(1));
                AddFilterValues(categories, reader.IsDBNull(2) ? null : reader.GetString(2), split: false);
            }
        }

        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var formatCommand = connection.CreateCommand();
        formatCommand.CommandText = "SELECT DISTINCT Format FROM BookFiles WHERE trim(Format) <> '';";
        await using (var reader = await formatCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var format = reader.GetString(0).Trim();
                if (format.Length > 0) formats.Add(format.ToUpperInvariant());
            }
        }

        return new LibraryFilterOptions(
            authors.Order(StringComparer.CurrentCultureIgnoreCase).ToArray(),
            tags.Order(StringComparer.CurrentCultureIgnoreCase).ToArray(),
            formats.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            categories.Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public async Task<LibraryPageResult> SearchPageAsync(
        string? query = null,
        string? author = null,
        string? tag = null,
        string? format = null,
        string? category = null,
        Guid? collectionId = null,
        LibraryReadingStatus? readingStatus = null,
        bool? favorite = null,
        LibrarySortMode sortMode = LibrarySortMode.UpdatedDescending,
        int pageIndex = 0,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        pageIndex = Math.Max(0, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var text = query?.Trim() ?? string.Empty;
        var pinyinQuery = PinyinSearchIndex.NormalizeQuery(text);
        if (text.Length > 0 && pinyinQuery.Length > 0)
        {
            return await SearchPageByPinyinAsync(
                connection,
                text,
                pinyinQuery,
                author,
                tag,
                format,
                category,
                collectionId,
                readingStatus,
                favorite,
                sortMode,
                pageIndex,
                pageSize,
                cancellationToken);
        }

        var where = BuildLibrarySearchWhere(
            query,
            author,
            tag,
            format,
            category,
            collectionId,
            readingStatus,
            favorite);

        var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM Books b {where};";
        AddLibrarySearchParameters(countCommand, query, author, tag, format, category, collectionId, readingStatus);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT b.Id, b.Title, b.Authors, b.Series, b.SeriesIndex, b.Description, b.Tags, b.Category, b.IsFavorite, b.ReadingStatus, b.CoverPath, b.CreatedAt, b.UpdatedAt,
                   b.Publisher, b.PublishDate, b.Isbn, b.PageCount, b.Binding, b.OnlineRating, b.OnlineRatingCount
            FROM Books b
            {where}
            ORDER BY {GetLibrarySortSql(sortMode)}
            LIMIT $limit OFFSET $offset;
            """;
        AddLibrarySearchParameters(command, query, author, tag, format, category, collectionId, readingStatus);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)pageIndex * pageSize);

        var books = new List<Book>(Math.Min(pageSize, totalCount));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                books.Add(ReadBook(reader));
        }

        var filesByBook = await ReadFilesByBookAsync(connection, books.Select(book => book.Id), cancellationToken);
        var collectionsByBook = await ReadCollectionIdsByBookAsync(connection, books.Select(book => book.Id), cancellationToken);
        foreach (var book in books)
        {
            book.Files = filesByBook.GetValueOrDefault(book.Id) ?? [];
            book.CollectionIds = collectionsByBook.GetValueOrDefault(book.Id) ?? [];
        }

        return new LibraryPageResult(books, totalCount);
    }

    public async Task<IReadOnlyList<LibraryCollectionSummary>> GetCollectionSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.Name, c.CreatedAt, COUNT(i.BookId)
            FROM BookCollections c
            LEFT JOIN BookCollectionItems i ON i.CollectionId = c.Id
            GROUP BY c.Id, c.Name, c.CreatedAt
            ORDER BY c.Name COLLATE NOCASE;
            """;
        var summaries = new List<(BookCollection Collection, int Count)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                summaries.Add((new BookCollection
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(2))
                },
                reader.GetInt32(3)));
            }
        }

        var coverPathsByCollection = new Dictionary<Guid, List<string>>();
        var covers = connection.CreateCommand();
        covers.CommandText = """
            SELECT CollectionId, CoverPath FROM (
                SELECT i.CollectionId, b.CoverPath,
                       ROW_NUMBER() OVER (PARTITION BY i.CollectionId ORDER BY b.UpdatedAt DESC, b.Id) AS Position
                FROM BookCollectionItems i
                INNER JOIN Books b ON b.Id = i.BookId
                WHERE b.CoverPath IS NOT NULL AND trim(b.CoverPath) <> ''
            ) WHERE Position <= 3 ORDER BY CollectionId, Position;
            """;
        await using (var reader = await covers.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var collectionId = Guid.Parse(reader.GetString(0));
                if (!coverPathsByCollection.TryGetValue(collectionId, out var paths))
                {
                    paths = [];
                    coverPathsByCollection[collectionId] = paths;
                }
                if (paths.Count < 3) paths.Add(reader.GetString(1));
            }
        }

        return summaries
            .Select(item => new LibraryCollectionSummary(
                item.Collection,
                item.Count,
                coverPathsByCollection.GetValueOrDefault(item.Collection.Id) ?? []))
            .ToArray();
    }

    private async Task<IReadOnlyList<LibraryBookMatch>> GetLibraryMatchRecordsCoreAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var books = new Dictionary<Guid, LibraryBookMatch>();
        var bookCommand = connection.CreateCommand();
        bookCommand.CommandText = "SELECT Id, Title, Authors, Isbn FROM Books ORDER BY Id;";
        await using (var reader = await bookCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                books[id] = new LibraryBookMatch(id, reader.GetString(1), reader.GetString(2), [])
                {
                    Isbn = reader.IsDBNull(3) ? null : reader.GetString(3)
                };
            }
        }

        var fileCommand = connection.CreateCommand();
        fileCommand.CommandText = """
            SELECT BookId, RelativePath, Sha256
            FROM BookFiles
            ORDER BY BookId, Id;
            """;
        var filesByBook = new Dictionary<Guid, List<LibraryFileMatch>>();
        await using (var reader = await fileCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var bookId = Guid.Parse(reader.GetString(0));
                if (!filesByBook.TryGetValue(bookId, out var files))
                {
                    files = [];
                    filesByBook[bookId] = files;
                }
                files.Add(new LibraryFileMatch(reader.GetString(1), reader.GetString(2)));
            }
        }

        return books.Values
            .Select(book => book with { Files = filesByBook.GetValueOrDefault(book.Id) ?? [] })
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetBookTitlesAsync(
        IReadOnlyCollection<Guid> bookIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, string>();
        if (bookIds.Count == 0) return result;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var batch in bookIds.Distinct().Chunk(500))
        {
            var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$book{index}").ToArray();
            command.CommandText = $"SELECT Id, Title FROM Books WHERE Id IN ({string.Join(", ", parameters)});";
            for (var index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, LibraryBookDisplayInfo>> GetBookDisplayInfosAsync(
        IReadOnlyCollection<Guid> bookIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, LibraryBookDisplayInfo>();
        if (bookIds.Count == 0) return result;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var batch in bookIds.Distinct().Chunk(500))
        {
            var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$book{index}").ToArray();
            command.CommandText = $"SELECT Id, Title, CoverPath, 1 FROM ReadingMaterialBooks WHERE Id IN ({string.Join(", ", parameters)}) AND Id NOT IN (SELECT Id FROM Books) UNION ALL SELECT Id, Title, CoverPath, 0 FROM Books WHERE Id IN ({string.Join(", ", parameters)});";
            for (var index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                result[id] = new LibraryBookDisplayInfo(
                    id,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3) != 0);
            }
        }
        return result;
    }

    private static void AddFilterValues(HashSet<string> values, string? value, bool split = true)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var candidates = split
            ? value.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value.Trim()];
        foreach (var candidate in candidates)
        {
            var normalized = candidate.Trim();
            if (normalized.Length > 0) values.Add(normalized);
        }
    }

    private static string BuildLibrarySearchWhere(
        string? query,
        string? author,
        string? tag,
        string? format,
        string? category,
        Guid? collectionId,
        LibraryReadingStatus? readingStatus,
        bool? favorite)
    {
        var clauses = new List<string> { "WHERE 1 = 1" };
        clauses.Add("AND ($query = '' OR b.Title LIKE $like OR b.Authors LIKE $like OR b.Tags LIKE $like OR b.Series LIKE $like)");
        if (!string.IsNullOrWhiteSpace(author))
            clauses.Add("AND instr(',' || unicode_lower(replace(replace(replace(replace(coalesce(b.Authors, ''), '，', ','), ';', ','), '；', ','), ' ', '')) || ',', ',' || $authorToken || ',') > 0");
        if (!string.IsNullOrWhiteSpace(tag))
            clauses.Add("AND instr(',' || unicode_lower(replace(replace(replace(replace(coalesce(b.Tags, ''), '，', ','), ';', ','), '；', ','), ' ', '')) || ',', ',' || $tagToken || ',') > 0");
        if (!string.IsNullOrWhiteSpace(format))
            clauses.Add("AND EXISTS (SELECT 1 FROM BookFiles f WHERE f.BookId = b.Id AND lower(f.Format) = lower($format))");
        if (!string.IsNullOrWhiteSpace(category))
            clauses.Add("AND unicode_lower(trim(coalesce(b.Category, ''))) = unicode_lower(trim($category))");
        if (collectionId is not null)
            clauses.Add("AND EXISTS (SELECT 1 FROM BookCollectionItems ci WHERE ci.BookId = b.Id AND ci.CollectionId = $collectionId)");
        if (readingStatus is not null)
            clauses.Add("AND b.ReadingStatus = $readingStatus");
        if (favorite is not null)
            clauses.Add($"AND b.IsFavorite = {(favorite.Value ? 1 : 0)}");
        return string.Join("\n", clauses);
    }

    private static void AddLibrarySearchParameters(
        SqliteCommand command,
        string? query,
        string? author,
        string? tag,
        string? format,
        string? category,
        Guid? collectionId,
        LibraryReadingStatus? readingStatus)
    {
        var text = query?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("$query", text);
        command.Parameters.AddWithValue("$like", $"%{text}%");
        if (!string.IsNullOrWhiteSpace(author)) command.Parameters.AddWithValue("$authorToken", NormalizeFilterToken(author));
        if (!string.IsNullOrWhiteSpace(tag)) command.Parameters.AddWithValue("$tagToken", NormalizeFilterToken(tag));
        if (!string.IsNullOrWhiteSpace(format)) command.Parameters.AddWithValue("$format", format.Trim());
        if (!string.IsNullOrWhiteSpace(category)) command.Parameters.AddWithValue("$category", category.Trim());
        if (collectionId is not null) command.Parameters.AddWithValue("$collectionId", collectionId.Value.ToString());
        if (readingStatus is not null) command.Parameters.AddWithValue("$readingStatus", (int)readingStatus.Value);
    }

    private static string NormalizeFilterToken(string value) => value
        .Trim()
        .Replace('，', ',')
        .Replace('；', ';')
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();

    private static string GetLibrarySortSql(LibrarySortMode sortMode) => sortMode switch
    {
        LibrarySortMode.TitleAscending => "b.Title COLLATE NOCASE, b.Id",
        LibrarySortMode.AuthorAscending => "b.Authors COLLATE NOCASE, b.Title COLLATE NOCASE, b.Id",
        LibrarySortMode.CreatedDescending => "b.CreatedAt DESC, b.Title COLLATE NOCASE, b.Id",
        LibrarySortMode.ProgressDescending => "b.ReadingStatus DESC, b.UpdatedAt DESC, b.Title COLLATE NOCASE, b.Id",
        _ => "b.UpdatedAt DESC, b.Title COLLATE NOCASE, b.Id"
    };
}
