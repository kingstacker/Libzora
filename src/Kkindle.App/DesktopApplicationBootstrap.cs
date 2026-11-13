using System.Diagnostics;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

internal static class DesktopApplicationBootstrap
{
    public static ApplicationModules Create(
        AppPaths? paths = null,
        IBookLibraryService? library = null,
        IBookFormatConverter? formatConverter = null,
        AppServices? services = null,
        AppSettings? startupSettings = null)
    {
        var resolvedPaths = paths ?? services?.Paths
            ?? new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        // The feedback log starts with a session header and records only
        // explicit failure events; see SoftwareLog for the sanitizing rules.
        SoftwareLog.Configure(resolvedPaths,
            ApplicationVersion.GetDisplayVersion(typeof(DesktopApplicationBootstrap).Assembly));
        return new(new ApplicationModuleOptions
        {
            Paths = resolvedPaths,
            SecretProtector = services?.SecretProtector ?? new PreviewSecretProtector(),
            Library = library,
            MetadataService = new BookMetadataService(services?.PdfDocumentFactory),
            FormatConverter = formatConverter ?? new BookFormatConversionService(),
            StartupSettings = startupSettings,
            UpdateInstaller = services?.UpdateInstaller,
            SyncService = services?.SyncService,
            TtsCacheDirectory = services?.TtsCacheDirectory,
            RetrievalLog = message => Debug.WriteLine($"[RAG] {message}")
        });
    }

    public static ApplicationModules CreateForStartup(AppServices? services)
    {
        var paths = services?.Paths
            ?? new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        var dataPaths = AccountRegionPaths.ForRegion(paths, AccountRegionPaths.ReadActiveRegion(paths));
        var settings = new AppSettingsStore(dataPaths).LoadSynchronously();
        return Create(paths: paths, services: services, startupSettings: settings);
    }

    // Platform heads always supply encryption; only the designer/test fallback
    // retains the existing unprotected-settings behavior.
    private sealed class PreviewSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
