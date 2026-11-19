using System.ComponentModel;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Python discovery and module invocation.
/// </summary>
public sealed partial class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private async Task<PythonCommand?> FindPythonAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new List<PythonCommand>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "python.exe", "python3.exe", "py.exe" })
            {
                var path = FindExecutableOnPath(name);
                if (path is not null)
                {
                    candidates.Add(new PythonCommand(
                        path,
                        Path.GetFileName(path).Equals(
                            "py.exe",
                            StringComparison.OrdinalIgnoreCase)
                            ? ["-3"]
                            : []));
                }
            }

            foreach (var path in EnumerateWindowsPythonLocations())
                candidates.Add(new PythonCommand(path, []));
        }
        else
        {
            foreach (var name in new[] { "python3", "python" })
            {
                var path = FindExecutableOnPath(name);
                if (path is not null)
                    candidates.Add(new PythonCommand(path, []));
            }

            // A Finder-launched .app inherits the minimal launchd PATH, which
            // does not include Homebrew. Probe the usual install locations so
            // an existing Python 3 is still found without a terminal.
            if (OperatingSystem.IsMacOS())
            {
                foreach (var path in MacPythonCandidates())
                    candidates.Add(new PythonCommand(path, []));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Executable)) continue;
            try
            {
                var result = await RunPythonAsync(
                        candidate,
                        ["-c", "import sys; print(sys.executable)"],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.TimedOut || result.ExitCode != 0)
                    continue;

                var interpreterPath = FirstNonEmptyLine(
                    result.StandardOutput,
                    result.StandardError);
                if (IsMicrosoftStorePythonPath(interpreterPath))
                    continue;

                return candidate with { InterpreterPath = interpreterPath };
            }
            catch (Win32Exception)
            {
            }
            catch (FileNotFoundException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> MacPythonCandidates()
    {
        yield return "/opt/homebrew/bin/python3";
        yield return "/usr/local/bin/python3";
        yield return "/usr/bin/python3";
        yield return "/Library/Frameworks/Python.framework/Versions/Current/bin/python3";
    }

    private static IEnumerable<string> EnumerateWindowsPythonLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local",
                "Programs",
                "Python"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Python"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Python"),
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.Exists(root)
                    ? Directory.EnumerateDirectories(root, "Python*", SearchOption.TopDirectoryOnly)
                    : [];
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories.OrderByDescending(path => path))
            {
                var python = Path.Combine(directory, "python.exe");
                if (File.Exists(python)) yield return python;
            }
        }
    }

    private static string PythonInterpreterPath(PythonCommand python)
        => string.IsNullOrWhiteSpace(python.InterpreterPath)
            ? python.Executable
            : python.InterpreterPath;

    internal static bool IsMicrosoftStorePythonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        return normalized.Contains(
                   @"\Microsoft\WindowsApps\",
                   StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                   @"\Packages\PythonSoftwareFoundation.",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmptyLine(params string?[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

    private Dictionary<string, string> BuildPipxEnvironment(PythonCommand python)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PIPX_HOME"] = TtsRuntimePaths.PipxHome(_paths),
            ["PIPX_BIN_DIR"] = TtsRuntimePaths.PipxBin(_paths),
            ["PIPX_MAN_DIR"] = TtsRuntimePaths.PipxMan(_paths),
            ["PIPX_VENV_CACHEDIR"] = TtsRuntimePaths.PipxVenvCache(_paths),
            ["PIPX_DEFAULT_PYTHON"] = PythonInterpreterPath(python),
        };
        return environment;
    }

    private static async Task<ProcessResult> RunPythonModuleAsync(
        PythonCommand python,
        IReadOnlyList<string> moduleArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
        => await RunPythonAsync(
                python,
                ["-m", .. moduleArguments],
                timeout,
                cancellationToken,
                environment)
            .ConfigureAwait(false);

    private static Task<ProcessResult> RunPythonAsync(
        PythonCommand python,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var allArguments = new List<string>(python.PrefixArguments.Count + arguments.Count);
        allArguments.AddRange(python.PrefixArguments);
        allArguments.AddRange(arguments);
        return RunProcessAsync(
            python.Executable,
            allArguments,
            timeout,
            cancellationToken,
            environment);
    }
}
