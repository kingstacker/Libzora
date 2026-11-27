using Libzora.Core;

namespace Libzora.Tests;

public class MobileReadingActivityClockTests
{
    [Fact]
    public void BackgroundTimeIsExcludedEvenWhenThePauseIsShort()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var clock = new ReadingActivityClock(start);

        Assert.Equal(20, clock.Pause(start.AddSeconds(20)));
        Assert.Equal(0, clock.CountUntil(start.AddSeconds(50)));
        clock.Resume(start.AddSeconds(50));
        Assert.Equal(10, clock.CountUntil(start.AddSeconds(60)));
    }

    [Fact]
    public void LongIdleIntervalsKeepTheirActivePrefixAndRepeatedResumeDoesNotResetTheClock()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var clock = new ReadingActivityClock(start);

        Assert.Equal(120, clock.CountUntil(start.AddMinutes(3)));
        clock.Resume(start.AddMinutes(3).AddSeconds(5));
        Assert.Equal(10, clock.CountUntil(start.AddMinutes(3).AddSeconds(10)));
    }

    [Fact]
    public void FractionalSamplesAccumulateWithoutLosingTails()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var clock = new ReadingActivityClock(start);
        long total = 0;
        for (var i = 1; i <= 100; i++) total += clock.CountUntil(start.AddMilliseconds(i * 1200));
        Assert.Equal(120, total);
        Assert.Equal(0, clock.CountUntil(start.AddSeconds(120)));
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(121, 120)]
    [InlineData(3600, 120)]
    public void IdleLimitNeverDiscardsTheWholeReadingInterval(int elapsed, int expected)
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var clock = new ReadingActivityClock(start);
        Assert.Equal(expected, clock.Pause(start.AddSeconds(elapsed)));
        Assert.Equal(expected, clock.LastSlice!.SplitUtcDays().Sum(day => day.ActiveSeconds));
        Assert.Equal(0, clock.CountUntil(start.AddSeconds(elapsed + 60)));
    }

    [Fact]
    public void ListeningCountsScreenOffIntervalsAndKeepsItsUtcBreakdown()
    {
        var start = DateTimeOffset.Parse("2026-12-31T23:59:30Z");
        var clock = new ListeningActivityClock();
        clock.SetPlaying(true, start);
        Assert.Equal(60, clock.CountUntil(start.AddSeconds(60)));
        Assert.Equal(new long[] { 30, 30 }, clock.LastSlice!.SplitUtcDays().Select(day => day.ListeningSeconds));
        Assert.Equal(0, clock.SetPlaying(false, start.AddSeconds(60)));
        Assert.Equal(0, clock.CountUntil(start.AddHours(1)));
    }

    [Fact]
    public void AccumulatorSplitsUtcDaysAndRestoresOnFailure()
    {
        var start = DateTimeOffset.Parse("2026-12-31T23:59:40Z");
        var visual = new ReadingActivityClock(start);
        var listening = new ListeningActivityClock();
        var accumulator = new ReadingTimeAccumulator();

        accumulator.Add(visual.Pause(start.AddSeconds(30)), visual.LastSlice);
        listening.SetPlaying(true, start.AddSeconds(30));
        accumulator.Add(listening.SetPlaying(false, start.AddSeconds(50)), listening.LastSlice, listening: true);

        Assert.Equal(30, accumulator.PendingReadingSeconds);
        Assert.Equal(20, accumulator.PendingListeningSeconds);
        Assert.Equal(50, accumulator.PendingTotalSeconds);

        var (readingSeconds, listeningSeconds, days) = accumulator.Drain(start.AddSeconds(50));
        Assert.Equal(30, readingSeconds);
        Assert.Equal(20, listeningSeconds);
        Assert.Equal(0, accumulator.PendingTotalSeconds);
        Assert.Collection(days,
            day =>
            {
                Assert.Equal(new DateOnly(2026, 12, 31), day.Date);
                Assert.Equal(20, day.ActiveSeconds);
                Assert.Equal(0, day.ListeningSeconds);
            },
            day =>
            {
                Assert.Equal(new DateOnly(2027, 1, 1), day.Date);
                Assert.Equal(30, day.ActiveSeconds);
                Assert.Equal(20, day.ListeningSeconds);
            });

        accumulator.Restore(readingSeconds, listeningSeconds, days);
        Assert.Equal(30, accumulator.PendingReadingSeconds);
        Assert.Equal(20, accumulator.PendingListeningSeconds);
        var reDrained = accumulator.Drain(start.AddSeconds(60));
        Assert.Equal(days, reDrained.Days);
    }
}
