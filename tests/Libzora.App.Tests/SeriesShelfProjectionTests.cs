using Libzora.Core;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed class SeriesShelfProjectionTests
{
    private sealed record Item(string Id, string Title, string Authors, string? Series, double? Index);

    private static BookSeriesCandidate Candidate(Item item) =>
        new(item.Id, item.Title, item.Authors, item.Series, item.Index);

    [Fact]
    public void CollapsesSeriesIntoOneCardAtTheFirstVolumePosition()
    {
        var items = new[]
        {
            new Item("a", "无关书籍", "作者甲", null, null),
            new Item("b", "第二部", "作者乙", "系列", 2),
            new Item("c", "第三部", "作者乙", "系列", 3),
            new Item("d", "第一部", "作者乙", "系列", 1)
        };
        var assigned = new Dictionary<string, (string? Key, string? Title, IReadOnlyList<Item>? Members)>();

        var shelf = SeriesShelfProjection.Project(
            items,
            enabled: true,
            Candidate,
            item => item.Id,
            (item, key, title, members) => assigned[item.Id] = (key, title, members),
            new Dictionary<string, string>());

        Assert.Equal(["a", "d"], shelf.Select(item => item.Id));
        Assert.Equal(["d", "b", "c"], assigned["d"].Members!.Select(item => item.Id));
        Assert.Null(assigned["a"].Key);
    }

    [Fact]
    public void PromotedVolumeBecomesFrontWithoutMovingTheStack()
    {
        var items = new[]
        {
            new Item("a", "无关书籍", "作者甲", null, null),
            new Item("b", "第二部", "作者乙", "系列", 2),
            new Item("c", "第三部", "作者乙", "系列", 3),
            new Item("d", "第一部", "作者乙", "系列", 1)
        };
        var fronts = new Dictionary<string, string> { ["系列\u001f"] = "c" };
        var assigned = new Dictionary<string, (string? Key, string? Title, IReadOnlyList<Item>? Members)>();

        var shelf = SeriesShelfProjection.Project(
            items,
            enabled: true,
            Candidate,
            item => item.Id,
            (item, key, title, members) => assigned[item.Id] = (key, title, members),
            fronts);

        Assert.Equal(["a", "c"], shelf.Select(item => item.Id));
        Assert.Equal(["c", "d", "b"], assigned["c"].Members!.Select(item => item.Id));
    }

    [Fact]
    public void DisabledStackingClearsPreviousMembership()
    {
        var item = new Item("a", "第一部", "作者乙", "系列", 1);
        var assigned = new List<(string? Key, IReadOnlyList<Item>? Members)>();

        var shelf = SeriesShelfProjection.Project(
            [item],
            enabled: false,
            Candidate,
            candidate => candidate.Id,
            (_, key, _, members) => assigned.Add((key, members)),
            new Dictionary<string, string>());

        var only = Assert.Single(shelf);
        Assert.Equal("a", only.Id);
        var call = Assert.Single(assigned);
        Assert.Null(call.Key);
        Assert.Null(call.Members);
    }
}
