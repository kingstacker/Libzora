using System.Security.Cryptography;
using System.Text.Json;
using Libzora.Core;
using SkiaSharp;

namespace Libzora.Infrastructure;

public sealed class FontLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim CatalogInstallGate = new(1, 1);
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    private readonly AppPaths _paths;
    public FontLibraryService(AppPaths paths) => _paths = paths;
    private string ManifestPath => Path.Combine(_paths.Fonts, "manifest.json");

    public async Task<IReadOnlyList<ManagedFont>> ListAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(ManifestPath))
        {
            Publish([]);
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(ManifestPath);
            var items = await JsonSerializer.DeserializeAsync<List<ManagedFont>>(stream, JsonOptions, cancellationToken) ?? [];
            Publish(items);
            return items;
        }
        catch (JsonException)
        {
            Publish([]);
            return [];
        }
    }

    public async Task<ManagedFont> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf" or ".woff" or ".woff2")) throw new InvalidDataException("仅支持 TTF、OTF、WOFF 和 WOFF2 字体。");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("字体文件不存在。", sourcePath);
        _paths.EnsureDirectories();
        var id = Guid.NewGuid().ToString("N");
        var relative = id + extension;
        await using (var input = File.OpenRead(sourcePath))
        await using (var output = File.Create(Path.Combine(_paths.Fonts, relative)))
            await input.CopyToAsync(output, cancellationToken);
        var item = new ManagedFont(id, Path.GetFileNameWithoutExtension(sourcePath), $"LibzoraFont_{id}", relative, DateTimeOffset.UtcNow);
        var manifest = (await ListAsync(cancellationToken)).ToList();
        manifest.Add(item);
        await SaveAsync(manifest, cancellationToken);
        Publish(manifest);
        return item;
    }

    /// <summary>
    /// Downloads (or copies) one optional catalog font, validates it and adds
    /// it to the same manifest used by imported fonts. The catalog id is kept
    /// stable so the reader preference survives app updates.
    /// </summary>
    public async Task<ManagedFont> InstallCatalogAsync(
        ReaderFontCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        await CatalogInstallGate.WaitAsync(cancellationToken);
        try
        {
            return await InstallCatalogCoreAsync(entry, cancellationToken);
        }
        finally
        {
            CatalogInstallGate.Release();
        }
    }

    private async Task<ManagedFont> InstallCatalogCoreAsync(
        ReaderFontCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(entry.FileName).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf"))
        {
            throw new InvalidDataException("阅读字体仅支持 TTF 或 OTF 文件。");
        }

        _paths.EnsureDirectories();
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var installed = manifest.FirstOrDefault(font => string.Equals(font.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (installed is not null && File.Exists(GetAbsolutePath(installed)))
        {
            return installed;
        }

        var relative = "catalog-" + entry.Id + extension;
        var destination = Path.Combine(_paths.Fonts, relative);
        var temporary = destination + ".download";
        try
        {
            var url = ReaderFontCatalog.ResolveUrl(entry);
            if (ReaderFontCatalog.ResolveLocalPath(url) is { } localPath)
            {
                if (!File.Exists(localPath))
                {
                    throw new FileNotFoundException("字体文件尚未部署到本地目录。", localPath);
                }

                await using var input = File.OpenRead(localPath);
                await using var output = File.Create(temporary);
                await input.CopyToAsync(output, cancellationToken);
            }
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                using var response = await DownloadClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (entry.Bytes > 0 && total is > 0 && total != entry.Bytes)
                {
                    throw new InvalidDataException("字体文件大小与目录记录不一致。");
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(temporary);
                await input.CopyToAsync(output, cancellationToken);
            }
            else
            {
                throw new InvalidDataException("字体下载地址无效。");
            }

            Validate(entry, temporary);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        manifest.RemoveAll(font => string.Equals(font.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        var item = new ManagedFont(
            entry.Id,
            entry.DisplayName,
            ReaderFontDefaults.CatalogFamilyPrefix + entry.Id,
            relative,
            DateTimeOffset.UtcNow);
        manifest.Add(item);
        await SaveAsync(manifest, cancellationToken);
        Publish(manifest);
        return item;
    }

    private static void Validate(ReaderFontCatalogEntry entry, string path)
    {
        var info = new FileInfo(path);
        if (entry.Bytes > 0 && info.Length != entry.Bytes)
        {
            throw new InvalidDataException("字体文件大小与目录记录不一致。");
        }

        if (!string.IsNullOrWhiteSpace(entry.Sha256))
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("字体文件校验失败，请重新下载。");
            }
        }

        using var typeface = SKTypeface.FromFile(path, 0);
        if (typeface is null || typeface.UnitsPerEm <= 0 || string.IsNullOrWhiteSpace(typeface.FamilyName))
        {
            throw new InvalidDataException("下载的文件不是可用的字体。");
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var item = manifest.FirstOrDefault(font => font.Id == id);
        if (item is null) return;
        var path = GetAbsolutePath(item);
        if (File.Exists(path)) File.Delete(path);
        manifest.Remove(item);
        await SaveAsync(manifest, cancellationToken);
        Publish(manifest);
    }

    public string GetAbsolutePath(ManagedFont font)
    {
        var root = Path.GetFullPath(_paths.Fonts) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_paths.Fonts, font.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("字体路径无效。");
        return path;
    }

    /// <summary>
    /// Publishes the installed manifest to the reader's in-memory resolver.
    /// Absolute paths are valid only for this launch; iOS changes the app
    /// container path on reinstall, so they must never be persisted.
    /// </summary>
    private void Publish(IReadOnlyList<ManagedFont> items)
    {
        var fonts = new List<(string Family, string Path)>(items.Count);
        foreach (var item in items)
        {
            try
            {
                var path = GetAbsolutePath(item);
                if (File.Exists(path))
                {
                    fonts.Add((item.CssFamily, path));
                }
            }
            catch (InvalidDataException)
            {
                // A damaged manifest row must not hide the remaining fonts.
            }
        }

        ReaderFontRegistry.Replace(fonts);
    }

    private async Task SaveAsync(IReadOnlyList<ManagedFont> items, CancellationToken cancellationToken)
    {
        var temporary = ManifestPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        File.Move(temporary, ManifestPath, true);
    }
}
