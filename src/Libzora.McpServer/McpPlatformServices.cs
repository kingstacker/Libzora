using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.Common;

namespace Libzora.McpServer;

internal static class McpPlatformServices
{
    public static IKindleDeviceService? CreateDeviceService(
        AppPaths paths,
        IMetadataService metadata)
    {
#if WINDOWS
        return new Libzora.Platform.Windows.KindleDeviceService(paths, metadata);
#else
        if (!OperatingSystem.IsMacOS()) return null;
        // macOS needs the same dual transport as the desktop head: mounted
        // storage for older Kindles and libmtp for newer MTP-only models.
        var massStorage = new MassStorageKindleDeviceService(
            paths,
            metadata,
            eject: Libzora.Platform.MacOS.MacOSKindleEjector.EjectAsync);
        var mtp = new Libzora.Platform.MacOS.MtpKindleDeviceService(paths, metadata);
        return new Libzora.Platform.MacOS.MacOSKindleDeviceService(massStorage, mtp);
#endif
    }
}
