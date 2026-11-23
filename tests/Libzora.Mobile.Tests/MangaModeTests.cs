using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using SkiaSharp;
using Xunit;

namespace Libzora.Mobile.Tests;

public sealed class MangaModeTests
{
    [Fact]
    public void EpubMangaDetectorIdentifiesComicAndDistinguishesProse()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-manga-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var comicDir = Path.Combine(root, "comic");
            Directory.CreateDirectory(comicDir);
            var cover = Path.Combine(comicDir, "page1.xhtml");
            var ch1 = Path.Combine(comicDir, "page2.xhtml");
            var ch2 = Path.Combine(comicDir, "page3.xhtml");
            File.WriteAllText(Path.Combine(comicDir, "content.opf"),
                "<package><metadata><meta property=\"rendition:layout\">pre-paginated</meta></metadata><spine page-progression-direction=\"rtl\"/></package>");
            File.WriteAllText(cover, "<html><body><p><img src='p1.png'/></p></body></html>");
            File.WriteAllText(ch1, "<html><body><p><img src='p2.png'/></p></body></html>");
            File.WriteAllText(ch2, "<html><body><p><img src='p3.png'/></p></body></html>");

            var comicDoc = new EpubReaderDocument(comicDir, [cover, ch1, ch2], [], []);
            var comicResult = EpubMangaDetector.Detect(comicDoc);
            Assert.True(comicResult.IsManga);
            Assert.True(comicResult.IsRightToLeft);

            var proseDir = Path.Combine(root, "prose");
            Directory.CreateDirectory(proseDir);
            var proseCh = Path.Combine(proseDir, "ch1.xhtml");
            File.WriteAllText(proseCh, $"<html><body><p>{new string('字', 250)}</p><p>{new string('文', 250)}</p></body></html>");
            var proseDoc = new EpubReaderDocument(proseDir, [proseCh], [], []);
            var proseResult = EpubMangaDetector.Detect(proseDoc);
            Assert.False(proseResult.IsManga);

            // Large single-chapter prose (> 32 KB) with embedded figures must
            // fast-scan as non-manga, while large image-only chapters remain manga.
            var largeProseDir = Path.Combine(root, "large-prose");
            Directory.CreateDirectory(largeProseDir);
            var largeProseCh = Path.Combine(largeProseDir, "ch1.xhtml");
            File.WriteAllText(largeProseCh,
                "<html><body>" + string.Concat(Enumerable.Repeat("<p>策略思维正文测试段落内容用于触发快速流式采样检测路径。</p>", 700))
                + "<p><img src='fig1.png'/><img src='fig2.png'/></p></body></html>");
            Assert.False(EpubMangaDetector.Detect(new EpubReaderDocument(largeProseDir, [largeProseCh], [], [])).IsManga);

            var largeComicDir = Path.Combine(root, "large-comic");
            Directory.CreateDirectory(largeComicDir);
            var largeComicCh = Path.Combine(largeComicDir, "ch1.xhtml");
            File.WriteAllText(largeComicCh,
                "<html><body>" + string.Concat(Enumerable.Repeat("<div class='panel'><img src='page.png' alt='illustration panel' /></div>", 650))
                + "</body></html>");
            Assert.True(EpubMangaDetector.Detect(new EpubReaderDocument(largeComicDir, [largeComicCh], [], [])).IsManga);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImageCropDetectorTrimsWhiteScanBorders()
    {
        const int width = 120;
        const int height = 160;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);

        for (var y = 30; y < 130; y++)
        {
            for (var x = 24; x < 96; x++)
            {
                var idx = (y * width + x) * 4;
                pixels[idx] = 20;
                pixels[idx + 1] = 20;
                pixels[idx + 2] = 20;
                pixels[idx + 3] = 255;
            }
        }

        var crop = ImageCropDetector.DetectNormalizedCropBox(pixels, width, height, padding: 0.008);
        Assert.NotNull(crop);
        Assert.InRange(crop!.Value.Left, 0.15f, 0.22f);
        Assert.InRange(crop.Value.Top, 0.15f, 0.22f);
        Assert.InRange(crop.Value.Right, 0.78f, 0.85f);
        Assert.InRange(crop.Value.Bottom, 0.78f, 0.85f);
    }

    [Fact]
    public void MobilePreferencesStoresPerBookMangaSettingsWithoutLeakingAcrossBooks()
    {
        var mangaId = Guid.NewGuid();
        var novelId = Guid.NewGuid();
        var prefs = new MobilePreferences().WithMangaSettings(mangaId, new MobileMangaBookSettings
        {
            Enabled = true,
            Direction = (int)MobileMangaDirection.RightToLeft,
            AutoCrop = true,
            BlackBackground = true
        });

        Assert.True(prefs.MangaMode);
        Assert.True(prefs.MangaAutoCrop);
        Assert.True(prefs.TryGetMangaSettings(mangaId, out var savedManga));
        Assert.True(savedManga.Enabled);
        Assert.True(savedManga.AutoCrop);
        Assert.False(prefs.TryGetMangaSettings(novelId, out _));
    }

    [Fact]
    public void PagingPolicyMirrorsRtlTapSwipeAndTransitionDirection()
    {
        Assert.Equal(1, MobileReaderPagingPolicy.TapDirection(0.1, verticalWriting: false, isPdf: false, isRightToLeft: true));
        Assert.Equal(-1, MobileReaderPagingPolicy.TapDirection(0.9, verticalWriting: false, isPdf: false, isRightToLeft: true));
        Assert.Equal(0, MobileReaderPagingPolicy.TapDirection(0.5, verticalWriting: false, isPdf: false, isRightToLeft: true));
        Assert.Equal(1, MobileReaderPagingPolicy.SwipeDirection(80, verticalWriting: false, isPdf: false, isRightToLeft: true));
        Assert.Equal(-1, MobileReaderPagingPolicy.SwipeDirection(-80, verticalWriting: false, isPdf: false, isRightToLeft: true));
    }

    [Fact]
    public void MangaComposerLaysOutEdgeToEdgePagesAndSeamlessWebtoonSlices()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-manga-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var imgPath = Path.Combine(root, "panel.png");
            using (var bmp = new SKBitmap(400, 1200))
            {
                bmp.Erase(SKColors.DarkSlateGray);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(imgPath, data.ToArray());
            }
            var html = Path.Combine(root, "p.xhtml");
            File.WriteAllText(html, "<html><body><p>   </p><p><img src='panel.png'/></p></body></html>");

            var content = new XhtmlChapterLoader().Load(html);
            using var engine = new TypesetEngine(new TypesetFontLibrary(FindBundledFont()));

            var paged = engine.Compose(content, new TypesetLayoutOptions
            {
                MangaMode = true,
                SeamlessImageScroll = false,
                ViewportWidth = 390f,
                ViewportHeight = 800f,
                InsetHorizontal = 0f,
                InsetVertical = 0f,
                InsetBottom = 0f,
            });
            Assert.Single(paged.Pages);
            var pageImage = Assert.Single(paged.Pages[0].Images);
            Assert.Equal(0f, pageImage.Rect.Top, 1);
            Assert.Equal(800f, pageImage.Rect.Bottom, 1);

            // Width-fit Webtoon: 400x1200 scaled to 390px width = 1170px height -> splits seamlessly across 2 800px slices.
            var webtoon = engine.Compose(content, new TypesetLayoutOptions
            {
                MangaMode = true,
                SeamlessImageScroll = true,
                ViewportWidth = 390f,
                ViewportHeight = 800f,
                InsetHorizontal = 0f,
                InsetVertical = 0f,
                InsetBottom = 0f,
            });
            Assert.Equal(2, webtoon.Pages.Count);
            var slice1 = Assert.Single(webtoon.Pages[0].Images);
            var slice2 = Assert.Single(webtoon.Pages[1].Images);
            Assert.Equal(0f, slice1.Rect.Left, 1);
            Assert.Equal(390f, slice1.Rect.Right, 1);
            Assert.Equal(0f, slice1.Rect.Top, 1);
            Assert.Equal(800f, slice1.Rect.Bottom, 1);
            Assert.Equal(0f, slice2.Rect.Top, 1);
            Assert.Equal(370f, slice2.Rect.Bottom, 1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FindBundledFont()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "Libzora.App", "Assets", "Fonts", "KingHwaOldSong-v3.0.ttf");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Bundled font not found.");
    }
}
