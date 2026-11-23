using System.IO.Compression;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class EpubCoverTests
{
    [Theory]
    [InlineData("<img src='images/cover.jpg'/>", true, 2)]
    [InlineData("<img src='images/copy.jpg'/>", true, 2)]
    [InlineData("<img src='images/different.jpg'/>", true, 3)]
    [InlineData("<img src='images/cover.jpg'/><p>Title and author</p>", true, 3)]
    [InlineData("<img src='images/cover.jpg'/>", false, 3)]
    public async Task NormalizesOnlyRepeatedDeclaredCoversAndPreservesCachedLocations(
        string secondBody, bool declaredCover, int expectedCount)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "covers.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf"/></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", $$"""
                    <package><metadata>{{(declaredCover ? "<meta name='cover' content='cover'/>" : "")}}</metadata>
                      <manifest>
                        <item id="first" href="titlepage.xhtml" media-type="application/xhtml+xml"/>
                        <item id="second" href="original.xhtml" media-type="application/xhtml+xml"/>
                        <item id="body" href="body.xhtml" media-type="application/xhtml+xml"/>
                        <item id="cover" href="images/cover.jpg" media-type="image/jpeg"/>
                        <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                      </manifest>
                      <spine toc="ncx"><itemref idref="first"/><itemref idref="second"/><itemref idref="body"/></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "titlepage.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Cover</title></head><body><div>
                      <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
                        <image xlink:href="images/cover.jpg" width="600" height="800"/>
                      </svg>
                    </div></body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "original.xhtml", $"<html><body>{secondBody}</body></html>");
                // The later illustration repeats the cover intentionally.
                TestHelpers.AddZipEntry(archive, "body.xhtml", """
                    <html><body><p id="body">正文</p><img src="images/cover.jpg"/></body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "toc.ncx", """
                    <ncx><navMap>
                      <navPoint><navLabel><text>Generated cover</text></navLabel><content src="titlepage.xhtml"/></navPoint>
                      <navPoint><navLabel><text>Original cover</text></navLabel><content src="original.xhtml#cover"/></navPoint>
                      <navPoint><navLabel><text>Body</text></navLabel><content src="body.xhtml#body"/></navPoint>
                    </navMap></ncx>
                    """);
                TestHelpers.AddZipEntry(archive, "images/cover.jpg", "cover-image");
                TestHelpers.AddZipEntry(archive, "images/copy.jpg", "cover-image");
                TestHelpers.AddZipEntry(archive, "images/different.jpg", "different-image");
            }
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var document = await service.PrepareAsync(epub, new string('a', 64));
            var cached = await service.PrepareAsync(epub, new string('a', 64));
            foreach (var prepared in new[] { document, cached })
            {
                Assert.Equal(expectedCount, prepared.Chapters.Count);
                Assert.Equal(expectedCount, prepared.ChapterTitles.Count);
                Assert.EndsWith("titlepage.xhtml", prepared.Chapters[0]);
                Assert.Contains("images/cover.jpg", await File.ReadAllTextAsync(prepared.Chapters[^1]));
                Assert.Equal(expectedCount - 1, prepared.Navigation.Single(item => item.Title == "Body").ChapterIndex);
                var body = ReaderResourcePolicy.RestoreProgress(prepared, Progress("body.xhtml") with
                {
                    ChapterIndex = 2, ContentPosition = new ReaderContentPosition { TextOffset = 1 },
                    Fragment = "body", ScrollPosition = 150
                });
                Assert.NotNull(body);
                Assert.Equal(expectedCount - 1, body.ChapterIndex);
                Assert.Equal(150, body.ScrollPosition);
                Assert.Equal(1, body.ContentPosition!.TextOffset);
                if (expectedCount == 2)
                {
                    Assert.Equal(3, prepared.OriginalChapters!.Count);
                    Assert.Equal(2, prepared.Navigation.Count);
                    var cover = ReaderResourcePolicy.RestoreProgress(prepared, Progress("original.xhtml") with
                    {
                        Fragment = "cover", ScrollPosition = 400
                    });
                    Assert.NotNull(cover);
                    Assert.Equal(0, cover.ChapterIndex);
                    Assert.Equal("titlepage.xhtml", cover.ChapterPath);
                    Assert.Equal(0, cover.ScrollPosition);
                    Assert.Null(cover.Fragment);
                }
                else Assert.Empty(prepared.ChapterAliases);
            }
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static ReaderProgressRow Progress(string path) =>
        new(Guid.NewGuid(), Guid.NewGuid(), path, null, 0, 0, 0, 0, DateTimeOffset.UtcNow);
}
