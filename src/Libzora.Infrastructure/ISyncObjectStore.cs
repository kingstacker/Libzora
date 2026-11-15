namespace Libzora.Infrastructure;

/// <summary>
/// The storage contract the snapshot/merge protocol depends on. Keys are
/// relative to the configured remote root; uploads leave the input stream open.
///
/// Implementations are intentionally limited to transport: they list, read,
/// write and delete objects. Entity merge, deletion inference, tombstones and
/// conflict resolution stay in <see cref="S3SyncService"/> and must not depend
/// on the provider.
/// </summary>
/// <remarks>
/// Contract requirements for future providers:
/// <list type="bullet">
/// <item><see cref="ListKeysAsync"/> and <see cref="ListObjectsAsync"/> must
/// return a complete listing for the prefix, or throw
/// <see cref="SyncListingUnavailableException"/>. A partial listing would make
/// remote snapshot/object cleanup unsafe.</item>
/// <item><see cref="PutAsync"/> must not expose a partially written object.
/// Replacing an existing key is atomic from the engine's point of view.</item>
/// <item><see cref="DeleteAsync"/> is idempotent; deleting a missing key is a
/// success.</item>
/// <item><see cref="OpenReadAsync"/> may fall back to a full read when range
/// reads are unsupported. Truncated or unverifiable reads must throw.</item>
/// </list>
/// </remarks>
public interface ISyncObjectStore : IDisposable
{
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    Task<SyncObjectRead> OpenReadAsync(
        string key,
        CancellationToken cancellationToken,
        long rangeStart = 0);
    Task PutAsync(
        string key,
        Stream input,
        string contentType,
        CancellationToken cancellationToken,
        string? knownSha256 = null);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    Task TestWriteAsync(string prefix, CancellationToken cancellationToken);
}

public sealed record SyncObjectMetadata(string Key, DateTimeOffset? LastModified);

public sealed class SyncObjectRead(
    Stream stream,
    long contentLength,
    IDisposable owner,
    bool isPartialResponse = false,
    long? totalLength = null) : IDisposable
{
    public Stream ResponseStream { get; } = stream;
    public long ContentLength { get; } = contentLength;
    public bool IsPartialResponse { get; } = isPartialResponse;
    public long? TotalLength { get; } = totalLength;
    public void Dispose() => owner.Dispose();
}

public sealed class SyncObjectNotFoundException(string message, Exception? inner = null)
    : IOException(message, inner);

public sealed class SyncListingUnavailableException(string message, Exception inner)
    : IOException(message, inner);

/// <summary>
/// Optional authoritative time source. The account server returns its clock in
/// response headers; custom S3/WebDAV stores do not, so the client falls back
/// to its own clock there.
/// </summary>
public interface ISyncServerClockSource
{
    TimeSpan? ServerClockOffset { get; }
}
