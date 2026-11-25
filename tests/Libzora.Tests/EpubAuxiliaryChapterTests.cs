using System.IO.Compression;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class EpubAuxiliaryChapterTests
{
    [Fact]
    public async Task FootnoteContainerLeavesTheReadingSpine()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "notes.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="one" href="chapter-1.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="chapter-2.xhtml" media-type="application/xhtml+xml" />
                        <item id="notes" href="notes.xhtml" media-type="application/xhtml+xml" />
                        <item id="appendix" href="appendix.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine>
                        <itemref idref="one" />
                        <itemref idref="two" />
                        <itemref idref="notes" />
                        <itemref idref="appendix" />
                      </spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="chapter-1.xhtml">第一章</a></li>
                        <li><a href="chapter-2.xhtml">第二章</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter-1.xhtml", """
                    <html><body><h1>第一章</h1><p>正文<a href="notes.xhtml#n1">［1］</a>。</p></body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter-2.xhtml", """
                    <html><body><h1>第二章</h1><p>另一段正文。</p></body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/notes.xhtml", """
                    <html><body>
                      <p><a id="n1" href="chapter-1.xhtml#fn1">注1</a> 这是第一条注释。</p>
                      <p><a id="n2" href="chapter-1.xhtml#fn2">注2</a> 这是第二条注释。</p>
                      <p><a id="n3" href="chapter-2.xhtml#fn3">注3</a> 这是第三条注释。</p>
                    </body></html>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/appendix.xhtml", """
                    <html><body>
                      <h1>附录</h1>
                      <p>第一段普通附录文字。</p>
                      <p>第二段普通附录文字。</p>
                      <p>第三段普通附录文字。</p>
                    </body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('c', 64));

            Assert.Equal(4, document.Chapters.Count);
            Assert.Single(document.AuxiliaryChapters);
            Assert.EndsWith("notes.xhtml", document.AuxiliaryChapters[0], StringComparison.Ordinal);
            Assert.DoesNotContain(document.AuxiliaryChapters,
                path => path.EndsWith("appendix.xhtml", StringComparison.Ordinal));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ChaptersNamedByTheTocAlwaysRemainReadingContent()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "named-notes.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="content.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="body" href="body.xhtml" media-type="application/xhtml+xml" />
                        <item id="notes" href="notes.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="body" /><itemref idref="notes" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="body.xhtml">正文</a></li>
                        <li><a href="notes.xhtml">注释</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "body.xhtml", "<html><body><p>正文。</p></body></html>");
                TestHelpers.AddZipEntry(archive, "notes.xhtml", """
                    <html><body>
                      <p><a id="n1" href="body.xhtml#fn1">注1</a> 这是第一条注释。</p>
                      <p><a id="n2" href="body.xhtml#fn2">注2</a> 这是第二条注释。</p>
                      <p><a id="n3" href="body.xhtml#fn3">注3</a> 这是第三条注释。</p>
                    </body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('e', 64));

            // A publisher that lists the notes chapter in its TOC intends it
            // to be readable; it must stay in the reading spine.
            Assert.Empty(document.AuxiliaryChapters);
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
