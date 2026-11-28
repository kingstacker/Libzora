using Libzora.Platform.MacOS;
using Xunit;

namespace Libzora.Platform.Common.Tests;

public sealed class MacOSAppUpdateInstallerTests
{
    [Fact]
    public void HelperScriptSupportsInstallerBundleAndLegacyDmgLayouts()
    {
        Assert.Contains("$mountpoint/ .app/Contents/Helpers/Libzora.app", MacOSAppUpdateInstaller.HelperScript);
        Assert.Contains("$mountpoint/Install Libzora.app/Contents/Helpers/Libzora.app", MacOSAppUpdateInstaller.HelperScript);
        Assert.Contains("$mountpoint/Libzora.app", MacOSAppUpdateInstaller.HelperScript);
        Assert.Contains("*.app/Contents/Helpers/Libzora.app", MacOSAppUpdateInstaller.HelperScript);
        Assert.Contains("Contents/MacOS/mcp/Libzora.McpServer*", MacOSAppUpdateInstaller.HelperScript);
    }

    [Fact]
    public void FindBundlePathResolvesMacOsAppBundleAndIgnoresLooseDirectories()
    {
        var bundle = MacOSAppUpdateInstaller.FindBundlePath("/Applications/Libzora.app/Contents/MacOS");
        Assert.Equal("/Applications/Libzora.app", bundle);

        var universalBundle = MacOSAppUpdateInstaller.FindBundlePath("/Applications/Libzora.app/Contents/MacOS/arm64");
        Assert.Equal("/Applications/Libzora.app", universalBundle);

        Assert.Null(MacOSAppUpdateInstaller.FindBundlePath("/tmp/Libzora/bin/Debug/net10.0"));
    }

    [Fact]
    public void MissingPackageFailsBeforeLaunchingAnything()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new MacOSAppUpdateInstaller().LaunchInstaller(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dmg")));
    }
}
