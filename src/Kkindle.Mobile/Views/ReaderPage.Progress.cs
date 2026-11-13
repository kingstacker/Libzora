using Avalonia.Threading;
using Kkindle.ApplicationServices;
using Kkindle.Core;

namespace Kkindle.Mobile.Views;

public partial class ReaderPage
{
    /// <summary>
    /// Active reading time needed before the measured pace is trusted. Below
    /// this the footer says the speed is still being learned.
    /// </summary>
    private const long PaceLearningSeconds = 120;
    private const double PaceLearningProgress = 1;
    private static readonly TimeSpan PaceRefreshInterval = TimeSpan.FromSeconds(30);

    private bool _readingStatsRequested;
    private bool _readingStatsLoading;
    private ReaderReadingStats? _readingStats;
    private DispatcherTimer? _readingStatsTimer;
    private bool _hasPdfOutline;

    private string ProgressDetail(string pages)
    {
        switch (_shell.Preferences.Current.ReaderProgressDisplay)
        {
            case MobileReaderProgressDisplay.Chapters:
                return ChapterPosition(pages);
            // Retired stored values are mapped to BookTimeRemaining when the
            // preferences load, so only the whole-book estimate remains here.
            case MobileReaderProgressDisplay.BookTimeRemaining:
                return RemainingTimeLabel();
            default:
                return pages;
        }
    }

    private string ChapterPosition(string pages)
    {
        if (_isPdf)
        {
            var selected = CurrentTocIndex();
            return _hasPdfOutline && selected >= 0
                ? $"第 {selected + 1} / {_tocEntries.Count} 章" : pages;
        }
        return _epubDocument is { } document
            ? $"第 {_chapterIndex + 1} / {document.Chapters.Count} 章" : pages;
    }

    /// <summary>
    /// Estimates the whole book from the reader's own measured pace instead of
    /// a fixed characters-per-minute constant. The first two minutes only say
    /// the speed is being learned.
    /// </summary>
    private string RemainingTimeLabel()
    {
        EnsureReadingStatsLoaded();
        var progress = Math.Clamp(_overallProgress * 100, 0, 100);
        if (_readingStats is not { CumulativeSeconds: >= PaceLearningSeconds } stats
            || !double.IsFinite(stats.ProgressPercent)
            || stats.ProgressPercent < PaceLearningProgress)
            return "阅读速度估计中";

        var measured = Math.Clamp(stats.ProgressPercent, PaceLearningProgress, 100);
        var secondsPerPercent = stats.CumulativeSeconds / measured;
        var seconds = secondsPerPercent * Math.Max(0, 100 - progress);
        return $"全书剩余约 {ReaderReadingTimeEstimate.FormatDuration(seconds)}";
    }

    private void EnsureReadingStatsLoaded(bool force = false)
    {
        if (_readingStatsLoading || _bookFile is null || _disposed) return;
        if (!force && _readingStatsRequested) return;
        _readingStatsRequested = true;
        _readingStatsLoading = true;
        _ = LoadReadingStatsAsync(_lifetime.Token);
    }

    private async Task LoadReadingStatsAsync(CancellationToken token)
    {
        try
        {
            var stats = await _modules.Reader.Data.GetReadingStatsAsync(_bookFile!.Id, token);
            if (_disposed || token.IsCancellationRequested) return;
            _readingStats = stats;
            RefreshPositionLabels();
            if (HasLearnedReadingPace(stats))
                StopReadingStatsTimer();
            else
                StartReadingStatsTimer();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The time label is cosmetic; a missing or unreadable statistics
            // row must only keep it in the learning state.
        }
        finally
        {
            _readingStatsLoading = false;
        }
    }

    private static bool HasLearnedReadingPace(ReaderReadingStats? stats) =>
        stats is { CumulativeSeconds: >= PaceLearningSeconds }
        && double.IsFinite(stats.ProgressPercent)
        && stats.ProgressPercent >= PaceLearningProgress;

    private void StartReadingStatsTimer()
    {
        if (_readingStatsTimer is not null || _disposed) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PaceRefreshInterval
        };
        timer.Tick += (_, _) =>
        {
            if (_disposed || _shell.Preferences.Current.ReaderProgressDisplay
                != MobileReaderProgressDisplay.BookTimeRemaining)
            {
                StopReadingStatsTimer();
                return;
            }
            EnsureReadingStatsLoaded(force: true);
        };
        _readingStatsTimer = timer;
        timer.Start();
    }

    private void StopReadingStatsTimer()
    {
        _readingStatsTimer?.Stop();
        _readingStatsTimer = null;
    }

    private void RefreshPositionLabels()
    {
        if (_isPdf) UpdatePdfLabels();
        else UpdateEpubLabels();
    }
}
