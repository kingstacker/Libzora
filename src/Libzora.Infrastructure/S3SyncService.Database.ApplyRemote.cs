using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Remote snapshot merge. This is the sync core; change it only with
/// the ICloudSyncTests and S3 snapshot tests green.
/// </summary>
public sealed partial class S3SyncService
{
    private async Task<DatabaseMergeResult> ApplyRemoteSnapshotsAsync(
        ISyncObjectStore client,
        S3SyncSettings settings,
        S3SyncSnapshot localSnapshot,
        S3SyncSnapshot? baselineSnapshot,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var snapshots = remoteSnapshots
            .GroupBy(snapshot => NormalizeKnownDeviceId(snapshot.DeviceId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CreatedAt).First())
            .ToArray();

        var readingReset = ReaderReadingDataReset.Latest(
            snapshots.Select(snapshot => snapshot.ReadingDataReset).Prepend(localSnapshot.ReadingDataReset));
        // Scope reading deletions to the winning reset before merging them;
        // old devices may still publish deletions with later wall-clock times.
        localSnapshot.Tombstones = MergeTombstones(
            FilterReadingTombstones(localSnapshot.Tombstones ?? [], readingReset),
            snapshots.SelectMany(snapshot => FilterReadingTombstones(snapshot.Tombstones ?? [], readingReset)));

        var warnings = new List<string>();
        var isPartial = false;
        var pathsToDelete = new List<string>();
        // Only merge independently imported records when they contain the
        // same file content. Matching metadata alone can represent editions.
        var duplicateBooksMerged = await ConsolidateLocalDuplicateBooksAsync(
            pathsToDelete,
            cancellationToken);
        var readingHistoryMerged = await ConsolidateLocalReadingHistoryAsync(cancellationToken);

        if (snapshots.Length == 0)
        {
            if (duplicateBooksMerged > 0)
                warnings.Add(UiText.Get("已自动合并 {0} 本重复书籍。", duplicateBooksMerged));
            var cleanupPending = await CleanupPendingLocalFilesAsync(warnings, cancellationToken);
            return new DatabaseMergeResult(
                0,
                0,
                0,
                duplicateBooksMerged > 0 || readingHistoryMerged,
                warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct()))
            {
                IsPartial = cleanupPending,
                RemoteBookIds = new HashSet<Guid>(),
                RemoteOnlyFileHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                // Local consolidation can rewrite reader rows (for example the
                // file id on a moved annotation) without bumping UpdatedAt, so
                // the post-merge capture must rescan those tables.
                ConsolidatedDuplicates = duplicateBooksMerged > 0 || readingHistoryMerged
            };
        }

        var localIdentity = await ReadLocalDatabaseIdentityAsync(cancellationToken);
        // Keep every device's version of a row. Collapsing to the latest
        // UpdatedAt before field merging loses edits from the other devices.
        var remoteBookVariantsById = snapshots
            .SelectMany(snapshot => snapshot.Books)
            .GroupBy(book => book.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        var remoteAnnotationVariantsById = snapshots
            .SelectMany(snapshot => snapshot.Annotations)
            .GroupBy(annotation => annotation.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        var allRemoteBooks = snapshots
            .SelectMany(snapshot => snapshot.Books)
            .GroupBy(book => book.Id)
            .Select(group => LatestVersioned(group, book => book.UpdatedAt, book => book.SyncVersion)!)
            .ToArray();
        var allRemoteFiles = snapshots
            .SelectMany(snapshot => snapshot.Files)
            .GroupBy(file => file.Id)
            .Select(group => LatestVersioned(group, file => file.ModifiedAt, file => file.SyncVersion)!)
            .ToArray();
        var remoteBooksById = allRemoteBooks.ToDictionary(book => book.Id);

        // Build mappings from the complete remote view before applying
        // tombstones. A stale row may be suppressed below, but its ID is still
        // needed to map the corresponding tombstone to an existing local row.
        var bookMap = BuildBookMap(allRemoteBooks, allRemoteFiles, localIdentity);
        var fileMap = BuildFileMap(allRemoteFiles, bookMap, localIdentity);
        var allRemoteCollections = snapshots
            .SelectMany(snapshot => snapshot.Collections)
            .GroupBy(collection => collection.Id)
            .Select(group => LatestVersioned(
                group, CollectionUpdatedAt, collection => collection.SyncVersion)!)
            .ToArray();
        var collectionMap = BuildCollectionMap(allRemoteCollections, localIdentity);
        // Keep IDs stable unless a remote file has an exact hash match in the
        // local library. Matching titles or authors alone never merges editions.
        AddLocalIdentityMappings(localIdentity, bookMap, fileMap, collectionMap);

        var tombstoneIndex = BuildTombstoneIndex(localSnapshot.Tombstones);
        // A deleted remote identity must never be matched to an unrelated
        // local book/file by the duplicate-title or content-hash heuristics.
        // Otherwise a tombstone for a removed copy could delete the surviving
        // copy that happens to share its title or bytes.
        foreach (var book in allRemoteBooks)
            if (IsTombstoned(tombstoneIndex, "book", book.Id, book.UpdatedAt, book.SyncVersion)
                && !localIdentity.BooksById.ContainsKey(book.Id))
                bookMap[book.Id] = book.Id;
        foreach (var file in allRemoteFiles)
            if ((IsTombstoned(tombstoneIndex, "file", file.Id, file.ModifiedAt, file.SyncVersion)
                    || IsTombstoned(tombstoneIndex, "book", file.BookId, file.ModifiedAt, file.SyncVersion))
                && !localIdentity.FilesById.ContainsKey(file.Id))
                fileMap[file.Id] = file.Id;
        foreach (var collection in allRemoteCollections)
            if (IsTombstoned(tombstoneIndex, "collection", collection.Id,
                    CollectionUpdatedAt(collection), collection.SyncVersion)
                && !localIdentity.CollectionsById.ContainsKey(collection.Id))
                collectionMap[collection.Id] = collection.Id;
        var tombstonedBooks = allRemoteBooks
            .Where(book => IsTombstoned(
                tombstoneIndex, "book", book.Id, book.UpdatedAt, book.SyncVersion))
            .Select(book => book.Id)
            .ToHashSet();
        var remoteBooks = allRemoteBooks
            .Where(book => !tombstonedBooks.Contains(book.Id)
                && !IsTombstoned(tombstoneIndex, "book", book.Id, book.UpdatedAt, book.SyncVersion))
            .ToArray();
        var remoteFiles = allRemoteFiles
            .Where(file => !tombstonedBooks.Contains(file.BookId)
                && !IsTombstoned(tombstoneIndex, "book", file.BookId, file.ModifiedAt, file.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "file", file.Id, file.ModifiedAt, file.SyncVersion))
            .ToArray();
        var tombstonedCollections = allRemoteCollections
            .Where(collection => IsTombstoned(
                tombstoneIndex,
                "collection",
                collection.Id,
                CollectionUpdatedAt(collection),
                collection.SyncVersion))
            .Select(collection => collection.Id)
            .ToHashSet();
        var remoteCollections = allRemoteCollections
            .Where(collection => !tombstonedCollections.Contains(collection.Id))
            .ToArray();
        var remoteItems = snapshots
            .SelectMany(snapshot => snapshot.CollectionItems)
            .GroupBy(item => CompositeKey(item.CollectionId, item.BookId), StringComparer.OrdinalIgnoreCase)
            .Select(group => LatestVersioned(group, item => item.AddedAt, item => item.SyncVersion)!)
            .Where(item => !tombstonedCollections.Contains(item.CollectionId)
                && !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "collection", item.CollectionId,
                    item.AddedAt, item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId,
                    item.AddedAt, item.SyncVersion)
                && !IsTombstoned(
                    tombstoneIndex,
                    "collection-item",
                    CompositeKey(item.CollectionId, item.BookId),
                    item.AddedAt,
                    item.SyncVersion))
            .ToArray();
        var remoteAnnotations = snapshots
            .SelectMany(snapshot => snapshot.Annotations)
            .GroupBy(annotation => annotation.Id)
            .Select(group => LatestVersioned(
                group, annotation => annotation.UpdatedAt, annotation => annotation.SyncVersion)!)
            .Where(annotation => !IsTombstoned(
                tombstoneIndex, "annotation", annotation.Id, annotation.UpdatedAt,
                annotation.SyncVersion))
            .ToArray();
        var remoteBookReflections = snapshots
            .SelectMany(snapshot => snapshot.BookReflections)
            .GroupBy(reflection => reflection.BookId)
            .Select(group => LatestVersioned(
                group, reflection => reflection.UpdatedAt, reflection => reflection.SyncVersion)!)
            .Where(reflection => !tombstonedBooks.Contains(reflection.BookId)
                && !IsTombstoned(tombstoneIndex, "book", reflection.BookId,
                    reflection.UpdatedAt, reflection.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "reflection", reflection.BookId,
                    reflection.UpdatedAt, reflection.SyncVersion))
            .ToArray();
        var remoteProgress = snapshots
            .Where(snapshot => snapshot.ReadingDataReset == readingReset)
            .SelectMany(snapshot => snapshot.Progress)
            .GroupBy(item => item.BookFileId)
            .Select(group => LatestVersioned(group, item => item.UpdatedAt, item => item.SyncVersion)!)
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.UpdatedAt, item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "progress", item.BookFileId, item.UpdatedAt, item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId, item.UpdatedAt, item.SyncVersion))
            .ToArray();
        var remoteBookmarks = snapshots
            .SelectMany(snapshot => snapshot.Bookmarks)
            .GroupBy(bookmark => bookmark.Id)
            .Select(group => LatestVersioned(
                group, bookmark => bookmark.CreatedAt, bookmark => bookmark.SyncVersion)!)
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId, item.CreatedAt, item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "bookmark", item.Id, item.CreatedAt, item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId, item.CreatedAt, item.SyncVersion))
            .ToArray();
        var remoteLayouts = snapshots
            .SelectMany(snapshot => snapshot.Layouts)
            .Where(item => !tombstonedBooks.Contains(item.BookId)
                && !IsTombstoned(tombstoneIndex, "book", item.BookId,
                    LayoutSyncVersion(item), item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "layout", item.BookFileId,
                    LayoutSyncVersion(item), item.SyncVersion)
                && !IsTombstoned(tombstoneIndex, "file", item.BookFileId,
                    LayoutSyncVersion(item), item.SyncVersion))
            .ToArray();
        var remoteStats = snapshots
            .Where(snapshot => snapshot.ReadingDataReset == readingReset)
            .SelectMany(snapshot => snapshot.ReadingStats)
            .GroupBy(stats => stats.BookFileId)
            .Select(MergeRemoteReadingStats)
            .Where(item => !IsTombstoned(
                tombstoneIndex, "stats", item.BookFileId, item.UpdatedAt, item.SyncVersion))
            .ToArray();
        var remoteTitles = allRemoteBooks.ToDictionary(book => book.Id, book => book.Title);
        var remoteHashes = allRemoteFiles.ToDictionary(file => file.Id, file => file.Sha256);
        foreach (var stats in remoteStats)
        {
            if (string.IsNullOrWhiteSpace(stats.BookTitle)) stats.BookTitle = remoteTitles.GetValueOrDefault(stats.BookId) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stats.FileSha256)) stats.FileSha256 = remoteHashes.GetValueOrDefault(stats.BookFileId) ?? string.Empty;
        }
        void RefilterRemoteRows()
        {
            var view = FilterRemoteRows(new S3SyncSnapshot
            {
                Books = remoteBooks.ToList(), Files = remoteFiles.ToList(), Collections = remoteCollections.ToList(),
                CollectionItems = remoteItems.ToList(), Annotations = remoteAnnotations.ToList(),
                BookReflections = remoteBookReflections.ToList(),
                Progress = remoteProgress.ToList(), Bookmarks = remoteBookmarks.ToList(),
                Layouts = remoteLayouts.ToList(), ReadingStats = remoteStats.ToList()
            }, tombstoneIndex, bookMap, fileMap, collectionMap);
            remoteBooks = view.Books.ToArray();
            remoteFiles = view.Files.ToArray();
            remoteCollections = view.Collections.ToArray();
            remoteItems = view.CollectionItems.ToArray();
            remoteAnnotations = view.Annotations.ToArray();
            remoteBookReflections = view.BookReflections.ToArray();
            remoteProgress = view.Progress.ToArray();
            remoteBookmarks = view.Bookmarks.ToArray();
            remoteLayouts = view.Layouts.ToArray();
            remoteStats = view.ReadingStats.ToArray();
        }
        RefilterRemoteRows();
        var preparedFilesByLocalId = new Dictionary<Guid, PreparedSyncFile>();
        var plannedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteOnlyFileHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteBookIds = new HashSet<Guid>();
        var coverUpdates = new Dictionary<Guid, (string RelativePath, DateTimeOffset UpdatedAt, string? SyncVersion)>();
        var synchronizedFileMoves = new List<SynchronizedFileMove>();
        var filesDownloaded = 0;
        var coversDownloaded = false;

        progress?.Report(settings.DownloadBookFilesOnSync
            ? UiText.Get("正在准备书籍文件…")
            : UiText.Get("正在登记书籍文件元数据…"));
        var fileDownloadRequests = new List<(string Hash, string AbsolutePath, string FileName)>();
        foreach (var remoteFile in remoteFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bookMap.TryGetValue(remoteFile.BookId, out var localBookId)
                || !fileMap.TryGetValue(remoteFile.Id, out var localFileId))
                continue;
            if (!IsSha256(remoteFile.Sha256))
            {
                warnings.Add(UiText.Get("《{0}》的远端文件缺少有效的 SHA-256 校验值，已跳过。", remoteFile.FileName));
                isPartial = true;
                continue;
            }

            var existing = localIdentity.FilesById.GetValueOrDefault(localFileId);
            if (preparedFilesByLocalId.TryGetValue(localFileId, out var preparedForSameFile))
            {
                // Different devices can describe the same content with
                // different BookFile IDs. Keep one local row and let both
                // remote IDs point at that row instead of downloading and
                // inserting the same bytes twice.
                var rebound = preparedForSameFile with { Source = remoteFile };
                preparedFilesByLocalId[localFileId] = rebound;
                continue;
            }
            var relativePath = existing?.RelativePath;
            if (existing is not null
                && remoteBooksById.TryGetValue(remoteFile.BookId, out var remoteBook)
                && ShouldApplyRemoteFileName(remoteBook, localBookId, localSnapshot))
            {
                relativePath = GetAvailableRemoteFilePath(
                    existing.RelativePath,
                    remoteFile.FileName,
                    remoteFile.Format,
                    remoteFile.Sha256,
                    plannedRelativePaths);
            }

            relativePath ??= Path.Combine(
                "library",
                localBookId.ToString("N"),
                GetAvailableFileName(remoteFile.FileName, remoteFile.Sha256, remoteFile.Format, localBookId, plannedRelativePaths));
            var absolutePath = ResolveDataPath(relativePath);
            if (absolutePath is null)
            {
                warnings.Add(UiText.Get("《{0}》的同步路径无效。", remoteFile.FileName));
                isPartial = true;
                continue;
            }

            var prepared = new PreparedSyncFile(
                remoteFile,
                localBookId,
                localFileId,
                Path.GetRelativePath(_paths.Data, absolutePath));
            preparedFilesByLocalId[localFileId] = prepared;
            plannedRelativePaths.Add(prepared.RelativePath);

            if (settings.DownloadBookFilesOnSync)
                fileDownloadRequests.Add((remoteFile.Sha256, absolutePath, remoteFile.FileName));
        }

        // Path planning above is cheap and order-sensitive; the network part is
        // neither. Run the downloads with the same concurrency as the upload
        // path instead of paying one round trip per book.
        if (fileDownloadRequests.Count > 0)
        {
            var warningLock = new object();
            await Parallel.ForEachAsync(
                fileDownloadRequests,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, settings.ConcurrentRequests),
                    CancellationToken = cancellationToken
                },
                async (request, token) =>
                {
                    try
                    {
                        if (await EnsureLocalBlobAsync(
                                client,
                                settings,
                                request.Hash,
                                request.AbsolutePath,
                                progress,
                                token))
                            Interlocked.Increment(ref filesDownloaded);
                    }
                    catch (SyncObjectNotFoundException)
                    {
                        lock (warningLock)
                        {
                            warnings.Add(UiText.Get("《{0}》的 S3 文件对象不存在，已跳过。", request.FileName));
                            isPartial = true;
                        }
                    }
                    catch (InvalidDataException exception)
                    {
                        lock (warningLock)
                        {
                            warnings.Add(UiText.Get("《{0}》校验失败：{1}", request.FileName, UiText.Localize(exception.Message)));
                            isPartial = true;
                        }
                    }
                });
        }

        foreach (var prepared in preparedFilesByLocalId.Values)
        {
            var absolutePath = ResolveDataPath(prepared.RelativePath);
            if (absolutePath is null || !File.Exists(absolutePath))
                remoteOnlyFileHashes.Add(prepared.Source.Sha256);
        }

        // A retry can see the database row created by an earlier merge, so it
        // will not enter the "new book" branch below. If any of its files is
        // still remote-only, retain the incoming-book provenance as well.
        foreach (var localBookId in preparedFilesByLocalId.Values
                     .Where(prepared => remoteOnlyFileHashes.Contains(prepared.Source.Sha256))
                     .Select(prepared => prepared.LocalBookId))
            remoteBookIds.Add(localBookId);

        var coverRequests = new Dictionary<Guid, (string RelativePath, string AbsolutePath, string Hash,
            DateTimeOffset UpdatedAt, string? SyncVersion)>();
        foreach (var remoteBook in remoteBooks)
        {
            if (!IsSha256(remoteBook.CoverHash)
                || string.IsNullOrWhiteSpace(remoteBook.CoverFileName)
                || !bookMap.TryGetValue(remoteBook.Id, out var localBookId))
                continue;

            var existing = localIdentity.BooksById.GetValueOrDefault(localBookId);
            var localSnapshotBook = localSnapshot.Books.FirstOrDefault(book => book.Id == localBookId);
            var localCoverPath = localSnapshotBook?.LocalCoverPath ?? existing?.CoverPath;
            var remoteWins = localSnapshotBook is null
                || IsRowNewer(
                    remoteBook.SyncVersion, remoteBook.UpdatedAt,
                    localSnapshotBook.SyncVersion, localSnapshotBook.UpdatedAt);
            if (!remoteWins && !string.IsNullOrWhiteSpace(localCoverPath)) continue;
            if (coverRequests.TryGetValue(localBookId, out var previousCover)
                && IsRowNewer(
                    previousCover.SyncVersion, previousCover.UpdatedAt,
                    remoteBook.SyncVersion, remoteBook.UpdatedAt))
                continue;

            // Never replace the currently displayed cover during network I/O.
            // The database transaction below decides whether this version wins.
            var relativePath = Path.Combine("covers",
                $"{localBookId:N}-{remoteBook.CoverHash!.ToLowerInvariant()}{NormalizeCoverExtension(remoteBook.CoverFileName)}");
            var absolutePath = ResolveDataPath(relativePath);
            if (absolutePath is null) continue;
            coverRequests[localBookId] =
                (Path.GetRelativePath(_paths.Data, absolutePath), absolutePath, remoteBook.CoverHash!,
                    remoteBook.UpdatedAt, remoteBook.SyncVersion);
        }

        if (coverRequests.Count > 0)
        {
            var failedCovers = new HashSet<Guid>();
            var warningLock = new object();
            await Parallel.ForEachAsync(
                coverRequests,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, settings.ConcurrentRequests),
                    CancellationToken = cancellationToken
                },
                async (entry, token) =>
                {
                    var (localBookId, request) = entry;
                    try
                    {
                        if (await EnsureLocalBlobAsync(
                                client,
                                settings,
                                request.Hash,
                                request.AbsolutePath,
                                progress,
                                token))
                            coversDownloaded = true;
                    }
                    catch (SyncObjectNotFoundException)
                    {
                        lock (warningLock)
                        {
                            warnings.Add(UiText.Get("有封面对象不存在，已跳过封面同步。"));
                            isPartial = true;
                            failedCovers.Add(localBookId);
                        }
                    }
                    catch (InvalidDataException exception)
                    {
                        lock (warningLock)
                        {
                            warnings.Add(UiText.Get("有封面校验失败：{0}", UiText.Localize(exception.Message)));
                            isPartial = true;
                            failedCovers.Add(localBookId);
                        }
                    }
                });

            foreach (var (localBookId, request) in coverRequests)
            {
                if (failedCovers.Contains(localBookId)) continue;
                if (coverUpdates.TryGetValue(localBookId, out var earlier)
                    && !string.Equals(earlier.RelativePath, request.RelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    var earlierPath = ResolveDataPath(earlier.RelativePath);
                    if (earlierPath is not null) pathsToDelete.Add(earlierPath);
                }
                coverUpdates[localBookId] = (request.RelativePath, request.UpdatedAt, request.SyncVersion);
            }
        }

        var retainedWarningCount = warnings.Count;
        var retainedBooks = await PrepareReadingMaterialBooksAsync(client, settings, snapshots, bookMap, progress, warnings, cancellationToken);
        isPartial |= warnings.Count > retainedWarningCount;
        var locallyKnownFiles = localIdentity.FilesById.Values.ToDictionary(file => file.Id, file => file.BookId);
        foreach (var prepared in preparedFilesByLocalId.Values)
            locallyKnownFiles[prepared.LocalFileId] = prepared.LocalBookId;

        var changed = duplicateBooksMerged > 0 || readingHistoryMerged;
        var booksAdded = 0;
        var addedBookIds = new HashSet<Guid>();
        var annotationsApplied = 0;
        S3SyncSnapshot? mergedBaseline = null;
        try
        {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await using (var connection = await OpenDatabaseConnectionAsync(cancellationToken))
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            // Network I/O may have taken minutes. Rebase against the current
            // database and deletion journal under the same writer transaction
            // that applies the merge, so a just-deleted row cannot be revived.
            var currentLocal = await CaptureIncrementalDatabaseSnapshotAsync(
                connection, transaction, localSnapshot.DeviceId, localSnapshot.Tombstones,
                localSnapshot, localSnapshot.CreatedAt, DateTimeOffset.UtcNow, cancellationToken,
                // A local edit whose wall clock moved backwards can fall behind
                // the incremental cursor. Re-read the small non-annotation
                // tables so such an edit still gets a new hybrid-clock version
                // instead of being overwritten by an older remote row.
                fullScanNonAnnotationTables: true);
            mergedBaseline = currentLocal;
            await EnsureVersionSchemaAsync(connection, transaction, cancellationToken);
            var clock = await S3SyncClock.LoadAsync(
                connection, transaction, localSnapshot.DeviceId, cancellationToken);
            var currentReset = ReaderReadingDataReset.Latest(
                snapshots.Select(snapshot => snapshot.ReadingDataReset).Prepend(currentLocal.ReadingDataReset));
            if (currentReset != readingReset)
            {
                // A local reset during the download supersedes every remote
                // generation selected above. None of those counters or
                // checkpoints can be imported into the new generation.
                remoteProgress = [];
                remoteStats = [];
                readingReset = currentReset;
            }
            if (currentLocal.ReadingDataReset != readingReset)
            {
                await ReaderReadingDataReset.ApplyAsync(connection, transaction, readingReset!, cancellationToken);
                currentLocal.ReadingDataReset = readingReset;
                currentLocal.Progress.Clear();
                currentLocal.ReadingStats.Clear();
                changed = true;
            }
            var currentBooks = currentLocal.Books.ToDictionary(book => book.Id);
            var baselineBooks = (baselineSnapshot?.Books ?? []).ToDictionary(book => book.Id);
            var baselineAnnotations = (baselineSnapshot?.Annotations ?? []).ToDictionary(annotation => annotation.Id);
            var currentAnnotations = currentLocal.Annotations.ToDictionary(annotation => annotation.Id);
            var currentCollections = currentLocal.Collections.ToDictionary(collection => collection.Id);
            var currentReflections = currentLocal.BookReflections.ToDictionary(reflection => reflection.BookId);
            var currentProgressRows = currentLocal.Progress.ToDictionary(progress => progress.BookFileId);
            var currentBookmarks = currentLocal.Bookmarks.ToDictionary(bookmark => bookmark.Id);
            var currentLayouts = currentLocal.Layouts.ToDictionary(layout => layout.BookFileId);
            var currentStatsRows = currentLocal.ReadingStats.ToDictionary(stats => stats.BookFileId);
            var currentFileIds = currentLocal.Files.Select(file => file.Id).ToHashSet();
            var currentFilesById = currentLocal.Files.ToDictionary(file => file.Id);
            localSnapshot.ReadingDataReset = readingReset;
            localSnapshot.Tombstones = MergeTombstones(
                FilterReadingTombstones(currentLocal.Tombstones, readingReset),
                snapshots.SelectMany(snapshot => FilterReadingTombstones(snapshot.Tombstones ?? [], readingReset)));
            tombstoneIndex = BuildTombstoneIndex(localSnapshot.Tombstones);
            RefilterRemoteRows();
            foreach (var (id, prepared) in preparedFilesByLocalId.ToArray())
            {
                var source = remoteFiles.FirstOrDefault(file => fileMap.GetValueOrDefault(file.Id) == id);
                if (source is not null) preparedFilesByLocalId[id] = prepared with { Source = source };
                else
                {
                    preparedFilesByLocalId.Remove(id);
                    var unusedPath = ResolveDataPath(prepared.RelativePath);
                    if (unusedPath is not null) pathsToDelete.Add(unusedPath);
                }
            }
            var survivingBookIds = remoteBooks.Select(book => bookMap[book.Id]).ToHashSet();
            foreach (var (id, cover) in coverUpdates.ToArray())
            {
                if (!survivingBookIds.Contains(id)
                    || (currentBooks.TryGetValue(id, out var currentBook) && currentBook.LocalCoverPath is { } coverPath
                        && !string.IsNullOrWhiteSpace(coverPath)
                        && IsRowNewer(
                            currentBook.SyncVersion, currentBook.UpdatedAt,
                            cover.SyncVersion, cover.UpdatedAt)))
                {
                    var unusedPath = ResolveDataPath(cover.RelativePath);
                    if (unusedPath is not null) pathsToDelete.Add(unusedPath);
                    coverUpdates.Remove(id);
                }
            }
            locallyKnownFiles.Clear();
            foreach (var file in currentLocal.Files)
                locallyKnownFiles[file.Id] = file.BookId;
            foreach (var prepared in preparedFilesByLocalId.Values)
                locallyKnownFiles[prepared.LocalFileId] = prepared.LocalBookId;
            MoveSynchronizedFilePaths(
                preparedFilesByLocalId.Values,
                currentLocal,
                pathsToDelete,
                synchronizedFileMoves,
                cancellationToken);
            await SuppressDeletionTrackingAsync(connection, transaction, true, cancellationToken);
            using var commandCache = new SqliteCommandCache(connection, transaction);
            foreach (var book in remoteBooks)
            {
                if (!bookMap.TryGetValue(book.Id, out var localBookId)) continue;
                var variants = remoteBookVariantsById.GetValueOrDefault(book.Id) ?? [book];
                var liveVariants = variants
                    .Where(item => !IsTombstoned(
                        tombstoneIndex, "book", item.Id, item.UpdatedAt, item.SyncVersion))
                    .ToList();
                if (liveVariants.Count == 0) continue;
                if (!currentBooks.ContainsKey(localBookId)
                    && addedBookIds.Add(localBookId))
                {
                    booksAdded++;
                    remoteBookIds.Add(localBookId);
                }
                var bookToApply = MergeBookRows(
                    currentBooks.GetValueOrDefault(localBookId),
                    baselineBooks.GetValueOrDefault(localBookId),
                    liveVariants,
                    clock) ?? book;
                bookToApply.Id = localBookId;
                if (currentBooks.TryGetValue(localBookId, out var localBook)
                    && SameBookPayload(bookToApply, localBook))
                    continue;
                var affected = await UpsertBookAsync(
                    commandCache.Get(UpsertBookSql),
                    bookToApply,
                    localBookId,
                    cancellationToken);
                changed |= affected > 0;
            }

            // Merge every remote variant of the same annotation with the
            // local row before duplicate detection. This is the step that
            // preserves different fields edited independently on three or
            // more devices.
            var mergedRemoteAnnotations = new List<S3SyncAnnotation>();
            foreach (var (annotationId, variants) in remoteAnnotationVariantsById)
            {
                var liveVariants = variants
                    .Where(item => !IsTombstoned(
                        tombstoneIndex, "annotation", item.Id, item.UpdatedAt))
                    .ToList();
                if (liveVariants.Count == 0) continue;
                var merged = MergeAnnotationRows(
                    currentAnnotations.GetValueOrDefault(annotationId),
                    baselineAnnotations.GetValueOrDefault(annotationId),
                    liveVariants,
                    clock);
                if (merged is not null) mergedRemoteAnnotations.Add(merged);
            }
            remoteAnnotations = mergedRemoteAnnotations.ToArray();
            RefilterRemoteRows();

            var duplicateResolution = await ResolveDuplicateReaderRowsAsync(
                connection,
                transaction,
                commandCache,
                currentLocal.Annotations,
                currentLocal.Bookmarks,
                remoteAnnotations,
                remoteBookmarks,
                baselineSnapshot,
                bookMap,
                fileMap,
                locallyKnownFiles,
                clock,
                cancellationToken);
            remoteAnnotations = duplicateResolution.RemoteAnnotations;
            remoteBookmarks = duplicateResolution.RemoteBookmarks;
            changed |= duplicateResolution.Changed;
            if (duplicateResolution.Tombstones.Count > 0)
            {
                localSnapshot.Tombstones = MergeTombstones(
                    localSnapshot.Tombstones,
                    duplicateResolution.Tombstones);
                tombstoneIndex = BuildTombstoneIndex(localSnapshot.Tombstones);
            }

            foreach (var collection in remoteCollections)
            {
                if (!collectionMap.TryGetValue(collection.Id, out var localCollectionId)) continue;
                if (currentCollections.TryGetValue(localCollectionId, out var localCollection)
                    && !IsRowNewer(
                        collection.SyncVersion, CollectionUpdatedAt(collection),
                        localCollection.SyncVersion, CollectionUpdatedAt(localCollection)))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO BookCollections (Id, Name, CreatedAt, UpdatedAt)
                    VALUES ($id, $name, $createdAt, $updatedAt)
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        UpdatedAt = excluded.UpdatedAt;
                    """
                    );
                AddParameter(command, "$id", localCollectionId.ToString());
                AddParameter(command, "$name", collection.Name);
                AddParameter(command, "$createdAt", collection.CreatedAt.ToString("O"));
                AddParameter(command, "$updatedAt", CollectionUpdatedAt(collection).ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var prepared in preparedFilesByLocalId.Values)
            {
                if (currentFileIds.Contains(prepared.LocalFileId)) continue;
                var command = commandCache.Get(
                    """
                    INSERT OR IGNORE INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256, SyncUpdatedAt)
                    VALUES ($id, $bookId, $format, $relativePath, $size, $sha256, $syncUpdatedAt);
                    """
                    );
                AddParameter(command, "$id", prepared.LocalFileId.ToString());
                AddParameter(command, "$bookId", prepared.LocalBookId.ToString());
                AddParameter(command, "$format", prepared.Source.Format);
                AddParameter(command, "$relativePath", prepared.RelativePath);
                AddParameter(command, "$size", prepared.Source.Size);
                AddParameter(command, "$sha256", prepared.Source.Sha256.ToLowerInvariant());
                AddParameter(command, "$syncUpdatedAt", prepared.Source.ModifiedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
                locallyKnownFiles[prepared.LocalFileId] = prepared.LocalBookId;
            }

            foreach (var prepared in preparedFilesByLocalId.Values)
            {
                var fileIsNewer = !currentFilesById.TryGetValue(prepared.LocalFileId, out var localFile)
                    || IsRowNewer(
                        prepared.Source.SyncVersion, prepared.Source.ModifiedAt,
                        localFile.SyncVersion, localFile.ModifiedAt);
                if (fileIsNewer)
                {
                    var versionCommand = commandCache.Get("""
                        UPDATE BookFiles SET SyncUpdatedAt = $syncUpdatedAt
                        WHERE Id = $id AND BookId = $bookId;
                        """);
                    AddParameter(versionCommand, "$syncUpdatedAt", prepared.Source.ModifiedAt.ToString("O"));
                    AddParameter(versionCommand, "$id", prepared.LocalFileId.ToString());
                    AddParameter(versionCommand, "$bookId", prepared.LocalBookId.ToString());
                    changed |= await versionCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                }

                if (!currentFileIds.Contains(prepared.LocalFileId)
                    || string.Equals(
                        currentLocal.LocalFilePaths.GetValueOrDefault(prepared.LocalFileId),
                        prepared.RelativePath,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var command = commandCache.Get(
                    """
                    UPDATE BookFiles
                    SET RelativePath = $relativePath
                    WHERE Id = $id AND BookId = $bookId;
                    """);
                AddParameter(command, "$relativePath", prepared.RelativePath);
                AddParameter(command, "$id", prepared.LocalFileId.ToString());
                AddParameter(command, "$bookId", prepared.LocalBookId.ToString());
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var (bookId, cover) in coverUpdates)
            {
                if (currentBooks.TryGetValue(bookId, out var currentCoverBook)
                    && !IsRowNewer(
                        cover.SyncVersion, cover.UpdatedAt,
                        currentCoverBook.SyncVersion, currentCoverBook.UpdatedAt))
                    continue;
                if (currentBooks.TryGetValue(bookId, out var previous) && !string.IsNullOrWhiteSpace(previous.LocalCoverPath))
                {
                    var previousPath = ResolveDataPath(previous.LocalCoverPath);
                    if (previousPath is not null) pathsToDelete.Add(previousPath);
                }
                var command = commandCache.Get(
                    """
                    UPDATE Books SET CoverPath = $coverPath
                    WHERE Id = $bookId AND (CoverPath IS NULL OR CoverPath <> $coverPath);
                    """
                    );
                AddParameter(command, "$bookId", bookId.ToString());
                AddParameter(command, "$coverPath", cover.RelativePath);
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var item in remoteItems)
            {
                if (!collectionMap.TryGetValue(item.CollectionId, out var localCollectionId)
                    || !bookMap.TryGetValue(item.BookId, out var localBookId))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                    VALUES ($collectionId, $bookId, $addedAt);
                    """
                    );
                AddParameter(command, "$collectionId", localCollectionId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$addedAt", item.AddedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var annotation in remoteAnnotations)
            {
                TryMapAnnotationRow(annotation, bookMap, fileMap, locallyKnownFiles, out var localBookId, out var localFileId);
                var annotationToApply = currentAnnotations.TryGetValue(annotation.Id, out var localAnnotation)
                    ? MergeAnnotationRows(localAnnotation, baselineAnnotations.GetValueOrDefault(annotation.Id),
                        [annotation], clock) ?? CloneAnnotation(annotation)
                    : CloneAnnotation(annotation);
                annotationToApply.Id = annotation.Id;
                annotationToApply.BookId = localBookId;
                annotationToApply.BookFileId = localFileId;
                if (currentAnnotations.TryGetValue(annotation.Id, out localAnnotation)
                    && SameAnnotationPayload(annotationToApply, localAnnotation))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderAnnotations (
                        Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                        SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt)
                    VALUES (
                        $id, $bookId, $bookFileId, $chapterPath, $fragment, $startOffset, $endOffset,
                        $selectedText, $prefix, $suffix, $color, $underlineStyle, $note, $createdAt, $updatedAt)
                    ON CONFLICT(Id) DO UPDATE SET
                        BookId = excluded.BookId, BookFileId = excluded.BookFileId,
                        ChapterPath = excluded.ChapterPath, Fragment = excluded.Fragment,
                        StartOffset = excluded.StartOffset, EndOffset = excluded.EndOffset,
                        SelectedText = excluded.SelectedText, Prefix = excluded.Prefix,
                        Suffix = excluded.Suffix, Color = excluded.Color,
                        UnderlineStyle = excluded.UnderlineStyle, Note = excluded.Note,
                        UpdatedAt = excluded.UpdatedAt;
                    """
                    );
                AddParameter(command, "$id", annotationToApply.Id.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$chapterPath", annotationToApply.ChapterPath);
                AddParameter(command, "$fragment", annotationToApply.Fragment);
                AddParameter(command, "$startOffset", annotationToApply.StartOffset);
                AddParameter(command, "$endOffset", annotationToApply.EndOffset);
                AddParameter(command, "$selectedText", annotationToApply.SelectedText);
                AddParameter(command, "$prefix", annotationToApply.Prefix);
                AddParameter(command, "$suffix", annotationToApply.Suffix);
                AddParameter(command, "$color", annotationToApply.Color);
                AddParameter(command, "$underlineStyle", annotationToApply.UnderlineStyle);
                AddParameter(command, "$note", annotationToApply.Note);
                AddParameter(command, "$createdAt", annotationToApply.CreatedAt.ToString("O"));
                AddParameter(command, "$updatedAt", annotationToApply.UpdatedAt.ToString("O"));
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                changed |= affected > 0;
                annotationsApplied += affected > 0 ? 1 : 0;
            }

            foreach (var reflection in remoteBookReflections)
            {
                if (!bookMap.TryGetValue(reflection.BookId, out var localBookId))
                    continue;
                if (currentReflections.TryGetValue(localBookId, out var localReflection)
                    && !IsRowNewer(
                        reflection.SyncVersion, reflection.UpdatedAt,
                        localReflection.SyncVersion, localReflection.UpdatedAt))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderBookReflections (BookId, Content, CreatedAt, UpdatedAt)
                    VALUES ($bookId, $content, $createdAt, $updatedAt)
                    ON CONFLICT(BookId) DO UPDATE SET
                        Content = excluded.Content, CreatedAt = excluded.CreatedAt,
                        UpdatedAt = excluded.UpdatedAt;
                    """
                    );
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$content", reflection.Content);
                AddParameter(command, "$createdAt", reflection.CreatedAt.ToString("O"));
                AddParameter(command, "$updatedAt", reflection.UpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var item in remoteProgress)
            {
                if (!TryMapReaderRow(item.BookFileId, fileMap, locallyKnownFiles, out var localBookId, out var localFileId))
                    continue;
                if (currentProgressRows.TryGetValue(localFileId, out var localProgress)
                    && !IsRowNewer(
                        item.SyncVersion, item.UpdatedAt,
                        localProgress.SyncVersion, localProgress.UpdatedAt))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderProgress (
                        BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                        ProgressPercent, FlowMode, UpdatedAt, ContentPositionJson)
                    VALUES (
                        $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                        $progressPercent, $flowMode, $updatedAt, $contentPosition)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId, ChapterPath = excluded.ChapterPath,
                        Fragment = excluded.Fragment, ChapterIndex = excluded.ChapterIndex,
                        ScrollPosition = excluded.ScrollPosition, ProgressPercent = excluded.ProgressPercent,
                        FlowMode = excluded.FlowMode, UpdatedAt = excluded.UpdatedAt,
                        ContentPositionJson = excluded.ContentPositionJson;
                    """
                    );
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$chapterPath", item.ChapterPath);
                AddParameter(command, "$fragment", item.Fragment);
                AddParameter(command, "$chapterIndex", item.ChapterIndex);
                AddParameter(command, "$scrollPosition", item.ScrollPosition);
                AddParameter(command, "$progressPercent", item.ProgressPercent);
                AddParameter(command, "$flowMode", item.FlowMode);
                AddParameter(command, "$updatedAt", item.UpdatedAt.ToString("O"));
                AddParameter(command, "$contentPosition", item.ContentPositionJson);
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var bookmark in remoteBookmarks)
            {
                if (!TryMapReaderRow(bookmark.BookFileId, fileMap, locallyKnownFiles, out var localBookId, out var localFileId))
                    continue;
                if (currentBookmarks.TryGetValue(bookmark.Id, out var localBookmark)
                    && !IsRowNewer(
                        bookmark.SyncVersion, bookmark.CreatedAt,
                        localBookmark.SyncVersion, localBookmark.CreatedAt))
                    continue;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderBookmarks (
                        Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                        ScrollPosition, FlowMode, Title, Quote, CreatedAt, ContentPositionJson)
                    VALUES (
                        $id, $bookId, $bookFileId, $chapterPath, $fragment, $chapterIndex,
                        $scrollPosition, $flowMode, $title, $quote, $createdAt, $contentPosition)
                    ON CONFLICT(Id) DO UPDATE SET
                        BookId = excluded.BookId, BookFileId = excluded.BookFileId,
                        ChapterPath = excluded.ChapterPath, Fragment = excluded.Fragment,
                        ChapterIndex = excluded.ChapterIndex, ScrollPosition = excluded.ScrollPosition,
                        FlowMode = excluded.FlowMode, Title = excluded.Title,
                        Quote = excluded.Quote, CreatedAt = excluded.CreatedAt,
                        ContentPositionJson = excluded.ContentPositionJson;
                    """
                    );
                AddParameter(command, "$id", bookmark.Id.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$chapterPath", bookmark.ChapterPath);
                AddParameter(command, "$fragment", bookmark.Fragment);
                AddParameter(command, "$chapterIndex", bookmark.ChapterIndex);
                AddParameter(command, "$scrollPosition", bookmark.ScrollPosition);
                AddParameter(command, "$flowMode", bookmark.FlowMode);
                AddParameter(command, "$title", bookmark.Title);
                AddParameter(command, "$quote", bookmark.Quote);
                AddParameter(command, "$createdAt", bookmark.CreatedAt.ToString("O"));
                AddParameter(command, "$contentPosition", bookmark.ContentPositionJson);
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var layoutGroup in remoteLayouts.GroupBy(layout => layout.BookFileId))
            {
                var layout = MergeRemoteLayouts(layoutGroup);
                if (!TryMapReaderRow(layout.BookFileId, fileMap, locallyKnownFiles, out var localBookId, out var localFileId))
                    continue;
                S3SyncLayout mergedLayout;
                if (currentLayouts.TryGetValue(localFileId, out var localLayout))
                {
                    mergedLayout = MergeLayoutRows(localLayout, layout);
                    if (ReferenceEquals(mergedLayout, localLayout)) continue;
                }
                else
                {
                    mergedLayout = layout;
                }
                var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
                    mergedLayout.FontScale,
                    mergedLayout.LineHeight,
                    mergedLayout.MaxWidth,
                    mergedLayout.BodyPadding,
                    mergedLayout.FontFamily ?? ReaderFontDefaults.DefaultFamily,
                    mergedLayout.FlowMode,
                    mergedLayout.VerticalWriting,
                    mergedLayout.TwoPageMode)
                {
                    ParagraphIndent = mergedLayout.ParagraphIndent
                });
                var normalizedAppearance = ReaderAppearanceSettings.Normalize(new ReaderAppearanceSettings
                {
                    Theme = mergedLayout.AppearanceTheme,
                    PaperEnabled = mergedLayout.AppearancePaperEnabled,
                    PaperStrength = mergedLayout.AppearancePaperStrength,
                    FibersEnabled = mergedLayout.AppearanceFibersEnabled
                });
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderLayoutSettings (
                        BookFileId, BookId, FontScale, LineHeight, MaxWidth, BodyPadding,
                        FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent,
                        UseBookOverride, AppearanceTheme, AppearancePaperEnabled,
                        AppearancePaperStrength, AppearanceFibersEnabled, ProfileUpdatedAt, LayoutUpdatedAt, UpdatedAt)
                    VALUES (
                        $bookFileId, $bookId, $fontScale, $lineHeight, $maxWidth, $bodyPadding,
                        $fontFamily, $flowMode, $verticalWriting, $twoPageMode, $paragraphIndent,
                        $useBookOverride, $appearanceTheme, $appearancePaperEnabled,
                        $appearancePaperStrength, $appearanceFibersEnabled, $profileUpdatedAt, $layoutUpdatedAt, $updatedAt)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId, FontScale = excluded.FontScale,
                        LineHeight = excluded.LineHeight, MaxWidth = excluded.MaxWidth,
                        BodyPadding = excluded.BodyPadding, FontFamily = excluded.FontFamily,
                        FlowMode = excluded.FlowMode, VerticalWriting = excluded.VerticalWriting,
                        TwoPageMode = excluded.TwoPageMode, ParagraphIndent = excluded.ParagraphIndent,
                        UseBookOverride = excluded.UseBookOverride,
                        AppearanceTheme = excluded.AppearanceTheme,
                        AppearancePaperEnabled = excluded.AppearancePaperEnabled,
                        AppearancePaperStrength = excluded.AppearancePaperStrength,
                        AppearanceFibersEnabled = excluded.AppearanceFibersEnabled,
                        ProfileUpdatedAt = excluded.ProfileUpdatedAt,
                        LayoutUpdatedAt = excluded.LayoutUpdatedAt,
                        UpdatedAt = excluded.UpdatedAt;
                    """
                    );
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$fontScale", normalized.FontScale);
                AddParameter(command, "$lineHeight", normalized.LineHeight);
                AddParameter(command, "$maxWidth", normalized.MaxWidth);
                AddParameter(command, "$bodyPadding", normalized.BodyPadding);
                AddParameter(command, "$fontFamily", normalized.FontFamily);
                AddParameter(command, "$flowMode", normalized.FlowMode);
                AddParameter(command, "$verticalWriting", normalized.VerticalWriting ? 1 : 0);
                AddParameter(command, "$twoPageMode", normalized.TwoPageMode ? 1 : 0);
                AddParameter(command, "$paragraphIndent", normalized.ParagraphIndent ? 1 : 0);
                AddParameter(command, "$useBookOverride", mergedLayout.ProfileUpdatedAt is not null && mergedLayout.UseBookOverride ? 1 : 0);
                AddParameter(command, "$appearanceTheme", (int)normalizedAppearance.Theme);
                AddParameter(command, "$appearancePaperEnabled", normalizedAppearance.PaperEnabled ? 1 : 0);
                AddParameter(command, "$appearancePaperStrength", normalizedAppearance.PaperStrength);
                AddParameter(command, "$appearanceFibersEnabled", normalizedAppearance.FibersEnabled ? 1 : 0);
                AddParameter(command, "$profileUpdatedAt", mergedLayout.ProfileUpdatedAt?.ToString("O"));
                AddParameter(command, "$layoutUpdatedAt", mergedLayout.LayoutUpdatedAt?.ToString("O")
                    ?? (mergedLayout.ProfileUpdatedAt is null ? mergedLayout.UpdatedAt.ToString("O") : null));
                AddParameter(command, "$updatedAt", mergedLayout.UpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            foreach (var stats in remoteStats)
            {
                if (!TryMapReaderRow(stats.BookFileId, fileMap, locallyKnownFiles, out var localBookId, out var localFileId))
                {
                    // Deleted books retain their reading history without
                    // recreating a library entry or downloading their files.
                    // Old snapshots without any identity metadata cannot be
                    // safely matched to archived records on another device.
                    if (string.IsNullOrWhiteSpace(stats.BookTitle) && !IsSha256(stats.FileSha256)) continue;
                    localBookId = stats.BookId;
                    localFileId = stats.BookFileId;
                }
                var mergedSeconds = await ReadingTimeSyncTracker.MergeAsync(
                    connection, transaction, localFileId, stats.SecondsByDevice, stats.CumulativeSeconds, cancellationToken);
                var localStatsRow = currentStatsRows.GetValueOrDefault(localFileId);
                var statsIsNewer = localStatsRow is null
                    || IsRowNewer(
                        stats.SyncVersion, stats.UpdatedAt,
                        localStatsRow.SyncVersion, localStatsRow.UpdatedAt);
                var progressPercent = statsIsNewer ? stats.ProgressPercent : localStatsRow!.ProgressPercent;
                var completedChapters = statsIsNewer ? stats.CompletedChapters : localStatsRow!.CompletedChapters;
                var totalChapters = statsIsNewer ? stats.TotalChapters : localStatsRow!.TotalChapters;
                var statsUpdatedAt = statsIsNewer ? stats.UpdatedAt : localStatsRow!.UpdatedAt;
                var command = commandCache.Get(
                    """
                    INSERT INTO ReaderReadingStats (
                        BookFileId, BookId, CumulativeSeconds, ProgressPercent,
                        CompletedChapters, TotalChapters, UpdatedAt)
                    VALUES (
                        $bookFileId, $bookId, $cumulativeSeconds, $progressPercent,
                        $completedChapters, $totalChapters, $updatedAt)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId, CumulativeSeconds = excluded.CumulativeSeconds,
                        ProgressPercent = excluded.ProgressPercent,
                        CompletedChapters = excluded.CompletedChapters,
                        TotalChapters = excluded.TotalChapters, UpdatedAt = excluded.UpdatedAt;
                    """
                    );
                AddParameter(command, "$bookFileId", localFileId.ToString());
                AddParameter(command, "$bookId", localBookId.ToString());
                AddParameter(command, "$cumulativeSeconds", mergedSeconds);
                AddParameter(command, "$progressPercent", progressPercent);
                AddParameter(command, "$completedChapters", completedChapters);
                AddParameter(command, "$totalChapters", totalChapters);
                AddParameter(command, "$updatedAt", statsUpdatedAt.ToString("O"));
                changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
                changed |= await ReaderReadingHistory.RememberAsync(
                    connection, transaction, localBookId, localFileId, stats.BookTitle, stats.FileSha256, cancellationToken);
                changed |= await ReadingTimeSyncTracker.UpdateTotalAsync(
                    connection, transaction, localFileId, mergedSeconds, cancellationToken) > 0;
                await ReadingTimeSyncTracker.MergeReadingDaysAsync(
                    connection, transaction, localFileId, stats.SecondsByDateByDevice, cancellationToken);
            }

            changed |= await ApplyTombstonesAsync(
                connection,
                transaction,
                localSnapshot.Tombstones,
                currentLocal,
                bookMap,
                fileMap,
                collectionMap,
                remoteBooks,
                remoteFiles,
                remoteCollections,
                remoteItems,
                remoteAnnotations,
                remoteBookReflections,
                remoteProgress,
                remoteBookmarks,
                remoteLayouts,
                remoteStats,
                pathsToDelete,
                commandCache,
                cancellationToken);

            changed |= await SaveReadingMaterialBooksAsync(connection, transaction, retainedBooks, cancellationToken);

            // Older snapshots can contain both the system "未收藏" row and
            // user-created collection rows. Keep the derived system view
            // consistent before this merge becomes the next sync baseline.
            changed |= await NormalizeUncollectedCollectionAsync(
                connection,
                transaction,
                cancellationToken);

            var booksMergedAfterRemoteApply = await ConsolidateLocalDuplicateBooksInTransactionAsync(
                connection,
                transaction,
                pathsToDelete,
                commandCache,
                cancellationToken);
            duplicateBooksMerged += booksMergedAfterRemoteApply;
            changed |= booksMergedAfterRemoteApply > 0;

            changed |= await ConsolidateReadingHistoryRowsAsync(connection, transaction, commandCache, cancellationToken);
            await RemoveReferencedScheduledPathsAsync(
                connection,
                transaction,
                pathsToDelete,
                cancellationToken);
            await EnqueueLocalCleanupAsync(connection, transaction, pathsToDelete, cancellationToken);
            await SuppressDeletionTrackingAsync(connection, transaction, false, cancellationToken);
            await clock.SaveAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            synchronizedFileMoves.Clear();
            if (duplicateBooksMerged > 0)
                warnings.Add(UiText.Get("已自动合并 {0} 本重复书籍。", duplicateBooksMerged));
        }
        }
        catch
        {
            RestoreSynchronizedFileMoves(synchronizedFileMoves);
            throw;
        }

        isPartial |= await CleanupPendingLocalFilesAsync(warnings, cancellationToken);

        return new DatabaseMergeResult(
            booksAdded,
            filesDownloaded,
            annotationsApplied,
            changed || filesDownloaded > 0 || coversDownloaded,
            warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct()))
        {
            IsPartial = isPartial,
            RemoteBookIds = remoteBookIds,
            RemoteOnlyFileHashes = remoteOnlyFileHashes,
            LocalBaseline = mergedBaseline,
            ConsolidatedDuplicates = duplicateBooksMerged > 0 || readingHistoryMerged
        };
    }
}
