using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tts.Sherpa;

/// <summary>
/// Composite desktop engine: installed system voices and any downloaded
/// sherpa-onnx packages appear in one voice list. The voice id prefix selects
/// the backend, so the rest of the application keeps using a single engine id
/// and a single offline audio format.
/// </summary>
public sealed class LocalTtsEngine : ITtsEngine
{
    private readonly LocalSystemTtsEngine _system = new();
    private readonly TtsModelStore _models;
    private readonly Dictionary<string, SherpaOnnxTtsEngine> _loaded =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public LocalTtsEngine(AppPaths paths)
    {
        _models = new TtsModelStore(paths ?? throw new ArgumentNullException(nameof(paths)));
    }

    /// <summary>
    /// The existing provider id is kept deliberately: the cache key, WAV
    /// preference and reader UI already treat this id as fully offline.
    /// </summary>
    public string Id => LocalSystemTtsEngine.ProviderId;

    public bool IsAvailable =>
        !_disposed
        && (_system.IsAvailable || TtsModelCatalog.Packages.Any(_models.IsInstalled));

    public async Task<TtsAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_system.IsAvailable && !TtsModelCatalog.Packages.Any(_models.IsInstalled))
        {
            return new TtsAvailability(
                false,
                "没有可用的本机语音；可在听书设置中下载离线语音包，或在系统设置中安装语音。");
        }
        if (_system.IsAvailable)
            return await _system.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        return new TtsAvailability(
            true,
            "已下载的离线语音包可用；朗读完全在本机完成。");
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var voices = new List<TtsVoiceInfo>();
        if (_system.IsAvailable)
        {
            try
            {
                voices.AddRange(
                    await _system.GetVoicesAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A broken system voice database must not hide downloaded packages.
            }
        }
        foreach (var package in TtsModelCatalog.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_models.IsInstalled(package))
                voices.AddRange(SherpaVoiceCatalog.GetVoices(package));
        }
        return voices;
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        TtsOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();

        if (SherpaVoiceCatalog.TryParseVoiceId(options.Voice, out var packageId, out _))
        {
            var package = TtsModelCatalog.Find(packageId);
            if (package is null || !_models.IsInstalled(package))
            {
                return TtsResult.Failure(
                    "所选离线语音包尚未下载或已被删除，请刷新声音列表后重新选择。");
            }
            var engine = await GetOrCreateAsync(package, cancellationToken)
                .ConfigureAwait(false);
            return await engine.SynthesizeAsync(text, options, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_system.IsAvailable)
            return TtsResult.Failure("没有可用的本机系统语音，请下载离线语音包或安装系统语音。");

        return await _system.SynthesizeAsync(text, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SherpaOnnxTtsEngine> GetOrCreateAsync(
        TtsModelPackage package,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loaded.TryGetValue(package.Id, out var engine)) return engine;
            var directory = _models.GetInstallDirectory(package);
            engine = new SherpaOnnxTtsEngine(package, directory);
            _loaded[package.Id] = engine;
            return engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var engine in _loaded.Values) engine.Dispose();
        _loaded.Clear();
        _system.Dispose();
        _models.Dispose();
        _gate.Dispose();
    }
}
