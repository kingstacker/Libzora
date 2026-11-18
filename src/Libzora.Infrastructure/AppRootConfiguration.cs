using System.Text.Json;

namespace Libzora.Infrastructure;

public static class AppRootConfiguration
{
    private const string FileName = "app-root.json";
    private static string ConfigPath(string applicationDirectory) => Path.Combine(applicationDirectory, FileName);

    public static string ResolveRoot(string configurationDirectory, string? fallbackRoot = null)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var fallback = Path.GetFullPath(fallbackRoot ?? configurationRoot);
        // A present but unreadable app-root.json must stop startup. Treating it
        // as "not configured" silently reopened the pre-migration library and
        // let new imports create a second, diverging data root.
        var configured = ReadRootOrThrow(configurationRoot);
        if (configured is null) return fallback;

        // A configured root is only valid while it is reachable. Falling back
        // silently here used to open an empty/old library on an unmounted
        // external disk and then write new books into the wrong root. Refuse
        // to start instead so the user can reconnect the disk or repair
        // app-root.json before any data is written.
        if (!Directory.Exists(configured))
            throw new AppRootUnavailableException(configured);
        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new AppRootUnavailableException(configured, exception);
        }
    }

    /// <summary>
    /// Copies a legacy app-root.json into a new, stable configuration
    /// directory. macOS historically stored the file inside the data root,
    /// which made it impossible to delete the old root after a migration.
    /// The legacy file is left in place so downgrading still works.
    /// </summary>
    public static void EnsureConfigurationMigrated(
        string legacyConfigurationDirectory,
        string configurationDirectory)
    {
        var legacy = Path.GetFullPath(legacyConfigurationDirectory);
        var current = Path.GetFullPath(configurationDirectory);
        if (string.Equals(legacy, current, PathComparison)) return;

        var target = ConfigPath(current);
        if (File.Exists(target)) return;
        var source = ConfigPath(legacy);
        if (!File.Exists(source)) return;

        Directory.CreateDirectory(current);
        var temporary = target + ".tmp";
        File.Copy(source, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Uninstall cleanup must be able to inspect the persisted root without
    // creating a directory that is about to be removed. Normal startup uses
    // ResolveRoot, which retains the historical create-on-read behavior.
    public static string? TryReadRoot(string configurationDirectory)
    {
        try
        {
            return ReadRootOrThrow(configurationDirectory, throwOnFailure: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the persisted root for startup. Missing configuration is normal,
    /// but a file that exists and cannot be parsed (or read) is an error: the
    /// caller must not fall back to another library without the user knowing.
    /// </summary>
    private static string? ReadRootOrThrow(string configurationDirectory, bool throwOnFailure = true)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var path = ConfigPath(configurationRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<RootConfig>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(config?.Root))
                throw new JsonException($"{FileName} 缺少有效的 Root 路径。");
            return Path.GetFullPath(config.Root);
        }
        catch (Exception exception) when (throwOnFailure
            && exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException
                or ArgumentException)
        {
            throw new AppRootConfigurationException(path, exception);
        }
    }

    public static void Save(string configurationDirectory, string root) =>
        Save(configurationDirectory, root, previousRoot: null);

    // The previous root is diagnostic/recovery metadata only. ResolveRoot
    // deliberately keeps using Root, so a failed manual cleanup can never
    // silently reopen an old library.
    public static void Save(string configurationDirectory, string root, string? previousRoot)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var target = Path.GetFullPath(root);
        Directory.CreateDirectory(configurationRoot);
        Directory.CreateDirectory(target);
        var config = new RootConfig { Root = target };
        if (!string.IsNullOrWhiteSpace(previousRoot))
            config.PreviousRoot = Path.GetFullPath(previousRoot);
        var temporary = ConfigPath(configurationRoot) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, ConfigPath(configurationRoot), true);
    }

    /// <summary>
    /// Returns the root recorded by the last successful migration, if any.
    /// Existing app-root.json files without this field remain valid.
    /// </summary>
    public static string? TryReadPreviousRoot(string configurationDirectory)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var path = ConfigPath(configurationRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<RootConfig>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(config?.PreviousRoot)
                ? null
                : Path.GetFullPath(config.PreviousRoot);
        }
        catch
        {
            return null;
        }
    }

    public static string MigrationBackupPath(string root) => Path.Combine(Path.GetFullPath(root), ".libzora-migration.libzora");

    internal const string MigratedMarkerFileName = ".libzora-migrated.json";

    /// <summary>
    /// Leaves a durable marker in the retired root. Long-running helper
    /// processes (the standalone MCP host) may still hold old AppPaths; they
    /// must fail clearly on their next write instead of silently creating a
    /// second library that the restarted desktop app will never see.
    /// </summary>
    internal static void MarkMigrated(string retiredRoot, string activeRoot)
    {
        try
        {
            var root = Path.GetFullPath(retiredRoot);
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, MigratedMarkerFileName);
            var temporary = path + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new MigratedRootConfig
                {
                    Root = Path.GetFullPath(activeRoot),
                    MigratedAt = DateTimeOffset.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // The new root is already published. Failing here would report a
            // migration failure after app-root.json was switched, so the
            // marker is best-effort; the process lock still covers the window
            // until this process restarts.
        }
    }

    internal static void ThrowIfMigrated(string root)
    {
        var path = Path.Combine(Path.GetFullPath(root), MigratedMarkerFileName);
        if (!File.Exists(path)) return;
        var target = string.Empty;
        try
        {
            target = JsonSerializer.Deserialize<MigratedRootConfig>(File.ReadAllText(path))?.Root ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
        }
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(target)
                ? $"此数据目录已迁移，当前进程仍在使用旧路径。请重启 Libzora；如需重新使用旧目录，请先删除 {MigratedMarkerFileName}。"
                : $"此数据目录已迁移到 {target}，当前进程仍在使用旧路径。请重启 Libzora；如需重新使用旧目录，请先删除旧目录中的 {MigratedMarkerFileName}。");
    }

    private sealed class RootConfig
    {
        public string Root { get; set; } = string.Empty;
        public string? PreviousRoot { get; set; }
    }

    private sealed class MigratedRootConfig
    {
        public string Root { get; set; } = string.Empty;
        public DateTimeOffset MigratedAt { get; set; }
    }
}

/// <summary>
/// Raised when app-root.json exists but cannot be read or parsed. Startup must
/// stop rather than silently opening a fallback or pre-migration library.
/// </summary>
public sealed class AppRootConfigurationException : IOException
{
    public AppRootConfigurationException(string path, Exception? innerException = null)
        : base(
            $"无法读取 Libzora 数据目录配置：{path}。\n"
            + "请修复或删除 app-root.json 后重新启动 Libzora。",
            innerException)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Raised when app-root.json points at a directory that is missing or no
/// longer accessible (for example an unmounted external disk). Startup must
/// stop instead of silently falling back to another library.
/// </summary>
public sealed class AppRootUnavailableException : IOException
{
    public AppRootUnavailableException(string root, Exception? innerException = null)
        : base(
            $"无法访问已配置的 Libzora 数据目录：{root}。\n"
            + "请重新连接该磁盘，或修改 app-root.json 后重新启动 Libzora。",
            innerException)
    {
        Root = root;
    }

    public string Root { get; }
}
