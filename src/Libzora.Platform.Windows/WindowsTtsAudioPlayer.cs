using Libzora.Core;
using NAudio.Wave;

namespace Libzora.Platform.Windows;

/// <summary>Windows MP3/WAV output for cached TTS audio.</summary>
public sealed class WindowsTtsAudioPlayer : ITtsAudioPlayer
{
    private readonly object _gate = new();
    private readonly Func<string, ITtsWaveOutput> _outputFactory;
    private ITtsWaveOutput? _output;
    private TaskCompletionSource<object?>? _completion;
    private bool _paused;
    private bool _disposed;

    public WindowsTtsAudioPlayer()
        : this(audioPath => new NAudioWaveOutput(CreateReader(audioPath)))
    {
    }

    internal WindowsTtsAudioPlayer(Func<string, ITtsWaveOutput> outputFactory)
    {
        ArgumentNullException.ThrowIfNull(outputFactory);
        _outputFactory = outputFactory;
    }

    public bool IsAvailable => !_disposed && OperatingSystem.IsWindows();

    public string? UnavailableReason
        => OperatingSystem.IsWindows()
            ? null
            : "Windows 音频播放器只能在 Windows 上使用。";

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    public Task PlayAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(UnavailableReason);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("TTS 音频文件不存在。", audioPath);
        cancellationToken.ThrowIfCancellationRequested();

        Stop();

        var output = _outputFactory(audioPath);
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _output = output;
            _completion = completion;
            _paused = false;
        }

        // NAudio reports decoder/output failures through the stopped event.
        // Forwarding only a null error would make a failed segment look like a
        // successful playback and let the reading position advance.
        output.PlaybackStopped += (_, args) => Complete(output, args.Exception);
        try
        {
            output.Init();
            output.Play();
        }
        catch (Exception exception)
        {
            Complete(output, exception);
        }

        CancellationTokenRegistration registration = default;
        registration = cancellationToken.Register(() =>
            Complete(output, new OperationCanceledException(cancellationToken)));
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    public void Pause()
    {
        ITtsWaveOutput? output;
        lock (_gate)
        {
            output = _output;
            if (output is null) return;
            _paused = true;
        }

        try { output.Pause(); }
        catch { }
    }

    public void Resume()
    {
        ITtsWaveOutput? output;
        lock (_gate)
        {
            output = _output;
            if (output is null) return;
            _paused = false;
        }

        try { output.Play(); }
        catch { }
    }

    public void Stop()
    {
        ITtsWaveOutput? output;
        lock (_gate) output = _output;
        if (output is null) return;
        Complete(output, new OperationCanceledException());
    }

    private void Complete(ITtsWaveOutput output, Exception? error)
    {
        TaskCompletionSource<object?>? completion;
        lock (_gate)
        {
            if (!ReferenceEquals(_output, output)) return;
            _output = null;
            completion = _completion;
            _completion = null;
            _paused = false;
        }

        // Detach the playback state before stopping. WaveOut raises
        // PlaybackStopped synchronously from Stop() with a null error, and that
        // event must not overwrite a user Stop/cancellation with success.
        try { output.Stop(); }
        catch { }
        try { output.Dispose(); }
        catch { }

        if (completion is null) return;
        if (error is OperationCanceledException)
            completion.TrySetCanceled();
        else if (error is not null)
            completion.TrySetException(error);
        else
            completion.TrySetResult(null);
    }

    private static WaveStream CreateReader(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new Mp3FileReader(path);
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new WaveFileReader(path);
        throw new InvalidDataException($"不支持的 TTS 音频格式：{extension}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Narrow seam around <see cref="WaveOut"/> so the completion and
    /// cancellation semantics can be tested without an audio device.
    /// </summary>
    internal interface ITtsWaveOutput : IDisposable
    {
        event EventHandler<StoppedEventArgs>? PlaybackStopped;
        void Init();
        void Play();
        void Pause();
        void Stop();
    }

    private sealed class NAudioWaveOutput : ITtsWaveOutput
    {
        private readonly WaveOut _output = new();
        private readonly WaveStream _reader;

        public NAudioWaveOutput(WaveStream reader) => _reader = reader;

        public event EventHandler<StoppedEventArgs>? PlaybackStopped
        {
            add => _output.PlaybackStopped += value;
            remove => _output.PlaybackStopped -= value;
        }

        public void Init() => _output.Init(_reader);
        public void Play() => _output.Play();
        public void Pause() => _output.Pause();
        public void Stop() => _output.Stop();

        public void Dispose()
        {
            try { _output.Dispose(); }
            finally { _reader.Dispose(); }
        }
    }
}
