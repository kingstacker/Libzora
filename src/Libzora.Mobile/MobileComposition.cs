using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Mobile;

/// <summary>
/// Builds the same service graph the desktop shell uses, from mobile platform
/// adapters. Construction still opens no library, starts no synchronization and
/// downloads no models; pages trigger that work when they become visible.
/// </summary>
public static class MobileComposition
{
    public static ApplicationModules Create(MobileServices services)
    {
        services.Paths.EnsureDirectories();
        SoftwareLog.Configure(services.Paths,
            ApplicationVersion.GetDisplayVersion(typeof(MobileApp).Assembly));
        // Legacy Avalonia-only hosts remain Chinese. The iOS shell configures
        // its shared UI catalog before constructing any presentation views.
        if (services.PreferredLanguages is null) UiText.SetLanguage("zh-CN");
        var dataPaths = AccountRegionPaths.ForRegion(services.Paths, AccountRegionPaths.ReadActiveRegion(services.Paths));
        var settings = new AppSettingsStore(dataPaths).LoadSynchronously();
        return new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = services.Paths,
            SecretProtector = services.SecretProtector,
            MetadataService = services.MetadataService,
            FormatConverter = services.FormatConverter,
            StartupSettings = settings
        });
    }
}
