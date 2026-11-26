using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Identity maps, normalized collections and file moves.
/// </summary>
public sealed partial class S3SyncService
{
    private static async Task<bool> NormalizeUncollectedCollectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var findDefault = CreateCommand(
            connection,
            transaction,
            "SELECT Id FROM BookCollections WHERE Name = $name COLLATE NOCASE LIMIT 1;");
        AddParameter(findDefault, "$name", BookLibraryDefaults.UncollectedCollectionName);
        if (await findDefault.ExecuteScalarAsync(cancellationToken) is not string defaultCollectionId)
            return false;

        using var removeStaleDefault = CreateCommand(connection, transaction, """
            DELETE FROM BookCollectionItems
            WHERE CollectionId = $defaultCollectionId
              AND EXISTS (
                  SELECT 1
                  FROM BookCollectionItems other
                  WHERE other.BookId = BookCollectionItems.BookId
                    AND other.CollectionId <> $defaultCollectionId
              );
            """);
        AddParameter(removeStaleDefault, "$defaultCollectionId", defaultCollectionId);
        var changed = await removeStaleDefault.ExecuteNonQueryAsync(cancellationToken) > 0;

        using var addMissingDefault = CreateCommand(connection, transaction, """
            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT $defaultCollectionId, b.Id, $addedAt
            FROM Books b
            WHERE NOT EXISTS (
                SELECT 1
                FROM BookCollectionItems custom
                WHERE custom.BookId = b.Id
                  AND custom.CollectionId <> $defaultCollectionId
            );
            """);
        AddParameter(addMissingDefault, "$defaultCollectionId", defaultCollectionId);
        AddParameter(addMissingDefault, "$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        changed |= await addMissingDefault.ExecuteNonQueryAsync(cancellationToken) > 0;
        return changed;
    }

    private static bool ShouldApplyRemoteFileName(
        S3SyncBook remoteBook,
        Guid localBookId,
        S3SyncSnapshot localSnapshot)
    {
        var localBook = localSnapshot.Books.FirstOrDefault(book => book.Id == localBookId);
        return localBook is null
            || IsRowNewer(
                remoteBook.SyncVersion, remoteBook.UpdatedAt,
                localBook.SyncVersion, localBook.UpdatedAt);
    }

    private string GetAvailableRemoteFilePath(
        string currentRelativePath,
        string? sourceName,
        string? format,
        string? hash,
        IReadOnlySet<string> plannedRelativePaths)
    {
        var currentPath = ResolveDataPath(currentRelativePath);
        var currentDirectory = Path.GetDirectoryName(currentRelativePath);
        if (string.IsNullOrWhiteSpace(currentDirectory))
            currentDirectory = "library";

        var name = SanitizeFileName(sourceName);
        if (name.Length == 0)
        {
            var fallbackHash = IsSha256(hash) ? hash![..12] : Guid.NewGuid().ToString("N");
            name = $"{fallbackHash}.{format?.Trim().TrimStart('.') ?? "bin"}";
        }
        else if (Path.GetExtension(name).Length == 0 && !string.IsNullOrWhiteSpace(format))
        {
            name += "." + format.Trim().TrimStart('.');
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var disambiguator = IsSha256(hash) ? hash![..12] : "file";
        for (var index = 1; ; index++)
        {
            var candidateName = index == 1
                ? name
                : $"{stem}-{disambiguator}{(index == 2 ? extension : $"-{index - 1}{extension}")}";
            var candidateRelativePath = Path.Combine(currentDirectory, candidateName);
            var candidatePath = ResolveDataPath(candidateRelativePath);
            if (candidatePath is null || plannedRelativePaths.Contains(candidateRelativePath))
                continue;
            if (currentPath is not null
                && string.Equals(candidatePath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return currentRelativePath;
            }
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
                return candidateRelativePath;
        }
    }

    private void MoveSynchronizedFilePaths(
        IEnumerable<PreparedSyncFile> preparedFiles,
        S3SyncSnapshot currentLocal,
        ICollection<string> pathsToDelete,
        ICollection<SynchronizedFileMove> moves,
        CancellationToken cancellationToken)
    {
        var currentFiles = currentLocal.Files.ToDictionary(
            file => file.Id,
            file => new LocalFileIdentity(
                file.Id,
                file.BookId,
                currentLocal.LocalFilePaths.GetValueOrDefault(file.Id) ?? string.Empty,
                file.Size,
                file.Sha256));

        foreach (var prepared in preparedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!currentFiles.TryGetValue(prepared.LocalFileId, out var existing)
                || string.IsNullOrWhiteSpace(existing.RelativePath)
                || string.Equals(existing.RelativePath, prepared.RelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            var sourcePath = ResolveDataPath(existing.RelativePath);
            var targetPath = ResolveDataPath(prepared.RelativePath);
            if (sourcePath is null || targetPath is null || !File.Exists(sourcePath))
                continue;

            if (File.Exists(targetPath))
            {
                pathsToDelete.Add(sourcePath);
                continue;
            }

            if (!string.Equals(existing.Sha256, prepared.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                pathsToDelete.Add(sourcePath);
                continue;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException(UiText.Get("同步文件目标目录无效。"));
            Directory.CreateDirectory(directory);
            var temporaryPath = sourcePath + $".libzora-sync-rename-{Guid.NewGuid():N}.tmp";
            var move = new SynchronizedFileMove(sourcePath, targetPath, temporaryPath);
            moves.Add(move);
            File.Move(sourcePath, temporaryPath);
            File.Move(temporaryPath, targetPath);
            move.TargetMoved = true;
        }
    }

    private static void RestoreSynchronizedFileMoves(IEnumerable<SynchronizedFileMove> moves)
    {
        foreach (var move in moves.Reverse())
        {
            try
            {
                if (move.TargetMoved && File.Exists(move.TargetPath))
                    File.Move(move.TargetPath, move.SourcePath);
                else if (File.Exists(move.TemporaryPath))
                    File.Move(move.TemporaryPath, move.SourcePath);
            }
            catch
            {
                // Preserve the original sync error; the next sync can report
                // any unusual filesystem residue without losing the cause.
            }
        }
    }

    private static S3SyncReadingStats MergeRemoteReadingStats(IEnumerable<S3SyncReadingStats> versions)
    {
        var rows = versions.ToArray();
        var latest = LatestVersioned(rows, row => row.UpdatedAt, row => row.SyncVersion)!;
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        var dailyCounters = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var values = row.SecondsByDevice is { Count: > 0 } ? row.SecondsByDevice
                : new Dictionary<string, long> { ["legacy"] = Math.Max(0, row.CumulativeSeconds) };
            foreach (var (device, seconds) in values)
                counters[device] = Math.Max(counters.GetValueOrDefault(device), seconds);

            if (row.SecondsByDateByDevice is null) continue;
            foreach (var (device, dates) in row.SecondsByDateByDevice)
            {
                if (!dailyCounters.TryGetValue(device, out var mergedDates))
                {
                    mergedDates = new Dictionary<string, long>(StringComparer.Ordinal);
                    dailyCounters[device] = mergedDates;
                }
                foreach (var (date, seconds) in dates)
                    mergedDates[date] = Math.Max(mergedDates.GetValueOrDefault(date), seconds);
            }
        }
        var listening = new Dictionary<string, long>(StringComparer.Ordinal);
        var listeningDays = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var (device, seconds) in row.ListeningSecondsByDevice ?? [])
                listening[device] = Math.Max(listening.GetValueOrDefault(device), seconds);
            foreach (var (device, dates) in row.ListeningSecondsByDateByDevice ?? [])
            {
                if (!listeningDays.TryGetValue(device, out var merged)) listeningDays[device] = merged = new(StringComparer.Ordinal);
                foreach (var (date, seconds) in dates) merged[date] = Math.Max(merged.GetValueOrDefault(date), seconds);
            }
        }
        var titleSource = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.BookTitle))
            .OrderBy(row => row, Comparer<S3SyncReadingStats>.Create((left, right) =>
            {
                var comparison = S3SyncVersion.Compare(
                    left.SyncVersion, left.UpdatedAt, right.SyncVersion, right.UpdatedAt);
                return comparison != 0 ? comparison
                    : CompareSerializedRows(left, right);
            }))
            .LastOrDefault();
        return new S3SyncReadingStats
        {
            BookId = latest.BookId, BookFileId = latest.BookFileId,
            BookTitle = titleSource?.BookTitle ?? string.Empty,
            FileSha256 = rows.Select(row => row.FileSha256).FirstOrDefault(IsSha256) ?? string.Empty,
            CumulativeSeconds = ReadingTimeSyncTracker.Total(counters), SecondsByDevice = counters,
            SecondsByDateByDevice = dailyCounters,
            ListeningSecondsByDevice = listening, ListeningSecondsByDateByDevice = listeningDays,
            HasFinished = rows.Any(row => row.HasFinished || row.ProgressPercent >= 99.5),
            ProgressPercent = latest.ProgressPercent, CompletedChapters = latest.CompletedChapters,
            TotalChapters = latest.TotalChapters, UpdatedAt = latest.UpdatedAt,
            SyncVersion = latest.SyncVersion
        };
    }

    private static S3SyncSnapshot FilterRemoteRows(
        S3SyncSnapshot view,
        IReadOnlyDictionary<string, S3SyncTombstone> tombstones,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> collectionMap)
    {
        bool Deleted(string type, Guid id, DateTimeOffset version, IReadOnlyDictionary<Guid, Guid> map,
            string? rowVersion = null) =>
            IsTombstoned(tombstones, type, id, version, rowVersion)
            || (map.TryGetValue(id, out var mapped) && IsTombstoned(tombstones, type, mapped, version, rowVersion));
        var deletedBooks = view.Books.Where(book => Deleted("book", book.Id, book.UpdatedAt, bookMap, book.SyncVersion))
            .Select(book => book.Id).ToHashSet();
        var restoredBooks = view.Books
            .Where(book => !Deleted("book", book.Id, book.UpdatedAt, bookMap, book.SyncVersion))
            .Select(book => book.Id)
            .ToHashSet();
        var restoredFiles = view.Files
            .Where(file => !Deleted("file", file.Id, file.ModifiedAt, fileMap, file.SyncVersion))
            .Select(file => file.Id)
            .ToHashSet();
        var deletedCollections = view.Collections.Where(row => Deleted(
                "collection", row.Id, CollectionUpdatedAt(row), collectionMap, row.SyncVersion))
            .Select(row => row.Id).ToHashSet();
        bool LiveReader(Guid book, Guid file, DateTimeOffset version, string? rowVersion = null) =>
            !deletedBooks.Contains(book)
            && (restoredBooks.Contains(book) || !Deleted("book", book, version, bookMap, rowVersion))
            && (restoredFiles.Contains(file) || !Deleted("file", file, version, fileMap, rowVersion));
        view.Books = view.Books.Where(book => !deletedBooks.Contains(book.Id)).ToList();
        view.Files = view.Files.Where(file =>
            LiveReader(file.BookId, file.Id, file.ModifiedAt, file.SyncVersion)).ToList();
        view.Collections = view.Collections.Where(row => !deletedCollections.Contains(row.Id)).ToList();
        view.CollectionItems = view.CollectionItems.Where(row =>
            !deletedBooks.Contains(row.BookId) && !deletedCollections.Contains(row.CollectionId)
            && !Deleted("book", row.BookId, row.AddedAt, bookMap, row.SyncVersion)
            && !Deleted("collection", row.CollectionId, row.AddedAt, collectionMap, row.SyncVersion)
            && !IsTombstoned(tombstones, "collection-item",
                CompositeKey(row.CollectionId, row.BookId), row.AddedAt, row.SyncVersion)
            && !IsTombstoned(tombstones, "collection-item", CompositeKey(
                collectionMap.GetValueOrDefault(row.CollectionId, row.CollectionId),
                bookMap.GetValueOrDefault(row.BookId, row.BookId)), row.AddedAt, row.SyncVersion)).ToList();
        // Annotations have their own tombstones and intentionally outlive a
        // deleted book when the user keeps them. Book/file tombstones must not
        // suppress these sync rows.
        view.Annotations = view.Annotations.Where(row =>
            !IsTombstoned(tombstones, "annotation", row.Id, row.UpdatedAt, row.SyncVersion)).ToList();
        view.BookReflections = view.BookReflections.Where(row =>
            !deletedBooks.Contains(row.BookId)
            && !Deleted("book", row.BookId, row.UpdatedAt, bookMap, row.SyncVersion)
            && !Deleted("reflection", row.BookId, row.UpdatedAt, bookMap, row.SyncVersion)).ToList();
        view.Progress = view.Progress.Where(row =>
            LiveReader(row.BookId, row.BookFileId, row.UpdatedAt, row.SyncVersion)
            && !Deleted("progress", row.BookFileId, row.UpdatedAt, fileMap, row.SyncVersion)).ToList();
        view.Bookmarks = view.Bookmarks.Where(row =>
            LiveReader(row.BookId, row.BookFileId, row.CreatedAt, row.SyncVersion)
            && !IsTombstoned(tombstones, "bookmark", row.Id, row.CreatedAt, row.SyncVersion)).ToList();
        view.Layouts = view.Layouts.Where(row =>
            LiveReader(row.BookId, row.BookFileId, LayoutSyncVersion(row), row.SyncVersion)
            && !Deleted("layout", row.BookFileId, LayoutSyncVersion(row), fileMap, row.SyncVersion)).ToList();
        view.ReadingStats = view.ReadingStats.Where(row =>
            !Deleted("stats", row.BookFileId, row.UpdatedAt, fileMap, row.SyncVersion)).ToList();
        return view;
    }

    private async Task<LocalDatabaseIdentity> ReadLocalDatabaseIdentityAsync(CancellationToken cancellationToken)
    {
        var identity = new LocalDatabaseIdentity();
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);

        using (var command = CreateCommand(connection, null, 
            """
            SELECT Id, Title, Authors, CreatedAt, UpdatedAt, CoverPath
            FROM Books;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var book = new LocalBookIdentity(
                    ParseGuid(reader.GetString(0), "Books.Id"),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseTimestamp(reader.GetString(3)),
                    ParseTimestamp(reader.GetString(4)),
                    NullableString(reader, 5));
                identity.BooksById[book.Id] = book;
            }
        }

        using (var command = CreateCommand(connection, null, 
            """
            SELECT Id, BookId, RelativePath, Size, Sha256
            FROM BookFiles;
            """
            ))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var file = new LocalFileIdentity(
                    ParseGuid(reader.GetString(0), "BookFiles.Id"),
                    ParseGuid(reader.GetString(1), "BookFiles.BookId"),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4));
                identity.FilesById[file.Id] = file;
                if (IsSha256(file.Sha256)) identity.FilesByHash[file.Sha256] = file;
            }
        }

        using (var command = CreateCommand(connection, null, "SELECT Id, Name, CreatedAt, UpdatedAt FROM BookCollections;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var collection = new LocalCollectionIdentity(
                    ParseGuid(reader.GetString(0), "BookCollections.Id"),
                    reader.GetString(1),
                    ParseTimestamp(reader.GetString(2)),
                    ParseTimestamp(reader.GetString(3)));
                identity.CollectionsById[collection.Id] = collection;
            }
        }

        return identity;
    }

    private static Dictionary<Guid, Guid> BuildBookMap(
        IReadOnlyList<S3SyncBook> remoteBooks,
        IReadOnlyList<S3SyncBookFile> remoteFiles,
        LocalDatabaseIdentity localIdentity)
    {
        var result = new Dictionary<Guid, Guid>();
        var localBookByHash = localIdentity.FilesById.Values
            .Where(file => IsSha256(file.Sha256))
            .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Hash = group.Key,
                BookIds = group.Select(file => file.BookId).Distinct().ToArray()
            })
            .Where(group => group.BookIds.Length == 1)
            .ToDictionary(group => group.Hash, group => group.BookIds[0], StringComparer.OrdinalIgnoreCase);
        var remoteHashesByBook = remoteFiles
            .Where(file => IsSha256(file.Sha256))
            .GroupBy(file => file.BookId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(file => file.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (var remoteBook in remoteBooks.OrderBy(book => book.Id))
        {
            if (localIdentity.BooksById.ContainsKey(remoteBook.Id))
            {
                result[remoteBook.Id] = remoteBook.Id;
                continue;
            }

            var matchingLocalBooks = remoteHashesByBook.TryGetValue(remoteBook.Id, out var hashes)
                ? hashes.Where(localBookByHash.ContainsKey)
                    .Select(hash => localBookByHash[hash])
                    .Distinct()
                    .ToArray()
                : [];
            // A shared content hash is strong identity evidence. A unique
            // match prevents duplicate book rows when separate devices
            // imported the same file, without collapsing different editions
            // that merely share a title and author.
            result[remoteBook.Id] = matchingLocalBooks.Length == 1
                ? matchingLocalBooks[0]
                : remoteBook.Id;
        }

        return result;
    }

    private static Dictionary<Guid, Guid> BuildFileMap(
        IReadOnlyList<S3SyncBookFile> remoteFiles,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        LocalDatabaseIdentity localIdentity)
    {
        var result = new Dictionary<Guid, Guid>();
        var usedLocalIds = new HashSet<Guid>(localIdentity.FilesById.Keys);
        var localByBookAndHash = localIdentity.FilesById.Values
            .Where(file => IsSha256(file.Sha256))
            .GroupBy(file => $"{file.BookId:N}:{file.Sha256}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var remoteByBookAndHash = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var remoteFile in remoteFiles)
        {
            var localBookId = bookMap.GetValueOrDefault(remoteFile.BookId, remoteFile.BookId);
            var hashKey = $"{localBookId:N}:{remoteFile.Sha256}";
            if (localIdentity.FilesById.TryGetValue(remoteFile.Id, out var byId)
                && byId.BookId == localBookId
                && string.Equals(byId.Sha256, remoteFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                result[remoteFile.Id] = byId.Id;
                if (IsSha256(remoteFile.Sha256))
                    remoteByBookAndHash[hashKey] = byId.Id;
                continue;
            }

            if (IsSha256(remoteFile.Sha256)
                && localByBookAndHash.TryGetValue(hashKey, out var byHash))
            {
                result[remoteFile.Id] = byHash.Id;
                remoteByBookAndHash[hashKey] = byHash.Id;
                continue;
            }

            if (IsSha256(remoteFile.Sha256)
                && remoteByBookAndHash.TryGetValue(hashKey, out var remoteMatch))
            {
                result[remoteFile.Id] = remoteMatch;
                continue;
            }

            var localId = remoteFile.Id;
            if (!usedLocalIds.Add(localId))
                localId = Guid.NewGuid();
            result[remoteFile.Id] = localId;
            if (IsSha256(remoteFile.Sha256))
                remoteByBookAndHash[hashKey] = localId;
        }
        return result;
    }

    private static Dictionary<Guid, Guid> BuildCollectionMap(
        IEnumerable<S3SyncCollection> remoteCollections,
        LocalDatabaseIdentity localIdentity)
    {
        var result = new Dictionary<Guid, Guid>();
        var usedLocalIds = new HashSet<Guid>(localIdentity.CollectionsById.Keys);
        var localByName = localIdentity.CollectionsById.Values
            .GroupBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var remoteCollection in remoteCollections.GroupBy(collection => collection.Id)
                     .Select(group => LatestVersioned(
                         group, CollectionUpdatedAt, collection => collection.SyncVersion)!))
        {
            if (localIdentity.CollectionsById.ContainsKey(remoteCollection.Id))
            {
                result[remoteCollection.Id] = remoteCollection.Id;
                continue;
            }
            if (localByName.TryGetValue(remoteCollection.Name, out var byName))
            {
                result[remoteCollection.Id] = byName;
                continue;
            }

            var localId = remoteCollection.Id;
            if (!usedLocalIds.Add(localId)) localId = Guid.NewGuid();
            result[remoteCollection.Id] = localId;
            localByName[remoteCollection.Name] = localId;
        }
        return result;
    }

    private static void AddLocalIdentityMappings(
        LocalDatabaseIdentity localIdentity,
        IDictionary<Guid, Guid> bookMap,
        IDictionary<Guid, Guid> fileMap,
        IDictionary<Guid, Guid> collectionMap)
    {
        foreach (var bookId in localIdentity.BooksById.Keys)
            bookMap.TryAdd(bookId, bookId);
        foreach (var fileId in localIdentity.FilesById.Keys)
            fileMap.TryAdd(fileId, fileId);
        foreach (var collectionId in localIdentity.CollectionsById.Keys)
            collectionMap.TryAdd(collectionId, collectionId);
    }

    private static Dictionary<string, S3SyncTombstone> BuildTombstoneIndex(
        IEnumerable<S3SyncTombstone> tombstones)
    {
        var index = new Dictionary<string, S3SyncTombstone>(StringComparer.OrdinalIgnoreCase);
        foreach (var tombstone in tombstones)
        {
            if (string.IsNullOrWhiteSpace(tombstone.EntityType)
                || string.IsNullOrWhiteSpace(tombstone.Key))
                continue;
            var entityType = NormalizeTombstoneEntityType(tombstone.EntityType);
            var key = VersionKey(entityType, NormalizeTombstoneKey(entityType, tombstone.Key));
            if (!index.TryGetValue(key, out var existing)
                || S3SyncVersion.IsNewer(
                    tombstone.SyncVersion, tombstone.DeletedAt,
                    existing.SyncVersion, existing.DeletedAt))
            {
                index[key] = new S3SyncTombstone
                {
                    EntityType = entityType,
                    Key = NormalizeTombstoneKey(entityType, tombstone.Key),
                    DeletedAt = tombstone.DeletedAt,
                    ReadingDataResetId = tombstone.ReadingDataResetId,
                    SyncVersion = tombstone.SyncVersion
                };
            }
        }
        return index;
    }

    private static bool IsTombstoned(
        IReadOnlyDictionary<string, S3SyncTombstone> index,
        string entityType,
        Guid id,
        DateTimeOffset version,
        string? rowVersion = null) =>
        IsTombstoned(index, entityType, id.ToString("N"), version, rowVersion);

    private static bool IsTombstoned(
        IReadOnlyDictionary<string, S3SyncTombstone> index,
        string entityType,
        string key,
        DateTimeOffset version,
        string? rowVersion = null) =>
        index.TryGetValue(
            VersionKey(
                NormalizeTombstoneEntityType(entityType),
                NormalizeTombstoneKey(entityType, key)),
            out var tombstone)
        // A deletion wins a tie with the row version it suppresses, but a
        // newer row (for example an explicit restore) can outlive it.
        && S3SyncVersion.Compare(
            tombstone.SyncVersion, tombstone.DeletedAt,
            rowVersion, version) >= 0;

    private static string NormalizeTombstoneEntityType(string? entityType) =>
        (entityType ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeTombstoneKey(string? entityType, string? key)
    {
        var normalizedType = NormalizeTombstoneEntityType(entityType);
        var value = (key ?? string.Empty).Trim();
        if (normalizedType == "collection-item")
        {
            var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && Guid.TryParse(parts[0], out var collectionId)
                && Guid.TryParse(parts[1], out var bookId))
                return CompositeKey(collectionId, bookId);
            return value;
        }

        return Guid.TryParse(value, out var id)
            ? id.ToString("N")
            : value;
    }

    private static DateTimeOffset CollectionUpdatedAt(S3SyncCollection collection) =>
        collection.UpdatedAt == default ? collection.CreatedAt : collection.UpdatedAt;

    private async Task<int> ConsolidateLocalDuplicateBooksAsync(
        ICollection<string> pathsToDelete,
        CancellationToken cancellationToken)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SuppressDeletionTrackingAsync(connection, transaction, true, cancellationToken);
        using var commandCache = new SqliteCommandCache(connection, transaction);
        var merged = await ConsolidateLocalDuplicateBooksInTransactionAsync(
            connection,
            transaction,
            pathsToDelete,
            commandCache,
            cancellationToken);
        await RemoveReferencedScheduledPathsAsync(connection, transaction, pathsToDelete, cancellationToken);
        await EnqueueLocalCleanupAsync(connection, transaction, pathsToDelete, cancellationToken);
        await SuppressDeletionTrackingAsync(connection, transaction, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return merged;
    }
}
