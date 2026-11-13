using System.Collections.Concurrent;
using SkiaSharp;

namespace Kkindle;

/// <summary>
/// Decodes library covers into a 32x32 RGBA thumbnail and asks
/// <see cref="AmbientPalette"/> for one representative colour. Only files that
/// still exist are read; results are cached by path and file stamp so scrolling
/// does not decode the same cover twice.
/// </summary>
internal static class AmbientCoverSampler
{
    private const int ThumbnailSize = 32;
    private const int MaximumConcurrentLoads = 2;
    private const int MaximumCachedCovers = 256;

    private static readonly SemaphoreSlim Gate = new(MaximumConcurrentLoads, MaximumConcurrentLoads);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.Ordinal);

    internal static async Task<AmbientCoverSelection?> LoadAsync(
        string path, CancellationToken cancellationToken)
    {
        var stamp = Stamp(path);
        if (stamp is null) return null;
        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Selection;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = await Task.Run(() => Decode(path), CancellationToken.None)
                .ConfigureAwait(false);
            if (Cache.Count >= MaximumCachedCovers) Cache.Clear();
            Cache[path] = new CacheEntry(stamp, selection);
            return selection;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void ResetCacheForTests() => Cache.Clear();

    private static string? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static AmbientCoverSelection? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;

            // Ask the codec for the closest supported reduced size first; big
            // covers then never occupy a full-resolution bitmap.
            var scale = ThumbnailSize / (double)Math.Max(codec.Info.Width, codec.Info.Height);
            var reduced = codec.GetScaledDimensions((float)scale);
            var reducedInfo = new SKImageInfo(
                Math.Max(1, reduced.Width),
                Math.Max(1, reduced.Height),
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul);
            using var decoded = SKBitmap.Decode(codec, reducedInfo);
            if (decoded is null) return null;

            var thumbnailInfo = new SKImageInfo(
                ThumbnailSize,
                ThumbnailSize,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul);
            using var thumbnail = decoded.Width == ThumbnailSize && decoded.Height == ThumbnailSize
                ? decoded.Copy()
                : decoded.Resize(thumbnailInfo,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (thumbnail is null) return null;

            var pixels = thumbnail.GetPixelSpan();
            if (pixels.Length < ThumbnailSize * ThumbnailSize * 4) return null;
            return AmbientPalette.ExtractCoverColor(pixels, ThumbnailSize, ThumbnailSize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException
                                               or InvalidOperationException)
        {
            return null;
        }
    }

    private readonly record struct CacheEntry(string Stamp, AmbientCoverSelection? Selection);
}
