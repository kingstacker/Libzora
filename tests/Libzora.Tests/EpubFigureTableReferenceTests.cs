using Libzora.Layout;
using SkiaSharp;
using Xunit;

namespace Libzora.Tests;

public sealed class EpubFigureTableReferenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fontPath;

    public EpubFigureTableReferenceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("libzora-figref-tests").FullName;
        _fontPath = FindBundledFont();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
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

        throw new InvalidOperationException("Bundled KingHwaOldSong font not found.");
    }

    private TypesetEngine CreateEngine() => new(new TypesetFontLibrary(_fontPath));

    private string WriteChapter(string body)
    {
        var path = Path.Combine(_tempDir, $"chapter-{Guid.NewGuid():N}.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>test</title></head>
              <body>{body}</body>
            </html>
            """);
        return path;
    }

    private string WritePng(string name = "fig.png")
    {
        var path = Path.Combine(_tempDir, name);
        using var surface = SKSurface.Create(new SKImageInfo(120, 80, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.SteelBlue);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return Path.GetFileName(path);
    }

    [Fact]
    public void LoaderLinksPlainFigureAndTableReferencesWhilePreservingExactBodyTextOffsets()
    {
        var img1 = WritePng("arch.png");
        var img2 = WritePng("perf.png");
        var path = WriteChapter($"""
            <p>第一章介绍系统整体架构，设计细节如图 1-1 所示。参数汇总请参见表 2.1（亦称表 2-1），性能曲线见 Figure 3a。</p>
            <p>图 1-1 展示了消息队列与存储引擎的交互流程。</p>
            <figure>
              <img src="{img1}" alt=""/>
              <figcaption>图 1-1 系统架构图</figcaption>
            </figure>
            <table>
              <caption>表 2.1 核心配置参数</caption>
              <tr><th>参数</th><th>默认值</th></tr>
              <tr><td>Timeout</td><td>30s</td></tr>
            </table>
            <div class="figure">
              <img src="{img2}" alt=""/>
              <h5>▲ Fig. 3A 吞吐量延迟曲线</h5>
            </div>
            """);

        var content = new XhtmlChapterLoader().Load(path);

        // Verify BodyText offset invariant across every text item in the chapter.
        foreach (var item in content.Blocks.SelectMany(b => b.Items).Where(i => !i.Ghost && i.TextStart >= 0))
        {
            Assert.Equal(item.Text, content.BodyText.Substring(item.TextStart, item.Text.Length));
        }

        var linkedItems = content.Blocks
            .SelectMany(b => b.Items)
            .Where(i => i.LinkHref is not null)
            .ToArray();

        Assert.Equal(5, linkedItems.Length);
        Assert.Equal("图 1-1", linkedItems[0].Text);
        Assert.EndsWith("#__libzora-figref-fig-1-1", linkedItems[0].LinkHref, StringComparison.Ordinal);
        Assert.Equal("图 1-1 系统架构图", linkedItems[0].FootnoteText);
        Assert.True(linkedItems[0].Style.Underline);

        Assert.Equal("表 2.1", linkedItems[1].Text);
        Assert.EndsWith("#__libzora-figref-tab-2-1", linkedItems[1].LinkHref, StringComparison.Ordinal);
        Assert.Equal("表 2.1 核心配置参数", linkedItems[1].FootnoteText);

        Assert.Equal("表 2-1", linkedItems[2].Text);
        Assert.EndsWith("#__libzora-figref-tab-2-1", linkedItems[2].LinkHref, StringComparison.Ordinal);

        Assert.Equal("Figure 3a", linkedItems[3].Text);
        Assert.EndsWith("#__libzora-figref-fig-3a", linkedItems[3].LinkHref, StringComparison.Ordinal);
        Assert.Equal("▲ Fig. 3A 吞吐量延迟曲线", linkedItems[3].FootnoteText);

        // A body sentence starting with "图 1-1 展示了..." is linked rather than mistaken for a caption.
        Assert.Equal("图 1-1", linkedItems[4].Text);
        Assert.EndsWith("#__libzora-figref-fig-1-1", linkedItems[4].LinkHref, StringComparison.Ordinal);

        // Captions themselves must never be turned into self-links.
        var captionBlocks = content.Blocks.Where(b => b.IsCaption).ToArray();
        Assert.Equal(3, captionBlocks.Length);
        Assert.All(captionBlocks, block => Assert.All(block.Items, item => Assert.Null(item.LinkHref)));
    }

    [Fact]
    public void LoaderRejectsCompoundWordsCountersAndUnmatchedNumbers()
    {
        var img = WritePng("fig1.png");
        var path = WriteChapter($"""
            <p>这代表 2 个部门的意图 1，企图 1 次完成列表 2，手表 2 与地图 1 均在图表 2 中，且发表 2 篇论文、图 100 个样本、表 2024 年数据、图 12 与未定义的图 9-9。</p>
            <p>真正的引用只有如图 1 所示和见表 2。</p>
            <figure>
              <img src="{img}" alt=""/>
              <figcaption>图 1 真实插图</figcaption>
            </figure>
            <p style="text-align:center">表 2 真实数据表</p>
            <table>
              <tr><th>项目</th><th>数值</th></tr>
              <tr><td>A</td><td>10</td></tr>
            </table>
            """);

        var content = new XhtmlChapterLoader().Load(path);
        var linkedItems = content.Blocks
            .SelectMany(b => b.Items)
            .Where(i => i.LinkHref is not null)
            .ToArray();

        Assert.Equal(2, linkedItems.Length);
        Assert.Equal("图 1", linkedItems[0].Text);
        Assert.EndsWith("#__libzora-figref-fig-1", linkedItems[0].LinkHref, StringComparison.Ordinal);
        Assert.Equal("表 2", linkedItems[1].Text);
        Assert.EndsWith("#__libzora-figref-tab-2", linkedItems[1].LinkHref, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void ComposedLayoutResolvesFigureAndTableFragmentsToExactTargetPages(TypesetWritingMode mode)
    {
        var img = WritePng("diagram.png");
        var filler = string.Concat(Enumerable.Range(0, 18).Select(i =>
            $"<p>第{i + 1}段填充正文，" + string.Concat(Enumerable.Repeat("春风吹过山林读书应当记住眼前这一行文字", 6)) + "</p>"));

        var path = WriteChapter($"""
            <p>开篇导读：请先参考后文的图 1-1 与表 1-1 了解整体结构。</p>
            {filler}
            <div>
              <img src="{img}" alt=""/>
              <p style="text-align:center">图 1-1 远端架构图</p>
            </div>
            {filler}
            <table>
              <caption>表 1-1 远端汇总表</caption>
              <tr><th>模块</th><th>职责</th></tr>
              <tr><td>Layout</td><td>排版与分页</td></tr>
            </table>
            """);

        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            WritingMode = mode,
            BaseFontSize = 17f,
            LineHeight = 1.8f,
            ViewportWidth = 360f,
            ViewportHeight = 420f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var layout = engine.Compose(content, options);

        // Page 0 has the hot zones for "图 1-1" and "表 1-1" with caption hover text.
        var page0Zones = layout.Pages[0].HotZones;
        Assert.Contains(page0Zones, z =>
            z.Kind == HotZoneKind.Link
            && z.Href.EndsWith("#__libzora-figref-fig-1-1", StringComparison.Ordinal)
            && z.FootnoteText == "图 1-1 远端架构图");
        Assert.Contains(page0Zones, z =>
            z.Kind == HotZoneKind.Link
            && z.Href.EndsWith("#__libzora-figref-tab-1-1", StringComparison.Ordinal)
            && z.FootnoteText == "表 1-1 远端汇总表");

        var figurePageIndex = layout.GetPageIndexOfFragment("__libzora-figref-fig-1-1");
        Assert.True(figurePageIndex > 0);
        Assert.NotEmpty(layout.Pages[figurePageIndex].Images);

        var tablePageIndex = layout.GetPageIndexOfFragment("__libzora-figref-tab-1-1");
        Assert.True(tablePageIndex > figurePageIndex);
    }
}
