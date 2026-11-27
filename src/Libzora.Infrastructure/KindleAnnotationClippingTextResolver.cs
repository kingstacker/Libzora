using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>Enriches the device list from the exact device book, without writing annotations.</summary>
internal static class KindleAnnotationClippingTextResolver
{
    private const int ResolutionVersion = 3;
    public static bool NeedsResolution(KindleClipping clip) => clip.SourceBook is not null
        && clip.Type == KindleClippingType.Highlight
        && (clip.TextResolutionVersion != ResolutionVersion
            || !clip.IsTextResolved && string.IsNullOrEmpty(clip.TextResolutionStamp));

    /// <summary>Only restore text after comparing current device records and the scanned file versions.</summary>
    public static void RestoreCachedText(
        KindleDevice device, IReadOnlyList<KindleBook> books, IReadOnlyList<KindleClipping> current,
        IReadOnlyList<KindleClipping> previous, bool retryUnresolved = false)
    {
        var stamp = LibraryStamp(device, books);
        var bookStamps = books.Select(book => BookStamp(device, book)).Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        var cached = previous.Where(clip => clip.SourceBook is not null && clip.Type == KindleClippingType.Highlight)
            .GroupBy(ClipKey).ToDictionary(group => group.Key, group => group.First());
        foreach (var clip in current.Where(clip => clip.SourceBook is not null && clip.Type == KindleClippingType.Highlight))
        {
            // No version evidence means the persisted text cannot be trusted.
            var source = cached.GetValueOrDefault(ClipKey(clip));
            if (source is not null && source.TextResolutionVersion == ResolutionVersion
                && (source.IsTextResolved
                    ? source.TextResolutionStamp is not null && bookStamps.Contains(source.TextResolutionStamp)
                    : !retryUnresolved && stamp is not null && source.TextResolutionStamp == stamp))
            {
                clip.BookTitle = source.BookTitle;
                clip.Author = source.Author;
                clip.Content = source.IsTextResolved ? source.Content : UnavailableText();
                clip.IsTextResolved = source.IsTextResolved;
                clip.TextResolutionVersion = source.TextResolutionVersion;
                clip.TextResolutionStamp = source.TextResolutionStamp;
            }
            else
            {
                clip.IsTextResolved = false;
                clip.TextResolutionVersion = 0;
                clip.TextResolutionStamp = null;
            }
        }
    }

    private static string ClipKey(KindleClipping clip) => KindleAnnotationBookTextCache.Hash(new[]
    {
        clip.Id, clip.SourceBook?.BookId ?? "", clip.SourceBook?.Asin ?? "", clip.SourceBook?.Guid ?? "",
        clip.StartPosition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        clip.EndPosition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        clip.Type.ToString()
    });

    private static string? LibraryStamp(KindleDevice device, IReadOnlyList<KindleBook> books)
    {
        var stamps = books.Select(book => KindleAnnotationBookTextCache.Stamp(device, book)).ToArray();
        if (stamps.Any(stamp => stamp is null)) return null;
        return "library:" + KindleAnnotationBookTextCache.Hash(stamps.Select(stamp => stamp!).Order(StringComparer.Ordinal)
            .Prepend("kindle-highlight-resolution-" + ResolutionVersion).Prepend(device.Identity));
    }

    private static string? BookStamp(KindleDevice device, KindleBook book) =>
        KindleAnnotationBookTextCache.Stamp(device, book) is { } stamp ? "book:" + stamp : null;

    private static string UnavailableText() => UiText.IsEnglish
        ? "Highlight text unavailable (book missing or unsupported format)"
        : "无法读取划线原文（书籍不可用或格式不支持）";

    public static async Task ResolveAsync(
        KindleDevice device, IReadOnlyList<KindleBook> deviceBooks,
        IReadOnlyList<KindleClipping> clippings, IKindleDeviceService kindle,
        CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null,
        KindleAnnotationBookTextCache? cache = null)
    {
        // Revalidate even when callers pass already-resolved in-memory rows.
        RestoreCachedText(device, deviceBooks, clippings, clippings);
        var pending = clippings.Where(NeedsResolution).ToArray();
        if (pending.Length == 0) return;
        cache ??= new();
        var stamp = LibraryStamp(device, deviceBooks);
        var identities = pending.Select(clip => clip.SourceBook!).DistinctBy(identity => identity.BookId).ToArray();
        var unresolved = identities.Select(identity => identity.BookId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = deviceBooks.Where(KindleAnnotationBookTextCache.IsSupported)
            .OrderByDescending(book => cache.TryGetIdentity(KindleAnnotationBookTextCache.Stamp(device, book), out var known)
                && identities.Any(known.Matches))
            .ThenByDescending(book => pending.Any(clip => string.Equals(clip.BookTitle, book.Title, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(book => book.Size).ToArray();
        var conclusive = true;
        for (var index = 0; index < candidates.Length && unresolved.Count > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var book = candidates[index];
            progress?.Report(new(KindleAnnotationSyncStage.ProcessingBooks, index, candidates.Length, book.Title));
            if (cache.TryGetIdentity(KindleAnnotationBookTextCache.Stamp(device, book), out var identity)
                && !identities.Any(item => unresolved.Contains(item.BookId) && identity.Matches(item))) continue;
            var result = await cache.ReadAsync(device, book, kindle, true, cancellationToken, progress);
            if (result.Text is not { } raw)
            {
                conclusive &= result.PermanentFailure;
                continue;
            }
            foreach (var source in identities)
            {
                if (!unresolved.Contains(source.BookId) || !raw.Matches(source)) continue;
                foreach (var clip in clippings.Where(clip => string.Equals(clip.SourceBook?.BookId, source.BookId, StringComparison.OrdinalIgnoreCase)))
                {
                    clip.BookTitle = raw.Title;
                    clip.Author = raw.Author;
                    if (clip.Type != KindleClippingType.Highlight || clip.StartPosition is not { } start || clip.EndPosition is not { } end) continue;
                    var quote = raw.ExtractDeviceRange(start, end,
                        writtenByLibzora: clip.Id.Contains(":libzora.highlight.", StringComparison.Ordinal));
                    if (string.IsNullOrWhiteSpace(quote)) continue;
                    clip.Content = quote;
                    clip.IsTextResolved = true;
                    clip.TextResolutionVersion = ResolutionVersion;
                    // An unrelated library change must not invalidate successfully resolved text.
                    clip.TextResolutionStamp = BookStamp(device, book);
                }
                unresolved.Remove(source.BookId);
            }
        }
        foreach (var clip in pending.Where(NeedsResolution))
        {
            clip.IsTextResolved = false;
            clip.TextResolutionVersion = ResolutionVersion;
            // Only cache an exhaustive, non-transient failure for a verifiable version.
            clip.TextResolutionStamp = conclusive ? stamp : null;
            clip.Content = UnavailableText();
        }
        progress?.Report(new(KindleAnnotationSyncStage.ProcessingBooks, candidates.Length, candidates.Length));
    }
}
