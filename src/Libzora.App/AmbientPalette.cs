namespace Libzora;

/// <summary>
/// A cover-derived RGB colour. Strength lets neutral covers stay almost
/// transparent without changing the shared wash implementation.
/// </summary>
internal readonly record struct AmbientColor(double Red, double Green, double Blue, double Strength = 1d);

/// <summary>One extracted cover colour plus whether it came from vivid artwork.</summary>
internal readonly record struct AmbientCoverSelection(AmbientColor Color, bool IsVivid);

/// <summary>A cover colour anchored to one of the nine fixed wash fields.</summary>
internal readonly record struct AmbientSample(AmbientColor Color, int Column, int Row, double Visibility);

/// <summary>
/// Desktop port of NativeAmbientPalette (NativeAmbientBackground.swift). The
/// algorithm is intentionally identical: bucket a 32x32 cover thumbnail by hue,
/// let area dominate the score, lift the winner into a pastel range, and fall
/// back to a faint neutral colour for black-and-white artwork.
/// </summary>
internal static class AmbientPalette
{
    internal const int FieldCount = 9;
    internal const double NeutralStrength = 0.12;

    private const int MinimumAlpha = 200;
    private const int HueBucketCount = 24;
    private const double MinimumVividSaturation = 0.22;
    private const double MinimumVividBrightness = 0.15;
    private const double MinimumVividArea = 0.06;

    internal static AmbientCoverSelection? ExtractCoverColor(
        ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var pixelCount = Math.Min(width * height, rgba.Length / 4);
        if (pixelCount <= 0) return null;

        var buckets = new Dictionary<int, Bucket>(HueBucketCount + 8);
        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            if (rgba[offset + 3] <= MinimumAlpha) continue;
            var red = rgba[offset];
            var green = rgba[offset + 1];
            var blue = rgba[offset + 2];
            var high = Math.Max(red, Math.Max(green, blue));
            var low = Math.Min(red, Math.Min(green, blue));
            var saturation = high == 0 ? 0d : (high - low) / (double)high;
            // Group shades of the same hue so shadows and highlights do not
            // split a dominant colour into many small RGB buckets.
            var key = saturation >= MinimumVividSaturation && high >= 38
                ? (int)(Hue(red / 255d, green / 255d, blue / 255d) * HueBucketCount) % HueBucketCount
                : 100 + high / 32;
            var bucket = buckets.TryGetValue(key, out var existing) ? existing : default;
            buckets[key] = new Bucket(
                bucket.Count + 1,
                bucket.Red + red,
                bucket.Green + green,
                bucket.Blue + blue);
        }
        if (buckets.Count == 0) return null;

        var candidates = new Candidate[buckets.Count];
        var candidateCount = 0;
        var visiblePixels = 0;
        foreach (var bucket in buckets.Values)
        {
            var divisor = bucket.Count * 255d;
            var red = bucket.Red / divisor;
            var green = bucket.Green / divisor;
            var blue = bucket.Blue / divisor;
            var high = Math.Max(red, Math.Max(green, blue));
            var low = Math.Min(red, Math.Min(green, blue));
            var saturation = high <= 0 ? 0 : (high - low) / high;
            candidates[candidateCount++] = new Candidate(
                red,
                green,
                blue,
                Hue(red, green, blue),
                saturation,
                high,
                bucket.Count);
            visiblePixels += bucket.Count;
        }

        // Area dominates: a tiny bright title must not outweigh the artwork.
        Candidate? vivid = null;
        foreach (var candidate in candidates.AsSpan(0, candidateCount))
        {
            if (candidate.Saturation < MinimumVividSaturation) continue;
            if (candidate.Brightness < MinimumVividBrightness) continue;
            if (candidate.Count < visiblePixels * MinimumVividArea) continue;
            if (vivid is null || candidate.Score > vivid.Value.Score) vivid = candidate;
        }
        if (vivid is { } winner)
            return new AmbientCoverSelection(winner.VividColor(), true);

        // Neutral covers are a fallback, so ink and paper never muddy a
        // colourful wash.
        Candidate? quiet = null;
        foreach (var candidate in candidates.AsSpan(0, candidateCount))
        {
            if (quiet is null || candidate.Score > quiet.Value.Score) quiet = candidate;
        }
        return quiet is { } neutral
            ? new AmbientCoverSelection(neutral.MutedColor(), false)
            : null;
    }

    /// <summary>
    /// Each fixed field follows nearby covers. Colours never carry scroll
    /// coordinates, so the wash stays anchored while the shelf scrolls.
    /// </summary>
    internal static AmbientColor[] RegionalColors(IReadOnlyList<AmbientSample> samples)
    {
        if (samples.Count == 0) return [];
        var colors = new AmbientColor[FieldCount];
        for (var index = 0; index < FieldCount; index++)
        {
            var column = index % 3;
            var row = index / 3;
            var best = samples[0];
            var bestScore = double.NegativeInfinity;
            foreach (var sample in samples)
            {
                var dx = sample.Column - column;
                var dy = sample.Row - row;
                var score = sample.Visibility / (0.2 + dx * dx + dy * dy);
                if (score <= bestScore) continue;
                bestScore = score;
                best = sample;
            }
            colors[index] = best.Color;
        }
        return colors;
    }

    /// <summary>Cycles one to four fallback colours across all nine fields.</summary>
    internal static AmbientColor[] ExpandFallback(IReadOnlyList<AmbientColor> seeds)
    {
        if (seeds.Count == 0) return [];
        var colors = new AmbientColor[FieldCount];
        for (var index = 0; index < FieldCount; index++)
            colors[index] = seeds[index % seeds.Count];
        return colors;
    }

    private static double Hue(double red, double green, double blue)
    {
        var high = Math.Max(red, Math.Max(green, blue));
        var low = Math.Min(red, Math.Min(green, blue));
        var delta = high - low;
        if (delta <= 0) return 0;
        var value = high == red
            ? (green - blue) / delta % 6
            : high == green
                ? (blue - red) / delta + 2
                : (red - green) / delta + 4;
        value /= 6;
        return value < 0 ? value + 1 : value;
    }

    private static (double Red, double Green, double Blue) Hsv(
        double hue, double saturation, double brightness)
    {
        var position = (hue - Math.Floor(hue)) * 6;
        var sector = (int)Math.Floor(position) % 6;
        var fraction = position - Math.Floor(position);
        var p = brightness * (1 - saturation);
        var q = brightness * (1 - saturation * fraction);
        var t = brightness * (1 - saturation * (1 - fraction));
        return sector switch
        {
            0 => (brightness, t, p),
            1 => (q, brightness, p),
            2 => (p, brightness, t),
            3 => (p, q, brightness),
            4 => (t, p, brightness),
            _ => (brightness, p, q)
        };
    }

    private readonly record struct Bucket(int Count, long Red, long Green, long Blue);

    private readonly record struct Candidate(
        double Red,
        double Green,
        double Blue,
        double Hue,
        double Saturation,
        double Brightness,
        int Count)
    {
        internal double Score =>
            Count * (0.8 + Saturation * 0.2) * (0.8 + Brightness * 0.2);

        /// <summary>
        /// Covers are often printed in shadow. Lift the sample into a pastel
        /// range so the wash reads as colour instead of turning muddy.
        /// </summary>
        internal AmbientColor VividColor()
        {
            var saturation = Math.Clamp(Saturation * 1.1, 0.55, 0.95);
            var brightness = Math.Clamp(Brightness * 1.15 + 0.12, 0.88, 1);
            var rgb = Hsv(Hue, saturation, brightness);
            return new AmbientColor(rgb.Red, rgb.Green, rgb.Blue);
        }

        /// <summary>
        /// Used only when the whole shelf is black and white, where forcing
        /// saturation would invent a hue the books don't have.
        /// </summary>
        internal AmbientColor MutedColor()
        {
            var peak = Math.Max(Red, Math.Max(Green, Blue));
            var scale = peak <= 0 ? 1 : Math.Min(2.5, 0.75 / peak);
            return new AmbientColor(
                Math.Min(1, Red * scale),
                Math.Min(1, Green * scale),
                Math.Min(1, Blue * scale),
                NeutralStrength);
        }
    }
}
