using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Annotations, book reflections, progress and bookmarks.
/// </summary>
public sealed partial class ReaderDataService
{
    public async Task<IReadOnlyList<ReaderAnnotation>> GetAnnotationsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations
            WHERE BookFileId = $bookFileId
            ORDER BY
                CASE WHEN ChapterPath LIKE 'pdf:page:%' THEN 0 ELSE 1 END,
                CASE WHEN ChapterPath LIKE 'pdf:page:%'
                     THEN CAST(substr(ChapterPath, 10) AS INTEGER)
                     ELSE 0 END,
                ChapterPath COLLATE NOCASE, StartOffset, CreatedAt
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 100_000));
        var result = new List<ReaderAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadAnnotation(reader));
        return result;
    }

    public async Task<IReadOnlyList<ReaderAnnotation>> GetAllAnnotationsAsync(
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations
            ORDER BY UpdatedAt DESC, BookId, ChapterPath COLLATE NOCASE, StartOffset
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 100_000));
        var result = new List<ReaderAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadAnnotation(reader));
        return result;
    }

    private static ReaderAnnotation ReadAnnotation(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        BookId = Guid.Parse(reader.GetString(1)),
        BookFileId = Guid.Parse(reader.GetString(2)),
        ChapterPath = reader.GetString(3),
        Fragment = reader.IsDBNull(4) ? null : reader.GetString(4),
        StartOffset = reader.GetInt32(5),
        EndOffset = reader.GetInt32(6),
        SelectedText = reader.GetString(7),
        Prefix = reader.GetString(8),
        Suffix = reader.GetString(9),
        Color = reader.GetString(10),
        UnderlineStyle = reader.GetString(11),
        Note = reader.GetString(12),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(14))
    };

    public async Task SaveAnnotationAsync(ReaderAnnotation annotation, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderAnnotations (
                    Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                    SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt)
                VALUES (
                    $id, $bookId, $bookFileId, $chapterPath, $fragment, $startOffset, $endOffset,
                    $selectedText, $prefix, $suffix, $color, $underlineStyle, $note, $createdAt, $updatedAt)
                ON CONFLICT(Id) DO UPDATE SET
                    ChapterPath=$chapterPath, Fragment=$fragment, StartOffset=$startOffset, EndOffset=$endOffset,
                    SelectedText=$selectedText, Prefix=$prefix, Suffix=$suffix, Color=$color,
                    UnderlineStyle=$underlineStyle,
                    Note=$note, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$id", annotation.Id.ToString());
            command.Parameters.AddWithValue("$bookId", annotation.BookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", annotation.BookFileId.ToString());
            command.Parameters.AddWithValue("$chapterPath", annotation.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)annotation.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$startOffset", annotation.StartOffset);
            command.Parameters.AddWithValue("$endOffset", annotation.EndOffset);
            command.Parameters.AddWithValue("$selectedText", annotation.SelectedText);
            command.Parameters.AddWithValue("$prefix", annotation.Prefix);
            command.Parameters.AddWithValue("$suffix", annotation.Suffix);
            command.Parameters.AddWithValue("$color", annotation.Color);
            command.Parameters.AddWithValue("$underlineStyle", annotation.UnderlineStyle);
            command.Parameters.AddWithValue("$note", annotation.Note);
            command.Parameters.AddWithValue("$createdAt", annotation.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", annotation.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.Annotation);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteAnnotationAsync(Guid annotationId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderAnnotations WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", annotationId.ToString());
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed > 0)
                NotifyDataChanged(LocalDataChangeKind.Annotation);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Book-level reflections.
    // ------------------------------------------------------------------

    public async Task<ReaderBookReflection?> GetBookReflectionAsync(
        Guid bookId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, Content, CreatedAt, UpdatedAt
            FROM ReaderBookReflections
            WHERE BookId = $bookId;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBookReflection(reader) : null;
    }

    public async Task<IReadOnlyList<ReaderBookReflection>> GetAllBookReflectionsAsync(
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, Content, CreatedAt, UpdatedAt
            FROM ReaderBookReflections
            ORDER BY UpdatedAt DESC, BookId
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 100_000));
        var result = new List<ReaderBookReflection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadBookReflection(reader));
        return result;
    }

    private static ReaderBookReflection ReadBookReflection(SqliteDataReader reader) => new()
    {
        BookId = Guid.Parse(reader.GetString(0)),
        Content = reader.GetString(1),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(3))
    };

    public async Task SaveBookReflectionAsync(
        ReaderBookReflection reflection,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderBookReflections (BookId, Content, CreatedAt, UpdatedAt)
                VALUES ($bookId, $content, $createdAt, $updatedAt)
                ON CONFLICT(BookId) DO UPDATE SET
                    Content = $content, CreatedAt = $createdAt, UpdatedAt = $updatedAt;
                """;
            command.Parameters.AddWithValue("$bookId", reflection.BookId.ToString());
            command.Parameters.AddWithValue("$content", reflection.Content);
            command.Parameters.AddWithValue("$createdAt", reflection.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", reflection.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.BookReflection);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteBookReflectionAsync(
        Guid bookId,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderBookReflections WHERE BookId = $bookId;";
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed > 0)
                NotifyDataChanged(LocalDataChangeKind.BookReflection);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Reading progress (breakpoint restore).
    // ------------------------------------------------------------------

    public async Task<ReaderProgressRow?> GetProgressAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                   ProgressPercent, FlowMode, UpdatedAt, ContentPositionJson
            FROM ReaderProgress
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderProgressRow(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetDouble(6),
            reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8)))
        {
            ContentPosition = ReaderContentPositionJson.Deserialize(reader.IsDBNull(9) ? null : reader.GetString(9))
        };
    }

    internal async Task<ReaderRecoveryContext> GetRecoveryContextAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var context = await ReaderRecoveryEpoch.ReadAsync(connection, transaction, cancellationToken);
        transaction.Commit();
        return CachedRecoveryContext = context;
    }

    public Task SaveProgressAsync(
        ReaderProgressRow progress,
        CancellationToken cancellationToken = default) =>
        SaveProgressCoreAsync(progress, false, null, cancellationToken);

    internal Task SaveRecoveredProgressAsync(
        ReaderProgressRow progress, ReaderRecoveryContext? context, CancellationToken cancellationToken) =>
        SaveProgressCoreAsync(progress, true, context, cancellationToken);

    private async Task SaveProgressCoreAsync(
        ReaderProgressRow progress, bool checkReset, ReaderRecoveryContext? expectedContext, CancellationToken cancellationToken)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            if (checkReset) await MakeRecoveryCommitDurableAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (checkReset && await ReaderRecoveryEpoch.ReadAsync(connection, transaction, cancellationToken) != expectedContext)
                return;

            // The comparison and write share a writer transaction, including
            // sub-millisecond timestamps and records written by sync/MCP.
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT UpdatedAt FROM ReaderProgress WHERE BookFileId = $bookFileId;";
                current.Parameters.AddWithValue("$bookFileId", progress.BookFileId.ToString());
                if (await current.ExecuteScalarAsync(cancellationToken) is string updatedAt
                    && DateTimeOffset.Parse(updatedAt) > progress.UpdatedAt)
                    return;
            }
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ReaderProgress (
                    BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                    ProgressPercent, FlowMode, UpdatedAt, ContentPositionJson)
                VALUES (
                    $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                    $progressPercent, $flowMode, $updatedAt, $contentPosition)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, ChapterPath=$chapterPath, Fragment=$fragment,
                    ChapterIndex=$chapterIndex, ScrollPosition=$scrollPosition,
                    ProgressPercent=$progressPercent, FlowMode=$flowMode, UpdatedAt=$updatedAt,
                    ContentPositionJson=$contentPosition;
                """;
            command.Parameters.AddWithValue("$bookFileId", progress.BookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", progress.BookId.ToString());
            command.Parameters.AddWithValue("$chapterPath", progress.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)progress.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapterIndex", progress.ChapterIndex);
            command.Parameters.AddWithValue("$scrollPosition", progress.ScrollPosition);
            command.Parameters.AddWithValue("$progressPercent", progress.ProgressPercent);
            command.Parameters.AddWithValue("$flowMode", progress.FlowMode);
            command.Parameters.AddWithValue("$updatedAt", progress.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$contentPosition", (object?)ReaderContentPositionJson.Serialize(progress.ContentPosition) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.ReadingProgress);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<ReaderSyncedPositionMarker?> GetSyncedPositionMarkerAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReaderSyncedPositionMarkerStore.GetAsync(connection, bookFileId, cancellationToken);
    }

    public async Task ClearSyncedPositionMarkerAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await ReaderSyncedPositionMarkerStore.ClearAsync(connection, bookFileId, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Bookmarks.
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ReaderBookmark>> GetBookmarksAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                   ScrollPosition, FlowMode, Title, Quote, CreatedAt, ContentPositionJson
            FROM ReaderBookmarks
            WHERE BookFileId = $bookFileId
            ORDER BY ChapterIndex, CreatedAt
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 100_000));
        var result = new List<ReaderBookmark>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReaderBookmark
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                BookFileId = Guid.Parse(reader.GetString(2)),
                ChapterPath = reader.GetString(3),
                Fragment = reader.IsDBNull(4) ? null : reader.GetString(4),
                ChapterIndex = reader.GetInt32(5),
                ScrollPosition = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                FlowMode = reader.GetInt32(7),
                Title = reader.GetString(8),
                Quote = reader.GetString(9),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(10)),
                ContentPosition = ReaderContentPositionJson.Deserialize(reader.IsDBNull(11) ? null : reader.GetString(11))
            });
        }
        return result;
    }

    public async Task SaveBookmarkAsync(ReaderBookmark bookmark, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderBookmarks (
                    Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                    ScrollPosition, FlowMode, Title, Quote, CreatedAt, ContentPositionJson)
                VALUES (
                    $id, $bookId, $bookFileId, $chapterPath, $fragment, $chapterIndex,
                    $scrollPosition, $flowMode, $title, $quote, $createdAt, $contentPosition)
                ON CONFLICT(Id) DO UPDATE SET
                    BookId=$bookId, BookFileId=$bookFileId, ChapterPath=$chapterPath, Fragment=$fragment,
                    ChapterIndex=$chapterIndex, ScrollPosition=$scrollPosition, FlowMode=$flowMode,
                    Title=$title, Quote=$quote, CreatedAt=$createdAt, ContentPositionJson=$contentPosition;
                """;
            command.Parameters.AddWithValue("$id", bookmark.Id.ToString());
            command.Parameters.AddWithValue("$bookId", bookmark.BookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", bookmark.BookFileId.ToString());
            command.Parameters.AddWithValue("$chapterPath", bookmark.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)bookmark.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapterIndex", bookmark.ChapterIndex);
            command.Parameters.AddWithValue("$scrollPosition", (object?)bookmark.ScrollPosition ?? DBNull.Value);
            command.Parameters.AddWithValue("$flowMode", bookmark.FlowMode);
            command.Parameters.AddWithValue("$title", bookmark.Title);
            command.Parameters.AddWithValue("$quote", bookmark.Quote);
            command.Parameters.AddWithValue("$createdAt", bookmark.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$contentPosition", (object?)ReaderContentPositionJson.Serialize(bookmark.ContentPosition) ?? DBNull.Value);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed > 0)
                NotifyDataChanged(LocalDataChangeKind.Bookmark);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteBookmarkAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderBookmarks WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", bookmarkId.ToString());
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed > 0)
                NotifyDataChanged(LocalDataChangeKind.Bookmark);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Per-book layout settings.
    // ------------------------------------------------------------------
}
