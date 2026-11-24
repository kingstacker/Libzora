namespace Libzora.Mobile;

/// <summary>Counts short foreground reading intervals and excludes background time.</summary>
internal sealed class ReadingActivityClock(DateTimeOffset startedAt)
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(120);
    private DateTimeOffset _last = startedAt;
    private bool _paused;

    public long CountUntil(DateTimeOffset now)
    {
        if (_paused) return 0;
        var elapsed = now - _last;
        if (elapsed.TotalSeconds < 1) return 0;
        _last = now;
        return elapsed <= IdleLimit ? (long)elapsed.TotalSeconds : 0;
    }

    public long Pause(DateTimeOffset now)
    {
        var seconds = CountUntil(now);
        _paused = true;
        return seconds;
    }

    public void Resume(DateTimeOffset now)
    {
        if (!_paused) return;
        _last = now;
        _paused = false;
    }
}

/// <summary>
/// Counts active TTS playback intervals without a foreground idle timeout so
/// hands-free and screen-off listening sessions accrue exact playback seconds.
/// </summary>
internal sealed class ListeningActivityClock
{
    private DateTimeOffset _last;
    private bool _playing;

    public bool IsPlaying => _playing;

    public long SetPlaying(bool playing, DateTimeOffset now)
    {
        if (playing == _playing) return 0;
        if (playing)
        {
            _last = now;
            _playing = true;
            return 0;
        }
        var seconds = CountUntil(now);
        _playing = false;
        return seconds;
    }

    public long CountUntil(DateTimeOffset now)
    {
        if (!_playing) return 0;
        var elapsed = now - _last;
        if (elapsed.TotalSeconds < 1) return 0;
        var whole = (long)elapsed.TotalSeconds;
        _last = _last.AddSeconds(whole);
        return whole;
    }
}
