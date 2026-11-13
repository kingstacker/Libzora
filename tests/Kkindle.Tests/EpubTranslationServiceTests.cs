using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EpubTranslationServiceTests
{
    [Fact]
    public async Task CreatesOriginalTranslatedAndBilingualEpubsWhilePreservingAssets()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "sample.epub");
            CreateEpub(sourcePath);
            var outputDirectory = Path.Combine(root, "translations");
            var progress = new List<BookTranslationProgress>();
            var settings = new BookTranslationSettings
            {
                Provider = BookTranslationProvider.GoogleFree,
                SourceLanguage = "en",
                TargetLanguage = "en",
                OutputMode = BookTranslationOutputMode.Original
                    | BookTranslationOutputMode.Translated
                    | BookTranslationOutputMode.Bilingual
            };

            var paths = new AppPaths(Path.Combine(root, "app"));
            TranslationTestBook.ConfigureModel(paths);
            using var service = new EpubTranslationService(
                paths,
                new TestHelpers.PlaintextSecretProtector());
            var result = await service.TranslateAsync(
                sourcePath,
                outputDirectory,
                settings,
                new TestHelpers.InlineProgress<BookTranslationProgress>(progress.Add));

            Assert.Equal(3, result.OutputPaths.Count);
            Assert.Equal(2, result.SegmentCount);
            Assert.Contains(progress, item => item.Stage == "正在扫描 EPUB");
            Assert.Contains(progress, item => item.Stage == "正在翻译" && item.ProcessedSegments == 2);
            Assert.Contains(
                progress,
                item => item.Segment?.Status == BookTranslationSegmentStatus.Processing
                    && item.Segment.OriginalText.Contains("Hello", StringComparison.Ordinal));
            Assert.Contains(
                progress,
                item => item.Segment?.Status == BookTranslationSegmentStatus.Completed
                    && item.Segment.TranslatedText.Contains("Hello", StringComparison.Ordinal)
                    && item.Segment.ProcessFlow.Contains("写入译文", StringComparison.Ordinal));
            Assert.Equal(
                2,
                progress.Count(item => item.Segment?.Status == BookTranslationSegmentStatus.Completed));
            Assert.Equal(100, progress[^1].Percentage);

            var original = result.OutputPaths.Single(path => path.EndsWith("-原文.epub", StringComparison.Ordinal));
            Assert.Equal(
                await File.ReadAllBytesAsync(sourcePath),
                await File.ReadAllBytesAsync(original));

            var translated = result.OutputPaths.Single(path => path.EndsWith("_单译版.epub", StringComparison.Ordinal));
            var bilingual = result.OutputPaths.Single(path => path.EndsWith("_双语版.epub", StringComparison.Ordinal));
            var translatedMarkup = ReadArchiveEntry(translated, "OEBPS/chapter.xhtml");
            Assert.True(translatedMarkup.Contains("Hello", StringComparison.Ordinal), translatedMarkup);
            Assert.Contains("world", translatedMarkup, StringComparison.Ordinal);
            Assert.Contains("image-bytes", ReadArchiveEntry(translated, "OEBPS/image.txt"));

            var bilingualMarkup = ReadArchiveEntry(bilingual, "OEBPS/chapter.xhtml");
            Assert.Contains("class=\"kkindle-translation\"", bilingualMarkup);
            Assert.Contains("kkindle-translation", bilingualMarkup);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task KeepsEpub3AndEpub2NavigationLabelsBilingual()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "navigation.epub");
            CreateEpubWithNavigation(sourcePath);
            var paths = new AppPaths(Path.Combine(root, "app"));
            TranslationTestBook.ConfigureModel(paths);
            using var service = new EpubTranslationService(
                paths,
                new TestHelpers.PlaintextSecretProtector(),
                httpHandler: new TestHelpers.StubHttpMessageHandler(request =>
                {
                    using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var source = payload.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
                    var translated = source.Replace("Chapter title", "章节标题", StringComparison.Ordinal);
                    return TranslationServiceTests.ModelReply(translated);
                }));

            var result = await service.TranslateAsync(
                sourcePath,
                Path.Combine(root, "translations"),
                new BookTranslationSettings
                {
                    Provider = BookTranslationProvider.GoogleFree,
                    SourceLanguage = "en",
                    TargetLanguage = "zh-CN",
                    OutputMode = BookTranslationOutputMode.Bilingual
                });

            Assert.Single(result.OutputPaths);
            var nav = ReadArchiveEntry(result.OutputPaths[0], "OEBPS/nav.xhtml");
            var ncx = ReadArchiveEntry(result.OutputPaths[0], "OEBPS/toc.ncx");
            Assert.Contains(
                "<a href=\"chapter.xhtml\">Chapter title / 章节标题</a>",
                nav,
                StringComparison.Ordinal);
            Assert.Contains("Chapter title / 章节标题", ncx, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task CanCopyOnlyTheOriginalWithoutCallingAProvider()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "original.epub");
            CreateEpub(sourcePath);
            var outputDirectory = Path.Combine(root, "translations");
            var paths = new AppPaths(Path.Combine(root, "app"));
            TranslationTestBook.ConfigureModel(paths);
            using var service = new EpubTranslationService(
                paths,
                new TestHelpers.PlaintextSecretProtector(),
                httpHandler: new TestHelpers.StubHttpMessageHandler(_ =>
                    throw new InvalidOperationException("provider should not be called")));

            var result = await service.TranslateAsync(
                sourcePath,
                outputDirectory,
                new BookTranslationSettings { OutputMode = BookTranslationOutputMode.Original });

            Assert.Single(result.OutputPaths);
            Assert.EndsWith("-原文.epub", result.OutputPaths[0], StringComparison.Ordinal);
            Assert.Equal(0, result.SegmentCount);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task KeepsResumeCacheWhenTranslationIsCanceled()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "interrupted.epub");
            CreateEpub(sourcePath);
            var outputDirectory = Path.Combine(root, "translations");
            var paths = new AppPaths(Path.Combine(root, "app"));
            TranslationTestBook.ConfigureModel(paths);
            using var service = new EpubTranslationService(
                paths,
                new TestHelpers.PlaintextSecretProtector(),
                httpHandler: new TestHelpers.StubHttpMessageHandler(_ =>
                    throw new InvalidOperationException("request should be canceled first")));
            using var cancellation = new CancellationTokenSource();
            var settings = new BookTranslationSettings
            {
                Provider = BookTranslationProvider.GoogleFree,
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
                OutputMode = BookTranslationOutputMode.Translated
            };
            var progress = new TestHelpers.InlineProgress<BookTranslationProgress>(value =>
            {
                if (value.Segment?.Status == BookTranslationSegmentStatus.Processing)
                    cancellation.Cancel();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(
                sourcePath,
                outputDirectory,
                settings,
                progress,
                cancellation.Token));

            var resume = await service.FindResumeAsync(
                sourcePath,
                outputDirectory,
                settings);
            Assert.NotNull(resume);
            Assert.Equal(2, resume.TotalSegments);
            Assert.Equal(0, resume.CompletedSegments);
            Assert.Equal(2, resume.IncompleteSegments);
            Assert.Equal(0, resume.FailedSegments);
            Assert.True(File.Exists(Path.Combine(outputDirectory, ".kkindle-translation-cache.jsonl")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task ResumesFailedSegmentsWithConfiguredModelAndRemovesCacheAfterSuccess()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "retry.epub");
            CreateEpub(sourcePath);
            var outputDirectory = Path.Combine(root, "translations");
            var paths = new AppPaths(Path.Combine(root, "app"));
            TranslationTestBook.ConfigureModel(paths);
            var firstSettings = new BookTranslationSettings
            {
                Provider = BookTranslationProvider.BingFree,
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
                OutputMode = BookTranslationOutputMode.Translated
            };
            using (var firstService = new EpubTranslationService(
                       paths,
                       new TestHelpers.PlaintextSecretProtector(),
                       httpHandler: new TestHelpers.StubHttpMessageHandler(_ =>
                           throw new InvalidOperationException("first provider failed"))))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => firstService.TranslateAsync(
                    sourcePath,
                    outputDirectory,
                    firstSettings));
            }

            var secondSettings = firstSettings with { Provider = BookTranslationProvider.GoogleFree };
            using var inspectionService = new EpubTranslationService(
                paths,
                new TestHelpers.PlaintextSecretProtector());
            var resumeInfo = await inspectionService.FindResumeAsync(
                sourcePath,
                outputDirectory,
                secondSettings);
            Assert.NotNull(resumeInfo);
            Assert.Equal(2, resumeInfo.FailedSegments);

            var requestCount = 0;
            using var secondService = new EpubTranslationService(
                paths,
                new TestHelpers.PlaintextSecretProtector(),
                httpHandler: new TestHelpers.StubHttpMessageHandler(request =>
                {
                    requestCount++;
                    using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var source = payload.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
                    var translated = source
                        .Replace("Chapter title", "章节标题", StringComparison.Ordinal)
                        .Replace("Hello", "你好", StringComparison.Ordinal)
                        .Replace("world", "世界", StringComparison.Ordinal);
                    return TranslationServiceTests.ModelReply(translated);
                }));

            var result = await secondService.TranslateAsync(
                sourcePath,
                outputDirectory,
                secondSettings,
                resumeMode: BookTranslationResumeMode.Resume);

            Assert.Single(result.OutputPaths);
            Assert.True(requestCount > 0);
            Assert.False(File.Exists(Path.Combine(outputDirectory, ".kkindle-translation-cache.jsonl")));
            Assert.Contains("你好", ReadArchiveEntry(result.OutputPaths[0], "OEBPS/chapter.xhtml"));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData(BookTranslationProvider.GoogleFree)]
    [InlineData(BookTranslationProvider.BingFree)]
    public async Task LegacySettingsUseOnlyTheConfiguredModelEndpoint(BookTranslationProvider legacyProvider)
    {
        using var book = new TranslationTestBook("<p>Hello world.</p><p>Chapter title</p>");
        var handler = new TranslationTestBook.ModelHandler(text => text.Replace("Hello world.", "你好，世界。").Replace("Chapter title", "章节标题"));
        using var service = book.Service(handler);
        var result = await service.TranslateAsync(book.Source, book.Output, TranslationTestBook.Settings with { Provider = legacyProvider });
        Assert.NotEmpty(handler.Inputs);
        Assert.Contains("你好，世界。", TranslationTestBook.Body(Assert.Single(result.OutputPaths)).Value);
    }

    [Fact]
    public async Task MissingModelSettingsFailWithoutWebRequests()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "missing.epub");
            CreateEpub(sourcePath);
            var requests = 0;
            using var service = new EpubTranslationService(new AppPaths(Path.Combine(root, "app")),
                new TestHelpers.PlaintextSecretProtector(), httpHandler: new TestHelpers.StubHttpMessageHandler(_ =>
                { requests++; throw new InvalidOperationException("No web request allowed"); }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(sourcePath,
                Path.Combine(root, "out"), new BookTranslationSettings { Provider = BookTranslationProvider.GoogleFree }));
            Assert.Equal(0, requests);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static void CreateEpub(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" /></manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>Sample</title></head>
              <body>
                <h1>Chapter title</h1>
                <p>Hello <em>world</em>!</p>
              </body>
            </html>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/image.txt", "image-bytes");
    }

    private static void CreateEpubWithNavigation(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
            </container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
                <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine toc="ncx"><itemref idref="chapter" /></spine>
            </package>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/nav.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body><nav epub:type="toc"><ol>
                <li><a href="chapter.xhtml">Chapter title</a></li>
              </ol></nav></body>
            </html>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/toc.ncx", """
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/">
              <navMap><navPoint id="chapter"><navLabel><text>Chapter title</text></navLabel>
                <content src="chapter.xhtml" /></navPoint></navMap>
            </ncx>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <body><h1>Chapter title</h1></body>
            </html>
            """);
    }

    private static string ReadArchiveEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(
            archive.GetEntry(entryName)!.Open(),
            new UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}
