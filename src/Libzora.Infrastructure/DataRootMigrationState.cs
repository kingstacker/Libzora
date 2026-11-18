using System.Collections.Concurrent;

namespace Libzora.Infrastructure;

/// <summary>
/// Tracks in-process data-root migrations. Writers that do not use
/// <see cref="AppDataProcessLock"/> (fonts, dictionaries) consult this state
/// so they fail fast instead of writing into the old root while it is being
/// copied.
/// </summary>
internal static class DataRootMigrationState
{
    private static readonly ConcurrentDictionary<string, byte> ActiveRoots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IDisposable Begin(string root) => Begin([root]);

    public static IDisposable Begin(params string[] roots)
    {
        var normalized = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Normalize)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var added = new List<string>();
        foreach (var root in normalized)
        {
            if (!ActiveRoots.TryAdd(root, 0))
            {
                foreach (var path in added) ActiveRoots.TryRemove(path, out _);
                throw new InvalidOperationException("数据目录迁移已在进行中，请稍后再试。");
            }
            added.Add(root);
        }
        return new Scope(added);
    }

    public static bool IsActiveFor(AppPaths paths) => IsActiveFor(paths.Root);

    public static void ThrowIfActive(AppPaths paths)
    {
        if (IsActiveFor(paths))
            throw new InvalidOperationException(
                "数据目录迁移正在进行，请等待迁移完成后再修改书库或资源。");
        AppRootConfiguration.ThrowIfMigrated(paths.Root);
        AppRootConfiguration.ThrowIfMigrated(paths.InstallationRoot);
    }

    public static bool IsActiveFor(string root)
    {
        var normalized = Normalize(root);
        foreach (var active in ActiveRoots.Keys)
        {
            if (IsSameOrUnder(normalized, active)) return true;
        }
        return false;
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        if (string.Equals(candidate, root, PathComparison)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, PathComparison)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class Scope(IReadOnlyList<string> roots) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var root in roots) ActiveRoots.TryRemove(root, out _);
        }
    }
}
