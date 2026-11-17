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
        var configured = TryReadRoot(configurationRoot);
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
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var path = ConfigPath(configurationRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<RootConfig>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(config?.Root)
                ? null
                : Path.GetFullPath(config.Root);
        }
        catch
        {
            return null;
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

    private sealed class RootConfig
    {
        public string Root { get; set; } = string.Empty;
        public string? PreviousRoot { get; set; }
    }
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
