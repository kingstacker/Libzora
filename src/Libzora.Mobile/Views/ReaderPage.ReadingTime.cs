using Libzora.Core;

namespace Libzora.Mobile.Views;

public partial class ReaderPage
{
    private readonly SemaphoreSlim _progressSaveGate = new(1, 1);
    private readonly Dictionary<DateOnly, ReadingDashboardDay> _pendingReadingDays = [];

    private void AddCountedReadingTime(long seconds, ReadingTimeSlice? slice, bool listening = false)
    {
        if (seconds <= 0) return;
        if (listening) Interlocked.Add(ref _unsavedListeningSeconds, seconds);
        else Interlocked.Add(ref _unsavedSeconds, seconds);
        if (slice is null) return;
        foreach (var day in slice.SplitUtcDays()) AddPendingDay(day);
    }

    private void AddPendingDay(ReadingDashboardDay day)
    {
        var previous = _pendingReadingDays.GetValueOrDefault(day.Date);
        _pendingReadingDays[day.Date] = new(day.Date, (previous?.ActiveSeconds ?? 0) + day.ActiveSeconds)
        { ListeningSeconds = (previous?.ListeningSeconds ?? 0) + day.ListeningSeconds };
    }

    private IReadOnlyList<ReadingDashboardDay> DrainReadingDays(long seconds, long listening, DateTimeOffset now)
    {
        // A legacy/injected pending count may have no interval. Keep its old
        // save-date semantics; never invent a continuous span across pauses.
        var missing = seconds - _pendingReadingDays.Values.Sum(day => day.ActiveSeconds);
        var missingListening = listening - _pendingReadingDays.Values.Sum(day => day.ListeningSeconds);
        if (missing > 0)
            AddPendingDay(new(DateOnly.FromDateTime(now.UtcDateTime), missing) { ListeningSeconds = Math.Clamp(missingListening, 0, missing) });
        var result = _pendingReadingDays.Values.OrderBy(day => day.Date).ToArray();
        _pendingReadingDays.Clear();
        return result;
    }

    private void RestoreReadingTime(long reading, long listening, IReadOnlyList<ReadingDashboardDay> days)
    {
        Interlocked.Add(ref _unsavedSeconds, reading);
        Interlocked.Add(ref _unsavedListeningSeconds, listening);
        foreach (var day in days) AddPendingDay(day);
    }

    private async Task PersistPendingReadingTimeAsync()
    {
        if (_bookFile is not { } file) return;
        var reading = Interlocked.Exchange(ref _unsavedSeconds, 0);
        var listening = Interlocked.Exchange(ref _unsavedListeningSeconds, 0);
        if (reading + listening <= 0) return;
        var now = DateTimeOffset.UtcNow;
        var days = DrainReadingDays(reading + listening, listening, now);
        var progress = _footnoteProgressSuppressed ? _footnoteBodyProgress : _lastSavedProgress;
        try
        {
            await _modules.Reader.ReadingTimeRecovery.SaveAsync(_book.Id, file.Id, reading + listening,
                progress?.ProgressPercent ?? _overallProgress * 100,
                progress?.ChapterIndex ?? _chapterIndex, _isPdf ? _pdfPageCount : _epubDocument?.Chapters.Count ?? 0,
                listening, CancellationToken.None, days, now);
        }
        catch { RestoreReadingTime(reading, listening, days); }
    }
}
