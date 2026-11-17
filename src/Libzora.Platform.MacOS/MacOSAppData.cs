using Libzora.Infrastructure;

namespace Libzora.Platform.MacOS;

public static class MacOSAppData
{
    // app-root.json and the single-instance lock must survive deletion of the
    // old data root after a migration. Keeping them inside ResolveRoot() meant
    // the migration prompt's "清理旧目录" step silently reverted the switch.
    private const string ConfigurationDirectoryName = "Libzora.Settings";

    public static string ResolveRoot()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS application paths can only be resolved on macOS.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("The current macOS user has no home directory.");
        return Path.Combine(home, "Library", "Application Support", "Libzora");
    }

    /// <summary>
    /// Stable per-user configuration directory. It is intentionally outside
    /// the data root so app-root.json is not removed with the old library.
    /// </summary>
    public static string ResolveConfigurationDirectory()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS application paths can only be resolved on macOS.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("The current macOS user has no home directory.");
        return Path.Combine(home, "Library", "Application Support", ConfigurationDirectoryName);
    }

    /// <summary>
    /// Creates the stable configuration directory and imports a legacy
    /// app-root.json that older builds stored inside the data root.
    /// </summary>
    public static string PrepareConfigurationDirectory()
    {
        var configurationDirectory = ResolveConfigurationDirectory();
        Directory.CreateDirectory(configurationDirectory);
        AppRootConfiguration.EnsureConfigurationMigrated(ResolveRoot(), configurationDirectory);
        return configurationDirectory;
    }
}
