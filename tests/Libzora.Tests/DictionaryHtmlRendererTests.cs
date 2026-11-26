using HtmlAgilityPack;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class DictionaryHtmlRendererTests
{
    [Fact]
    public void PreservesDictionaryHierarchyAndCssWhileRemovingSourceScripts()
    {
        var html = DictionaryHtmlRenderer.Render([new("apple", "苹果", "朗文", """
            <link href="lm6.css" rel="stylesheet"><script>untrusted()</script>
            <div class="entry"><span class="pos">noun</span><div class="sense"><b>1</b>
            <span class="def">苹果</span><span class="example">an apple</span></div>
            <span onclick="dictionary.toggle(this)">Examples</span><div class="content">more</div>
            <a href="entry://fruit">fruit</a><img src="file:///secret.png" onerror="steal()"></div>
            """, ".sense{display:block;color:blue}")]);
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//div[@class='entry']/div[@class='sense']/span[@class='example']"));
        Assert.Contains(".sense{display:block;color:blue}", html);
        Assert.DoesNotContain("untrusted()", html);
        Assert.Null(doc.DocumentNode.SelectSingleNode("//*[@onclick or @onerror]"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//img"));
        Assert.Equal("toggle", doc.DocumentNode.SelectSingleNode("//*[@data-dictionary-toggle]").GetAttributeValue("data-dictionary-toggle", ""));
        Assert.Equal("fruit", doc.DocumentNode.SelectSingleNode("//*[@data-dictionary-term]").GetAttributeValue("data-dictionary-term", ""));
        Assert.Contains("Content-Security-Policy", html);
    }

    [Fact]
    public void PlainDefinitionsAndDictionaryNamesAreEscaped()
    {
        var html = DictionaryHtmlRenderer.Render([new("x", "<script>example</script>", "<b>name</b>")]);
        Assert.Contains("&lt;script&gt;example&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;name&lt;/b&gt;", html);
    }

    [Fact]
    public void BuiltInStyleModeOverridesExternalCssAndInlinePresentationalAttributes()
    {
        var html = DictionaryHtmlRenderer.Render(
            [new("apple", "苹果", "朗文", """
                <style>.entry{color:red}</style>
                <div class="entry" style="background:yellow"><font color="red"><span class="hwd">apple</span></font>
                <span class="def" style="font-size:40px">苹果</span></div>
                """, ".sense{display:block;color:blue}")],
            styleMode: DictionaryEntryStyleMode.BuiltIn);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        Assert.DoesNotContain(".sense{display:block;color:blue}", html);
        Assert.DoesNotContain(".entry{color:red}", html);
        Assert.Contains(".libzora-plain-headword", html);
        Assert.Null(doc.DocumentNode.SelectSingleNode("//div[@class='entry']/@style"));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//font/@color"));
        Assert.NotNull(doc.DocumentNode.SelectSingleNode("//div[@class='entry']//span[@class='hwd']"));
    }

    [Fact]
    public void BuiltInStyleModeFormatsPlainEntriesWithStructuredHeadwordAndParagraphs()
    {
        var html = DictionaryHtmlRenderer.Render(
            [new("apple", "n. 苹果\n\n一种水果", "简明词典")],
            styleMode: DictionaryEntryStyleMode.BuiltIn);

        Assert.Contains("libzora-plain-headword", html);
        Assert.Contains("libzora-plain-def", html);
        Assert.Contains("<p>n. 苹果</p>", html);
        Assert.Contains("<p>一种水果</p>", html);
    }

    [Fact]
    public void FastToReadableTextStripsMarkupDecodesEntitiesAndNormalizesLines()
    {
        var readable = DictionaryService.ToReadableText("""
            <head><style>.x{color:red}</style></head>
            <div class="entry"><script>ignored()</script><b>apple</b>&nbsp;&amp;&nbsp;pie<br/>  line   two  </div>
            <p>line three</p>
            """);

        var lines = readable.Split(Environment.NewLine);
        Assert.Equal(["apple & pie", "line two", "line three"], lines);
        Assert.Equal("@@@LINK=target", DictionaryService.ToReadableText("  @@@LINK=target  "));
    }
}
