using Kkindle.Core;
using Xunit;

namespace Kkindle.Tests;

public sealed class ReaderReadingTimeEstimateTests
{
    [Fact]
    public void CountsCjkCharactersAndOtherWordsAtTheirOwnRates()
    {
        var text = new string('中', 500) + " " + string.Join(" ", Enumerable.Repeat("word", 250));
        Assert.Equal(120, ReaderReadingTimeEstimate.EstimateSeconds(text.AsSpan()), precision: 6);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(" \r\n，。！😀", 0)]
    [InlineData("中 文\n，。", 0.24)]
    [InlineData("𠀀𠀁", 0.24)]
    [InlineData("don't stop", 0.48)]
    [InlineData("e\u0301clair", 0.24)]
    public void IgnoresPunctuationAndCountsUnicodeText(string text, double seconds) =>
        Assert.Equal(seconds, ReaderReadingTimeEstimate.EstimateSeconds(text.AsSpan()), precision: 6);

    [Fact]
    public void RemainingEstimateStartsAtTheActualTextOffset()
    {
        var text = new string('中', 500);
        Assert.Equal(30, ReaderReadingTimeEstimate.EstimateSeconds(text.AsSpan(250)), precision: 6);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1.0, "—")]
    [InlineData(0.0, "0 分钟")]
    [InlineData(59.9, "不足 1 分钟")]
    [InlineData(60.0, "1 分钟")]
    [InlineData(61.0, "2 分钟")]
    [InlineData(3600.0, "1 小时")]
    [InlineData(3601.0, "1 小时 1 分钟")]
    public void RoundsRemainingTimeUpWithoutClaimingMissingTextIsComplete(double? seconds, string expected) =>
        Assert.Equal(expected, ReaderReadingTimeEstimate.FormatDuration(seconds));

    [Fact]
    public void InvalidEstimatesRemainUnavailable()
    {
        Assert.Equal("—", ReaderReadingTimeEstimate.FormatDuration(double.NaN));
        Assert.Equal("—", ReaderReadingTimeEstimate.FormatDuration(double.PositiveInfinity));
    }
}
