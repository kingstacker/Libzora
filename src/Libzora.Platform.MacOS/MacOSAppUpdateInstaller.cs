using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Libzora.Core;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Installs a downloaded <c>.dmg</c> update without leaving Libzora.
/// The app is distributed inside an installer bundle
/// (<c>Install Libzora.app/Contents/Helpers/Libzora.app</c>, or at the disk
/// image root <c>Libzora.app</c> for older packages), so the update is applied
/// by a detached helper script: it waits for this process to exit, mounts the
/// image, copies the bundle out, swaps it in place and reopens the app.
/// Installation is only offered when the bundle directory is writable by the
/// current user; otherwise the release page remains the fallback.
/// </summary>
public sealed class MacOSAppUpdateInstaller : IAppUpdateInstaller
{
    private const string BundleName = "Libzora.app";

    private static readonly Lazy<string?> BundlePathValue = new(() => FindBundlePath(AppContext.BaseDirectory));

    public static string? BundlePath => BundlePathValue.Value;

    public bool CanInstall
    {
        get
        {
            var bundle = BundlePath;
            if (bundle is null || !Directory.Exists(bundle)) return false;
            // App Translocation runs the bundle from a read-only random path.
            if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal)) return false;
            var parent = Path.GetDirectoryName(bundle);
            return parent is not null && Directory.Exists(parent) && IsWritableDirectory(parent);
        }
    }

    public string UnavailableReason =>
        "当前不是从可写的 Libzora.app 应用包运行，暂不支持应用内安装。";

    public string GetPackageAssetName(string version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("版本号不能用于生成安装包文件名。", nameof(version));
        return $"Libzora-{version}-{RuntimeIdentifier}.dmg";
    }

    public void LaunchInstaller(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("找不到已下载的 Libzora 更新包。", fullPath);
        if (!Path.GetFileName(fullPath).EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Libzora 更新包格式不正确。");
        var bundle = BundlePath;
        if (!CanInstall || bundle is null) throw new InvalidOperationException(UnavailableReason);

        // The helper lives outside the bundle so the bundle can be replaced
        // while the script still runs. Every path is passed as an argument;
        // the script itself is a constant and never interpolates them.
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"libzora-update-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath, HelperScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var info = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(scriptPath);
        info.ArgumentList.Add(fullPath);
        info.ArgumentList.Add(bundle);
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(fullPath + ".install.log");

        using var process = Process.Start(info);
        if (process is null) throw new InvalidOperationException("无法启动 Libzora 更新程序。");
    }

    /// <summary>
    /// Resolves the enclosing <c>.app</c> bundle from the executable directory
    /// (<c>Libzora.app/Contents/MacOS</c>). Returns null for development builds.
    /// </summary>
    internal static string? FindBundlePath(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var directory = Path.GetFullPath(baseDirectory);
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(directory); depth++)
        {
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
            if (string.Equals(name, BundleName, StringComparison.OrdinalIgnoreCase))
                return directory.TrimEnd(Path.DirectorySeparatorChar);
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    private static string RuntimeIdentifier =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    private static bool IsWritableDirectory(string path)
    {
        var probe = Path.Combine(path, $".libzora-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal const string HelperScript = """
        #!/bin/sh
        # Libzora updater: disk image, target bundle, parent pid, log.
        image=$1
        target=$2
        parent_pid=$3
        log=$4
        exec >>"$log" 2>&1
        echo "[$(date '+%Y-%m-%d %H:%M:%S')] updating $target from $image"
        # The app shuts down immediately after launching this script. Give up
        # (without touching the installed bundle) if it is still running after
        # two minutes, e.g. because a quit prompt was cancelled.
        waited=0
        while kill -0 "$parent_pid" 2>/dev/null; do
          if [ "$waited" -ge 600 ]; then
            echo "Libzora did not exit within 120 seconds; update cancelled"
            exit 1
          fi
          sleep 0.2
          waited=$((waited + 1))
        done
        # Stage next to the installed bundle so the final swap is a same-volume
        # rename. A cross-volume mv copies file by file and can fail halfway.
        stage=$(mktemp -d "$(dirname "$target")/.libzora-update.XXXXXX" 2>/dev/null) \
          || stage=$(mktemp -d "${TMPDIR:-/tmp}/libzora-update.XXXXXX") || exit 1
        mountpoint="$stage/dmg"
        cleanup() {
          /usr/bin/hdiutil detach "$mountpoint" -force -quiet >/dev/null 2>&1 || true
          /bin/rm -rf "$stage"
        }
        trap cleanup EXIT

        # Mount read-only at a private mount point. -nobrowse and -noautoopen
        # keep Finder and the user's sidebar out of the update.
        /bin/mkdir -p "$mountpoint"
        if ! /usr/bin/hdiutil attach -readonly -nobrowse -noautoopen \
          -mountpoint "$mountpoint" "$image" >/dev/null; then
          echo "failed to mount $image"
          exit 1
        fi
        if [ -d "$mountpoint/ .app/Contents/Helpers/Libzora.app" ]; then
          source_app="$mountpoint/ .app/Contents/Helpers/Libzora.app"
        elif [ -d "$mountpoint/Install Libzora.app/Contents/Helpers/Libzora.app" ]; then
          source_app="$mountpoint/Install Libzora.app/Contents/Helpers/Libzora.app"
        elif [ -d "$mountpoint/Libzora.app" ]; then
          source_app="$mountpoint/Libzora.app"
        else
          source_app=""
          for candidate in "$mountpoint"/*.app/Contents/Helpers/Libzora.app; do
            if [ -d "$candidate" ]; then
              source_app="$candidate"
              break
            fi
          done
        fi
        if [ -z "$source_app" ] \
          || [ ! -d "$source_app" ] \
          || [ ! -x "$source_app/Contents/MacOS/Libzora" ]; then
          echo "the disk image does not contain a runnable Libzora.app"
          exit 1
        fi
        # ditto preserves the bundle's extended attributes and signature.
        if ! /usr/bin/ditto "$source_app" "$stage/Libzora.app"; then
          echo "failed to copy the update bundle"
          exit 1
        fi
        /usr/bin/hdiutil detach "$mountpoint" -quiet >/dev/null 2>&1 \
          || /usr/bin/hdiutil detach "$mountpoint" -force -quiet >/dev/null 2>&1 || true

        # Reject an update whose bundle signature is broken before replacing
        # the working copy. Ad-hoc signed builds still pass this check.
        if ! /usr/bin/codesign --verify --deep --strict "$stage/Libzora.app" >/dev/null 2>&1; then
          echo "the downloaded bundle has an invalid code signature"
          exit 1
        fi

        # External MCP clients keep the bundled server running; stop it before
        # the bundle directory is replaced. Match the bundle path literally:
        # pkill -f treats characters such as "." or "+" in the path as a regex.
        /bin/ps -axo pid=,command= | while read -r pid command; do
          case "$command" in
            "$target"/Contents/MacOS/mcp/Libzora.McpServer*|"$target"/Contents/MacOS/*/mcp/Libzora.McpServer*|"$target"/Contents/mcp/Libzora.McpServer*|"$target"/Contents/*/mcp/Libzora.McpServer*)
              kill "$pid" 2>/dev/null || true
              ;;
          esac
        done
        sleep 0.5

        backup="$target.libzora-old"
        rm -rf "$backup"
        if ! mv "$target" "$backup"; then
          echo "failed to move the current bundle aside"
          exit 1
        fi
        if ! mv "$stage/Libzora.app" "$target"; then
          echo "failed to install the new bundle, restoring the previous version"
          # A partial copy may exist; without removing it, mv would move the
          # backup *into* it and leave neither version runnable.
          /bin/rm -rf "$target"
          mv "$backup" "$target"
          exit 1
        fi
        if ! /usr/bin/codesign --verify --deep --strict "$target" >/dev/null 2>&1; then
          echo "the installed bundle failed signature verification, restoring the previous version"
          /bin/rm -rf "$target"
          mv "$backup" "$target"
          exit 1
        fi
        /bin/chmod -R u+rwX,go+rX "$target" 2>/dev/null
        /usr/bin/xattr -dr com.apple.quarantine "$target" >/dev/null 2>&1
        echo "installed $target, relaunching"
        if ! /usr/bin/open "$target"; then
          echo "failed to relaunch $target, restoring the previous version"
          /bin/rm -rf "$target"
          mv "$backup" "$target"
          /usr/bin/open "$target" >/dev/null 2>&1
          exit 1
        fi
        /bin/rm -rf "$backup"
        /bin/rm -f "$0"
        exit 0
        """;
}
