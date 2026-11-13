using Kkindle;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed class AmbientCoverSamplerTests
{
    [Fact]
    public async Task SamplesVividAndNeutralCoversAndIgnoresMissingFiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "kkindle-ambient-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var vividPath = Path.Combine(directory, "vivid.png");
            WriteCover(vividPath, new SKColor(30, 90, 200));
            var vivid = await AmbientCoverSampler.LoadAsync(vividPath, CancellationToken.None);
            Assert.NotNull(vivid);
            Assert.True(vivid.Value.IsVivid);
            Assert.Equal(1d, vivid.Value.Color.Strength, 6);
            Assert.True(vivid.Value.Color.Blue > vivid.Value.Color.Red + 0.3,
                $"{vivid.Value.Color}");

            var neutralPath = Path.Combine(directory, "neutral.png");
            WriteCover(neutralPath, new SKColor(140, 140, 140));
            var neutral = await AmbientCoverSampler.LoadAsync(neutralPath, CancellationToken.None);
            Assert.NotNull(neutral);
            Assert.False(neutral.Value.IsVivid);
            Assert.Equal(AmbientPalette.NeutralStrength, neutral.Value.Color.Strength, 6);

            Assert.Null(await AmbientCoverSampler.LoadAsync(
                Path.Combine(directory, "missing.png"), CancellationToken.None));
        }
        finally
        {
            AmbientCoverSampler.ResetCacheForTests();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void WriteCover(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
