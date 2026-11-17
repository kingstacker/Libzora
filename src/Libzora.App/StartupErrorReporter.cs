using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Libzora;

/// <summary>
/// Shows a native error dialog before Avalonia exists. Startup can fail while
/// resolving the configured data root (for example when an external disk is
/// unmounted), and a silent exit would leave the user with no explanation.
/// </summary>
public static class StartupErrorReporter
{
    public static void Show(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsWindows()) ShowWindows(title, message);
            else if (OperatingSystem.IsMacOS()) ShowMacOS(title, message);
            else ShowLinux(title, message);
        }
        catch
        {
            // A diagnostic dialog must never replace the original failure.
        }

        try
        {
            Console.Error.WriteLine($"{title}: {message}");
        }
        catch
        {
        }
    }

    private static void ShowWindows(string title, string message)
    {
        const uint IconError = 0x00000010;
        MessageBoxW(IntPtr.Zero, message, title, IconError);
    }

    private static void ShowMacOS(string title, string message)
    {
        var script =
            $"display alert {AppleScriptString(title)} message {AppleScriptMessage(message)} "
            + "as critical buttons {\"OK\"} default button \"OK\"";
        var startInfo = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo);
        process?.WaitForExit(TimeSpan.FromMinutes(2));
    }

    private static void ShowLinux(string title, string message)
    {
        if (TryRun("zenity", ["--error", $"--title={title}", $"--text={message}"])) return;
        if (TryRun("kdialog", ["--title", title, "--error", message])) return;
        Console.Error.WriteLine($"{title}: {message}");
    }

    private static bool TryRun(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit(TimeSpan.FromMinutes(2));
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException)
        {
            return false;
        }
    }

    private static string AppleScriptString(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string AppleScriptMessage(string message)
    {
        var lines = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return "(" + string.Join(" & linefeed & ", lines.Select(AppleScriptString)) + ")";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
