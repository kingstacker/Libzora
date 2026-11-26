using Libzora.Core;

namespace Libzora.Infrastructure;

public sealed partial class KindleAnnotationSyncService
{
    private readonly KindleAnnotationBookTextCache _deviceBookText = new();
    private const int MaxCachedReaderBooks = 16;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<BookContentChunk>> _readerChapterCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _readerChapterOrder = new();

    /// <summary>Reuse exact-edition text between the notes page and read-only sync checks.</summary>
    public Task ResolveClippingTextAsync(
        KindleDevice device, IReadOnlyList<KindleBook> books, IReadOnlyList<KindleClipping> clippings,
        IKindleDeviceService kindle, CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null) =>
        KindleAnnotationClippingTextResolver.ResolveAsync(device, books, clippings, kindle,
            cancellationToken, progress, _deviceBookText);

    /// <summary>Explicit retries clear deterministic failures, not successful book text.</summary>
    public void RetryUnresolvedClippingText() => _deviceBookText.ClearFailures();

    private static string? ReaderChapterKey(Book book, Guid? fileId)
    {
        if (book.Files.Count == 0 || book.Files.Any(file => string.IsNullOrWhiteSpace(file.Sha256))) return null;
        return $"{book.Id:N}|{fileId?.ToString("N")}|" + string.Join(
            ',',
            book.Files.OrderBy(file => file.Id).Select(file => $"{file.Id:N}:{file.Sha256.ToUpperInvariant()}"));
    }

    private bool TryGetCachedReaderChapters(string? key, out IReadOnlyList<BookContentChunk> chapters)
    {
        chapters = [];
        if (key is null) return false;
        lock (_cacheGate)
        {
            if (!_readerChapterCache.TryGetValue(key, out var found)) return false;
            chapters = found;
            _readerChapterOrder.Remove(key);
            _readerChapterOrder.AddLast(key);
            return true;
        }
    }

    private void StoreReaderChapters(string? key, IReadOnlyList<BookContentChunk> chapters)
    {
        if (key is null || chapters.Count == 0) return;
        lock (_cacheGate)
        {
            if (_readerChapterCache.ContainsKey(key)) _readerChapterOrder.Remove(key);
            _readerChapterCache[key] = chapters;
            _readerChapterOrder.AddLast(key);
            while (_readerChapterOrder.Count > MaxCachedReaderBooks && _readerChapterOrder.First is { } oldest)
            {
                _readerChapterOrder.RemoveFirst();
                _readerChapterCache.Remove(oldest.Value);
            }
        }
    }
}
