using Libzora.Core;

namespace Libzora.Tests;

public sealed class BookSeriesGroupingTests
{
    private static BookSeriesCandidate Book(
        string id,
        string title,
        string authors = "作者",
        string? series = null,
        double? seriesIndex = null,
        params string[] fileTitles) =>
        new(id, title, authors, series, seriesIndex,
            fileTitles.Length == 0 ? null : fileTitles);

    [Fact]
    public void GroupsExplicitSeriesAndKeepsPositionOrder()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("b3", "第三部", series: "银河", seriesIndex: 3),
            Book("b1", "第一部", series: "银河", seriesIndex: 1),
            Book("other", "无关书籍"),
            Book("b2", "第二部", series: "银河", seriesIndex: 2)
        ]);

        var stack = Assert.Single(stacks);
        Assert.Equal("银河", stack.Title);
        Assert.Equal(["b1", "b2", "b3"], stack.Members.Select(member => member.Id));
    }

    [Fact]
    public void InfersSeriesFromTitleVolumeMarkers()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("a", "星海 第2卷"),
            Book("b", "星海 第1卷"),
            Book("c", "星海")
        ]);

        var stack = Assert.Single(stacks);
        Assert.Equal(["b", "a"], stack.Members.Select(member => member.Id));
    }

    [Fact]
    public void AcceptsEnglishAndFileVolumeMarkers()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("a", "Saga Book 2"),
            Book("b", "Saga Vol. 1"),
            Book("c", "另一个故事", fileTitles: ["Saga Volume 3"])
        ]);

        var stack = Assert.Single(stacks);
        Assert.Equal(["b", "a", "c"], stack.Members.Select(member => member.Id));
    }

    [Fact]
    public void BareNumbersAreNotVolumeMarkers()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("a", "1984"),
            Book("b", "2001"),
            Book("c", "沙丘 2")
        ]);

        Assert.Empty(stacks);
    }

    [Fact]
    public void RequiresMoreThanOneDistinctIndexWithoutSeriesMetadata()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("a", "同一书名 Vol. 1"),
            Book("b", "同一书名 Vol. 1")
        ]);

        Assert.Empty(stacks);
    }

    [Fact]
    public void ExplicitSeriesMayCombineAuthorsButInferenceStaysAuthorScoped()
    {
        var explicitStacks = BookSeriesGrouping.Stacks([
            Book("a", "合集一", authors: "甲", series: "合集", seriesIndex: 1),
            Book("b", "合集二", authors: "乙", series: "合集", seriesIndex: 2)
        ]);
        Assert.Single(explicitStacks);

        var inferredStacks = BookSeriesGrouping.Stacks([
            Book("a", "同名 Vol. 1", authors: "甲"),
            Book("b", "同名 Vol. 2", authors: "乙")
        ]);
        Assert.Empty(inferredStacks);
    }

    [Fact]
    public void InferredVolumeJoinsExplicitSeriesForKnownAuthor()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("a", "系列一", authors: "甲", series: "系列", seriesIndex: 1),
            Book("b", "系列二", authors: "乙", series: "系列", seriesIndex: 2),
            Book("c", "系列 Vol. 3", authors: "甲")
        ]);

        var stack = Assert.Single(stacks);
        Assert.Equal(["a", "b", "c"], stack.Members.Select(member => member.Id));
    }

    [Fact]
    public void SingleExplicitVolumeIsNotStackable()
    {
        var stacks = BookSeriesGrouping.Stacks([
            Book("a", "独卷", series: "独卷系列", seriesIndex: 1)
        ]);

        Assert.Empty(stacks);
    }
}
