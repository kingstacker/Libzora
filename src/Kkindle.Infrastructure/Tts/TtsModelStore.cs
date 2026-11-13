using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;

namespace Kkindle.Infrastructure;

public enum TtsModelDownloadPhase
{
    Preparing,
    Downloading,
    Verifying,
    Extracting,
    Completed,
}

public sealed record TtsModelDownloadProgress(
    TtsModelDownloadPhase Phase,
    long BytesReceived,
    long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

/// <summary>
/// Owns optional offline TTS packages below the application data directory.
/// Downloads come only from the pinned GitHub Release URL in the catalog;
/// partial files survive cancellation so the next attempt can resume with a
/// Range request. Nothing is installed until the archive hash, extracted
/// layout and required entries have all been verified.
/// </summary>
public sealed class TtsModelStore : IDisposable
{
    private const string UserAgent = "Kkindle-TTS-Models";
    private const string MarkerFileName = ".kkindle-tts-model.json";
    private const long MaximumEntryBytes = 1536L * 1024 * 1024;
    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private bool _disposed;

    public TtsModelStore(
        AppPaths paths,
        HttpClient? httpClient = null,
        string? proxyAddress = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _httpClient = httpClient ?? new HttpClient(CreateHttpHandler(proxyAddress))
        {
            // Large packs on slow links can legitimately take hours. The UI
            // cancellation token, not this timeout, is the way to stop one.
            Timeout = TimeSpan.FromHours(12),
        };
        _ownsHttpClient = httpClient is null;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(UserAgent, "1.0"));
    }

    public string Root => _paths.TtsModels;

    public string GetInstallDirectory(TtsModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Path.Combine(Root, package.Id);
    }

    public bool IsInstalled(TtsModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var directory = GetInstallDirectory(package);
        if (!Directory.Exists(directory)) return false;
        if (!TryReadMarker(package, directory, out _)) return false;
        foreach (var entry in package.RequiredEntries)
        {
            var path = Path.Combine(directory, entry);
            try
            {
                if (Directory.Exists(path)) continue;
                if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            return false;
        }
        return true;
    }

    public long GetInstalledBytes(TtsModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var directory = GetInstallDirectory(package);
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path).Length)
                .Sum();
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public async Task<string> DownloadAsync(
        TtsModelPackage package,
        IProgress<TtsModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidatePackage(package);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        try
        {
            var finalDirectory = GetInstallDirectory(package);
            if (IsInstalled(package)) return finalDirectory;

            _paths.EnsureDirectories();
            var downloadRoot = Path.Combine(Root, ".downloads");
            var stagingRoot = Path.Combine(Root, ".staging");
            Directory.CreateDirectory(downloadRoot);
            Directory.CreateDirectory(stagingRoot);

            var archivePath = Path.Combine(downloadRoot, package.ArchiveFileName);
            if (File.Exists(archivePath)
                && !await FileMatchesHashAsync(archivePath, package.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                TryDeleteFile(archivePath);
            }

            if (!File.Exists(archivePath))
            {
                progress?.Report(new TtsModelDownloadProgress(
                    TtsModelDownloadPhase.Preparing, 0, package.ArchiveBytes));
                await DownloadArchiveAsync(package, archivePath, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report(new TtsModelDownloadProgress(
                TtsModelDownloadPhase.Verifying, package.ArchiveBytes, package.ArchiveBytes));
            if (!await FileMatchesHashAsync(archivePath, package.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                TryDeleteFile(archivePath);
                throw new InvalidDataException("语音包 SHA-256 校验失败，下载内容可能不完整或已被替换。");
            }

            progress?.Report(new TtsModelDownloadProgress(
                TtsModelDownloadPhase.Extracting, package.ArchiveBytes, package.ArchiveBytes));
            stagingDirectory = Path.Combine(stagingRoot, $"{package.Id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            await Task.Run(
                    () => ExtractArchive(archivePath, stagingDirectory, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            var contentRoot = ResolveContentRoot(stagingDirectory);
            ValidateRequiredEntries(package, contentRoot);
            InstallStagedDirectory(contentRoot, finalDirectory);
            TryDeleteDirectory(stagingDirectory);
            stagingDirectory = null;
            WriteMarker(package, finalDirectory);
            TryDeleteFile(archivePath);

            progress?.Report(new TtsModelDownloadProgress(
                TtsModelDownloadPhase.Completed, package.ArchiveBytes, package.ArchiveBytes));
            return finalDirectory;
        }
        catch (OperationCanceledException)
        {
            // Keep the .partial file so a later attempt can resume.
            throw;
        }
        finally
        {
            if (stagingDirectory is not null) TryDeleteDirectory(stagingDirectory);
            _downloadGate.Release();
        }
    }

    public void Delete(TtsModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        TryDeleteDirectory(GetInstallDirectory(package));
        var downloadRoot = Path.Combine(Root, ".downloads");
        TryDeleteFile(Path.Combine(downloadRoot, package.ArchiveFileName));
        TryDeleteFile(Path.Combine(downloadRoot, package.ArchiveFileName + ".partial"));
    }

    private async Task DownloadArchiveAsync(
        TtsModelPackage package,
        string archivePath,
        IProgress<TtsModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partialPath = archivePath + ".partial";
        var existing = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        if (existing > package.ArchiveBytes)
        {
            TryDeleteFile(partialPath);
            existing = 0;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usingRange = existing > 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUri);
            if (usingRange)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureTrustedDownloadUri(
                response.RequestMessage?.RequestUri ?? package.DownloadUri);

            if (usingRange && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (existing == package.ArchiveBytes) break;
                TryDeleteFile(partialPath);
                existing = 0;
                continue;
            }

            if (usingRange && response.StatusCode == HttpStatusCode.OK)
            {
                // The server ignored Range. Start over with this response body.
                existing = 0;
                usingRange = false;
            }
            else if (usingRange)
            {
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    response.EnsureSuccessStatusCode();
                    throw new InvalidDataException("语音包服务器没有返回断点续传响应。");
                }
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.From != existing
                    || contentRange.Length is { } total && total != package.ArchiveBytes)
                {
                    TryDeleteFile(partialPath);
                    existing = 0;
                    continue;
                }
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }

            if (response.Content.Headers.ContentLength is { } remainingBytes
                && remainingBytes > package.ArchiveBytes - existing)
            {
                throw new InvalidDataException("语音包服务器返回的文件大小异常。");
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (existing > 0)
            {
                try
                {
                    await AppendFileToHashAsync(partialPath, hasher, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    TryDeleteFile(partialPath);
                    existing = 0;
                    usingRange = false;
                    continue;
                }
            }

            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                usingRange ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            var receivedThisAttempt = 0L;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                receivedThisAttempt += read;
                if (existing + receivedThisAttempt > package.ArchiveBytes)
                    throw new InvalidDataException("语音包下载超过了预期大小。");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new TtsModelDownloadProgress(
                    TtsModelDownloadPhase.Downloading,
                    existing + receivedThisAttempt,
                    package.ArchiveBytes));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            var received = existing + receivedThisAttempt;
            if (received != package.ArchiveBytes)
            {
                // Keep the partial file: a later click can continue from here.
                throw new InvalidDataException(
                    $"语音包下载不完整（{received} / {package.ArchiveBytes} 字节），请重试。");
            }

            var actualSha256 = Convert.ToHexString(hasher.GetHashAndReset());
            if (!actualSha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(partialPath);
                throw new InvalidDataException("语音包 SHA-256 校验失败，下载内容可能不完整或已被替换。");
            }

            File.Move(partialPath, archivePath, overwrite: true);
            return;
        }

        if (File.Exists(partialPath)
            && new FileInfo(partialPath).Length == package.ArchiveBytes)
        {
            File.Move(partialPath, archivePath, overwrite: true);
            return;
        }

        throw new InvalidDataException("无法从服务器继续下载语音包，请重试。");
    }

    private static async Task AppendFileToHashAsync(
        string path,
        IncrementalHash hasher,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
        }
    }

    private static async Task<bool> FileMatchesHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hasher.AppendData(buffer, 0, read);
            }
            var actual = Convert.ToHexString(hasher.GetHashAndReset());
            return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ExtractArchive(
        string archivePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var stagingFullPath = Path.GetFullPath(stagingDirectory);
        using var file = File.OpenRead(archivePath);
        using var bzip2 = new BZip2InputStream(file) { IsStreamOwner = false };
        using var tar = new TarInputStream(bzip2) { IsStreamOwner = false };
        var buffer = new byte[81920];
        var totalExtracted = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = tar.GetNextEntry();
            if (entry is null) break;
            var relativePath = NormalizeEntryName(entry.Name);
            if (relativePath is null) continue;

            var destination = Path.GetFullPath(Path.Combine(stagingFullPath, relativePath));
            if (!destination.StartsWith(
                    stagingFullPath + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("语音包包含不安全的文件路径。");
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (entry.Size <= 0)
            {
                // Symlinks, hard links and empty entries carry no model bytes.
                continue;
            }
            if (entry.Size > MaximumEntryBytes)
                throw new InvalidDataException("语音包内的文件超过大小限制。");

            totalExtracted += entry.Size;
            if (totalExtracted > MaximumExtractedBytes)
                throw new InvalidDataException("语音包解压后的体积超过限制。");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.SequentialScan);
            long written = 0;
            while (written < entry.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, entry.Size - written);
                var read = tar.Read(buffer, 0, toRead);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                written += read;
            }
            if (written != entry.Size)
                throw new InvalidDataException("语音包解压时读到不完整的文件。");
        }
    }

    private static string? NormalizeEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Replace('\\', '/').Trim();
        if (normalized.Length == 0
            || normalized.StartsWith('/')
            || normalized.Contains('\0'))
            return null;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Contains(':')) return null;
        }
        return Path.Combine(parts);
    }

    private static string ResolveContentRoot(string stagingDirectory)
    {
        if (HasModelFile(stagingDirectory))
            return stagingDirectory;
        foreach (var directory in Directory.EnumerateDirectories(stagingDirectory))
        {
            if (HasModelFile(directory))
                return directory;
        }
        throw new InvalidDataException("语音包中没有找到 model.onnx。");
    }

    private static bool HasModelFile(string directory) =>
        Directory.EnumerateFiles(directory, "model*.onnx").Any();

    private static void ValidateRequiredEntries(
        TtsModelPackage package,
        string contentRoot)
    {
        foreach (var entry in package.RequiredEntries)
        {
            var path = Path.Combine(contentRoot, entry);
            if (Directory.Exists(path)) continue;
            if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
            throw new InvalidDataException($"语音包缺少 {entry}。");
        }
    }

    private void InstallStagedDirectory(string contentRoot, string finalDirectory)
    {
        var backupDirectory = finalDirectory + $".backup-{Guid.NewGuid():N}";
        var movedOldDirectory = false;
        try
        {
            if (Directory.Exists(finalDirectory))
            {
                Directory.Move(finalDirectory, backupDirectory);
                movedOldDirectory = true;
            }
            Directory.Move(contentRoot, finalDirectory);
            if (movedOldDirectory) TryDeleteDirectory(backupDirectory);
        }
        catch
        {
            if (!Directory.Exists(finalDirectory)
                && movedOldDirectory
                && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, finalDirectory);
            }
            throw;
        }
    }

    private void WriteMarker(TtsModelPackage package, string directory)
    {
        var marker = new InstalledMarker(package.Id, package.Sha256, DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(directory, MarkerFileName),
            JsonSerializer.Serialize(marker, MarkerJsonOptions));
    }

    private static bool TryReadMarker(
        TtsModelPackage package,
        string directory,
        out InstalledMarker? marker)
    {
        marker = null;
        var path = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(path)) return false;
        try
        {
            marker = JsonSerializer.Deserialize<InstalledMarker>(
                File.ReadAllText(path),
                MarkerJsonOptions);
            return marker is not null
                && marker.Id.Equals(package.Id, StringComparison.Ordinal)
                && marker.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ValidatePackage(TtsModelPackage package)
    {
        EnsureTrustedDownloadUri(package.DownloadUri);
        if (package.ArchiveBytes <= 0)
            throw new ArgumentException("语音包大小无效。", nameof(package));
        if (package.Sha256.Length != 64
            || package.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("语音包 SHA-256 校验值无效。", nameof(package));
        if (string.IsNullOrWhiteSpace(package.ArchiveFileName)
            || Path.GetFileName(package.ArchiveFileName) != package.ArchiveFileName)
            throw new ArgumentException("语音包文件名无效。", nameof(package));
    }

    private static void EnsureTrustedDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !IsTrustedHost(uri.Host))
            throw new InvalidDataException("语音包下载地址不是受信任的 HTTPS 地址。");
    }

    private static bool IsTrustedHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static HttpMessageHandler CreateHttpHandler(string? proxyAddress)
    {
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            try
            {
                return TranslationProxy.CreateHandler(
                    TranslationProxy.NormalizeAddress(proxyAddress));
            }
            catch (ArgumentException)
            {
                // A malformed proxy setting must not prevent the app from opening.
            }
        }

        return new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseProxy = true,
        };
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadGate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record InstalledMarker(
        string Id,
        string Sha256,
        DateTimeOffset InstalledAt);
}
