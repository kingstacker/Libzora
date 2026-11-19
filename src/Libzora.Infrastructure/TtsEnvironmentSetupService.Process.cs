using System.Diagnostics;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Process execution and diagnostics.
/// </summary>
public sealed partial class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(exitTask, timeoutTask)
                .ConfigureAwait(false);
            if (completed != exitTask)
            {
                KillProcess(process);
                await WaitForExitSafelyAsync(exitTask).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                return new ProcessResult(
                    -1,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false),
                    true);
            }

            await exitTask.ConfigureAwait(false);
        }
        catch
        {
            KillProcess(process);
            await WaitForExitSafelyAsync(exitTask).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false),
            false);
    }

    private static string? FindExecutableOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? Path.GetFullPath(name) : null;

        var pathValue = Environment.GetEnvironmentVariable("PATH")
            ?? Environment.GetEnvironmentVariable("Path");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;

        var names = OperatingSystem.IsWindows()
            ? BuildWindowsExecutableNames(name)
            : [name];
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidateName in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), candidateName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildWindowsExecutableNames(string name)
    {
        if (Path.HasExtension(name)) return [name];
        return [name + ".exe", name, name + ".cmd", name + ".bat"];
    }

    private static void Report(
        IProgress<TtsSetupProgress>? progress,
        string message,
        double? percentage = null)
        => progress?.Report(new TtsSetupProgress(
            message,
            percentage is null,
            percentage));

    private static TtsSetupResult Failure(
        string? message,
        IProgress<TtsSetupProgress>? progress,
        bool changedSystem = false)
    {
        var detail = string.IsNullOrWhiteSpace(message)
            ? "没有更多错误信息。"
            : message.Trim();
        Report(progress, detail);
        return new TtsSetupResult(false, detail, changedSystem);
    }

    private static void RequireSuccess(ProcessResult result, string fallback)
    {
        if (result.TimedOut)
            throw new InvalidOperationException(fallback + "命令执行超时。 ");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FormatProcessDetail(result, fallback));
    }

    private static string FormatProcessDetail(ProcessResult result, string fallback)
    {
        var detail = string.Join(
            Environment.NewLine,
            new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));
        if (detail.Length > 3000) detail = detail[^3000..];
        return string.IsNullOrWhiteSpace(detail)
            ? fallback
            : fallback + Environment.NewLine + detail;
    }

    private static async Task WaitForExitSafelyAsync(Task exitTask)
    {
        try { await exitTask.ConfigureAwait(false); }
        catch { }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }
}
