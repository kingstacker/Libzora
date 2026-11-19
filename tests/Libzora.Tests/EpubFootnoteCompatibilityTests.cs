using Libzora.Infrastructure;
using Libzora.Layout;

namespace Libzora.Tests;

public sealed class EpubFootnoteCompatibilityTests : IDisposable
{
    private readonly string _root = TestHelpers.CreateTempDirectory();

    public static TheoryData<string, string> LegacyReferences => new()
    {
        { "<span class='math-super'><a id='z1' href='notes.xhtml#bz1'>1</a></span>", "bz1" },
        { "<a id='z1' href='notes.xhtml#bz1'>[1]</a>", "bz1" },
        { "<a id='A_1' href='notes.xhtml#B_1'>[1]</a>", "B_1" },
        { "<a id='commentRef_1' href='notes.xhtml#comment_1'>[1]</a>", "comment_1" },
        { "<span style='vertical-align:super'><a href='notes.xhtml#target1'>1</a></span>", "target1" },
        { "<a class='raised' href='notes.xhtml#target1'>[1]</a>", "target1" },
        { "<span epub:type='noteref'><a href='notes.xhtml#target1'>①</a></span>", "target1" },
        { "<span role='doc-noteref'><a href='notes.xhtml#target1'>注一</a></span>", "target1" },
        { "<sup><a href='notes.xhtml#target1'>①</a></sup>", "target1" },
        { "<sup><a href='notes.xhtml#target1'>＊＊</a></sup>", "target1" },
        { "<a name='w1'></a><a href='notes.xhtml#m1'>[1]</a>", "m1" },
    };

    [Fact]
    public void LoaderRecognizesGutenbergLinknoteReferences()
    {
        var chapter = WriteChapter("body.xhtml", """
            <p>正文<a href="notes.xhtml#linknote-1" id="linknoteref-1" class="pginternal"><small>1</small></a>
            和<a href="notes.xhtml#linknote-10" id="linknoteref-10" class="pginternal">10</a>。</p>
            <p>另一个<a href="notes.xhtml#linknote-1" id="linknoteref-3" class="pginternal">3</a>。</p>
            """);
        var content = new XhtmlChapterLoader().Load(chapter);
        var markers = content.Blocks.SelectMany(block => block.Items)
            .Where(item => item.Kind == InlineKind.FootnoteMarker).ToArray();
        Assert.Equal(3, markers.Length);
        Assert.Equal(
            new[] { "#linknote-1", "#linknote-10", "#linknote-1" },
            markers.Select(marker =>
            {
                var href = marker.FootnoteHref!;
                return href[href.IndexOf('#')..];
            }));
    }

    [Fact]
    public void LoaderKeepsGutenbergNotesSectionBacklinksAsPlainLinks()
    {
        var chapter = WriteChapter("notes.xhtml", """
            <h2 id="notes">Notes</h2>
            <p><a href="#linknoteref-3" id="linknote-3">3</a>这条注释的正文。</p>
            """);
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
        Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.LinkHref?.EndsWith("#linknoteref-3", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void LoaderRecognizesImageOnlyDuokanFootnoteReferences()
    {
        var chapter = WriteChapter("body.xhtml", """
            <p>正文<a class="calibre2" id="A_1" href="#B_1"><img alt="" src="marker.png"/></a>之后。</p>
            <ol class="duokan-footnote-content1"><li class="duokan-footnote-item" id="B_1" value="1">
              <p class="footnote"><a href="#A_1">原文为英语Mister的注释。</a></p>
            </li></ol>
            """);
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("Mister", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(LegacyReferences))]
    public void LoaderRecognizesPublisherReferences(string reference, string target)
    {
        var chapter = WriteChapter("body.xhtml", "<p>正文" + reference + "之后。</p>",
            ".math-super, .raised { vertical-align: super; font-size: 0.75em; }");
        var content = new XhtmlChapterLoader().Load(chapter);
        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);

        Assert.EndsWith("#" + target, marker.FootnoteHref, StringComparison.Ordinal);
        Assert.True(marker.Style.Superscript);
        Assert.True(marker.Style.NoWrap);
    }

    [Fact]
    public void LoaderRecognizesUnstyledLinksToSemanticDefinitions()
    {
        var chapter = WriteChapter("body.xhtml", """
            <p>正文<a href='#target'>①</a>之后。</p>
            <aside epub:type='footnote' id='target'><p>第一条说明。</p></aside>
            """);
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("第一条说明", StringComparison.Ordinal));
    }

    [Fact]
    public void LoaderRecognizesLocalReciprocalAnchorsWithoutNamingConventions()
    {
        var chapter = WriteChapter("body.xhtml", """
            <p>正文<a id='source' href='#target'>[1]</a>之后。</p>
            <p><span><a id='target' href='#source'>[1]</a></span>第一条说明。</p>
            """);
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
        Assert.Contains("第一条说明", content.BodyText);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("第一条说明", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<a href='notes-for-travel.xhtml#chapter-2'>第二章</a>")]
    [InlineData("<a href='footnotes.xhtml#chapter-2'>第二章</a>")]
    [InlineData("<a href='https://example.com/footnote#fn1'>[1]</a>")]
    [InlineData("<a class='notebook' href='#chapter-2'>笔记本</a>")]
    public void LoaderDoesNotTreatDocumentNamesOrExternalLinksAsFootnotes(string link)
    {
        // A publication directory can itself contain "footnote". It must not
        // influence the classification of every absolute link inside it.
        var chapter = WriteChapter("footnote-book/body.xhtml", "<p>正文" + link + "。</p>");
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
    }

    [Fact]
    public void LoaderKeepsNumberedTocAndSuperscriptChapterCrossReferencesAsLinks()
    {
        var chapter = WriteChapter("body.xhtml", """
            <nav epub:type='toc'><ol><li><a href='#note1'>1</a></li></ol></nav>
            <p>参见第<sup><a href='#chapter2'>2</a></sup>章。</p>
            <h2 id='chapter2'>第二章</h2><p>正文。</p>
            <aside epub:type='footnote' id='note1'><p>真实注释。</p></aside>
            """);
        var content = new XhtmlChapterLoader().Load(chapter);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
    }

    [Fact]
    public void LoaderKeepsRevealedNoteBacklinksAtNormalSize()
    {
        var chapter = WriteChapter("body.xhtml", """
            <p>正文<a id='fnref1' href='#fn1' epub:type='noteref'>[1]</a>。</p>
            <aside id='fn1' epub:type='footnote'>
              <p><a href='#fnref1' role='doc-backlink'>[1]</a>第一条说明。</p>
            </aside>
            """);
        var content = new XhtmlChapterLoader(visibleFootnoteFragment: "fn1").Load(chapter);
        var items = content.Blocks.SelectMany(block => block.Items).ToArray();
        Assert.Single(items, item => item.Kind == InlineKind.FootnoteMarker);
        var backlink = Assert.Single(items, item => item.LinkHref?.EndsWith("#fnref1", StringComparison.Ordinal) == true);
        Assert.False(backlink.Style.Superscript);
    }

    [Fact]
    public void LoaderTracksLegacyNamedAnchorAliasesAndRevealsTheirDefinitions()
    {
        var chapter = WriteChapter("body.xhtml", """
            <p>正文<a id='w1' name='source-alias' href='#m1'>[1]</a>。</p>
            <aside epub:type='footnote'><p><a name='m1' href='#w1'>[1]</a>命名锚点说明。</p></aside>
            """);
        var hidden = new XhtmlChapterLoader().Load(chapter);
        var revealed = new XhtmlChapterLoader(visibleFootnoteFragment: "m1").Load(chapter);
        Assert.Contains("source-alias", hidden.FragmentIds);
        Assert.Contains("m1", hidden.FragmentIds);
        Assert.Equal(hidden.BodyText, revealed.BodyText);
        Assert.Contains(revealed.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("命名锚点说明", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("alt='' title='' data-footnote-text='数据属性中的完整说明。'", "数据属性中的完整说明。")]
    [InlineData("alt='1' zy-footnote='出版工具保存的完整说明。'", "出版工具保存的完整说明。")]
    [InlineData("alt='' data-note='另一种图片注释。'", "另一种图片注释。")]
    [InlineData("data-footnote='true' alt='标记属性不应覆盖真正的说明。'", "标记属性不应覆盖真正的说明。")]
    [InlineData("alt='[1]' title='标题属性保存的注释。'", "标题属性保存的注释。")]
    public void LoaderPrefersActualImageNotePayloadOverEmptyOrNumericAlt(string attributes, string expected)
    {
        var chapter = WriteChapter("body.xhtml",
            "<p>正文<img class='duokan-footnote' src='marker.png' " + attributes + "/>。</p>");
        var content = new XhtmlChapterLoader().Load(chapter);
        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
        Assert.Equal(expected, marker.FootnoteText);
    }

    [Fact]
    public async Task ResolverIncludesAllParagraphsOfOneSemanticNoteWithoutTheNextNote()
    {
        var chapter = WriteChapter("notes.xhtml", """
            <section epub:type='endnotes'>
              <aside epub:type='footnote' id='note1'>
                <p><a id='note-label' href='body.xhtml#source' role='doc-backlink'>[1]</a>第一段。</p>
                <p>第二段<br/>第二行。</p>
              </aside>
              <aside epub:type='footnote' id='note2'><p>不属于本条的说明。</p></aside>
            </section>
            """);
        var detail = await new EpubFootnoteResolver().ResolveDetailAsync(_root, Target(chapter, "note-label"));
        Assert.NotNull(detail);
        Assert.Contains("第一段。", detail.Text);
        Assert.Contains("第二段", detail.Text);
        Assert.Contains("第二行。", detail.Text);
        Assert.DoesNotContain("不属于本条", detail.Text);
        Assert.Contains('\n', detail.Text);
        Assert.Equal(EpubFootnoteResolver.NormalizeTargetKey(Target(Path.Combine(_root, "body.xhtml"), "source")), detail.Backlink);
    }

    [Fact]
    public async Task ResolverCollapsesSourceFormattingButKeepsRealParagraphBreaks()
    {
        var chapter = WriteChapter("notes.xhtml", """
            <aside epub:type='footnote' id='note1'>
              <p>第一段源文件
                的排版换行。</p>
              <p>第二段<em>强调</em>内容。</p>
            </aside>
            """);
        var detail = await new EpubFootnoteResolver().ResolveDetailAsync(_root, Target(chapter, "note1"));
        Assert.NotNull(detail);
        Assert.Equal("第一段源文件 的排版换行。\n第二段强调内容。", detail.Text);
    }

    [Fact]
    public async Task ResolverReadsTheParagraphAfterAStandaloneNamedAnchor()
    {
        var chapter = WriteChapter("notes.xhtml", """
            <a name='bz1'></a>
            <p><a href='body.xhtml#z1'>[1]</a>独立锚点之后的说明。</p>
            <a name='bz2'></a><p>[2]另一条说明。</p>
            """);
        var detail = await new EpubFootnoteResolver().ResolveDetailAsync(_root, Target(chapter, "bz1"));
        Assert.NotNull(detail);
        Assert.Contains("独立锚点之后的说明", detail.Text);
        Assert.DoesNotContain("另一条说明", detail.Text);
        Assert.Equal(EpubFootnoteResolver.NormalizeTargetKey(Target(Path.Combine(_root, "body.xhtml"), "z1")), detail.Backlink);
    }

    [Fact]
    public async Task ResolverDoesNotConfuseNestedNoteReferencesWithBacklinks()
    {
        var chapter = WriteChapter("notes.xhtml", """
            <aside epub:type='footnote' id='note1'>
              <p><a epub:type='noteref' href='#note2'>[2]</a>另一个注释的引用。</p>
              <a role='doc-backlink' href='body.xhtml#source'>↩</a>
            </aside>
            <aside epub:type='footnote' id='note2'><p>另一条说明。</p></aside>
            """);
        var detail = await new EpubFootnoteResolver().ResolveDetailAsync(_root, Target(chapter, "note1"));
        Assert.NotNull(detail);
        Assert.Equal(EpubFootnoteResolver.NormalizeTargetKey(Target(Path.Combine(_root, "body.xhtml"), "source")), detail.Backlink);
    }

    [Fact]
    public async Task ResolverDoesNotReadAllNotesInASharedFnoteWrapper()
    {
        var chapter = WriteChapter("notes.xhtml", """
            <div class='fnote'>
              <p><a id='note1n' href='body.xhtml#note1'>[1]</a>第一条。</p>
              <p><a id='note2n' href='body.xhtml#note2'>[2]</a>第二条。</p>
            </div>
            """);
        var detail = await new EpubFootnoteResolver().ResolveDetailAsync(_root, Target(chapter, "note1n"));
        Assert.NotNull(detail);
        Assert.Contains("第一条", detail.Text);
        Assert.DoesNotContain("第二条", detail.Text);
    }

    [Fact]
    public async Task ResolverSeparatesCaseDistinctFilesAndFragments()
    {
        var lower = WriteChapter("notes.xhtml", "<p id='note1'>小写文件说明。</p><p id='Note1'>大写锚点说明。</p>");
        Assert.NotEqual(EpubFootnoteResolver.NormalizeTargetKey(Target(lower, "note1")),
            EpubFootnoteResolver.NormalizeTargetKey(Target(lower, "Note1")));
        if (OperatingSystem.IsWindows()) return;
        var upper = Path.Combine(_root, "Notes.xhtml");
        Assert.NotEqual(EpubFootnoteResolver.NormalizeTargetKey(Target(lower, "note1")),
            EpubFootnoteResolver.NormalizeTargetKey(Target(upper, "note1")));
        // Case-insensitive host filesystems cannot hold both archive entries;
        // on a case-sensitive host also exercise the document cache itself.
        if (File.Exists(upper)) return;
        upper = WriteChapter("Notes.xhtml", "<p id='note1'>大写文件说明。</p>");
        var details = await new EpubFootnoteResolver().ResolveAsync(_root,
            [Target(lower, "note1"), Target(upper, "note1")]);
        Assert.Equal(2, details.Count);
        Assert.Contains("小写文件说明", details[EpubFootnoteResolver.NormalizeTargetKey(Target(lower, "note1"))]);
        Assert.Contains("大写文件说明", details[EpubFootnoteResolver.NormalizeTargetKey(Target(upper, "note1"))]);
    }

    private string WriteChapter(string relative, string body, string css = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>"
            + "<head><style>" + css + "</style></head><body>" + body + "</body></html>");
        return path;
    }

    private static string Target(string path, string fragment) => new Uri(path).AbsoluteUri + "#" + fragment;
    public void Dispose() => TestHelpers.TryDelete(_root);
}
