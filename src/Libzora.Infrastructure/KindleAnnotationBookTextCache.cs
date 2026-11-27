using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>Shared, bounded read-only cache. Write plans always bypass cached reads.</summary>
internal sealed class KindleAnnotationBookTextCache
{
    private const long MaximumBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 2048;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReadResult> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KindleRawBookText> _identities = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, TaskCompletionSource<ReadResult>> _reads = new(StringComparer.Ordinal);
    private long _bytes;

    internal sealed record ReadResult(KindleRawBookText? Text, bool PermanentFailure = false);

    internal static bool IsSupported(KindleBook book) => book.Format.ToLowerInvariant() is "azw3" or "mobi" or "azw" or "prc";

    // Size alone is never evidence that an edition is unchanged.
    internal static string? Stamp(KindleDevice device, KindleBook book) =>
        book.ModifiedAt is null && string.IsNullOrWhiteSpace(book.Sha256) ? null : Hash(new[]
        {
            device.Identity, book.RelativePath.Replace('\\', '/').TrimStart('/').ToUpperInvariant(),
            book.Format.ToLowerInvariant(), book.Size.ToString(CultureInfo.InvariantCulture),
            (book.ModifiedAt?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture), book.Sha256.ToUpperInvariant()
        });

    internal static string Hash(IEnumerable<string> values) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));

    internal bool TryGetIdentity(string? stamp, out KindleRawBookText identity)
    {
        lock (_gate)
        {
            identity = null!;
            if (stamp is null || !_identities.TryGetValue(stamp, out var found)) return false;
            identity = found;
            return true;
        }
    }

    internal void ClearFailures()
    {
        lock (_gate)
        {
            foreach (var key in _texts.Where(pair => pair.Value.PermanentFailure).Select(pair => pair.Key).ToArray())
            {
                _texts.Remove(key);
                _order.Remove(key);
            }
        }
    }

    internal async Task<ReadResult> ReadAsync(
        KindleDevice device, KindleBook book, IKindleDeviceService kindle,
        bool allowCachedText, CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported(book) || book.Size > 512L * 1024 * 1024) return new(null, true);
        var stamp = Stamp(device, book);
        // Unversioned files only share an in-flight read, never a completed result.
        var key = stamp ?? Hash(new[] { device.Identity, book.RelativePath, book.Format, book.Size.ToString(CultureInfo.InvariantCulture) });
        TaskCompletionSource<ReadResult>? owner = null;
        Task<ReadResult>? pending = null;
        if (allowCachedText)
        {
            lock (_gate)
            {
                if (stamp is not null && _texts.TryGetValue(stamp, out var cached))
                {
                    _order.Remove(stamp);
                    _order.AddLast(stamp);
                    return cached;
                }
                if (_reads.TryGetValue(key, out var active)) pending = active.Task;
                else
                {
                    owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _reads[key] = owner;
                }
            }
            if (pending is not null) return await pending.WaitAsync(cancellationToken);
        }
        try
        {
            var result = await ReadFreshAsync(device, book, kindle, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            if (stamp is not null) Store(stamp, result);
            owner?.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            owner?.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            owner?.TrySetException(exception);
            // Observe failures even if this request had no followers.
            if (owner is not null) _ = owner.Task.Exception;
            throw;
        }
        finally
        {
            if (owner is not null)
                lock (_gate) _reads.Remove(key);
        }
    }

    private void Store(string stamp, ReadResult result)
    {
        lock (_gate)
        {
            if (result.Text is { } text)
            {
                _identities[stamp] = new([], text.Encoding, text.PalmName, text.Asin, text.UniqueId, text.Title, text.Author);
                while (_identities.Count > MaximumEntries) _identities.Remove(_identities.Keys.First());
            }
            if (_texts.Remove(stamp, out var old)) _bytes -= Size(old);
            _order.Remove(stamp);
            if (result.Text is null && !result.PermanentFailure || Size(result) > MaximumBytes / 4) return;
            _texts[stamp] = result;
            _order.AddLast(stamp);
            _bytes += Size(result);
            while ((_bytes > MaximumBytes || _texts.Count > MaximumEntries) && _order.First is { } first)
            {
                _order.RemoveFirst();
                if (_texts.Remove(first.Value, out var evicted)) _bytes -= Size(evicted);
            }
        }
    }

    private static long Size(ReadResult result) => result.Text is { } text
        ? text.Text.LongLength + (text.UnassembledText?.LongLength ?? 0) : 0;

    private static async Task<ReadResult> ReadFreshAsync(
        KindleDevice device, KindleBook book, IKindleDeviceService kindle,
        CancellationToken cancellationToken, IProgress<KindleAnnotationSyncProgress>? progress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Libzora", "annotation-book", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = await kindle.ExportBookAsync(device, book, directory,
                progress is null ? null : new KindleAnnotationTransferProgress(progress, KindleAnnotationSyncStage.ReadingBook, book.Title),
                cancellationToken);
            return new(await KindleBookTextService.ReadAsync(path, cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return new(null, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new(null);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
