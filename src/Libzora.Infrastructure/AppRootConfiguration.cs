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
        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch
        {
            return fallback;
        }
    }

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
