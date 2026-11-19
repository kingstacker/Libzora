using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Book content index replacement and completion.
/// </summary>
public sealed partial class ReaderDataService
{
    public async Task<bool> IsIndexCurrentAsync(
        Guid bookFileId,
        string sourceHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                WHEN EXISTS(
                    SELECT 1 FROM BookContentIndexStates
                    WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                        AND TextExtractionVersion = $textVersion AND IsComplete = 1) THEN 1
                WHEN EXISTS(
                    SELECT 1 FROM BookContentIndexStates
                    WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                        AND TextExtractionVersion = $textVersion) THEN 0
                -- Builds before the chapter-state tables wrote every chapter
                -- in one transaction, so their chunks imply a complete index.
                WHEN EXISTS(
                    SELECT 1 FROM BookContentChunks
                    WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                        AND TextExtractionVersion = $textVersion LIMIT 1) THEN 1
                ELSE 0
            END;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        command.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task MakeRecoveryCommitDurableAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task ReplaceBookChunksAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        IReadOnlyList<BookContentChunkDraft> chunks,
        CancellationToken cancellationToken = default)
    {
        await ReplaceBookChunksStreamingAsync(
            bookId,
            bookFileId,
            sourceHash,
            ToAsyncEnumerable(chunks, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Replaces an EPUB's chunks while consuming them incrementally. The
    /// caller can parse one chapter at a time without retaining the entire
    /// book's extracted text and chunk list.
    /// </summary>
    public async Task<int> ReplaceBookChunksStreamingAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        IAsyncEnumerable<BookContentChunkDraft> chunks,
        CancellationToken cancellationToken = default,
        int totalChapters = 0)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var deleteEmbeddings = connection.CreateCommand();
            deleteEmbeddings.Transaction = (SqliteTransaction)transaction;
            deleteEmbeddings.CommandText = "DELETE FROM BookChunkEmbeddings WHERE BookFileId = $bookFileId;";
            deleteEmbeddings.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            await deleteEmbeddings.ExecuteNonQueryAsync(cancellationToken);

            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM BookContentChunks WHERE BookFileId = $bookFileId;";
            delete.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);

            var deleteStates = connection.CreateCommand();
            deleteStates.Transaction = (SqliteTransaction)transaction;
            deleteStates.CommandText = """
                DELETE FROM BookContentIndexStates WHERE BookFileId = $bookFileId;
                DELETE FROM BookContentIndexChapters WHERE BookFileId = $bookFileId;
                """;
            deleteStates.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            await deleteStates.ExecuteNonQueryAsync(cancellationToken);

            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO BookContentChunks (
                    BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                    ChapterPath, StartOffset, EndOffset, Content, TextExtractionVersion)
                VALUES (
                    $bookId, $bookFileId, $sourceHash, $chapterIndex, $chunkIndex, $chapterTitle,
                    $chapterPath, $startOffset, $endOffset, $content, $textVersion);
                """;
            insert.Parameters.Add("$bookId", SqliteType.Text);
            insert.Parameters.Add("$bookFileId", SqliteType.Text);
            insert.Parameters.Add("$sourceHash", SqliteType.Text);
            insert.Parameters.Add("$chapterIndex", SqliteType.Integer);
            insert.Parameters.Add("$chunkIndex", SqliteType.Integer);
            insert.Parameters.Add("$chapterTitle", SqliteType.Text);
            insert.Parameters.Add("$chapterPath", SqliteType.Text);
            insert.Parameters.Add("$startOffset", SqliteType.Integer);
            insert.Parameters.Add("$endOffset", SqliteType.Integer);
            insert.Parameters.Add("$content", SqliteType.Text);
            insert.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);

            var count = 0;
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                insert.Parameters["$bookId"].Value = bookId.ToString();
                insert.Parameters["$bookFileId"].Value = bookFileId.ToString();
                insert.Parameters["$sourceHash"].Value = sourceHash;
                insert.Parameters["$chapterIndex"].Value = chunk.ChapterIndex;
                insert.Parameters["$chunkIndex"].Value = chunk.ChunkIndex;
                insert.Parameters["$chapterTitle"].Value = chunk.ChapterTitle;
                insert.Parameters["$chapterPath"].Value = chunk.ChapterPath;
                insert.Parameters["$startOffset"].Value = chunk.StartOffset;
                insert.Parameters["$endOffset"].Value = chunk.EndOffset;
                insert.Parameters["$content"].Value = chunk.Content;
                await insert.ExecuteNonQueryAsync(cancellationToken);
                count++;
            }

            var state = connection.CreateCommand();
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                INSERT INTO BookContentIndexStates (
                    BookFileId, SourceHash, TextExtractionVersion, TotalChapters, IsComplete, UpdatedAt)
                VALUES ($bookFileId, $sourceHash, $textVersion, $totalChapters, 1, $updatedAt);
                """;
            state.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            state.Parameters.AddWithValue("$sourceHash", sourceHash);
            state.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
            state.Parameters.AddWithValue("$totalChapters", Math.Max(0, totalChapters));
            state.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await state.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return count;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>
    /// Reports which chapters of one book file are already in the local text
    /// index. Legacy indexes written before chapter state existed are adopted
    /// as complete, because those builds inserted every chapter atomically.
    /// </summary>
    public async Task<BookContentIndexSnapshot> GetBookContentIndexSnapshotAsync(
        Guid bookFileId,
        string sourceHash,
        CancellationToken cancellationToken = default)
    {
        var fileId = bookFileId.ToString();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.CommandText = """
                SELECT IsComplete FROM BookContentIndexStates
                WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                    AND TextExtractionVersion = $textVersion;
                """;
            stateCommand.Parameters.AddWithValue("$bookFileId", fileId);
            stateCommand.Parameters.AddWithValue("$sourceHash", sourceHash);
            stateCommand.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
            var state = await stateCommand.ExecuteScalarAsync(cancellationToken);
            if (state is not null)
            {
                return new BookContentIndexSnapshot(
                    Convert.ToInt32(state) == 1,
                    await ReadIndexedChapterIndexesAsync(connection, fileId, sourceHash, cancellationToken));
            }
        }

        var legacyChapters = new HashSet<int>();
        using (var legacyCommand = connection.CreateCommand())
        {
            legacyCommand.CommandText = """
                SELECT DISTINCT ChapterIndex FROM BookContentChunks
                WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                    AND TextExtractionVersion = $textVersion;
                """;
            legacyCommand.Parameters.AddWithValue("$bookFileId", fileId);
            legacyCommand.Parameters.AddWithValue("$sourceHash", sourceHash);
            legacyCommand.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
            await using var reader = await legacyCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                legacyChapters.Add(reader.GetInt32(0));
        }

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connectionWrite = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connectionWrite.BeginTransactionAsync(cancellationToken);
            if (legacyChapters.Count == 0)
            {
                // A hash or extraction change makes the previous index
                // unreachable; drop it now so storage does not leak and FTS
                // callers that omit a source hash cannot see stale chapters.
                using var cleanup = connectionWrite.CreateCommand();
                cleanup.Transaction = (SqliteTransaction)transaction;
                cleanup.CommandText = """
                    DELETE FROM BookChunkEmbeddings
                    WHERE BookFileId = $bookFileId
                      AND (SourceHash <> $sourceHash OR ChunkId IN (
                          SELECT Id FROM BookContentChunks
                          WHERE BookFileId = $bookFileId AND TextExtractionVersion <> $textVersion));
                    DELETE FROM BookContentChunks
                    WHERE BookFileId = $bookFileId
                      AND (SourceHash <> $sourceHash OR TextExtractionVersion <> $textVersion);
                    DELETE FROM BookContentIndexChapters
                    WHERE BookFileId = $bookFileId
                      AND (SourceHash <> $sourceHash OR TextExtractionVersion <> $textVersion);
                    DELETE FROM BookContentIndexStates
                    WHERE BookFileId = $bookFileId
                      AND (SourceHash <> $sourceHash OR TextExtractionVersion <> $textVersion);
                    """;
                cleanup.Parameters.AddWithValue("$bookFileId", fileId);
                cleanup.Parameters.AddWithValue("$sourceHash", sourceHash);
                cleanup.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                using var migrate = connectionWrite.CreateCommand();
                migrate.Transaction = (SqliteTransaction)transaction;
                migrate.CommandText = """
                    INSERT OR REPLACE INTO BookContentIndexStates (
                        BookFileId, SourceHash, TextExtractionVersion, TotalChapters, IsComplete, UpdatedAt)
                    VALUES ($bookFileId, $sourceHash, $textVersion, $totalChapters, 1, $updatedAt);
                    INSERT OR REPLACE INTO BookContentIndexChapters (
                        BookFileId, SourceHash, TextExtractionVersion, ChapterIndex, UpdatedAt)
                    VALUES ($bookFileId, $sourceHash, $textVersion, $chapterIndex, $updatedAt);
                    """;
                migrate.Parameters.AddWithValue("$bookFileId", fileId);
                migrate.Parameters.AddWithValue("$sourceHash", sourceHash);
                migrate.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
                migrate.Parameters.AddWithValue("$totalChapters", legacyChapters.Count);
                migrate.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                var chapterParameter = migrate.Parameters.Add("$chapterIndex", SqliteType.Integer);
                foreach (var chapterIndex in legacyChapters)
                {
                    chapterParameter.Value = chapterIndex;
                    await migrate.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }

        return legacyChapters.Count > 0
            ? new BookContentIndexSnapshot(true, legacyChapters)
            : new BookContentIndexSnapshot(false, new HashSet<int>());
    }

    private static async Task<IReadOnlySet<int>> ReadIndexedChapterIndexesAsync(
        SqliteConnection connection,
        string bookFileId,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ChapterIndex FROM BookContentIndexChapters
            WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                AND TextExtractionVersion = $textVersion;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId);
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        command.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
        var chapters = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            chapters.Add(reader.GetInt32(0));
        return chapters;
    }

    /// <summary>
    /// Creates the progress marker before the first chapter is written. Once
    /// this row exists, <see cref="IsIndexCurrentAsync"/> no longer treats the
    /// chunks as a complete legacy index.
    /// </summary>
    public async Task EnsureBookContentIndexStateAsync(
        Guid bookFileId,
        string sourceHash,
        int totalChapters,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BookContentIndexStates (
                    BookFileId, SourceHash, TextExtractionVersion, TotalChapters, IsComplete, UpdatedAt)
                VALUES ($bookFileId, $sourceHash, $textVersion, $totalChapters, 0, $updatedAt)
                ON CONFLICT(BookFileId, SourceHash, TextExtractionVersion) DO UPDATE SET
                    TotalChapters = MAX(TotalChapters, excluded.TotalChapters),
                    UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$sourceHash", sourceHash);
            command.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
            command.Parameters.AddWithValue("$totalChapters", Math.Max(0, totalChapters));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>
    /// Replaces one chapter's chunks in its own transaction so a progressive
    /// search can publish results without waiting for the rest of the book.
    /// </summary>
    public async Task<int> ReplaceBookChapterChunksAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        int chapterIndex,
        IReadOnlyList<BookContentChunkDraft> chunks,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM BookChunkEmbeddings
                WHERE BookFileId = $bookFileId
                  AND ChunkId IN (
                      SELECT Id FROM BookContentChunks
                      WHERE BookFileId = $bookFileId AND ChapterIndex = $chapterIndex);
                DELETE FROM BookContentChunks
                WHERE BookFileId = $bookFileId AND ChapterIndex = $chapterIndex;
                """;
            delete.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            delete.Parameters.AddWithValue("$chapterIndex", chapterIndex);
            await delete.ExecuteNonQueryAsync(cancellationToken);

            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO BookContentChunks (
                    BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                    ChapterPath, StartOffset, EndOffset, Content, TextExtractionVersion)
                VALUES (
                    $bookId, $bookFileId, $sourceHash, $chapterIndex, $chunkIndex, $chapterTitle,
                    $chapterPath, $startOffset, $endOffset, $content, $textVersion);
                """;
            insert.Parameters.AddWithValue("$bookId", bookId.ToString());
            insert.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            insert.Parameters.AddWithValue("$sourceHash", sourceHash);
            insert.Parameters.AddWithValue("$chapterIndex", chapterIndex);
            insert.Parameters.Add("$chunkIndex", SqliteType.Integer);
            insert.Parameters.Add("$chapterTitle", SqliteType.Text);
            insert.Parameters.Add("$chapterPath", SqliteType.Text);
            insert.Parameters.Add("$startOffset", SqliteType.Integer);
            insert.Parameters.Add("$endOffset", SqliteType.Integer);
            insert.Parameters.Add("$content", SqliteType.Text);
            insert.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);

            foreach (var chunk in chunks)
            {
                insert.Parameters["$chunkIndex"].Value = chunk.ChunkIndex;
                insert.Parameters["$chapterTitle"].Value = chunk.ChapterTitle;
                insert.Parameters["$chapterPath"].Value = chunk.ChapterPath;
                insert.Parameters["$startOffset"].Value = chunk.StartOffset;
                insert.Parameters["$endOffset"].Value = chunk.EndOffset;
                insert.Parameters["$content"].Value = chunk.Content;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var chapterState = connection.CreateCommand();
            chapterState.Transaction = (SqliteTransaction)transaction;
            chapterState.CommandText = """
                INSERT OR REPLACE INTO BookContentIndexChapters (
                    BookFileId, SourceHash, TextExtractionVersion, ChapterIndex, UpdatedAt)
                VALUES ($bookFileId, $sourceHash, $textVersion, $chapterIndex, $updatedAt);
                UPDATE BookContentIndexStates SET UpdatedAt = $updatedAt
                WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                    AND TextExtractionVersion = $textVersion;
                """;
            chapterState.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            chapterState.Parameters.AddWithValue("$sourceHash", sourceHash);
            chapterState.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
            chapterState.Parameters.AddWithValue("$chapterIndex", chapterIndex);
            chapterState.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await chapterState.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return chunks.Count;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>
    /// Marks the progressive index complete only when every chapter has a
    /// state row. A result cap or cancellation therefore leaves the index
    /// resumable instead of claiming a partial index is current.
    /// </summary>
    public async Task<bool> MarkBookContentIndexCompleteAsync(
        Guid bookFileId,
        string sourceHash,
        int totalChapters,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var fileId = bookFileId.ToString();
            var parameters = new
            {
                FileId = fileId,
                Hash = sourceHash,
                Version = CurrentTextExtractionVersion,
                Total = Math.Max(0, totalChapters),
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            using (var state = connection.CreateCommand())
            {
                state.Transaction = (SqliteTransaction)transaction;
                state.CommandText = """
                    INSERT INTO BookContentIndexStates (
                        BookFileId, SourceHash, TextExtractionVersion, TotalChapters, IsComplete, UpdatedAt)
                    VALUES ($bookFileId, $sourceHash, $textVersion, $totalChapters, 0, $updatedAt)
                    ON CONFLICT(BookFileId, SourceHash, TextExtractionVersion) DO UPDATE SET
                        TotalChapters = MAX(TotalChapters, excluded.TotalChapters);
                    """;
                state.Parameters.AddWithValue("$bookFileId", parameters.FileId);
                state.Parameters.AddWithValue("$sourceHash", parameters.Hash);
                state.Parameters.AddWithValue("$textVersion", parameters.Version);
                state.Parameters.AddWithValue("$totalChapters", parameters.Total);
                state.Parameters.AddWithValue("$updatedAt", parameters.UpdatedAt);
                await state.ExecuteNonQueryAsync(cancellationToken);
            }

            var indexed = 0;
            using (var count = connection.CreateCommand())
            {
                count.Transaction = (SqliteTransaction)transaction;
                count.CommandText = """
                    SELECT COUNT(*) FROM BookContentIndexChapters
                    WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                        AND TextExtractionVersion = $textVersion;
                    """;
                count.Parameters.AddWithValue("$bookFileId", parameters.FileId);
                count.Parameters.AddWithValue("$sourceHash", parameters.Hash);
                count.Parameters.AddWithValue("$textVersion", parameters.Version);
                indexed = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
            }

            var complete = parameters.Total <= 0 || indexed >= parameters.Total;
            using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE BookContentIndexStates
                    SET TotalChapters = $totalChapters, IsComplete = $isComplete, UpdatedAt = $updatedAt
                    WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash
                        AND TextExtractionVersion = $textVersion;
                    """;
                update.Parameters.AddWithValue("$bookFileId", parameters.FileId);
                update.Parameters.AddWithValue("$sourceHash", parameters.Hash);
                update.Parameters.AddWithValue("$textVersion", parameters.Version);
                update.Parameters.AddWithValue("$totalChapters", parameters.Total);
                update.Parameters.AddWithValue("$isComplete", complete ? 1 : 0);
                update.Parameters.AddWithValue("$updatedAt", parameters.UpdatedAt);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return complete;
        }
        finally
        {
            _databaseGate.Release();
        }
    }
}
