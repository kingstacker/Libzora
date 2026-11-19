using SkiaSharp;

namespace Libzora.Layout;

/// <summary>One CJK-capable face exposed by the platform font manager.</summary>
public sealed record SystemFontFace(string Family, string DisplayName, string PostScriptName);

/// <summary>
/// Enumerates the body-text faces the platform already provides. On iOS the
/// app sandbox cannot read <c>/System/Library/Fonts</c>, but CoreText exposes
/// the faces through Skia's font manager; <see cref="TypesetFontLibrary"/>
/// obtains their bytes from <see cref="SKTypeface.OpenStream"/> instead.
/// </summary>
public static class SystemFontCatalog
{
    private static readonly Lazy<IReadOnlyList<SystemFontFace>> Cached = new(Load, isThreadSafe: true);
    private static readonly Lazy<IReadOnlyList<SystemFontFace>> CachedLatin = new(LoadLatin, isThreadSafe: true);

    // The first four simplified characters are enough to reject Korean Hanja
    // and Japanese-only faces while keeping every Chinese body face.
    private const string CjkProbe = "永国书这";
    private const string LatinProbe = "AgQ&’";

    // Curated Latin reading faces commonly available on iOS / macOS / desktop.
    private static readonly string[] PreferredLatinFamilies =
    [
        "New York",
        "Charter",
        "Iowan Old Style",
        "Georgia",
        "Palatino",
        "Baskerville",
        "Hoefler Text",
        "Times New Roman",
        "Avenir Next",
        "Helvetica Neue",
        "SF Pro",
        "Cambria",
        "Garamond",
        "Liberation Serif",
        "DejaVu Serif",
    ];

    public static readonly string[] PreferredMonospaceFamilies =
    [
        "Menlo",
        "SF Mono",
        "Courier New",
        "Consolas",
        "DejaVu Sans Mono",
        "Liberation Mono",
    ];

    // Prefer the canonical family names so an alias such as Heiti SC does not
    // hide PingFang SC when both resolve to the same PostScript face.
    private static readonly string[] PreferredFamilies =
    [
        "PingFang SC",
        "Songti SC",
        "Kaiti SC",
        "Yuanti SC",
        "Heiti SC",
        "STSong",
        "STKaiti",
        "STHeiti",
        "Hiragino Sans GB",
        "BiauKaiTC",
        "PingFang TC",
        "PingFang HK",
        "PingFang MO",
    ];

    public static IReadOnlyList<SystemFontFace> CjkFonts => Cached.Value;

    /// <summary>Curated Latin body-text faces exposed by the platform font manager.</summary>
    public static IReadOnlyList<SystemFontFace> LatinFonts => CachedLatin.Value;

    private static IReadOnlyList<SystemFontFace> LoadLatin()
    {
        var manager = SKFontManager.Default;
        var faces = new List<SystemFontFace>();
        var seenPostScript = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in PreferredLatinFamilies)
        {
            using var typeface = manager.MatchFamily(family);
            if (typeface is null
                || !string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase)
                    && !typeface.FamilyName.Contains(family, StringComparison.OrdinalIgnoreCase)
                    && !family.Contains(typeface.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!typeface.ContainsGlyphs(LatinProbe)
                || !seenPostScript.Add(typeface.PostScriptName))
            {
                continue;
            }

            faces.Add(new SystemFontFace(family, family, typeface.PostScriptName));
        }

        return faces.ToArray();
    }

    private static IReadOnlyList<SystemFontFace> Load()
    {
        var manager = SKFontManager.Default;
        var candidates = new List<string>(PreferredFamilies.Length + manager.FontFamilyCount);
        var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in PreferredFamilies)
        {
            if (seenFamilies.Add(family))
            {
                candidates.Add(family);
            }
        }

        for (var index = 0; index < manager.FontFamilyCount; index++)
        {
            var family = manager.GetFamilyName(index);
            if (!string.IsNullOrWhiteSpace(family) && seenFamilies.Add(family))
            {
                candidates.Add(family);
            }
        }

        var faces = new List<SystemFontFace>();
        var seenPostScript = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in candidates)
        {
            using var typeface = manager.MatchFamily(family);
            if (typeface is null
                || string.Equals(typeface.FamilyName, "KingHwaOldSong", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeface.PostScriptName, "KingHwaOldSong", StringComparison.OrdinalIgnoreCase)
                || !typeface.ContainsGlyphs(CjkProbe))
            {
                continue;
            }

            // Alias families share a PostScript face. Keep the first one, which
            // is why the canonical names above are probed before enumeration.
            if (!seenPostScript.Add(typeface.PostScriptName))
            {
                continue;
            }

            faces.Add(new SystemFontFace(family, DisplayName(family), typeface.PostScriptName));
        }

        return faces
            .OrderBy(face => Rank(face.Family))
            .ThenBy(face => face.Family, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int Rank(string family)
    {
        for (var index = 0; index < PreferredFamilies.Length; index++)
        {
            if (string.Equals(PreferredFamilies[index], family, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return PreferredFamilies.Length;
    }

    public static string DisplayName(string family) => family switch
    {
        "PingFang SC" => "苹方",
        "PingFang TC" => "苹方（繁体）",
        "PingFang HK" => "苹方（香港）",
        "PingFang MO" => "苹方（澳门）",
        "Songti SC" => "宋体",
        "Songti TC" => "宋体（繁体）",
        "Kaiti SC" => "楷体",
        "Kaiti TC" => "楷体（繁体）",
        "Heiti SC" => "黑体",
        "Heiti TC" => "黑体（繁体）",
        "Yuanti SC" => "圆体",
        "Hiragino Sans GB" => "冬青黑体",
        "BiauKaiTC" => "标楷体",
        "STSong" => "华文宋体",
        "STKaiti" => "华文楷体",
        "STHeiti" => "华文黑体",
        _ => family,
    };
}
