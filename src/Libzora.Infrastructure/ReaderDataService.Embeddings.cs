using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Chunk embeddings and vector lookups.
/// </summary>
public sealed partial class ReaderDataService
{
    private static async IAsyncEnumerable<BookContentChunkDraft> ToAsyncEnumerable(
        IReadOnlyList<BookContentChunkDraft> chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Returns chunks that do not have an embedding for the requested model
    /// version. The query is intentionally scoped to one BookFile and source
    /// hash so a changed EPUB never reuses vectors from an older index.
    /// </summary>
    public async Task<IReadOnlyList<BookContentChunk>> GetChunksNeedingEmbeddingsAsync(
        Guid bookFileId,
        string sourceHash,
        string embeddingModel,
        int embeddingDimension,
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.BookId, c.BookFileId, c.SourceHash, c.ChapterIndex, c.ChunkIndex,
                   c.ChapterTitle, c.ChapterPath, c.StartOffset, c.EndOffset, c.Content
            FROM BookContentChunks c
            LEFT JOIN BookChunkEmbeddings e ON e.ChunkId = c.Id
            WHERE c.BookFileId = $bookFileId
              AND c.SourceHash = $sourceHash
              AND (
                    e.ChunkId IS NULL
                    OR e.SourceHash <> $sourceHash
                    OR e.EmbeddingModel <> $embeddingModel
                    OR e.EmbeddingDimension <> $embeddingDimension)
            ORDER BY c.ChapterIndex, c.ChunkIndex, c.Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        command.Parameters.AddWithValue("$embeddingModel", embeddingModel);
        command.Parameters.AddWithValue("$embeddingDimension", embeddingDimension);
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 1000));

        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadChunk(reader));
        return result;
    }

    public async Task<int> CountChunksNeedingEmbeddingsAsync(
        Guid bookFileId,
        string sourceHash,
        string embeddingModel,
        int embeddingDimension,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM BookContentChunks c
            LEFT JOIN BookChunkEmbeddings e ON e.ChunkId = c.Id
            WHERE c.BookFileId = $bookFileId
              AND c.SourceHash = $sourceHash
              AND (
                    e.ChunkId IS NULL
                    OR e.SourceHash <> $sourceHash
                    OR e.EmbeddingModel <> $embeddingModel
                    OR e.EmbeddingDimension <> $embeddingDimension);
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        command.Parameters.AddWithValue("$embeddingModel", embeddingModel);
        command.Parameters.AddWithValue("$embeddingDimension", embeddingDimension);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// Persists a batch of vectors atomically. The vector bytes are stored as
    /// little-endian float32 values, which keeps the database portable across
    /// the supported desktop platforms.
    /// </summary>
    public async Task UpsertChunkEmbeddingsAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        string embeddingModel,
        int embeddingDimension,
        IReadOnlyList<ReaderChunkEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        if (embeddings.Count == 0) return;
        if (embeddingDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(embeddingDimension));

        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO BookChunkEmbeddings (
                    ChunkId, BookId, BookFileId, SourceHash, Embedding,
                    EmbeddingModel, EmbeddingDimension, UpdatedAt)
                VALUES (
                    $chunkId, $bookId, $bookFileId, $sourceHash, $embedding,
                    $embeddingModel, $embeddingDimension, $updatedAt)
                ON CONFLICT(ChunkId) DO UPDATE SET
                    BookId = excluded.BookId,
                    BookFileId = excluded.BookFileId,
                    SourceHash = excluded.SourceHash,
                    Embedding = excluded.Embedding,
                    EmbeddingModel = excluded.EmbeddingModel,
                    EmbeddingDimension = excluded.EmbeddingDimension,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.Add("$chunkId", SqliteType.Integer);
            command.Parameters.Add("$bookId", SqliteType.Text);
            command.Parameters.Add("$bookFileId", SqliteType.Text);
            command.Parameters.Add("$sourceHash", SqliteType.Text);
            command.Parameters.Add("$embedding", SqliteType.Blob);
            command.Parameters.Add("$embeddingModel", SqliteType.Text);
            command.Parameters.Add("$embeddingDimension", SqliteType.Integer);
            command.Parameters.Add("$updatedAt", SqliteType.Text);

            foreach (var embedding in embeddings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (embedding.Vector.Count != embeddingDimension)
                    throw new InvalidDataException("Embedding 向量维度不一致。");

                command.Parameters["$chunkId"].Value = embedding.ChunkId;
                command.Parameters["$bookId"].Value = bookId.ToString();
                command.Parameters["$bookFileId"].Value = bookFileId.ToString();
                command.Parameters["$sourceHash"].Value = sourceHash;
                command.Parameters["$embedding"].Value = SerializeEmbedding(embedding.Vector);
                command.Parameters["$embeddingModel"].Value = embeddingModel;
                command.Parameters["$embeddingDimension"].Value = embeddingDimension;
                command.Parameters["$updatedAt"].Value = DateTimeOffset.UtcNow.ToString("O");
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>
    /// Loads only vectors that still match the source chunk and requested
    /// model version. Stale rows can therefore remain harmlessly until the
    /// owning chunk is rebuilt.
    /// </summary>
    public async Task<IReadOnlyList<(BookContentChunk Chunk, float[] Vector)>> GetBookChunkEmbeddingsAsync(
        Guid bookId,
        string embeddingModel,
        int embeddingDimension,
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue,
        int offset = 0)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.BookId, c.BookFileId, c.SourceHash, c.ChapterIndex, c.ChunkIndex,
                   c.ChapterTitle, c.ChapterPath, c.StartOffset, c.EndOffset, c.Content,
                   e.Embedding
            FROM BookContentChunks c
            INNER JOIN BookChunkEmbeddings e ON e.ChunkId = c.Id
            WHERE c.BookId = $bookId
              AND c.SourceHash = e.SourceHash
              AND e.EmbeddingModel = $embeddingModel
              AND e.EmbeddingDimension = $embeddingDimension
            ORDER BY c.BookFileId, c.ChapterIndex, c.ChunkIndex, c.Id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$embeddingModel", embeddingModel);
        command.Parameters.AddWithValue("$embeddingDimension", embeddingDimension);
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 1000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var result = new List<(BookContentChunk Chunk, float[] Vector)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetValue(11) is not byte[] bytes)
                continue;
            var vector = DeserializeEmbedding(bytes, embeddingDimension);
            if (vector is not null)
                result.Add((ReadChunk(reader), vector));
        }
        return result;
    }

    public Task<IReadOnlyList<BookContentChunk>> GetBookChunksAsync(
        Guid bookId,
        CancellationToken cancellationToken = default)
        => GetBookChunksAsync(bookId, null, cancellationToken);

    public async Task<IReadOnlyList<BookContentChunk>> GetBookChunksAsync(
        Guid bookId,
        Guid? bookFileId,
        CancellationToken cancellationToken = default,
        int? chapterFrom = null,
        int? chapterTo = null,
        string? sourceHash = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId
              AND ($bookFileId IS NULL OR BookFileId = $bookFileId)
              AND ($chapterFrom IS NULL OR ChapterIndex >= $chapterFrom)
              AND ($chapterTo IS NULL OR ChapterIndex <= $chapterTo)
              AND ($sourceHash IS NULL OR (SourceHash = $sourceHash AND TextExtractionVersion = $textVersion))
            ORDER BY BookFileId, ChapterIndex, ChunkIndex, Id;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$bookFileId", (object?)bookFileId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterFrom", (object?)chapterFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterTo", (object?)chapterTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceHash", (object?)sourceHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);

        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadChunk(reader));
        return result;
    }
}
