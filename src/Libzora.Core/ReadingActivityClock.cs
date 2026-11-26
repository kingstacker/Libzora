namespace Libzora.Core;

/// <summary>Counts effective foreground intervals, retaining fractional seconds.</summary>
public sealed class ReadingActivityClock(DateTimeOffset startedAt)
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(120);
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

/// <summary>Counts real TTS playing intervals, including background or screen-off listening.</summary>
public sealed class ListeningActivityClock
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

/// <summary>
/// Thread-safe accumulator for pending visual/listening seconds and their
/// UTC day breakdown, supporting atomic drain and rollback on save failure.
/// </summary>
public sealed class ReadingTimeAccumulator
{
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, ReadingDashboardDay> _pendingDays = [];
    private long _readingSeconds;
    private long _listeningSeconds;

    public long PendingReadingSeconds
    {
        get { lock (_gate) return _readingSeconds; }
    }

    public long PendingListeningSeconds
    {
        get { lock (_gate) return _listeningSeconds; }
    }

    public long PendingTotalSeconds
    {
        get { lock (_gate) return _readingSeconds + _listeningSeconds; }
    }

    public void Add(long seconds, ReadingTimeSlice? slice, bool listening = false)
    {
        if (seconds <= 0) return;
        lock (_gate)
        {
            if (listening) _listeningSeconds += seconds;
            else _readingSeconds += seconds;
            if (slice is null) return;
            foreach (var day in slice.SplitUtcDays()) AddPendingDayLocked(day);
        }
    }

    public (long ReadingSeconds, long ListeningSeconds, IReadOnlyList<ReadingDashboardDay> Days) Drain(DateTimeOffset now)
    {
        lock (_gate)
        {
            var reading = _readingSeconds;
            var listening = _listeningSeconds;
            _readingSeconds = 0;
            _listeningSeconds = 0;
            var total = reading + listening;
            if (total <= 0)
            {
                _pendingDays.Clear();
                return (0, 0, []);
            }
            var missing = total - _pendingDays.Values.Sum(day => day.ActiveSeconds);
            var missingListening = listening - _pendingDays.Values.Sum(day => day.ListeningSeconds);
            if (missing > 0)
            {
                AddPendingDayLocked(new(DateOnly.FromDateTime(now.UtcDateTime), missing)
                {
                    ListeningSeconds = Math.Clamp(missingListening, 0, missing)
                });
            }
            var days = _pendingDays.Values.OrderBy(day => day.Date).ToArray();
            _pendingDays.Clear();
            return (reading, listening, days);
        }
    }

    public void Restore(long readingSeconds, long listeningSeconds, IReadOnlyList<ReadingDashboardDay> days)
    {
        if (readingSeconds <= 0 && listeningSeconds <= 0 && days.Count == 0) return;
        lock (_gate)
        {
            _readingSeconds += Math.Max(0, readingSeconds);
            _listeningSeconds += Math.Max(0, listeningSeconds);
            foreach (var day in days) AddPendingDayLocked(day);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _readingSeconds = 0;
            _listeningSeconds = 0;
            _pendingDays.Clear();
        }
    }

    private void AddPendingDayLocked(ReadingDashboardDay day)
    {
        var previous = _pendingDays.GetValueOrDefault(day.Date);
        _pendingDays[day.Date] = new(day.Date, (previous?.ActiveSeconds ?? 0) + day.ActiveSeconds)
        {
            ListeningSeconds = (previous?.ListeningSeconds ?? 0) + day.ListeningSeconds
        };
    }
}
