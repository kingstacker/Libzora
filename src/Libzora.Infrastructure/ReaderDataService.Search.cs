using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Full-text search, phrase filtering and regex helpers.
/// </summary>
public sealed partial class ReaderDataService
{
    public async Task<IReadOnlyList<BookContentChunk>> SearchBookAsync(
        Guid bookId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default,
        bool exactPhraseOnly = false,
        Guid? bookFileId = null,
        int? chapterFrom = null,
        int? chapterTo = null,
        string? sourceHash = null)
    {
        var directPhrase = exactPhraseOnly && !query.Trim().Any(char.IsWhiteSpace);
        IReadOnlyList<string> terms = directPhrase
            ? (string.IsNullOrWhiteSpace(query) ? [] : [query.Trim()])
            : BuildSearchTerms(query);
        if (terms.Count == 0) return [];
        // int.MaxValue is the explicit "whole book" mode used by the reader
        // search UI. Small bounded callers (for example AI context retrieval)
        // keep their existing limits.
        var requestedLimit = limit == int.MaxValue ? int.MaxValue : Math.Clamp(limit, 1, 500);
        // Adjacent index chunks overlap so a phrase crossing a chunk boundary
        // remains searchable. Fetch extra candidates, then collapse candidates
        // whose first matching character resolves to the same chapter offset.
        var candidateLimit = requestedLimit == int.MaxValue
            ? int.MaxValue
            : Math.Clamp(requestedLimit * 3, requestedLimit, 1500);

        if (_ftsAvailable && terms.Any(term => term.Length >= 3))
        {
            try
            {
                var ftsResults = await SearchFullTextAsync(
                    bookId, bookFileId, terms, candidateLimit, cancellationToken,
                    chapterFrom, chapterTo, sourceHash);
                if (_ftsUsesTrigram && directPhrase)
                    return DeduplicateSearchResults(FilterExactPhraseMatches(ftsResults, query), query, terms, requestedLimit);
                if (ftsResults.Count > 0)
                {
                    var matches = exactPhraseOnly
                        ? FilterExactPhraseMatches(ftsResults, query)
                        : ftsResults;
                    if (matches.Count > 0)
                        return DeduplicateSearchResults(matches, query, terms, requestedLimit);
                }
            }
            catch (SqliteException)
            {
                // Malformed or unsupported FTS query: the LIKE fallback remains fully local.
            }
        }

        var likeResults = await SearchLikeAsync(
            bookId, bookFileId, terms, candidateLimit, cancellationToken,
            chapterFrom, chapterTo, sourceHash);
        var filtered = exactPhraseOnly
            ? FilterExactPhraseMatches(likeResults, query)
            : likeResults;
        return DeduplicateSearchResults(filtered, query, terms, requestedLimit);
    }

    private static IReadOnlyList<BookContentChunk> FilterExactPhraseMatches(
        IEnumerable<BookContentChunk> candidates,
        string query)
    {
        var normalizedQuery = WhitespaceRegex().Replace(query.Trim(), " ");
        if (normalizedQuery.Length == 0) return [];
        return candidates
            .Where(candidate =>
                ContainsNormalizedPhrase(candidate.Content, normalizedQuery)
                || ContainsNormalizedPhrase(candidate.ChapterTitle, normalizedQuery))
            .ToArray();
    }

    private static bool ContainsNormalizedPhrase(string? value, string normalizedQuery)
    {
        var normalizedValue = WhitespaceRegex().Replace(value ?? string.Empty, " ");
        return normalizedValue.Contains(
            normalizedQuery,
            StringComparison.CurrentCultureIgnoreCase);
    }

    public async Task<IReadOnlyList<BookContentChunk>> GetBookOverviewChunksAsync(
        Guid bookId,
        int limit = 12,
        CancellationToken cancellationToken = default,
        Guid? bookFileId = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId
              AND ($bookFileId IS NULL OR BookFileId = $bookFileId)
              AND ChunkIndex = 0
            ORDER BY ChapterIndex;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$bookFileId", (object?)bookFileId?.ToString() ?? DBNull.Value);
        var openings = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) openings.Add(ReadChunk(reader));
        if (openings.Count <= limit) return openings;

        var sampled = new List<BookContentChunk>(limit);
        for (var index = 0; index < limit; index++)
        {
            var sourceIndex = (int)Math.Round(index * (openings.Count - 1d) / (limit - 1d));
            sampled.Add(openings[sourceIndex]);
        }
        return sampled;
    }

    public async Task<IReadOnlyList<BookContentChunk>> GetBookChapterChunksAsync(
        Guid bookFileId,
        int chapterIndex,
        int? startOffset = null,
        int? endOffset = null,
        int limit = 256,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookFileId = $bookFileId
              AND ChapterIndex = $chapterIndex
              AND ($startOffset IS NULL OR EndOffset > $startOffset)
              AND ($endOffset IS NULL OR StartOffset < $endOffset)
            ORDER BY ChunkIndex, Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$chapterIndex", chapterIndex);
        command.Parameters.AddWithValue("$startOffset", startOffset is null ? DBNull.Value : startOffset.Value);
        command.Parameters.AddWithValue("$endOffset", endOffset is null ? DBNull.Value : endOffset.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader));
        return result;
    }

    public async Task<IReadOnlyList<BookContentChunk>> GetBookChunkNeighborsAsync(
        IReadOnlyCollection<(Guid BookFileId, int ChapterIndex, int ChunkIndex)> locations,
        int neighborRadius = 1,
        CancellationToken cancellationToken = default)
    {
        if (locations.Count == 0) return [];
        neighborRadius = Math.Clamp(neighborRadius, 0, 3);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        var predicates = new List<string>(locations.Count);
        var index = 0;
        foreach (var location in locations.Distinct())
        {
            var fileParameter = $"$neighborFile{index}";
            var chapterParameter = $"$neighborChapter{index}";
            var startParameter = $"$neighborStart{index}";
            var endParameter = $"$neighborEnd{index}";
            predicates.Add($"(BookFileId = {fileParameter} AND ChapterIndex = {chapterParameter} AND ChunkIndex BETWEEN {startParameter} AND {endParameter})");
            command.Parameters.AddWithValue(fileParameter, location.BookFileId.ToString());
            command.Parameters.AddWithValue(chapterParameter, location.ChapterIndex);
            command.Parameters.AddWithValue(startParameter, Math.Max(0, location.ChunkIndex - neighborRadius));
            command.Parameters.AddWithValue(endParameter, location.ChunkIndex + neighborRadius);
            index++;
        }
        command.CommandText = $"""
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE {string.Join(" OR ", predicates)}
            ORDER BY BookFileId, ChapterIndex, ChunkIndex, Id;
            """;
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader));
        return result;
    }

    private async Task<IReadOnlyList<BookContentChunk>> SearchFullTextAsync(
        Guid bookId,
        Guid? bookFileId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken,
        int? chapterFrom = null,
        int? chapterTo = null,
        string? sourceHash = null)
    {
        var searchable = terms.Where(term => term.Length >= 3).Take(16).ToArray();
        if (searchable.Length == 0) return [];
        // ChapterTitle is stored on every content chunk for navigation. Search
        // it only on the first chunk below; otherwise a title-only query would
        // return the same chapter once for every chunk in that chapter.
        var contentMatch = BuildFtsColumnQuery("Content", searchable);
        var titleMatch = BuildFtsColumnQuery("ChapterTitle", searchable);
        // The reader's whole-book search presents every hit as a reading list.
        // Progressive searches also emit chapter windows in reading order;
        // bounded retrieval callers keep relevance ordering.
        var orderBy = limit == int.MaxValue || chapterFrom is not null
            ? "c.ChapterIndex, c.ChunkIndex, c.StartOffset, c.Id"
            : "best_matches.Rank, c.ChapterIndex, c.ChunkIndex";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH matched AS (
                SELECT c.Id, bm25(BookContentFts, 1.0, 2.8) AS Rank
                FROM BookContentFts
                INNER JOIN BookContentChunks c ON c.Id = BookContentFts.rowid
                WHERE BookContentFts MATCH $contentQuery AND c.BookId = $bookId
                  AND ($bookFileId IS NULL OR c.BookFileId = $bookFileId)
                  AND ($sourceHash IS NULL OR c.SourceHash = $sourceHash)
                  AND c.TextExtractionVersion = $textVersion
                  AND ($chapterFrom IS NULL OR c.ChapterIndex >= $chapterFrom)
                  AND ($chapterTo IS NULL OR c.ChapterIndex <= $chapterTo)
                UNION ALL
                SELECT c.Id, bm25(BookContentFts, 1.0, 2.8) AS Rank
                FROM BookContentFts
                INNER JOIN BookContentChunks c ON c.Id = BookContentFts.rowid
                WHERE BookContentFts MATCH $titleQuery
                  AND c.BookId = $bookId
                  AND ($bookFileId IS NULL OR c.BookFileId = $bookFileId)
                  AND ($sourceHash IS NULL OR c.SourceHash = $sourceHash)
                  AND c.TextExtractionVersion = $textVersion
                  AND ($chapterFrom IS NULL OR c.ChapterIndex >= $chapterFrom)
                  AND ($chapterTo IS NULL OR c.ChapterIndex <= $chapterTo)
                  AND c.ChunkIndex = 0
            ), best_matches AS (
                SELECT Id, MIN(Rank) AS Rank
                FROM matched
                GROUP BY Id
            )
            SELECT c.Id, c.BookId, c.BookFileId, c.SourceHash, c.ChapterIndex, c.ChunkIndex,
                   c.ChapterTitle, c.ChapterPath, c.StartOffset, c.EndOffset, c.Content,
                   best_matches.Rank
            FROM best_matches
            INNER JOIN BookContentChunks c ON c.Id = best_matches.Id
            ORDER BY {orderBy}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$contentQuery", contentMatch);
        command.Parameters.AddWithValue("$titleQuery", titleMatch);
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$bookFileId", (object?)bookFileId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceHash", (object?)sourceHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
        command.Parameters.AddWithValue("$chapterFrom", (object?)chapterFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterTo", (object?)chapterTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 1500));
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader, includeRank: true));
        return result;
    }

    private static string BuildFtsColumnQuery(string column, IEnumerable<string> terms) =>
        string.Join(" OR ", terms.Select(term =>
            $"{column} : \"{term.Replace("\"", "\"\"")}\""));

    private async Task<IReadOnlyList<BookContentChunk>> SearchLikeAsync(
        Guid bookId,
        Guid? bookFileId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken,
        int? chapterFrom = null,
        int? chapterTo = null,
        string? sourceHash = null)
    {
        var selectedTerms = terms.OrderByDescending(term => term.Length).Take(5).ToArray();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        var predicates = new List<string>();
        for (var index = 0; index < selectedTerms.Length; index++)
        {
            // ChapterTitle is duplicated on every chunk. Only the chapter's
            // opening chunk represents a title-only match; content matches
            // still return every relevant chunk.
            predicates.Add($"(Content LIKE $term{index} ESCAPE '\\' OR (ChunkIndex = 0 AND ChapterTitle LIKE $term{index} ESCAPE '\\'))");
            command.Parameters.AddWithValue($"$term{index}", $"%{selectedTerms[index].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
        }
        command.CommandText = $"""
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId
              AND ($bookFileId IS NULL OR BookFileId = $bookFileId)
              AND ($sourceHash IS NULL OR SourceHash = $sourceHash)
              AND TextExtractionVersion = $textVersion
              AND ($chapterFrom IS NULL OR ChapterIndex >= $chapterFrom)
              AND ($chapterTo IS NULL OR ChapterIndex <= $chapterTo)
              AND ({string.Join(" OR ", predicates)})
            ORDER BY ChapterIndex, ChunkIndex
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$bookFileId", (object?)bookFileId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceHash", (object?)sourceHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$textVersion", CurrentTextExtractionVersion);
        command.Parameters.AddWithValue("$chapterFrom", (object?)chapterFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterTo", (object?)chapterTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 1500));
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader));
        return result;
    }

    internal static IReadOnlyList<BookContentChunk> DeduplicateSearchResults(
        IEnumerable<BookContentChunk> candidates,
        string query,
        IReadOnlyList<string> terms,
        int limit)
    {
        var results = new List<BookContentChunk>();
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenParagraphRanges = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var localOffset = FindPrimarySearchMatch(candidate.Content, query, terms);
            var location = localOffset >= 0
                ? candidate.StartOffset + localOffset
                : candidate.StartOffset;
            var key = $"{candidate.BookFileId:N}\u001f{candidate.ChapterPath}\u001f{location}";
            if (seenLocations.Contains(key)) continue;
            // One indexed chunk can end halfway through a paragraph and the
            // next overlapping chunk can report a later occurrence from that
            // same paragraph as a separate result. Both clicks then land on
            // the same rendered page. Merge overlapping paragraph spans while
            // retaining all hit highlighting inside the surviving result.
            var paragraphRange = GetSearchParagraphRange(candidate, localOffset);
            var paragraphKey = $"{candidate.BookFileId:N}\u001f{candidate.ChapterPath}";
            if (seenParagraphRanges.TryGetValue(paragraphKey, out var ranges)
                && ranges.Any(range => paragraphRange.Start < range.End && range.Start < paragraphRange.End))
            {
                continue;
            }
            seenLocations.Add(key);
            if (!seenParagraphRanges.TryGetValue(paragraphKey, out ranges))
            {
                ranges = [];
                seenParagraphRanges[paragraphKey] = ranges;
            }
            ranges.Add(paragraphRange);
            results.Add(candidate);
            if (results.Count >= (limit == int.MaxValue ? int.MaxValue : Math.Clamp(limit, 1, 500))) break;
        }
        return results;
    }

    private static (int Start, int End) GetSearchParagraphRange(
        BookContentChunk candidate,
        int localOffset)
    {
        var content = candidate.Content ?? string.Empty;
        if (content.Length == 0) return (candidate.StartOffset, candidate.StartOffset + 1);
        var match = Math.Clamp(localOffset, 0, content.Length - 1);
        var paragraphStart = content.LastIndexOf('\n', match);
        paragraphStart = paragraphStart < 0 ? 0 : paragraphStart + 1;
        var paragraphEnd = content.IndexOf('\n', match);
        if (paragraphEnd < 0) paragraphEnd = content.Length;
        return (
            candidate.StartOffset + paragraphStart,
            candidate.StartOffset + Math.Max(paragraphStart + 1, paragraphEnd));
    }

    private static int FindPrimarySearchMatch(
        string content,
        string query,
        IReadOnlyList<string> terms)
    {
        var exactQuery = query.Trim();
        if (exactQuery.Length > 0)
        {
            var exact = content.IndexOf(exactQuery, StringComparison.CurrentCultureIgnoreCase);
            if (exact >= 0) return exact;
        }

        var earliest = -1;
        foreach (var term in terms)
        {
            var match = content.IndexOf(term, StringComparison.CurrentCultureIgnoreCase);
            if (match >= 0 && (earliest < 0 || match < earliest)) earliest = match;
        }
        return earliest;
    }

    internal static IReadOnlyList<string> BuildSearchTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var normalized = WhitespaceRegex().Replace(query.Trim(), " ");
        foreach (var stopPhrase in ChineseStopPhrases)
            normalized = normalized.Replace(stopPhrase, string.Empty, StringComparison.OrdinalIgnoreCase);

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LatinWordRegex().Matches(normalized))
            if (match.Value.Length >= 3) terms.Add(match.Value);

        foreach (Match match in ChineseSequenceRegex().Matches(normalized))
        {
            var value = match.Value;
            if (value.Length >= 3) terms.Add(value.Length <= 18 ? value : value[..18]);
            for (var size = Math.Min(6, value.Length); size >= 3; size--)
            {
                for (var start = 0; start + size <= value.Length; start += Math.Max(1, size - 2))
                    terms.Add(value.Substring(start, size));
            }
        }

        if (terms.Count == 0)
        {
            var fallback = normalized.Trim();
            if (fallback.Length > 0) terms.Add(fallback);
        }
        return terms.OrderByDescending(term => term.Length).Take(24).ToArray();
    }

    private async Task<bool> EnsureFullTextIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var existence = connection.CreateCommand();
        existence.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'BookContentFts');";
        var existed = Convert.ToInt32(await existence.ExecuteScalarAsync(cancellationToken)) == 1;
        try
        {
            await CreateFtsTableAsync(connection, "trigram", cancellationToken);
        }
        catch (SqliteException)
        {
            try
            {
                await CreateFtsTableAsync(connection, "unicode61 remove_diacritics 2", cancellationToken);
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        using var definition = connection.CreateCommand();
        definition.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'BookContentFts';";
        _ftsUsesTrigram = (await definition.ExecuteScalarAsync(cancellationToken))?.ToString()
            ?.Contains("trigram", StringComparison.OrdinalIgnoreCase) == true;
        using var triggers = connection.CreateCommand();
        triggers.CommandText = """
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_ai AFTER INSERT ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(rowid, Content, ChapterTitle)
                VALUES (new.Id, new.Content, new.ChapterTitle);
            END;
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_ad AFTER DELETE ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(BookContentFts, rowid, Content, ChapterTitle)
                VALUES ('delete', old.Id, old.Content, old.ChapterTitle);
            END;
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_au AFTER UPDATE ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(BookContentFts, rowid, Content, ChapterTitle)
                VALUES ('delete', old.Id, old.Content, old.ChapterTitle);
                INSERT INTO BookContentFts(rowid, Content, ChapterTitle)
                VALUES (new.Id, new.Content, new.ChapterTitle);
            END;
            """;
        await triggers.ExecuteNonQueryAsync(cancellationToken);
        if (!existed)
        {
            using var rebuild = connection.CreateCommand();
            rebuild.CommandText = "INSERT INTO BookContentFts(BookContentFts) VALUES('rebuild');";
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    private static async Task CreateFtsTableAsync(
        SqliteConnection connection,
        string tokenizer,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS BookContentFts USING fts5(
                Content,
                ChapterTitle,
                content='BookContentChunks',
                content_rowid='Id',
                tokenize='{tokenizer}'
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -65536;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 5000;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static BookContentChunk ReadChunk(SqliteDataReader reader, bool includeRank = false)
    {
        return new BookContentChunk(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            includeRank && !reader.IsDBNull(11) ? reader.GetDouble(11) : 0);
    }

    private static byte[] SerializeEmbedding(IReadOnlyList<float> vector)
    {
        var bytes = new byte[checked(vector.Count * sizeof(float))];
        for (var index = 0; index < vector.Count; index++)
        {
            var bits = BitConverter.SingleToInt32Bits(vector[index]);
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                bits);
        }
        return bytes;
    }

    private static float[]? DeserializeEmbedding(byte[] bytes, int dimension)
    {
        if (dimension <= 0 || bytes.Length != checked(dimension * sizeof(float)))
            return null;

        var vector = new float[dimension];
        for (var index = 0; index < dimension; index++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)));
            var value = BitConverter.Int32BitsToSingle(bits);
            if (!float.IsFinite(value)) return null;
            vector[index] = value;
        }
        return vector;
    }

    private static readonly string[] ChineseStopPhrases =
    [
        "请根据", "请帮我", "这本书", "本书", "这一章", "本章", "当前章节", "当前",
        "如何", "怎么", "什么是", "为什么", "哪些", "是否", "请", "帮我", "一下",
        "总结", "概括", "解释", "分析", "介绍", "关于", "根据"
    ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{3,}")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"[\u3400-\u9FFF]{2,}")]
    private static partial Regex ChineseSequenceRegex();
}
