using System.Diagnostics;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Requests a clean process restart after the active data root changes. The
/// platform head launches the new process only after the old single-instance
/// lock has been released, otherwise the new copy would merely activate the
/// instance that is about to exit.
/// </summary>
public static class AppRestartRequest
{
    private static int _requested;

    public static void Request() => Interlocked.Exchange(ref _requested, 1);

    public static void LaunchIfRequested()
    {
        if (Interlocked.Exchange(ref _requested, 0) == 0) return;
        try
        {
            if (OperatingSystem.IsMacOS() && FindMacBundle(AppContext.BaseDirectory) is { } bundle)
            {
                var startInfo = new ProcessStartInfo("/usr/bin/open")
                {
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("-n");
                startInfo.ArgumentList.Add(bundle);
                Process.Start(startInfo);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (Exception exception)
        {
            SoftwareLog.ErrorWithoutMessage("app.restart", exception);
        }
    }

    private static string? FindMacBundle(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
