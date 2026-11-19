using System.ComponentModel;
using System.Diagnostics;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Prepares the external pieces needed by the edge-tts implementation. The
/// installation is deliberately application-local where possible: pipx (or a
/// plain venv on macOS) keeps its virtual environment and command shims below
/// Libzora's data directory. Only Linux system audio/Python packages require
/// an OS authorization dialog.
/// </summary>
public sealed partial class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(20);

    private readonly AppPaths _paths;
    private readonly ITtsEngine _engine;
    private readonly ITtsAudioPlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public TtsEnvironmentSetupService(
        AppPaths paths,
        ITtsEngine engine,
        ITtsAudioPlayer player)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public async Task<TtsSetupResult> EnsureReadyAsync(
        IProgress<TtsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(progress, "正在检查 TTS 环境…");
            var engineAvailability = await _engine.CheckAvailabilityAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var needsEngine = !engineAvailability.IsAvailable;
            var needsPlayer = !_player.IsAvailable;

            if (!needsEngine && !needsPlayer)
            {
                Report(progress, "TTS 环境已就绪。", 100);
                return new TtsSetupResult(true, "TTS 环境已就绪。");
            }

            if (!OperatingSystem.IsWindows()
                && !OperatingSystem.IsLinux()
                && !OperatingSystem.IsMacOS())
            {
                return Failure(
                    "当前平台暂不支持自动准备 edge-tts。",
                    progress);
            }

            var changedSystem = false;
            if (OperatingSystem.IsLinux())
            {
                var packages = await GetMissingLinuxPackagesAsync(
                        needsEngine,
                        needsPlayer,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (packages.Count > 0)
                {
                    await InstallLinuxPackagesAsync(
                            packages,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    changedSystem = true;
                }
            }

            if (needsEngine)
            {
                await EnsureEdgeTtsAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var finalEngine = await _engine.CheckAvailabilityAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (!finalEngine.IsAvailable)
            {
                return Failure(
                    finalEngine.Message,
                    progress,
                    changedSystem);
            }

            if (!_player.IsAvailable)
            {
                return Failure(
                    _player.UnavailableReason
                        ?? "TTS 已安装，但当前平台没有可用的音频输出。",
                    progress,
                    changedSystem);
            }

            Report(progress, "TTS 已自动安装并准备完成。", 100);
            return new TtsSetupResult(
                true,
                "TTS 已自动安装并准备完成。",
                changedSystem);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                $"TTS 自动安装失败：{exception.Message}",
                progress);
        }
        finally
        {
            _gate.Release();
        }
    }
}
