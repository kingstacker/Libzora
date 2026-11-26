using Libzora.Core;

namespace Libzora.Mobile;

/// <summary>Counts effective foreground intervals, retaining fractional seconds.</summary>
internal sealed class ReadingActivityClock(DateTimeOffset startedAt)
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(120);
    private DateTimeOffset _last = startedAt;
    private bool _paused;
    public ReadingTimeSlice? LastSlice { get; private set; }

    public long CountUntil(DateTimeOffset now)
    {
        LastSlice = null;
        if (_paused) return 0;
        var elapsed = now - _last;
        if (elapsed.TotalSeconds < 1) return 0;
        var whole = (long)Math.Min(elapsed.TotalSeconds, IdleLimit.TotalSeconds);
        var end = _last.AddSeconds(whole);
        LastSlice = new(_last, end);
        // Preserve the tail on ordinary sampling; discard only the idle gap.
        _last = elapsed > IdleLimit ? now : end;
        return whole;
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

/// <summary>Counts real TTS playing intervals, including screen-off listening.</summary>
internal sealed class ListeningActivityClock
{
    private DateTimeOffset _last;
    private bool _playing;
    public bool IsPlaying => _playing;
    public ReadingTimeSlice? LastSlice { get; private set; }

    public long SetPlaying(bool playing, DateTimeOffset now)
    {
        LastSlice = null;
        if (playing == _playing) return 0;
        if (playing) { _last = now; _playing = true; return 0; }
        var seconds = CountUntil(now);
        _playing = false;
        return seconds;
    }

    public long CountUntil(DateTimeOffset now)
    {
        LastSlice = null;
        if (!_playing) return 0;
        var elapsed = now - _last;
        if (elapsed.TotalSeconds < 1) return 0;
        var whole = (long)elapsed.TotalSeconds;
        var end = _last.AddSeconds(whole);
        LastSlice = new(_last, end, Listening: true);
        _last = end;
        return whole;
    }
}
