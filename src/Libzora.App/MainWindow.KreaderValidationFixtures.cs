#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Validation fixtures and generated EPUBs.
/// </summary>
public partial class MainWindow
{
    private static void RequireKreaderVerticalFlowInvariants(
        JsonElement edge,
        string context,
        bool requireTopAlignedColumns)
    {
        // Under vertical-rl `direction` selects the inline axis, not the
        // column axis: rtl bottom aligns every partially filled line, the 2em
        // first-line indent included, while changing nothing the other
        // diagnostics measure.
        Require(
            edge.GetProperty("direction").GetString() == "ltr",
            $"{context}: body inline direction is not ltr, so partially filled columns "
            + "are bottom aligned: " + edge);
        var blockDirection = edge.GetProperty("blockDirection").GetString();
        Require(
            string.IsNullOrEmpty(blockDirection) || blockDirection == "ltr",
            $"{context}: a text block inherited a non-ltr inline direction: " + edge);
        Require(
            edge.GetProperty("overflowTopCount").GetInt32() == 0
            && edge.GetProperty("overflowBottomCount").GetInt32() == 0,
            $"{context}: glyphs are painted outside the column band, so an atomic "
            + "sideways run overflows the page: " + edge);
        // Column pitch regression guard. The WebKitGTK orthogonal-cell bug
        // collapsed every line box to the 1em cell height: the visible
        // signature is adjacent glyph columns pitched at roughly 0.55× the
        // configured line height. Ink-band edges of centred cells and heading
        // margins legitimately measure between 0.7× and 2× the grid, so only
        // a pitch below 0.7× is a collapse.
        var lineHeight = edge.GetProperty("lineHeight").GetDouble();
        var columnLefts = edge.GetProperty("columnLefts").EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        if (lineHeight > 1 && columnLefts.Length > 2)
        {
            var collapsedPitches = 0;
            double? worstPitch = null;
            for (var index = 1; index < columnLefts.Length; index++)
            {
                var pitch = columnLefts[index] - columnLefts[index - 1];
                if (pitch <= 0.5 || pitch >= lineHeight * 0.7) continue;
                collapsedPitches++;
                worstPitch = worstPitch is { } worst ? Math.Min(worst, pitch) : pitch;
            }
            Require(
                collapsedPitches == 0,
                $"{context}: {collapsedPitches} column pitches collapsed below the "
                + $"{lineHeight:F1}px line grid (worst {worstPitch:F1}px) — the WebKitGTK "
                + "orthogonal-cell line-box regression returned: " + edge);
        }
        if (requireTopAlignedColumns)
        {
            Require(
                edge.GetProperty("bottomAlignedColumnCount").GetInt32() == 0,
                $"{context}: a column's text is pushed against the page bottom: " + edge);
        }
    }

    private static void CreateKreaderValidationEpub(string path, bool includeCover = false)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var coverItem = includeCover
            ? """<item id="cover" href="cover.png" media-type="image/png" properties="cover-image" />"""
            : string.Empty;
        AddValidationZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="EPUB/package.opf" media-type="application/oebps-package+xml" /></rootfiles>
            </container>
            """);
        AddValidationZipEntry(archive, "EPUB/package.opf", $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">urn:uuid:1db98b52-2d1f-4d80-a3e9-kreaderlinux</dc:identifier>
                <dc:title>Linux Kreader Validation Long Numbers EPUB</dc:title>
                <dc:creator>Libzora Validation</dc:creator>
                <dc:language>zh-CN</dc:language>
                <dc:description>Linux Kreader validation fixture</dc:description>
              </metadata>
              <manifest>
                {{coverItem}}
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                <item id="one" href="chapter1.xhtml" media-type="application/xhtml+xml" />
                <item id="two" href="chapter2.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="one" /><itemref idref="two" /></spine>
            </package>
            """);
        AddValidationZipEntry(archive, "EPUB/nav.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body><nav epub:type="toc"><ol>
                <li><a href="chapter1.xhtml">Chapter One</a></li>
                <li><a href="chapter2.xhtml">Chapter Two</a></li>
              </ol></nav></body>
            </html>
            """);
        AddValidationZipEntry(archive, "EPUB/chapter1.xhtml", BuildValidationChapter("Chapter One", "001", includeFootnote: true));
        AddValidationZipEntry(archive, "EPUB/chapter2.xhtml", BuildValidationChapter("Chapter Two", "201", includeFootnote: false));
        if (includeCover) AddReaderOpenProbeCover(archive);
    }

    private static string BuildValidationChapter(string title, string start, bool includeFootnote)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>""" + title + """</title><style>.publication-tcy { text-combine-upright: all; -webkit-text-combine: horizontal; }</style></head><body>""");
        builder.Append("<h1>").Append(title).AppendLine("</h1>");
        if (includeFootnote)
        {
            builder.AppendLine("""<p>Linux validation paragraph 001 includes COPYRIGHT, ISBN, A1, 单数字7，出版物直排横书<span class="publication-tcy">12</span>，三位数200，search-token-linux and AI context linux.竖排长数字12345678901234567890不能断开。<a epub:type="noteref" role="doc-noteref" href="#footnote-1">[1]</a></p>""");
            builder.AppendLine("""<aside id="footnote-1" epub:type="footnote"><p>Footnote validation body for Linux WebKit.</p></aside>""");
        }
        builder.AppendLine("""<p class="vertical-fixture-sample">数字：单数字7，双位数12，三位数200。脚注<a epub:type="noteref" role="doc-noteref" href="#fixture-note">[12]</a>。缩写：DNA、FPGA、CPU、AI。中文标点：逗号，句号。问号？叹号！顿号、分号；冒号：双引号“甲”单引号‘乙’圆括号（丙）书名号《丁》〈戊〉方括号【己】〔庚〕［辛］花括号｛壬｝破折号——省略号……间隔号·。ASCII标点：!&quot;#$%&amp;'()*+,-./:;&lt;=&gt;?@[\]^_`{|}~。</p><aside id="fixture-note" epub:type="footnote"><p>Fixture footnote.</p></aside>""");
        var first = int.Parse(start);
        for (var i = 0; i < 90; i++)
        {
            builder.Append("<p>Linux validation paragraph ")
                .Append((first + i).ToString("000"))
                .Append(" keeps enough text for pagination, scrolling, 双位数12，三位数200，search-token-linux, and AI context linux validation inside WPE WebKit. ")
                .Append("The sentence repeats to create measurable layout width and height on Linux desktop. ")
                .Append("竖排正文验证：天地玄黄，宇宙洪荒；日月盈昃，辰宿列张。标点必须留在完整字列内，不能在页面两侧被裁切。</p>");
        }
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static void AddValidationZipEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
#endif
