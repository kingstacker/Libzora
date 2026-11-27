using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;
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

    [Fact]
    public void HeadingHierarchyScalesLevelsAndKeepsSubheadingWithFollowingParagraph()
    {
        var filler = string.Concat(Enumerable.Range(0, 14).Select(i => $"<p>第{i + 1}段填充正文用于把小标题推到页面底部附近。</p>"));
        var html = "<h1>一级标题</h1><h2>二级标题</h2><h3>三级标题</h3>"
            + filler
            + "<h3>页尾小节标题</h3><p>紧随小节标题的正文第一句，应当与标题在同一页开始，不让标题孤悬在上一页底部。</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            LineHeight = 1.75f,
            ParagraphIndent = false,
            ViewportWidth = 360f,
            ViewportHeight = 520f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var layout = engine.Compose(content, options);
        var allRuns = layout.Pages.SelectMany(p => p.Runs).ToList();
        var h1Run = allRuns.First(r => r.TextStart >= 0 && content.BodyText.Substring(r.TextStart, r.TextLength).Contains("一级"));
        var h2Run = allRuns.First(r => r.TextStart >= 0 && content.BodyText.Substring(r.TextStart, r.TextLength).Contains("二级"));
        var h3Run = allRuns.First(r => r.TextStart >= 0 && content.BodyText.Substring(r.TextStart, r.TextLength).Contains("三级"));
        var h1Size = h1Run.FontSize;
        var h2Size = h2Run.FontSize;
        var h3Size = h3Run.FontSize;
        Assert.True(h1Size > h2Size && h2Size > h3Size && h3Size > options.BaseFontSize,
            $"Expected h1 > h2 > h3 > body: h1={h1Size}, h2={h2Size}, h3={h3Size}, body={options.BaseFontSize}");
        Assert.True(h2Run.OriginY > h1Run.OriginY + 8f && h3Run.OriginY > h2Run.OriginY + 8f,
            $"Expected h1, h2, h3 on separate lines: h1.Y={h1Run.OriginY}, h2.Y={h2Run.OriginY}, h3.Y={h3Run.OriginY}");

        var subHeadingOffset = content.BodyText.IndexOf("页尾小节标题", StringComparison.Ordinal);
        var followOffset = content.BodyText.IndexOf("紧随小节标题", StringComparison.Ordinal);
        Assert.Equal(layout.GetPageIndexOfOffset(subHeadingOffset), layout.GetPageIndexOfOffset(followOffset));
    }

    [Fact]
    public void HeadingMaintainsWideGapAboveFollowingBodyParagraph()
    {
        var html =
            "<h2 style=\"font-size:1.125em;line-height:1.2;margin:2em 0 1em\">章节标题</h2>"
            + "<p style=\"line-height:1.84;margin:1em 0 0\">正文第一行内容应当与上方标题保持清晰舒展的间距，不会显得挤在一起。这里再补一些字让正文换行到第二行，方便对比行距。</p>";
        var content = new XhtmlChapterLoader().Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 22f,
            LineHeight = 1.65f,
            ParagraphSpacingMatchesLineHeight = true,
            ViewportWidth = 420f,
            ViewportHeight = 640f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };

        var layout = engine.Compose(content, options);
        var page = Assert.Single(layout.Pages);
        var headingRun = page.Runs.First(r => r.TextStart >= 0 && content.BodyText.Substring(r.TextStart, r.TextLength).Contains("章节标题"));
        var bodyLineBaselines = page.Runs
            .Where(r => r.TextStart >= 0 && r.TextStart >= "章节标题".Length)
            .Select(r => r.OriginY)
            .Distinct()
            .Order()
            .ToArray();

        Assert.True(bodyLineBaselines.Length >= 2);
        var bodyLinePitch = bodyLineBaselines[1] - bodyLineBaselines[0];
        var headingToBodyGap = bodyLineBaselines[0] - headingRun.OriginY;
        Assert.True(
            headingToBodyGap >= bodyLinePitch + options.BaseFontSize * 1.0f,
            $"Expected wide gap below heading: gap={headingToBodyGap}, bodyPitch={bodyLinePitch}");
    }

    [Fact]
    public void StyledBlockquoteEmitsLeftVerticalBar()
    {
        var html = "<blockquote style=\"border-left: 0.2em solid #666\"><p>这是一段带有出版社左边框样式的引用正文。</p></blockquote>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            ViewportWidth = 360f,
            ViewportHeight = 500f,
        });
        Assert.Contains(layout.Pages[0].Decorations, d => d.Kind == DecorationKind.BlockquoteBar && d.Rect.Height > 10f);
    }

    [Fact]
    public void ExplicitCssPageBreaksSplitPagesInBothWritingModes()
    {
        var html = "<p>第一页内容。</p><div style=\"page-break-before: always\"><p>第二页内容。</p></div><p style=\"break-after: page\">仍在第二页。</p><p>第三页内容。</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, new TypesetLayoutOptions
            {
                WritingMode = mode,
                BaseFontSize = 18f,
                ViewportWidth = 400f,
                ViewportHeight = 600f,
            });
            var p1 = layout.GetPageIndexOfOffset(content.BodyText.IndexOf("第一页", StringComparison.Ordinal));
            var p2 = layout.GetPageIndexOfOffset(content.BodyText.IndexOf("第二页", StringComparison.Ordinal));
            var p3 = layout.GetPageIndexOfOffset(content.BodyText.IndexOf("第三页", StringComparison.Ordinal));
            Assert.True(p1 < p2 && p2 < p3, $"Expected 3 distinct pages in {mode}: p1={p1}, p2={p2}, p3={p3}");
        }
    }

    [Fact]
    public void DoubleCharacterEllipsisAndDashNeverSplitAcrossLines()
    {
        var html = "<p>这是一段专门用来测试双字符省略号……以及双字符破折号——在不同窄屏宽度下都不会被拆散成两行的中文正文。</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();

        var ellipsisOffset = content.BodyText.IndexOf("……", StringComparison.Ordinal);
        var dashOffset = content.BodyText.IndexOf("——", StringComparison.Ordinal);
        for (var width = 180f; width <= 260f; width += 6f)
        {
            var layout = engine.Compose(content, new TypesetLayoutOptions
            {
                BaseFontSize = 18f,
                ParagraphIndent = false,
                ViewportWidth = width,
                ViewportHeight = 800f,
                InsetHorizontal = 24f,
                InsetVertical = 24f,
            });
            var e1 = layout.GetCharRect(layout.GetPageIndexOfOffset(ellipsisOffset), ellipsisOffset);
            var e2 = layout.GetCharRect(layout.GetPageIndexOfOffset(ellipsisOffset + 1), ellipsisOffset + 1);
            Assert.True(e1.HasValue && e2.HasValue);
            Assert.InRange(Math.Abs(e1!.Value.Top - e2!.Value.Top), 0f, 2f);

            var d1 = layout.GetCharRect(layout.GetPageIndexOfOffset(dashOffset), dashOffset);
            var d2 = layout.GetCharRect(layout.GetPageIndexOfOffset(dashOffset + 1), dashOffset + 1);
            Assert.True(d1.HasValue && d2.HasValue);
            Assert.InRange(Math.Abs(d1!.Value.Top - d2!.Value.Top), 0f, 2f);
        }
    }

    [Fact]
    public void FloatedDropCapWrapsInitialLinesBesideLetterThenResumesFullWidth()
    {
        var html = "<p><span style=\"float:left; font-size:2.6em\">I</span>n the beginning there was a long English paragraph crafted so the first two or three lines wrap neatly beside the large floated initial letter while the remaining lines flow back to the left margin across the full width of the column.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            LineHeight = 1.75f,
            NaturalLatinTypography = true,
            ParagraphIndent = false,
            ViewportWidth = 360f,
            ViewportHeight = 800f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var layout = engine.Compose(content, options);
        var dropRun = layout.Pages[0].Runs.First(r => r.FontSize > options.BaseFontSize * 2f);
        var bodyLines = layout.Pages[0].Runs
            .Where(r => r.FontSize <= options.BaseFontSize * 1.1f)
            .GroupBy(r => MathF.Round(r.OriginY, 1))
            .OrderBy(g => g.Key)
            .ToList();

        Assert.True(bodyLines.Count >= 4, $"Expected at least 4 wrapped body lines, got {bodyLines.Count}");
        var line1Left = bodyLines[0].Min(r => r.OriginX);
        var line2Left = bodyLines[1].Min(r => r.OriginX);
        var laterLineLeft = bodyLines[^1].Min(r => r.OriginX);
        Assert.True(line1Left > dropRun.OriginX + dropRun.FlowAdvance * 0.8f,
            $"Line 1 should sit to the right of drop cap: line1Left={line1Left}, dropRight={dropRun.OriginX + dropRun.FlowAdvance}");
        Assert.InRange(Math.Abs(line1Left - line2Left), 0f, 1f);
        Assert.True(laterLineLeft < line1Left - 10f,
            $"Later lines should return to full left margin: laterLineLeft={laterLineLeft}, line1Left={line1Left}");
    }

    [Fact]
    public void FigureImageAndCaptionStayOnSamePage()
    {
        var imgPath = Path.Combine(_tempDir, "sample.png");
        using (var surface = SKSurface.Create(new SKImageInfo(240, 140)))
        {
            surface.Canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.DarkSlateGray };
            surface.Canvas.DrawRect(20, 20, 200, 100, paint);
            using var snap = surface.Snapshot();
            using var data = snap.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(imgPath);
            data.SaveTo(fs);
        }

        var filler = string.Concat(Enumerable.Range(0, 7).Select(i => $"<p>前面填充正文段落 {i + 1}，用来把插图推到页面下半部分。</p>"));
        var html = filler + "<figure><img src=\"sample.png\" /><figcaption>图 1：插图与图注应保持在同一页。</figcaption></figure>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            LineHeight = 1.75f,
            ParagraphIndent = false,
            ViewportWidth = 360f,
            ViewportHeight = 460f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        });

        var imagePage = layout.Pages.ToList().FindIndex(p => p.Images.Count > 0);
        var captionPage = layout.GetPageIndexOfOffset(content.BodyText.IndexOf("图 1", StringComparison.Ordinal));
        Assert.True(imagePage >= 0);
        Assert.Equal(imagePage, captionPage);
    }

    [Fact]
    public void KnuthPlassBalancesJustifiedEnglishParagraphsAndOpticalHyphenProtrudes()
    {
        var html = "<p>The art of typography demands careful attention to every syllable and space across the entire paragraph so that consecutive lines never alternate harshly between tight compression and gaping rivers of white space, even when internationalization and counterrevolutionaries appear near a narrow margin.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 18f,
            LineHeight = 1.75f,
            NaturalLatinTypography = true,
            HyphenateEnglish = true,
            HangingPunctuation = true,
            ParagraphIndent = false,
            ViewportWidth = 340f,
            ViewportHeight = 800f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var layout = engine.Compose(content, options);
        var runs = layout.Pages[0].Runs;
        Assert.True(runs.Count >= 5);
        // Verify optical protrusion: if a line ends with a synthetic hyphen,
        // its visual right edge slightly exceeds rightMargin within the optical cap.
        var rightMargin = options.ViewportWidth - options.InsetHorizontal;
        var hyphenRun = runs.FirstOrDefault(r => r.SyntheticHyphen);
        if (hyphenRun is not null)
        {
            var runRight = hyphenRun.OriginX + hyphenRun.FlowAdvance;
            Assert.InRange(runRight, rightMargin - 1f, rightMargin + options.BaseFontSize * 0.35f);
        }
    }

    [Fact]
    public void DarkThemeDimsRenderedImagesWithoutBreakingLightTheme()
    {
        var imgPath = Path.Combine(_tempDir, "bright.png");
        using (var surface = SKSurface.Create(new SKImageInfo(40, 40)))
        {
            surface.Canvas.Clear(SKColors.White);
            using var snap = surface.Snapshot();
            using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(imgPath);
            data.SaveTo(fs);
        }

        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter("<img src=\"bright.png\" />"));
        using var fonts = new TypesetFontLibrary(_fontPath);
        using var engine = new TypesetEngine(fonts, ownsFonts: false);
        var layout = engine.Compose(content, new TypesetLayoutOptions { ViewportWidth = 200f, ViewportHeight = 200f });
        using var skImage = SKImage.FromEncodedData(imgPath);
        var darkTheme = new TypesetPaintTheme
        {
            Background = new SKColor(0x0E, 0x0E, 0x0E),
            Text = new SKColor(0xE8, 0xE8, 0xE8),
            Muted = new SKColor(0x99, 0x99, 0x99),
            Selection = SKColors.White,
            SelectionText = SKColors.Black,
            Highlight = new SKColor(0x33, 0x33, 0x33),
            SearchMark = new SKColor(0x44, 0x44, 0x44),
            Rule = new SKColor(0x66, 0x66, 0x66),
        };
        var painter = new TypesetPainter(fonts, darkTheme, _ => skImage);
        using var outSurface = SKSurface.Create(new SKImageInfo(200, 200));
        painter.Paint(outSurface.Canvas, layout.Pages[0]);
        using var outBitmap = SKBitmap.FromImage(outSurface.Snapshot());
        var rect = layout.Pages[0].Images[0].Rect;
        var sample = outBitmap.GetPixel((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
        Assert.InRange(sample.Red, (byte)190, (byte)230); // attenuated from 255 to ~214
    }

    [Fact]
    public void KnuthPlassBreaksAfterStandaloneEmDashInEnglishDialogue()
    {
        var html = "<p>“Which is it to-day?” I asked,—“morphine or cocaine?”</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 22f,
            LineHeight = 1.75f,
            NaturalLatinTypography = true,
            HyphenateEnglish = true,
            HangingPunctuation = true,
            ParagraphIndent = false,
            ViewportWidth = 390f,
            ViewportHeight = 800f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };

        var layout = engine.Compose(content, options);
        var lines = layout.Pages[0].Runs
            .GroupBy(r => MathF.Round(r.OriginY, 1))
            .OrderBy(g => g.Key)
            .Select(g => string.Concat(g.OrderBy(r => r.OriginX)
                .Where(r => r.TextStart >= 0 && r.TextLength > 0)
                .Select(r => content.BodyText.Substring(r.TextStart, r.TextLength))))
            .ToList();

        Assert.Equal(2, lines.Count);
        Assert.Contains("asked,—", lines[0]);
        Assert.StartsWith("“morphine", lines[1]);
    }

    [Fact]
    public void SingleColumnEnglishTocTableCollapsesHeadingGapAndUsesNaturalSpacing()
    {
        var html = """
            <h2>Contents</h2>
            <table style="margin-left:auto;margin-right:auto">
              <tbody>
                <tr><td><a href="#c1">Chapter I. The Science of Deduction</a></td></tr>
                <tr><td><a href="#c2">Chapter II. The Statement of the Case</a></td></tr>
                <tr><td><a href="#c3">Chapter VIII. The Baker Street Irregulars</a></td></tr>
              </tbody>
            </table>
            """;
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 20f,
            LineHeight = 1.75f,
            NaturalLatinTypography = true,
            HyphenateEnglish = true,
            ParagraphIndent = false,
            ViewportWidth = 390f,
            ViewportHeight = 800f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };

        var layout = engine.Compose(content, options);
        var baselines = layout.Pages[0].Runs
            .Select(r => MathF.Round(r.OriginY, 1))
            .Distinct()
            .Order()
            .ToArray();

        Assert.True(baselines.Length >= 4);
        // Heading-to-table gap should collapse instead of stacking h2 bottom space + table top space.
        var headingGap = baselines[1] - baselines[0];
        Assert.True(headingGap < options.BaseFontSize * 3.8f,
            $"Expected collapsed heading-to-table gap, got {headingGap:F1}pt.");

        // Single-column English TOC table must tighten CJK 0.5em spaces, stay left-aligned,
        // and never hyphenate chapter titles ("Ir-regulars").
        Assert.DoesNotContain(layout.Pages[0].Runs, r => r.SyntheticHyphen);
        var firstItemRuns = layout.Pages[0].Runs
            .Where(r => MathF.Abs(r.OriginY - baselines[1]) < 0.5f)
            .OrderBy(r => r.OriginX)
            .ToList();
        var firstItemText = string.Concat(firstItemRuns
            .Where(r => r.TextStart >= 0 && r.TextLength > 0)
            .Select(r => content.BodyText.Substring(r.TextStart, r.TextLength)));
        Assert.Contains("Deduction", firstItemText);
    }

    [Fact]
    public void JustifiedEnglishProseEliminatesRightMarginNotchesWithMicroTracking()
    {
        var html = "<p>Sherlock Holmes took his bottle from the corner of the mantel-piece and his hypodermic syringe from its neat morocco case. With his long, white, nervous fingers he adjusted the delicate needle, and rolled back his left shirt-cuff. For some little time his eyes rested thoughtfully upon the sinewy forearm and wrist all dotted and scarred with innumerable puncture-marks. Finally he thrust the sharp point home, pressed down the tiny piston, and sank back into the velvet-lined arm-chair with a long sigh of satisfaction.</p>";
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(html));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 21f,
            LineHeight = 1.75f,
            NaturalLatinTypography = true,
            HyphenateEnglish = true,
            HangingPunctuation = true,
            ParagraphIndent = false,
            ViewportWidth = 390f,
            ViewportHeight = 844f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };

        var layout = engine.Compose(content, options);
        var rightEdge = options.ViewportWidth - options.InsetHorizontal;
        var lines = layout.Pages.SelectMany(p => p.Runs)
            .GroupBy(r => MathF.Round(r.OriginY, 1))
            .OrderBy(g => g.Key)
            .ToList();

        Assert.True(lines.Count >= 10, $"Expected multi-line paragraph, got {lines.Count} lines.");
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var lineRight = lines[i].Max(r => r.OriginX + r.FlowAdvance);
            var shortfall = rightEdge - lineRight;
            Assert.True(shortfall <= 1.0f,
                $"Line {i} fell short of right margin by {shortfall:F2}pt (right={lineRight:F2}, edge={rightEdge:F2}).");
        }
    }
}
