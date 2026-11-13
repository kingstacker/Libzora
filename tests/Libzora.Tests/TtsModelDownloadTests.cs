using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class TtsModelDownloadTests
{
    [Fact]
    public void CatalogUsesPinnedGitHubAssetsAndMatchingSizes()
    {
        Assert.NotEmpty(TtsModelCatalog.Packages);
        foreach (var package in TtsModelCatalog.Packages)
        {
            Assert.StartsWith("https://github.com/", package.DownloadUrl, StringComparison.Ordinal);
            Assert.Equal(64, package.Sha256.Length);
            Assert.True(package.Sha256.All(Uri.IsHexDigit));
            Assert.True(package.ArchiveBytes > 0);
            Assert.EndsWith(".tar.bz2", package.ArchiveFileName, StringComparison.Ordinal);
        }
        Assert.NotNull(TtsModelCatalog.Find(" kokoro-int8 "));
        Assert.Null(TtsModelCatalog.Find("missing"));
    }

    [Fact]
    public void ParsesPackageScopedVoiceIds()
    {
        Assert.True(TtsModelCatalog.TryParseVoiceId("kokoro-int8:42", out var package, out var sid));
        Assert.Equal("kokoro-int8", package);
        Assert.Equal(42, sid);

        Assert.False(TtsModelCatalog.TryParseVoiceId("zh-CN-XiaoxiaoNeural", out _, out _));
        Assert.False(TtsModelCatalog.TryParseVoiceId("kokoro-int8:-1", out _, out _));
        Assert.False(TtsModelCatalog.TryParseVoiceId("missing:0", out _, out _));
    }

    [Fact]
    public async Task DownloadsAndInstallsTheArchiveAtomically()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var archive = CreateArchive();
            var package = CreatePackage(archive);
            var handler = new RangeHandler(archive);
            using var client = new HttpClient(handler);
            using var store = new TtsModelStore(
                new AppPaths(Path.Combine(root, "app")),
                client);
            var progress = new RecordingProgress();

            var directory = await store.DownloadAsync(package, progress);

            Assert.True(store.IsInstalled(package));
            Assert.Equal(
                new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(Path.Combine(directory, "model.onnx")));
            Assert.True(File.Exists(Path.Combine(directory, "tokens.txt")));
            Assert.True(File.Exists(Path.Combine(directory, "voices.bin")));
            Assert.False(Directory.EnumerateFiles(directory, "*.partial").Any());
            Assert.False(File.Exists(
                Path.Combine(store.Root, ".downloads", package.ArchiveFileName)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                Path.Combine(store.Root, ".staging")));
            Assert.Contains(progress.Items, item =>
                item.Phase == TtsModelDownloadPhase.Completed
                && item.BytesReceived == archive.Length);
            Assert.True(store.GetInstalledBytes(package) > 0);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task ResumesAnIncompleteDownloadWithARangeRequest()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var archive = CreateArchive();
            var package = CreatePackage(archive);
            var split = archive.Length / 2;
            var downloadDirectory = Path.Combine(root, "app", "data", "tts-models", ".downloads");
            Directory.CreateDirectory(downloadDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(downloadDirectory, package.ArchiveFileName + ".partial"),
                archive[..split]);

            var handler = new RangeHandler(archive);
            using var client = new HttpClient(handler);
            using var store = new TtsModelStore(
                new AppPaths(Path.Combine(root, "app")),
                client);

            await store.DownloadAsync(package);

            Assert.True(store.IsInstalled(package));
            Assert.Equal(split, handler.LastRangeStart);
            Assert.Equal(package.ArchiveFileName, HandlerFileName(handler.LastRequestUri));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsAWrongHashAndRemovesThePartialFile()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var archive = CreateArchive();
            var package = CreatePackage(archive) with
            {
                Sha256 = new string('0', 64),
            };
            using var client = new HttpClient(new RangeHandler(archive));
            using var store = new TtsModelStore(
                new AppPaths(Path.Combine(root, "app")),
                client);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.DownloadAsync(package));

            Assert.False(store.IsInstalled(package));
            Assert.False(File.Exists(Path.Combine(
                store.Root,
                ".downloads",
                package.ArchiveFileName + ".partial")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task SkipsTraversalEntriesWhileInstallingTheModel()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var archive = CreateArchive(includeTraversalEntry: true);
            var package = CreatePackage(archive);
            using var client = new HttpClient(new RangeHandler(archive));
            using var store = new TtsModelStore(
                new AppPaths(Path.Combine(root, "app")),
                client);

            var directory = await store.DownloadAsync(package);

            Assert.True(store.IsInstalled(package));
            Assert.False(File.Exists(Path.Combine(root, "app", "data", "evil.txt")));
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.True(File.Exists(Path.Combine(directory, "model.onnx")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static string? HandlerFileName(Uri? uri) => uri is null
        ? null
        : Path.GetFileName(uri.AbsolutePath);

    private static TtsModelPackage CreatePackage(byte[] archive)
    {
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        return new TtsModelPackage(
            Id: "test-model",
            EngineId: "sherpa-test",
            DisplayName: "Test model",
            Description: "Test package",
            ArchiveFileName: "test-model.tar.bz2",
            DownloadUrl: "https://github.com/example/test/releases/download/test/test-model.tar.bz2",
            ArchiveBytes: archive.Length,
            Sha256: hash,
            LicenseName: "MIT",
            LicenseUrl: "https://github.com/example/test/blob/main/LICENSE",
            RequiredEntries: ["model.onnx", "tokens.txt", "voices.bin"]);
    }

    private static byte[] CreateArchive(bool includeTraversalEntry = false)
    {
        using var output = new MemoryStream();
        using (var bzip2 = new BZip2OutputStream(output) { IsStreamOwner = false })
        using (var tar = new TarOutputStream(bzip2, Encoding.UTF8) { IsStreamOwner = false })
        {
            if (includeTraversalEntry)
                AddEntry(tar, "../evil.txt", [9, 9, 9]);
            AddEntry(tar, "test-model/model.onnx", [1, 2, 3, 4]);
            AddEntry(tar, "test-model/tokens.txt", [5, 6]);
            AddEntry(tar, "test-model/voices.bin", [7, 8, 9]);
        }
        return output.ToArray();
    }

    private static void AddEntry(TarOutputStream tar, string name, byte[] data)
    {
        var entry = TarEntry.CreateTarEntry(name);
        entry.Size = data.Length;
        tar.PutNextEntry(entry);
        tar.Write(data, 0, data.Length);
        tar.CloseEntry();
    }

    private sealed class RecordingProgress : IProgress<TtsModelDownloadProgress>
    {
        public List<TtsModelDownloadProgress> Items { get; } = [];

        public void Report(TtsModelDownloadProgress value) => Items.Add(value);
    }

    private sealed class RangeHandler(byte[] content) : HttpMessageHandler
    {
        public long LastRangeStart { get; private set; } = -1;
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var start = Math.Clamp(range?.From ?? 0, 0, content.Length);
            LastRangeStart = range?.From ?? -1;
            var slice = content[(int)start..];
            var response = new HttpResponseMessage(
                start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(slice),
            };
            response.Content.Headers.ContentLength = slice.Length;
            if (start > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start,
                    content.Length - 1,
                    content.Length);
            }
            return Task.FromResult(response);
        }
    }
}
