using System.Reflection;
using Libzora.Mobile.Views;
using Xunit;

namespace Libzora.Mobile.Tests;

public sealed class ReaderFootnoteLabelTests
{
    [Theory]
    [InlineData("[1]完整的说明。", "1", "完整的说明。")]
    [InlineData("〔1〕完整的说明。", "[1]", "完整的说明。")]
    [InlineData("［1］完整的说明。", "①", "完整的说明。")]
    [InlineData("（1）\n第一段。\n第二段。", "[1]", "第一段。\n第二段。")]
    [InlineData("註1：完整的说明。", "注1", "完整的说明。")]
    [InlineData("1月有31天。", "1", "1月有31天。")]
    [InlineData("3.14是圆周率的近似值。", "3", "3.14是圆周率的近似值。")]
    [InlineData("没有重复编号的说明。", "[1]", "没有重复编号的说明。")]
    public void PreviewStripsEquivalentLabelsButPreservesGenuineNumbers(string text, string marker, string expected)
    {
        var method = typeof(ReaderPage).GetMethod("StripFootnoteLabel", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, method.Invoke(null, [text, marker]));
    }
}
