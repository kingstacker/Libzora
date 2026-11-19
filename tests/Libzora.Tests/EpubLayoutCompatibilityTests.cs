using Libzora.Layout;

namespace Libzora.Tests;

public sealed class EpubLayoutCompatibilityTests : IDisposable
{
    private readonly string _root = TestHelpers.CreateTempDirectory();

    [Theory]
    [InlineData("<p style='display:none'>不应出现</p>")]
    [InlineData("<p style='visibility:hidden'>不应出现</p>")]
    [InlineData("<p hidden='hidden'>不应出现</p>")]
    [InlineData("<div style='display:none'><p>嵌套不应出现</p></div>")]
    [InlineData("<span style='display:none'>不应出现</span>")]
    public void HiddenContentIsNeitherLaidOutNorSearchable(string markup)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter("<p>可见正文。</p>" + markup));
        Assert.Equal("可见正文。", content.BodyText);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("不应出现", StringComparison.Ordinal));
        Assert.Contains(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("可见正文", StringComparison.Ordinal));
    }

    [Fact]
    public void StylesheetDisplayNoneIsHonored()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p class='print-only'>隐藏正文。</p><p>显示正文。</p>",
            ".print-only { display: none; }"));
        Assert.Equal("显示正文。", content.BodyText);
    }

    [Fact]
    public void OrderedAndUnorderedListsGenerateMarkersWithoutTouchingOffsets()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<ol><li>第一项</li><li>第二项</li></ol>"
            + "<ul><li>圆点项</li></ul>"
            + "<ul style='list-style-type:square'><li>方形项</li></ul>"));
        var items = content.Blocks;
        Assert.Equal(BlockKind.ListItem, items[0].Kind);
        Assert.Equal("1. ", items[0].ListMarker);
        Assert.Equal("2. ", items[1].ListMarker);
        Assert.Equal("• ", items[2].ListMarker);
        Assert.Equal("■ ", items[3].ListMarker);
        Assert.All(items.Take(4), block => Assert.Equal(1, block.ListLevel));
        // Synthetic markers must not pollute the annotation/search offset stream.
        Assert.Equal("第一项第二项圆点项方形项", content.BodyText);
        Assert.All(items.Take(4), block =>
            Assert.Equal(-1, block.Items[0].TextStart));
    }

    [Fact]
    public void ListStartValueAndStyleNoneAreHonored()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<ol start='3'><li value='5'>甲</li><li>乙</li></ol>"
            + "<ol style='list-style: none'><li>丙</li></ol>"));
        Assert.Equal("5. ", content.Blocks[0].ListMarker);
        Assert.Equal("6. ", content.Blocks[1].ListMarker);
        Assert.Null(content.Blocks[2].ListMarker);
    }

    [Fact]
    public void NestedListsIncreaseIndentAndRestartNumbering()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<ol><li>外层<ol><li>内层甲</li><li>内层乙</li></ol></li></ol>"));
        var nested = content.Blocks.Where(block => block.ListLevel == 2).ToArray();
        Assert.Equal(new[] { "1. ", "2. " }, nested.Select(block => block.ListMarker));
    }

    [Fact]
    public void TableOfContentsListsDoNotGainGeneratedMarkersOrIndent()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<nav epub:type='toc'><ol><li><a href='x.xhtml'>第一章</a></li><li><a href='y.xhtml'>第二章</a></li></ol></nav>"));
        var items = content.Blocks.Where(block => block.Kind == BlockKind.ListItem).ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, block =>
        {
            Assert.Null(block.ListMarker);
            Assert.Equal(0, block.ListLevel);
        });
    }

    [Fact]
    public void HangingIndentAlignsWrappedListTextUnderTheItem()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<ol style='text-indent:0'><li>" + string.Concat(Enumerable.Repeat("列表正文需要换行对齐", 12)) + "</li></ol>"));
        using var engine = CreateEngine();
        var options = Options();
        var layout = engine.Compose(content, options);
        var block = content.Blocks[0];
        var marker = block.Items[0];
        Assert.Equal(-1, marker.TextStart);

        var runs = layout.Pages.SelectMany(page => page.Runs)
            .Where(run => run.TextStart >= 0).ToArray();
        var firstLine = runs.Min(run => run.OriginY);
        var secondLine = runs.Where(run => Math.Abs(run.OriginY - firstLine) > 0.1f)
            .Select(run => run.OriginY).DefaultIfEmpty(firstLine).Min();
        Assert.True(secondLine > firstLine, "The item needs at least two wrapped lines.");
        // The continuation starts after the marker width, not at the page edge.
        var page = layout.Pages.First();
        var firstLineLeft = runs.Where(run => Math.Abs(run.OriginY - firstLine) < 0.1f).Min(run => run.OriginX);
        var secondLineLeft = runs.Where(run => Math.Abs(run.OriginY - secondLine) < 0.1f).Min(run => run.OriginX);
        Assert.True(secondLineLeft > options.InsetHorizontal + options.BaseFontSize * 0.8f,
            $"wrapped text must stay indented under the marker, got {secondLineLeft}");
        Assert.InRange(Math.Abs(secondLineLeft - firstLineLeft), 0f, 2f);
        Assert.NotNull(page);
    }

    [Theory]
    [InlineData("text-decoration: underline", true, false)]
    [InlineData("text-decoration: line-through", false, true)]
    [InlineData("text-decoration: underline line-through", true, true)]
    public void CssTextDecorationAppliesToInlineText(string css, bool underline, bool strikeout)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p style='{css}'>修饰文本</p>"));
        var item = Assert.Single(content.Blocks.SelectMany(block => block.Items),
            entry => entry.Text == "修饰文本");
        Assert.Equal(underline, item.Style.Underline);
        Assert.Equal(strikeout, item.Style.Strikeout);
    }

    [Fact]
    public void TextDecorationNoneOverridesTheBlockDecoration()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p style='text-decoration: underline'>普通<em style='text-decoration: none'>取消</em></p>"));
        var items = content.Blocks.SelectMany(block => block.Items).ToArray();
        Assert.True(Assert.Single(items, item => item.Text == "普通").Style.Underline);
        Assert.False(Assert.Single(items, item => item.Text == "取消").Style.Underline);
    }

    [Fact]
    public void SubscriptAndCssVerticalAlignSubSitBelowTheBaseline()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p>H<sub>2</sub>O 与 x<span style='vertical-align: sub'>i</span></p>"));
        var items = content.Blocks.SelectMany(block => block.Items).ToArray();
        var two = Assert.Single(items, item => item.Text == "2");
        Assert.True(two.Style.Subscript);
        Assert.False(two.Style.Superscript);
        Assert.True(Assert.Single(items, item => item.Text == "i").Style.Subscript);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var runs = layout.Pages.SelectMany(page => page.Runs).ToArray();
            var baseRun = Assert.Single(runs, run => run.TextStart == 0 && run.TextLength == 1);
            var subRun = Assert.Single(runs, run => run.TextStart == 1 && run.TextLength == 1);
            Assert.True(subRun.FontSize < baseRun.FontSize);
            Assert.True(
                mode == TypesetWritingMode.HorizontalTb
                    ? subRun.OriginY > baseRun.OriginY
                    : subRun.OriginY >= baseRun.OriginY,
                $"subscript should sit below the baseline in {mode}");
        }
    }

    [Theory]
    [InlineData("uppercase", "Hello World", "HELLO WORLD")]
    [InlineData("lowercase", "Hello WORLD", "hello world")]
    [InlineData("capitalize", "hello world", "Hello World")]
    public void TextTransformChangesOnlyTheVisibleText(string transform, string source, string expected)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p style='text-transform:{transform}'>{source}</p>"));
        var item = Assert.Single(content.Blocks.SelectMany(block => block.Items),
            entry => entry.Text == expected);
        Assert.Equal(0, item.TextStart);
        Assert.Equal(source, content.BodyText);
    }

    [Fact]
    public void SmallCapsUppercasesLowercaseLettersAtASmallerSize()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p style='font-variant: small-caps'>aB</p>"));
        var items = content.Blocks.SelectMany(block => block.Items)
            .Where(item => item.Text.Length > 0 && item.TextStart >= 0).ToArray();
        var lower = Assert.Single(items, item => item.Text == "A");
        var upper = Assert.Single(items, item => item.Text == "B");
        Assert.True(lower.Style.FontSizeFactor < upper.Style.FontSizeFactor,
            $"lower={lower.Style.FontSizeFactor}, upper={upper.Style.FontSizeFactor}");
        Assert.Equal(0, lower.TextStart);
        Assert.Equal(1, upper.TextStart);
        Assert.Equal("aB", content.BodyText);
    }

    [Fact]
    public void LetterSpacingHintIsResolvedForTheTextRun()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p style='letter-spacing:0.2em'>字距</p>"));
        Assert.All(content.Blocks.SelectMany(block => block.Items)
                .Where(item => item.Text.Length > 0),
            item => Assert.Equal(0.2f, item.Style.LetterSpacingEm, 3));
    }

    [Theory]
    [InlineData("display:none; visibility:visible")]
    [InlineData("visibility:visible; display:none")]
    [InlineData("visibility:hidden; display:block")]
    [InlineData("display:block; visibility:hidden")]
    public void DisplayAndVisibilityCannotCancelEachOther(string style)
    {
        var hints = MicroCss.ParseInlineStyle(style);
        Assert.True(hints.Hidden);
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p style='{style}'>隐藏内容</p><p>正文</p>"));
        Assert.Equal("正文", content.BodyText);
    }

    [Theory]
    [InlineData("display:none", "display:block", "恢复正文")]
    [InlineData("visibility:hidden", "visibility:visible", "恢复正文")]
    [InlineData("display:none; visibility:hidden", "display:block", "")]
    [InlineData("display:none; visibility:hidden", "visibility:visible", "")]
    public void InlineOverridesApplyPerHiddenProperty(string stylesheet, string inline, string expected)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p class='screen-only' style='{inline}'>恢复正文</p>",
            $".screen-only {{ {stylesheet}; }}"));
        Assert.Equal(expected, content.BodyText);
    }

    [Theory]
    [InlineData("<div style='visibility:hidden'>隐藏<p style='visibility:visible'>恢复正文</p><p>仍隐藏</p></div>")]
    [InlineData("<p style='visibility:hidden'>隐藏<span style='visibility:visible'>恢复正文</span>仍隐藏</p>")]
    [InlineData("<div class='hidden'><p class='visible'>恢复正文</p><p>仍隐藏</p></div>")]
    public void VisibilityHiddenStillAllowsVisibleDescendants(string markup)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(markup,
            ".hidden { visibility:hidden; } .visible { visibility:visible; }"));
        Assert.Equal("恢复正文", content.BodyText);
        Assert.Equal("恢复正文", string.Concat(content.Blocks.SelectMany(block => block.Items).Select(item => item.Text)));
    }

    [Fact]
    public void DisplayNoneCannotBeOverriddenByADescendantsVisibility()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<div style='display:none'><p style='visibility:visible'>隐藏</p></div><p>正文</p>"));
        Assert.Equal("正文", content.BodyText);
    }

    [Theory]
    [InlineData("<tr style='display:none'><td>隐藏</td></tr>")]
    [InlineData("<tbody style='display:none'><tr><td>隐藏</td></tr></tbody>")]
    [InlineData("<thead hidden='hidden'><tr><th>隐藏</th></tr></thead>")]
    public void HiddenTableRowsAndGroupsAreExcluded(string hidden)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<table>" + hidden + "<tr><td>正文</td></tr></table>"));
        Assert.Equal("正文", content.BodyText);
        Assert.Single(Assert.Single(content.Blocks).Table!.Rows);
    }

    [Fact]
    public void TableVisibilityCanBeRestoredInsideAHiddenRow()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<table><tr style='visibility:hidden'><td>隐藏<span style='visibility:visible'>正文</span></td></tr></table>"));
        Assert.Equal("正文", content.BodyText);
    }

    [Theory]
    [InlineData("<ol><li style='list-style-type:none' value='5'>甲</li><li>乙</li></ol>", "6. ")]
    [InlineData("<ol start='3'><li style='list-style-type:none'>甲</li><li>乙</li></ol>", "4. ")]
    public void MarkerlessItemsStillAdvanceTheListCounter(string markup, string nextMarker)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(markup));
        Assert.Null(content.Blocks[0].ListMarker);
        Assert.Equal(nextMarker, content.Blocks[1].ListMarker);
        Assert.Equal("甲乙", content.BodyText);
    }

    [Theory]
    [InlineData("disc", TypesetWritingMode.HorizontalTb)]
    [InlineData("decimal", TypesetWritingMode.HorizontalTb)]
    [InlineData("disc", TypesetWritingMode.VerticalRl)]
    [InlineData("decimal", TypesetWritingMode.VerticalRl)]
    public void EveryGlyphOfASyntheticListMarkerKeepsANegativeOffset(string markerStyle, TypesetWritingMode mode)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            $"<ol start='12' style='list-style-type:{markerStyle}'><li>列表正文</li></ol>"));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(mode));
        var markers = layout.Pages.SelectMany(page => page.Runs).Where(run => run.Style.NoWrap).ToArray();
        Assert.NotEmpty(markers);
        Assert.All(markers, run => Assert.Equal(-1, run.TextStart));
        Assert.Equal("列表正文", content.BodyText);
    }

    [Theory]
    [InlineData("甲乙丙")]
    [InlineData("甲<span>乙</span>丙")]
    public void PublisherLetterSpacingChangesTheActualCjkGlyphPositions(string markup)
    {
        var normal = new XhtmlChapterLoader().Load(WriteChapter($"<p style='text-align:left;letter-spacing:0'>{markup}</p>"));
        var spaced = new XhtmlChapterLoader().Load(WriteChapter($"<p style='text-align:left;letter-spacing:1em'>{markup}</p>"));
        using var engine = CreateEngine();
        var options = Options();
        var normalLayout = engine.Compose(normal, options);
        var spacedLayout = engine.Compose(spaced, options);
        for (var offset = 0; offset < normal.BodyText.Length; offset++)
        {
            Assert.Equal(offset * options.BaseFontSize,
                GlyphX(spacedLayout, offset) - GlyphX(normalLayout, offset), 2);
        }
        Assert.Equal(normal.BodyText, spaced.BodyText);
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("unset")]
    public void ExplicitInheritedVisibilityOverridesTheElementsStylesheet(string value)
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p class='hidden' style='visibility:{value}'>恢复正文</p>"
            + $"<div class='hidden'><p class='visible' style='visibility:{value}'>仍隐藏</p></div>",
            ".hidden { visibility:hidden; } .visible { visibility:visible; }"));
        Assert.Equal("恢复正文", content.BodyText);
    }

    [Fact]
    public void AHiddenBodyCannotExposeItsChildren()
    {
        var path = Path.Combine(_root, "hidden-body.xhtml");
        File.WriteAllText(path, "<html><body style='display:none'><p>隐藏正文</p></body></html>");
        var content = new XhtmlChapterLoader().Load(path);
        Assert.Empty(content.BodyText);
        Assert.Empty(content.Blocks);
    }

    [Fact]
    public void PublisherTrackingIsOmittedOnlyAtTheActualLineEnd()
    {
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            "<p style='text-align:left;text-indent:0;letter-spacing:1em'>甲乙丙</p>"));
        using var engine = CreateEngine();
        var options = Options();
        var natural = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            "<p style='text-align:left;text-indent:0;letter-spacing:0'>甲乙丙</p>"));
        var naturalLayout = engine.Compose(natural, options);
        // Use the face's actual advances, not an assumed exact one-em glyph.
        var twoGlyphWidth = GlyphX(naturalLayout, 2) - GlyphX(naturalLayout, 0);
        options = Options(viewportWidth: twoGlyphWidth + options.BaseFontSize + options.InsetHorizontal * 2f + 0.05f);
        var layout = engine.Compose(content, options);
        var first = layout.GetCharRect(0, 0)!.Value;
        var second = layout.GetCharRect(0, 1)!.Value;
        var third = layout.GetCharRect(0, 2)!.Value;
        Assert.True(Math.Abs(first.Top - second.Top) < 0.01f,
            $"width={options.ContentWidth}, indent={content.Blocks[0].TextIndentEm}, natural={twoGlyphWidth}, first={first}, second={second}; "
            + string.Join("; ", layout.Pages.SelectMany(page => page.Runs).Select(run =>
                $"offset={run.TextStart}, length={run.TextLength}, x={run.OriginX}, y={run.OriginY}, advance={run.FlowAdvance}")));
        Assert.True(third.Top > second.Top);
        Assert.InRange(second.Right, options.InsetHorizontal, options.ViewportWidth - options.InsetHorizontal + 0.01f);
    }

    private static float GlyphX(ChapterLayout layout, int offset)
    {
        var run = Assert.Single(layout.Pages.SelectMany(page => page.Runs),
            run => run.TextStart >= 0 && offset >= run.TextStart && offset < run.TextStart + run.TextLength);
        var glyph = Array.FindIndex(run.Clusters, cluster => cluster == offset - run.TextStart);
        Assert.True(glyph >= 0);
        return run.OriginX + run.X[glyph];
    }

    [Theory]
    [InlineData("table", new[] { 2 })]
    [InlineData("democrat", new[] { 3, 4 })]
    [InlineData("project", new int[0])]
    [InlineData("Table", new[] { 2 })]
    public void HyphenatorHonorsUpstreamExceptions(string word, int[] expected)
    {
        Assert.Equal(expected, EnglishHyphenator.GetBreakpoints(word));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("an")]
    [InlineData("cat")]
    [InlineData("WORD")]
    [InlineData("well-known")]
    [InlineData("don't")]
    [InlineData("word123")]
    public void HyphenatorLeavesShortOrNonWordTokensAlone(string word)
    {
        Assert.Empty(EnglishHyphenator.GetBreakpoints(word));
    }

    [Fact]
    public void HyphenatorKeepsMinimaForLongEnglishWords()
    {
        var points = EnglishHyphenator.GetBreakpoints("hyphenation");
        Assert.NotEmpty(points);
        Assert.All(points, point =>
        {
            Assert.InRange(point, 2, "hyphenation".Length - 3);
        });
        Assert.Equal(points.Order(), points);
    }

    [Fact]
    public void EnglishHyphenationFillsALineWithoutChangingTheTextStream()
    {
        const string text = "The typography of antidisestablishmentarianism demands careful measurement.";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            "<p style='text-align:left;text-indent:0'>" + text + "</p>"));
        using var engine = CreateEngine();
        var options = Options(viewportWidth: 240f, hyphenateEnglish: true);
        var layout = engine.Compose(content, options);
        var hyphenated = layout.Pages.SelectMany(page => page.Runs)
            .Where(run => run.SyntheticHyphen).ToArray();
        Assert.NotEmpty(hyphenated);
        Assert.Equal(text, content.BodyText);
        Assert.All(layout.Pages.SelectMany(page => page.Runs).Where(run => run.TextStart >= 0),
            run => Assert.InRange(run.TextStart + run.TextLength, 0, content.BodyText.Length));
        // Every hyphenated run's visible hyphen is display-only: the source
        // character at the run's last offset is a letter, never a dash.
        Assert.All(hyphenated, run =>
            Assert.True(char.IsAsciiLetter(content.BodyText[run.TextStart + run.TextLength - 1])));
    }

    [Fact]
    public void DisablingEnglishHyphenationKeepsWholeWords()
    {
        const string text = "The typography of antidisestablishmentarianism demands careful measurement.";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            "<p style='text-align:left;text-indent:0'>" + text + "</p>"));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(viewportWidth: 240f));
        Assert.DoesNotContain(layout.Pages.SelectMany(page => page.Runs), run => run.SyntheticHyphen);
    }

    private string WriteChapter(string body, string css = "")
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xhtml");
        File.WriteAllText(path,
            "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>"
            + "<head><style>" + css + "</style></head><body>" + body + "</body></html>");
        return path;
    }

    private static TypesetLayoutOptions Options(
        TypesetWritingMode mode = TypesetWritingMode.HorizontalTb,
        float viewportWidth = 400f,
        bool hyphenateEnglish = false) => new()
    {
        WritingMode = mode,
        BaseFontSize = 17f,
        LineHeight = 1.8f,
        LetterSpacingEm = 0f,
        ViewportWidth = viewportWidth,
        ViewportHeight = 600f,
        InsetHorizontal = 24f,
        InsetVertical = 24f,
        ParagraphIndent = false,
        HyphenateEnglish = hyphenateEnglish,
    };

    private static TypesetEngine CreateEngine() => new(new TypesetFontLibrary(FindBundledFont()));

    private static string FindBundledFont()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Libzora.App", "Assets", "Fonts",
                "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Bundled font not found.");
    }

    public void Dispose() => TestHelpers.TryDelete(_root);
}
