using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// iCloud Drive transport for the existing snapshot protocol. Only immutable
/// local read copies escape file coordination; uploads publish by same-directory
/// atomic rename after the entire input has been staged.
/// </summary>
public sealed class ICloudSyncObjectStore : ISyncObjectStore
{
    private const string UploadTemporaryPrefix = ".libzora-upload-";
    private readonly IICloudSyncFileSystem _fileSystem;
    private readonly string _accountIdentity;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _rootGate = new(1, 1);
    private string? _root;
    private bool _disposed;

    public ICloudSyncObjectStore(IICloudSyncFileSystem fileSystem, S3SyncSettings settings)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _accountIdentity = settings.ICloudAccountIdentity;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 600));
        EnsureAccount();
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken) =>
        (await ListObjectsAsync(prefix, cancellationToken)).Select(item => item.Key).ToArray();

    public Task<IReadOnlyList<SyncObjectMetadata>> ListObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        prefix = CanonicalKey(prefix, isPrefix: true);
        return RunAsync<IReadOnlyList<SyncObjectMetadata>>(async token =>
        {
            var root = await GetRootAsync(token).ConfigureAwait(false);
            await _fileSystem.RefreshDirectoryAsync(root, token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => _fileSystem.Coordinate(root, ICloudSyncFileAccess.Read, coordinatedRoot =>
                {
                    EnsureAccount();
                    EnsureContained(root, coordinatedRoot);
                    var result = new Dictionary<string, SyncObjectMetadata>(StringComparer.Ordinal);
                    var directories = new Stack<string>();
                    var searchDirectory = prefix.EndsWith('/')
                        ? prefix.TrimEnd('/') : prefix.Contains('/') ? prefix[..prefix.LastIndexOf('/')] : "";
                    var start = ResolveKey(root, searchDirectory);
                    if (!Directory.Exists(start)) return (IReadOnlyList<SyncObjectMetadata>)[];
                    directories.Push(start);
                    while (directories.TryPop(out var directory))
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            token.ThrowIfCancellationRequested();
                            var attributes = File.GetAttributes(path);
                            // Never follow remote-supplied symbolic links out of
                            // the entitled container (or around the key prefix).
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                directories.Push(path);
                                continue;
                            }
                            var name = Path.GetFileName(path);
                            if (name.StartsWith(UploadTemporaryPrefix, StringComparison.Ordinal)) continue;
                            // Older iCloud Drive versions expose dataless items
                            // as .name.icloud. List their logical key, not the
                            // placeholder's bytes; reads materialize that key.
                            if (name.StartsWith('.') && name.EndsWith(".icloud", StringComparison.Ordinal)
                                && name.Length > ".icloud".Length + 1)
                                name = name[1..^7];
                            if (name.StartsWith(UploadTemporaryPrefix, StringComparison.Ordinal)) continue;
                            var logicalPath = Path.Combine(Path.GetDirectoryName(path)!, name);
                            var key = Path.GetRelativePath(root, logicalPath).Replace(Path.DirectorySeparatorChar, '/');
                            key = CanonicalKey(key);
                            if (key.StartsWith(prefix, StringComparison.Ordinal))
                                result[key] = new SyncObjectMetadata(key, null);
                        }
                    }
                    EnsureAccount();
                    return (IReadOnlyList<SyncObjectMetadata>)result.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
                }, token), token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SyncListingUnavailableException(UiText.Get("暂时无法读取 iCloud 同步目录，请稍后重试。"), exception);
            }
        }, cancellationToken);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        key = CanonicalKey(key);
        return RunAsync(async token =>
        {
            var root = await GetRootAsync(token).ConfigureAwait(false);
            var path = ResolveKey(root, key);
            var exists = await Task.Run(() =>
            {
                EnsureAccount();
                return _fileSystem.ItemExists(path);
            }, token).ConfigureAwait(false);
            // A previous interrupted run may have left a complete local object
            // still queued for upload. Do not publish a snapshot referencing it
            // merely because it exists on this device.
            if (exists) await _fileSystem.EnsureUploadedAsync(path, token).ConfigureAwait(false);
            EnsureAccount();
            return exists;
        }, cancellationToken);
    }

    public Task<SyncObjectRead> OpenReadAsync(string key, CancellationToken cancellationToken, long rangeStart = 0)
    {
        key = CanonicalKey(key);
        if (rangeStart < 0) throw new ArgumentOutOfRangeException(nameof(rangeStart));
        return RunAsync(async token =>
        {
            var root = await GetRootAsync(token).ConfigureAwait(false);
            var path = ResolveKey(root, key);
            EnsureAccount();
            await _fileSystem.EnsureDownloadedAsync(path, token).ConfigureAwait(false);
            var temporaryPath = NewLocalTemporaryPath();
            try
            {
                await Task.Run(() => _fileSystem.Coordinate(path, ICloudSyncFileAccess.Read, coordinatedPath =>
                {
                    EnsureAccount();
                    EnsureContained(root, coordinatedPath);
                    RejectSymbolicLinks(root, coordinatedPath);
                    using var source = new FileStream(coordinatedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyWithCancellation(source, destination, token);
                    EnsureAccount();
                    return true;
                }, token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Range reads are deliberately not advertised. Returning the
                // complete stable copy lets the engine restart a resumed read.
                var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                return new SyncObjectRead(stream, stream.Length, stream);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                TryDeleteTemporaryFile(temporaryPath);
                throw new SyncObjectNotFoundException(UiText.Get("iCloud 同步文件尚不可用，请稍后重试。"), exception);
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryPath);
                throw;
            }
        }, cancellationToken);
    }

    public async Task PutAsync(string key, Stream input, string contentType, CancellationToken cancellationToken,
        string? knownSha256 = null)
    {
        key = CanonicalKey(key);
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stagedPath = NewLocalTemporaryPath();
        long stagedBytes;
        try
        {
            await using (var staged = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(staged, cancellationToken).ConfigureAwait(false);
                stagedBytes = staged.Length;
            }
            // Scale upload confirmation timeout for large books (minimum 256 KB/s
            // allowance, capped at 15 min) so large PDFs/EPUBs do not falsely fail
            // under the default 60s metadata timeout.
            var uploadTimeout = ComputeUploadTimeout(_timeout, stagedBytes);
            await RunAsync(async token =>
            {
                var root = await GetRootAsync(token).ConfigureAwait(false);
                await Task.Run(() =>
                {
                    EnsureAccount();
                    var path = ResolveKey(root, key);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    RejectSymbolicLinks(root, path);
                    _fileSystem.Coordinate(path, ICloudSyncFileAccess.Write, coordinatedPath =>
                    {
                        EnsureAccount();
                        EnsureContained(root, coordinatedPath);
                        RejectSymbolicLinks(root, coordinatedPath);
                        var temporaryPath = Path.Combine(Path.GetDirectoryName(coordinatedPath)!,
                            UploadTemporaryPrefix + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            using (var source = File.OpenRead(stagedPath))
                            using (var destination = new FileStream(temporaryPath, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None))
                            {
                                CopyWithCancellation(source, destination, token);
                                destination.Flush(flushToDisk: true);
                            }
                            token.ThrowIfCancellationRequested();
                            EnsureAccount();
                            // Both paths are siblings: this is a rename, never
                            // a cross-volume copy exposing a partial final key.
                            File.Move(temporaryPath, coordinatedPath, overwrite: true);
                        }
                        finally { TryDeleteTemporaryFile(temporaryPath); }
                        return true;
                    }, token);
                    EnsureAccount();
                }, token).ConfigureAwait(false);
                await _fileSystem.EnsureUploadedAsync(ResolveKey(root, key), token).ConfigureAwait(false);
                return true;
            }, cancellationToken, uploadTimeout).ConfigureAwait(false);
        }
        finally { TryDeleteTemporaryFile(stagedPath); }
    }

    internal static TimeSpan ComputeUploadTimeout(TimeSpan baseTimeout, long bytes)
    {
        if (bytes <= 0) return baseTimeout;
        var scaledSeconds = Math.Max(baseTimeout.TotalSeconds, 60.0 + bytes / (256.0 * 1024.0));
        return TimeSpan.FromSeconds(Math.Min(900.0, scaledSeconds));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        key = CanonicalKey(key);
        return RunAsync(async token =>
        {
            var root = await GetRootAsync(token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                EnsureAccount();
                var path = ResolveKey(root, key);
                if (!_fileSystem.ItemExists(path)) return;
                _fileSystem.Coordinate(path, ICloudSyncFileAccess.Delete, coordinatedPath =>
                {
                    EnsureAccount();
                    EnsureContained(root, coordinatedPath);
                    RejectSymbolicLinks(root, coordinatedPath);
                    _fileSystem.DeleteItem(coordinatedPath);
                    return true;
                }, token);
                EnsureAccount();
            }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public async Task TestWriteAsync(string prefix, CancellationToken cancellationToken)
    {
        prefix = CanonicalKey(prefix, isPrefix: true);
        var rootPrefix = prefix.TrimEnd('/');
        var key = (rootPrefix.Length > 0 ? rootPrefix + "/" : "") + ".sync-test/" + Guid.NewGuid().ToString("N") + ".txt";
        using var input = new MemoryStream("Libzora iCloud sync probe"u8.ToArray(), writable: false);
        try { await PutAsync(key, input, "text/plain", cancellationToken).ConfigureAwait(false); }
        finally { await DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
    }

    private async Task<string> GetRootAsync(CancellationToken token)
    {
        EnsureAccount();
        await _rootGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_root is not null) return _root;
            var root = await _fileSystem.GetContainerDirectoryAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                throw new IOException(UiText.Get("当前应用无法访问 iCloud 云盘，请检查 iCloud 设置和应用权限。"));
            _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            EnsureAccount();
            return _root;
        }
        finally { _rootGate.Release(); }
    }

    private void EnsureAccount()
    {
        if (_accountIdentity.Length == 0 || !string.Equals(_accountIdentity, _fileSystem.AccountIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException(UiText.Get("系统 iCloud 账户已更改。请确认当前 Apple 账户后重新保存同步设置。"));
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutOverride ?? _timeout);
        try
        {
            var result = await action(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                EnsureAccount();
                return result;
            }
            catch
            {
                // A cancellation/account change after staging a read must not
                // leak its private temporary stream to an unobserved caller.
                if (result is IDisposable disposable) disposable.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(UiText.Get("等待 iCloud 文件超时，请检查网络连接后重试。"));
        }
    }

    private static string ResolveKey(string root, string key)
    {
        ValidateKey(key, isPrefix: true);
        var path = key.Length == 0 ? root : Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        EnsureContained(root, path);
        RejectSymbolicLinks(root, path);
        return path;
    }

    private static string CanonicalKey(string key, bool isPrefix = false)
    {
        ValidateKey(key, isPrefix);
        return key.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }

    private static void ValidateKey(string key, bool isPrefix = false)
    {
        if (key is null || key.Any(char.IsControl) || key.Contains('\\') || key.Contains(':') || key.Contains("//", StringComparison.Ordinal)
            || key.StartsWith('/') || (!isPrefix && (key.Length == 0 || key.EndsWith('/')))
            || key.TrimEnd('/').Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException(UiText.Get("iCloud 同步对象路径无效。"), nameof(key));
    }

    private static void EnsureContained(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, root, StringComparison.Ordinal)
            && !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException(UiText.Get("iCloud 同步对象路径超出同步目录。"));
    }

    private static void RejectSymbolicLinks(string root, string path)
    {
        var current = root;
        foreach (var component in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            if (component == ".") continue;
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(UiText.Get("iCloud 同步目录不能包含符号链接。"));
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }

    private static string NewLocalTemporaryPath() => Path.Combine(Path.GetTempPath(), "libzora-icloud-" + Guid.NewGuid().ToString("N") + ".tmp");

    private static void CopyWithCancellation(Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rootGate.Dispose();
    }
}
