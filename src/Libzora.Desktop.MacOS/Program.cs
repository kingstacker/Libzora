using System.Diagnostics;
using Avalonia;
using Libzora;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.Common;
using Libzora.Platform.MacOS;
using Libzora.Tts.Sherpa;

namespace Libzora.Desktop.MacOS;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This Libzora head is for macOS.");

        // Mirror the Windows head: any unhandled exception is written next to
        // the executable and in the data logs directory before the process
        // dies, so startup failures on a user's Mac are diagnosable.
        try
        {
            var startupPaths = new AppPaths(AppRootConfiguration.ResolveRoot(
                MacOSAppData.PrepareConfigurationDirectory(),
                MacOSAppData.ResolveRoot()));
            SoftwareLog.Configure(startupPaths, ApplicationVersion.GetDisplayVersion(typeof(Program).Assembly));
        }
        catch
        {
            // The optional feedback log must never prevent startup.
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // A second launch only asks the running instance to come forward.
        // The DEBUG validation processes stay isolated so they can run next to
        // the user's copy, matching the Windows head.
#if DEBUG
        var isolateValidation = string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_AI_PREVIEW"),
                "1",
                StringComparison.Ordinal)
            || string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE"),
                "1",
                StringComparison.Ordinal)
            || string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_ANIMATION_PROBE"),
                "1",
                StringComparison.Ordinal);
#else
        const bool isolateValidation = false;
#endif
        FileStream? instanceLock = null;
        MacOSInstanceActivationServer? activationServer = null;
        if (!isolateValidation)
        {
            if (!MacOSSingleInstanceGuard.TryAcquire(out instanceLock))
            {
                // Files passed on the command line belong to the running
                // instance, so forward them with the activation request.
                // Resolve relative paths here: the primary instance has its
                // own working directory and would otherwise drop them.
                var requestedFiles = args
                    .Select(ResolveExistingFileArgument)
                    .Where(path => path is not null)
                    .Select(path => path!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                // The primary instance takes the lock a moment before it binds
                // the socket, so retry briefly before falling back.
                var activated = false;
                for (var attempt = 0; attempt < 20 && !activated; attempt++)
                {
                    activated = MacOSInstanceActivationServer.RequestActivate(requestedFiles);
                    if (!activated) Thread.Sleep(100);
                }
                if (!activated)
                    ActivateWithLaunchServices(requestedFiles);
                instanceLock?.Dispose();
                return;
            }
            activationServer = MacOSInstanceActivationServer.TryStart();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            WriteCrashLog("StartupFailure", exception);
            StartupErrorReporter.Show(
                "Libzora 无法启动",
                exception is AppRootUnavailableException or AppRootConfigurationException
                    ? exception.Message
                    : $"启动时发生错误：{exception.Message}\n\n详细信息已写入 crash.log。");
        }
        finally
        {
            activationServer?.Dispose();
            instanceLock?.Dispose();
        }

        // Start the replacement only after releasing the instance lock, so it
        // can become the primary instance instead of activating this one.
        AppRestartRequest.LaunchIfRequested();
    }

    private static string? ResolveExistingFileArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return null;
        try
        {
            var path = Path.GetFullPath(argument);
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new Libzora.App(BuildServices()) { Name = "Libzora" })
            .UsePlatformDetect()
            // Avalonia's Metal pipeline fails to build on the NVIDIA driver used
            // by this Hackintosh ("The GPU was lost"), which leaves the window
            // blank. Real Macs keep GPU rendering; LIBZORA_MACOS_RENDERER can
            // force metal, opengl or software explicitly.
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = ResolveRenderingModes()
            })
            .WithInterFont()
            .LogToTrace()
            // Activation requests from a second launch may arrive before the
            // UI thread exists; they wait for this signal before dispatching.
            .AfterSetup(_ => MacOSInstanceActivationServer.SignalUiReady());

    private static IReadOnlyList<AvaloniaNativeRenderingMode> ResolveRenderingModes()
    {
        switch (Environment.GetEnvironmentVariable("LIBZORA_MACOS_RENDERER")?.Trim().ToLowerInvariant())
        {
            case "metal":
                return [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software];
            case "opengl" or "gl":
                return [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software];
            case "software":
                return [AvaloniaNativeRenderingMode.Software];
        }

        var deviceName = MacOSGraphicsInfo.TryGetMetalDeviceName();
        if (deviceName is not null
            && deviceName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return [AvaloniaNativeRenderingMode.Software];
        }

        return
        [
            AvaloniaNativeRenderingMode.Metal,
            AvaloniaNativeRenderingMode.OpenGl,
            AvaloniaNativeRenderingMode.Software
        ];
    }

    private static AppServices BuildServices()
    {
        var configurationDirectory = MacOSAppData.PrepareConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, MacOSAppData.ResolveRoot()));
        var metadata = new BookMetadataService();
        var massStorage = new MassStorageKindleDeviceService(
            paths,
            metadata,
            eject: MacOSKindleEjector.EjectAsync);
        // Newer Kindles only expose MTP, which macOS cannot mount; libmtp
        // provides the same access path calibre uses on this platform.
        var mtp = new MtpKindleDeviceService(paths, metadata);
        // Read-aloud uses installed system voices plus any explicitly
        // downloaded offline voice packs; nothing leaves the device.
        var ttsEngine = new LocalTtsEngine(paths);
        var ttsPlayer = new MacOSTtsAudioPlayer();
        return new AppServices(
            SecretProtector: new MacOSSecretProtector(),
            // IOKit USB notifications refresh the device list on plug/unplug.
            CreateDeviceChangeNotifier: _ =>
            {
                var notifier = new MacOSDeviceChangeNotifier();
                notifier.DeviceChanged += (_, _) => mtp.InvalidateDetectionCache();
                return notifier;
            },
            KindleDeviceService: new MacOSKindleDeviceService(massStorage, mtp),
            UpdateInstaller: new MacOSAppUpdateInstaller(),
            TtsEngine: ttsEngine,
            TtsAudioPlayer: ttsPlayer,
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }

    /// <summary>
    /// Fallback when the activation socket is unreachable. LaunchServices
    /// forwards the files to the running instance as an open-documents event,
    /// so they are not lost.
    /// </summary>
    private static void ActivateWithLaunchServices(IReadOnlyList<string> files)
    {
        if (MacOSAppUpdateInstaller.BundlePath is not { } bundle) return;
        try
        {
            var info = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-a");
            info.ArgumentList.Add(bundle);
            foreach (var file in files) info.ArgumentList.Add(Path.GetFullPath(file));
            using var process = Process.Start(info);
        }
        catch
        {
            // Activation is best effort.
        }
    }

    private static void WriteCrashLog(string kind, Exception? exception)
    {
        SoftwareLog.ErrorWithoutMessage("app.unhandled.process", exception, ("kind", kind));
        var payload = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        var targets = new List<string>();
        // Never write inside Libzora.app: an extra file in the sealed
        // Contents/MacOS directory breaks the bundle's code signature. Loose
        // Debug executables keep the log next to the binary.
        try
        {
            if (!AppContext.BaseDirectory.Contains(".app/Contents/", StringComparison.Ordinal))
                targets.Add(Path.Combine(AppContext.BaseDirectory, "libzora-crash.log"));
        }
        catch
        {
        }
        try
        {
            var paths = new AppPaths(AppRootConfiguration.ResolveRoot(
                MacOSAppData.PrepareConfigurationDirectory(),
                MacOSAppData.ResolveRoot()));
            targets.Add(Path.Combine(paths.Logs, "crash.log"));
        }
        catch
        {
        }

        foreach (var target in targets.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(target, payload, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // Logging must never mask the original failure.
            }
        }
    }
}
