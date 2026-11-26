using Libzora.Mobile;

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
}
