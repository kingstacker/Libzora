using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class ReaderBookmarkAnchorTests
{
    [Fact]
    public void CapturesOriginalTextAtLogicalOffsetInsteadOfPageStart()
    {
        const string body = "Page starts here, but the actual saved bookmark begins further down in the original body text.";
        var offset = body.IndexOf("actual", StringComparison.Ordinal);
        var position = new ReaderContentPosition { TextOffset = offset, PageIndex = 4, RunY = 12 }.WithTextAnchor(body);
        Assert.StartsWith("actual", position.AnchorQuote);
        Assert.Equal(body[..offset], position.AnchorPrefix);
        Assert.Equal(offset, position.TextOffset);
        Assert.Equal(4, position.PageIndex);
        Assert.Equal(12, position.RunY);
    }

    [Fact]
    public void AnchorSlicesNeverSplitSurrogatePairs()
    {
        var body = new string('a', 47) + "😀" + new string('b', 90) + "😀";
        var utf8 = new UTF8Encoding(false, true);
        foreach (var offset in new[] { 0, 48, 88 })
        {
            var position = new ReaderContentPosition { TextOffset = offset }.WithTextAnchor(body);
            utf8.GetBytes(position.AnchorQuote!);
            utf8.GetBytes(position.AnchorPrefix!);
            utf8.GetBytes(position.AnchorSuffix!);
        }
    }

    [Fact]
    public void ImagePositionNeverAcquiresTextEvidence()
    {
        var position = new ReaderContentPosition { TextOffset = 0, ImageIndex = 2, AnchorQuote = "stale" }.WithTextAnchor("Next chapter text");
        Assert.Null(position.AnchorQuote);
        Assert.Null(position.AnchorPrefix);
        Assert.Null(position.AnchorSuffix);
        Assert.Equal(2, position.ImageIndex);
    }

    [Fact]
    public void VersionOneWithoutAnchorFieldsRemainsReadable()
    {
        var position = ReaderContentPositionJson.Deserialize("{\"Version\":1,\"TextOffset\":7,\"PageIndex\":3}");
        Assert.NotNull(position);
        Assert.Equal(7, position.TextOffset);
        Assert.Null(position.AnchorQuote);
        Assert.Null(ReaderContentPosition.Validate(position with { AnchorQuote = new string('x', 129) }));
    }

    [Fact]
    public async Task BookmarkOriginalAnchorSurvivesDatabaseReopen()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            const string body = "Previous source context. Original bookmark target has enough text to preserve its precise location.";
            var position = new ReaderContentPosition { TextOffset = body.IndexOf("Original", StringComparison.Ordinal) }.WithTextAnchor(body);
            var bookmark = new ReaderBookmark { BookId = Guid.NewGuid(), BookFileId = Guid.NewGuid(), ChapterPath = "chapter.xhtml",
                Quote = "Display-only page quote", ContentPosition = position };
            await reader.SaveBookmarkAsync(bookmark);
            var reopened = Assert.Single(await new ReaderDataService(paths).GetBookmarksAsync(bookmark.BookFileId));
            Assert.Equal(position, reopened.ContentPosition);
            Assert.Equal("Display-only page quote", reopened.Quote);
            Assert.StartsWith("Original", reopened.ContentPosition!.AnchorQuote);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
