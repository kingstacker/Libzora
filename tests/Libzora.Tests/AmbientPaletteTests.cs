using Libzora;

namespace Libzora.Tests;

public sealed class AmbientPaletteTests
{
    private const int Size = 32;

    [Fact]
    public void SaturatedCoverProducesAVividLiftedColour()
    {
        var selection = AmbientPalette.ExtractCoverColor(
            Fill((255, 0, 0), 255), Size, Size);

        Assert.NotNull(selection);
        Assert.True(selection.Value.IsVivid);
        var color = selection.Value.Color;
        Assert.Equal(1d, color.Strength, 6);
        Assert.True(color.Red > 0.9, $"red {color.Red}");
        Assert.True(color.Green < 0.15, $"green {color.Green}");
        Assert.True(color.Blue < 0.15, $"blue {color.Blue}");
    }

    [Fact]
    public void TinyBrightAccentDoesNotOutweighTheCoverArtwork()
    {
        var pixels = Fill((250, 250, 250), 255);
        // Two percent red is below the six-percent vivid-area threshold, so
        // the neutral paper dominates and the wash stays almost invisible.
        for (var index = 0; index < (int)(Size * Size * 0.02); index++)
            Set(pixels, index, (255, 0, 0));

        var selection = AmbientPalette.ExtractCoverColor(pixels, Size, Size);

        Assert.NotNull(selection);
        Assert.False(selection.Value.IsVivid);
        var color = selection.Value.Color;
        Assert.Equal(AmbientPalette.NeutralStrength, color.Strength, 6);
        Assert.InRange(Math.Abs(color.Red - color.Green), 0, 0.01);
        Assert.InRange(Math.Abs(color.Green - color.Blue), 0, 0.01);
        Assert.InRange(color.Red, 0.7, 0.8);
    }

    [Fact]
    public void TransparentPixelsAreIgnored()
    {
        Assert.Null(AmbientPalette.ExtractCoverColor(
            Fill((255, 0, 0), 100), Size, Size));
    }

    [Fact]
    public void RegionalColoursFollowTheNearestVisibleCovers()
    {
        var red = new AmbientColor(1, 0, 0);
        var blue = new AmbientColor(0, 0, 1);
        var colors = AmbientPalette.RegionalColors(
        [
            new AmbientSample(red, 0, 0, 1.0),
            new AmbientSample(blue, 2, 2, 0.5)
        ]);

        Assert.Equal(AmbientPalette.FieldCount, colors.Length);
        Assert.Equal(red, colors[0]);
        Assert.Equal(red, colors[2]);
        Assert.Equal(red, colors[4]);
        Assert.Equal(red, colors[6]);
        Assert.Equal(blue, colors[5]);
        Assert.Equal(blue, colors[8]);
    }

    [Fact]
    public void FallbackSeedsCycleAcrossAllNineFields()
    {
        Assert.Empty(AmbientPalette.ExpandFallback([]));

        var seed = new AmbientColor(0.2, 0.3, 0.4, AmbientPalette.NeutralStrength);
        var single = AmbientPalette.ExpandFallback([seed]);
        Assert.Equal(AmbientPalette.FieldCount, single.Length);
        Assert.All(single, color => Assert.Equal(seed, color));

        AmbientColor[] seeds =
        [
            new(0.1, 0.2, 0.3),
            new(0.4, 0.5, 0.6),
            new(0.7, 0.8, 0.9)
        ];
        var expanded = AmbientPalette.ExpandFallback(seeds);
        Assert.Equal(AmbientPalette.FieldCount, expanded.Length);
        for (var index = 0; index < expanded.Length; index++)
            Assert.Equal(seeds[index % seeds.Length], expanded[index]);
    }

    private static byte[] Fill((byte Red, byte Green, byte Blue) color, byte alpha)
    {
        var pixels = new byte[Size * Size * 4];
        for (var index = 0; index < Size * Size; index++)
            Set(pixels, index, color, alpha);
        return pixels;
    }

    private static void Set(
        byte[] pixels, int index, (byte Red, byte Green, byte Blue) color, byte alpha = 255)
    {
        var offset = index * 4;
        pixels[offset] = color.Red;
        pixels[offset + 1] = color.Green;
        pixels[offset + 2] = color.Blue;
        pixels[offset + 3] = alpha;
    }
}
