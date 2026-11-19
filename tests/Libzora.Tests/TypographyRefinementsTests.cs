using Libzora.Core;
using Libzora.Layout;
using Xunit;

namespace Libzora.Tests;

public sealed class TypographyRefinementsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "libzora-typo-" + Guid.NewGuid().ToString("N"));
    private readonly string _fontPath;

    public TypographyRefinementsTests()
    {
        Directory.CreateDirectory(_tempDir);
        _fontPath = FindBundledFont();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string FindBundledFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Libzora.App", "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Bundled KingHwaOldSong font not found.");
    }

    private TypesetEngine CreateEngine() => new(new TypesetFontLibrary(_fontPath));

    private string WriteChapter(string bodyHtml)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".xhtml");
        File.WriteAllText(path,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><body>{bodyHtml}</body></html>");
        return path;
    }

    [Fact]
    public void DropCapOnlyExpandsTheFirstLineHeight()
    {
        var html = "<p><span style=\"font-size:2.5em\">I</span>n the beginning there was a long English paragraph that wraps cleanly across three or more lines inside a narrow mobile column so subsequent lines keep normal spacing.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            LineHeight = 1.75f,
            NaturalLatinTypography = true,
            ParagraphIndent = false,
            ViewportWidth = 340f,
            ViewportHeight = 800f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var layout = engine.Compose(content, options);
        var baselines = layout.Pages[0].Runs
            .Select(run => MathF.Round(run.OriginY, 1))
            .Distinct()
            .Order()
            .ToArray();

        Assert.True(baselines.Length >= 4, $"Expected at least 4 lines, got {baselines.Length}");
        var firstGap = baselines[1] - baselines[0];
        var normalGap = baselines[2] - baselines[1];
        var thirdGap = baselines[3] - baselines[2];
        Assert.True(firstGap > normalGap + 10f, $"First line should expand for drop-cap: first={firstGap}, normal={normalGap}");
        Assert.InRange(Math.Abs(normalGap - thirdGap), 0f, 0.5f);
    }

    [Fact]
    public void NestedOpeningPunctuationNeverStaysAtLineEnd()
    {
        // Craft text so "“《" lands right at the line edge across several widths.
        var html = "<p>一二三四五六七八九十“《红楼梦》是一部伟大的中国古典长篇小说，描写了贾宝玉与林黛玉的故事。”</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        for (var width = 200f; width <= 260f; width += 5f)
        {
            var options = new TypesetLayoutOptions
            {
                BaseFontSize = 18f,
                ParagraphIndent = false,
                ViewportWidth = width,
                ViewportHeight = 600f,
                InsetHorizontal = 24f,
                InsetVertical = 24f,
            };
            var layout = engine.Compose(content, options);
            var lines = layout.Pages.SelectMany(p => p.Runs)
                .GroupBy(r => MathF.Round(r.OriginY, 1))
                .OrderBy(g => g.Key)
                .ToList();

            for (var i = 0; i < lines.Count - 1; i++)
            {
                var lastRun = lines[i].OrderBy(r => r.OriginX).Last();
                if (lastRun.TextStart >= 0 && lastRun.TextLength > 0)
                {
                    var lastChar = content.BodyText[lastRun.TextStart + lastRun.TextLength - 1];
                    Assert.False(TypesetText.IsProhibitedAtLineEnd(lastChar),
                        $"Line {i} at width {width} ended with prohibited opening mark '{lastChar}'.");
                }
            }
        }
    }

    [Fact]
    public void CjkLatinSpacingInsertsGapOnlyWhenEnabled()
    {
        var html = "<p>在iOS上使用Libzora阅读。</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        var withGap = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            ParagraphIndent = false,
            CjkLatinSpacing = true,
            ViewportWidth = 500f,
            ViewportHeight = 400f,
        });
        var withoutGap = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            ParagraphIndent = false,
            CjkLatinSpacing = false,
            ViewportWidth = 500f,
            ViewportHeight = 400f,
        });

        var widthWithGap = withGap.Pages[0].Runs.Sum(r => r.FlowAdvance);
        var widthWithoutGap = withoutGap.Pages[0].Runs.Sum(r => r.FlowAdvance);
        // Four boundaries: 在|iOS, iOS|上, 用|Libzora, Libzora|阅
        Assert.True(widthWithGap > widthWithoutGap + 4f,
            $"Expected CJK-Latin spacing to widen mixed run: with={widthWithGap}, without={widthWithoutGap}");
    }

    [Fact]
    public void ExtendedEnglishHyphenatorSupportsPossessivesAndAccents()
    {
        Assert.NotEmpty(EnglishHyphenator.GetBreakpointsForToken("government's"));
        Assert.NotEmpty(EnglishHyphenator.GetBreakpointsForToken("government’s"));
        Assert.NotEmpty(EnglishHyphenator.GetBreakpointsForToken("coöperation"));
        Assert.NotEmpty(EnglishHyphenator.GetBreakpointsForToken("international-"));
    }

    [Fact]
    public void LongEnglishWordBeforeTrailingPunctuationStillHyphenates()
    {
        var html = "<p>We discuss internationalization, and how careful hyphenation prevents awkward gaps across narrow phone screens.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        // Scan narrow widths to find a boundary where "internationalization,"
        // would overflow solely because of the trailing comma or final letters.
        var sawHyphenated = false;
        for (var width = 180f; width <= 290f; width += 4f)
        {
            var layout = engine.Compose(content, new TypesetLayoutOptions
            {
                BaseFontSize = 18f,
                NaturalLatinTypography = true,
                HyphenateEnglish = true,
                ParagraphIndent = false,
                ViewportWidth = width,
                ViewportHeight = 600f,
                InsetHorizontal = 20f,
                InsetVertical = 20f,
            });
            if (layout.Pages.SelectMany(p => p.Runs).Any(r => r.SyntheticHyphen))
            {
                sawHyphenated = true;
                break;
            }
        }

        Assert.True(sawHyphenated, "Expected hyphenation to split 'internationalization' even when followed by a comma.");
    }

    [Fact]
    public void AlignLeftOptionPreventsSpaceStretching()
    {
        var html = "<p>This is a reasonably long English sentence with multiple words designed to wrap across several lines inside a phone-sized column without stretching word spaces when left alignment is chosen.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        var justified = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            NaturalLatinTypography = true,
            AlignLeft = false,
            ParagraphIndent = false,
            ViewportWidth = 320f,
            ViewportHeight = 600f,
            InsetHorizontal = 20f,
        });
        var leftAligned = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            NaturalLatinTypography = true,
            AlignLeft = true,
            ParagraphIndent = false,
            ViewportWidth = 320f,
            ViewportHeight = 600f,
            InsetHorizontal = 20f,
        });

        var rightEdge = 320f - 20f;
        var justSlack = justified.Pages[0].Runs.SkipLast(1).Sum(r => Math.Max(0f, rightEdge - r.OriginX - r.FlowAdvance));
        var leftSlack = leftAligned.Pages[0].Runs.SkipLast(1).Sum(r => Math.Max(0f, rightEdge - r.OriginX - r.FlowAdvance));
        Assert.True(leftSlack > justSlack + 2f,
            $"Left-aligned lines should remain ragged: leftSlack={leftSlack}, justSlack={justSlack}");
    }

    [Fact]
    public void PreformattedBlocksPreserveNewlinesAndIndentation()
    {
        var html = "<pre>line1\n    indented\nline3</pre>";
        var content = new XhtmlChapterLoader(paragraphIndent: true).Load(WriteChapter(html));
        using var engine = CreateEngine();

        var layout = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 16f,
            ViewportWidth = 480f,
            ViewportHeight = 400f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        });

        var lines = layout.Pages[0].Runs
            .GroupBy(r => MathF.Round(r.OriginY, 1))
            .OrderBy(g => g.Key)
            .ToList();
        Assert.Equal(3, lines.Count);
        var line1X = lines[0].Min(r => r.OriginX);
        var line2TextRun = lines[1].First(r => content.BodyText.Substring(r.TextStart, r.TextLength).Contains("indented"));
        // Leading spaces before "indented" must push its visible start to the right of line1.
        var indentedOffset = content.BodyText.IndexOf("indented", StringComparison.Ordinal);
        var indentedRect = layout.GetCharRect(0, indentedOffset);
        Assert.True(indentedRect.HasValue);
        Assert.True(indentedRect!.Value.Left > line1X + 12f,
            $"Expected preserved indentation on line 2: line1X={line1X}, indentedLeft={indentedRect.Value.Left}");
    }

    [Fact]
    public void PublisherZeroIndentIsRespectedWhenParagraphIndentEnabled()
    {
        var html = "<h1>Chapter One</h1><p style=\"text-indent:0\">Opening paragraph without indent.</p><p>Second paragraph with indent.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: true).Load(WriteChapter(html));

        Assert.Equal(3, content.Blocks.Count);
        Assert.True(content.Blocks[1].ExplicitTextIndent);
        Assert.Equal(0f, content.Blocks[1].TextIndentEm);
        Assert.False(content.Blocks[2].ExplicitTextIndent);
        Assert.Equal(2f, content.Blocks[2].TextIndentEm);
    }

    [Fact]
    public void LatinFontCatalogAndPairedLatinFaceWork()
    {
        var latinFaces = SystemFontCatalog.LatinFonts;
        Assert.NotEmpty(latinFaces);

        using var fonts = new TypesetFontLibrary(_fontPath);
        var chosen = latinFaces[0].Family;
        Assert.True(fonts.TryEnsureSystemFont(chosen, out var latinKey));
        Assert.True(fonts.TrySetLatinFont(latinKey));

        var html = "<p>中文和 <b>Bold</b> <i>Italic</i> 混排。</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = new TypesetEngine(fonts, ownsFonts: false);
        var layout = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            ParagraphIndent = false,
            ViewportWidth = 500f,
            ViewportHeight = 400f,
        });

        var usedFonts = layout.Pages[0].Runs.Select(r => r.FontPath).Distinct().ToArray();
        Assert.Contains(usedFonts, f => f.StartsWith(latinKey, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(usedFonts, f => string.Equals(f, _fontPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HorizontalHangingPunctuationKeepsCJKLineBalanced()
    {
        // With hanging punctuation enabled, a closing full stop just past the
        // margin hangs on the current line instead of pulling a Han character down.
        var html = "<p>一二三四五六七八九十。甲乙丙丁戊己庚辛壬癸</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        // 10 CJK chars * 18px + 9 * 0.216px ≈ 182px; availWidth = 184px fits 10 chars
        // but not the 11th full stop (9px). With hanging punctuation, '。' stays on line 1.
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            ParagraphIndent = false,
            HangingPunctuation = true,
            ViewportWidth = 184f + 48f,
            ViewportHeight = 600f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var layout = engine.Compose(content, options);
        var periodOffset = content.BodyText.IndexOf('。');
        var firstCharRect = layout.GetCharRect(0, 0);
        var periodRect = layout.GetCharRect(0, periodOffset);
        Assert.True(firstCharRect.HasValue && periodRect.HasValue);
        Assert.InRange(Math.Abs(periodRect!.Value.Top - firstCharRect!.Value.Top), 0f, 2f);
    }
}
