using System.Diagnostics;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices.Tts;

public sealed class TtsStateChangedEventArgs : EventArgs
{
    public TtsStateChangedEventArgs(
        TtsPlaybackState state,
        string? message,
        int segmentIndex,
        int segmentCount,
        bool isSelection = false)
    {
        State = state;
        Message = message;
        SegmentIndex = segmentIndex;
        SegmentCount = segmentCount;
        IsSelection = isSelection;
    }

    public TtsPlaybackState State { get; }
    public string? Message { get; }
    public int SegmentIndex { get; }
    public int SegmentCount { get; }
    public bool IsSelection { get; }
}

/// <summary>
/// Coordinates sentence segmentation, cached generation, playback, sentence
/// highlighting and automatic chapter navigation. UI code only supplies the
/// current reader document and receives state changes.
/// </summary>

public sealed partial class TtsService : IDisposable
{
    public const string PreviewText = "你好，这是声音试听。";
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

    public static string GetPreviewText(string voice, string? culture = null)
        => TtsSelectionVoicePolicy.IsAmericanEnglishVoice(voice)
            || (!string.IsNullOrWhiteSpace(culture)
                && culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            || TtsModelCatalog.TryParseVoiceId(voice, out var packageId, out var speakerId)
                && packageId.StartsWith("kokoro", StringComparison.OrdinalIgnoreCase)
                && speakerId <= 2
            ? "Hello, this is an English voice preview."
            : PreviewText;

    private readonly ITtsEngine? _engine;
    private readonly TtsCacheManager _cache;
    private readonly TtsPlaybackQueue? _queue;
    private readonly ITtsAudioPlayer? _player;
    private readonly ITtsEnvironmentSetup? _environmentSetup;
    private readonly Func<ReaderTtsDocument?> _documentAccessor;
    private readonly Func<CancellationToken, Task<bool>>? _advanceChapterAsync;
    private readonly Func<CancellationToken, Task<ReaderTtsDocument?>>?
        _prefetchDocumentAccessor;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly object _gate = new();

    private TtsSettings _settings = new();
    private TtsAvailability? _availability;
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _playbackCancellation;
    private long _navigationVersion;
    private Task? _loopTask;
    private readonly List<Task> _loopTasks = [];
    private Task? _startupTask;
    private CancellationToken _sessionToken;
    private int _startGeneration;
    private int _generation;
    private bool _disposed;
    private int _activePreviewCount;
    private TaskCompletionSource? _previewsIdle;
    private TtsPlaybackState _state = TtsPlaybackState.Stopped;
    private string? _message;
    private string? _environmentMessage;
    private bool _environmentSetupInProgress;
    private int _segmentIndex;
    private int _segmentCount;
    private IReadOnlyList<TtsTextSegment>? _currentSegments;
    private string? _currentDocumentKey;
    private int _currentSegmentIndex = -1;
    private int _requestedSegmentIndex = -1;
    private bool _previewInProgress;
    private bool _isSelectionPlayback;

    public TtsService(
        ITtsEngine? engine,
        TtsCacheManager cache,
        ITtsAudioPlayer? player,
        Func<ReaderTtsDocument?> documentAccessor,
        Func<CancellationToken, Task<bool>>? advanceChapterAsync = null,
        ITtsEnvironmentSetup? environmentSetup = null,
        Func<CancellationToken, Task<ReaderTtsDocument?>>? prefetchDocumentAccessor = null)
    {
        _engine = engine;
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _queue = engine is null ? null : new TtsPlaybackQueue(engine, cache);
        _player = player;
        _environmentSetup = environmentSetup;
        _documentAccessor = documentAccessor
            ?? throw new ArgumentNullException(nameof(documentAccessor));
        _advanceChapterAsync = advanceChapterAsync;
        _prefetchDocumentAccessor = prefetchDocumentAccessor;
        if (_player is ITtsExternalPlaybackObserver observer)
            observer.ExternalPauseStateChanged += OnExternalPauseStateChanged;
    }

    public event EventHandler<TtsStateChangedEventArgs>? StateChanged;
    public event EventHandler? EnvironmentChanged;

    /// <summary>True only when both the configured engine and audio output are ready.</summary>
    public bool IsAvailable =>
        !_disposed
        && _engine?.IsAvailable == true
        && _player?.IsAvailable == true;

    /// <summary>The active platform engine, independent of legacy saved provider settings.</summary>
    public string? EngineId => _engine?.Id;

    private TtsOptions BuildOptions(TtsSettings settings)
    {
        var options = settings.ToOptions();
        return options with
        {
            Provider = _engine?.Id ?? options.Provider,
            AudioFormat = _engine?.Id == "local-system-tts" ? "wav" : options.AudioFormat
        };
    }

    /// <summary>Keep settings accessible even when system voices are missing.</summary>
    public bool CanOpen => !_disposed && _player is not null;

    /// <summary>
    /// Indicates whether the caller can offer an automatic environment repair.
    /// The setup implementation is optional so platform heads and tests can
    /// still use TTS without promising an installer that is not present.
    /// </summary>
    public bool CanRepairEnvironment =>
        !_disposed && _engine is not null && _environmentSetup is not null;

    public TtsPlaybackState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public bool IsSelectionPlayback
    {
        get
        {
            lock (_gate) return _isSelectionPlayback;
        }
    }

    public string? Message
    {
        get
        {
            lock (_gate) return _message;
        }
    }

    public TtsAvailability? Availability
    {
        get
        {
            lock (_gate) return _availability;
        }
    }

    public bool EnvironmentSetupInProgress
    {
        get
        {
            lock (_gate) return _environmentSetupInProgress;
        }
    }

    public bool PreviewInProgress
    {
        get
        {
            lock (_gate) return _previewInProgress;
        }
    }

    public string? EnvironmentMessage
    {
        get
        {
            lock (_gate) return _environmentMessage;
        }
    }

    public TtsSettings Settings
    {
        get
        {
            lock (_gate) return _settings.Clone();
        }
    }

    public int SegmentIndex
    {
        get
        {
            lock (_gate) return _segmentIndex;
        }
    }

    public int SegmentCount
    {
        get
        {
            lock (_gate) return _segmentCount;
        }
    }

    public bool CanSkipPrevious
    {
        get
        {
            lock (_gate)
            {
                return _currentSegmentIndex > 0
                    && _currentSegments is { Count: > 0 };
            }
        }
    }

    public bool CanSkipNext
    {
        get
        {
            lock (_gate)
            {
                return _currentSegments is { Count: > 0 } segments
                    && _currentSegmentIndex >= 0
                    && _currentSegmentIndex + 1 < segments.Count;
            }
        }
    }

    public async Task<TtsAvailability> CheckEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        if (_engine is null)
        {
            var unavailable = new TtsAvailability(
                false,
                "当前平台没有配置 TTS 引擎。");
            lock (_gate) _availability = unavailable;
            return unavailable;
        }

        var availability = await _engine.CheckAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        availability = IncludePlayerAvailability(availability);

        lock (_gate) _availability = availability;
        return availability;
    }

    /// <summary>
    /// Ensures the platform dependencies exist before playback starts. The
    /// setup service is optional so tests and future providers can omit it.
    /// </summary>
    public async Task<TtsAvailability> EnsureEnvironmentReadyAsync(
        IProgress<TtsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is null)
        {
            var unavailable = new TtsAvailability(
                false,
                "当前平台没有配置 TTS 引擎。 ");
            lock (_gate)
            {
                _availability = unavailable;
                _environmentMessage = unavailable.Message;
                _environmentSetupInProgress = false;
            }
            return unavailable;
        }

        if (_environmentSetup is not null)
        {
            SetEnvironmentStatus("正在自动准备 TTS 环境…", inProgress: true);
            // Setup progress MUST flip the in-progress flag synchronously on
            // this workflow thread. A UI-posted Progress<T> would queue the
            // "in progress" callbacks behind a busy startup UI thread and
            // replay them AFTER the final ready transition, permanently
            // leaving EnvironmentSetupInProgress=true (grey, disabled button).
            var setupProgress = new SynchronousProgress<TtsSetupProgress>(update =>
            {
                SetEnvironmentStatus(update.Message, inProgress: true);
                progress?.Report(update);
            });
            var setup = await _environmentSetup.EnsureReadyAsync(
                    setupProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!setup.IsSuccess)
            {
                SetEnvironmentStatus(setup.Message, inProgress: false);
                var unavailable = new TtsAvailability(false, setup.Message);
                lock (_gate) _availability = unavailable;
                return unavailable;
            }
        }

        var availability = await CheckEnvironmentAsync(cancellationToken)
            .ConfigureAwait(false);
        SetEnvironmentStatus(
            availability.IsAvailable ? "TTS 环境已就绪。" : availability.Message,
            inProgress: false);
        return availability;
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_engine is null) return [];
        // The voice popup can be opened while the startup bootstrap is still
        // running. Reuse it here so a fast click does not race the installer.
        if (_environmentSetup is not null)
        {
            var ready = await EnsureEnvironmentReadyAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!ready.IsAvailable) return [];
        }

        var engineAvailability = await _engine.CheckAvailabilityAsync(
                cancellationToken)
            .ConfigureAwait(false);
        var availability = IncludePlayerAvailability(engineAvailability);
        lock (_gate) _availability = availability;
        if (!engineAvailability.IsAvailable) return [];
        return await _engine.GetVoicesAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private TtsAvailability IncludePlayerAvailability(
        TtsAvailability availability)
    {
        if (_player is null || _player.IsAvailable) return availability;

        var playerMessage = string.IsNullOrWhiteSpace(_player.UnavailableReason)
            ? "当前平台没有可用的音频输出。"
            : _player.UnavailableReason;
        return availability with
        {
            IsAvailable = false,
            Message = string.IsNullOrWhiteSpace(availability.Message)
                ? playerMessage
                : availability.Message + "\n\n" + playerMessage
        };
    }

    public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        string engineId,
        CancellationToken cancellationToken = default)
        => string.Equals(
            engineId,
            _engine?.Id,
            StringComparison.OrdinalIgnoreCase)
            ? GetVoicesAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<TtsVoiceInfo>>([]);

    /// <summary>
    /// Generates and plays one fixed sample using the supplied settings. The
    /// sample is deliberately not written to the book cache; its temporary MP3
    /// is removed after playback or cancellation.
    /// </summary>
    public async Task PreviewAsync(
        TtsSettings settings,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? idleSignal = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activePreviewCount++ == 0)
            {
                _previewsIdle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        try
        {
            var normalized = TtsSettings.Normalize(settings);
            if (_engine is null || _player is null)
            {
                throw new InvalidOperationException(
                    "当前平台没有配置 TTS 引擎或音频播放器。");
            }

            await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var markedInProgress = false;
            string? temporaryPath = null;
            try
            {
                // StartAsync holds this gate while it prepares a session. Taking
                // it here prevents a preview from racing an in-flight start,
                // while the short-lived flag blocks a new start during playback.
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        if (_loopTask is { IsCompleted: false }
                            || _startCancellation is not null
                            || _state is TtsPlaybackState.Generating
                                or TtsPlaybackState.Playing
                                or TtsPlaybackState.Paused
                                or TtsPlaybackState.AdvancingChapter)
                        {
                            throw new InvalidOperationException(
                                "请先停止听书，再试听声音。");
                        }

                        _previewInProgress = true;
                        markedInProgress = true;
                    }
                }
                finally
                {
                    _lifecycle.Release();
                }

                var availability = await EnsureEnvironmentReadyAsync(
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!availability.IsAvailable)
                {
                    throw new InvalidOperationException(availability.Message);
                }

                if (!_player.IsAvailable)
                {
                    throw new InvalidOperationException(
                        _player.UnavailableReason ?? "当前平台没有可用的音频输出。");
                }

                var generated = await _engine.SynthesizeAsync(
                        GetPreviewText(normalized.Voice, normalized.VoiceCulture),
                        BuildOptions(normalized),
                        cancellationToken)
                    .ConfigureAwait(false);
                temporaryPath = generated.AudioPath;
                if (!generated.IsSuccess
                    || string.IsNullOrWhiteSpace(generated.AudioPath))
                {
                    throw new InvalidOperationException(
                        generated.ErrorMessage ?? "声音试听失败。");
                }

                await _player.PlayAsync(
                        generated.AudioPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _cache.DeleteTemporaryFile(temporaryPath);
                if (markedInProgress)
                {
                    lock (_gate) _previewInProgress = false;
                }

                try { _previewGate.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
        finally
        {
            lock (_gate)
            {
                _activePreviewCount = Math.Max(0, _activePreviewCount - 1);
                if (_activePreviewCount == 0)
                {
                    idleSignal = _previewsIdle;
                    _previewsIdle = null;
                }
            }

            idleSignal?.TrySetResult();
        }
    }
}
