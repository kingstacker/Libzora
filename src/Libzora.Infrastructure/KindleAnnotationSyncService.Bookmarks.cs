using System.Text.Json.Nodes;
using Libzora.Core;

namespace Libzora.Infrastructure;

public sealed partial class KindleAnnotationSyncService
{
    private const string BookmarkMetadata = "{\"mchl_color\":\"dark_blue\"}";

    private async Task<IReadOnlyList<ReaderBookmark>> ReadLocalBookmarksAsync(Book book, CancellationToken cancellationToken)
    {
        var bookmarks = new List<ReaderBookmark>();
        foreach (var file in book.Files)
            bookmarks.AddRange(await _readerData.GetBookmarksAsync(file.Id, cancellationToken, limit: 100_000));
        return bookmarks;
    }

    private async Task PrepareBookmarksAsync(BookContext context, CancellationToken cancellationToken)
    {
        var deviceBookmarks = context.DeviceRecords.Where(record => record.Dataset == 2)
            .OrderBy(record => record.Start).ThenBy(record => record.CreatedTime).ToArray();
        context.DeviceBookmarks.AddRange(deviceBookmarks);
        if (deviceBookmarks.Length == 0 && context.LocalBookmarks.Count == 0) return;
        if (context.RawText is not { } raw)
        {
            context.UnsupportedCount += deviceBookmarks.Length + context.LocalBookmarks.Count;
            return;
        }
        var resolved = new Dictionary<Guid, ResolvedBookmark>();
        foreach (var local in context.LocalBookmarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ResolveLocalBookmarkAsync(context, local, raw, cancellationToken) is not { } bookmark) continue;
            resolved[local.Id] = bookmark;
            if (BookmarkNeedsRepair(bookmark)) QueueBookmarkImport(context, bookmark.Prepared);
        }
        var ownedRecords = deviceBookmarks.Select(record => (Id: OwnedBookmarkId(record.AnnotationId), Record: record))
            .Where(item => item.Id.HasValue).GroupBy(item => item.Id!.Value)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Record).ToArray());
        var byPoint = resolved.Values.GroupBy(bookmark => bookmark.DevicePosition)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var matchedLocal = new HashSet<Guid>();
        var handledRecords = new HashSet<string>(StringComparer.Ordinal);
        var devicePoints = new HashSet<long>();

        // Stable IDs are authoritative and must be reserved before any location matching.
        foreach (var local in context.LocalBookmarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownedRecords.TryGetValue(local.Id, out var exact)) continue;
            matchedLocal.Add(local.Id);
            foreach (var device in exact) handledRecords.Add(device.AnnotationId);
            if (exact.Length != 1 || !resolved.TryGetValue(local.Id, out var bookmark))
            {
                context.UnsupportedCount += exact.Length;
                continue;
            }
            var existing = exact[0];
            devicePoints.Add(bookmark.DevicePosition);
            if (!raw.ExtractBookmarkAnchorAt(existing.Start, out _, out _, out _, out var canonical)
                || canonical != bookmark.DevicePosition)
                context.BookmarksToDevice.Add(new(local, bookmark.DevicePosition, existing));
        }

        foreach (var device in deviceBookmarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handledRecords.Contains(device.AnnotationId)) continue;
            if (!raw.ExtractBookmarkAnchorAt(device.Start, out var quote, out var prefix, out var suffix, out var point))
            {
                context.UnsupportedCount++;
                continue;
            }
            // This set also includes planned owned-ID repairs and earlier imports in this batch.
            if (!devicePoints.Add(point)) continue;
            if (byPoint.TryGetValue(point, out var samePoint))
            {
                foreach (var bookmark in samePoint) matchedLocal.Add(bookmark.Source.Id);
                continue;
            }
            var imported = await CreateLocalBookmarkAsync(context, device, quote, prefix, suffix, cancellationToken);
            if (imported is null)
            {
                context.UnsupportedCount++;
                continue;
            }
            QueueBookmarkImport(context, imported);
        }

        foreach (var local in context.LocalBookmarks.Where(bookmark => !matchedLocal.Contains(bookmark.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolved.TryGetValue(local.Id, out var bookmark))
            {
                context.UnsupportedCount++;
                continue;
            }
            // Exact, proven points only: neither byte proximity nor quote prefixes imply equality.
            if (devicePoints.Add(bookmark.DevicePosition))
                context.BookmarksToDevice.Add(new(local, bookmark.DevicePosition));
        }
    }

    private static Guid? OwnedBookmarkId(string id)
    {
        var start = id.StartsWith("libzora.bookmark.", StringComparison.Ordinal) ? "libzora.bookmark.".Length
            : id.StartsWith("kkindle.bookmark.", StringComparison.Ordinal) ? "kkindle.bookmark.".Length : 0;
        return start > 0 && Guid.TryParseExact(id.AsSpan(start), "N", out var guid) ? guid : null;
    }

    private static int BuildBookmarkMutations(BookContext context, JsonObject bookData, string bookId,
        List<KindleAnnotationMutation> mutations, Dictionary<(string Id, string BookId), int> byKey)
    {
        foreach (var change in context.BookmarksToDevice)
        {
            if (change.ExistingDevice is { } existing)
            {
                // Keep original IDs, creation times, colors and unknown firmware metadata.
                AddRelocatedMutation(mutations, byKey, existing, change.Position, change.Position);
                continue;
            }
            var created = change.Local.CreatedAt.ToUnixTimeMilliseconds();
            var modified = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), created);
            AddMutation(mutations, byKey, NewPayloadMutation("libzora.bookmark." + change.Local.Id.ToString("N"),
                bookId, 2, change.Position, change.Position, created, modified, "BOOKMARK", BookmarkMetadata, bookData));
        }
        return context.BookmarksToDevice.Count;
    }

    private sealed record DeviceBookmarkChange(ReaderBookmark Local, long Position, KindleAnnotationRecord? ExistingDevice = null);
}
