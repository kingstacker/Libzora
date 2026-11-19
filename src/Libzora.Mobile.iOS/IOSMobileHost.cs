using Foundation;
using Libzora.ApplicationServices;
using Libzora.Infrastructure;
using Libzora.Mobile;
using Libzora.Platform.Common;

namespace Libzora.iOSHead;

/// <summary>Chooses the iOS implementations of the Core platform contracts.</summary>
internal static class IOSMobileHost
{
    public static MobileServices Create(IMobileTabBar? tabBar = null)
    {
        // Keep the library in the app's private, backed-up data container.
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Libzora");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();

        return new MobileServices(
            paths,
            new IOSSecretProtector(Path.Combine(root, "keys")),
            new BookMetadataService(),
            // Calibre is an external process; conversion is unavailable on iOS
            // until replaced by an in-process converter.
            new UnavailableBookFormatConverter(),
            new IOSFileImportSource(),
            CreateTtsEngine: () => new IOSTtsEngine(Path.Combine(Path.GetTempPath(), "Libzora", "tts")),
            CreateTtsPlayer: () => new IOSTtsAudioPlayer(),
            TabBar: tabBar,
            BackgroundWork: new IOSBackgroundWork(),
            FileShare: new IOSFileShare(),
            PreferredLanguages: () => NSLocale.PreferredLanguages,
            SyncProviders: CreateSyncProviders());
    }

    private static SyncProviderRegistry CreateSyncProviders()
    {
        var providers = SyncProviderRegistry.CreateDefault();
#if LIBZORA_ICLOUD
        providers.Register(new ICloudSyncProviderFactory(new AppleICloudSyncFileSystem()));
#endif
        return providers;
    }
}
