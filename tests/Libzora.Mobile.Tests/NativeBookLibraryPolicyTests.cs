using System.Text.Json;
using Libzora.Core;
using Libzora.iOSHead;
using Xunit;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.Mobile.Tests;

public sealed class NativeBookLibraryPolicyTests
{
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void HistoricalNonFiniteSeriesIndicesCannotBreakLibrarySerialization(double value)
    {
        using var document = JsonDocument.Parse(Serialize(Data(("books", new[]
        {
            Data(("id", Guid.NewGuid()), ("seriesIndex", IOSNativeBookLibraryPolicy.FiniteSeriesIndex(value))),
            Data(("id", Guid.NewGuid()), ("seriesIndex", IOSNativeBookLibraryPolicy.FiniteSeriesIndex(2.5)))
        }))));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("books")[0].GetProperty("seriesIndex").ValueKind);
        Assert.Equal(2.5, document.RootElement.GetProperty("books")[1].GetProperty("seriesIndex").GetDouble());
    }

    [Fact]
    public void ChangingOrClearingSeriesCannotCarryItsOldPosition()
    {
        var book = new Book { Series = "A", SeriesIndex = 10 };
        IOSNativeBookLibraryPolicy.ApplySeries(book, "B", null, hasIndex: false);
        Assert.Equal("B", book.Series);
        Assert.Null(book.SeriesIndex);
        book.SeriesIndex = 3;
        IOSNativeBookLibraryPolicy.ApplySeries(book, " ", "3", hasIndex: true);
        Assert.Null(book.Series);
        Assert.Null(book.SeriesIndex);
    }

    [Fact]
    public void OlderFormsPreserveAnUnchangedSeriesPosition()
    {
        var book = new Book { Series = " Library Series ", SeriesIndex = 2.5 };
        IOSNativeBookLibraryPolicy.ApplySeries(book, "library series", null, hasIndex: false);
        Assert.Equal("library series", book.Series);
        Assert.Equal(2.5, book.SeriesIndex);
    }

    [Fact]
    public void ExplicitPositionCanBeUpdatedOrCleared()
    {
        var book = new Book { Series = "A", SeriesIndex = 10 };
        IOSNativeBookLibraryPolicy.ApplySeries(book, "B", "1.5", hasIndex: true);
        Assert.Equal(1.5, book.SeriesIndex);
        IOSNativeBookLibraryPolicy.ApplySeries(book, "B", "", hasIndex: true);
        Assert.Null(book.SeriesIndex);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("not a number")]
    public void InvalidSeriesPositionDoesNotMutateTheBook(string value)
    {
        var book = new Book { Series = "A", SeriesIndex = 10 };
        Assert.Throws<InvalidOperationException>(() => IOSNativeBookLibraryPolicy.ApplySeries(book, "B", value, true));
        Assert.Equal("A", book.Series);
        Assert.Equal(10, book.SeriesIndex);
    }

    [Fact]
    public async Task BusySyncGateCannotHoldTheLocalLibraryAndLeavesNoWaiter()
    {
        using var syncGate = new SemaphoreSlim(0, 1);
        var result = await IOSNativeBookLibraryPolicy.OptionalSyncStatusesAsync(async token =>
        {
            await syncGate.WaitAsync(token);
            try { return new Dictionary<Guid, BookSyncStatus>(); }
            finally { syncGate.Release(); }
        }, CancellationToken.None, TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result);
        syncGate.Release();
        Assert.Equal(1, syncGate.CurrentCount);
    }

    [Fact]
    public async Task AvailableBadgesAreReturnedWithoutChangingTheirValues()
    {
        var statuses = new Dictionary<Guid, BookSyncStatus> { [Guid.NewGuid()] = BookSyncStatus.Synced };
        var result = await IOSNativeBookLibraryPolicy.OptionalSyncStatusesAsync(
            _ => Task.FromResult<IReadOnlyDictionary<Guid, BookSyncStatus>>(statuses), CancellationToken.None);
        Assert.Same(statuses, result);
    }

    [Fact]
    public async Task MissingOptionalBaselineDoesNotFailLibraryLoading()
    {
        var result = await IOSNativeBookLibraryPolicy.OptionalSyncStatusesAsync(
            _ => Task.FromException<IReadOnlyDictionary<Guid, BookSyncStatus>>(new IOException("Missing baseline")), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task CancellingLibraryRequestIsNotSwallowedAsAnOptionalBadgeFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IOSNativeBookLibraryPolicy.OptionalSyncStatusesAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new Dictionary<Guid, BookSyncStatus>();
        }, cancellation.Token));
    }

    [Fact]
    public void PartiallySuccessfulImportMustReachTheNativeRefresh()
    {
        var result = new ImportBatchResult();
        result.Items.Add(new("first.epub", true, null, null, Added: true));
        result.Items.Add(new("broken.epub", false, "Invalid EPUB", null));
        Assert.False(IOSNativeBookLibraryPolicy.ImportFailedCompletely(result));
        result.Items.RemoveAt(0);
        Assert.True(IOSNativeBookLibraryPolicy.ImportFailedCompletely(result));
    }

    [Theory]
    [InlineData("EPUB", false)]
    [InlineData(".epub", false)]
    [InlineData("pdf", true)]
    public void NativeImportCannotAttachAnInaccessibleSecondFileOfTheSameFormat(string format, bool allowed)
    {
        var book = new Book { Files = [new BookFile { Format = "epub" }] };
        var conflict = new ImportBookConflict("volume.epub", book, "Series", "Author", format);
        Assert.Equal(allowed, IOSNativeBookLibraryPolicy.CanAddFormat(conflict));
    }
}
