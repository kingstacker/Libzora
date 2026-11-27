using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed class ReadingStatsAccuracyTests
{
    [Fact]
    public async Task CompletionSurvivesRevisitingAndChangingFormatsWithoutCountingTwice()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var reader = new ReaderDataService(new AppPaths(root));
            await reader.InitializeAsync();
            var book = Guid.NewGuid(); var epub = Guid.NewGuid(); var pdf = Guid.NewGuid();
            await reader.AddReadingTimeAsync(book, epub, 120, 100, 10, 10);
            await reader.AddReadingTimeAsync(book, epub, 0, 10, 1, 10);
            await reader.AddReadingTimeAsync(book, pdf, 60, 0, 0, 10);
            var dashboard = await reader.GetReadingDashboardAsync();
            Assert.Equal(1, dashboard.BooksStarted);
            Assert.Equal(1, dashboard.BooksFinished);
            Assert.True(Assert.Single(dashboard.Books).HasFinished);
            Assert.Equal(180, dashboard.TotalSeconds);
            Assert.Equal(0, dashboard.AverageProgress);
            Assert.True(ReadingMedalCatalog.Build(dashboard)[1].IsUnlocked); // First-finish milestone, independent of UI language.
            await reader.ResetReadingDataAsync();
            await reader.InitializeAsync();
            Assert.Equal(0, (await reader.GetReadingDashboardAsync()).BooksFinished);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ManualCompletionDoesNotSeekOrInventTimeAndResetClearsIt()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var reader = new ReaderDataService(new AppPaths(root));
            await reader.InitializeAsync();
            var book = new Book { Id = Guid.NewGuid() };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub" };
            book.Files = [file];
            var point = new ReaderProgressRow(book.Id, file.Id, "body.xhtml", null, 1, 200, 20, 1, DateTimeOffset.UtcNow);
            await reader.SaveProgressAsync(point);
            await reader.MarkBookFinishedAsync(book);
            await reader.MarkBookFinishedAsync(book);
            Assert.Equal(point, await reader.GetProgressAsync(file.Id));
            var dashboard = await reader.GetReadingDashboardAsync();
            Assert.Equal(1, dashboard.BooksStarted);
            Assert.Equal(1, dashboard.BooksFinished);
            Assert.Equal(0, dashboard.TotalSeconds);
            Assert.All(dashboard.DailyReading, day => Assert.Equal(0, day.ActiveSeconds));
            await reader.ResetReadingDataAsync();
            await reader.InitializeAsync();
            Assert.Equal(0, (await reader.GetReadingDashboardAsync()).BooksFinished);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("2026-11-25T23:59:30Z", "2026-11-26T00:00:30Z")]
    [InlineData("2026-11-30T23:59:30Z", "2026-12-01T00:00:30Z")]
    [InlineData("2026-12-31T23:59:30Z", "2027-01-01T00:00:30Z")]
    public async Task EffectiveIntervalsAreSplitAcrossUtcBoundariesAndRecoveryKeepsDates(string start, string end)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var book = Guid.NewGuid(); var file = Guid.NewGuid();
            var from = DateTimeOffset.Parse(start); var to = DateTimeOffset.Parse(end);
            var days = new ReadingTimeSlice(from, to, Listening: true).SplitUtcDays();
            Assert.Equal(new long[] { 30, 30 }, days.Select(day => day.ActiveSeconds));
            var recovery = new ReaderReadingTimeRecoveryService(paths, reader);
            await recovery.SaveAsync(book, file, 60, 10, 1, 10, 60, new CancellationToken(true), days, to);
            var queued = await File.ReadAllTextAsync(Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName));
            await recovery.InitializeAsync();
            await File.WriteAllTextAsync(Path.Combine(paths.Data, ReaderReadingTimeRecoveryService.FileName), queued);
            await new ReaderReadingTimeRecoveryService(paths, reader).InitializeAsync();
            var dashboard = await reader.GetReadingDashboardAsync(dayCount: 2, throughDate: DateOnly.FromDateTime(to.UtcDateTime));
            Assert.Equal(new long[] { 30, 30 }, dashboard.DailyReading.Select(day => day.ActiveSeconds));
            Assert.Equal(new long[] { 30, 30 }, dashboard.DailyReading.Select(day => day.ListeningSeconds));
            Assert.Equal(60, dashboard.TotalSeconds);
            Assert.Equal(60, dashboard.ListeningSeconds);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void MultiDayListeningAndFractionalMidnightConserveSeconds()
    {
        var start = DateTimeOffset.Parse("2026-11-25T23:59:59.600Z");
        var end = start.AddSeconds(86401);
        var days = new ReadingTimeSlice(start, end, Listening: true).SplitUtcDays();
        Assert.Equal(86401, days.Sum(day => day.ActiveSeconds));
        Assert.Equal(86401, days.Sum(day => day.ListeningSeconds));
        Assert.All(days, day => Assert.InRange(day.ActiveSeconds, 1, 86400));
    }

    [Fact]
    public async Task InvalidBreakdownRollsBackTotalsAndSessions()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var reader = new ReaderDataService(new AppPaths(root));
            await reader.InitializeAsync();
            var book = Guid.NewGuid(); var file = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.AddReadingTimeAsync(book, file,
                60, 20, 1, 10, 30, [new(new DateOnly(2026, 11, 26), 40)], DateTimeOffset.UtcNow));
            Assert.Null(await reader.GetReadingStatsAsync(file));
            Assert.Equal(0, (await reader.GetReadingDashboardAsync()).TotalSeconds);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }
}
