using Libzora.Core;

namespace Libzora.Infrastructure;

public sealed partial class KindleAnnotationSyncService
{
    private async Task<ResolvedBookmark?> ResolveLocalBookmarkAsync(
        BookContext context, ReaderBookmark local, KindleRawBookText raw, CancellationToken cancellationToken)
    {
        var position = ReaderContentPosition.Validate(local.ContentPosition);
        if (local.ContentPosition is not null && position is null || position?.ImageIndex is not null) return null;
        var hasAnchor = !string.IsNullOrWhiteSpace(position?.AnchorQuote);
        var quote = hasAnchor ? position!.AnchorQuote! : local.Quote;
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var prefix = hasAnchor ? position!.AnchorPrefix : null;
        var suffix = hasAnchor ? position!.AnchorSuffix : null;
        var chapters = await GetReaderChaptersAsync(context, local.BookFileId, cancellationToken);
        var preferred = chapters.Where(chapter => string.Equals(chapter.ChapterPath, local.ChapterPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var location = LocateQuoteWithContext(preferred.Length > 0 ? preferred : chapters, quote, prefix, suffix);
        if (location is null || !raw.TryFindBookmarkPosition(quote, prefix, suffix, out var devicePosition)) return null;

        var anchored = (position ?? new ReaderContentPosition()) with
        {
            TextOffset = location.Start, ImageIndex = null,
            AnchorQuote = hasAnchor ? quote : null, AnchorPrefix = hasAnchor ? prefix : null, AnchorSuffix = hasAnchor ? suffix : null
        };
        if (!hasAnchor)
        {
            // Upgrade legacy display-only quotes only after proving their full text.
            var enriched = anchored.WithTextAnchor(location.Chunk.Content);
            if (!string.IsNullOrWhiteSpace(enriched.AnchorQuote)
                && raw.TryFindBookmarkPosition(enriched.AnchorQuote, enriched.AnchorPrefix, enriched.AnchorSuffix, out var enrichedPosition)
                && enrichedPosition == devicePosition)
                anchored = enriched;
            else if (quote.Length <= 128)
                anchored = anchored with { AnchorQuote = quote };
        }
        var prepared = AtBookmarkLocation(local, location, anchored);
        return new(local, devicePosition, prepared);
    }

    private async Task<long?> TryResolveLocalBookmarkPositionAsync(
        BookContext context, ReaderBookmark local, KindleRawBookText raw, CancellationToken cancellationToken) =>
        (await ResolveLocalBookmarkAsync(context, local, raw, cancellationToken))?.DevicePosition;

    private static ReaderBookmark AtBookmarkLocation(ReaderBookmark source, LocatedQuote location, ReaderContentPosition position) => new()
    {
        Id = source.Id, BookId = source.BookId, BookFileId = location.Chunk.BookFileId,
        ChapterPath = location.Chunk.ChapterPath, ChapterIndex = location.Chunk.ChapterIndex,
        ContentPosition = position, Fragment = null, ScrollPosition = null, FlowMode = source.FlowMode,
        Title = source.Title, Quote = source.Quote, CreatedAt = source.CreatedAt
    };

    private static bool BookmarkNeedsRepair(ResolvedBookmark bookmark) =>
        bookmark.Source.BookFileId != bookmark.Prepared.BookFileId
        || !string.Equals(bookmark.Source.ChapterPath, bookmark.Prepared.ChapterPath, StringComparison.OrdinalIgnoreCase)
        || bookmark.Source.ContentPosition != bookmark.Prepared.ContentPosition;

    private async Task<ReaderBookmark?> CreateLocalBookmarkAsync(
        BookContext context, KindleAnnotationRecord device, string quote, string prefix, string suffix,
        CancellationToken cancellationToken)
    {
        var chunks = await GetReaderChaptersAsync(context, null, cancellationToken);
        // Even a short chapter-end anchor is accepted only with all available context and one match.
        var location = LocateQuoteWithContext(chunks, quote, prefix, suffix);
        if (location is null) return null;
        var title = string.IsNullOrWhiteSpace(location.Chunk.ChapterTitle) ? context.LocalBook.Title : location.Chunk.ChapterTitle;
        return new ReaderBookmark
        {
            BookId = context.LocalBook.Id, BookFileId = location.Chunk.BookFileId,
            ChapterPath = location.Chunk.ChapterPath, ChapterIndex = location.Chunk.ChapterIndex, FlowMode = 0,
            ContentPosition = new ReaderContentPosition
            {
                TextOffset = location.Start, AnchorQuote = quote, AnchorPrefix = prefix, AnchorSuffix = suffix
            },
            Title = title, Quote = location.SelectedText, CreatedAt = device.CreatedAt
        };
    }

    private static void QueueBookmarkImport(BookContext context, ReaderBookmark bookmark)
    {
        if (context.BookmarkComputerIndexes.TryGetValue(bookmark.Id, out var index))
            context.BookmarksToComputer[index] = bookmark;
        else
        {
            context.BookmarkComputerIndexes[bookmark.Id] = context.BookmarksToComputer.Count;
            context.BookmarksToComputer.Add(bookmark);
        }
    }

    private sealed record ResolvedBookmark(ReaderBookmark Source, long DevicePosition, ReaderBookmark Prepared);
}
