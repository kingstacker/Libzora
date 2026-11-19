using Libzora.Core;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Paired Latin/monospace face selection and true bold/italic style variants.
/// </summary>
public sealed partial class TypesetFontLibrary
{
    private const string StyleSeparator = "#style:";

    public static bool IsStyledVariantKey(string? fontPath) =>
        fontPath is not null
        && fontPath.Contains(StyleSeparator, StringComparison.OrdinalIgnoreCase);

    internal bool HasRegisteredFace(string fontPath) => ContainsFace(fontPath);

    /// <summary>
    /// Selects an optional paired Latin font (or clears it when <paramref name="fontPath"/> is null).
    /// </summary>
    public bool TrySetLatinFont(string? fontPath)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_faces)
        {
            if (string.IsNullOrWhiteSpace(fontPath))
            {
                LatinFontPath = null;
                return true;
            }

            if (!_faces.ContainsKey(fontPath))
            {
                return false;
            }

            LatinFontPath = fontPath;
            return true;
        }
    }

    /// <summary>
    /// Lazily resolves a platform monospace face for <c>&lt;code&gt;</c> and
    /// <c>&lt;pre&gt;</c> spans. Returns null when no monospace face is exposed.
    /// </summary>
    public string? EnsureMonospaceFont()
    {
        if (_disposed)
        {
            return null;
        }

        lock (_faces)
        {
            if (MonospaceFontPath is not null && _faces.ContainsKey(MonospaceFontPath))
            {
                return MonospaceFontPath;
            }

            if (_monospaceProbed)
            {
                return null;
            }

            _monospaceProbed = true;
        }

        foreach (var family in SystemFontCatalog.PreferredMonospaceFamilies)
        {
            if (TryEnsureSystemFont(family, out var key))
            {
                lock (_faces)
                {
                    if (!_disposed && _faces.ContainsKey(key))
                    {
                        MonospaceFontPath = key;
                        return key;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a true bold / italic / bold-italic face for a registered system
    /// family when the platform provides one. Returns <paramref name="baseFontPath"/>
    /// unchanged when no distinct styled face exists (the painter then falls
    /// back to synthetic bold / oblique skew).
    /// </summary>
    public string ResolveStyledFont(string baseFontPath, bool bold, bool italic)
    {
        if (_disposed || (!bold && !italic) || !IsSystemFontKey(baseFontPath) || IsStyledVariantKey(baseFontPath))
        {
            return baseFontPath;
        }

        var cacheKey = (baseFontPath, bold, italic);
        lock (_faces)
        {
            if (_styledFontCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached is not null && _faces.ContainsKey(cached))
                {
                    return cached;
                }

                if (cached is null)
                {
                    return baseFontPath;
                }
            }
        }

        var family = baseFontPath[ReaderFontDefaults.SystemFamilyPrefix.Length..].Trim();
        var style = (bold, italic) switch
        {
            (true, true) => SKFontStyle.BoldItalic,
            (true, false) => SKFontStyle.Bold,
            (false, true) => SKFontStyle.Italic,
            _ => SKFontStyle.Normal,
        };
        var suffix = (bold, italic) switch
        {
            (true, true) => "bi",
            (true, false) => "b",
            (false, true) => "i",
            _ => "r",
        };
        var variantKey = baseFontPath + StyleSeparator + suffix;

        string? resolved = null;
        try
        {
            if (ContainsFace(variantKey))
            {
                resolved = variantKey;
            }
            else if (CreateSystemEntry(family, style, variantKey) is { } candidate)
            {
                var baseTypeface = TryGetTypeface(baseFontPath);
                var matchesBold = !bold || candidate.Typeface.FontWeight >= 600;
                var matchesItalic = !italic || candidate.Typeface.FontSlant != SKFontStyleSlant.Upright;
                var distinct = baseTypeface is null
                    || !string.Equals(baseTypeface.PostScriptName, candidate.Typeface.PostScriptName, StringComparison.OrdinalIgnoreCase);
                if (distinct && (matchesBold || matchesItalic) && TryAddFace(variantKey, candidate))
                {
                    resolved = variantKey;
                }
                else
                {
                    candidate.Dispose();
                }
            }
        }
        catch (Exception)
        {
            resolved = null;
        }

        lock (_faces)
        {
            if (!_disposed)
            {
                _styledFontCache[cacheKey] = resolved;
            }
        }

        return resolved ?? baseFontPath;
    }

    private SKTypeface? TryGetTypeface(string fontPath)
    {
        lock (_faces)
        {
            return _faces.TryGetValue(fontPath, out var entry) ? entry.Typeface : null;
        }
    }

    private bool IsProtectedSystemKey(string key, string keepMain)
    {
        var baseKey = StripStyleSuffix(key);
        if (string.Equals(baseKey, keepMain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (LatinFontPath is not null
            && string.Equals(baseKey, LatinFontPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (MonospaceFontPath is not null
            && string.Equals(baseKey, MonospaceFontPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string StripStyleSuffix(string key)
    {
        var index = key.IndexOf(StyleSeparator, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? key[..index] : key;
    }

    private void PurgeMissingStyledCacheLocked()
    {
        if (_styledFontCache.Count == 0)
        {
            return;
        }

        List<(string BaseKey, bool Bold, bool Italic)>? stale = null;
        foreach (var (cacheKey, target) in _styledFontCache)
        {
            if (!_faces.ContainsKey(cacheKey.BaseKey)
                || (target is not null && !_faces.ContainsKey(target)))
            {
                stale ??= [];
                stale.Add(cacheKey);
            }
        }

        if (stale is not null)
        {
            foreach (var key in stale)
            {
                _styledFontCache.Remove(key);
            }
        }
    }
}
