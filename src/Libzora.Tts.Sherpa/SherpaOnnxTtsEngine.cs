using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using SherpaOnnx;

namespace Libzora.Tts.Sherpa;

/// <summary>
/// One downloaded sherpa-onnx TTS package. Construction is cheap; the
/// native model is loaded lazily on the first synthesis request and reused.
/// </summary>
public sealed class SherpaOnnxTtsEngine : ITtsEngine
{
    private readonly TtsModelPackage _package;
    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _generateGate = new(1, 1);
    private OfflineTts? _tts;
    private bool _disposed;

    public SherpaOnnxTtsEngine(TtsModelPackage package, string modelDirectory)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        _modelDirectory = Path.GetFullPath(modelDirectory);
    }

    public string Id => _package.EngineId;

    public bool IsAvailable =>
        !_disposed
        && OperatingSystem.IsMacOS()
            || !_disposed && OperatingSystem.IsWindows()
            || !_disposed && OperatingSystem.IsLinux();

    public Task<TtsAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Directory.Exists(_modelDirectory))
        {
            return Task.FromResult(new TtsAvailability(
                false,
                $"尚未下载 {_package.DisplayName}。"));
        }
        foreach (var entry in _package.RequiredEntries)
        {
            var path = Path.Combine(_modelDirectory, entry);
            if (!Directory.Exists(path)
                && (!File.Exists(path) || new FileInfo(path).Length <= 0))
            {
                return Task.FromResult(new TtsAvailability(
                    false,
                    $"{_package.DisplayName} 文件不完整，请重新下载。"));
            }
        }
        return Task.FromResult(new TtsAvailability(
            true,
            $"{_package.DisplayName} 已就绪；朗读完全在本机完成。"));
    }

    public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(SherpaVoiceCatalog.GetVoices(_package));
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        TtsOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();

        if (!SherpaVoiceCatalog.TryParseVoiceId(options.Voice, out var packageId, out var speakerId)
            || !packageId.Equals(_package.Id, StringComparison.OrdinalIgnoreCase))
        {
            speakerId = 0;
        }

        string? temporaryPath = null;
        try
        {
            var tts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            await _generateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var speed = (float)Math.Clamp(
                    1d + options.RatePercent / 100d,
                    0.5d,
                    2d);
                SherpaOnnx.OfflineTtsCallback callback =
                    (_, _) => cancellationToken.IsCancellationRequested ? 0 : 1;
                var audio = await Task.Run(
                        () => tts.GenerateWithCallback(text, speed, speakerId, callback),
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (audio.NumSamples <= 0)
                        return TtsResult.Failure("离线语音包没有生成音频。");

                    temporaryPath = Path.Combine(
                        Path.GetTempPath(),
                        "libzora-sherpa-tts",
                        Guid.NewGuid().ToString("N") + ".wav");
                    Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
                    WriteWaveFile(
                        temporaryPath,
                        audio.Samples,
                        audio.SampleRate,
                        options.VolumePercent);
                    var result = TtsResult.Success(temporaryPath, "wav");
                    temporaryPath = null;
                    return result;
                }
                finally
                {
                    audio.Dispose();
                }
            }
            finally
            {
                _generateGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            return TtsResult.Failure($"离线语音合成失败：{exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<OfflineTts> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_tts is not null) return _tts;
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tts is not null) return _tts;
            var config = BuildConfig();
            _tts = await Task.Run(() => new OfflineTts(config), cancellationToken)
                .ConfigureAwait(false);
            return _tts;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"无法加载 {_package.DisplayName}：{exception.Message}",
                exception);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private OfflineTtsConfig BuildConfig()
    {
        var config = new OfflineTtsConfig
        {
            MaxNumSentences = 1,
        };
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";

        if (_package.EngineId.Equals("sherpa-kokoro", StringComparison.Ordinal))
        {
            var modelFile = new[]
            {
                Path.Combine(_modelDirectory, "model.onnx"),
                Path.Combine(_modelDirectory, "model.int8.onnx"),
            }.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("语音包中没有找到 Kokoro ONNX 模型。");
            var lexicons = new[]
            {
                Path.Combine(_modelDirectory, "lexicon-us-en.txt"),
                Path.Combine(_modelDirectory, "lexicon-zh.txt"),
            }.Where(File.Exists);
            config.Model.Kokoro = new OfflineTtsKokoroModelConfig
            {
                Model = modelFile,
                Voices = Path.Combine(_modelDirectory, "voices.bin"),
                Tokens = Path.Combine(_modelDirectory, "tokens.txt"),
                DataDir = Path.Combine(_modelDirectory, "espeak-ng-data"),
                Lexicon = string.Join(",", lexicons),
            };
            var ruleFsts = new[]
            {
                Path.Combine(_modelDirectory, "date-zh.fst"),
                Path.Combine(_modelDirectory, "number-zh.fst"),
                Path.Combine(_modelDirectory, "phone-zh.fst"),
            }.Where(File.Exists);
            var ruleFstsValue = string.Join(",", ruleFsts);
            if (ruleFstsValue.Length > 0) config.RuleFsts = ruleFstsValue;
        }
        else if (_package.EngineId.Equals("sherpa-melo", StringComparison.Ordinal))
        {
            var dictDirectory = Path.Combine(_modelDirectory, "dict");
            var dataDirectory = Path.Combine(_modelDirectory, "espeak-ng-data");
            config.Model.Vits = new OfflineTtsVitsModelConfig
            {
                Model = Path.Combine(_modelDirectory, "model.onnx"),
                Lexicon = Path.Combine(_modelDirectory, "lexicon.txt"),
                Tokens = Path.Combine(_modelDirectory, "tokens.txt"),
                DictDir = Directory.Exists(dictDirectory) ? dictDirectory : string.Empty,
                DataDir = Directory.Exists(dataDirectory) ? dataDirectory : string.Empty,
            };
            var dateFst = Path.Combine(_modelDirectory, "date.fst");
            if (File.Exists(dateFst)) config.RuleFsts = dateFst;
        }
        else
        {
            throw new InvalidOperationException(
                $"不支持的离线语音包类型：{_package.EngineId}。");
        }

        return config;
    }

    private static void WriteWaveFile(
        string path,
        float[] samples,
        int sampleRate,
        int volumePercent)
    {
        var gain = Math.Clamp(1d + volumePercent / 100d, 0d, 2d);
        var dataLength = samples.Length * sizeof(short);
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            var scaled = Math.Clamp(sample * gain, -1d, 1d);
            writer.Write((short)Math.Round(scaled * short.MaxValue));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tts?.Dispose();
        _tts = null;
        _loadGate.Dispose();
        _generateGate.Dispose();
    }
}
