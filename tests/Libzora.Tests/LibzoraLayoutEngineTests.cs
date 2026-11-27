using Libzora.Layout;
using SkiaSharp;
using Xunit;

namespace Libzora.Tests;

/// <summary>
/// Engine-level tests for the self-drawn typesetting engine. The bundled
/// KingHwaOldSong font is used so shaping exercises the real CJK vertical
/// forms; tests locate it from the App assets directory.
/// </summary>
public sealed class LibzoraLayoutEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fontPath;

    public LibzoraLayoutEngineTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("libzora-layout-tests").FullName;
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

        throw new InvalidOperationException(
            "Bundled KingHwaOldSong font not found; the layout determinism suite requires the repo font assets.");
    }

    private TypesetEngine CreateEngine()
    {
        var fonts = new TypesetFontLibrary(_fontPath);
        return new TypesetEngine(fonts);
    }

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

    private string WritePng(string name = "image.png")
    {
        var path = Path.Combine(_tempDir, name);
        using var surface = SKSurface.Create(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.CornflowerBlue);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static TypesetLayoutOptions Options(
        TypesetWritingMode mode,
        float width = 400f,
        float height = 600f) => new()
    {
        WritingMode = mode,
        BaseFontSize = 17f,
        LineHeight = 1.8f,
        ViewportWidth = width,
        ViewportHeight = height,
        InsetHorizontal = 24f,
        InsetVertical = 24f,
    };

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void SmallerBottomInsetFillsTheReleasedSpaceWithoutMovingTheTop(TypesetWritingMode mode)
    {
        var text = string.Concat(Enumerable.Repeat("春风吹过山林读书文字", 200));
        var content = new XhtmlChapterLoader().Load(WriteChapter($"<p style=\"margin:0;text-indent:0\">{text}</p>"));
        TypesetLayoutOptions OptionsWithBottom(float? bottom) => new()
        {
            WritingMode = mode, ViewportWidth = 240, ViewportHeight = 280,
            InsetHorizontal = 24, InsetVertical = 24, InsetBottom = bottom,
            BaseFontSize = 16, LineHeight = 1.5f, LetterSpacingEm = 0,
            ParagraphIndent = false, ParagraphSpacingMatchesLineHeight = true,
            OverridePublisherLineHeight = true, OverridePublisherParagraphSpacing = true,
            AvoidWidowsAndOrphans = false,
        };
        using var engine = CreateEngine();
        var ordinary = engine.Compose(content, OptionsWithBottom(null));
        var expanded = engine.Compose(content, OptionsWithBottom(8));
        Assert.Equal(16f, expanded.Options.ContentHeight - ordinary.Options.ContentHeight);
        Assert.Equal(ordinary.Pages[0].Runs[0].OriginY, expanded.Pages[0].Runs[0].OriginY);
        Assert.True(expanded.Pages[0].TextEndOffset > ordinary.Pages[0].TextEndOffset);
        Assert.True(expanded.Pages.Count <= ordinary.Pages.Count);
        Assert.All(expanded.Pages.SelectMany(page => page.Runs), run =>
            Assert.True(MeasureRunInk(engine.Fonts, run).Bottom <= expanded.Options.ViewportHeight - 8 + 0.5f));
        static IEnumerable<int> TextOffsets(ChapterLayout layout) => layout.Pages.SelectMany(page => page.Runs)
            .Where(run => run.TextStart >= 0 && run.TextLength > 0)
            .SelectMany(run => Enumerable.Range(run.TextStart, run.TextLength));
        Assert.Equal(TextOffsets(ordinary), TextOffsets(expanded));
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb, false)]
    [InlineData(TypesetWritingMode.HorizontalTb, true)]
    [InlineData(TypesetWritingMode.VerticalRl, false)]
    [InlineData(TypesetWritingMode.VerticalRl, true)]
    public void AnchoredPageBreakKeepsEveryTextOffsetExactlyOnce(TypesetWritingMode mode, bool compact)
    {
        var path = WriteChapter(string.Concat(Enumerable.Range(0, 24).Select(_ =>
            "<p>" + string.Concat(Enumerable.Repeat("春风吹过山林读书应当记住眼前这一行文字", 9)) + "</p>")));
        var oldContent = new XhtmlChapterLoader(false).Load(path);
        var newContent = new XhtmlChapterLoader(true).Load(path);
        using var engine = CreateEngine();
        var original = engine.Compose(oldContent, Options(mode, 300, 300));
        var anchor = original.Pages[original.Pages.Count / 2].TextStartOffset;
        var options = new TypesetLayoutOptions
        {
            WritingMode = mode, ViewportWidth = 300, ViewportHeight = 300,
            InsetHorizontal = 24, InsetVertical = 24, BaseFontSize = 22, LineHeight = 1.8f,
            ParagraphIndent = true, ParagraphSpacingEm = compact ? 0 : 0.7f,
            ParagraphSpacingMatchesLineHeight = compact,
        };
        var ordinary = engine.Compose(newContent, options);
        var anchored = engine.Compose(newContent, options, pageStartTextOffset: anchor);
        var page = anchored.GetPageIndexOfOffset(anchor);
        Assert.True(page > 0);
        Assert.Equal(anchor, anchored.Pages[page].TextStartOffset);
        Assert.True(page < anchored.Pages.Count - 1);

        static int[] PageOffsets(LayoutPage p) => p.Runs
            .Where(run => run.TextStart >= 0 && run.TextLength > 0)
            .SelectMany(run => Enumerable.Range(run.TextStart, run.TextLength)).ToArray();
        var expected = ordinary.Pages.SelectMany(PageOffsets).ToArray();
        var actual = anchored.Pages.SelectMany(PageOffsets).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        var adjacent = anchored.Pages.Skip(page - 1).Take(3).SelectMany(PageOffsets).ToArray();
        var adjacentStart = Array.IndexOf(expected, adjacent[0]);
        Assert.Equal(expected.Skip(adjacentStart).Take(adjacent.Length), adjacent);
        Assert.Equal(anchor, PageOffsets(anchored.Pages[page])[0]);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void AnchorInsideAtomicTextUsesTheStartOfTheUnit(TypesetWritingMode mode)
    {
        var text = string.Concat(Enumerable.Repeat("中文段落内容", 100)) + " alphabetic 中文结束";
        var content = new XhtmlChapterLoader().Load(WriteChapter($"<p>{text}</p>"));
        var word = content.BodyText.IndexOf("alphabetic", StringComparison.Ordinal);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(mode, 300, 300), pageStartTextOffset: word + 3);
        var page = layout.GetPageIndexOfOffset(word + 3);
        Assert.True(page > 0);
        Assert.Equal(word, layout.Pages[page].TextStartOffset);
        Assert.Contains(layout.Pages[page].Runs, run => run.TextStart == word && run.TextLength >= 10);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void CancellingCompositionStopsAnInProgressChapter(TypesetWritingMode mode)
    {
        using var engine = CreateEngine();
        using var cancellation = new CancellationTokenSource();
        var content = new XhtmlChapterLoader().Load(WriteChapter("<p>第一段正文。</p><p>取消后不再处理这一段。</p>"));
        var cancellable = new ChapterContent
        {
            ChapterPath = content.ChapterPath,
            BodyText = content.BodyText,
            Blocks = new CancelAfterFirstBlock(content.Blocks, cancellation),
            FragmentIds = content.FragmentIds,
            FragmentTextOffsets = content.FragmentTextOffsets
        };
        var exception = Assert.Throws<OperationCanceledException>(() => engine.Compose(cancellable, Options(mode), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.NotEmpty(engine.Compose(content, Options(mode)).Pages);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void OpeningCompositionCanStopAfterTheFirstScreen(TypesetWritingMode mode)
    {
        var path = WriteChapter(string.Concat(Enumerable.Range(0, 60).Select(index =>
            $"<p>第{index + 1}段 " + string.Concat(Enumerable.Repeat("春风吹过山林读书应当记住眼前这一行文字", 8)) + "</p>")));
        var content = new XhtmlChapterLoader(false).Load(path);
        using var engine = CreateEngine();
        var options = Options(mode);
        var full = engine.Compose(content, options);
        Assert.True(full.IsComplete);
        Assert.True(full.Pages.Count > 4);

        var stop = full.Pages[full.Pages.Count / 2].TextEndOffset;
        var partial = engine.Compose(content, options, stopAfterTextOffset: stop);

        Assert.False(partial.IsComplete);
        Assert.NotEmpty(partial.Pages);
        Assert.True(partial.Pages.Count < full.Pages.Count);
        Assert.True(partial.Pages[^1].TextEndOffset >= stop);
        Assert.False(partial.Pages[^1].EndOfChapter);
        for (var index = 0; index < partial.Pages.Count; index++)
        {
            Assert.Equal(full.Pages[index].TextStartOffset, partial.Pages[index].TextStartOffset);
            Assert.Equal(full.Pages[index].TextEndOffset, partial.Pages[index].TextEndOffset);
            Assert.Equal(full.Pages[index].Runs.Count, partial.Pages[index].Runs.Count);
        }
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void OffsetLookupMatchesTheLinearContract(TypesetWritingMode mode)
    {
        var content = new XhtmlChapterLoader(false).Load(WriteChapter(
            "<h1 id=\"start\">开始</h1>"
            + string.Concat(Enumerable.Range(0, 18).Select(index =>
                $"<p id=\"p{index}\">" + string.Concat(Enumerable.Repeat("山川湖海与城市街巷中的普通一天", 6)) + "</p>"))
            + "<p id=\"end\">结束</p>"));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(mode, 360, 420));

        static int Linear(ChapterLayout value, int offset)
        {
            var firstTextPage = -1;
            var lastTextPage = -1;
            for (var index = 0; index < value.Pages.Count; index++)
            {
                var page = value.Pages[index];
                if (page.TextStartOffset < 0) continue;
                firstTextPage = firstTextPage < 0 ? index : firstTextPage;
                lastTextPage = index;
                if (offset < page.TextStartOffset) return index == firstTextPage ? firstTextPage : index;
                if (page.TextEndOffset < 0 || offset < page.TextEndOffset) return index;
            }
            return firstTextPage < 0 ? -1 : lastTextPage;
        }

        for (var offset = -4; offset <= layout.BodyTextLength + 4; offset += 37)
        {
            Assert.Equal(Linear(layout, offset), layout.GetPageIndexOfOffset(offset));
        }
        Assert.Equal(Linear(layout, int.MaxValue), layout.GetPageIndexOfOffset(int.MaxValue));
        Assert.Equal(Linear(layout, int.MinValue), layout.GetPageIndexOfOffset(int.MinValue));
    }

    private sealed class CancelAfterFirstBlock(IReadOnlyList<ContentBlock> blocks, CancellationTokenSource cancellation)
        : IReadOnlyList<ContentBlock>
    {
        public int Count => blocks.Count;
        public ContentBlock this[int index] => blocks[index];
        public IEnumerator<ContentBlock> GetEnumerator()
        {
            yield return blocks[0];
            cancellation.Cancel();
            for (var index = 1; index < blocks.Count; index++) yield return blocks[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Horizontal_SingleEmptyParagraphUsesNormalLineSpacing()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p>第一行<br/>第二行</p><p><br/></p><p>下一段</p>"));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        var baselines = layout.Pages.SelectMany(page => page.Runs)
            .Select(run => run.OriginY)
            .Distinct()
            .OrderBy(y => y)
            .ToArray();

        Assert.Equal(3, baselines.Length);
        Assert.InRange(Math.Abs((baselines[2] - baselines[1]) - (baselines[1] - baselines[0])), 0f, 0.01f);
    }

    [Fact]
    public void MultipleEmptyParagraphsRemainVisible()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p>前一段</p><p><br/></p><p><br/></p><p>后一段</p>"));

        Assert.Equal(4, content.Blocks.Count);
    }

    [Fact]
    public void TrailingEmptyParagraphsDoNotCreateBlankFinalPage()
    {
        var trailingBlanks = string.Concat(Enumerable.Repeat("<p><br/></p>", 25));
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<h1>第一章</h1><p>正文内容。</p>" + trailingBlanks));
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        Assert.Single(layout.Pages);
        Assert.Contains(layout.Pages[0].Runs, run => run.Glyphs.Length > 0);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void ParagraphSpacingChangesOnlyTheGapAndPreservesTextOffsets(TypesetWritingMode mode)
    {
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            "<p>甲甲<br/>甲甲</p><p>甲甲</p>"));
        using var engine = CreateEngine();
        (int Start, int Length)[]? initialOffsets = null;
        foreach (var spacing in new[] { 0f, 0.5f, 1f })
        {
            var options = new TypesetLayoutOptions
            {
                WritingMode = mode,
                BaseFontSize = 17f,
                LineHeight = 1.8f,
                ParagraphSpacingEm = spacing,
                ViewportWidth = 400f,
                ViewportHeight = 600f
            };
            var layout = engine.Compose(content, options);
            var page = Assert.Single(layout.Pages);
            var positions = page.Runs.Select(run => mode == TypesetWritingMode.HorizontalTb ? run.OriginY : -run.OriginX)
                .Distinct().Order().ToArray();
            Assert.Equal(3, positions.Length);
            var linePitch = positions[1] - positions[0];
            var paragraphPitch = positions[2] - positions[1];
            Assert.InRange(Math.Abs(linePitch - options.BodyLineHeight), 0, 0.02f);
            Assert.InRange(Math.Abs(paragraphPitch - linePitch - spacing * options.BaseFontSize), 0, 0.02f);
            Assert.Equal(content.BodyText.Length, layout.BodyTextLength);
            var offsets = page.Runs.Select(run => (run.TextStart, run.TextLength)).OrderBy(item => item.TextStart).ToArray();
            initialOffsets ??= offsets;
            Assert.Equal(initialOffsets, offsets);
        }
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void CompactParagraphSpacingUsesNormalLinePitchAndKeepsFirstLine(TypesetWritingMode mode)
    {
        var source = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            "<p>甲甲<br/>甲甲</p><p>甲甲</p>"));
        var content = new ChapterContent
        {
            ChapterPath = source.ChapterPath,
            BodyText = source.BodyText,
            Blocks =
            [
                new ContentBlock { Items = source.Blocks[0].Items, SpaceBeforeLines = 0.5f,
                    SpaceAfterLines = 0.8f, MarginBottomEm = 0.6f },
                new ContentBlock { Items = source.Blocks[1].Items, SpaceBeforeLines = 0.7f,
                    MarginTopEm = 0.4f }
            ]
        };
        using var engine = CreateEngine();
        ChapterLayout Compose(bool compact) => engine.Compose(content, new TypesetLayoutOptions
        {
            WritingMode = mode,
            BaseFontSize = 17f,
            LineHeight = 1.8f,
            ParagraphSpacingMatchesLineHeight = compact,
            ViewportWidth = 400f,
            ViewportHeight = 600f
        });
        float[] Positions(ChapterLayout layout) => Assert.Single(layout.Pages).Runs
            .Select(run => mode == TypesetWritingMode.HorizontalTb ? run.OriginY : -run.OriginX)
            .Distinct().Order().ToArray();

        var compact = Positions(Compose(true));
        var normal = Positions(Compose(false));
        Assert.Equal(3, compact.Length);
        Assert.Equal(3, normal.Length);
        Assert.InRange(Math.Abs(compact[0] - normal[0]), 0f, 0.01f);
        Assert.InRange(Math.Abs((compact[2] - compact[1]) - (compact[1] - compact[0])), 0f, 0.02f);
        Assert.True(normal[2] - normal[1] > compact[2] - compact[1]);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void ParagraphSpacingRepaginatesWithoutDroppingText(TypesetWritingMode mode)
    {
        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(WriteChapter(
            string.Concat(Enumerable.Repeat("<p>甲甲</p>", 10))));
        using var engine = CreateEngine();
        ChapterLayout Compose(float spacing) => engine.Compose(content, new TypesetLayoutOptions
        {
            WritingMode = mode,
            BaseFontSize = 17f,
            LineHeight = 1.8f,
            ParagraphSpacingEm = spacing,
            ViewportWidth = 120f,
            ViewportHeight = 120f
        });
        var compact = Compose(0);
        var loose = Compose(1);
        Assert.True(loose.Pages.Count > compact.Pages.Count);
        var compactOffsets = compact.Pages.SelectMany(page => page.Runs).Select(run => (run.TextStart, run.TextLength)).ToArray();
        var looseOffsets = loose.Pages.SelectMany(page => page.Runs).Select(run => (run.TextStart, run.TextLength)).ToArray();
        Assert.Equal(compactOffsets, looseOffsets);
        Assert.Equal(compact.BodyTextLength, loose.BodyTextLength);
    }

    [Fact]
    public void Horizontal_PaginationCoversTextWithoutOverflow()
    {
        var longParagraph = string.Concat(Enumerable.Range(0, 12).Select(i =>
            $"这是一段足够长的中文正文第{i}句，用来验证分页器会把整章内容切成多页而不会丢失任何文字，也不会把字形画出页面边界之外。"));
        var path = WriteChapter(
            $"<p>{longParagraph}</p>"
            + "<p>The second paragraph mixes English words with 中文标点，用来检验混排断行。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        Assert.Contains("分页器", content.BodyText);
        Assert.True(content.Blocks.Count >= 2);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        Assert.True(layout.Pages.Count >= 2, $"expected multiple pages, got {layout.Pages.Count}");
        Assert.Equal(content.BodyText.Length, layout.BodyTextLength);

        var covered = 0;
        foreach (var page in layout.Pages)
        {
            if (page.TextStartOffset < 0)
            {
                continue;
            }

            Assert.True(page.TextEndOffset >= page.TextStartOffset);
            covered += page.TextEndOffset - page.TextStartOffset;
            foreach (var run in page.Runs)
            {
                Assert.True(run.Glyphs.Length > 0);
                for (var i = 0; i < run.X.Length; i++)
                {
                    var x = run.OriginX + run.X[i];
                    Assert.InRange(x, 0f, page.Width);
                }
            }
        }

        // Every non-whitespace character must land on some page.
        var nonWhitespace = content.BodyText.Count(c => !char.IsWhiteSpace(c));
        Assert.True(covered >= nonWhitespace, $"covered {covered} < {nonWhitespace}");
    }

    [Fact]
    public void Horizontal_JustifiedLinesStayInsideTheContentBox()
    {
        var paragraph = string.Concat(Enumerable.Range(0, 18).Select(_ =>
            "两端对齐会把行内字距均匀铺满内容宽度，这一行必须和其他行一样停在右侧边界之前。"));
        var path = WriteChapter($"<p>{paragraph}</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 360f, height: 520f);
        var layout = engine.Compose(content, options);
        var rightEdge = options.ViewportWidth - options.InsetHorizontal;

        foreach (var page in layout.Pages)
        {
            foreach (var run in page.Runs)
            {
                Assert.True(
                    run.OriginX + run.FlowAdvance <= rightEdge + 0.5f,
                    $"run ends at {run.OriginX + run.FlowAdvance}, content edge is {rightEdge}");
            }
        }
    }

    [Fact]
    public void Blockquote_DoesNotAddAnArtificialVerticalBar()
    {
        var path = WriteChapter("<blockquote><p>引用内容也应该保持普通正文的干净排版。</p></blockquote>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            Assert.DoesNotContain(
                layout.Pages.SelectMany(page => page.Decorations),
                decoration => decoration.Kind == DecorationKind.BlockquoteBar);
        }
    }

    [Fact]
    public void Horizontal_BreaksAnUnbreakableTokenThatExceedsTheContentBox()
    {
        var token = new string('W', 400);
        var path = WriteChapter($"<p>{token}</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 300f, height: 520f);
        var layout = engine.Compose(content, options);
        var rightEdge = options.ViewportWidth - options.InsetHorizontal;

        Assert.True(layout.Pages.Count > 1, $"expected split token to occupy multiple pages, got {layout.Pages.Count}");
        foreach (var page in layout.Pages)
        {
            foreach (var run in page.Runs)
            {
                Assert.True(
                    run.OriginX + run.FlowAdvance <= rightEdge + 0.5f,
                    $"unbreakable token ends at {run.OriginX + run.FlowAdvance}, content edge is {rightEdge}");
            }
        }
    }

    [Fact]
    public void Horizontal_NoLineStartsWithClosingPunctuation()
    {
        var path = WriteChapter("<p>书籍阅读器需要处理禁则，例如标点“。」”这样的组合，以及多个连续的）」」绝不能出现在行首，除非悬挂在边距里。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb, width: 260f));

        foreach (var page in layout.Pages)
        {
            var lines = page.Runs
                .GroupBy(r => MathF.Round(r.OriginY, 1))
                .Select(g => g.OrderBy(r => r.OriginX).First())
                .ToList();
            foreach (var line in lines)
            {
                if (line.TextStart < 0 || line.TextLength == 0)
                {
                    continue;
                }

                var first = content.BodyText[line.TextStart];
                Assert.False(
                    TypesetText.IsProhibitedAtLineStart(first) && line.OriginX <= 24f + 1f,
                    $"line starts with prohibited mark '{first}'");
            }
        }
    }

    [Fact]
    public void Vertical_DigitPolicyMatchesLegacyRules()
    {
        var path = WriteChapter("<p>第12章：到2026年，第7卷已经写完3卷，readme 与 AT&amp;T 保持原样。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);
        Assert.Contains("AT", content.BodyText);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        var combinedRuns = new List<PlacedRun>();
        var sidewaysRuns = new List<PlacedRun>();
        foreach (var page in layout.Pages)
        {
            foreach (var run in page.Runs)
            {
                if (run.TextLength == 2 && content.BodyText.Substring(run.TextStart, run.TextLength) == "12")
                {
                    combinedRuns.Add(run);
                }

                if (run.Sideways)
                {
                    sidewaysRuns.Add(run);
                }
            }
        }

        Assert.True(combinedRuns.Count >= 1, "two-digit year/chapter number must form one tate-chu-yoko cell");
        Assert.True(sidewaysRuns.Count >= 1, "latin phrases and long digit runs must stay atomic sideways runs");
        var sidewaysText = string.Concat(sidewaysRuns.Select(r =>
            content.BodyText.Substring(r.TextStart, r.TextLength)));
        Assert.Contains("2026", sidewaysText);
        Assert.Contains("readme", sidewaysText);
        Assert.Contains("AT&T", sidewaysText);
    }

    [Fact]
    public void Vertical_PunctuationUsesMixedOrientationAndCentersNarrowDigits()
    {
        var path = WriteChapter("<p>甲；乙;丙~丁—戊（2）己（12）庚(2)辛。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl);
        var layout = engine.Compose(content, options);

        PlacedRun RunFor(char character, int occurrence = 0)
        {
            var offset = content.BodyText
                .Select((value, index) => (value, index))
                .Where(pair => pair.value == character)
                .Skip(occurrence)
                .Select(pair => pair.index)
                .First();
            return layout.Pages
                .SelectMany(page => page.Runs)
                .Single(run => run.TextStart == offset && run.TextLength == 1);
        }

        // ASCII marks are UAX #50 R characters and must be rotated in mixed
        // vertical text. They must not be allowed to remain horizontal just
        // because the selected font lacks a vertical alternate.
        Assert.True(RunFor(';').Sideways);
        Assert.True(RunFor('~').Sideways);
        Assert.True(RunFor('—').Sideways);
        Assert.True(RunFor('(').Sideways);

        // Fullwidth/CJK punctuation is Tr/Tu and uses the font's explicit
        // vertical presentation glyph instead of the ASCII fallback.
        Assert.False(RunFor('；').Sideways);
        Assert.False(RunFor('（').Sideways);
        Assert.False(RunFor('）').Sideways);

        // The narrow Arabic digit is centered by its actual shaped advance,
        // not by an assumed one-em glyph box. It therefore shares the exact
        // cross-flow center with the surrounding fullwidth parentheses.
        var opening = RunFor('（');
        var digit = RunFor('2');
        Assert.NotEmpty(opening.X);
        Assert.NotEmpty(digit.X);
        var digitShape = engine.Fonts is { } fonts
            ? new GlyphShaper(fonts).Shape("2", 0, 1, fonts.MainFontPath, options.BaseFontSize)
            : throw new InvalidOperationException("engine font library is unavailable");
        var digitCenter = digit.OriginX + digit.X[0] + digitShape.Advances[0] / 2f;
        var openingRect = layout.GetCharRect(
            layout.GetPageIndexOfOffset(content.BodyText.IndexOf('（')),
            content.BodyText.IndexOf('（'));
        Assert.NotNull(openingRect);
        Assert.InRange(
            Math.Abs(digitCenter - openingRect!.Value.MidX),
            0f,
            0.05f);

        Assert.True(TypesetText.ShouldRotateInVertical(';'));
        Assert.True(TypesetText.ShouldRotateInVertical('~'));
        Assert.True(TypesetText.ShouldRotateInVertical('—'));
        Assert.False(TypesetText.ShouldRotateInVertical('；'));
        Assert.False(TypesetText.ShouldRotateInVertical('（'));
        Assert.True(TypesetText.ShouldRotateInVertical('－'));
        Assert.False(TypesetText.ShouldRotateInVertical('○'));
    }

    [Fact]
    public void Vertical_WhiteCircleUsedAsNumericZeroIsCenteredInItsCell()
    {
        var path = WriteChapter("<p>前四○三年</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var zero = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == "○");

        Assert.False(zero.Sideways);
        using var font = new SKFont(engine.Fonts.GetTypeface(_fontPath), zero.FontSize * zero.Scale);
        _ = font.GetGlyphWidths(zero.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        var actualCenterX = zero.OriginX
            + zero.X[0]
            + (bounds[0].Left + bounds[0].Right) / 2f;
        var expectedCenterX = zero.OriginX + zero.CellWidth / 2f;
        Assert.InRange(Math.Abs(actualCenterX - expectedCenterX), 0f, 0.75f);
    }

    [Fact]
    public void Vertical_ChineseInterpunctIsCenteredInItsCell()
    {
        var path = WriteChapter("<p>杰弗里·韦斯特</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var dot = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == "·");

        Assert.False(dot.Sideways);
        using var font = new SKFont(engine.Fonts.GetTypeface(_fontPath), dot.FontSize * dot.Scale);
        _ = font.GetGlyphWidths(dot.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        var actualCenterX = dot.OriginX
            + dot.X[0]
            + (bounds[0].Left + bounds[0].Right) / 2f;
        var actualCenterY = dot.OriginY
            + dot.Y[0]
            + (bounds[0].Top + bounds[0].Bottom) / 2f;
        var expectedCenterX = dot.OriginX + dot.CellWidth / 2f;
        var cellPitch = options.BaseFontSize + options.LetterSpacingEm * options.BaseFontSize;
        var expectedCenterY = options.InsetVertical + (dot.TextStart + 0.5f) * cellPitch;

        Assert.InRange(Math.Abs(actualCenterX - expectedCenterX), 0f, 0.75f);
        Assert.InRange(Math.Abs(actualCenterY - expectedCenterY), 0f, 0.75f);
    }

    [Theory]
    [InlineData("·")]
    [InlineData("•")]
    [InlineData("‧")]
    [InlineData("・")]
    [InlineData("∙")]
    public void Vertical_NameConnectorDotsStayUprightAndCentered(string dotChar)
    {
        var path = WriteChapter($"<p>列夫{dotChar}托尔斯泰</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var dot = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == dotChar);

        Assert.False(dot.Sideways);
        using var font = new SKFont(engine.Fonts.GetTypeface(dot.FontPath), dot.FontSize * dot.Scale);
        _ = font.GetGlyphWidths(dot.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        if (bounds[0].Width > 0f && bounds[0].Height > 0f)
        {
            var actualCenterX = dot.OriginX
                + dot.X[0]
                + (bounds[0].Left + bounds[0].Right) / 2f;
            var expectedCenterX = dot.OriginX + dot.CellWidth / 2f;
            Assert.InRange(Math.Abs(actualCenterX - expectedCenterX), 0f, 0.75f);
        }
    }

    [Fact]
    public void Vertical_SelectionBandMatchesTextWidthAndUnifiesMixedRuns()
    {
        var path = WriteChapter("<p>甲乙ABC丙26丁—戊</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 240f, height: 320f);
        var layout = engine.Compose(content, options);

        var bands = layout.GetOverlayRects(0, 0, content.BodyText.Length);
        var band = Assert.Single(bands);
        var expectedWidth = MathF.Min(options.BodyLineHeight, options.BaseFontSize * 1.12f);
        Assert.InRange(Math.Abs(band.Width - expectedWidth), 0f, 0.25f);
        Assert.True(band.Width < options.BodyLineHeight - 1f,
            "Vertical selection band should hug text width instead of filling the full inter-column line pitch.");
    }

    [Fact]
    public void Vertical_KinsokuBacktracksWholeClosingCluster()
    {
        var path = WriteChapter("<p>甲乙丙」。丁戊己庚辛壬癸。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);

        foreach (var page in layout.Pages)
        {
            var columns = page.Runs
                .Where(run => run.TextLength > 0)
                .GroupBy(run => MathF.Round(run.OriginX, 1));
            foreach (var column in columns)
            {
                var ordered = column.OrderBy(run => run.OriginY).ToList();
                var first = ordered[0];
                var firstCharacter = content.BodyText[first.TextStart];
                Assert.False(
                    TypesetText.IsProhibitedAtLineStart(firstCharacter),
                    $"column starts with prohibited mark '{firstCharacter}'");

                var last = ordered[^1];
                var lastCharacter = content.BodyText[last.TextStart + last.TextLength - 1];
                var nextOffset = last.TextStart + last.TextLength;
                if (nextOffset < content.BodyText.Length)
                {
                    Assert.False(
                        TypesetText.IsProhibitedAtLineEnd(lastCharacter),
                        $"column ends with opening mark '{lastCharacter}'");
                }
            }
        }
    }

    [Fact]
    public void Vertical_NumericAffixesStayInTheSameColumn()
    {
        var path = WriteChapter("<p>甲2026年乙第12章丙。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);
        var numberOffset = content.BodyText.IndexOf("2026", StringComparison.Ordinal);
        var yearOffset = content.BodyText.IndexOf('年');
        var chapterOffset = content.BodyText.IndexOf("第12", StringComparison.Ordinal);
        var chapterNumberOffset = chapterOffset + 1;

        var numberPage = layout.GetPageIndexOfOffset(numberOffset);
        var yearPage = layout.GetPageIndexOfOffset(yearOffset);
        var chapterPage = layout.GetPageIndexOfOffset(chapterOffset);
        var chapterNumberPage = layout.GetPageIndexOfOffset(chapterNumberOffset);

        Assert.Equal(numberPage, yearPage);
        Assert.Equal(chapterPage, chapterNumberPage);

        var numberRun = layout.Pages[numberPage].Runs.Single(run =>
            run.TextStart == numberOffset && run.TextLength == 4);
        var yearRun = layout.Pages[yearPage].Runs.Single(run =>
            run.TextStart == yearOffset && run.TextLength == 1);
        Assert.InRange(
            Math.Abs(numberRun.OriginX - yearRun.OriginX),
            0f,
            options.BodyLineHeight * 0.75f);
    }

    [Fact]
    public void Vertical_ThousandsGroupingSpaceStaysInsideOneNumericRun()
    {
        var path = WriteChapter("<p>公司资产达到5 000亿美元。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(
            content,
            Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f));
        var numberOffset = content.BodyText.IndexOf("5 000", StringComparison.Ordinal);
        var numberRun = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => run.TextStart == numberOffset);

        Assert.True(numberRun.Sideways);
        Assert.Equal(5, numberRun.TextLength);
        Assert.DoesNotContain(
            layout.Pages.SelectMany(page => page.Runs),
            run => run.TextStart == numberOffset + 1 && run.TextLength == 1);
    }

    [Fact]
    public void Vertical_OpticallyCentersIdeographicZeroInItsCell()
    {
        var path = WriteChapter("<p>前四〇三年</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 220f);
        var layout = engine.Compose(content, options);
        var zero = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => content.BodyText.Substring(run.TextStart, run.TextLength) == "〇");

        using var font = new SKFont(engine.Fonts.GetTypeface(_fontPath), zero.FontSize * zero.Scale);
        _ = font.GetGlyphWidths(zero.Glyphs.AsSpan(), out var bounds, null);
        Assert.Single(bounds);

        var actualCenterY = zero.OriginY
            + zero.Y[0]
            + (bounds[0].Top + bounds[0].Bottom) / 2f;
        var cellPitch = options.BaseFontSize + options.LetterSpacingEm * options.BaseFontSize;
        var expectedCenterY = options.InsetVertical
            + (zero.TextStart + 0.5f) * cellPitch;

        Assert.InRange(Math.Abs(actualCenterY - expectedCenterY), 0f, 0.55f);
    }

    [Fact]
    public void Vertical_OversizedSidewaysRunSplitsAtTextElementBoundaries()
    {
        var token = new string('A', 260);
        var path = WriteChapter($"<p>{token}</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);
        var runs = layout.Pages
            .SelectMany(page => page.Runs)
            .Where(run => run.Sideways && run.TextLength > 0)
            .OrderBy(run => run.TextStart)
            .ToList();

        Assert.True(runs.Count > 1, "the oversized sideways run should be split");
        Assert.All(runs, run => Assert.InRange(run.FlowAdvance, 0f, options.ContentHeight + 0.5f));
        Assert.Equal(token.Length, runs.Sum(run => run.TextLength));
        for (var index = 1; index < runs.Count; index++)
        {
            Assert.Equal(
                runs[index - 1].TextStart + runs[index - 1].TextLength,
                runs[index].TextStart);
        }
    }

    [Fact]
    public void Vertical_HonorsPublisherCombineAndOrientationStyles()
    {
        var path = WriteChapter(
            "<p style=\"text-combine-upright: digits 4\">1234</p>"
            + "<p style=\"text-orientation: upright\">AB</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));
        var combined = layout.Pages
            .SelectMany(page => page.Runs)
            .Single(run => run.TextLength == 4);
        var uprightLetters = layout.Pages
            .SelectMany(page => page.Runs)
            .Where(run => run.TextLength == 1
                && content.BodyText[run.TextStart] is 'A' or 'B')
            .ToList();

        Assert.False(combined.Sideways);
        Assert.True(combined.Scale < 1f);
        Assert.Equal(2, uprightLetters.Count);
        Assert.All(uprightLetters, run => Assert.False(run.Sideways));
    }

    [Fact]
    public void KinsokuStringChecksUseTheCorrectEndOfTheTextElement()
    {
        Assert.True(TypesetText.IsProhibitedAtLineEnd("字（"));
        Assert.False(TypesetText.IsProhibitedAtLineEnd("（字"));
        Assert.True(TypesetText.ShouldKeepTogether("2026", "年"));
        Assert.True(TypesetText.ShouldKeepTogether("第", "12"));
    }

    [Fact]
    public void Vertical_CommonPunctuationMatchesUax50OrientationClasses()
    {
        const string rotated = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"
            + "‐‑‒–—―…";
        const string upright = "§©®±×÷‖†‡‰‱※‼⁇⁈⁉⁂⁑∞∴∵·•‧∙⋅・･";
        const string transformed = "‘’“”〈〉〈〉《》「」『』【】〔〕〖〗〘〙〚〛"
            + "〝〞〟〜〰゠ー﹙﹚﹛﹜﹝﹞（）：；［］＿｛｝｜～｟｠￣";

        foreach (var character in rotated)
        {
            Assert.True(
                TypesetText.ShouldRotateInVertical(character),
                $"{character} should be a sideways R character");
        }

        foreach (var character in upright)
        {
            Assert.False(
                TypesetText.ShouldRotateInVertical(character),
                $"{character} should remain upright");
        }

        foreach (var character in transformed)
        {
            Assert.True(TypesetText.IsVerticalTransformed(character), $"{character} should use vert");
            Assert.False(TypesetText.ShouldRotateInVertical(character));
        }

        Assert.False(TypesetText.ShouldRotateInVertical('·'));
    }

    [Fact]
    public void Vertical_OffsetStreamStaysMonotonicAcrossPages()
    {
        var body = string.Concat(Enumerable.Range(0, 40).Select(i => $"<p>第{i}段：竖排分页必须保持字符偏移随页序单调递增，这是进度恢复与跨端同步的前提。</p>"));
        var path = WriteChapter(body);
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        Assert.True(layout.Pages.Count > 1);
        var previous = -1;
        foreach (var page in layout.Pages)
        {
            if (page.TextStartOffset < 0)
            {
                continue;
            }

            Assert.True(page.TextStartOffset >= previous, "page text offsets must be monotonic");
            previous = page.TextStartOffset;
            foreach (var run in page.Runs)
            {
                Assert.InRange(run.OriginX, 0f, page.Width);
                Assert.InRange(run.OriginY, 0f, page.Height + 40f); // hanging marks may enter the margin
            }
        }
    }

    [Fact]
    public void Compose_IsDeterministicAcrossRuns()
    {
        var path = WriteChapter("<p>确定性测试：同一本书在 Windows、Linux 与 macOS 上必须得到逐字节相同的分页结果，否则进度同步就没有意义。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        string first;
        using (var engine = CreateEngine())
        {
            first = PageModelJson.Serialize(engine.Compose(content, Options(TypesetWritingMode.VerticalRl)));
        }

        string second;
        using (var engine = CreateEngine())
        {
            second = PageModelJson.Serialize(engine.Compose(content, Options(TypesetWritingMode.VerticalRl)));
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public void Interaction_OffsetPageMappingAndHitTestRoundTrip()
    {
        var path = WriteChapter("<p>命中测试与偏移映射是选择、批注与搜索高亮的基础。</p><p>第二段文本提供更多偏移量供断言使用。</p>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        var firstCharOffset = content.BodyText.IndexOf("命中", StringComparison.Ordinal);
        Assert.True(firstCharOffset >= 0);
        var pageIndex = layout.GetPageIndexOfOffset(firstCharOffset);
        Assert.True(pageIndex >= 0);

        var rect = layout.GetCharRect(pageIndex, firstCharOffset);
        Assert.NotNull(rect);
        Assert.True(rect.Value.Width > 0);

        var hit = layout.HitTest(pageIndex, new SKPoint(rect.Value.Left + 2f, (rect.Value.Top + rect.Value.Bottom) / 2f));
        Assert.True(hit >= firstCharOffset && hit <= firstCharOffset + 2, $"hit {hit} not near {firstCharOffset}");

        var overlays = layout.GetOverlayRects(pageIndex, firstCharOffset, 2);
        Assert.NotEmpty(overlays);
        Assert.Equal(-1, layout.HitTest(pageIndex, new SKPoint(0, 0)));
    }

    [Fact]
    public void Interaction_HitTestCharacterKeepsTheGlyphPastItsMidpoint()
    {
        var path = WriteChapter("<p>甲乙丙丁</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        var offset = content.BodyText.IndexOf('乙');
        Assert.True(offset >= 0);
        var pageIndex = layout.GetPageIndexOfOffset(offset);
        var rect = layout.GetCharRect(pageIndex, offset);
        Assert.NotNull(rect);
        var midY = (rect.Value.Top + rect.Value.Bottom) / 2;

        Assert.Equal(offset, layout.HitTestCharacter(pageIndex, new SKPoint(rect.Value.Left + 1, midY)));
        // A long press past the midpoint still selects 乙; the caret hit test
        // deliberately advances to the boundary before 丙.
        Assert.Equal(offset, layout.HitTestCharacter(pageIndex, new SKPoint(rect.Value.Right - 1, midY)));
        Assert.Equal(offset + 1, layout.HitTest(pageIndex, new SKPoint(rect.Value.Right - 1, midY)));
    }

    [Fact]
    public void Paint_HorizontalTextProducesInkInsideThePage()
    {
        var path = WriteChapter("<p>横排自绘文字必须落在基线附近，而不是被重复减去绝对基线后画到页面外。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));

        using var surface = SKSurface.Create(new SKImageInfo(400, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var fonts = new TypesetFontLibrary(_fontPath);
        new TypesetPainter(fonts).Paint(surface.Canvas, layout.Pages[0]);
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        var darkPixels = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128)
                    darkPixels++;
            }
        }

        Assert.True(darkPixels > 10, $"expected horizontal glyph ink, found {darkPixels} dark samples");
        var first = content.BodyText.IndexOf('横');
        var page = layout.GetPageIndexOfOffset(first);
        var rect = layout.GetCharRect(page, first);
        Assert.True(rect is { Top: >= 0, Bottom: <= 600 });
    }

    [Fact]
    public void Vertical_FirstImageDoesNotCreateAnEmptyPage()
    {
        var imagePath = WritePng();
        var path = WriteChapter("<img id=\"cover\" src=\"image.png\"/><p>图片之后的正文。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        Assert.True(layout.Pages.Count >= 2);
        Assert.Contains(layout.Pages[0].Images, image => image.Path == imagePath);
        Assert.NotEmpty(layout.Pages[1].Runs);
        Assert.Equal(0, layout.GetPageIndexOfFragment("cover"));
    }

    [Fact]
    public void Loader_ResolvesImageQueryAndInlineFootnoteHotZone()
    {
        var imagePath = WritePng();
        var path = Path.Combine(_tempDir, "linked.xhtml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body>
                <p id="intro"><span id="inline-anchor">正文<a href="#fn1" epub:type="noteref">注</a></span></p>
                <img src="image.png?cache=1#cover"/>
                <aside id="fn1" epub:type="footnote"><p>脚注定义。</p></aside>
              </body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);
        var imageBlock = Assert.Single(content.Blocks, block => block.Kind == BlockKind.Image);
        Assert.Equal(imagePath, imageBlock.Items[0].ImagePath);
        Assert.Contains("inline-anchor", content.Blocks[0].FragmentIds);

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        Assert.True(layout.GetPageIndexOfFragment("inline-anchor") >= 0);
        var hotZone = Assert.Single(layout.Pages.SelectMany(pageItem => pageItem.HotZones));
        Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
        Assert.Contains("#fn1", hotZone.Href, StringComparison.Ordinal);
        Assert.Equal(hotZone, layout.GetHotZoneAt(layout.GetPageIndexOfFragment("intro"),
            new SKPoint((hotZone.Rect.Left + hotZone.Rect.Right) / 2f, (hotZone.Rect.Top + hotZone.Rect.Bottom) / 2f)));
    }

    [Fact]
    public void Loader_ConvertsPlainNumberedNotesIntoHoverableFootnotes()
    {
        var path = WriteChapter(
            "<p>正文第一处（1），第二处[2]。</p>"
            + "<p>【注释】</p>"
            + "<p>（1）</p>"
            + "<p>第一条注释说明。</p>"
            + "<p>[2]</p>"
            + "<p>第二条注释说明。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var markers = content.Blocks
            .SelectMany(block => block.Items)
            .Where(item => item.Kind == InlineKind.FootnoteMarker)
            .ToList();

        Assert.Equal(2, markers.Count);
        Assert.Equal("（1）", markers[0].Text);
        Assert.Equal("第一条注释说明。", markers[0].FootnoteText);
        Assert.Equal("[2]", markers[1].Text);
        Assert.Equal("第二条注释说明。", markers[1].FootnoteText);
        // Plain notes have no element anchor; the synthetic fragment carries
        // the note's body-text offset so the reader can jump to its definition.
        foreach (var (marker, definition) in markers.Zip(new[] { "（1）", "[2]" }))
        {
            Assert.NotNull(marker.FootnoteHref);
            Assert.Contains("__libzora-plain-footnote-", marker.FootnoteHref, StringComparison.Ordinal);
            var offset = int.Parse(marker.FootnoteHref!.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(offset, 0, content.BodyText.Length - definition.Length);
            Assert.StartsWith(definition, content.BodyText[offset..]);
        }
        var firstNoteBlock = Assert.Single(content.Blocks, block =>
            block.Items.Any(item => item.Text == "第一条注释说明。"));
        Assert.Contains(firstNoteBlock.Items, item => item.Text == "（1）");
        var secondNoteBlock = Assert.Single(content.Blocks, block =>
            block.Items.Any(item => item.Text == "第二条注释说明。"));
        Assert.Contains(secondNoteBlock.Items, item => item.Text == "[2]");
        Assert.Contains("（1）", content.BodyText);
        Assert.Contains("第二条注释说明", content.BodyText);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var hotZones = layout.Pages
                .SelectMany(page => page.HotZones)
                .Where(zone => zone.Kind == HotZoneKind.FootnoteMarker)
                .ToList();

            Assert.Equal(2, hotZones.Count);
            Assert.Contains(hotZones, zone => zone.FootnoteText == "第一条注释说明。");
            Assert.Contains(hotZones, zone => zone.FootnoteText == "第二条注释说明。");
        }
    }

    [Fact]
    public void Loader_AssociatesRepeatedPlainNoteSectionsWithTheirOwnBodyMarkers()
    {
        var path = WriteChapter(
            "<p>第一段正文（1）。</p>"
            + "<p>【注释】</p>"
            + "<p>（1）第一组注释。</p>"
            + "<p>【译文】</p>"
            + "<p>第一组译文。</p>"
            + "<p>第二段正文（1），另一个标记(2)。</p>"
            + "<p>【注释】</p>"
            + "<p>（1）第二组第一条。</p>"
            + "<p>（2）第二组第二条。</p>"
            + "<p>【译文】</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var markers = content.Blocks
            .SelectMany(block => block.Items)
            .Where(item => item.Kind == InlineKind.FootnoteMarker)
            .ToList();

        Assert.Equal(3, markers.Count);
        Assert.Equal("第一组注释。", markers[0].FootnoteText);
        Assert.Equal("第二组第一条。", markers[1].FootnoteText);
        Assert.Equal("第二组第二条。", markers[2].FootnoteText);
        Assert.NotEqual(markers[0].FootnoteHref, markers[1].FootnoteHref);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var hotZones = layout.Pages
                .SelectMany(page => page.HotZones)
                .Where(zone => zone.Kind == HotZoneKind.FootnoteMarker)
                .ToList();

            Assert.Equal(3, hotZones.Count);
            Assert.Contains(hotZones, zone => zone.FootnoteText == "第一组注释。");
            Assert.Contains(hotZones, zone => zone.FootnoteText == "第二组第一条。");
            Assert.Contains(hotZones, zone => zone.FootnoteText == "第二组第二条。");
        }
    }

    [Fact]
    public void Loader_RecognizesCalibreSuperscriptFootnoteEndnotes()
    {
        // Calibre leaves no epub:type, class or noteref attribute. The only
        // signals are the superscript wrapper, the bracketed marker, and a
        // cross-document fragment target such as "#filepos…".
        var path = WriteChapter(
            "<p>“既然野人<sup class=\"calibre5\"><small id=\"filepos12816\" class=\"calibre6\">"
            + "<a href=\"notes.xhtml#filepos652450\"><span class=\"calibre7\">[1]</span></a>"
            + "</small></sup>已经死了，”</p>"
            + "<p id=\"filepos652450\" class=\"calibre_\">"
            + "<a href=\"body.xhtml#filepos12816\">[1]</a>野人：指居住在绝境长城以北的人。</p>"
            + "<p id=\"filepos652669\" class=\"calibre_\">"
            + "<a href=\"body.xhtml#filepos14617\">[2]</a>守夜人：一支驻守长城的部队。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var marker = Assert.Single(
            content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);

        Assert.Equal("[1]", marker.Text);
        Assert.EndsWith("#filepos652450", marker.FootnoteHref, StringComparison.Ordinal);

        // The definitions are still in the offset stream for search and
        // annotations, but they must not occupy reading pages.
        Assert.Contains("野人：指居住在绝境长城以北的人。", content.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("野人：指居住在绝境长城以北的人。", StringComparison.Ordinal));
        Assert.Contains("filepos652450", content.FragmentIds);
        Assert.True(content.FragmentTextOffsets.ContainsKey("filepos652450"));
        Assert.True(content.FragmentTextOffsets.ContainsKey("filepos652669"));

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        var hotZone = Assert.Single(layout.Pages.SelectMany(page => page.HotZones));
        Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
        Assert.EndsWith("#filepos652450", hotZone.Href, StringComparison.Ordinal);
        Assert.Equal("[1]", hotZone.Marker);
    }

    [Fact]
    public void Loader_ResolvesUnlinkedBracketEndnotesFromCompanionEndnoteChapter()
    {
        var epubTextDir = Path.Combine(_tempDir, "companion-endnotes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(epubTextDir);

        var chapter1Path = Path.Combine(epubTextDir, "part0007.xhtml");
        var chapter5Path = Path.Combine(epubTextDir, "part0012.xhtml");
        var chapter21Path = Path.Combine(epubTextDir, "part0028.xhtml");
        var endnotePath = Path.Combine(epubTextDir, "part0029.xhtml");

        File.WriteAllText(
            chapter1Path,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>引　子</title></head>
              <body>
                <p class="title1">引　子</p>
                <p>第一段引文<sup>［1］</sup>与译者注<sup><a id="fhzs1" href="part0007.xhtml#zhushi1">〔1〕</a></sup>并存，后接第二处<sup>［2］</sup>。</p>
                <p class="zy1" id="zhushi1"><a href="part0007.xhtml#fhzs1">〔1〕</a>本章译者注内容。</p>
              </body>
            </html>
            """);

        File.WriteAllText(
            chapter5Path,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>第五章　斗争（1927—1935）</title></head>
              <body>
                <p class="title1">第五章　斗争（1927—1935）</p>
                <p>井冈山时期记录<sup>［1］</sup>以及无上标方括号［2］引用。</p>
              </body>
            </html>
            """);

        File.WriteAllText(
            chapter21Path,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>二十一章　飞鸣镝将坠（1976）</title></head>
              <body>
                <p class="title1">二十一章　飞鸣镝将坠（1976）</p>
                <p>尾声记录<sup>［1］</sup>。</p>
              </body>
            </html>
            """);

        File.WriteAllText(
            endnotePath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>注释</title></head>
              <body>
                <p class="title1">注　释</p>
                <p class="normaltext3">引子</p>
                <p class="normaltext">［1］引子第一条文献出处。</p>
                <p class="normaltext">［2］引子第二条文献出处。</p>
                <p class="normaltext3">第五章　斗争（1927—1935）</p>
                <p class="normaltext">注：毛泽东在这个时期的很多活动，在斯诺的《西行漫记》第四、第五章中有生动叙述，本章注释不再逐一重复这些背景说明。</p>
                <p class="normaltext">［1］第五章第一条文献出处。</p>
                <p class="normaltext">［2］第五章第二条文献出处。</p>
                <p class="normaltext3">第二十一章　飞鸣镝将坠（1976）</p>
                <p class="normaltext">［1］第二十一章第一条文献出处。</p>
              </body>
            </html>
            """);

        var loader = new XhtmlChapterLoader();
        var chapter1 = loader.Load(chapter1Path);
        var chapter1Markers = chapter1.Blocks
            .SelectMany(block => block.Items)
            .Where(item => item.Kind == InlineKind.FootnoteMarker)
            .ToList();

        Assert.Equal(3, chapter1Markers.Count);
        Assert.Equal("［1］", chapter1Markers[0].Text);
        Assert.Equal("引子第一条文献出处。", chapter1Markers[0].FootnoteText);
        Assert.Contains("part0029.xhtml#__libzora-plain-footnote-", chapter1Markers[0].FootnoteHref, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("〔1〕", chapter1Markers[1].Text);
        Assert.Contains("part0007.xhtml#zhushi1", chapter1Markers[1].FootnoteHref, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("［2］", chapter1Markers[2].Text);
        Assert.Equal("引子第二条文献出处。", chapter1Markers[2].FootnoteText);

        var chapter5 = loader.Load(chapter5Path);
        var chapter5Markers = chapter5.Blocks
            .SelectMany(block => block.Items)
            .Where(item => item.Kind == InlineKind.FootnoteMarker)
            .ToList();

        Assert.Equal(2, chapter5Markers.Count);
        Assert.Equal("第五章第一条文献出处。", chapter5Markers[0].FootnoteText);
        Assert.Equal("第五章第二条文献出处。", chapter5Markers[1].FootnoteText);

        var chapter21 = loader.Load(chapter21Path);
        var chapter21Marker = Assert.Single(
            chapter21.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker);
        Assert.Equal("第二十一章第一条文献出处。", chapter21Marker.FootnoteText);
    }

    [Fact]
    public void Horizontal_FootnoteMarkerRisesAboveTheBaseline()
    {
        var path = Path.Combine(_tempDir, "footnote-baseline.xhtml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body>
                <p>春风吹过山林<a href="#fn1" epub:type="noteref">（1）</a>读书应当记住眼前这一行文字。</p>
                <aside id="fn1" epub:type="footnote"><p>第一条脚注说明文字。</p></aside>
              </body>
            </html>
            """);
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            WritingMode = TypesetWritingMode.HorizontalTb,
            BaseFontSize = 34f,
            LineHeight = 1.8f,
            ViewportWidth = 520f,
            ViewportHeight = 720f,
            InsetHorizontal = 28f,
            InsetVertical = 28f,
        };
        var layout = engine.Compose(content, options);
        var page = layout.Pages[0];
        var markerOffset = content.Blocks
            .SelectMany(block => block.Items)
            .First(item => item.Kind == InlineKind.FootnoteMarker)
            .TextStart;

        var marker = Assert.Single(page.Runs, run => run.Style.Superscript
            && run.TextStart <= markerOffset
            && markerOffset < run.TextStart + run.TextLength);
        var body = page.Runs
            .Where(run => !run.Style.Superscript && run.TextLength > 0
                && run.TextStart + run.TextLength == markerOffset)
            .OrderByDescending(run => run.TextStart)
            .First();

        var expectedShift = options.BaseFontSize * 0.38f;
        Assert.InRange(body.OriginY - marker.OriginY, expectedShift - 1.5f, expectedShift + 1.5f);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\t")]
    public void Horizontal_FootnoteMarkerWrapsWithItsPrecedingText(string space)
    {
        var path = WriteChapter($"""
            <p>甲乙丙丁戊{space}<a href="#fn1" role="doc-noteref">注</a>己庚。</p>
            <aside id="fn1" role="doc-footnote"><p>脚注内容。</p></aside>
            """);
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            WritingMode = TypesetWritingMode.HorizontalTb,
            BaseFontSize = 30f,
            LineHeight = 1.8f,
            ViewportWidth = 198f,
            ViewportHeight = 720f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };
        var page = engine.Compose(content, options).Pages[0];
        var precedingOffset = content.BodyText.IndexOf('戊');
        Assert.True(precedingOffset >= 0);
        var markerOffset = Assert.Single(content.Blocks.SelectMany(block => block.Items),
            item => item.Kind == InlineKind.FootnoteMarker).TextStart;

        var preceding = Assert.Single(page.Runs, run => run.TextStart <= precedingOffset
            && precedingOffset < run.TextStart + run.TextLength);
        var marker = Assert.Single(page.Runs, run => run.TextStart == markerOffset && run.Style.Superscript);
        var beginning = Assert.Single(page.Runs, run => run.TextStart == 0);
        var hotZone = Assert.Single(page.HotZones, zone => zone.Kind == HotZoneKind.FootnoteMarker);

        Assert.True(preceding.OriginY > beginning.OriginY);
        Assert.Equal(options.BaseFontSize * 0.38f, preceding.OriginY - marker.OriginY, 2);
        var precedingGlyphX = preceding.OriginX + preceding.X[^1];
        Assert.InRange(hotZone.Rect.Left - precedingGlyphX, 0.95f * options.BaseFontSize, 1.05f * options.BaseFontSize);
        Assert.Equal(options.BaseFontSize * 0.55f, marker.FontSize, 2);
        Assert.EndsWith("#fn1", hotZone.Href);
    }

    [Fact]
    public void Vertical_FootnoteMarkerSitsOnTheRightEdgeOfItsColumn()
    {
        var path = Path.Combine(_tempDir, "footnote-vertical.xhtml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body>
                <p>春风吹过山林<a href="#fn1" epub:type="noteref">（1）</a>读书应当记住眼前这一行文字。</p>
                <aside id="fn1" epub:type="footnote"><p>第一条脚注说明文字。</p></aside>
              </body>
            </html>
            """);
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            WritingMode = TypesetWritingMode.VerticalRl,
            BaseFontSize = 34f,
            LineHeight = 1.8f,
            ViewportWidth = 520f,
            ViewportHeight = 720f,
            InsetHorizontal = 28f,
            InsetVertical = 28f,
        };
        var layout = engine.Compose(content, options);
        var page = layout.Pages[0];
        var markerOffset = content.Blocks
            .SelectMany(block => block.Items)
            .First(item => item.Kind == InlineKind.FootnoteMarker)
            .TextStart;

        var marker = Assert.Single(page.Runs, run => run.Style.Superscript
            && run.TextStart <= markerOffset
            && markerOffset < run.TextStart + run.TextLength);
        var zone = Assert.Single(page.HotZones, hotZone => hotZone.Kind == HotZoneKind.FootnoteMarker);

        // A vertical note reference is a tate-chu-yoko unit: the cell is the
        // column, but the compact glyphs must lean toward the upper-right of
        // the base character instead of sitting in the column's centre.
        var inkCenter = marker.OriginX + (marker.X[0] + marker.X[^1]) / 2f
            + marker.FontSize * marker.Scale * 0.25f;
        Assert.True(
            inkCenter > zone.Rect.Left + zone.Rect.Width / 2f + 2f,
            $"marker ink centre {inkCenter:F2} should sit right of column centre "
            + $"{zone.Rect.Left + zone.Rect.Width / 2f:F2}");
    }

    [Fact]
    public void Loader_KeepsInlineImageFragmentOnTheImageBlock()
    {
        var imagePath = WritePng();
        var path = WriteChapter(
            $"<p>图片前文字<img id=\"inline-image\" src=\"{Path.GetFileName(imagePath)}\"/>图片后文字。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var imageBlock = Assert.Single(content.Blocks, block => block.Kind == BlockKind.Image);

        Assert.Contains("inline-image", imageBlock.FragmentIds);
        Assert.DoesNotContain("inline-image", content.Blocks
            .Where(block => block.Kind != BlockKind.Image)
            .SelectMany(block => block.FragmentIds));
    }

    [Fact]
    public void FormulaImagesStayInlineAndFitBothWritingModes()
    {
        var imagePath = WritePng("w2.png");
        var path = WriteChapter(
            "<p>公式前<img alt=\"w2\" src=\"w2.png\" style=\"height:1.5em;\"/>公式后。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        var paragraph = Assert.Single(content.Blocks);
        Assert.Equal(BlockKind.Paragraph, paragraph.Kind);
        var formula = Assert.Single(paragraph.Items, item => item.Kind == InlineKind.Image);
        Assert.Equal(imagePath, formula.ImagePath);
        Assert.Equal(1.5f, formula.ImageHeightEm);
        Assert.DoesNotContain(content.Blocks, block => block.Kind == BlockKind.Image);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var options = Options(mode);
            var layout = engine.Compose(content, options);
            var placed = Assert.Single(layout.Pages.SelectMany(pageItem => pageItem.Images));

            Assert.True(placed.Rect.Left >= 0f, $"formula left edge escaped in {mode}");
            Assert.True(placed.Rect.Top >= 0f, $"formula top edge escaped in {mode}");
            Assert.True(placed.Rect.Right <= options.ViewportWidth + 0.01f, $"formula right edge escaped in {mode}");
            Assert.True(placed.Rect.Bottom <= options.ViewportHeight + 0.01f, $"formula bottom edge escaped in {mode}");
            Assert.True(placed.Rect.Width < 60f, $"formula was not scaled as an inline object in {mode}");
            Assert.Equal(mode == TypesetWritingMode.VerticalRl ? 90f : 0f, placed.RotationDegrees);
        }
    }

    [Fact]
    public void CssSizedGlyphImagesStayInlineAndPreserveHeight()
    {
        var imagePath = WritePng("glyph.png");
        var cssPath = Path.Combine(_tempDir, "glyphs.css");
        File.WriteAllText(cssPath, ".glyph { height: 0.675em; width: 0.69312em; }");
        var path = Path.Combine(_tempDir, "glyph.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><link rel="stylesheet" type="text/css" href="glyphs.css" /></head>
              <body><p>字前<img class="glyph" src="{Path.GetFileName(imagePath)}" />字后</p></body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);

        var paragraph = Assert.Single(content.Blocks);
        Assert.Equal(BlockKind.Paragraph, paragraph.Kind);
        var glyph = Assert.Single(paragraph.Items, item => item.Kind == InlineKind.Image);
        Assert.Equal(imagePath, glyph.ImagePath);
        Assert.Equal(0.675f, glyph.ImageHeightEm);
        Assert.DoesNotContain(content.Blocks, block => block.Kind == BlockKind.Image);
    }

    [Fact]
    public void DecorativeQuoteImagesHonorCssWidthAndAlignment()
    {
        var leftImagePath = WritePng("yinhao-left.png");
        var rightImagePath = WritePng("yinhao-right.png");
        var cssPath = Path.Combine(_tempDir, "quotes.css");
        File.WriteAllText(cssPath, """
            .center { text-align: left; text-indent: 0em; }
            img.yinhao_l { width: 12%; }
            .yinhao_r { text-align: right; }
            .yinhao_r img { width: 12%; }
            """);
        var path = Path.Combine(_tempDir, "dedication.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><link rel="stylesheet" type="text/css" href="quotes.css"/></head>
              <body>
                <div class="juzhong3">
                  <p class="center"><img class="yinhao_l" src="{Path.GetFileName(leftImagePath)}"/><b>致</b></p>
                  <p>杰奎琳（Jacqueline）、乔舒亚（Joshua）。</p>
                  <div class="yinhao_r"><img src="{Path.GetFileName(rightImagePath)}"/></div>
                </div>
              </body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);
        var first = Assert.Single(content.Blocks, block => block.Items.Any(item => item.ImagePath == leftImagePath));
        Assert.Equal(BlockKind.Paragraph, first.Kind);
        var left = Assert.Single(first.Items, item => item.ImagePath == leftImagePath);
        Assert.Equal(0.12f, left.ImageWidthFactor);
        Assert.True(left.DecorativeQuote);

        var rightBlock = Assert.Single(content.Blocks, block => block.Items.Any(item => item.ImagePath == rightImagePath));
        Assert.Equal(BlockKind.Image, rightBlock.Kind);
        Assert.True(rightBlock.AlignRight);
        Assert.Equal(0.12f, rightBlock.Items[0].ImageWidthFactor);
        Assert.True(rightBlock.Items[0].DecorativeQuote);

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb);
        var layout = engine.Compose(content, options);
        var images = layout.Pages.SelectMany(page => page.Images).ToList();
        Assert.Equal(2, images.Count);
        foreach (var image in images)
        {
            Assert.InRange(image.Rect.Width, 0f, options.BaseFontSize * 5f + 0.01f);
        }

        var rightPlaced = Assert.Single(images, image => image.Path == rightImagePath);
        Assert.Equal(options.ViewportWidth - options.InsetHorizontal, rightPlaced.Rect.Right, 2);
    }

    [Fact]
    public void FragmentInLongParagraphResolvesToItsActualPage()
    {
        var prefix = string.Concat(Enumerable.Range(0, 60).Select(_ => "前置文字用于把锚点推到下一页。"));
        var path = WriteChapter($"<p>{prefix}<a id=\"later\" href=\"#later\">后置锚点文字。</a></p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var offset = content.BodyText.IndexOf("后置锚点", StringComparison.Ordinal);
            var expectedPage = layout.GetPageIndexOfOffset(offset);

            Assert.True(expectedPage > 0, $"test anchor should cross a page in {mode}");
            Assert.Equal(expectedPage, layout.GetPageIndexOfFragment("later"));
        }
    }

    [Fact]
    public void FragmentHeadingStartsAtTheTopOfItsPage()
    {
        var prefix = string.Concat(Enumerable.Range(0, 35).Select(_ => "前置正文用于让目录标题落在已有内容之后。"));
        var path = WriteChapter($"<p>{prefix}</p><h2 id=\"target-heading\">目标章节</h2><p>章节正文。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        var headingOffset = content.BodyText.IndexOf("目标章节", StringComparison.Ordinal);
        Assert.True(headingOffset >= 0);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var options = Options(mode);
            var layout = engine.Compose(content, options);
            var pageIndex = layout.GetPageIndexOfFragment("target-heading");
            Assert.True(pageIndex > 0, $"heading should follow a filled page in {mode}");
            Assert.Equal(pageIndex, layout.GetPageIndexOfOffset(headingOffset));

            var rect = layout.GetCharRect(pageIndex, headingOffset);
            Assert.True(rect.HasValue, $"heading glyph is missing in {mode}");
            Assert.InRange(
                rect.Value.Top,
                0f,
                options.InsetVertical + options.BaseFontSize * 1.5f);
        }
    }

    [Fact]
    public void HeadingFootnoteReferenceDoesNotForceANewPage()
    {
        var path = WriteChapter("<p>前面的正文需要继续排版，不能因为下一节标题中的脚注标记就提前结束这一页并留下大片空白。</p>"
            + "<h4>第二节<a class=\"zy\" href=\"#id6a\" id=\"id6\">〔6〕</a></h4>"
            + "<p>后面的正文。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        var heading = Assert.Single(content.Blocks, block => block.Kind == BlockKind.Heading);
        Assert.Contains("id6", heading.FootnoteReferenceFragmentIds);
        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            Assert.Single(layout.Pages);
            Assert.Equal(0, layout.GetPageIndexOfFragment("id6"));
        }
    }

    [Fact]
    public void AdjacentFragmentHeadingStaysWithThePreviousHeading()
    {
        var path = WriteChapter(
            "<h1>第一章</h1>"
            + "<h1 id=\"chapter-title\">老三</h1>"
            + "<p>章节正文应该紧跟在标题之后。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        var firstHeadingOffset = content.BodyText.IndexOf("第一章", StringComparison.Ordinal);
        var secondHeadingOffset = content.BodyText.IndexOf("老三", StringComparison.Ordinal);
        Assert.True(firstHeadingOffset >= 0);
        Assert.True(secondHeadingOffset >= 0);
        var heading = Assert.Single(content.Blocks, block => block.Kind == BlockKind.Heading);
        Assert.Equal("第一章 老三", string.Concat(heading.Items.Select(item => item.Text)));

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var firstPage = layout.GetPageIndexOfOffset(firstHeadingOffset);
            var secondPage = layout.GetPageIndexOfFragment("chapter-title");

            Assert.Equal(firstPage, secondPage);
            Assert.Equal(secondPage, layout.GetPageIndexOfOffset(secondHeadingOffset));
        }
    }

    [Fact]
    public void FigureCaptionHeadingDoesNotMergeIntoFollowingSectionHeading()
    {
        var imageName = $"fig-{Guid.NewGuid():N}.png";
        File.WriteAllBytes(Path.Combine(_tempDir, imageName), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X2S0AAAAASUVORK5CYII="));
        var path = WriteChapter(
            "<p>前一小节的正文内容需要超过四十个字符以免被自动识别为章首标题，这里继续补充一些示例文字。</p>"
            + $"<div class=\"chatu\" id=\"aid-wrap\"><img src=\"{imageName}\" alt=\"\" /><h5 id=\"aid-cap\">▲ 图5-3 航拍图</h5></div>"
            + "<h2 id=\"sigil_toc_id_38\">2. 城市、城市化和全球可持续性</h2>"
            + "<p>第二节正文。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var captionBlock = Assert.Single(content.Blocks, b => b.IsCaption);
        Assert.Equal(BlockKind.Paragraph, captionBlock.Kind);
        Assert.Contains("图5-3", string.Concat(captionBlock.Items.Select(i => i.Text)));

        var sectionHeading = Assert.Single(content.Blocks, b => b.Kind == BlockKind.Heading);
        Assert.Equal("2. 城市、城市化和全球可持续性", string.Concat(sectionHeading.Items.Select(i => i.Text)));

        var imageBlock = Assert.Single(content.Blocks, b => b.Kind == BlockKind.Image);
        Assert.True(imageBlock.KeepWithNext);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var captionPage = layout.GetPageIndexOfFragment("aid-cap");
            var sectionPage = layout.GetPageIndexOfFragment("sigil_toc_id_38");
            Assert.True(sectionPage > captionPage, $"Section heading should start on a fresh page after figure caption in {mode}");
            var sectionOffset = content.BodyText.IndexOf("2. 城市", StringComparison.Ordinal);
            Assert.Equal(sectionOffset, layout.Pages[sectionPage].TextStartOffset);
        }
    }

    [Fact]
    public void SubordinateHeadingDoesNotMergeIntoHigherLevelHeading()
    {
        var path = WriteChapter(
            "<h5 id=\"sub-label\">附注说明</h5>"
            + "<h2 id=\"toc-next\">第二章</h2>"
            + "<p>正文内容。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var headings = content.Blocks.Where(b => b.Kind == BlockKind.Heading).ToArray();
        Assert.Equal(2, headings.Length);
        Assert.Equal("附注说明", string.Concat(headings[0].Items.Select(i => i.Text)));
        Assert.Equal("第二章", string.Concat(headings[1].Items.Select(i => i.Text)));
    }

    [Fact]
    public void NavigationFragmentsPreventInternalIdSubheadingsFromBreakingPages()
    {
        var path = WriteChapter(
            "<p>第一段正文。</p>"
            + "<h3 id=\"aid-sub\">空间填充</h3>"
            + "<p>小节正文。</p>"
            + "<h2 id=\"sigil_toc_id_38\">2. 下一节</h2>"
            + "<p>第二节正文。</p>");

        var content = new XhtmlChapterLoader().Load(path);
        var options = new TypesetLayoutOptions
        {
            ViewportWidth = 420f,
            ViewportHeight = 640f,
            NavigationFragments = new HashSet<string>(["sigil_toc_id_38"], StringComparer.Ordinal),
        };

        using var engine = CreateEngine();
        var layout = engine.Compose(content, options);
        Assert.Equal(0, layout.GetPageIndexOfFragment("aid-sub"));
        Assert.Equal(1, layout.GetPageIndexOfFragment("sigil_toc_id_38"));
    }

    [Fact]
    public void HeadingToBodySpacingCollapsesPublisherMarginsAndStaysCompact()
    {
        var path = Path.Combine(_tempDir, $"chapter-{Guid.NewGuid():N}.xhtml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
            <head>
              <style>
                h2.sec { font-size: 1.125em; line-height: 1.2; margin: 2em 0 1em; }
                p.body { line-height: 1.84; margin: 1em 0 0; }
              </style>
            </head>
            <body>
              <h2 class="sec" id="toc-1">2. 城市、城市化和全球可持续性</h2>
              <p class="body">或许，我们最伟大的发明便是城市。</p>
            </body>
            </html>
            """);

        var content = new XhtmlChapterLoader(paragraphIndent: false).Load(path);
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 20f,
            LineHeight = 1.75f,
            ViewportWidth = 600f,
            ViewportHeight = 800f,
            InsetHorizontal = 24f,
            InsetVertical = 24f,
        };

        using var engine = CreateEngine();
        var layout = engine.Compose(content, options);
        var headingOffset = content.BodyText.IndexOf("2. 城市", StringComparison.Ordinal);
        var bodyOffset = content.BodyText.IndexOf("或许", StringComparison.Ordinal);
        var headingRect = layout.GetCharRect(0, headingOffset)!.Value;
        var bodyRect = layout.GetCharRect(0, bodyOffset)!.Value;
        var visualGap = bodyRect.Top - headingRect.Bottom;

        // Collapsed margin with heading breathing room instead of stacking ~84px.
        Assert.InRange(visualGap, options.BaseFontSize * 1.0f, options.BaseFontSize * 2.2f);
    }

    [Fact]
    public void Loader_PreservesCollapsedWhitespaceBetweenInlineElements()
    {
        var path = WriteChapter("<p><span>甲</span> <span>乙</span></p>");
        var content = new XhtmlChapterLoader().Load(path);
        var block = Assert.Single(content.Blocks);
        Assert.Contains(block.Items, item => item.Text == " ");

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var firstOffset = content.BodyText.IndexOf('甲');
            var secondOffset = content.BodyText.IndexOf('乙');
            var firstPage = layout.GetPageIndexOfOffset(firstOffset);
            var secondPage = layout.GetPageIndexOfOffset(secondOffset);
            Assert.Equal(firstPage, secondPage);

            var firstRect = layout.GetCharRect(firstPage, firstOffset);
            var secondRect = layout.GetCharRect(secondPage, secondOffset);
            Assert.True(firstRect.HasValue && secondRect.HasValue);
            if (mode == TypesetWritingMode.HorizontalTb)
                Assert.True(secondRect.Value.Left > firstRect.Value.Left);
            else
                Assert.True(secondRect.Value.Top > firstRect.Value.Top);
        }
    }

    [Fact]
    public void Loader_HidesFnoteDefinitionsButKeepsTheirReferenceInteractive()
    {
        var path = WriteChapter(
            "<p>正文<sup><a href=\"#note1n\" id=\"note1\">[1]</a></sup>之后。</p>"
            + "<div class=\"fnote\"><p><a href=\"#note1\" id=\"note1n\">[1]</a>这是脚注定义。</p></div>");
        var content = new XhtmlChapterLoader().Load(path);

        Assert.Contains("这是脚注定义", content.BodyText);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items), item =>
            item.Text.Contains("脚注定义", StringComparison.Ordinal));
        Assert.Contains("note1n", content.FragmentIds);
        Assert.True(content.FragmentTextOffsets.ContainsKey("note1n"));

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var hotZone = Assert.Single(
                layout.Pages.SelectMany(page => page.HotZones),
                zone => zone.Kind == HotZoneKind.FootnoteMarker
                    && zone.Href.Contains("#note1n", StringComparison.Ordinal));
            Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
            Assert.True(layout.GetPageIndexOfFragment("note1n") >= 0);
            Assert.Equal(
                layout.GetPageIndexOfOffset(content.BodyText.IndexOf("这是脚注定义", StringComparison.Ordinal)),
                layout.GetPageIndexOfFragment("note1n"));
        }
    }

    [Fact]
    public void Loader_RecognizesReciprocalMAnchorFootnotes()
    {
        var path = WriteChapter(
            "<p>正文<a id=\"w1\"></a><a href=\"#m1\"><sup>[1]</sup></a>之后。</p>"
            + "<p class=\"note\"><a id=\"m1\"></a><a href=\"#w1\">[1]</a>脚注定义。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items), item =>
            item.Kind == InlineKind.FootnoteMarker);
        Assert.Equal("[1]", marker.Text);
        Assert.Contains("#m1", marker.FootnoteHref, StringComparison.Ordinal);
        Assert.Contains("脚注定义", content.BodyText);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items), item =>
            item.Text.Contains("脚注定义", StringComparison.Ordinal));

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        var hotZone = Assert.Single(layout.Pages.SelectMany(page => page.HotZones));
        Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
        Assert.Contains("#m1", hotZone.Href, StringComparison.Ordinal);
    }

    [Fact]
    public void FootnoteReferenceIsCompactAtomicSuperscriptInBothWritingModes()
    {
        var path = WriteChapter(
            "<p>正文<sup><a href=\"#note3n\" id=\"note3\">[3]</a></sup>之后。</p>"
            + "<div class=\"fnote\" id=\"note3n\"><p>这是脚注定义。</p></div>");
        var content = new XhtmlChapterLoader().Load(path);
        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items), item =>
            item.Kind == InlineKind.FootnoteMarker);

        Assert.Equal("[3]", marker.Text);
        Assert.True(marker.Style.Superscript);
        Assert.True(marker.TextStart >= 0);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var options = Options(mode);
            var layout = engine.Compose(content, options);
            var markerRuns = layout.Pages
                .SelectMany(page => page.Runs)
                .Where(run => run.TextStart == marker.TextStart && run.TextLength == marker.Text.Length)
                .ToList();

            // The complete marker is one shaped run, so both brackets share
            // the same superscript baseline instead of becoming three cells.
            var markerRun = Assert.Single(markerRuns);
            Assert.True(markerRun.Style.Superscript);
            Assert.InRange(markerRun.FontSize, 0f, options.BaseFontSize * 0.70f + 0.01f);
            Assert.Equal(3, markerRun.TextLength);
            if (mode == TypesetWritingMode.VerticalRl)
            {
                Assert.False(markerRun.Sideways);
                Assert.Equal(3, markerRun.Glyphs.Length);
            }

            var hotZone = Assert.Single(
                layout.Pages.SelectMany(page => page.HotZones),
                zone => zone.Kind == HotZoneKind.FootnoteMarker
                    && zone.Href.Contains("#note3n", StringComparison.Ordinal));
            Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
            Assert.Contains("#note3n", hotZone.Href, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items), item =>
            item.Text.Contains("这是脚注定义", StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_TurnsAltTextFootnoteImagesIntoInlineMarkers()
    {
        var path = WriteChapter(
            "<p>正文前<img class=\"qqreader-footnote\" alt=\"这是图片脚注内容。\" src=\"missing-note.png\"/>正文后。</p>");
        var content = new XhtmlChapterLoader().Load(path);

        Assert.DoesNotContain(content.Blocks, block => block.Kind == BlockKind.Image);
        var marker = Assert.Single(content.Blocks.SelectMany(block => block.Items), item =>
            item.Kind == InlineKind.FootnoteMarker);
        Assert.Equal("这是图片脚注内容。", marker.FootnoteText);

        using var engine = CreateEngine();
        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var hotZone = Assert.Single(layout.Pages.SelectMany(page => page.HotZones));
            Assert.Equal(HotZoneKind.FootnoteMarker, hotZone.Kind);
            Assert.Equal(marker.FootnoteText, hotZone.FootnoteText);
        }
    }

    [Fact]
    public void Vertical_CharRectAndHitTestUseThePaintedCell()
    {
        var path = WriteChapter("<p>竖排文字的命中区域必须和实际字格保持一致。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        var offset = content.BodyText.IndexOf('竖');
        var pageIndex = layout.GetPageIndexOfOffset(offset);
        var rect = layout.GetCharRect(pageIndex, offset);

        Assert.True(rect is { Width: > 0, Height: > 0 });
        Assert.InRange(rect!.Value.Left, 0, layout.Pages[pageIndex].Width);
        Assert.InRange(rect.Value.Top, 0, layout.Pages[pageIndex].Height);
        var hit = layout.HitTest(
            pageIndex,
            new SKPoint(
                (rect.Value.Left + rect.Value.Right) / 2f,
                (rect.Value.Top + rect.Value.Bottom) / 2f));
        Assert.InRange(hit, offset, offset + 1);
    }

    [Fact]
    public void Vertical_CollapsesParagraphWhitespaceBeforeTheFirstCell()
    {
        var path = WriteChapter("<p>\n        甲乙\n        丙丁\n      </p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl);
        var layout = engine.Compose(content, options);

        var offset = content.BodyText.IndexOf('甲');
        var pageIndex = layout.GetPageIndexOfOffset(offset);
        var rect = layout.GetCharRect(pageIndex, offset);

        Assert.True(rect is { Width: > 0, Height: > 0 });
        // Vertical paragraphs are flush with the content top even though the
        // loader's horizontal paragraph-indent preference remains enabled.
        Assert.InRange(
            rect!.Value.Top,
            options.InsetVertical - 2f,
            options.InsetVertical + options.BaseFontSize);
    }

    [Fact]
    public void Vertical_FullColumnsKeepEveryGlyphInsideTheBottomContentEdge()
    {
        // Five cells fit this content height. The full stop is the sixth cell:
        // it must continue in the next column instead of hanging below the
        // content edge as the legacy WebKit rule allowed.
        var path = WriteChapter("<p>甲乙丙丁戊。己庚辛壬癸。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.VerticalRl, width: 220f, height: 134f);
        var layout = engine.Compose(content, options);
        var contentBottom = options.ViewportHeight - options.InsetVertical;

        for (var offset = 0; offset < content.BodyText.Length; offset++)
        {
            var pageIndex = layout.GetPageIndexOfOffset(offset);
            if (layout.GetCharRect(pageIndex, offset) is not { } rect)
            {
                continue;
            }

            Assert.True(
                rect.Bottom <= contentBottom + 0.01f,
                $"character {offset} ends at {rect.Bottom}, content edge is {contentBottom}");
        }
    }

    [Fact]
    public void Loader_FootnoteDefinitionsStayInOffsetStreamButAreNotRendered()
    {
        var path = WriteChapter(
            "<p>正文引用了一个注<sup>1</sup>。</p>"
            + "<aside id=\"footnote-1\"><p>这是脚注的定义内容。</p></aside>");
        var loader = new XhtmlChapterLoader();
        var content = loader.Load(path);

        Assert.Contains("脚注的定义内容", content.BodyText);
        Assert.DoesNotContain(content.Blocks.SelectMany(block => block.Items), item =>
            item.Text.Contains("脚注的定义", StringComparison.Ordinal));
        Assert.Contains("footnote-1", content.FragmentIds);
        Assert.True(content.FragmentTextOffsets.ContainsKey("footnote-1"));

        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        Assert.True(layout.GetPageIndexOfFragment("footnote-1") >= 0);
    }

    [Fact]
    public void Loader_RendersTheTargetedFootnoteWithoutChangingItsTextOffset()
    {
        var path = WriteChapter("<p>正文。</p>"
            + "<aside id='footnote-1'><p>第一条脚注。</p></aside>"
            + "<aside id='footnote-2'><p>第二条脚注。</p></aside>");
        var hidden = new XhtmlChapterLoader().Load(path);
        var revealed = new XhtmlChapterLoader(visibleFootnoteFragment: "footnote-1").Load(path);
        Assert.Equal(hidden.BodyText, revealed.BodyText);
        Assert.Equal(hidden.FragmentTextOffsets["footnote-1"], revealed.FragmentTextOffsets["footnote-1"]);
        Assert.Contains(revealed.Blocks.SelectMany(block => block.Items), item => item.Text.Contains("第一条脚注"));
        Assert.DoesNotContain(revealed.Blocks.SelectMany(block => block.Items), item => item.Text.Contains("第二条脚注"));
    }

    [Fact]
    public void Loader_DoesNotInferListNumbersWhenNotesHaveExplicitLinks()
    {
        var path = WriteChapter("<p>条件：(1) 有很好的群众；(2) 有很好的党；(3) 有相当力量的红军。</p>"
            + "<p>真正脚注<a id='id1' href='#id1a'>〔1〕</a>和<a id='id2' href='#id2a'>〔2〕</a>。</p>"
            + "<h3>注释</h3><p><a id='id1a' href='#id1'>〔1〕</a>第一条说明。</p>"
            + "<p><a id='id2a' href='#id2'>〔2〕</a>第二条说明。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        var markers = content.Blocks.SelectMany(block => block.Items)
            .Where(item => item.Kind == InlineKind.FootnoteMarker).ToArray();
        Assert.Equal(new[] { "〔1〕", "〔2〕" }, markers.Select(item => item.Text));
        Assert.Contains(content.Blocks.SelectMany(block => block.Items),
            item => item.Text.Contains("(1)") && item.FootnoteHref is null && !item.Style.Superscript);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void RubyPronunciationIsLoadedAndPaintedInBothWritingModes(TypesetWritingMode mode)
    {
        var path = WriteChapter("<p><ruby>银<rt>yín</rt></ruby>行</p>");
        var content = new XhtmlChapterLoader().Load(path);

        var ruby = Assert.Single(content.Blocks.SelectMany(block => block.Items), item =>
            item.Kind == InlineKind.Ruby);
        Assert.Equal("银", ruby.Text);
        Assert.Equal("yín", ruby.RubyText);
        Assert.Equal("银yín行", content.BodyText);

        using var engine = CreateEngine();
        var options = Options(mode);
        var layout = engine.Compose(content, options);
        var runs = layout.Pages.SelectMany(page => page.Runs).ToList();
        var rubyRun = Assert.Single(runs, run =>
            run.TextStart < 0
            && run.TextLength == 0
            && run.FontSize >= options.BaseFontSize * 0.55f
            && run.FontSize < options.BaseFontSize
            && run.Glyphs.Length > 0);
        var baseRun = Assert.Single(runs, run => run.TextStart == 0 && run.TextLength == 1);
        Assert.True(
            rubyRun.OriginY < baseRun.OriginY,
            $"Ruby baseline {rubyRun.OriginY} must be above base baseline {baseRun.OriginY}.");
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb, 17f)]
    [InlineData(TypesetWritingMode.HorizontalTb, 28f)]
    [InlineData(TypesetWritingMode.HorizontalTb, 40f)]
    [InlineData(TypesetWritingMode.VerticalRl, 17f)]
    [InlineData(TypesetWritingMode.VerticalRl, 28f)]
    [InlineData(TypesetWritingMode.VerticalRl, 40f)]
    public void RubyKeepsUniformSizeAndFitsItsBaseAcrossWrappingAndJustification(TypesetWritingMode mode, float fontSize)
    {
        var syllables = new[]
        {
            ("书", "shū"), ("名", "míng"), ("规", "guī"), ("模", "mó"),
            ("中", "zhōng"), ("信", "xìn"), ("窗", "chuāng"), ("女", "nu\u0308\u030c"),
        };
        var markup = string.Concat(Enumerable.Range(0, 8).SelectMany(_ => syllables)
            .Select(pair => $"<ruby>{pair.Item1}<rt>{pair.Item2}</rt></ruby>"));
        var content = new XhtmlChapterLoader().Load(WriteChapter(
            "<p style=\"text-indent:0;text-align:justify\">" + markup + "</p>"));
        var options = new TypesetLayoutOptions
        {
            WritingMode = mode, BaseFontSize = fontSize, LineHeight = 1.1f,
            LetterSpacingEm = 0f, ParagraphIndent = false,
            ViewportWidth = 220f, ViewportHeight = 240f,
            InsetHorizontal = 24f, InsetVertical = 24f,
        };
        using var fonts = new TypesetFontLibrary(_fontPath);
        using var engine = new TypesetEngine(fonts);
        var layout = engine.Compose(content, options);
        var annotations = layout.Pages.SelectMany(page => page.Runs).Where(run => run.TextStart < 0).ToArray();
        Assert.Equal(syllables.Length * 8, annotations.Length);
        Assert.True(layout.Pages.Count > 1);
        Assert.All(annotations, run =>
        {
            Assert.Equal(annotations[0].FontSize, run.FontSize, precision: 3);
            Assert.True(run.FontSize >= Math.Min(14f, fontSize * 0.55f));
        });

        foreach (var page in layout.Pages)
        {
            var ink = page.Runs.Select(run => (Run: run, Bounds: MeasureRunInk(fonts, run))).ToArray();
            foreach (var (run, bounds) in ink.Where(item => item.Run.TextStart < 0))
            {
                Assert.True(bounds.Left >= options.InsetHorizontal - 0.1f, $"Ruby left edge: {bounds.Left}");
                Assert.True(bounds.Top >= options.InsetVertical - 0.1f, $"Ruby top edge: {bounds.Top}");
                Assert.True(bounds.Right <= page.Width - options.InsetHorizontal + 0.1f, $"Ruby right edge: {bounds.Right}");
                Assert.True(bounds.Bottom <= page.Height - options.InsetVertical + 0.1f, $"Ruby bottom edge: {bounds.Bottom}");
                foreach (var other in ink.Where(item => !ReferenceEquals(item.Run, run)))
                {
                    Assert.False(bounds.IntersectsWith(other.Bounds),
                        $"Ruby {bounds} overlaps run at text offset {other.Run.TextStart} ({other.Bounds}) in {mode}, font size {fontSize}.");
                }
            }

            for (var index = 1; index < ink.Length; index++)
            {
                if (ink[index].Run.TextStart >= 0) continue;
                var annotation = ink[index].Run;
                var baseRun = ink[index - 1].Run;
                var baseInk = ink[index - 1].Bounds;
                Assert.True(baseRun.TextStart >= 0);
                var annotationCenter = mode == TypesetWritingMode.HorizontalTb
                    ? annotation.OriginX + annotation.FlowAdvance / 2f
                    : annotation.OriginY + annotation.FlowAdvance / 2f;
                var baseCenter = mode == TypesetWritingMode.HorizontalTb ? baseInk.MidX : baseInk.MidY;
                Assert.InRange(Math.Abs(annotationCenter - baseCenter), 0f, fontSize * 0.08f);
                var hit = layout.HitTest(page.Index, new SKPoint(baseInk.MidX, baseInk.MidY));
                Assert.InRange(hit, baseRun.TextStart, baseRun.TextStart + baseRun.TextLength);
                Assert.NotNull(layout.GetCharRect(page.Index, baseRun.TextStart));
            }
        }
    }

    [Fact]
    public void FullWidthPunctuationUsesAHalfWidthCell()
    {
        var path = WriteChapter("<p>甲乙。丙丁（戊己）庚辛</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb);
        var layout = engine.Compose(content, options);

        foreach (var mark in new[] { '。', '（', '）' })
        {
            var offset = content.BodyText.IndexOf(mark);
            Assert.True(offset >= 0);
            var pageIndex = layout.GetPageIndexOfOffset(offset);
            var rect = layout.GetCharRect(pageIndex, offset);
            Assert.True(rect is { Width: > 0 });
            Assert.InRange(
                rect!.Value.Width,
                options.BaseFontSize * 0.45f,
                options.BaseFontSize * 0.55f);
        }
    }

    [Fact]
    public void CenteredSentenceMarksKeepTheirCellBeforeClosingQuotes()
    {
        var content = new XhtmlChapterLoader().Load(WriteChapter("<p>他说“好吗？”接着说“好！”</p>"));
        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb);
        var layout = engine.Compose(content, options);

        foreach (var mark in new[] { '？', '！' })
        {
            var offset = content.BodyText.IndexOf(mark);
            var pageIndex = layout.GetPageIndexOfOffset(offset);
            var rect = layout.GetCharRect(pageIndex, offset);
            var quote = layout.GetCharRect(pageIndex, offset + 1);
            Assert.True(rect.HasValue && quote.HasValue);
            Assert.InRange(rect.Value.Width, options.BaseFontSize * 0.97f, options.BaseFontSize * 1.03f);
            Assert.True(quote.Value.Left >= rect.Value.Right - 0.01f);
        }
    }

    [Fact]
    public void MissingGlyphsUseTheBundledFallbackWithoutBreakingNeighbours()
    {
        var emojiFont = FindAssetFont("NotoEmoji.ttf");
        var path = WriteChapter("<p>前后😀文字</p>");
        var content = new XhtmlChapterLoader().Load(path);

        using var fonts = new TypesetFontLibrary(_fontPath, new[] { emojiFont });
        using var engine = new TypesetEngine(fonts);
        var layout = engine.Compose(content, Options(TypesetWritingMode.HorizontalTb));
        var runs = layout.Pages.SelectMany(page => page.Runs).ToList();

        Assert.Contains(runs, run =>
            run.FontPath == emojiFont && run.Glyphs.Any(glyph => glyph != 0));
        Assert.Contains(runs, run =>
            run.FontPath == _fontPath && run.Glyphs.Any(glyph => glyph != 0));
        Assert.DoesNotContain(runs.SelectMany(run => run.Glyphs), glyph => glyph == 0);
    }

    [Fact]
    public void NaturalLatinProseUsesBalancedWordSpacingAndAComfortableLinePitch()
    {
        var text = string.Join(" ", Enumerable.Repeat(
            "As I sit at my window this summer afternoon, hawks are circling about my clearing.", 12));
        var justified = new XhtmlChapterLoader().Load(WriteChapter($"<p style=\"text-align:justify\">{text}</p>"));
        var ragged = new XhtmlChapterLoader().Load(WriteChapter($"<p style=\"text-align:left\">{text}</p>"));
        TypesetLayoutOptions ProseOptions(bool natural) => new()
        {
            BaseFontSize = 20, LineHeight = 1.75f,
            LetterSpacingEm = natural ? 0.07f : 0,
            NaturalLatinTypography = true, ParagraphIndent = false,
            OverridePublisherLineHeight = true, OverridePublisherParagraphSpacing = true,
            AvoidWidowsAndOrphans = false,
            ViewportWidth = 400, ViewportHeight = 800, InsetHorizontal = 24, InsetVertical = 24,
        };
        using var engine = CreateEngine();
        var actual = engine.Compose(justified, ProseOptions(true));
        var expected = engine.Compose(ragged, ProseOptions(false));
        Assert.Equal(expected.Pages.Select(page => (page.TextStartOffset, page.TextEndOffset)),
            actual.Pages.Select(page => (page.TextStartOffset, page.TextEndOffset)));
        var expectedSpaces = CollectSpaceGaps(expected, ragged.BodyText);
        var actualSpaces = CollectSpaceGaps(actual, justified.BodyText);
        Assert.True(actualSpaces.Count > 20);
        foreach (var (offset, gap) in actualSpaces)
            Assert.InRange(gap, expectedSpaces[offset] - 0.01f, 20f * 0.65f + 0.01f);
        Assert.Contains(actualSpaces, pair => pair.Value > expectedSpaces[pair.Key] + 0.25f);
        var actualLines = actual.Pages.SelectMany(page => page.Runs).ToArray();
        var expectedLines = expected.Pages.SelectMany(page => page.Runs).ToArray();
        var rightEdge = actual.Options.ViewportWidth - actual.Options.InsetHorizontal;
        var actualSlack = actualLines.SkipLast(1).Sum(run => Math.Max(0f, rightEdge - run.OriginX - run.FlowAdvance));
        var raggedSlack = expectedLines.SkipLast(1).Sum(run => Math.Max(0f, rightEdge - run.OriginX - run.FlowAdvance));
        Assert.True(actualSlack < raggedSlack * 0.5f,
            $"Balanced English should reclaim the ragged right edge: {actualSlack} vs {raggedSlack}.");
        Assert.Contains(actualLines.SkipLast(1), run => Math.Abs(rightEdge - run.OriginX - run.FlowAdvance) < 0.05f);
        Assert.InRange(Math.Abs(actualLines[^1].FlowAdvance - expectedLines[^1].FlowAdvance), 0, 0.01f);
        var expectedLetters = CollectWordInteriorGaps(expected, ragged.BodyText);
        foreach (var (offset, gap) in CollectWordInteriorGaps(actual, justified.BodyText))
            Assert.InRange(Math.Abs(gap - expectedLetters[offset]), 0, 0.01f);
        var baselines = BaselineY(actual);
        Assert.InRange(Math.Abs(baselines[1] - baselines[0] - 31f), 0, 0.05f);
        Assert.All(actual.Pages.SelectMany(page => page.Runs), run => Assert.Equal(20f, run.FontSize));
    }

    [Theory]
    [InlineData("man’s", 3)]
    [InlineData("don’t", 3)]
    [InlineData("l’été", 1)]
    public void NaturalLatinApostrophesKeepTheirGlyphWithoutAFullWidthGap(string text, int apostropheOffset)
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p style=\"margin:0;text-indent:0;text-align:left\">{text}</p>"));
        TypesetLayoutOptions ProseOptions(bool natural) => new()
        {
            BaseFontSize = 20, LineHeight = 1.75f, LetterSpacingEm = 0,
            NaturalLatinTypography = natural, ParagraphIndent = false,
            ViewportWidth = 400, ViewportHeight = 600,
        };
        using var engine = CreateEngine();
        var layout = engine.Compose(chapter, ProseOptions(true));
        var natural = Assert.Single(Assert.Single(layout.Pages).Runs);
        var ordinary = Assert.Single(Assert.Single(engine.Compose(chapter, ProseOptions(false)).Pages).Runs);
        Assert.Equal(ordinary.Glyphs, natural.Glyphs);
        Assert.Equal(ordinary.Clusters, natural.Clusters);
        Assert.Equal(text, chapter.BodyText.Substring(natural.TextStart, natural.TextLength));
        var index = Array.IndexOf(natural.Clusters, apostropheOffset);
        Assert.InRange(index, 0, natural.Glyphs.Length - 2);
        Assert.InRange(ordinary.X[index + 1] - ordinary.X[index], 19f, 21f);
        Assert.InRange(natural.X[index + 1] - natural.X[index], 0.1f, 8f);
        var quoteRect = layout.GetCharRect(0, natural.TextStart + apostropheOffset);
        Assert.True(quoteRect.HasValue);
        Assert.InRange(quoteRect.GetValueOrDefault().Width, 0.1f, 8f);
        using var font = new SKFont(engine.Fonts.GetTypeface(natural.FontPath), natural.FontSize);
        _ = font.GetGlyphWidths(natural.Glyphs.AsSpan(), out var bounds, null);
        Assert.True(natural.X[index] + bounds[index].Right <= natural.X[index + 1] + bounds[index + 1].Left,
            "The apostrophe's original ink must not overlap the following letter.");
        for (var before = 0; before <= index; before++)
            Assert.InRange(Math.Abs(natural.X[before] - ordinary.X[before]), 0, 0.01f);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void NaturalLatinPolicyKeepsChineseAndVerticalPageRhythm(TypesetWritingMode mode)
    {
        var text = string.Concat(Enumerable.Repeat("春风吹过山林，正文排版应当保持平静清晰。", 40));
        var chapter = new XhtmlChapterLoader().Load(WriteChapter($"<p>{text}</p><p>{text}</p>"));
        TypesetLayoutOptions ProseOptions(bool natural) => new()
        {
            WritingMode = mode, BaseFontSize = 17, LineHeight = 1.75f,
            NaturalLatinTypography = natural, ParagraphIndent = false,
            ParagraphSpacingMatchesLineHeight = true,
            OverridePublisherLineHeight = true, OverridePublisherParagraphSpacing = true,
            ViewportWidth = 300, ViewportHeight = 600,
        };
        using var engine = CreateEngine();
        var ordinary = engine.Compose(chapter, ProseOptions(false));
        var natural = engine.Compose(chapter, ProseOptions(true));
        Assert.Equal(ordinary.Pages.Select(page => (page.TextStartOffset, page.TextEndOffset)),
            natural.Pages.Select(page => (page.TextStartOffset, page.TextEndOffset)));
        Assert.Equal(ordinary.Pages.SelectMany(page => page.Runs).Select(run => (run.OriginX, run.OriginY, run.FontSize)),
            natural.Pages.SelectMany(page => page.Runs).Select(run => (run.OriginX, run.OriginY, run.FontSize)));
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(false, 0.5f)]
    [InlineData(false, 1f)]
    [InlineData(true, 0f)]
    [InlineData(true, 0.5f)]
    [InlineData(true, 1f)]
    public void NaturalEnglishParagraphsStayDistinctAndRespectLargerSpacing(bool indent, float spacing)
    {
        var chapter = new XhtmlChapterLoader(indent).Load(WriteChapter(
            "<p>First line<br/>Second line</p><p>Next paragraph</p>"));
        using var engine = CreateEngine();
        var layout = engine.Compose(chapter, new TypesetLayoutOptions
        {
            BaseFontSize = 20, LineHeight = 1.75f, NaturalLatinTypography = true,
            ParagraphIndent = indent, ParagraphSpacingEm = spacing, ParagraphSpacingMatchesLineHeight = spacing == 0,
            OverridePublisherLineHeight = true, OverridePublisherParagraphSpacing = true,
            ViewportWidth = 400, ViewportHeight = 800,
        });
        var baselines = BaselineY(layout);
        Assert.Equal(3, baselines.Count);
        Assert.InRange(Math.Abs(baselines[1] - baselines[0] - 31f), 0, 0.05f);
        var gap = indent ? spacing : Math.Max(0.35f, spacing);
        Assert.InRange(Math.Abs(baselines[2] - baselines[1] - 31f - gap * 20), 0, 0.05f);
    }

    [Fact]
    public void AccentedLatinWordsAndCombiningMarksStayInTheirSpellingUnits()
    {
        const string text = "éclair Café naïve résumé Café coöperate state-of-the-art";
        var units = TypesetText.Itemize(text, 0, text.Length).Where(unit => unit.Kind != TextUnitKind.Space).ToArray();
        Assert.All(units, unit => Assert.Equal(TextUnitKind.LatinWord, unit.Kind));
        Assert.Equal(new[] { "éclair", "Café", "naïve", "résumé", "Café", "coöperate", "state-", "of-", "the-", "art" },
            units.Select(unit => text.Substring(unit.Start, unit.Length)));
    }

    [Fact]
    public void NarrowProseColumnsWrapLongWordsAtWholeGraphemesWithoutDroppingOffsets()
    {
        var word = string.Concat(Enumerable.Repeat("Café", 4));
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            $"<p style=\"margin-left:4em;margin-right:4em\">{word} continued text</p>"));
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions
        {
            BaseFontSize = 17, LineHeight = 1.75f, NaturalLatinTypography = true,
            ParagraphIndent = false, ViewportWidth = 300, ViewportHeight = 600,
        };
        var shaper = new GlyphShaper(engine.Fonts);
        shaper.TryShapeWithFallback(word, 17, false, out _, out var shaped);
        var columnLeft = options.InsetHorizontal + 4 * options.BaseFontSize;
        var columnRight = options.ViewportWidth - options.InsetHorizontal - 4 * options.BaseFontSize;
        Assert.InRange(shaped.TotalAdvance, columnRight - columnLeft + 1, options.ContentWidth);
        var layout = engine.Compose(chapter, options);
        var runs = layout.Pages.SelectMany(page => page.Runs).ToArray();
        Assert.True(runs.Select(run => run.OriginY).Distinct().Count() > 1);
        Assert.All(runs, run => Assert.True(MeasureRunInk(engine.Fonts, run).Right <= columnRight + 0.5f));
        var start = chapter.BodyText.IndexOf(word, StringComparison.Ordinal);
        var offsets = runs.SelectMany(run => Enumerable.Range(run.TextStart, run.TextLength)).ToHashSet();
        Assert.All(Enumerable.Range(start, word.Length), offset => Assert.Contains(offset, offsets));
        Assert.DoesNotContain(runs, run => run.TextStart >= 0 && chapter.BodyText[run.TextStart] == '́');
    }

    [Fact]
    public void RightAlignedProseKeepsItsPublisherAlignment()
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<p style=\"text-align:right\">Signed by the author.</p>"));
        Assert.True(Assert.Single(chapter.Blocks).AlignRight);
        using var engine = CreateEngine();
        var options = new TypesetLayoutOptions { NaturalLatinTypography = true, ViewportWidth = 400, ViewportHeight = 600 };
        var layout = engine.Compose(chapter, options);
        var run = Assert.Single(Assert.Single(layout.Pages).Runs);
        Assert.InRange(Math.Abs(run.OriginX + run.FlowAdvance - (options.ViewportWidth - options.InsetHorizontal)), 0, 0.02f);
    }

    [Fact]
    public void JustificationExpandsWordSpacesWithoutStretchingLetters()
    {
        var paragraph = string.Join(
            " ",
            Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 12));
        var justified = new XhtmlChapterLoader().Load(WriteChapter($"<p>{paragraph}</p>"));
        var ragged = new XhtmlChapterLoader().Load(
            WriteChapter($"<p style=\"text-align:left\">{paragraph}</p>"));

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f);
        var justifiedLayout = engine.Compose(justified, options);
        var raggedLayout = engine.Compose(ragged, options);

        var justifiedGaps = CollectWordInteriorGaps(justifiedLayout, justified.BodyText);
        var raggedGaps = CollectWordInteriorGaps(raggedLayout, ragged.BodyText);
        Assert.True(justifiedGaps.Count > 20, $"expected word gaps, got {justifiedGaps.Count}");

        foreach (var (offset, justifiedGap) in justifiedGaps)
        {
            Assert.True(raggedGaps.TryGetValue(offset, out var raggedGap));
            Assert.InRange(Math.Abs(justifiedGap - raggedGap), 0f, 0.01f);
        }

        // The justified layout must still use the available width: at least
        // one inter-word gap has to be wider than in the ragged layout.
        var justifiedSpaces = CollectSpaceGaps(justifiedLayout, justified.BodyText);
        var raggedSpaces = CollectSpaceGaps(raggedLayout, ragged.BodyText);
        foreach (var (offset, gap) in justifiedSpaces)
        {
            if (raggedSpaces.TryGetValue(offset, out var original))
                Assert.InRange(gap - original, -0.01f, options.BaseFontSize * 0.2f + 0.01f);
        }
        Assert.True(
            justifiedSpaces.Any(pair =>
                raggedSpaces.TryGetValue(pair.Key, out var raggedGap)
                && pair.Value > raggedGap + 0.5f),
            "expected justification to widen at least one word space");
    }

    [Fact]
    public void MixedChineseAndEnglishLinesAreJustifiedBetweenCharacters()
    {
        // A Chinese sentence with an embedded English word must stay flush:
        // treating the whole line as "Latin" would leave it ragged because
        // there are no inter-word spaces to expand.
        var paragraph = string.Concat(Enumerable.Repeat(
            "中文排版测试会穿插英文单词 Kindle 与标点，模拟实际书页中的混排段落。", 40));
        var justified = new XhtmlChapterLoader().Load(WriteChapter($"<p>{paragraph}</p>"));
        var ragged = new XhtmlChapterLoader().Load(
            WriteChapter($"<p style=\"text-align:left\">{paragraph}</p>"));

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f);
        var justifiedLayout = engine.Compose(justified, options);
        var raggedLayout = engine.Compose(ragged, options);

        var justifiedGaps = CollectCjkGaps(justifiedLayout, justified.BodyText);
        var raggedGaps = CollectCjkGaps(raggedLayout, ragged.BodyText);
        Assert.True(justifiedGaps.Count > 20, $"expected CJK gaps, got {justifiedGaps.Count}");
        Assert.True(
            justifiedGaps.Any(pair =>
                raggedGaps.TryGetValue(pair.Key, out var raggedGap)
                && pair.Value > raggedGap + 0.2f),
            "expected a mixed line to widen its inter-character gaps");
        var justifiedWords = CollectWordInteriorGaps(justifiedLayout, justified.BodyText);
        var raggedWords = CollectWordInteriorGaps(raggedLayout, ragged.BodyText);
        Assert.True(justifiedWords.Count > 20);
        foreach (var (offset, gap) in justifiedWords)
            Assert.InRange(Math.Abs(gap - raggedWords[offset]), 0, 0.01f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DensePaginationUsesAvailableLineWithoutLosingParagraphText(bool avoidWidows, bool newParagraph)
    {
        var html = newParagraph
            ? "<p>甲<br/>乙<br/>丙</p><p>丁<br/>戊</p>"
            : "<p>甲<br/>乙<br/>丙<br/>丁<br/>戊</p>";
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(html));
        using var engine = CreateEngine();
        var layout = engine.Compose(chapter, new TypesetLayoutOptions
        {
            BaseFontSize = 17f, LineHeight = 1.75f,
            ViewportWidth = 400f, ViewportHeight = 160f, InsetHorizontal = 16f, InsetVertical = 16f,
            OverridePublisherLineHeight = true, OverridePublisherParagraphSpacing = true,
            ParagraphSpacingEm = 0.5f, AvoidWidowsAndOrphans = avoidWidows
        });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(avoidWidows ? 1 : 0, layout.GetPageIndexOfOffset(chapter.BodyText.IndexOf('丁')));
        Assert.Equal(1, layout.GetPageIndexOfOffset(chapter.BodyText.IndexOf('戊')));
        var offsets = layout.Pages.SelectMany(page => page.Runs)
            .Where(run => run.TextStart >= 0 && run.TextLength > 0)
            .SelectMany(run => Enumerable.Range(run.TextStart, run.TextLength)).ToArray();
        Assert.Equal(offsets.Length, offsets.Distinct().Count());
        foreach (var character in "甲乙丙丁戊")
            Assert.Contains(chapter.BodyText.IndexOf(character), offsets);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(1.75f)]
    [InlineData(2.1f)]
    public void ReaderLineHeightOverridesPublisherBodyLineHeight(float height)
    {
        var text = string.Concat(Enumerable.Repeat("这是一段用来测量行距的文字。", 24));
        var chapter = new XhtmlChapterLoader().Load(WriteChapter($"<p style=\"line-height:2.5\">{text}</p>"));
        using var engine = CreateEngine();
        var layout = engine.Compose(chapter, new TypesetLayoutOptions
        {
            BaseFontSize = 17f, LineHeight = height, OverridePublisherLineHeight = true,
            ViewportWidth = 400f, ViewportHeight = 900f
        });
        var baselines = layout.Pages[0].Runs.Select(run => run.OriginY).Distinct().Order().ToArray();
        Assert.True(baselines.Length > 2);
        Assert.InRange(Math.Abs(baselines[1] - baselines[0] - 17f * height), 0, 0.05f);
    }

    [Theory]
    [InlineData(TypesetWritingMode.HorizontalTb)]
    [InlineData(TypesetWritingMode.VerticalRl)]
    public void ReaderParagraphSpacingReplacesPublisherMarginsAndDoesNotChangeLinePitch(TypesetWritingMode mode)
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<p style=\"margin-top:2em;margin-bottom:3em;line-height:2.5\">第一行<br/>第二行</p>" +
            "<p style=\"margin-top:2em;margin-bottom:3em;line-height:2.5\">下一段</p>"));
        using var engine = CreateEngine();
        foreach (var spacing in new[] { 0f, 0.5f, 1f })
        {
            var options = new TypesetLayoutOptions
            {
                WritingMode = mode, BaseFontSize = 17f, LineHeight = 1.75f,
                OverridePublisherLineHeight = true, OverridePublisherParagraphSpacing = true,
                ParagraphSpacingEm = spacing, ParagraphSpacingMatchesLineHeight = spacing == 0,
                ViewportWidth = 800f, ViewportHeight = 900f
            };
            var layout = engine.Compose(chapter, options);
            var page = Assert.Single(layout.Pages);
            var positions = page.Runs.Select(run => mode == TypesetWritingMode.HorizontalTb ? run.OriginY : -run.OriginX)
                .Distinct().Order().ToArray();
            Assert.Equal(3, positions.Length);
            var linePitch = positions[1] - positions[0];
            Assert.InRange(Math.Abs(linePitch - options.BodyLineHeight), 0, 0.05f);
            Assert.InRange(Math.Abs(positions[2] - positions[1] - linePitch - spacing * 17f), 0, 0.05f);
        }
    }

    [Fact]
    public void PublisherLineHeightControlsBaselinePitch()
    {
        var text = string.Concat(Enumerable.Repeat("这是一段用来测量行距的文字。", 24));
        var chapter = new XhtmlChapterLoader().Load(
            WriteChapter($"<p style=\"line-height:2.5\">{text}</p>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 900f));

        var baselines = layout.Pages[0].Runs
            .Select(run => MathF.Round(run.OriginY, 2))
            .Distinct()
            .OrderBy(y => y)
            .ToList();
        Assert.True(baselines.Count >= 3, $"expected several lines, got {baselines.Count}");
        var pitch = baselines[1] - baselines[0];
        Assert.InRange(pitch, 17f * 2.5f - 0.5f, 17f * 2.5f + 0.5f);
    }

    [Fact]
    public void ContainerLineHeightInheritsIntoParagraphs()
    {
        var text = string.Concat(Enumerable.Repeat("容器行距应当传给段落里的每一行。", 20));
        var chapter = new XhtmlChapterLoader().Load(
            WriteChapter($"<div style=\"line-height:2.5\"><p>{text}</p></div>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 900f));
        var baselines = BaselineY(layout);

        Assert.True(baselines.Count >= 3, $"expected several lines, got {baselines.Count}");
        Assert.InRange(baselines[1] - baselines[0], 17f * 2.5f - 0.5f, 17f * 2.5f + 0.5f);
    }

    [Fact]
    public void PublisherFontSizeScalesSpansAndHeadings()
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<p>常<span style=\"font-size:2em\">大字</span>常</p>"
            + "<h2 style=\"font-size:1.5em\">标题</h2>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f));
        var runs = layout.Pages[0].Runs;

        var normal = RunAt(runs, chapter.BodyText.IndexOf("常", StringComparison.Ordinal));
        var big = RunAt(runs, chapter.BodyText.IndexOf("大字", StringComparison.Ordinal));
        var heading = RunAt(runs, chapter.BodyText.IndexOf("标题", StringComparison.Ordinal));

        Assert.InRange(normal.FontSize, 16.9f, 17.1f);
        Assert.InRange(big.FontSize, 33.9f, 34.1f);
        Assert.InRange(heading.FontSize, 25.4f, 25.6f);
    }

    [Fact]
    public void PublisherMarginAndPaddingNarrowTheTextColumn()
    {
        var plain = new XhtmlChapterLoader().Load(WriteChapter("<p>正文起始位置。</p>"));
        var styled = new XhtmlChapterLoader().Load(
            WriteChapter("<p style=\"margin: 0 0 0 2em; padding-left: 1em\">正文起始位置。</p>"));

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f);
        var plainX = engine.Compose(plain, options).Pages[0].Runs[0].OriginX;
        var styledX = engine.Compose(styled, options).Pages[0].Runs[0].OriginX;

        // 2em margin + 1em padding at 17px = 51px.
        Assert.InRange(styledX - plainX, 50.5f, 51.5f);
    }

    [Fact]
    public void PublisherBottomMarginSeparatesParagraphs()
    {
        var plain = new XhtmlChapterLoader().Load(WriteChapter("<p>第一段。</p><p>第二段。</p>"));
        var spaced = new XhtmlChapterLoader().Load(
            WriteChapter("<p style=\"margin-bottom:2em\">第一段。</p><p>第二段。</p>"));

        using var engine = CreateEngine();
        var options = Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 900f);
        var plainBaselines = BaselineY(engine.Compose(plain, options));
        var spacedBaselines = BaselineY(engine.Compose(spaced, options));

        Assert.True(plainBaselines.Count >= 2 && spacedBaselines.Count >= 2);
        var plainGap = plainBaselines[^1] - plainBaselines[0];
        var spacedGap = spacedBaselines[^1] - spacedBaselines[0];
        // 2em at 17px = 34px of extra space between the paragraphs.
        Assert.InRange(spacedGap - plainGap, 33.5f, 34.5f);
    }

    [Fact]
    public void TableCellsLayOutInColumnsAndKeepHeaderWeight()
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<table>"
            + "<tr><th>名称</th><th>数量</th></tr>"
            + "<tr><td>左栏内容</td><td>右栏内容</td></tr>"
            + "</table>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f));
        var runs = layout.Pages[0].Runs;

        var headerRun = RunAt(runs, chapter.BodyText.IndexOf("名称", StringComparison.Ordinal));
        var leftRun = RunAt(runs, chapter.BodyText.IndexOf("左栏内容", StringComparison.Ordinal));
        var rightRun = RunAt(runs, chapter.BodyText.IndexOf("右栏内容", StringComparison.Ordinal));

        Assert.InRange(Math.Abs(leftRun.OriginY - rightRun.OriginY), 0f, 0.5f);
        Assert.True(
            rightRun.OriginX > leftRun.OriginX + 100f,
            $"expected two columns, left={leftRun.OriginX} right={rightRun.OriginX}");
        Assert.True(headerRun.Style.Bold, "expected <th> text to be bold");
    }

    [Fact]
    public void RowSpanCellsKeepTheirGridColumnsAcrossRows()
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<table>"
            + "<tr><td rowspan=\"2\">合并格</td><td>右上</td></tr>"
            + "<tr><td>右下</td></tr>"
            + "</table>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f));
        var runs = layout.Pages[0].Runs;

        var merged = RunAt(runs, chapter.BodyText.IndexOf("合并格", StringComparison.Ordinal));
        var upperRight = RunAt(runs, chapter.BodyText.IndexOf("右上", StringComparison.Ordinal));
        var lowerRight = RunAt(runs, chapter.BodyText.IndexOf("右下", StringComparison.Ordinal));

        Assert.Equal(1, runs.Count(run => run.TextStart == merged.TextStart));
        Assert.True(
            upperRight.OriginX > merged.OriginX + 20f,
            $"expected the data column right of the span, merged={merged.OriginX} upper={upperRight.OriginX}");
        Assert.InRange(Math.Abs(lowerRight.OriginX - upperRight.OriginX), 0f, 0.5f);
        Assert.True(
            lowerRight.OriginY > upperRight.OriginY + 1f,
            "expected the second data row below the first");
        Assert.InRange(
            Math.Abs(merged.OriginY - upperRight.OriginY),
            0f,
            6f);
    }

    [Fact]
    public void LongEnglishWordsWrapInsideNarrowTableCells()
    {
        const string word = "antidisestablishmentarianism";
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<table><tr>"
            + "<td>这是一个占据较多宽度的第一列</td>"
            + $"<td>{word} continued text</td>"
            + "</tr></table>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f));

        var start = chapter.BodyText.IndexOf(word, StringComparison.Ordinal);
        var wordRuns = layout.Pages
            .SelectMany(page => page.Runs)
            .Where(run => run.TextStart >= 0
                && run.TextLength > 0
                && run.TextStart < start + word.Length
                && run.TextStart + run.TextLength > start)
            .ToList();

        Assert.True(
            wordRuns.Count >= 2,
            $"expected the long word to wrap inside its cell, got {wordRuns.Count} run(s)");
    }

    [Fact]
    public void HyphenatedCompoundsOfferBreakOpportunities()
    {
        const string text = "well-known state-of-the-art";
        var units = TypesetText.Itemize(text, 0, text.Length)
            .Select(unit => text.Substring(unit.Start, unit.Length))
            .ToArray();

        Assert.Equal(
            new[] { "well-", "known", " ", "state-", "of-", "the-", "art" },
            units);
    }

    [Fact]
    public void CssUsesLastDeclarationAndIgnoresImportant()
    {
        var css = MicroCss.Parse(
            "p { font-weight: normal; font-weight: bold !important; "
            + "border: 1px solid #000; border-style: none; }");

        var hints = css.Resolve("p", Array.Empty<string>(), null);

        Assert.True(hints.Bold);
        Assert.Equal(0f, hints.BorderTopEm);
        Assert.Equal(0f, hints.BorderBottomEm);
    }

    [Fact]
    public void CssSelectorGroupsApplyToEverySelector()
    {
        var css = MicroCss.Parse("td, th { text-align: center; border: 1px solid #000; }");

        var header = css.Resolve("th", Array.Empty<string>(), null);
        Assert.True(header.Center);
        Assert.True(header.BorderTopEm > 0f);
        Assert.True(header.BorderBottomEm > 0f);

        var cell = css.Resolve("td", Array.Empty<string>(), null);
        Assert.True(cell.Center);
        Assert.True(cell.BorderLeftEm > 0f);
    }

    [Fact]
    public void TableBordersDrawCellOutlines()
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<table><tr>"
            + "<td style=\"border:1px solid #000\">甲</td>"
            + "<td style=\"border:1px solid #000\">乙</td></tr><tr>"
            + "<td style=\"border:1px solid #000\">丙</td>"
            + "<td style=\"border:1px solid #000\">丁</td></tr></table>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f));

        var rules = layout.Pages
            .SelectMany(page => page.Decorations)
            .Where(decoration => decoration.Kind == DecorationKind.Rule)
            .ToList();

        Assert.True(rules.Count >= 8, $"expected cell borders, got {rules.Count} rules");
        Assert.Contains(rules, rule => rule.Rect.Height < 2f && rule.Rect.Width > 40f);
        Assert.Contains(rules, rule => rule.Rect.Width < 2f && rule.Rect.Height > 10f);

        // Adjacent bordered columns must share their inner vertical grid line
        // instead of leaving an empty gap between column boxes.
        var horizontalBounds = rules
            .Where(rule => rule.Rect.Height < 2f)
            .Select(rule => (Left: MathF.Round(rule.Rect.Left, 1), Right: MathF.Round(rule.Rect.Right, 1)))
            .Distinct()
            .OrderBy(span => span.Left)
            .ToArray();
        Assert.Equal(2, horizontalBounds.Length);
        Assert.InRange(Math.Abs(horizontalBounds[0].Right - horizontalBounds[1].Left), 0f, 0.5f);
    }

    [Fact]
    public void BlockFiguresAndTablesExposeReaderTapTargets()
    {
        WritePng("tap.png");
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<p>正文</p><p><img src=\"tap.png\" alt=\"插图\"/></p>"
            + "<table>"
            + "<tr><th>名称</th><th>数量</th></tr>"
            + "<tr><td>左栏内容</td><td>右栏内容</td></tr>"
            + "</table>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.HorizontalTb, width: 420f, height: 600f));

        var imagePage = layout.Pages.First(page => page.Images.Any(image => image.IsBlock));
        var image = Assert.Single(imagePage.Images, item => item.IsBlock);
        var imageCenter = new SKPoint(
            (image.Rect.Left + image.Rect.Right) / 2f,
            (image.Rect.Top + image.Rect.Bottom) / 2f);
        Assert.Same(image, layout.GetBlockImageAt(imagePage.Index, imageCenter));
        Assert.Null(layout.GetBlockImageAt(imagePage.Index, new SKPoint(-1f, -1f)));

        var tablePage = layout.Pages.First(page => page.Tables.Count > 0);
        var table = tablePage.Tables[0];
        var tableCenter = new SKPoint(
            (table.Rect.Left + table.Rect.Right) / 2f,
            (table.Rect.Top + table.Rect.Bottom) / 2f);
        Assert.Same(table, layout.GetTableAt(tablePage.Index, tableCenter));
        Assert.Null(layout.GetTableAt(tablePage.Index, new SKPoint(-1f, -1f)));
    }

    [Fact]
    public void VerticalBlockFigureExposesReaderTapTarget()
    {
        WritePng("tap-vertical.png");
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<p>正文</p><p><img src=\"tap-vertical.png\" alt=\"插图\"/></p>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.VerticalRl, width: 420f, height: 600f));

        var page = layout.Pages.First(item => item.Images.Any(image => image.IsBlock));
        var block = Assert.Single(page.Images, image => image.IsBlock);
        var center = new SKPoint(
            (block.Rect.Left + block.Rect.Right) / 2f,
            (block.Rect.Top + block.Rect.Bottom) / 2f);
        Assert.Same(block, layout.GetBlockImageAt(page.Index, center));
    }

    [Fact]
    public void TablesFallBackToTextFlowInVerticalWriting()
    {
        var chapter = new XhtmlChapterLoader().Load(WriteChapter(
            "<table><tr><td>甲</td><td>乙</td></tr><tr><td>丙</td><td>丁</td></tr></table>"));

        using var engine = CreateEngine();
        var layout = engine.Compose(
            chapter, Options(TypesetWritingMode.VerticalRl, width: 420f, height: 600f));

        Assert.NotEmpty(layout.Pages);
        Assert.Contains(layout.Pages.SelectMany(page => page.Runs), run => run.TextLength > 0);
    }

    private static PlacedRun RunAt(IReadOnlyList<PlacedRun> runs, int offset)
    {
        Assert.True(offset >= 0);
        return runs.First(run =>
            run.TextStart >= 0 && offset >= run.TextStart && offset < run.TextStart + run.TextLength);
    }

    private static List<float> BaselineY(ChapterLayout layout) =>
        layout.Pages[0].Runs
            .Select(run => MathF.Round(run.OriginY, 2))
            .Distinct()
            .OrderBy(y => y)
            .ToList();

    private static Dictionary<int, float> CollectWordInteriorGaps(
        ChapterLayout layout,
        string bodyText)
    {
        var result = new Dictionary<int, float>();
        foreach (var run in layout.Pages.SelectMany(page => page.Runs))
        {
            for (var index = 0; index < run.Glyphs.Length - 1; index++)
            {
                if (index >= run.Clusters.Length || index + 1 >= run.Clusters.Length)
                {
                    continue;
                }

                var first = run.TextStart + run.Clusters[index];
                var second = run.TextStart + run.Clusters[index + 1];
                if (second != first + 1
                    || first < 0
                    || second >= bodyText.Length
                    || !char.IsAsciiLetterOrDigit(bodyText[first])
                    || !char.IsAsciiLetterOrDigit(bodyText[second]))
                {
                    continue;
                }

                result[first] = run.X[index + 1] - run.X[index];
            }
        }

        return result;
    }

    private static Dictionary<int, float> CollectSpaceGaps(ChapterLayout layout, string bodyText)
    {
        var gaps = new Dictionary<int, float>();
        foreach (var run in layout.Pages.SelectMany(page => page.Runs))
        {
            for (var index = 0; index < run.Glyphs.Length - 1; index++)
            {
                if (index >= run.Clusters.Length || index + 1 >= run.Clusters.Length)
                {
                    continue;
                }

                var first = run.TextStart + run.Clusters[index];
                var second = run.TextStart + run.Clusters[index + 1];
                if (second != first + 1
                    || first < 0
                    || first >= bodyText.Length
                    || !char.IsWhiteSpace(bodyText[first]))
                {
                    continue;
                }

                gaps[second] = run.X[index + 1] - run.X[index];
            }
        }

        return gaps;
    }

    private static Dictionary<int, float> CollectCjkGaps(ChapterLayout layout, string bodyText)
    {
        var gaps = new Dictionary<int, float>();
        foreach (var run in layout.Pages.SelectMany(page => page.Runs))
        {
            for (var index = 0; index < run.Glyphs.Length - 1; index++)
            {
                if (index >= run.Clusters.Length || index + 1 >= run.Clusters.Length)
                {
                    continue;
                }

                var first = run.TextStart + run.Clusters[index];
                var second = run.TextStart + run.Clusters[index + 1];
                if (second != first + 1
                    || first < 0
                    || first >= bodyText.Length
                    || !IsCjk(bodyText[first])
                    || !IsCjk(bodyText[second]))
                {
                    continue;
                }

                gaps[first] = run.X[index + 1] - run.X[index];
            }
        }

        return gaps;
    }

    private static bool IsCjk(char c) => c is >= '㐀' and <= '鿿';

    private static string FindAssetFont(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Libzora.App", "Assets", "Fonts", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Font asset not found: {fileName}");
    }

    private static SKRect MeasureRunInk(TypesetFontLibrary fonts, PlacedRun run)
    {
        using var font = new SKFont(fonts.GetTypeface(run.FontPath), run.FontSize);
        _ = font.GetGlyphWidths(run.Glyphs.AsSpan(), out var bounds, null);
        var result = SKRect.Empty;
        for (var index = 0; index < bounds.Length; index++)
        {
            if (bounds[index].IsEmpty) continue;
            var left = (run.X[index] + bounds[index].Left) * run.Scale;
            var right = (run.X[index] + bounds[index].Right) * run.Scale;
            var top = (run.Y[index] + bounds[index].Top) * run.Scale;
            var bottom = (run.Y[index] + bounds[index].Bottom) * run.Scale;
            var transformed = run.Sideways
                ? new SKRect(run.OriginX - bottom, run.OriginY + left, run.OriginX - top, run.OriginY + right)
                : new SKRect(run.OriginX + left, run.OriginY + top, run.OriginX + right, run.OriginY + bottom);
            result = result.IsEmpty ? transformed : SKRect.Union(result, transformed);
        }
        return result;
    }

    [Fact]
    public void Loader_MicroCssResolvesPublisherEmphasis()
    {
        var cssPath = Path.Combine(_tempDir, "style.css");
        File.WriteAllText(cssPath, ".em { font-style: italic; } .ttl { text-align: center; font-weight: bold; }");
        var path = Path.Combine(_tempDir, $"styled-{Guid.NewGuid():N}.xhtml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><link rel="stylesheet" type="text/css" href="style.css"/></head>
              <body><p class="ttl">标题</p><p>一段<span class="em">斜体强调</span>正文。</p></body>
            </html>
            """);

        var content = new XhtmlChapterLoader().Load(path);

        var title = content.Blocks[0];
        Assert.Equal(BlockKind.Heading, title.Kind);
        Assert.True(title.Center);

        var body = content.Blocks[1];
        var emphasized = body.Items.Single(i => i.Text == "斜体强调");
        Assert.True(emphasized.Style.Italic);
    }

    [Fact]
    public void Paint_RendersInkOnTheSurface()
    {
        var path = WriteChapter("<p>渲染冒烟测试。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        var layout = engine.Compose(content, Options(TypesetWritingMode.VerticalRl));

        using var surface = SKSurface.Create(new SKImageInfo(400, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Assert.NotNull(surface);
        using (var fonts = new TypesetFontLibrary(_fontPath))
        {
            var painter = new TypesetPainter(fonts);
            painter.Paint(surface.Canvas, layout.Pages[0]);
            surface.Canvas.Flush();
        }

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        Assert.NotNull(bitmap);

        var darkPixels = 0;
        for (var y = 0; y < 600; y += 2)
        {
            for (var x = 0; x < 400; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128)
                {
                    darkPixels++;
                }
            }
        }

        Assert.True(darkPixels > 10, $"expected glyph ink on the page, found {darkPixels} dark samples");
    }

    [Fact]
    public void Paint_RendersEveryAnnotationUnderlineStyleInBothWritingModes()
    {
        var path = WriteChapter("<p>横排和竖排都要能显示选中文字的划线。</p>");
        var content = new XhtmlChapterLoader().Load(path);
        using var engine = CreateEngine();
        using var fonts = new TypesetFontLibrary(_fontPath);

        foreach (var mode in new[] { TypesetWritingMode.HorizontalTb, TypesetWritingMode.VerticalRl })
        {
            var layout = engine.Compose(content, Options(mode));
            var annotationStart = content.BodyText.IndexOf('横');
            var pageIndex = layout.GetPageIndexOfOffset(annotationStart);
            Assert.True(pageIndex >= 0);
            var page = layout.Pages[pageIndex];
            var bands = layout.GetOverlayRects(pageIndex, annotationStart, 5);
            Assert.NotEmpty(bands);

            foreach (var style in new[] { "solid", "double", "dashed", "dotted", "wavy" })
            {
                using var surface = SKSurface.Create(new SKImageInfo(400, 600, SKColorType.Bgra8888, SKAlphaType.Opaque));
                var painter = new TypesetPainter(fonts);
                painter.Paint(
                    surface.Canvas,
                    page,
                    annotationOverlays: new[]
                    {
                        new TypesetAnnotationOverlay
                        {
                            Bands = bands,
                            Style = style,
                            Color = new SKColor(220, 20, 20),
                        },
                    });
                surface.Canvas.Flush();

                using var snapshot = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(snapshot);
                var coloredPixels = 0;
                for (var y = 0; y < 600; y++)
                {
                    for (var x = 0; x < 400; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.Red > 150 && pixel.Green < 100 && pixel.Blue < 100)
                        {
                            coloredPixels++;
                        }
                    }
                }

                Assert.True(
                    coloredPixels > 0,
                    $"expected {style} annotation ink in {mode}, found no colored pixels");
            }
        }
    }

    private static IReadOnlyList<string> SystemTestFontFamilies()
    {
        // These tests exercise real platform-face registration and lifetime,
        // not the font-picker's exhaustive catalog. On headless macOS 15,
        // probing optional families can block in CoreText/FontRegistry before
        // a TypesetFontLibrary or composition lease has even been created.
        // Both PingFang faces ship with macOS; registration/shaping must succeed
        // rather than silently returning early when they are unavailable.
        return OperatingSystem.IsMacOS()
            ? ["PingFang SC", "PingFang TC"]
            : SystemFontCatalog.CjkFonts.Select(face => face.Family).ToArray();
    }

    [Fact]
    public void SystemFontCanBeRegisteredWithoutAFile()
    {
        var family = SystemTestFontFamilies().FirstOrDefault();
        if (family is null)
        {
            // Headless CI images may not install a CJK system face.
            return;
        }

        using var library = new TypesetFontLibrary(_fontPath);
        Assert.True(library.TryEnsureSystemFont(family, out var key));
        Assert.Equal(TypesetFontLibrary.SystemFontKey(family), key);
        Assert.True(library.TrySetMainFont(key));
        Assert.Equal(key, library.MainFontPath);
        Assert.NotNull(library.GetTypeface(key));
        Assert.True(library.GetUpem(key) > 0);

        // The face bytes must live outside the managed heap: a compacting GC
        // between registration and shaping used to leave HarfBuzz reading a
        // moved array, crashing inside its layout-table lookup.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var shaper = new GlyphShaper(library);
        var shaped = shaper.Shape("阅读测试", 0, 4, key, 16f);
        Assert.False(shaped.ContainsNotdef);
        Assert.True(shaped.TotalAdvance > 0);
    }

    [Fact]
    public void SwitchingSystemFontReleasesThePreviousFace()
    {
        var families = SystemTestFontFamilies().ToArray();
        if (families.Length == 0)
        {
            // Headless CI images may not install a CJK system face.
            return;
        }

        using var library = new TypesetFontLibrary(_fontPath);
        Assert.True(library.TryEnsureSystemFont(families[0], out var first));
        library.ReleaseSystemFontsExcept(first);
        Assert.Contains(first, library.LoadedSystemFontKeys);

        if (families.Length > 1)
        {
            Assert.True(library.TryEnsureSystemFont(families[1], out var second));
            Assert.NotEqual(library.GetTypeface(first).PostScriptName, library.GetTypeface(second).PostScriptName);
            Assert.True(library.TrySetMainFont(second));
            library.ReleaseSystemFontsExcept(second);
            Assert.DoesNotContain(first, library.LoadedSystemFontKeys);
            Assert.Contains(second, library.LoadedSystemFontKeys);
            Assert.True(library.TrySetMainFont(second));

            // Switching back re-opens the released family on demand.
            Assert.True(library.TryEnsureSystemFont(families[0], out var reopened));
            Assert.Equal(first, reopened);
            Assert.True(library.TrySetMainFont(reopened));
        }

        Assert.True(library.TrySetMainFont(_fontPath));
        library.ReleaseSystemFontsExcept(null);
        Assert.Empty(library.LoadedSystemFontKeys);
        // File-backed faces and the bundled main font survive the release.
        Assert.NotNull(library.GetTypeface(_fontPath));
        var shaper = new GlyphShaper(library);
        Assert.False(shaper.Shape("阅读测试", 0, 4, _fontPath, 16f).ContainsNotdef);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingToMissingGlyphFontKeepsLiveLayoutFallback(bool deferred)
    {
        var family = SystemTestFontFamilies().FirstOrDefault();
        if (family is null) return;

        // This file-backed face deliberately has no Chinese body glyphs. The
        // previously selected platform face must supply the new layout's ink.
        var symbols = Path.Combine(Path.GetDirectoryName(_fontPath)!, "NotoSansSymbols2-Regular.ttf");
        using var library = new TypesetFontLibrary(symbols);
        Assert.True(library.TryEnsureSystemFont(family, out var systemKey));
        Assert.True(library.TrySetMainFont(systemKey));
        using var engine = new TypesetEngine(library, ownsFonts: false);
        var content = new XhtmlChapterLoader().Load(WriteChapter("<p>阅读测试</p>"));
        var options = new TypesetLayoutOptions { ViewportWidth = 390, ViewportHeight = 600 };
        Assert.Contains(engine.Compose(content, options).Pages.SelectMany(page => page.Runs),
            run => run.FontPath == systemKey);

        Assert.True(library.TrySetMainFont(symbols));
        ChapterLayout layout;
        using (deferred ? library.BeginComposition() : null)
        {
            layout = engine.Compose(content, options);
            Assert.Contains(layout.Pages.SelectMany(page => page.Runs), run => run.FontPath == systemKey);
            // Another layout can request cleanup before this worker's result
            // is installed. The outer scope protects that unpublished result;
            // its retained set must then govern the older cleanup request.
            if (deferred)
            {
                library.ReleaseSystemFontsExcept(library.MainFontPath);
                Assert.Contains(systemKey, library.LoadedSystemFontKeys);
            }
            library.SetRetainedFontPaths(layout.Pages.SelectMany(page => page.Runs).Select(run => run.FontPath));
            library.ReleaseSystemFontsExcept(library.MainFontPath);
        }

        // Layout installation must not retire a face still used as fallback.
        Assert.NotNull(library.GetTypeface(systemKey));
        using var surface = SKSurface.Create(new SKImageInfo(390, 600));
        new TypesetPainter(library).Paint(surface.Canvas, layout.Pages[0]);

        // A later layout that needs no platform face must reclaim it, rather
        // than keeping every previously selected system font indefinitely.
        library.SetRetainedFontPaths([]);
        library.ReleaseSystemFontsExcept(library.MainFontPath);
        Assert.Empty(library.LoadedSystemFontKeys);
    }

    [Fact]
    public void DeferredSystemFontReleaseWaitsForComposition()
    {
        var family = SystemTestFontFamilies().FirstOrDefault();
        if (family is null)
        {
            return;
        }

        using var library = new TypesetFontLibrary(_fontPath);
        Assert.True(library.TryEnsureSystemFont(family, out var key));
        Assert.True(library.TrySetMainFont(_fontPath));
        using (library.BeginComposition())
        {
            library.ReleaseSystemFontsExcept(null);
            // The face stays readable while a composer still holds it.
            Assert.Contains(key, library.LoadedSystemFontKeys);
            Assert.NotNull(library.GetTypeface(key));
        }
        Assert.Empty(library.LoadedSystemFontKeys);
    }
}
