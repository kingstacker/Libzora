using Libzora;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;
using Xunit;

namespace Libzora.Tests;

public sealed class ReaderTablePreviewTests
{
    [Fact]
    public void RenderProducesWidePngForTableBlock()
    {
        var directory = Directory.CreateTempSubdirectory("libzora-table-preview-tests").FullName;
        try
        {
            var chapterPath = Path.Combine(directory, "chapter.xhtml");
            File.WriteAllText(chapterPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                <table>
                  <tr><th>名称</th><th>数量</th><th>说明</th></tr>
                  <tr><td>甲</td><td>1</td><td>第一行说明文字</td></tr>
                  <tr><td>乙</td><td>2</td><td>第二行说明文字</td></tr>
                </table>
                </body></html>
                """);
            var chapter = new XhtmlChapterLoader().Load(chapterPath);
            var table = Assert.Single(chapter.Blocks, block => block.Kind == BlockKind.Table);
            var fonts = new TypesetFontLibrary(FindBundledFont());
            try
            {
                var settings = new ReaderLayoutSettings(FontScale: 1.0, MaxWidth: 900);
                var path = ReaderTablePreviewRenderer.Render(
                    table, settings, 1000f, TypesetPaintTheme.Paper, fonts, null, CancellationToken.None);
                Assert.NotNull(path);
                try
                {
                    using var codec = SKCodec.Create(path);
                    Assert.NotNull(codec);
                    // Rendered at 2× so the zoomed table stays crisp.
                    Assert.InRange(codec!.Info.Width, 1990, 2010);
                    Assert.True(codec.Info.Height > 100);
                }
                finally
                {
                    File.Delete(path);
                }
            }
            finally
            {
                fonts.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindBundledFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src", "Libzora.App", "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Bundled KingHwaOldSong font not found; the table preview test requires the repo font assets.");
    }
}
