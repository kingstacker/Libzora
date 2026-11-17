using Libzora.Core;

namespace Libzora.Tests;

public sealed class ReaderWordSelectionTests
{
    [Theory]
    [InlineData("中文阅读", 1, 1, 2)] // One CJK character, not the whole run.
    [InlineData("𠀀𠀁", 0, 0, 2)] // A supplementary CJK character keeps both UTF-16 units.
    [InlineData("𠀀𠀁", 2, 2, 4)]
    [InlineData("hello world", 7, 6, 11)]
    [InlineData("hello world", 5, 5, 6)] // A space selects only itself.
    [InlineData("don't stop", 2, 0, 5)]
    [InlineData("don't stop", 3, 0, 5)] // An internal apostrophe belongs to the word.
    [InlineData("well-known", 6, 0, 10)]
    [InlineData("well-known", 4, 0, 10)] // A hyphen joins the surrounding word runs.
    [InlineData("'hello'", 0, 0, 1)] // An opening quote is not an internal connector.
    [InlineData("'hello'", 6, 6, 7)]
    [InlineData("AT&T", 3, 0, 4)]
    [InlineData("20°C today", 0, 0, 4)]
    [InlineData("50%的读者", 1, 0, 3)]
    [InlineData("café", 3, 0, 4)] // Non-ASCII Latin letters stay in the word.
    [InlineData("éclair", 0, 0, 7)] // A combining mark stays with its base letter.
    [InlineData("😀x", 0, 0, 2)] // An emoji is one text element, not half a surrogate pair.
    [InlineData("，中文", 0, 0, 1)] // Chinese punctuation selects as one character.
    public void SelectsOneCharacterOrOneWholeWord(string text, int offset,
        int expectedStart, int expectedEnd)
    {
        Assert.Equal((expectedStart, expectedEnd), ReaderWordSelection.GetRange(text, offset));
    }

    [Fact]
    public void ClampsOffsetsAtEitherEndOfTheText()
    {
        Assert.Equal((0, 5), ReaderWordSelection.GetRange("hello", -1));
        Assert.Equal((0, 5), ReaderWordSelection.GetRange("hello", 5));
        Assert.Equal((0, 0), ReaderWordSelection.GetRange(null, 0));
        Assert.Equal((0, 0), ReaderWordSelection.GetRange(string.Empty, 4));
    }

    [Fact]
    public void PressingEitherSideOfAConnectorSelectsTheSameCompounds()
    {
        Assert.Equal((0, 10), ReaderWordSelection.GetRange("well-known", 1));
        Assert.Equal((0, 10), ReaderWordSelection.GetRange("well-known", 8));
        Assert.Equal((0, 4), ReaderWordSelection.GetRange("AT&T", 0));
    }
}
