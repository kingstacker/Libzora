using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Local duplicate book and file consolidation.
/// </summary>
public sealed partial class S3SyncService
{
    private async Task<int> ConsolidateLocalDuplicateBooksInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var books = new List<LocalDuplicateBook>();
        using (var command = CreateCommand(connection, transaction,
            """
            SELECT b.Id, b.Title, b.Authors, b.CreatedAt, b.UpdatedAt, b.CoverPath,
                   (SELECT COUNT(*) FROM BookFiles f WHERE f.BookId = b.Id)
            FROM Books b;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                books.Add(new LocalDuplicateBook(
                    ParseGuid(reader.GetString(0), "Books.Id"),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseTimestamp(reader.GetString(3)),
                    ParseTimestamp(reader.GetString(4)),
                    NullableString(reader, 5),
                    reader.GetInt64(6)));
            }
        }

        var fileHashesByBook = new Dictionary<Guid, HashSet<string>>();
        using (var command = CreateCommand(connection, transaction,
            "SELECT BookId, Sha256 FROM BookFiles;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var bookId = ParseGuid(reader.GetString(0), "BookFiles.BookId");
                var hash = reader.GetString(1);
                if (!IsSha256(hash)) continue;
                if (!fileHashesByBook.TryGetValue(bookId, out var hashes))
                    fileHashesByBook[bookId] = hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                hashes.Add(hash);
            }
        }

        var parentByBookId = books.ToDictionary(book => book.Id, book => book.Id);
        Guid FindRoot(Guid id)
        {
            var parent = parentByBookId[id];
            if (parent != id) parentByBookId[id] = FindRoot(parent);
            return parentByBookId[id];
        }

        void Union(Guid left, Guid right)
        {
            var leftRoot = FindRoot(left);
            var rightRoot = FindRoot(right);
            if (leftRoot != rightRoot) parentByBookId[rightRoot] = leftRoot;
        }

        foreach (var group in fileHashesByBook
                     .SelectMany(entry => entry.Value.Select(hash => (Hash: hash, BookId: entry.Key)))
                     .GroupBy(entry => entry.Hash, StringComparer.OrdinalIgnoreCase))
        {
            var bookIds = group.Select(entry => entry.BookId).Distinct().ToArray();
            for (var index = 1; index < bookIds.Length; index++)
                Union(bookIds[0], bookIds[index]);
        }

        var merged = 0;
        foreach (var group in books
                     .GroupBy(book => FindRoot(book.Id))
                     .Where(group => group.Count() > 1))
        {
            var canonical = group
                .OrderByDescending(book => book.FileCount)
                .ThenByDescending(book => book.UpdatedAt)
                .ThenBy(book => book.CreatedAt)
                .ThenBy(book => book.Id)
                .First();

            foreach (var duplicate in group.Where(book => book.Id != canonical.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MergeDuplicateBookAsync(
                    connection,
                    transaction,
                    canonical,
                    duplicate,
                    pathsToDelete,
                    commandCache,
                    cancellationToken);
                merged++;
            }
        }

        return merged;
    }

    private async Task MergeDuplicateBookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalDuplicateBook canonical,
        LocalDuplicateBook duplicate,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var canonicalCoverPath = string.IsNullOrWhiteSpace(canonical.CoverPath)
            ? duplicate.CoverPath
            : canonical.CoverPath;

        var mergeMetadata = commandCache.Get(
            """
            UPDATE Books
            SET Series = CASE WHEN NULLIF(Series, '') IS NULL
                              THEN (SELECT Series FROM Books WHERE Id = $duplicate)
                              ELSE Series END,
                SeriesIndex = COALESCE(SeriesIndex,
                              (SELECT SeriesIndex FROM Books WHERE Id = $duplicate)),
                Description = CASE WHEN NULLIF(Description, '') IS NULL
                                   THEN (SELECT Description FROM Books WHERE Id = $duplicate)
                                   ELSE Description END,
                Publisher = CASE WHEN NULLIF(Publisher, '') IS NULL
                                 THEN (SELECT Publisher FROM Books WHERE Id = $duplicate)
                                 ELSE Publisher END,
                PublishDate = CASE WHEN NULLIF(PublishDate, '') IS NULL
                                   THEN (SELECT PublishDate FROM Books WHERE Id = $duplicate)
                                   ELSE PublishDate END,
                Isbn = CASE WHEN NULLIF(Isbn, '') IS NULL
                            THEN (SELECT Isbn FROM Books WHERE Id = $duplicate)
                            ELSE Isbn END,
                PageCount = CASE WHEN NULLIF(PageCount, '') IS NULL
                                 THEN (SELECT PageCount FROM Books WHERE Id = $duplicate)
                                 ELSE PageCount END,
                Binding = CASE WHEN NULLIF(Binding, '') IS NULL
                               THEN (SELECT Binding FROM Books WHERE Id = $duplicate)
                               ELSE Binding END,
                OnlineRating = COALESCE(OnlineRating,
                              (SELECT OnlineRating FROM Books WHERE Id = $duplicate)),
                OnlineRatingCount = COALESCE(OnlineRatingCount,
                              (SELECT OnlineRatingCount FROM Books WHERE Id = $duplicate)),
                Tags = CASE WHEN NULLIF(Tags, '') IS NULL
                            THEN COALESCE((SELECT NULLIF(Tags, '') FROM Books WHERE Id = $duplicate), '')
                            ELSE Tags END,
                Category = CASE WHEN NULLIF(Category, '') IS NULL
                                THEN COALESCE((SELECT NULLIF(Category, '') FROM Books WHERE Id = $duplicate), '')
                                ELSE Category END,
                IsFavorite = CASE WHEN IsFavorite <> 0
                                       OR COALESCE((SELECT IsFavorite FROM Books WHERE Id = $duplicate), 0) <> 0
                                  THEN 1 ELSE 0 END,
                ReadingStatus = CASE WHEN julianday((SELECT UpdatedAt FROM Books WHERE Id = $duplicate))
                                           > julianday(UpdatedAt)
                                     THEN (SELECT ReadingStatus FROM Books WHERE Id = $duplicate)
                                     ELSE ReadingStatus END,
                CoverPath = CASE WHEN NULLIF(CoverPath, '') IS NULL
                                 THEN (SELECT CoverPath FROM Books WHERE Id = $duplicate)
                                 ELSE CoverPath END,
                CreatedAt = CASE WHEN julianday((SELECT CreatedAt FROM Books WHERE Id = $duplicate))
                                       < julianday(CreatedAt)
                                THEN (SELECT CreatedAt FROM Books WHERE Id = $duplicate)
                                ELSE CreatedAt END,
                UpdatedAt = CASE WHEN julianday((SELECT UpdatedAt FROM Books WHERE Id = $duplicate))
                                       > julianday(UpdatedAt)
                                THEN (SELECT UpdatedAt FROM Books WHERE Id = $duplicate)
                                ELSE UpdatedAt END
            WHERE Id = $canonical;
            """);
        AddParameter(mergeMetadata, "$canonical", canonical.Id.ToString());
        AddParameter(mergeMetadata, "$duplicate", duplicate.Id.ToString());
        await mergeMetadata.ExecuteNonQueryAsync(cancellationToken);

        await MergeDuplicateBookReflectionAsync(
            connection,
            transaction,
            canonical.Id,
            duplicate.Id,
            commandCache,
            cancellationToken);

        var copyCollections = commandCache.Get(
            """
            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT CollectionId, $canonical, AddedAt
            FROM BookCollectionItems
            WHERE BookId = $duplicate;
            """);
        AddParameter(copyCollections, "$canonical", canonical.Id.ToString());
        AddParameter(copyCollections, "$duplicate", duplicate.Id.ToString());
        await copyCollections.ExecuteNonQueryAsync(cancellationToken);

        var deleteCollections = commandCache.Get(
            "DELETE FROM BookCollectionItems WHERE BookId = $duplicate;");
        AddParameter(deleteCollections, "$duplicate", duplicate.Id.ToString());
        await deleteCollections.ExecuteNonQueryAsync(cancellationToken);

        var duplicateFiles = await ReadLocalDuplicateFilesAsync(
            connection,
            transaction,
            duplicate.Id,
            commandCache,
            cancellationToken);
        var canonicalFiles = await ReadLocalDuplicateFilesAsync(
            connection,
            transaction,
            canonical.Id,
            commandCache,
            cancellationToken);
        var canonicalFilesByHash = canonicalFiles
            .Where(file => IsSha256(file.Sha256))
            .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicateFile in duplicateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSha256(duplicateFile.Sha256)
                && canonicalFilesByHash.TryGetValue(duplicateFile.Sha256, out var canonicalFile))
            {
                await MergeDuplicateFileRowsAsync(
                    connection,
                    transaction,
                    duplicateFile.Id,
                    canonicalFile.Id,
                    canonical.Id,
                    commandCache,
                    cancellationToken);

                var deleteFile = commandCache.Get(
                    "DELETE FROM BookFiles WHERE Id = $fileId;");
                AddParameter(deleteFile, "$fileId", duplicateFile.Id.ToString());
                await deleteFile.ExecuteNonQueryAsync(cancellationToken);

                if (!string.Equals(
                        duplicateFile.RelativePath,
                        canonicalFile.RelativePath,
                        StringComparison.OrdinalIgnoreCase)
                    && !await IsRelativeDataPathReferencedAsync(
                        connection,
                        transaction,
                        duplicateFile.RelativePath,
                        cancellationToken))
                {
                    var absolute = ResolveDataPath(duplicateFile.RelativePath);
                    if (absolute is not null) pathsToDelete.Add(absolute);
                }
            }
            else
            {
                await UpdateFileBookReferencesAsync(
                    connection,
                    transaction,
                    duplicateFile.Id,
                    canonical.Id,
                    commandCache,
                    cancellationToken);

                var rebindFile = commandCache.Get(
                    "UPDATE BookFiles SET BookId = $canonical WHERE Id = $fileId;");
                AddParameter(rebindFile, "$canonical", canonical.Id.ToString());
                AddParameter(rebindFile, "$fileId", duplicateFile.Id.ToString());
                await rebindFile.ExecuteNonQueryAsync(cancellationToken);
                if (IsSha256(duplicateFile.Sha256))
                    canonicalFilesByHash[duplicateFile.Sha256] = duplicateFile;
            }
        }

        foreach (var statement in new[]
        {
            "UPDATE ReaderAnnotations SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderProgress SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderBookmarks SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderLayoutSettings SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderReadingStats SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE ReaderReadingSessions SET BookId = $canonical WHERE BookId = $duplicate;",
            "UPDATE BookContentChunks SET BookId = $canonical WHERE BookId = $duplicate;"
        })
        {
            var update = commandCache.Get(statement);
            AddParameter(update, "$canonical", canonical.Id.ToString());
            AddParameter(update, "$duplicate", duplicate.Id.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var deleteBook = commandCache.Get("DELETE FROM Books WHERE Id = $duplicate;");
        AddParameter(deleteBook, "$duplicate", duplicate.Id.ToString());
        await deleteBook.ExecuteNonQueryAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(duplicate.CoverPath)
            && !string.Equals(duplicate.CoverPath, canonicalCoverPath, StringComparison.OrdinalIgnoreCase)
            && !await IsRelativeDataPathReferencedAsync(
                connection,
                transaction,
                duplicate.CoverPath,
                cancellationToken))
        {
            var absolute = ResolveDataPath(duplicate.CoverPath);
            if (absolute is not null) pathsToDelete.Add(absolute);
        }
    }

    private static async Task MergeDuplicateBookReflectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid canonicalBookId,
        Guid duplicateBookId,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var copy = commandCache.Get(
            """
            INSERT OR IGNORE INTO ReaderBookReflections (BookId, Content, CreatedAt, UpdatedAt)
            SELECT $canonical, Content, CreatedAt, UpdatedAt
            FROM ReaderBookReflections
            WHERE BookId = $duplicate;
            """
            );
        AddParameter(copy, "$canonical", canonicalBookId.ToString());
        AddParameter(copy, "$duplicate", duplicateBookId.ToString());
        await copy.ExecuteNonQueryAsync(cancellationToken);

        var update = commandCache.Get(
            """
            UPDATE ReaderBookReflections
            SET Content = (SELECT Content FROM ReaderBookReflections WHERE BookId = $duplicate),
                CreatedAt = CASE WHEN julianday((SELECT CreatedAt FROM ReaderBookReflections WHERE BookId = $duplicate))
                                      < julianday(CreatedAt)
                                 THEN (SELECT CreatedAt FROM ReaderBookReflections WHERE BookId = $duplicate)
                                 ELSE CreatedAt END,
                UpdatedAt = (SELECT UpdatedAt FROM ReaderBookReflections WHERE BookId = $duplicate)
            WHERE BookId = $canonical
              AND EXISTS (SELECT 1 FROM ReaderBookReflections WHERE BookId = $duplicate)
              AND julianday((SELECT UpdatedAt FROM ReaderBookReflections WHERE BookId = $duplicate))
                  > julianday(UpdatedAt);
            """
            );
        AddParameter(update, "$canonical", canonicalBookId.ToString());
        AddParameter(update, "$duplicate", duplicateBookId.ToString());
        await update.ExecuteNonQueryAsync(cancellationToken);

        var delete = commandCache.Get(
            "DELETE FROM ReaderBookReflections WHERE BookId = $duplicate;");
        AddParameter(delete, "$duplicate", duplicateBookId.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}
