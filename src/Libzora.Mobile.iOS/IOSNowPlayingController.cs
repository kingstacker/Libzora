#if LIBZORA_SWIFT_UI
using Avalonia.Threading;
using CoreGraphics;
using Foundation;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using MediaPlayer;
using UIKit;

namespace Libzora.iOSHead;

/// <summary>
/// Keeps iOS Lock Screen / Control Center Now Playing metadata and remote
/// transport commands synchronized with active TTS playback, and holds a
/// short background task bridge across sentence and chapter transitions so
/// screen-off listening never suspends between segments.
/// </summary>
internal sealed class IOSNowPlayingController : IDisposable
{
    private readonly Func<ReaderPage?> _readerAccessor;
    private readonly List<NSObject> _commandTargets = [];
    private nint _backgroundTaskId = UIApplication.BackgroundTaskInvalid;
    private string? _cachedCoverPath;
    private MPMediaItemArtwork? _cachedArtwork;
    private bool _active;
    private bool _disposed;

    public IOSNowPlayingController(Func<ReaderPage?> readerAccessor)
    {
        _readerAccessor = readerAccessor;
        RegisterRemoteCommands();
    }

    public void Update(ReaderPage reader, NativeReaderState state, AppPaths paths)
    {
        if (_disposed) return;
        var active = state.SpeechState is TtsPlaybackState.Generating
            or TtsPlaybackState.Playing
            or TtsPlaybackState.Paused
            or TtsPlaybackState.AdvancingChapter;
        if (!active)
        {
            Clear();
            return;
        }

        _active = true;
        SetCommandsEnabled(true);
        // Only hold an assertion during silent synthesis/chapter-turn gaps;
        // release it as soon as audio plays so the 30s OS budget stays fresh
        // for future chapter transitions during long lock-screen sessions.
        if (state.SpeechState is TtsPlaybackState.Generating or TtsPlaybackState.AdvancingChapter)
            EnsureBackgroundTask();
        else
            EndBackgroundTask();

        var info = new MPNowPlayingInfo
        {
            Title = string.IsNullOrWhiteSpace(reader.BookTitle) ? "Libzora" : reader.BookTitle,
            Artist = string.IsNullOrWhiteSpace(reader.BookAuthors) ? "Libzora" : reader.BookAuthors,
            AlbumTitle = string.IsNullOrWhiteSpace(state.Chapter) ? reader.BookTitle : state.Chapter,
            PlaybackRate = state.SpeechState == TtsPlaybackState.Playing ? 1.0 : 0.0,
            DefaultPlaybackRate = state.SpeechSpeed,
            MediaType = MPNowPlayingInfoMediaType.Audio,
            Artwork = ResolveArtwork(reader.BookCoverPath, paths)
        };
        if (state.SpeechSegmentCount > 0)
        {
            info.PlaybackDuration = state.SpeechSegmentCount;
            info.ElapsedPlaybackTime = Math.Clamp(state.SpeechSegmentIndex, 0, state.SpeechSegmentCount);
        }
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
    }

    public void Clear()
    {
        if (!_active && _backgroundTaskId == UIApplication.BackgroundTaskInvalid) return;
        _active = false;
        SetCommandsEnabled(false);
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null;
        EndBackgroundTask();
        IOSTtsAudioPlayer.DeactivateSessionIfIdle();
    }

    private MPMediaItemArtwork? ResolveArtwork(string? relativeOrAbsolutePath, AppPaths paths)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            _cachedCoverPath = null;
            _cachedArtwork = null;
            return null;
        }
        var fullPath = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(paths.Data, relativeOrAbsolutePath);
        if (string.Equals(_cachedCoverPath, fullPath, StringComparison.Ordinal) && _cachedArtwork is not null)
            return _cachedArtwork;

        if (!File.Exists(fullPath)) return null;
        var image = UIImage.FromFile(fullPath);
        if (image is null) return null;
        _cachedCoverPath = fullPath;
        _cachedArtwork = new MPMediaItemArtwork(image.Size, _ => image);
        return _cachedArtwork;
    }

    private void RegisterRemoteCommands()
    {
        var center = MPRemoteCommandCenter.Shared;
        _commandTargets.Add(center.PlayCommand.AddTarget(_ => RunOnReader(reader => reader.NativeResumeIfPaused())));
        _commandTargets.Add(center.PauseCommand.AddTarget(_ => RunOnReader(reader => reader.NativePauseIfPlaying())));
        _commandTargets.Add(center.TogglePlayPauseCommand.AddTarget(_ => RunOnReader(reader => reader.NativePauseOrResumeSpeech())));
        _commandTargets.Add(center.NextTrackCommand.AddTarget(_ => RunOnReader(reader => reader.NativeSkipSpeechSegment(1))));
        _commandTargets.Add(center.PreviousTrackCommand.AddTarget(_ => RunOnReader(reader => reader.NativeSkipSpeechSegment(-1))));
        SetCommandsEnabled(false);
    }

    private void SetCommandsEnabled(bool enabled)
    {
        var center = MPRemoteCommandCenter.Shared;
        center.PlayCommand.Enabled = enabled;
        center.PauseCommand.Enabled = enabled;
        center.TogglePlayPauseCommand.Enabled = enabled;
        center.NextTrackCommand.Enabled = enabled;
        center.PreviousTrackCommand.Enabled = enabled;
    }

    private MPRemoteCommandHandlerStatus RunOnReader(Action<ReaderPage> action)
    {
        if (_disposed) return MPRemoteCommandHandlerStatus.CommandFailed;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _readerAccessor() is not { } reader) return;
            action(reader);
        });
        return MPRemoteCommandHandlerStatus.Success;
    }

    private void EnsureBackgroundTask()
    {
        if (_backgroundTaskId != UIApplication.BackgroundTaskInvalid) return;
        _backgroundTaskId = UIApplication.SharedApplication.BeginBackgroundTask(
            "Libzora TTS Playback",
            EndBackgroundTask);
    }

    private void EndBackgroundTask()
    {
        var current = Interlocked.Exchange(ref _backgroundTaskId, UIApplication.BackgroundTaskInvalid);
        if (current != UIApplication.BackgroundTaskInvalid)
            UIApplication.SharedApplication.EndBackgroundTask(current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        var center = MPRemoteCommandCenter.Shared;
        foreach (var target in _commandTargets)
        {
            center.PlayCommand.RemoveTarget(target);
            center.PauseCommand.RemoveTarget(target);
            center.TogglePlayPauseCommand.RemoveTarget(target);
            center.NextTrackCommand.RemoveTarget(target);
            center.PreviousTrackCommand.RemoveTarget(target);
        }
        _commandTargets.Clear();
    }
}
#endif
