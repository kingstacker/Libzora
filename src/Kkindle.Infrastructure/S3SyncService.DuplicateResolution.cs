using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    private sealed record AnnotationCandidate(S3SyncAnnotation Row, bool HasLocal, bool HasRemote);

    private sealed record BookmarkCandidate(
        S3SyncBookmark Row,
        S3SyncBookmark? Remote,
        bool HasLocal,
        bool HasRemote);

    private sealed record DuplicateResolutionResult(
        S3SyncAnnotation[] RemoteAnnotations,
        S3SyncBookmark[] RemoteBookmarks,
        List<S3SyncTombstone> Tombstones,
        bool Changed);

    /// <summary>
    /// Resolves duplicate reader rows that were created independently on two
    /// devices before either device synced. The merge is by stable ID first,
    /// then by a conservative content/position predicate. Losing rows get an
    /// explicit tombstone so the decision survives a full round trip through
    /// every device and cannot be undone by a stale snapshot.
    /// </summary>
    private async Task<DuplicateResolutionResult> ResolveDuplicateReaderRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCommandCache commandCache,
        IReadOnlyList<S3SyncAnnotation> localAnnotations,
        IReadOnlyList<S3SyncBookmark> localBookmarks,
        IReadOnlyList<S3SyncAnnotation> remoteAnnotations,
        IReadOnlyList<S3SyncBookmark> remoteBookmarks,
        S3SyncSnapshot? baseline,
        IReadOnlyDictionary<Guid, Guid> bookMap,
        IReadOnlyDictionary<Guid, Guid> fileMap,
        IReadOnlyDictionary<Guid, Guid> locallyKnownFiles,
        S3SyncClock clock,
        CancellationToken cancellationToken)
    {
        var tombstones = new List<S3SyncTombstone>();
        var changed = false;
        var baselineAnnotations = (baseline?.Annotations ?? []).ToDictionary(item => item.Id);
        var localAnnotationsById = localAnnotations.ToDictionary(item => item.Id);
        var localBookmarksById = localBookmarks.ToDictionary(item => item.Id);
        var remoteAnnotationsById = remoteAnnotations.ToDictionary(item => item.Id);
        var remoteBookmarksById = remoteBookmarks.ToDictionary(item => item.Id);

        var annotationCandidates = new List<AnnotationCandidate>();
        foreach (var id in localAnnotationsById.Keys.Union(remoteAnnotationsById.Keys))
        {
            localAnnotationsById.TryGetValue(id, out var local);
            remoteAnnotationsById.TryGetValue(id, out var remote);
            S3SyncAnnotation row;
            if (local is not null && remote is not null)
            {
                row = MergeAnnotationRow(local, baselineAnnotations.GetValueOrDefault(id), remote);
                row.Id = id;
            }
            else
            {
                row = CloneAnnotation((local ?? remote)!);
                if (local is null)
                {
                    TryMapAnnotationRow(remote!, bookMap, fileMap, locallyKnownFiles, out var bookId, out var fileId);
                    row.BookId = bookId;
                    row.BookFileId = fileId;
                }
            }

            annotationCandidates.Add(new AnnotationCandidate(row, local is not null, remote is not null));
        }

        var annotationDuplicates = ClusterDuplicates(
            annotationCandidates,
            candidate => (candidate.Row.BookFileId != Guid.Empty ? candidate.Row.BookFileId : candidate.Row.BookId,
                candidate.Row.ChapterPath ?? string.Empty,
                candidate.Row.Fragment ?? string.Empty),
            (left, right) => IsDuplicateAnnotation(left.Row, right.Row));
        var finalAnnotations = new List<S3SyncAnnotation>();
        foreach (var group in annotationDuplicates)
        {
            if (group.Count == 1)
            {
                var single = group[0];
                if (single.HasRemote) remoteAnnotationsById[single.Row.Id] = single.Row;
                finalAnnotations.Add(single.Row);
                continue;
            }

            changed = true;
            var winner = group[0];
            foreach (var candidate in group.Skip(1))
            {
                if (WinsDuplicate(candidate.Row, winner.Row, row => row.CreatedAt, row => row.Id))
                    winner = candidate;
            }

            var merged = CloneAnnotation(winner.Row);
            foreach (var candidate in group
                         .OrderBy(item => item.Row.CreatedAt)
                         .ThenBy(item => item.Row.Id))
            {
                merged.Note = CombineNotes(merged.Note, candidate.Row.Note);
                merged.CreatedAt = Earliest(merged.CreatedAt, candidate.Row.CreatedAt);
            }
            merged.UpdatedAt = Latest(
                Latest(winner.Row.UpdatedAt, group.Max(item => item.Row.UpdatedAt)),
                DateTimeOffset.UtcNow).AddMilliseconds(1);
            merged.SyncVersion = clock.Next(merged.UpdatedAt);

            if (winner.HasLocal)
                await WriteMergedAnnotationAsync(commandCache, merged, cancellationToken);
            if (winner.HasRemote)
                remoteAnnotationsById[winner.Row.Id] = merged;
            finalAnnotations.Add(merged);

            foreach (var loser in group.Where(item => item.Row.Id != winner.Row.Id))
            {
                if (loser.HasLocal)
                {
                    var delete = commandCache.Get("DELETE FROM ReaderAnnotations WHERE Id = $id;");
                    AddParameter(delete, "$id", loser.Row.Id.ToString());
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }
                remoteAnnotationsById.Remove(loser.Row.Id);
                var tombstone = new S3SyncTombstone
                {
                    EntityType = "annotation",
                    Key = loser.Row.Id.ToString("N"),
                    DeletedAt = Latest(loser.Row.UpdatedAt, merged.UpdatedAt).AddMilliseconds(1)
                };
                tombstone.SyncVersion = clock.Next(tombstone.DeletedAt);
                tombstones.Add(tombstone);
                await RecordSyntheticDeletionAsync(commandCache, tombstone, cancellationToken);
            }
        }

        var bookmarkCandidates = new List<BookmarkCandidate>();
        foreach (var id in localBookmarksById.Keys.Union(remoteBookmarksById.Keys))
        {
            localBookmarksById.TryGetValue(id, out var local);
            remoteBookmarksById.TryGetValue(id, out var remote);
            var row = CloneBookmark((local ?? remote)!);
            if (local is null)
            {
                Guid bookId;
                Guid fileId;
                if (TryMapReaderRow(remote!.BookFileId, fileMap, locallyKnownFiles, out var mappedBookId, out var mappedFileId))
                {
                    bookId = mappedBookId;
                    fileId = mappedFileId;
                }
                else
                {
                    bookId = remote.BookId;
                    fileId = remote.BookFileId;
                }
                row.BookId = bookId;
                row.BookFileId = fileId;
            }

            bookmarkCandidates.Add(new BookmarkCandidate(row, remote, local is not null, remote is not null));
        }

        var bookmarkDuplicates = ClusterDuplicates(
            bookmarkCandidates,
            candidate => (candidate.Row.BookFileId != Guid.Empty ? candidate.Row.BookFileId : candidate.Row.BookId,
                candidate.Row.ChapterPath ?? string.Empty,
                candidate.Row.Fragment ?? string.Empty),
            (left, right) => IsDuplicateBookmark(left.Row, right.Row));
        var finalBookmarks = new List<S3SyncBookmark>();
        foreach (var group in bookmarkDuplicates)
        {
            if (group.Count == 1)
            {
                // Apply the original remote row; the local clone above exists
                // only for duplicate comparison.
                finalBookmarks.Add(group[0].Remote ?? group[0].Row);
                continue;
            }

            changed = true;
            var winner = group[0];
            foreach (var candidate in group.Skip(1))
            {
                if (WinsDuplicate(candidate.Row, winner.Row, row => row.CreatedAt, row => row.Id))
                    winner = candidate;
            }

            if (winner.HasRemote)
                remoteBookmarksById[winner.Row.Id] = winner.Remote!;
            finalBookmarks.Add(winner.Remote ?? winner.Row);

            foreach (var loser in group.Where(item => item.Row.Id != winner.Row.Id))
            {
                if (loser.HasLocal)
                {
                    var delete = commandCache.Get("DELETE FROM ReaderBookmarks WHERE Id = $id;");
                    AddParameter(delete, "$id", loser.Row.Id.ToString());
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }
                remoteBookmarksById.Remove(loser.Row.Id);
                var tombstone = new S3SyncTombstone
                {
                    EntityType = "bookmark",
                    Key = loser.Row.Id.ToString("N"),
                    DeletedAt = Latest(loser.Row.CreatedAt, winner.Row.CreatedAt).AddMilliseconds(1)
                };
                tombstone.SyncVersion = clock.Next(tombstone.DeletedAt);
                tombstones.Add(tombstone);
                await RecordSyntheticDeletionAsync(commandCache, tombstone, cancellationToken);
            }
        }

        return new DuplicateResolutionResult(
            finalAnnotations.ToArray(),
            finalBookmarks.ToArray(),
            tombstones,
            changed);
    }

    private static S3SyncBookmark CloneBookmark(S3SyncBookmark source)
    {
        var clone = new S3SyncBookmark
        {
            Id = source.Id,
            BookId = source.BookId,
            BookFileId = source.BookFileId,
            ChapterPath = source.ChapterPath,
            Fragment = source.Fragment,
            ChapterIndex = source.ChapterIndex,
            ScrollPosition = source.ScrollPosition,
            FlowMode = source.FlowMode,
            ContentPositionJson = source.ContentPositionJson,
            Title = source.Title,
            Quote = source.Quote,
            CreatedAt = source.CreatedAt,
            SyncVersion = source.SyncVersion
        };
        CopyOpaqueData(clone, source);
        return clone;
    }

    private static async Task RecordSyntheticDeletionAsync(
        SqliteCommandCache commandCache,
        S3SyncTombstone tombstone,
        CancellationToken cancellationToken)
    {
        var command = commandCache.Get(
            """
            INSERT INTO S3SyncDeletionLog (EntityType, EntityKey, DeletedAt)
            VALUES ($type, $key, $deletedAt)
            ON CONFLICT(EntityType, EntityKey) DO UPDATE SET DeletedAt = excluded.DeletedAt
            WHERE julianday(excluded.DeletedAt) > julianday(S3SyncDeletionLog.DeletedAt);
            """);
        AddParameter(command, "$type", tombstone.EntityType);
        AddParameter(command, "$key", tombstone.Key);
        AddParameter(command, "$deletedAt", tombstone.DeletedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteMergedAnnotationAsync(
        SqliteCommandCache commandCache,
        S3SyncAnnotation annotation,
        CancellationToken cancellationToken)
    {
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
            """);
        AddParameter(command, "$id", annotation.Id.ToString());
        AddParameter(command, "$bookId", annotation.BookId.ToString());
        AddParameter(command, "$bookFileId", annotation.BookFileId.ToString());
        AddParameter(command, "$chapterPath", annotation.ChapterPath);
        AddParameter(command, "$fragment", annotation.Fragment);
        AddParameter(command, "$startOffset", annotation.StartOffset);
        AddParameter(command, "$endOffset", annotation.EndOffset);
        AddParameter(command, "$selectedText", annotation.SelectedText);
        AddParameter(command, "$prefix", annotation.Prefix);
        AddParameter(command, "$suffix", annotation.Suffix);
        AddParameter(command, "$color", annotation.Color);
        AddParameter(command, "$underlineStyle", annotation.UnderlineStyle);
        AddParameter(command, "$note", annotation.Note);
        AddParameter(command, "$createdAt", annotation.CreatedAt.ToString("O"));
        AddParameter(command, "$updatedAt", annotation.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static List<List<T>> ClusterDuplicates<T, TKey>(
        IReadOnlyList<T> candidates,
        Func<T, TKey> bucket,
        Func<T, T, bool> duplicate)
        where TKey : notnull
    {
        var groups = new List<List<T>>();
        var buckets = new Dictionary<TKey, List<List<T>>>();
        foreach (var candidate in candidates)
        {
            var key = bucket(candidate);
            if (!buckets.TryGetValue(key, out var localGroups))
            {
                localGroups = [];
                buckets[key] = localGroups;
            }

            var group = localGroups.FirstOrDefault(existing => existing.Any(item => duplicate(item, candidate)));
            if (group is null)
            {
                group = [candidate];
                localGroups.Add(group);
                groups.Add(group);
            }
            else
            {
                group.Add(candidate);
            }
        }
        return groups;
    }
}
