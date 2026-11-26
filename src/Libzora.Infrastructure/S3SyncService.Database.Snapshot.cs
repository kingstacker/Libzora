using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Full and incremental snapshot capture.
/// </summary>
public sealed partial class S3SyncService
{
    private static async Task<S3SyncSnapshot> CaptureDatabaseSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        CancellationToken cancellationToken,
        DateTimeOffset captureStartedAt)
    {
        var readingReset = await ReaderReadingDataReset.ReadAsync(connection, transaction, cancellationToken);
        var snapshot = new S3SyncSnapshot
        {
            Version = SnapshotVersion,
            MinimumReaderVersion = SnapshotVersion,
            ReadingDataReset = readingReset,
            DeviceId = deviceId,
            CreatedAt = captureStartedAt,
            Tombstones = tombstones.ToList()
        };

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Publisher,
                   PublishDate, Isbn, PageCount, Binding, OnlineRating,
                   OnlineRatingCount, Tags, Category, IsFavorite, ReadingStatus,
                   CreatedAt, UpdatedAt, CoverPath
            FROM Books;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = ParseGuid(reader.GetString(0), "Books.Id");
                var createdAt = ParseTimestamp(reader.GetString(17));
                var updatedAt = ParseTimestamp(reader.GetString(18));
                var coverPath = NullableString(reader, 19);
                snapshot.Books.Add(new S3SyncBook
                {
                    Id = id,
                    Title = reader.GetString(1),
                    Authors = reader.GetString(2),
                    Series = NullableString(reader, 3),
                    SeriesIndex = NullableDouble(reader, 4),
                    Description = NullableString(reader, 5),
                    Publisher = NullableString(reader, 6),
                    PublishDate = NullableString(reader, 7),
                    Isbn = NullableString(reader, 8),
                    PageCount = NullableString(reader, 9),
                    Binding = NullableString(reader, 10),
                    OnlineRating = NullableDouble(reader, 11),
                    OnlineRatingCount = NullableInt(reader, 12),
                    Tags = reader.GetString(13),
                    Category = reader.GetString(14),
                    IsFavorite = reader.GetInt32(15) != 0,
                    ReadingStatus = (LibraryReadingStatus)reader.GetInt32(16),
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                    LocalCoverPath = coverPath
                });
                if (!string.IsNullOrWhiteSpace(coverPath))
                    snapshot.LocalCoverPaths[id] = coverPath;
            }
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, BookId, Format, RelativePath, Size, Sha256, SyncUpdatedAt
            FROM BookFiles;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = ParseGuid(reader.GetString(0), "BookFiles.Id");
                var bookId = ParseGuid(reader.GetString(1), "BookFiles.BookId");
                var relativePath = reader.GetString(3);
                snapshot.LocalFilePaths[id] = relativePath;
                snapshot.Files.Add(new S3SyncBookFile
                {
                    Id = id,
                    BookId = bookId,
                    Format = reader.GetString(2),
                    FileName = Path.GetFileName(relativePath.Replace('\\', '/')),
                    Size = reader.GetInt64(4),
                    Sha256 = reader.GetString(5),
                    ModifiedAt = ParseTimestamp(reader.GetString(6))
                });
            }
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, Name, CreatedAt, UpdatedAt
            FROM BookCollections;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Collections.Add(new S3SyncCollection
                {
                    Id = ParseGuid(reader.GetString(0), "BookCollections.Id"),
                    Name = reader.GetString(1),
                    CreatedAt = ParseTimestamp(reader.GetString(2)),
                    UpdatedAt = ParseTimestamp(reader.GetString(3))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT CollectionId, BookId, AddedAt
            FROM BookCollectionItems;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.CollectionItems.Add(new S3SyncCollectionItem
                {
                    CollectionId = ParseGuid(reader.GetString(0), "BookCollectionItems.CollectionId"),
                    BookId = ParseGuid(reader.GetString(1), "BookCollectionItems.BookId"),
                    AddedAt = ParseTimestamp(reader.GetString(2))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Annotations.Add(new S3SyncAnnotation
                {
                    Id = ParseGuid(reader.GetString(0), "ReaderAnnotations.Id"),
                    BookId = ParseGuid(reader.GetString(1), "ReaderAnnotations.BookId"),
                    BookFileId = ParseGuid(reader.GetString(2), "ReaderAnnotations.BookFileId"),
                    ChapterPath = reader.GetString(3),
                    Fragment = NullableString(reader, 4),
                    StartOffset = reader.GetInt32(5),
                    EndOffset = reader.GetInt32(6),
                    SelectedText = reader.GetString(7),
                    Prefix = reader.GetString(8),
                    Suffix = reader.GetString(9),
                    Color = reader.GetString(10),
                    UnderlineStyle = reader.GetString(11),
                    Note = reader.GetString(12),
                    CreatedAt = ParseTimestamp(reader.GetString(13)),
                    UpdatedAt = ParseTimestamp(reader.GetString(14))
                });
        }

        using (var command = CreateCommand(connection, transaction,
            """
            SELECT BookId, Content, CreatedAt, UpdatedAt
            FROM ReaderBookReflections;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.BookReflections.Add(new S3SyncBookReflection
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderBookReflections.BookId"),
                    Content = reader.GetString(1),
                    CreatedAt = ParseTimestamp(reader.GetString(2)),
                    UpdatedAt = ParseTimestamp(reader.GetString(3))
                });
        }

        using (var command = CreateCommand(connection, transaction,
            """
            SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                   ProgressPercent, FlowMode, UpdatedAt, ContentPositionJson
            FROM ReaderProgress;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Progress.Add(new S3SyncProgress
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderProgress.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderProgress.BookFileId"),
                    ChapterPath = reader.GetString(2),
                    Fragment = NullableString(reader, 3),
                    ChapterIndex = reader.GetInt32(4),
                    ScrollPosition = reader.GetInt32(5),
                    ProgressPercent = reader.GetDouble(6),
                    FlowMode = reader.GetInt32(7),
                    UpdatedAt = ParseTimestamp(reader.GetString(8)),
                    ContentPositionJson = NullableString(reader, 9)
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                   ScrollPosition, FlowMode, Title, Quote, CreatedAt, ContentPositionJson
            FROM ReaderBookmarks;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Bookmarks.Add(new S3SyncBookmark
                {
                    Id = ParseGuid(reader.GetString(0), "ReaderBookmarks.Id"),
                    BookId = ParseGuid(reader.GetString(1), "ReaderBookmarks.BookId"),
                    BookFileId = ParseGuid(reader.GetString(2), "ReaderBookmarks.BookFileId"),
                    ChapterPath = reader.GetString(3),
                    Fragment = NullableString(reader, 4),
                    ChapterIndex = reader.GetInt32(5),
                    ScrollPosition = NullableInt(reader, 6),
                    FlowMode = reader.GetInt32(7),
                    Title = reader.GetString(8),
                    Quote = reader.GetString(9),
                    CreatedAt = ParseTimestamp(reader.GetString(10)),
                    ContentPositionJson = NullableString(reader, 11)
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT BookId, BookFileId, FontScale, LineHeight, MaxWidth, BodyPadding,
                   FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent,
                   UseBookOverride, AppearanceTheme, AppearancePaperEnabled,
                   AppearancePaperStrength, AppearanceFibersEnabled, ProfileUpdatedAt, LayoutUpdatedAt, UpdatedAt
            FROM ReaderLayoutSettings;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Layouts.Add(new S3SyncLayout
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderLayoutSettings.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderLayoutSettings.BookFileId"),
                    FontScale = reader.GetDouble(2),
                    LineHeight = reader.GetDouble(3),
                    MaxWidth = reader.GetDouble(4),
                    BodyPadding = reader.GetDouble(5),
                    FontFamily = NullableString(reader, 6),
                    FlowMode = reader.GetInt32(7),
                    VerticalWriting = reader.GetInt32(8) != 0,
                    TwoPageMode = reader.GetInt32(9) != 0,
                    ParagraphIndent = reader.GetInt32(10) != 0,
                    UseBookOverride = reader.GetInt32(11) != 0,
                    AppearanceTheme = Enum.IsDefined((ReaderTheme)reader.GetInt32(12))
                        ? (ReaderTheme)reader.GetInt32(12)
                        : ReaderTheme.Classic,
                    AppearancePaperEnabled = reader.GetInt32(13) != 0,
                    AppearancePaperStrength = reader.GetDouble(14),
                    AppearanceFibersEnabled = reader.GetInt32(15) != 0,
                    ProfileUpdatedAt = reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)),
                    LayoutUpdatedAt = reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                    UpdatedAt = ParseTimestamp(reader.GetString(18))
                });
        }

        using (var command = CreateCommand(connection, transaction, 
            """
            SELECT s.BookId, s.BookFileId, s.CumulativeSeconds, s.ProgressPercent,
                   s.CompletedChapters, s.TotalChapters, s.UpdatedAt,
                   COALESCE(h.Title, ''), COALESCE(h.Sha256, ''), s.HasFinished
            FROM ReaderReadingStats s
            LEFT JOIN ReaderReadingHistory h ON h.BookFileId = s.BookFileId;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.ReadingStats.Add(new S3SyncReadingStats
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderReadingStats.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderReadingStats.BookFileId"),
                    CumulativeSeconds = reader.GetInt64(2),
                    ProgressPercent = reader.GetDouble(3),
                    CompletedChapters = reader.GetInt32(4),
                    TotalChapters = reader.GetInt32(5),
                    UpdatedAt = ParseTimestamp(reader.GetString(6)),
                    BookTitle = reader.GetString(7),
                    FileSha256 = reader.GetString(8),
                    HasFinished = reader.GetInt32(9) != 0
                });
        }

        await ReadingTimeSyncTracker.CaptureAllAsync(
            connection, transaction, snapshot.ReadingStats, cancellationToken);
        var recorded = await ReadRecordedDeletionTimesInTransactionAsync(connection, transaction, cancellationToken);
        snapshot.LocalDeletionTimes = recorded;
        snapshot.Tombstones = MergeTombstones(
            FilterReadingTombstones(tombstones, readingReset), GetRecordedTombstones(recorded, snapshot));
        await CaptureReadingMaterialBooksAsync(connection, transaction, snapshot, cancellationToken);
        await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
        var clock = await S3SyncClock.LoadAsync(connection, transaction, deviceId, cancellationToken);
        await AssignSnapshotVersionsAsync(connection, transaction, snapshot, null, clock, cancellationToken);
        return snapshot;
    }

    /// <summary>
    /// Incremental counterpart of <see cref="CaptureDatabaseSnapshotAsync"/>.
    /// Rows with an UpdatedAt column are read only when they changed after the
    /// watermark; unchanged rows are reused from the previous snapshot.
    /// BookFiles, CollectionItems, Bookmarks and ReadingStats are still read in
    /// full: they are small, and BookFiles/Bookmarks can be updated without an
    /// UpdatedAt column.
    /// </summary>
    private static async Task<S3SyncSnapshot> CaptureIncrementalDatabaseSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        S3SyncSnapshot baseline,
        DateTimeOffset since,
        DateTimeOffset captureStartedAt,
        CancellationToken cancellationToken,
        bool fullScanNonAnnotationTables = false,
        IReadOnlyCollection<Guid>? annotationIds = null,
        bool fullScanAnnotations = false)
    {
        var readingReset = await ReaderReadingDataReset.ReadAsync(connection, transaction, cancellationToken);
        var snapshot = new S3SyncSnapshot
        {
            Version = SnapshotVersion,
            MinimumReaderVersion = SnapshotVersion,
            ReadingDataReset = readingReset,
            DeviceId = deviceId,
            CreatedAt = captureStartedAt,
            Tombstones = CloneTombstones(tombstones),
            TombstonesPrunedBefore = baseline.TombstonesPrunedBefore
        };

        var recorded = await ReadRecordedDeletionTimesInTransactionAsync(connection, transaction, cancellationToken);
        var sinceText = since.ToUniversalTime().ToString("O");
        var deletedBookIds = new HashSet<Guid>();

        // Cover paths are local-only and are persisted in the state file, not
        // in the baseline JSON. Seed them before the changed-book overlay.
        if (!fullScanNonAnnotationTables)
        {
            foreach (var (id, path) in baseline.LocalCoverPaths)
                snapshot.LocalCoverPaths[id] = path;
        }

        // Books: reuse the baseline, drop deleted rows, then overlay rows that
        // changed after the watermark. The converged post-merge capture scans
        // this small table in full because merged remote rows can carry an
        // older UpdatedAt.
        var books = new List<S3SyncBook>(fullScanNonAnnotationTables ? 0 : baseline.Books.Count);
        var bookIndex = new Dictionary<Guid, int>();
        if (!fullScanNonAnnotationTables)
        {
            foreach (var source in baseline.Books)
            {
                if (recorded.ContainsKey(VersionKey("book", source.Id)))
                {
                    deletedBookIds.Add(source.Id);
                    snapshot.LocalCoverPaths.Remove(source.Id);
                    continue;
                }
                var book = CloneBook(source);
                bookIndex[book.Id] = books.Count;
                books.Add(book);
                if (!string.IsNullOrWhiteSpace(book.LocalCoverPath))
                    snapshot.LocalCoverPaths[book.Id] = book.LocalCoverPath;
            }
        }

        var booksSql = fullScanNonAnnotationTables
            ? """
              SELECT Id, Title, Authors, Series, SeriesIndex, Description, Publisher,
                     PublishDate, Isbn, PageCount, Binding, OnlineRating,
                     OnlineRatingCount, Tags, Category, IsFavorite, ReadingStatus,
                     CreatedAt, UpdatedAt, CoverPath
              FROM Books;
              """
            : """
              SELECT Id, Title, Authors, Series, SeriesIndex, Description, Publisher,
                     PublishDate, Isbn, PageCount, Binding, OnlineRating,
                     OnlineRatingCount, Tags, Category, IsFavorite, ReadingStatus,
                     CreatedAt, UpdatedAt, CoverPath
              FROM Books WHERE UpdatedAt > $since;
              """;
        using (var command = CreateCommand(connection, transaction, booksSql))
        {
            if (!fullScanNonAnnotationTables) AddParameter(command, "$since", sinceText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var book = ReadBookRow(reader);
                deletedBookIds.Remove(book.Id);
                if (bookIndex.TryGetValue(book.Id, out var index)) books[index] = book;
                else
                {
                    bookIndex[book.Id] = books.Count;
                    books.Add(book);
                }
                if (!string.IsNullOrWhiteSpace(book.LocalCoverPath))
                    snapshot.LocalCoverPaths[book.Id] = book.LocalCoverPath;
                else
                    snapshot.LocalCoverPaths.Remove(book.Id);
            }
        }
        snapshot.Books = books;
        // Paths are device-local and can be renamed; scan files independently
        // of the book metadata watermark.
        using (var command = CreateCommand(connection, transaction, """
            SELECT Id, BookId, Format, RelativePath, Size, Sha256, SyncUpdatedAt
            FROM BookFiles;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = ParseGuid(reader.GetString(0), "BookFiles.Id");
                var bookId = ParseGuid(reader.GetString(1), "BookFiles.BookId");
                if (deletedBookIds.Contains(bookId)) continue;
                var relativePath = reader.GetString(3);
                snapshot.LocalFilePaths[id] = relativePath;
                snapshot.Files.Add(new S3SyncBookFile
                {
                    Id = id,
                    BookId = bookId,
                    Format = reader.GetString(2),
                    FileName = Path.GetFileName(relativePath.Replace('\\', '/')),
                    Size = reader.GetInt64(4),
                    Sha256 = reader.GetString(5),
                    ModifiedAt = ParseTimestamp(reader.GetString(6))
                });
            }
        }

        var collections = new List<S3SyncCollection>(
            fullScanNonAnnotationTables ? 0 : baseline.Collections.Count);
        var collectionIndex = new Dictionary<Guid, int>();
        if (!fullScanNonAnnotationTables)
        {
            foreach (var source in baseline.Collections)
            {
                if (recorded.ContainsKey(VersionKey("collection", source.Id))) continue;
                collectionIndex[source.Id] = collections.Count;
                collections.Add(source);
            }
        }
        var collectionsSql = fullScanNonAnnotationTables
            ? "SELECT Id, Name, CreatedAt, UpdatedAt FROM BookCollections;"
            : "SELECT Id, Name, CreatedAt, UpdatedAt FROM BookCollections WHERE UpdatedAt > $since;";
        using (var command = CreateCommand(connection, transaction, collectionsSql))
        {
            if (!fullScanNonAnnotationTables) AddParameter(command, "$since", sinceText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var collection = new S3SyncCollection
                {
                    Id = ParseGuid(reader.GetString(0), "BookCollections.Id"),
                    Name = reader.GetString(1),
                    CreatedAt = ParseTimestamp(reader.GetString(2)),
                    UpdatedAt = ParseTimestamp(reader.GetString(3))
                };
                if (collectionIndex.TryGetValue(collection.Id, out var index))
                    collections[index] = collection;
                else
                {
                    collectionIndex[collection.Id] = collections.Count;
                    collections.Add(collection);
                }
            }
        }
        snapshot.Collections = collections;

        // Collection items have no UpdatedAt; the table is small and cascades
        // from both books and collections.
        using (var command = CreateCommand(connection, transaction, """
            SELECT CollectionId, BookId, AddedAt
            FROM BookCollectionItems;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.CollectionItems.Add(new S3SyncCollectionItem
                {
                    CollectionId = ParseGuid(reader.GetString(0), "BookCollectionItems.CollectionId"),
                    BookId = ParseGuid(reader.GetString(1), "BookCollectionItems.BookId"),
                    AddedAt = ParseTimestamp(reader.GetString(2))
                });
        }

        var annotations = new List<S3SyncAnnotation>(
            fullScanAnnotations ? 0 : baseline.Annotations.Count);
        var annotationIndex = new Dictionary<Guid, int>();
        if (!fullScanAnnotations)
        {
            foreach (var source in baseline.Annotations)
            {
                if (recorded.ContainsKey(VersionKey("annotation", source.Id))) continue;
                annotationIndex[source.Id] = annotations.Count;
                annotations.Add(source);
            }
        }
        var annotationsSql = fullScanAnnotations
            ? """
              SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                     SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
              FROM ReaderAnnotations;
              """
            : """
              SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                     SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
              FROM ReaderAnnotations WHERE UpdatedAt > $since;
              """;
        using (var command = CreateCommand(connection, transaction, annotationsSql))
        {
            if (!fullScanAnnotations) AddParameter(command, "$since", sinceText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                OverlayAnnotation(ReadAnnotationRow(reader), annotations, annotationIndex);
        }

        // The converged capture must include remote annotations whose
        // timestamps are older than the transaction start. Re-read exactly the
        // IDs that were present in the downloaded snapshots.
        if (!fullScanAnnotations && annotationIds is { Count: > 0 })
        {
            foreach (var chunk in annotationIds.Distinct().Chunk(500))
            {
                var names = chunk.Select((_, index) => $"$id{index}").ToArray();
                var sql = $"""
                    SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                           SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
                    FROM ReaderAnnotations WHERE Id IN ({string.Join(", ", names)});
                    """;
                using var command = CreateCommand(connection, transaction, sql);
                for (var index = 0; index < chunk.Length; index++)
                    AddParameter(command, names[index], chunk[index].ToString());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    OverlayAnnotation(ReadAnnotationRow(reader), annotations, annotationIndex);
            }
        }
        snapshot.Annotations = annotations;

        var reflections = new List<S3SyncBookReflection>(
            fullScanNonAnnotationTables ? 0 : baseline.BookReflections.Count);
        var reflectionIndex = new Dictionary<Guid, int>();
        if (!fullScanNonAnnotationTables)
        {
            foreach (var source in baseline.BookReflections)
            {
                if (recorded.ContainsKey(VersionKey("reflection", source.BookId.ToString("N")))) continue;
                reflectionIndex[source.BookId] = reflections.Count;
                reflections.Add(source);
            }
        }
        var reflectionsSql = fullScanNonAnnotationTables
            ? "SELECT BookId, Content, CreatedAt, UpdatedAt FROM ReaderBookReflections;"
            : "SELECT BookId, Content, CreatedAt, UpdatedAt FROM ReaderBookReflections WHERE UpdatedAt > $since;";
        using (var command = CreateCommand(connection, transaction, reflectionsSql))
        {
            if (!fullScanNonAnnotationTables) AddParameter(command, "$since", sinceText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var reflection = new S3SyncBookReflection
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderBookReflections.BookId"),
                    Content = reader.GetString(1),
                    CreatedAt = ParseTimestamp(reader.GetString(2)),
                    UpdatedAt = ParseTimestamp(reader.GetString(3))
                };
                if (reflectionIndex.TryGetValue(reflection.BookId, out var index))
                    reflections[index] = reflection;
                else
                {
                    reflectionIndex[reflection.BookId] = reflections.Count;
                    reflections.Add(reflection);
                }
            }
        }
        snapshot.BookReflections = reflections;

        var progress = new List<S3SyncProgress>(
            fullScanNonAnnotationTables ? 0 : baseline.Progress.Count);
        var progressIndex = new Dictionary<Guid, int>();
        if (!fullScanNonAnnotationTables)
        {
            foreach (var source in baseline.Progress)
            {
                if (recorded.ContainsKey(VersionKey("progress", source.BookFileId.ToString("N")))) continue;
                progressIndex[source.BookFileId] = progress.Count;
                progress.Add(source);
            }
        }
        var progressSql = fullScanNonAnnotationTables
            ? """
              SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                     ProgressPercent, FlowMode, UpdatedAt, ContentPositionJson
              FROM ReaderProgress;
              """
            : """
              SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                     ProgressPercent, FlowMode, UpdatedAt, ContentPositionJson
              FROM ReaderProgress WHERE UpdatedAt > $since;
              """;
        using (var command = CreateCommand(connection, transaction, progressSql))
        {
            if (!fullScanNonAnnotationTables) AddParameter(command, "$since", sinceText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new S3SyncProgress
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderProgress.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderProgress.BookFileId"),
                    ChapterPath = reader.GetString(2),
                    Fragment = NullableString(reader, 3),
                    ChapterIndex = reader.GetInt32(4),
                    ScrollPosition = reader.GetInt32(5),
                    ProgressPercent = reader.GetDouble(6),
                    FlowMode = reader.GetInt32(7),
                    UpdatedAt = ParseTimestamp(reader.GetString(8)),
                    ContentPositionJson = NullableString(reader, 9)
                };
                if (progressIndex.TryGetValue(row.BookFileId, out var index))
                    progress[index] = row;
                else
                {
                    progressIndex[row.BookFileId] = progress.Count;
                    progress.Add(row);
                }
            }
        }
        snapshot.Progress = progress;

        // Bookmarks can be re-pointed to another book without a timestamp;
        // the table is small, so read it in full.
        using (var command = CreateCommand(connection, transaction, """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                   ScrollPosition, FlowMode, Title, Quote, CreatedAt, ContentPositionJson
            FROM ReaderBookmarks;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.Bookmarks.Add(new S3SyncBookmark
                {
                    Id = ParseGuid(reader.GetString(0), "ReaderBookmarks.Id"),
                    BookId = ParseGuid(reader.GetString(1), "ReaderBookmarks.BookId"),
                    BookFileId = ParseGuid(reader.GetString(2), "ReaderBookmarks.BookFileId"),
                    ChapterPath = reader.GetString(3),
                    Fragment = NullableString(reader, 4),
                    ChapterIndex = reader.GetInt32(5),
                    ScrollPosition = NullableInt(reader, 6),
                    FlowMode = reader.GetInt32(7),
                    Title = reader.GetString(8),
                    Quote = reader.GetString(9),
                    CreatedAt = ParseTimestamp(reader.GetString(10)),
                    ContentPositionJson = NullableString(reader, 11)
                });
        }

        var layouts = new List<S3SyncLayout>(
            fullScanNonAnnotationTables ? 0 : baseline.Layouts.Count);
        var layoutIndex = new Dictionary<Guid, int>();
        if (!fullScanNonAnnotationTables)
        {
            foreach (var source in baseline.Layouts)
            {
                if (recorded.ContainsKey(VersionKey("layout", source.BookFileId.ToString("N")))) continue;
                layoutIndex[source.BookFileId] = layouts.Count;
                layouts.Add(source);
            }
        }
        var layoutsSql = fullScanNonAnnotationTables
            ? """
              SELECT BookId, BookFileId, FontScale, LineHeight, MaxWidth, BodyPadding,
                     FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent,
                     UseBookOverride, AppearanceTheme, AppearancePaperEnabled,
                     AppearancePaperStrength, AppearanceFibersEnabled, ProfileUpdatedAt, LayoutUpdatedAt, UpdatedAt
              FROM ReaderLayoutSettings;
              """
            : """
              SELECT BookId, BookFileId, FontScale, LineHeight, MaxWidth, BodyPadding,
                     FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent,
                     UseBookOverride, AppearanceTheme, AppearancePaperEnabled,
                     AppearancePaperStrength, AppearanceFibersEnabled, ProfileUpdatedAt, LayoutUpdatedAt, UpdatedAt
              FROM ReaderLayoutSettings
              WHERE UpdatedAt > $since OR (ProfileUpdatedAt IS NOT NULL AND ProfileUpdatedAt > $since);
              """;
        using (var command = CreateCommand(connection, transaction, layoutsSql))
        {
            if (!fullScanNonAnnotationTables) AddParameter(command, "$since", sinceText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var layout = new S3SyncLayout
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderLayoutSettings.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderLayoutSettings.BookFileId"),
                    FontScale = reader.GetDouble(2),
                    LineHeight = reader.GetDouble(3),
                    MaxWidth = reader.GetDouble(4),
                    BodyPadding = reader.GetDouble(5),
                    FontFamily = NullableString(reader, 6),
                    FlowMode = reader.GetInt32(7),
                    VerticalWriting = reader.GetInt32(8) != 0,
                    TwoPageMode = reader.GetInt32(9) != 0,
                    ParagraphIndent = reader.GetInt32(10) != 0,
                    UseBookOverride = reader.GetInt32(11) != 0,
                    AppearanceTheme = Enum.IsDefined((ReaderTheme)reader.GetInt32(12))
                        ? (ReaderTheme)reader.GetInt32(12)
                        : ReaderTheme.Classic,
                    AppearancePaperEnabled = reader.GetInt32(13) != 0,
                    AppearancePaperStrength = reader.GetDouble(14),
                    AppearanceFibersEnabled = reader.GetInt32(15) != 0,
                    ProfileUpdatedAt = reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)),
                    LayoutUpdatedAt = reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                    UpdatedAt = ParseTimestamp(reader.GetString(18))
                };
                if (layoutIndex.TryGetValue(layout.BookFileId, out var index))
                    layouts[index] = layout;
                else
                {
                    layoutIndex[layout.BookFileId] = layouts.Count;
                    layouts.Add(layout);
                }
            }
        }
        snapshot.Layouts = layouts;

        // Reading stats are small (one row per file) and CaptureAllAsync
        // mutates their per-device counters, so read them fresh.
        using (var command = CreateCommand(connection, transaction, """
            SELECT s.BookId, s.BookFileId, s.CumulativeSeconds, s.ProgressPercent,
                   s.CompletedChapters, s.TotalChapters, s.UpdatedAt,
                   COALESCE(h.Title, ''), COALESCE(h.Sha256, ''), s.HasFinished
            FROM ReaderReadingStats s
            LEFT JOIN ReaderReadingHistory h ON h.BookFileId = s.BookFileId;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                snapshot.ReadingStats.Add(new S3SyncReadingStats
                {
                    BookId = ParseGuid(reader.GetString(0), "ReaderReadingStats.BookId"),
                    BookFileId = ParseGuid(reader.GetString(1), "ReaderReadingStats.BookFileId"),
                    CumulativeSeconds = reader.GetInt64(2),
                    ProgressPercent = reader.GetDouble(3),
                    CompletedChapters = reader.GetInt32(4),
                    TotalChapters = reader.GetInt32(5),
                    UpdatedAt = ParseTimestamp(reader.GetString(6)),
                    BookTitle = reader.GetString(7),
                    FileSha256 = reader.GetString(8),
                    HasFinished = reader.GetInt32(9) != 0
                });
        }
        await ReadingTimeSyncTracker.CaptureAllAsync(
            connection, transaction, snapshot.ReadingStats, cancellationToken);

        snapshot.LocalDeletionTimes = recorded;
        snapshot.Tombstones = MergeTombstones(
            FilterReadingTombstones(snapshot.Tombstones, readingReset),
            GetRecordedTombstones(recorded, snapshot));
        await CaptureReadingMaterialBooksAsync(connection, transaction, snapshot, cancellationToken);
        await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
        var clock = await S3SyncClock.LoadAsync(connection, transaction, deviceId, cancellationToken);
        await AssignSnapshotVersionsAsync(connection, transaction, snapshot, baseline, clock, cancellationToken);
        return snapshot;
    }
}
