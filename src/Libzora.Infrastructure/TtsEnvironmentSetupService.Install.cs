using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Windows Python and Linux package install.
/// </summary>
public sealed partial class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private async Task InstallLinuxPackagesAsync(
        IReadOnlyList<string> packages,
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var apt = FindExecutableOnPath("apt-get")
            ?? (File.Exists("/usr/bin/apt-get") ? "/usr/bin/apt-get" : null);
        if (apt is null)
        {
            throw new InvalidOperationException(
                "未找到 apt-get，无法自动安装 Linux TTS 依赖。 ");
        }

        var pkexec = FindExecutableOnPath("pkexec");
        var sudo = FindExecutableOnPath("sudo");
        var isRoot = string.Equals(
            Environment.UserName,
            "root",
            StringComparison.OrdinalIgnoreCase);

        if (!isRoot && pkexec is null && sudo is null)
        {
            throw new InvalidOperationException(
                "安装 Linux TTS 依赖需要 pkexec 或 sudo，但系统中未找到。 ");
        }

        Report(progress, "正在请求系统授权并更新软件包索引…");
        var update = await RunPrivilegedAptAsync(
                apt,
                pkexec,
                sudo,
                ["update"],
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(update, "更新 Linux 软件包索引失败。");

        Report(
            progress,
            $"正在安装 Linux TTS 依赖：{string.Join(", ", packages)}…");
        var installArguments = new List<string>
        {
            "install",
            "--yes",
            "--no-install-recommends",
        };
        installArguments.AddRange(packages);
        var install = await RunPrivilegedAptAsync(
                apt,
                pkexec,
                sudo,
                installArguments,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(install, "安装 Linux TTS 依赖失败。");
    }

    private static Task<ProcessResult> RunPrivilegedAptAsync(
        string apt,
        string? pkexec,
        string? sudo,
        IReadOnlyList<string> aptArguments,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                Environment.UserName,
                "root",
                StringComparison.OrdinalIgnoreCase))
        {
            return RunProcessAsync(
                apt,
                aptArguments,
                CommandTimeout,
                cancellationToken);
        }

        if (pkexec is not null)
        {
            var arguments = new List<string> { apt };
            arguments.AddRange(aptArguments);
            return RunProcessAsync(
                pkexec,
                arguments,
                CommandTimeout,
                cancellationToken);
        }

        var sudoArguments = new List<string> { "--non-interactive", apt };
        sudoArguments.AddRange(aptArguments);
        return RunProcessAsync(
            sudo!,
            sudoArguments,
            CommandTimeout,
            cancellationToken);
    }
}
