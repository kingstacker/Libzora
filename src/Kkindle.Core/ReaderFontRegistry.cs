namespace Kkindle.Core;

/// <summary>
/// In-memory map from a reader font preference key to the current absolute
/// font file path. Installed fonts live inside the app container, whose path
/// changes after an iOS reinstall or update, so the path is never persisted;
/// the manifest is reloaded at startup and this registry is repopulated.
/// </summary>
public static class ReaderFontRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the registry with the currently installed user fonts.</summary>
    public static void Replace(IEnumerable<(string Family, string Path)> fonts)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (family, path) in fonts)
        {
            if (!string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(path))
            {
                paths[family] = path;
            }
        }

        lock (Gate)
        {
            _paths = paths;
        }
    }

    /// <summary>Adds or replaces one installed font without reloading the manifest.</summary>
    public static void Set(string family, string path)
    {
        if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (Gate)
        {
            var paths = new Dictionary<string, string>(_paths, StringComparer.OrdinalIgnoreCase)
            {
                [family] = path,
            };
            _paths = paths;
        }
    }

    public static bool TryResolve(string? family, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        string? resolved;
        lock (Gate)
        {
            _paths.TryGetValue(family.Trim(), out resolved);
        }

        if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved))
        {
            return false;
        }

        path = resolved;
        return true;
    }

    public static bool IsInstalled(string? family) => TryResolve(family, out _);
}
