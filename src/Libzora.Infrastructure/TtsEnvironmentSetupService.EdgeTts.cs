using System.ComponentModel;
using System.Diagnostics;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Edge TTS installation steps.
/// </summary>
public sealed partial class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private async Task<IReadOnlyList<string>> GetMissingLinuxPackagesAsync(
        bool needsEngine,
        bool needsPlayer,
        CancellationToken cancellationToken)
    {
        var packages = new List<string>();
        if (needsPlayer)
            packages.Add("mpv");

        if (needsEngine)
        {
            if (await FindPythonAsync(cancellationToken).ConfigureAwait(false) is null)
                packages.Add("python3");
            if (FindExecutableOnPath("pipx") is null)
                packages.Add("pipx");
        }

        return packages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task EnsureEdgeTtsAsync(
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var python = await FindPythonAsync(cancellationToken).ConfigureAwait(false);
        if (python is null && OperatingSystem.IsWindows())
        {
            await InstallWindowsPythonAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            python = await FindPythonAsync(cancellationToken).ConfigureAwait(false);
        }

        if (python is null)
        {
            throw new InvalidOperationException(
                OperatingSystem.IsLinux()
                    ? "未找到 python3。请允许系统授权对话框安装 python3 后重试。"
                    : OperatingSystem.IsMacOS()
                        ? "未找到 Python 3。可以执行 xcode-select --install 安装命令行工具，"
                            + "或使用 Homebrew 执行 brew install python3，然后重启 Libzora。"
                        : "未找到 Python，且无法通过 winget 自动安装。请先安装 Python 3。 ");
        }

        // macOS must not assume pipx is installed, and Apple's / Homebrew
        // Python both refuse user-level pip installs (PEP 668). A dedicated
        // venv below the app data directory sidesteps both problems.
        if (OperatingSystem.IsMacOS())
        {
            await EnsureMacEdgeTtsAsync(python, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var pipx = FindExecutableOnPath("pipx");
        if (pipx is null)
        {
            var moduleCheck = await RunPythonModuleAsync(
                    python,
                    ["pipx", "--version"],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (moduleCheck.ExitCode != 0 || moduleCheck.TimedOut)
            {
                if (OperatingSystem.IsLinux())
                {
                    throw new InvalidOperationException(
                        "未找到 pipx。请允许系统授权对话框安装 pipx 后重试。\n"
                        + FormatProcessDetail(moduleCheck, "pipx 检测失败。"));
                }

                Report(progress, "正在安装 pipx…");
                var installPipx = await RunPythonModuleAsync(
                        python,
                        [
                            "pip",
                            "install",
                            "--user",
                            "--upgrade",
                            "pipx",
                        ],
                        CommandTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireSuccess(installPipx, "安装 pipx 失败。");
            }
        }

        pipx = FindExecutableOnPath("pipx");
        var pipxUsesModule = pipx is null;
        if (pipxUsesModule)
        {
            var moduleCheck = await RunPythonModuleAsync(
                    python,
                    ["pipx", "--version"],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireSuccess(moduleCheck, "安装 pipx 后仍无法启动 pipx。");
        }

        Directory.CreateDirectory(TtsRuntimePaths.PipxHome(_paths));
        Directory.CreateDirectory(TtsRuntimePaths.PipxBin(_paths));
        Directory.CreateDirectory(TtsRuntimePaths.PipxMan(_paths));
        Directory.CreateDirectory(TtsRuntimePaths.PipxVenvCache(_paths));

        Report(progress, "正在安装 edge-tts 到 Libzora 专用环境…");
        var installArguments = new List<string>();
        if (pipxUsesModule)
        {
            installArguments.AddRange(["pipx", "install", EdgeTtsEngine.PackageRequirement, "--force"]);
        }
        else
        {
            installArguments.AddRange(["install", EdgeTtsEngine.PackageRequirement, "--force"]);
        }

        // Do not let pipx select its own default interpreter here. Windows
        // Store Python redirects venv paths into the package sandbox, which
        // makes pipx fail immediately after printing only
        // "creating virtual environment...". FindPythonAsync filters that
        // interpreter out and records the real interpreter path for pipx.
        installArguments.AddRange(["--python", PythonInterpreterPath(python)]);

        var install = pipxUsesModule
            ? await RunPythonModuleAsync(
                    python,
                    installArguments,
                    CommandTimeout,
                    cancellationToken,
                    BuildPipxEnvironment(python))
                .ConfigureAwait(false)
            : await RunProcessAsync(
                    pipx!,
                    installArguments,
                    CommandTimeout,
                    cancellationToken,
                    BuildPipxEnvironment(python))
                .ConfigureAwait(false);
        RequireSuccess(install, "安装 edge-tts 失败。");

        var edgeExecutable = TtsRuntimePaths.EdgeTtsCandidates(_paths)
            .FirstOrDefault(File.Exists);
        if (edgeExecutable is null)
        {
            throw new InvalidOperationException(
                "edge-tts 安装命令已完成，但没有找到 Libzora 专用的 edge-tts 启动文件。 ");
        }
    }

    private async Task EnsureMacEdgeTtsAsync(
        PythonCommand python,
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var venvDirectory = TtsRuntimePaths.Venv(_paths);
        var venvPython = Path.Combine(venvDirectory, "bin", "python3");

        if (!File.Exists(venvPython))
        {
            Report(progress, "正在创建 Libzora 专用的 Python 环境…");
            var create = await RunPythonAsync(
                    python,
                    ["-m", "venv", venvDirectory],
                    CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireSuccess(create, "创建 Libzora 专用 Python 环境失败。");
        }

        if (!File.Exists(venvPython))
        {
            throw new InvalidOperationException(
                "Python 虚拟环境已创建，但没有找到其中的 python3。 ");
        }

        Report(progress, "正在安装 edge-tts 到 Libzora 专用环境…");
        var install = await RunProcessAsync(
                venvPython,
                ["-m", "pip", "install", "--upgrade", EdgeTtsEngine.PackageRequirement],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(install, "安装 edge-tts 失败。");

        var venvEdgeTts = Path.Combine(venvDirectory, "bin", "edge-tts");
        if (!File.Exists(venvEdgeTts))
        {
            throw new InvalidOperationException(
                "edge-tts 安装完成，但没有找到虚拟环境中的 edge-tts 启动文件。 ");
        }

        // EdgeTtsEngine only knows the shared application-local entry point
        // directory, so expose the venv command there. This also means no
        // PATH change or pipx installation is required on macOS.
        Directory.CreateDirectory(TtsRuntimePaths.PipxBin(_paths));
        var linkPath = TtsRuntimePaths.PreferredEdgeTtsPath(_paths);
        try
        {
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
                File.Delete(linkPath);
            File.CreateSymbolicLink(linkPath, venvEdgeTts);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "无法在 Libzora 数据目录中创建 edge-tts 启动链接。\n"
                + exception.Message);
        }
    }

    private async Task InstallWindowsPythonAsync(
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var winget = FindExecutableOnPath("winget");
        if (winget is null)
        {
            throw new InvalidOperationException(
                "Windows 未找到 winget，无法自动安装 Python。请安装 App Installer 后重试。\n"
                + "可执行：winget install --id Python.Python.3.12 --scope user");
        }

        Report(progress, "正在通过 Windows 包管理器安装 Python…");
        var result = await RunProcessAsync(
                winget,
                [
                    "install",
                    "--id",
                    "Python.Python.3.12",
                    "--exact",
                    "--scope",
                    "user",
                    "--silent",
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                ],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(result, "通过 winget 安装 Python 失败。");

        if (await FindPythonAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                "Python 安装程序已结束，但当前系统仍找不到 python.exe。请重启 Libzora 后重试。 ");
        }
    }
}
