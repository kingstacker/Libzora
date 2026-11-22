using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Upserts, tombstones and SQL parameter helpers.
/// </summary>
public sealed partial class S3SyncService
{
    private const string UpsertBookSql =
        """
        INSERT INTO Books (
            Id, Title, Authors, Series, SeriesIndex, Description, Publisher, PublishDate,
            Isbn, PageCount, Binding, OnlineRating, OnlineRatingCount, Tags, Category,
            IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt)
        VALUES (
            $id, $title, $authors, $series, $seriesIndex, $description, $publisher, $publishDate,
            $isbn, $pageCount, $binding, $onlineRating, $onlineRatingCount, $tags, $category,
            $isFavorite, $readingStatus, NULL, $createdAt, $updatedAt)
        ON CONFLICT(Id) DO UPDATE SET
            Title = excluded.Title, Authors = excluded.Authors, Series = excluded.Series,
            SeriesIndex = excluded.SeriesIndex, Description = excluded.Description,
            Publisher = excluded.Publisher, PublishDate = excluded.PublishDate,
            Isbn = excluded.Isbn, PageCount = excluded.PageCount, Binding = excluded.Binding,
            OnlineRating = excluded.OnlineRating, OnlineRatingCount = excluded.OnlineRatingCount,
            Tags = excluded.Tags, Category = excluded.Category, IsFavorite = excluded.IsFavorite,
            ReadingStatus = excluded.ReadingStatus, UpdatedAt = excluded.UpdatedAt;
        """;

    private static async Task<int> UpsertBookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        S3SyncBook book,
        Guid localBookId,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, UpsertBookSql);
        return await UpsertBookAsync(command, book, localBookId, cancellationToken);
    }

    private static async Task<int> UpsertBookAsync(
        SqliteCommand command,
        S3SyncBook book,
        Guid localBookId,
        CancellationToken cancellationToken)
    {
        AddParameter(command, "$id", localBookId.ToString());
        AddParameter(command, "$title", book.Title);
        AddParameter(command, "$authors", book.Authors);
        AddParameter(command, "$series", book.Series);
        AddParameter(command, "$seriesIndex", book.SeriesIndex);
        AddParameter(command, "$description", book.Description);
        AddParameter(command, "$publisher", book.Publisher);
        AddParameter(command, "$publishDate", book.PublishDate);
        AddParameter(command, "$isbn", book.Isbn);
        AddParameter(command, "$pageCount", book.PageCount);
        AddParameter(command, "$binding", book.Binding);
        AddParameter(command, "$onlineRating", book.OnlineRating);
        AddParameter(command, "$onlineRatingCount", book.OnlineRatingCount);
        AddParameter(command, "$tags", book.Tags);
        AddParameter(command, "$category", book.Category);
        AddParameter(command, "$isFavorite", book.IsFavorite ? 1 : 0);
        AddParameter(command, "$readingStatus", (int)book.ReadingStatus);
        AddParameter(command, "$createdAt", book.CreatedAt.ToString("O"));
        AddParameter(command, "$updatedAt", book.UpdatedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryMapReaderRow(
        Guid remoteFileId,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> locallyKnownFiles,
        out Guid localBookId,
        out Guid localFileId)
    {
        localBookId = default;
        localFileId = default;
        // The mapped file owns the reading data. An older snapshot may still
        // name the book ID that existed before duplicate-book consolidation.
        return fileMap.TryGetValue(remoteFileId, out localFileId)
            && locallyKnownFiles.TryGetValue(localFileId, out localBookId);
    }

    private static void TryMapAnnotationRow(
        S3SyncAnnotation annotation,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> locallyKnownFiles,
        out Guid localBookId,
        out Guid localFileId)
    {
        if (TryMapReaderRow(annotation.BookFileId, fileMap, locallyKnownFiles, out localBookId, out localFileId))
            return;

        // Annotation rows intentionally survive book/file tombstones. On a
        // device that has never had that book, keep their stable remote IDs so
        // the orphaned annotations still replicate and can be restored later.
        localBookId = bookMap.GetValueOrDefault(annotation.BookId, annotation.BookId);
        localFileId = fileMap.GetValueOrDefault(annotation.BookFileId, annotation.BookFileId);
    }

    private static bool TryMapTombstoneId(
        string entityType,
        Guid remoteId,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> collectionMap,
        out Guid localId)
    {
        switch (entityType)
        {
            case "book":
                return bookMap.TryGetValue(remoteId, out localId);
            case "file":
                return fileMap.TryGetValue(remoteId, out localId);
            case "collection":
                return collectionMap.TryGetValue(remoteId, out localId);
            case "annotation":
            case "reflection":
            case "progress":
            case "bookmark":
            case "layout":
            case "stats":
                localId = remoteId;
                return true;
            default:
                localId = default;
                return false;
        }
    }

    internal static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameterValue = value ?? DBNull.Value;
        var index = command.Parameters.IndexOf(name);
        if (index >= 0)
            command.Parameters[index].Value = parameterValue;
        else
            command.Parameters.AddWithValue(name, parameterValue);
    }

    private async Task<bool> ApplyTombstonesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        S3SyncSnapshot localSnapshot,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> collectionMap,
        IReadOnlyCollection<S3SyncBook> remoteBooks,
        IReadOnlyCollection<S3SyncBookFile> remoteFiles,
        IReadOnlyCollection<S3SyncCollection> remoteCollections,
        IReadOnlyCollection<S3SyncCollectionItem> remoteItems,
        IReadOnlyCollection<S3SyncAnnotation> remoteAnnotations,
        IReadOnlyCollection<S3SyncBookReflection> remoteBookReflections,
        IReadOnlyCollection<S3SyncProgress> remoteProgress,
        IReadOnlyCollection<S3SyncBookmark> remoteBookmarks,
        IReadOnlyCollection<S3SyncLayout> remoteLayouts,
        IReadOnlyCollection<S3SyncReadingStats> remoteStats,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var liveVersions = new Dictionary<string, (string? Version, DateTimeOffset Time)>(StringComparer.OrdinalIgnoreCase);
        var localBookIds = localSnapshot.Books.Select(item => item.Id).ToHashSet();
        var localFileIds = localSnapshot.Files.Select(item => item.Id).ToHashSet();
        var localCollectionIds = localSnapshot.Collections.Select(item => item.Id).ToHashSet();
        var localCollectionItemKeys = localSnapshot.CollectionItems
            .Select(item => CompositeKey(item.CollectionId, item.BookId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localAnnotationIds = localSnapshot.Annotations.Select(item => item.Id).ToHashSet();
        var localBookReflectionIds = localSnapshot.BookReflections.Select(item => item.BookId).ToHashSet();
        var localProgressIds = localSnapshot.Progress.Select(item => item.BookFileId).ToHashSet();
        var localBookmarkIds = localSnapshot.Bookmarks.Select(item => item.Id).ToHashSet();
        var localLayoutIds = localSnapshot.Layouts.Select(item => item.BookFileId).ToHashSet();
        var localStatsIds = localSnapshot.ReadingStats.Select(item => item.BookFileId).ToHashSet();
        void AddLive(string type, Guid id, DateTimeOffset version, string? syncVersion)
        {
            AddLiveKey(type, id.ToString("N"), version, syncVersion);
        }

        void AddLiveKey(string type, string id, DateTimeOffset version, string? syncVersion)
        {
            var key = VersionKey(type, id);
            if (!liveVersions.TryGetValue(key, out var existing)
                || IsRowNewer(syncVersion, version, existing.Version, existing.Time))
                liveVersions[key] = (syncVersion, version);
        }

        // A local re-create/update must win over a remote tombstone when its
        // version is newer. Remote rows alone are not enough because the
        // current local snapshot is uploaded only after this merge.
        foreach (var item in localSnapshot.Books)
            AddLive("book", item.Id, item.UpdatedAt, item.SyncVersion);
        foreach (var item in localSnapshot.Files)
            AddLive("file", item.Id, item.ModifiedAt, item.SyncVersion);
        foreach (var item in localSnapshot.Collections)
            AddLive("collection", item.Id, CollectionUpdatedAt(item), item.SyncVersion);
        foreach (var item in localSnapshot.CollectionItems)
            AddLiveKey("collection-item", CompositeKey(item.CollectionId, item.BookId), item.AddedAt, item.SyncVersion);
        foreach (var item in localSnapshot.Annotations)
            AddLive("annotation", item.Id, item.UpdatedAt, item.SyncVersion);
        foreach (var item in localSnapshot.BookReflections)
            AddLive("reflection", item.BookId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in localSnapshot.Progress)
            AddLive("progress", item.BookFileId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in localSnapshot.Bookmarks)
            AddLive("bookmark", item.Id, item.CreatedAt, item.SyncVersion);
        foreach (var item in localSnapshot.Layouts)
            AddLive("layout", item.BookFileId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in localSnapshot.ReadingStats)
            AddLive("stats", item.BookFileId, item.UpdatedAt, item.SyncVersion);

        foreach (var item in remoteBooks)
            if (bookMap.TryGetValue(item.Id, out var localId)) AddLive("book", localId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in remoteFiles)
            if (fileMap.TryGetValue(item.Id, out var localId)) AddLive("file", localId, item.ModifiedAt, item.SyncVersion);
        foreach (var item in remoteCollections)
            if (collectionMap.TryGetValue(item.Id, out var localId))
                AddLive("collection", localId, CollectionUpdatedAt(item), item.SyncVersion);
        foreach (var item in remoteItems)
        {
            if (collectionMap.TryGetValue(item.CollectionId, out var collectionId)
                && bookMap.TryGetValue(item.BookId, out var bookId))
                AddLiveKey("collection-item", CompositeKey(collectionId, bookId), item.AddedAt, item.SyncVersion);
        }
        foreach (var item in remoteAnnotations)
            AddLive("annotation", item.Id, item.UpdatedAt, item.SyncVersion);
        foreach (var item in remoteBookReflections)
            if (bookMap.TryGetValue(item.BookId, out var localId)) AddLive("reflection", localId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in remoteProgress)
            if (fileMap.TryGetValue(item.BookFileId, out var localId)) AddLive("progress", localId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in remoteBookmarks)
            AddLive("bookmark", item.Id, item.CreatedAt, item.SyncVersion);
        foreach (var item in remoteLayouts)
            if (fileMap.TryGetValue(item.BookFileId, out var localId)) AddLive("layout", localId, item.UpdatedAt, item.SyncVersion);
        foreach (var item in remoteStats)
            if (fileMap.TryGetValue(item.BookFileId, out var localId)) AddLive("stats", localId, item.UpdatedAt, item.SyncVersion);

        var deleteCollectionItemCommand = commandCache.Get(
            """
            DELETE FROM BookCollectionItems
            WHERE CollectionId = $collectionId AND BookId = $bookId;
            """);
        var deleteCollectionItemsCommand = commandCache.Get(
            "DELETE FROM BookCollectionItems WHERE CollectionId = $id;");
        var deleteCollectionCommand = commandCache.Get(
            "DELETE FROM BookCollections WHERE Id = $id;");

        var changed = false;
        foreach (var tombstone in tombstones
                     .Where(item => !string.IsNullOrWhiteSpace(item.EntityType) && !string.IsNullOrWhiteSpace(item.Key))
                     // Apply a book tombstone before its cascade-generated
                     // file tombstones so those files cannot be mistaken for
                     // independent deletions that should also remove notes.
                     .OrderBy(item => NormalizeTombstoneEntityType(item.EntityType) == "book" ? 0 : 1)
                     .ThenBy(item => item.DeletedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityType = NormalizeTombstoneEntityType(tombstone.EntityType);
            if (entityType == "collection-item")
            {
                var normalizedItemKey = NormalizeTombstoneKey(entityType, tombstone.Key);
                var parts = normalizedItemKey.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !Guid.TryParse(parts[0], out var remoteCollectionId)
                    || !Guid.TryParse(parts[1], out var remoteBookId)
                    || !collectionMap.TryGetValue(remoteCollectionId, out var localCollectionId)
                    || !bookMap.TryGetValue(remoteBookId, out var localBookId))
                    continue;
                var liveKey = VersionKey(entityType, CompositeKey(localCollectionId, localBookId));
                if (liveVersions.TryGetValue(liveKey, out var live)
                    && S3SyncVersion.IsNewer(
                        live.Version, live.Time, tombstone.SyncVersion, tombstone.DeletedAt))
                    continue;
                var localItemKey = CompositeKey(localCollectionId, localBookId);
                if (!localCollectionItemKeys.Contains(localItemKey))
                    continue;
                AddParameter(deleteCollectionItemCommand, "$collectionId", localCollectionId.ToString());
                AddParameter(deleteCollectionItemCommand, "$bookId", localBookId.ToString());
                changed |= await deleteCollectionItemCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                continue;
            }

            var normalizedTombstoneKey = NormalizeTombstoneKey(entityType, tombstone.Key);
            if (!Guid.TryParse(normalizedTombstoneKey, out var remoteId)) continue;
            if (!TryMapTombstoneId(
                    entityType,
                    remoteId,
                    bookMap,
                    fileMap,
                    collectionMap,
                    out var localId))
                continue;
            var localExists = entityType switch
            {
                "book" => localBookIds.Contains(localId),
                "file" => localFileIds.Contains(localId),
                "collection" => localCollectionIds.Contains(localId),
                "annotation" => localAnnotationIds.Contains(localId),
                "reflection" => localBookReflectionIds.Contains(localId),
                "progress" => localProgressIds.Contains(localId),
                "bookmark" => localBookmarkIds.Contains(localId),
                "layout" => localLayoutIds.Contains(localId),
                "stats" => localStatsIds.Contains(localId),
                _ => false
            };
            if (!localExists)
                continue;
            var versionKey = VersionKey(entityType, localId);
            if (liveVersions.TryGetValue(versionKey, out var newerLiveVersion)
                && S3SyncVersion.IsNewer(
                    newerLiveVersion.Version, newerLiveVersion.Time,
                    tombstone.SyncVersion, tombstone.DeletedAt))
                continue;

            switch (entityType)
            {
                case "book":
                    changed |= await DeleteBookRowsAsync(
                        localId,
                        pathsToDelete,
                        commandCache,
                        cancellationToken);
                    break;
                case "file":
                    changed |= await DeleteFileRowsAsync(
                        connection,
                        transaction,
                        localId,
                        pathsToDelete,
                        commandCache,
                        cancellationToken);
                    break;
                case "collection":
                {
                    AddParameter(deleteCollectionItemsCommand, "$id", localId.ToString());
                    changed |= await deleteCollectionItemsCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                    AddParameter(deleteCollectionCommand, "$id", localId.ToString());
                    changed |= await deleteCollectionCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
                    break;
                }
                case "annotation":
                    changed |= await DeleteRowAsync(
                        "ReaderAnnotations", "Id", localId, commandCache, cancellationToken);
                    break;
                case "reflection":
                    changed |= await DeleteRowAsync(
                        "ReaderBookReflections", "BookId", localId, commandCache, cancellationToken);
                    break;
                case "progress":
                    changed |= await DeleteRowAsync(
                        "ReaderProgress", "BookFileId", localId, commandCache, cancellationToken);
                    await DeleteRowAsync(
                        "ReaderSyncedPositionMarkers", "BookFileId", localId, commandCache, cancellationToken);
                    break;
                case "bookmark":
                    changed |= await DeleteRowAsync(
                        "ReaderBookmarks", "Id", localId, commandCache, cancellationToken);
                    break;
                case "layout":
                    changed |= await DeleteRowAsync(
                        "ReaderLayoutSettings", "BookFileId", localId, commandCache, cancellationToken);
                    break;
                case "stats":
                    changed |= await DeleteRowAsync(
                        "ReaderReadingStats", "BookFileId", localId, commandCache, cancellationToken);
                    break;
            }
        }

        return changed;
    }

    private async Task<bool> DeleteBookRowsAsync(
        Guid bookId,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var cover = commandCache.Get("SELECT CoverPath FROM Books WHERE Id = $id LIMIT 1;");
        AddParameter(cover, "$id", bookId.ToString());
        if (await cover.ExecuteScalarAsync(cancellationToken) is string coverPath)
        {
            var absolute = ResolveDataPath(coverPath);
            if (absolute is not null) pathsToDelete.Add(absolute);
        }

        var paths = commandCache.Get("SELECT RelativePath FROM BookFiles WHERE BookId = $bookId;");
        AddParameter(paths, "$bookId", bookId.ToString());
        await using (var reader = await paths.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var absolute = ResolveDataPath(reader.GetString(0));
                if (absolute is not null) pathsToDelete.Add(absolute);
            }
        }

        var changed = false;
        foreach (var statement in new[]
        {
            "DELETE FROM ReaderBookReflections WHERE BookId = $bookId;",
            "DELETE FROM ReaderProgress WHERE BookId = $bookId;",
            "DELETE FROM ReaderSyncedPositionMarkers WHERE BookId = $bookId;",
            "DELETE FROM ReaderBookmarks WHERE BookId = $bookId;",
            "DELETE FROM ReaderLayoutSettings WHERE BookId = $bookId;",
            "DELETE FROM BookContentChunks WHERE BookId = $bookId;",
            "DELETE FROM BookCollectionItems WHERE BookId = $bookId;",
            "DELETE FROM Books WHERE Id = $bookId;"
        })
        {
            var command = commandCache.Get(statement);
            AddParameter(command, "$bookId", bookId.ToString());
            changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        return changed;
    }

    private async Task<bool> DeleteFileRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        ICollection<string> pathsToDelete,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var pathCommand = commandCache.Get("SELECT RelativePath FROM BookFiles WHERE Id = $id LIMIT 1;");
        AddParameter(pathCommand, "$id", fileId.ToString());
        var relativePath = await pathCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (relativePath is not null)
        {
            var absolute = ResolveDataPath(relativePath);
            if (absolute is not null) pathsToDelete.Add(absolute);
        }

        var changed = false;
        foreach (var statement in new[]
        {
            "DELETE FROM ReaderProgress WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderSyncedPositionMarkers WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderBookmarks WHERE BookFileId = $fileId;",
            "DELETE FROM ReaderLayoutSettings WHERE BookFileId = $fileId;",
            "DELETE FROM BookContentChunks WHERE BookFileId = $fileId;",
            "DELETE FROM BookFiles WHERE Id = $fileId;"
        })
        {
            var command = commandCache.Get(statement);
            AddParameter(command, "$fileId", fileId.ToString());
            changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        return changed;
    }

    private static async Task<bool> DeleteRowAsync(
        string table,
        string idColumn,
        Guid id,
        SqliteCommandCache commandCache,
        CancellationToken cancellationToken)
    {
        var command = commandCache.Get($"DELETE FROM {table} WHERE {idColumn} = $id;");
        AddParameter(command, "$id", id.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static string CompositeKey(Guid left, Guid right) => $"{left:N}|{right:N}";

    private static string VersionKey(string type, Guid id) => VersionKey(type, id.ToString("N"));

    private static string VersionKey(string type, string id) =>
        $"{NormalizeTombstoneEntityType(type)}:{NormalizeTombstoneKey(type, id)}";

    private async Task<bool> ApplyRemoteSettingsAsync(
        S3SyncSettingsSnapshot? localSettings,
        IReadOnlyList<S3SyncSnapshot> remoteSnapshots,
        CancellationToken cancellationToken)
    {
        var candidates = remoteSnapshots
            .Select(snapshot => snapshot.Settings)
            .OfType<S3SyncSettingsSnapshot>()
            .ToArray();
        if (candidates.Length == 0) return false;
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        var settingsAtMergeStart = localSettings;
        localSettings = await CaptureSettingsUnderLockAsync(cancellationToken);
        PreserveSettingsTimestampsForUnchangedValues(localSettings, settingsAtMergeStart);
        S3SyncSettingsSnapshot Latest(Func<S3SyncSettingsSnapshot, DateTimeOffset?> version) =>
            candidates.MaxBy(settings => version(settings) ?? settings.UpdatedAt)!;
        var applied = false;

        // Compare each settings file independently. A cancelled/failed write
        // can then resume without an already-applied file masking the others.
        var remoteSettings = Latest(settings => settings.AppUpdatedAt);
        var remoteUpdatedAt = remoteSettings.AppUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.AppUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentApp = AppSettings.Normalize(await _appSettingsStore.LoadAsync(cancellationToken));
            var remoteApp = remoteSettings.App ?? new S3SyncAppSettings();
            var mergedApp = AppSettings.Normalize(currentApp with
            {
                UiLanguage = remoteApp.UiLanguage,
                PreferredOpenFormat = remoteApp.PreferredOpenFormat,
                AutoBackupEnabled = remoteApp.AutoBackupEnabled,
                AutoGenerateEpubAndAzw3OnImport = remoteApp.AutoGenerateEpubAndAzw3OnImport,
                CollectionsMutuallyExclusive = remoteApp.CollectionsMutuallyExclusive,
                AutoBackupRetention = remoteApp.AutoBackupRetention,
                AiEnabled = remoteApp.AiEnabled,
                // Network access is a device-local privacy and safety choice;
                // it is deliberately absent from synchronized settings.
                AutoUpdateCheckEnabled = remoteApp.AutoUpdateCheckEnabled,
                DevelopmentUpdateCheckEnabled = remoteApp.DevelopmentUpdateCheckEnabled,
                CompareKindleLibraryEnabled = remoteApp.CompareKindleLibraryEnabled,
                GridGalleryDisplay = remoteApp.GridGalleryDisplay,
                ShowSyncStatusIcon = remoteApp.ShowSyncStatusIcon,
                ShowLibraryPresenceIcon = remoteApp.ShowLibraryPresenceIcon,
                ShowUnearnedReadingMedals = remoteApp.ShowUnearnedReadingMedals,
                ReadingMaterialsCollapsedByDefault = remoteApp.ReadingMaterialsCollapsedByDefault,
                PinyinContextMenuEnabled = remoteApp.PinyinContextMenuEnabled,
                PinyinLocalOnly = remoteApp.PinyinLocalOnly,
                PinyinEngineId = string.IsNullOrWhiteSpace(remoteApp.PinyinEngineId)
                    ? currentApp.PinyinEngineId
                    : remoteApp.PinyinEngineId,
                DefaultReaderLayout = remoteApp.DefaultReaderLayout ?? new ReaderLayoutSettings()
            });
            await _appSettingsStore.SaveUnderLockAsync(mergedApp, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }

        remoteSettings = Latest(settings => settings.AiUpdatedAt);
        remoteUpdatedAt = remoteSettings.AiUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.AiUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentAi = await _aiSettingsStore.LoadAsync(cancellationToken);
            var remoteAi = remoteSettings.Ai ?? new S3SyncAiSettings();
            await _aiSettingsStore.SaveUnderLockAsync(new AiConnectionSettings
            {
                Provider = string.IsNullOrWhiteSpace(remoteAi.Provider) ? currentAi.Provider : remoteAi.Provider,
                BaseUrl = string.IsNullOrWhiteSpace(remoteAi.BaseUrl) ? currentAi.BaseUrl : remoteAi.BaseUrl,
                Model = string.IsNullOrWhiteSpace(remoteAi.Model) ? currentAi.Model : remoteAi.Model,
                ApiKey = currentAi.ApiKey
            }, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }

        remoteSettings = Latest(settings => settings.KindleEmailUpdatedAt);
        remoteUpdatedAt = remoteSettings.KindleEmailUpdatedAt ?? remoteSettings.UpdatedAt;
        if (remoteUpdatedAt > (localSettings.KindleEmailUpdatedAt ?? localSettings.UpdatedAt))
        {
            var currentEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
            var remoteEmail = remoteSettings.KindleEmail ?? new S3SyncKindleEmailSettings();
            await _kindleEmailSettingsStore.SaveUnderLockAsync(new KindleEmailSettings
            {
                KindleEmailAddress = remoteEmail.KindleEmailAddress,
                SenderEmailAddress = remoteEmail.SenderEmailAddress,
                SmtpHost = remoteEmail.SmtpHost,
                SmtpPort = remoteEmail.SmtpPort,
                SmtpUsername = remoteEmail.SmtpUsername,
                SmtpPassword = currentEmail.SmtpPassword,
                EnableSsl = remoteEmail.EnableSsl
            }, cancellationToken, remoteUpdatedAt);
            RemoteSettingsApplied?.Invoke(this, EventArgs.Empty);
            applied = true;
        }

        return applied;
    }
}
