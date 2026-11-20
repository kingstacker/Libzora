using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Wraps any <see cref="ISyncObjectStore"/> to uniformly enforce provider
/// <see cref="SyncProviderCapabilities.MaxObjectBytes"/> and retry transient
/// <see cref="SyncRateLimitedException"/> throttling across all providers.
/// </summary>
internal sealed class GuardedSyncObjectStore(
    ISyncObjectStore inner,
    SyncProviderCapabilities capabilities,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
    : ISyncObjectStore, ISyncServerClockSource
{
    private const int MaxRateLimitAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public TimeSpan? ServerClockOffset => (inner as ISyncServerClockSource)?.ServerClockOffset;

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken) =>
        RetryAsync(ct => inner.ListKeysAsync(prefix, ct), cancellationToken);

    public Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken) =>
        RetryAsync(ct => inner.ListObjectsAsync(prefix, ct), cancellationToken);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        RetryAsync(ct => inner.ExistsAsync(key, ct), cancellationToken);

    public Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken, long rangeStart = 0) =>
        RetryAsync(ct => inner.OpenReadAsync(key, ct, rangeStart), cancellationToken);

    public async Task PutAsync(
        string key,
        Stream input,
        string contentType,
        CancellationToken cancellationToken,
        string? knownSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (capabilities.MaxObjectBytes is { } limit && limit > 0 && input.CanSeek && input.Length > limit)
        {
            throw new SyncObjectTooLargeException(
                UiText.Get("文件大小超过当前同步服务的单文件上限，无法上传。"),
                input.Length,
                limit);
        }

        var startPosition = input.CanSeek ? input.Position : -1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await inner.PutAsync(key, input, contentType, cancellationToken, knownSha256).ConfigureAwait(false);
                return;
            }
            catch (SyncRateLimitedException ex) when (attempt < MaxRateLimitAttempts && startPosition >= 0 && input.CanSeek)
            {
                await _delay(ResolveDelay(ex.RetryAfter, attempt), cancellationToken).ConfigureAwait(false);
                input.Position = startPosition;
            }
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        RetryAsync(async ct =>
        {
            await inner.DeleteAsync(key, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task TestWriteAsync(string prefix, CancellationToken cancellationToken) =>
        RetryAsync(async ct =>
        {
            await inner.TestWriteAsync(prefix, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public void Dispose() => inner.Dispose();

    private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (SyncRateLimitedException ex) when (attempt < MaxRateLimitAttempts)
            {
                await _delay(ResolveDelay(ex.RetryAfter, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ResolveDelay(TimeSpan? retryAfter, int attempt)
    {
        var fallback = TimeSpan.FromMilliseconds(400 * (1 << Math.Min(attempt, 4)));
        var chosen = retryAfter is { } hint && hint > TimeSpan.Zero ? hint : fallback;
        return chosen > MaxRetryDelay ? MaxRetryDelay : chosen;
    }
}
