using System.Text.Json.Nodes;
using Libzora.Core;

namespace Libzora.Infrastructure;

public sealed partial class KindleAnnotationSyncService
{
    private const string BookmarkMetadata = "{\"mchl_color\":\"dark_blue\"}";
    private const long BookmarkProximityBytes = 128;

    private async Task<IReadOnlyList<ReaderBookmark>> ReadLocalBookmarksAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var bookmarks = new List<ReaderBookmark>();
        foreach (var file in book.Files)
            bookmarks.AddRange(await _readerData.GetBookmarksAsync(
                file.Id,
                cancellationToken,
                limit: 100_000));
        return bookmarks;
    }

    private async Task PrepareBookmarksAsync(BookContext context, CancellationToken cancellationToken)
    {
        var deviceBookmarks = context.DeviceRecords
            .Where(record => record.Dataset == 2)
            .OrderBy(record => record.Start)
            .ThenBy(record => record.CreatedTime)
            .ToArray();
        context.DeviceBookmarks.AddRange(deviceBookmarks);
        if (deviceBookmarks.Length == 0 && context.LocalBookmarks.Count == 0) return;

        var raw = context.RawText;
        var localPositions = new Dictionary<Guid, long>();
        if (raw is not null)
        {
            foreach (var local in context.LocalBookmarks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryResolveLocalBookmarkPositionAsync(context, local, raw, cancellationToken) is { } pos)
                    localPositions[local.Id] = pos;
            }
        }

        var matchedLocal = new HashSet<Guid>();
        foreach (var device in deviceBookmarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string deviceQuote = string.Empty, devicePrefix = string.Empty, deviceSuffix = string.Empty;
            var hasAnchor = raw is not null
                && raw.ExtractAnchorAt(device.Start, out deviceQuote, out devicePrefix, out deviceSuffix);
            var deviceQuoteKey = hasAnchor ? TextKey(deviceQuote) : string.Empty;

            var existing = context.LocalBookmarks.FirstOrDefault(local =>
                !matchedLocal.Contains(local.Id)
                && (device.AnnotationId == "libzora.bookmark." + local.Id.ToString("N")
                    || device.AnnotationId == "kkindle.bookmark." + local.Id.ToString("N")
                    || (localPositions.TryGetValue(local.Id, out var localPos)
                        && Math.Abs(localPos - device.Start) <= BookmarkProximityBytes)
                    || (deviceQuoteKey.Length >= 8
                        && MatchesBookmarkQuote(TextKey(local.Quote), deviceQuoteKey))));
            if (existing is not null)
            {
                matchedLocal.Add(existing.Id);
                continue;
            }

            if (!hasAnchor)
            {
                context.UnsupportedCount++;
                context.DeviceUnsupportedCount++;
                continue;
            }

            var imported = await CreateLocalBookmarkAsync(
                context, device, deviceQuote, devicePrefix, deviceSuffix, cancellationToken);
            if (imported is null)
            {
                context.UnsupportedCount++;
                context.DeviceUnsupportedCount++;
                continue;
            }
            context.BookmarksToComputer.Add(imported);
        }

        foreach (var local in context.LocalBookmarks.Where(bookmark => !matchedLocal.Contains(bookmark.Id)))
        {
            if (localPositions.TryGetValue(local.Id, out var pos))
            {
                context.BookmarksToDevice.Add(new DeviceBookmarkChange(local, pos));
            }
            else
            {
                context.UnsupportedCount++;
            }
        }
    }

    private async Task<long?> TryResolveLocalBookmarkPositionAsync(
        BookContext context,
        ReaderBookmark local,
        KindleRawBookText raw,
        CancellationToken cancellationToken)
    {
        if (local.ContentPosition is { TextOffset: >= 0 } position)
        {
            var chapters = await GetReaderChaptersAsync(context, local.BookFileId, cancellationToken);
            var chapter = chapters.FirstOrDefault(item =>
                string.Equals(item.ChapterPath, local.ChapterPath, StringComparison.OrdinalIgnoreCase));
            if (chapter is not null && position.TextOffset < chapter.Content.Length)
            {
                var start = position.TextOffset;
                var quoteLength = Math.Min(48, chapter.Content.Length - start);
                var quote = chapter.Content.Substring(start, quoteLength).Trim();
                var prefixStart = Math.Max(0, start - 40);
                var prefix = chapter.Content[prefixStart..start];
                var suffixStart = Math.Min(chapter.Content.Length, start + quoteLength);
                var suffix = chapter.Content[suffixStart..Math.Min(chapter.Content.Length, suffixStart + 40)];
                if (raw.TryFindAnchorPosition(quote, prefix, suffix, out var resolved))
                    return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(local.Quote)
            && raw.TryFindAnchorPosition(local.Quote, null, null, out var fromQuote))
            return fromQuote;

        return null;
    }

    private async Task<ReaderBookmark?> CreateLocalBookmarkAsync(
        BookContext context,
        KindleAnnotationRecord device,
        string quote,
        string prefix,
        string suffix,
        CancellationToken cancellationToken)
    {
        var chunks = await GetReaderChaptersAsync(context, null, cancellationToken);
        if (chunks.Count == 0) return null;

        LocatedQuote? location = null;
        foreach (var length in new[] { 24, 12, 6, 0 })
        {
            var p = length > 0 && prefix.Length > length ? prefix[^length..] : length > 0 ? prefix : null;
            var s = length > 0 && suffix.Length > length ? suffix[..length] : length > 0 ? suffix : null;
            location = LocateQuoteWithContext(chunks, quote, p, s)
                ?? (p is not null ? LocateQuoteWithContext(chunks, quote, p, null) : null)
                ?? (s is not null ? LocateQuoteWithContext(chunks, quote, null, s) : null);
            if (location is not null) break;
        }
        if (location is null && quote.Length > 16)
            location = LocateQuote(chunks, quote[..16]);
        if (location is null) return null;

        var title = string.IsNullOrWhiteSpace(location.Chunk.ChapterTitle)
            ? context.LocalBook.Title
            : location.Chunk.ChapterTitle;
        var displayQuote = location.SelectedText.Length > 80
            ? location.SelectedText[..80]
            : location.SelectedText;
        return new ReaderBookmark
        {
            BookId = context.LocalBook.Id,
            BookFileId = location.Chunk.BookFileId,
            ChapterPath = location.Chunk.ChapterPath,
            ChapterIndex = location.Chunk.ChapterIndex,
            FlowMode = 0,
            ContentPosition = new ReaderContentPosition { TextOffset = location.Start },
            Title = title,
            Quote = displayQuote,
            CreatedAt = device.CreatedAt
        };
    }

    private static int BuildBookmarkMutations(
        BookContext context,
        JsonObject bookData,
        string bookId,
        List<KindleAnnotationMutation> mutations,
        Dictionary<(string Id, string BookId), int> byKey)
    {
        var count = 0;
        foreach (var change in context.BookmarksToDevice)
        {
            var created = change.Local.CreatedAt.ToUnixTimeMilliseconds();
            var modified = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), created);
            var mutation = NewPayloadMutation(
                "libzora.bookmark." + change.Local.Id.ToString("N"),
                bookId,
                2,
                change.Position,
                change.Position,
                created,
                modified,
                "BOOKMARK",
                BookmarkMetadata,
                bookData);
            AddMutation(mutations, byKey, mutation);
            count++;
        }
        return count;
    }

    private static bool MatchesBookmarkQuote(string localQuoteKey, string deviceQuoteKey)
    {
        if (localQuoteKey.Length < 8 || deviceQuoteKey.Length < 8) return false;
        var compareLength = Math.Min(16, Math.Min(localQuoteKey.Length, deviceQuoteKey.Length));
        return localQuoteKey.AsSpan(0, compareLength)
            .Equals(deviceQuoteKey.AsSpan(0, compareLength), StringComparison.Ordinal);
    }

    private sealed record DeviceBookmarkChange(ReaderBookmark Local, long Position);
}
