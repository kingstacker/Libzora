using Libzora.Core;
using Xunit;

namespace Libzora.Tests;

public sealed class ReaderContinuousProgressTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0.5, 0.25)]
    [InlineData(0, 1, 0.5)]
    [InlineData(1, 0, 0.5)]
    [InlineData(1, 0.5, 0.75)]
    [InlineData(1, 1, 1)]
    public void OnePageChaptersHaveContinuousProgressWithoutPrematureCompletion(int chapter, double local, double expected) =>
        Assert.Equal(expected, ReaderBookProgressPolicy.CalculateScrollFraction([1, 1], chapter, local), 8);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(77.7)]
    [InlineData(100)]
    public void ScrollPercentageAndSeekAreInversesAndSkipNotes(double percent)
    {
        int[] counts = [3, 0, 10, 0];
        var target = ReaderBookProgressPolicy.MapScrollPercentToChapterTarget(counts, percent);
        Assert.True(counts[target.ChapterIndex] > 0);
        Assert.Equal(percent / 100, ReaderBookProgressPolicy.CalculateScrollFraction(counts, target.ChapterIndex, target.ChapterRatio), 8);
    }

    [Fact]
    public void ExactChapterBoundarySelectsTheNextReadingChapter()
    {
        Assert.Equal((1, 0d), ReaderBookProgressPolicy.MapScrollPercentToChapterTarget([1, 1], 50));
        Assert.Equal(0, ReaderBookProgressPolicy.CalculateScrollFraction([0, 0], 0, double.NaN));
        Assert.Equal((0, 0d), ReaderBookProgressPolicy.MapScrollPercentToChapterTarget([1, 1], double.NaN));
    }
}
