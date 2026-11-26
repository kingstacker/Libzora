using System.Reflection;
using Libzora.Core;
using Libzora.Mobile.Views;
using Xunit;

namespace Libzora.Mobile.Tests;

public sealed partial class ReaderPersistenceRegressionTests
{
    [Fact]
    public Task LoadingReaderResumeDoesNotStartReadingClock() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        var clock = Field("_activityClock").GetValue(state.Reader)!;
        var count = clock.GetType().GetMethod("CountUntil")!;
        Set(state.Reader, "_ready", false);
        state.Reader.Resume();
        Assert.Equal(0L, count.Invoke(clock, [DateTimeOffset.UtcNow.AddSeconds(60)]));
        Set(state.Reader, "_ready", true);
        state.Reader.Resume();
        var last = (DateTimeOffset)clock.GetType().GetField("_last", PrivateInstance)!.GetValue(clock)!;
        Assert.Equal(60L, count.Invoke(clock, [last.AddSeconds(60)]));
    }, CancellationToken.None);

    [Fact]
    public Task FailedSaveRetainsTheOriginalListeningDatesForRetry() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        CaptureMidnightListening(state.Reader);
        await ExecuteAsync(state.Paths, "CREATE TRIGGER FailReadingTime BEFORE INSERT ON ReaderReadingStats BEGIN SELECT RAISE(FAIL,'blocked'); END;");
        await state.Reader.PauseAsync();
        Assert.Equal(60L, Field("_unsavedListeningSeconds").GetValue(state.Reader));
        await ExecuteAsync(state.Paths, "DROP TRIGGER FailReadingTime;");
        await state.Reader.PauseAsync();
        await state.Reader.PauseAsync();
        var dashboard = await state.Modules.Reader.Data.GetReadingDashboardAsync(dayCount: 2, throughDate: new(2026, 11, 26));
        Assert.Equal(60, dashboard.TotalSeconds);
        Assert.Equal(60, dashboard.ListeningSeconds);
        Assert.Equal(new long[] { 30, 30 }, dashboard.DailyReading.Select(day => day.ListeningSeconds));
    }, CancellationToken.None);

    [Fact]
    public Task DetachedHostDoesNotDiscardPendingTimeOrItsOriginalDates() => session.Session.Dispatch(async () =>
    {
        using var state = await ReaderState.CreateAsync();
        CaptureMidnightListening(state.Reader);
        Set(state.Reader, "_readerHost", null);
        await (Task)typeof(ReaderPage).GetMethod("SaveAndSyncOnExitAsync", PrivateInstance)!.Invoke(state.Reader, null)!;
        var dashboard = await state.Modules.Reader.Data.GetReadingDashboardAsync(dayCount: 2, throughDate: new(2026, 11, 26));
        Assert.Equal(60, dashboard.TotalSeconds);
        Assert.Equal(new long[] { 30, 30 }, dashboard.DailyReading.Select(day => day.ListeningSeconds));
        Assert.Equal(0L, Field("_unsavedListeningSeconds").GetValue(state.Reader));
    }, CancellationToken.None);

    private static void CaptureMidnightListening(ReaderPage reader)
    {
        var clock = Field("_listeningClock").GetValue(reader)!;
        var play = clock.GetType().GetMethod("SetPlaying")!;
        var start = DateTimeOffset.Parse("2026-11-25T23:59:30Z");
        play.Invoke(clock, [true, start]);
        typeof(ReaderPage).GetMethod("CountReadingTime", PrivateInstance)!.Invoke(reader, [start.AddSeconds(60)]);
        play.Invoke(clock, [false, start.AddSeconds(60)]);
    }
}
