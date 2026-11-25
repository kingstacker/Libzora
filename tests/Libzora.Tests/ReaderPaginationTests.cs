using Libzora.Core;

namespace Libzora.Tests;

public sealed class ReaderPaginationTests
{
    [Theory]
    [InlineData(true, false, -1)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, -1)]
    public void ClickDirectionMirrorsOnlyVerticalWriting(
        bool onLeft,
        bool verticalWriting,
        int expectedDirection)
    {
        Assert.Equal(
            expectedDirection,
            ReaderPaginationPolicy.GetClickDirection(onLeft, verticalWriting));
    }

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(-1, false, -1)]
    [InlineData(1, true, -1)]
    [InlineData(-1, true, 1)]
    [InlineData(0, true, 0)]
    public void VisualTurnDirectionMirrorsOnlyVerticalWriting(
        int navigationDirection,
        bool verticalWriting,
        int expectedDirection)
    {
        Assert.Equal(
            expectedDirection,
            ReaderPaginationPolicy.GetVisualTurnDirection(
                navigationDirection,
                verticalWriting));
    }

    [Theory]
    [InlineData(0, 0, 10, true)]
    [InlineData(1, 0, 10, true)]
    [InlineData(2, 0, 10, false)]
    [InlineData(10, 0, 10, false)]
    public void SelectionPagingRecognizesTheFirstCharacterBoundary(
        int offset,
        int pageStart,
        int pageEnd,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReaderSelectionPagingPolicy.IsAtPageStart(offset, pageStart, pageEnd));
    }

    [Theory]
    [InlineData(9, 0, 10, true)]
    [InlineData(10, 0, 10, true)]
    [InlineData(8, 0, 10, false)]
    [InlineData(0, 0, 10, false)]
    public void SelectionPagingRecognizesTheLastCharacterBoundary(
        int offset,
        int pageStart,
        int pageEnd,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReaderSelectionPagingPolicy.IsAtPageEnd(offset, pageStart, pageEnd));
    }

    [Theory]
    [InlineData(1, 100, 200, 101)]
    [InlineData(-1, 100, 200, 199)]
    [InlineData(0, 100, 200, -1)]
    [InlineData(1, -1, -1, -1)]
    public void SelectionPagingUsesTheTargetPageBoundaryAsTheCaretEndpoint(
        int direction,
        int pageStart,
        int pageEnd,
        int expectedEndpoint)
    {
        Assert.Equal(
            expectedEndpoint,
            ReaderSelectionPagingPolicy.GetCrossPageEndpoint(direction, pageStart, pageEnd));
    }

    [Fact]
    public void ColumnWidthSupportsSingleAndTwoPageSpreads()
    {
        Assert.Equal(952, ReaderPaginationDefaults.GetColumnWidth(1000), precision: 6);
        Assert.Equal(452, ReaderPaginationDefaults.GetColumnWidth(1000, pagesPerView: 2), precision: 6);
        Assert.Equal(452, ReaderPaginationDefaults.GetColumnWidth(1000, pagesPerView: 99), precision: 6);
    }

    [Fact]
    public void ColumnWidthUsesConfiguredReadingMargin()
    {
        Assert.Equal(
            864,
            ReaderPaginationDefaults.GetColumnWidth(1000, horizontalPadding: 68),
            precision: 6);
        Assert.Equal(
            364,
            ReaderPaginationDefaults.GetColumnWidth(1000, pagesPerView: 2, horizontalPadding: 68),
            precision: 6);

        var column = ReaderPaginationDefaults.GetColumnWidth(
            1000,
            pagesPerView: 2,
            horizontalPadding: 68);
        Assert.Equal(1000, column * 2 + 68 * 4, precision: 6);
    }

    [Fact]
    public void TwoPageColumnsAndInsetsFillExactlyOneViewport()
    {
        const double viewport = 1000;
        var column = ReaderPaginationDefaults.GetColumnWidth(viewport, pagesPerView: 2);

        var spreadWidth = ReaderPaginationDefaults.HorizontalPadding * 2
            + column * 2
            + ReaderPaginationDefaults.ColumnGap;

        Assert.Equal(viewport, spreadWidth, precision: 6);
    }

    [Fact]
    public void SnapStartsAtViewportOriginSoPageMarginsStayEven()
    {
        var snapped = ReaderPaginationPolicy.SnapScrollLeft(
            scrollLeft: 0,
            clientWidth: 1000,
            scrollWidth: 6000);

        Assert.Equal(0, snapped);
        Assert.False(ReaderPaginationPolicy.CanTurn(0, -1, 1000, 6000));
        Assert.True(ReaderPaginationPolicy.CanTurn(0, 1, 1000, 6000));
    }

    [Fact]
    public void SnapUsesTheScrollContainerWidth()
    {
        var snapped = ReaderPaginationPolicy.SnapScrollLeft(
            scrollLeft: 1130,
            clientWidth: 997.5,
            scrollWidth: 6000);

        Assert.Equal(997.5, snapped, precision: 6);
    }

    [Theory]
    [InlineData(-1, 1000, 0)]
    [InlineData(1, 1000, 2000)]
    public void TurnTargetAdvancesByOneViewport(int direction, double expectedCurrent, double expectedTarget)
    {
        var target = ReaderPaginationPolicy.GetTurnTarget(
            scrollLeft: expectedCurrent,
            direction,
            clientWidth: 1000,
            scrollWidth: 6000);

        Assert.Equal(expectedTarget, target, precision: 6);
    }

    [Fact]
    public void TurnTargetClampsAtTheLastFullPageBoundaryBeforeTrailingInset()
    {
        var target = ReaderPaginationPolicy.GetTurnTarget(
            scrollLeft: 4000,
            direction: 1,
            clientWidth: 1000,
            scrollWidth: 5024);

        Assert.Equal(4000, target);
        Assert.False(ReaderPaginationPolicy.CanTurn(4000, 1, 1000, 5024));
        Assert.True(ReaderPaginationPolicy.CanTurn(4000, -1, 1000, 5024));
    }

    [Fact]
    public void LastPageBoundaryRemovesTheTrailingBodyInset()
    {
        Assert.Equal(4000, ReaderPaginationPolicy.GetMaxScrollLeft(1000, 5000));
        Assert.Equal(4000, ReaderPaginationPolicy.GetLastPageScrollLeft(1000, 5024));
    }

    [Fact]
    public void InvalidViewportMetricsFailClosed()
    {
        Assert.Equal(0, ReaderPaginationPolicy.SnapScrollLeft(20, 0, 1000));
        Assert.False(ReaderPaginationPolicy.CanTurn(0, 1, double.NaN, 1000));
        Assert.Equal(0, ReaderPaginationPolicy.GetTurnTarget(0, 1, double.PositiveInfinity, 1000));
    }

    [Fact]
    public void BookProgressUsesPageCountsAcrossUnequalChapters()
    {
        int[] counts = [10, 90, 900];
        var position = ReaderBookProgressPolicy.ResolveBookPagePosition(counts, 1, 41, 90);
        Assert.Equal((51, 1000), position);

        var fraction = ReaderBookProgressPolicy.CalculateFraction(position.CurrentPage, position.TotalPages);
        Assert.Equal(0.051, fraction, precision: 6);

        var continuousStart = ReaderBookProgressPolicy.CalculateFraction(counts, 1, 0.0);
        Assert.Equal(11.0 / 1000.0, continuousStart, precision: 6);

        var continuousEnd = ReaderBookProgressPolicy.CalculateFraction(counts, 1, 1.0);
        Assert.Equal(100.0 / 1000.0, continuousEnd, precision: 6);
    }

    [Fact]
    public void BookProgressSeekMapsPercentToMatchingChapterAndPage()
    {
        int[] counts = [10, 90, 900];

        Assert.Equal((0, 0d), ReaderBookProgressPolicy.MapPercentToChapterTarget(counts, 0));
        Assert.Equal((2, 1d), ReaderBookProgressPolicy.MapPercentToChapterTarget(counts, 100));

        var (chapter50, ratio50) = ReaderBookProgressPolicy.MapPercentToChapterTarget(counts, 50);
        Assert.Equal(2, chapter50);
        var landedPage = (int)Math.Round(ratio50 * (counts[chapter50] - 1)) + 1;
        var resolved = ReaderBookProgressPolicy.ResolveBookPagePosition(counts, chapter50, landedPage, counts[chapter50]);
        Assert.Equal(500, resolved.CurrentPage);
    }

    [Fact]
    public void ExactChapterPageCountReplacesATooLargeEstimate()
    {
        int[] counts = [10, 90, 900];
        // The background estimate guessed 900 pages; the composed chapter has
        // 880. The last page must still read as the last page of the book.
        var position = ReaderBookProgressPolicy.ResolveBookPagePosition(
            counts, 2, 880, 880, chapterPageCountIsExact: true);
        Assert.Equal((980, 980), position);
        Assert.Equal(1.0, ReaderBookProgressPolicy.CalculateFraction(
            position.CurrentPage, position.TotalPages), precision: 6);

        // Before the layout is complete the estimate is only a lower bound.
        var partial = ReaderBookProgressPolicy.ResolveBookPagePosition(
            [10, 90, 900], 2, 1, 40);
        Assert.Equal((101, 1000), partial);
    }

    [Fact]
    public void AuxiliaryChaptersTakeNoPartInBookProgress()
    {
        // The last spine document is a footnotes container: zero pages.
        int[] counts = [10, 90, 0];
        var position = ReaderBookProgressPolicy.ResolveBookPagePosition(counts, 1, 90, 90);
        Assert.Equal((100, 100), position);
        Assert.Equal(1.0, ReaderBookProgressPolicy.CalculateFraction(
            position.CurrentPage, position.TotalPages), precision: 6);

        Assert.Equal(0.99,
            ReaderBookProgressPolicy.CalculateFraction(counts, 1, 88.0 / 89.0), precision: 6);

        var (chapter, ratio) = ReaderBookProgressPolicy.MapPercentToChapterTarget(counts, 100);
        Assert.Equal((1, 1d), (chapter, ratio));
        Assert.Equal((0, 0d), ReaderBookProgressPolicy.MapPercentToChapterTarget(counts, 0));

        var half = ReaderBookProgressPolicy.MapPercentToChapterTarget(counts, 50);
        Assert.Equal(1, half.ChapterIndex);
        Assert.InRange(half.ChapterRatio, 0, 1);
    }
}
