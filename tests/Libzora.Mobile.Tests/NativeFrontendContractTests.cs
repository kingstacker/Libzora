using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.iOSHead;
using Libzora.Mobile.Views;
using Xunit;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.Mobile.Tests;

public sealed class NativeFrontendContractTests
{
    [Fact]
    public void ReaderEventUsesSwiftFieldNamesAndNumericEnums()
    {
        var state = new NativeReaderState(true, true, false, "测试书", "第一章", "3 / 9", 32.5,
            false, null, TtsPlaybackState.Paused, "已暂停", "所选文字")
        {
            SelectionBounds = new(16, 120, 180, 390, 760)
        };
        using var document = JsonDocument.Parse(Serialize(Data(("type", "reader"), ("reader", state))));
        var reader = document.RootElement.GetProperty("reader");
        Assert.Equal("reader", document.RootElement.GetProperty("type").GetString());
        Assert.True(reader.GetProperty("ready").GetBoolean());
        Assert.Equal(3, reader.GetProperty("speechState").GetInt32());
        Assert.Equal(32.5, reader.GetProperty("progress").GetDouble());
        Assert.Equal("所选文字", reader.GetProperty("selectedText").GetString());
        var bounds = reader.GetProperty("selectionBounds");
        Assert.Equal(16, bounds.GetProperty("x").GetDouble());
        Assert.Equal(120, bounds.GetProperty("y").GetDouble());
        Assert.Equal(180, bounds.GetProperty("bottom").GetDouble());
        Assert.Equal(390, bounds.GetProperty("viewportWidth").GetDouble());
        Assert.Equal(760, bounds.GetProperty("viewportHeight").GetDouble());
        Assert.Equal(JsonValueKind.Null, reader.GetProperty("error").ValueKind);

        using var preferences = JsonDocument.Parse(Serialize(new MobilePreferences
            { ReaderTheme = ReaderTheme.Ink, GalleryMode = true, ColorfulMode = true, ShowBookSyncStatus = true, ShowBookRating = true }));
        Assert.Equal(5, preferences.RootElement.GetProperty("readerTheme").GetInt32());
        Assert.Equal(1.35, preferences.RootElement.GetProperty("readerFontScale").GetDouble());
        Assert.Equal("京华老宋体", preferences.RootElement.GetProperty("readerFontFamily").GetString());
        Assert.Equal(0, preferences.RootElement.GetProperty("readerParagraphSpacing").GetDouble());
        Assert.Equal(0, preferences.RootElement.GetProperty("readerProgressDisplay").GetInt32());
        Assert.Equal(1, preferences.RootElement.GetProperty("readerFlowMode").GetInt32());
        Assert.True(preferences.RootElement.GetProperty("readerTwoPageInLandscape").GetBoolean());
        Assert.True(preferences.RootElement.GetProperty("galleryMode").GetBoolean());
        Assert.True(preferences.RootElement.GetProperty("colorfulMode").GetBoolean());
        Assert.True(preferences.RootElement.GetProperty("showBookSyncStatus").GetBoolean());
        Assert.True(preferences.RootElement.GetProperty("showBookRating").GetBoolean());
    }

    [Fact]
    public void ReaderMediaEventUsesSwiftFieldNames()
    {
        var media = new NativeReaderMedia(Guid.NewGuid(), "table", "测试书", "/tmp/table.png");
        using var document = JsonDocument.Parse(
            Serialize(Data(("type", "readerMedia"), ("media", media))));
        Assert.Equal("readerMedia", document.RootElement.GetProperty("type").GetString());
        var encoded = document.RootElement.GetProperty("media");
        Assert.Equal(media.Id.ToString(), encoded.GetProperty("id").GetString());
        Assert.Equal("table", encoded.GetProperty("kind").GetString());
        Assert.Equal("测试书", encoded.GetProperty("title").GetString());
        Assert.Equal("/tmp/table.png", encoded.GetProperty("path").GetString());
    }

    [Fact]
    public void MaterialLocationSurvivesTheSwiftJsonRoundTrip()
    {
        var location = new ReaderProgressRow(Guid.NewGuid(), Guid.NewGuid(), "OEBPS/chapter.xhtml", "note-3",
            2, 450, 32.5, 1, DateTimeOffset.UtcNow)
        {
            ContentPosition = new ReaderContentPosition { TextOffset = 719, ImageIndex = 2, PageIndex = 4, SliceFraction = 0.35 }
        };
        var material = new NativeMaterial(Guid.NewGuid().ToString(), location.BookId, "测试书", "bookmark",
            "正文", "", "第一章", location.UpdatedAt, location);
        using var document = JsonDocument.Parse(Serialize(new[] { material }));
        var encoded = document.RootElement[0].GetProperty("location");
        Assert.Equal(location.BookId.ToString(), encoded.GetProperty("bookId").GetString());
        var restored = encoded.Deserialize(IOSNativeJsonContext.Default.ReaderProgressRow);
        Assert.Equal(location, restored);
        Assert.Equal(719, restored!.ContentPosition!.TextOffset);
        // The list shows the short location; only exports use the full one.
        Assert.Equal("第一章", material.ExportLocation);
        var highlight = material with { LocationText = "2026/11/08", ExportLocation = location.ChapterPath };
        using var highlightDocument = JsonDocument.Parse(Serialize(new[] { highlight }));
        Assert.Equal("2026/11/08", highlightDocument.RootElement[0].GetProperty("locationText").GetString());
        Assert.False(highlightDocument.RootElement[0].TryGetProperty("exportLocation", out _));
    }

    [Fact]
    public void NativeSeriesAndImportChoicesSerializeWithOnlyGeneratedMetadata()
    {
        var book = Data(("id", Guid.NewGuid()), ("series", "Library Series"),
            ("seriesIndex", IOSNativeBookLibraryPolicy.FiniteSeriesIndex(double.PositiveInfinity)),
            ("fileTitles", new[] { "Library Series Volume 1", "Library Series Volume 1 PDF" }));
        var prompt = Data(("id", 1L), ("title", "Same title"), ("message", "Choose an import action"),
            ("confirm", "Keep Separate"), ("choices", new[]
            {
                Data(("id", "keepSeparate"), ("title", "Keep Separate"), ("cancel", false)),
                Data(("id", "skip"), ("title", "Skip"), ("cancel", true))
            }));
        using var document = JsonDocument.Parse(Serialize(Data(("books", new[] { book }), ("prompt", prompt))));
        var encoded = document.RootElement.GetProperty("books")[0];
        Assert.Equal(JsonValueKind.Null, encoded.GetProperty("seriesIndex").ValueKind);
        Assert.Equal("Library Series Volume 1", encoded.GetProperty("fileTitles")[0].GetString());
        var choices = document.RootElement.GetProperty("prompt").GetProperty("choices");
        Assert.Equal("keepSeparate", choices[0].GetProperty("id").GetString());
        Assert.True(choices[1].GetProperty("cancel").GetBoolean());
    }

    [Fact]
    public void SwiftSettingsFormsKeepMissingServiceDefaults()
    {
        const string sync = """{"provider":1,"enabled":false,"webDavEndpoint":"https://example.invalid/libzora","webDavUsername":"test","webDavPassword":"","prefix":"test"}""";
        using var syncDocument = JsonDocument.Parse(sync);
        var settings = Read(syncDocument.RootElement, new S3SyncSettings(), IOSNativeJsonContext.Default.S3SyncSettings);
        Assert.Equal(SyncProvider.WebDav, settings.Provider);
        Assert.False(settings.Enabled);
        Assert.Equal(new S3SyncSettings().TimeoutSeconds, settings.TimeoutSeconds);
        Assert.Equal(new S3SyncSettings().ConcurrentRequests, settings.ConcurrentRequests);

        const string preferences = """{"readerTheme":5,"readerFontScale":1.45,"readerParagraphIndent":true,"readerVerticalWriting":true}""";
        using var preferencesDocument = JsonDocument.Parse(preferences);
        var choice = Read(preferencesDocument.RootElement, new MobilePreferences(), IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(ReaderTheme.Ink, choice.ReaderTheme);
        Assert.True(choice.ReaderParagraphIndent);
        Assert.True(choice.ReaderHyphenation);
        Assert.True(choice.ReaderVerticalWriting);
        Assert.Equal(1.45, choice.ReaderFontScale);
        Assert.Equal(ReaderFontDefaults.DefaultFamily, choice.ReaderFontFamily);
        Assert.Equal(24, choice.ReaderBodyPadding);
        Assert.Equal(0, choice.ReaderParagraphSpacing);
        Assert.Equal(MobileReaderProgressDisplay.Pages, choice.ReaderProgressDisplay);
        Assert.False(choice.GalleryMode);
        Assert.False(choice.ColorfulMode);
        Assert.False(choice.ShowBookSyncStatus);
        Assert.False(choice.ShowBookRating);

        using var spacingDocument = JsonDocument.Parse("""{"readerParagraphSpacing":0.5,"galleryMode":true}""");
        var spaced = Read(spacingDocument.RootElement, choice, IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(0.5, spaced.ReaderParagraphSpacing);
        Assert.True(spaced.GalleryMode);
        Assert.Equal(choice.ReaderLineHeight, spaced.ReaderLineHeight);

        using var progressDocument = JsonDocument.Parse("""{"readerProgressDisplay":3}""");
        var timed = Read(progressDocument.RootElement, spaced, IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(MobileReaderProgressDisplay.BookTimeRemaining, timed.ReaderProgressDisplay);
        Assert.Equal(spaced.ReaderParagraphSpacing, timed.ReaderParagraphSpacing);
        Assert.True(timed.GalleryMode);

        using var offDocument = JsonDocument.Parse("""{"readerProgressDisplay":4}""");
        var hidden = Read(offDocument.RootElement, timed, IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(MobileReaderProgressDisplay.Off, hidden.ReaderProgressDisplay);
        Assert.Equal(hidden.ReaderProgressDisplay, MobilePreferences.NormalizeProgressDisplay(hidden.ReaderProgressDisplay));
        Assert.Equal(timed.ReaderParagraphSpacing, hidden.ReaderParagraphSpacing);
        Assert.True(hidden.GalleryMode);
        using var encoded = JsonDocument.Parse(Serialize(hidden));
        Assert.Equal(4, encoded.RootElement.GetProperty("readerProgressDisplay").GetInt32());
    }

    [Theory]
    [InlineData(MobileReaderProgressDisplay.Pages)]
    [InlineData(MobileReaderProgressDisplay.Chapters)]
    [InlineData(MobileReaderProgressDisplay.BookTimeRemaining)]
    [InlineData(MobileReaderProgressDisplay.Off)]
    public void ProgressDisplaySurvivesRestartWithoutChangingOtherPreferences(MobileReaderProgressDisplay display)
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-progress-preferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var store = new MobilePreferencesStore(paths);
            store.Update(value => value with { ReaderProgressDisplay = display, ReaderFontScale = 1.45, ReaderVerticalWriting = true });
            var restored = new MobilePreferencesStore(paths);
            restored.Load();
            Assert.Equal(display, restored.Current.ReaderProgressDisplay);
            Assert.Equal(1.45, restored.Current.ReaderFontScale);
            Assert.True(restored.Current.ReaderVerticalWriting);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(36)]
    public void FooterOffReclaimsTheBottomMarginWithoutChangingOtherLayoutSettings(double padding)
    {
        var preferences = new MobilePreferences { ReaderBodyPadding = padding, ReaderFontScale = 1.45 };
        var build = typeof(ReaderPage).GetMethod("BuildLayout",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        ReaderLayoutSettings Layout(MobilePreferences value) =>
            (ReaderLayoutSettings)build.Invoke(null, [value])!;
        var visible = Layout(preferences);
        var hidden = Layout(preferences with { ReaderProgressDisplay = MobileReaderProgressDisplay.Off });
        Assert.True(visible.NaturalLatinTypography);
        Assert.True(hidden.NaturalLatinTypography);
        Assert.Null(visible.BodyBottomPadding);
        Assert.Equal(8d, hidden.BodyBottomPadding);
        Assert.Equal(padding, hidden.BodyPadding);
        Assert.Equal(visible with { BodyBottomPadding = 8 }, hidden);
        Assert.Equal(visible, Layout(preferences));
    }

    [Fact]
    public void RetiredChapterTimeDisplayUpgradesToWholeBookWithoutResettingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-progress-preferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            File.WriteAllText(Path.Combine(root, "mobile-preferences.json"),
                """{"ReaderProgressDisplay":2,"ReaderFontScale":1.45,"ReaderVerticalWriting":true}""");
            var store = new MobilePreferencesStore(paths);
            store.Load();
            Assert.Equal(MobileReaderProgressDisplay.BookTimeRemaining, store.Current.ReaderProgressDisplay);
            Assert.Equal(1.45, store.Current.ReaderFontScale);
            Assert.True(store.Current.ReaderVerticalWriting);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnknownProgressDisplayFallsBackToPagesWithoutResettingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-progress-preferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            File.WriteAllText(Path.Combine(root, "mobile-preferences.json"),
                """{"ReaderProgressDisplay":999,"ReaderFontScale":1.45,"ReaderVerticalWriting":true}""");
            var store = new MobilePreferencesStore(paths);
            store.Load();
            Assert.Equal(MobileReaderProgressDisplay.Pages, store.Current.ReaderProgressDisplay);
            Assert.Equal(1.45, store.Current.ReaderFontScale);
            Assert.True(store.Current.ReaderVerticalWriting);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FeatureRepliesSerializeWithOnlyGeneratedMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var bookId = Guid.NewGuid();
        var dashboard = new ReadingDashboard(1, 0, 120, 25, 0, 0,
            [new ReadingDashboardBook(bookId, Guid.NewGuid(), 25, 120, now) { Title = "测试书", IsInLibrary = true }],
            [new ReadingDashboardDay(new DateOnly(2026, 11, 7), 120)]);
        object[] replies =
        [
            Data(("books", new object[] { Data(("id", bookId), ("progress", 25.0), ("collectionIds", new Guid[] { Guid.NewGuid() }), ("updatedAt", now)) }),
                ("collections", new[] { Data(("id", Guid.NewGuid()), ("count", 1), ("automatic", false)) }), ("preferences", new MobilePreferences())),
            Data(("dashboard", dashboard), ("medals", ReadingMedalCatalog.Build(dashboard).ToArray()), ("streak", 1)),
            new AccountStatus("http://127.0.0.1:5088", new AccountUser(bookId, "test_reader"), now.AddDays(30)),
            Data(("period", 2), ("today", "2026-11-08"), ("buckets", new ReadingPeriodBucket[]
                { new(new(2026, 11, 1), new(2026, 11, 30), 120) })),
            new BookCollection { Id = Guid.NewGuid(), Name = "测试收藏夹" },
            new NativeReaderTocItem[] { new(0, "第一章", 0, true) },
            new NativeReaderSearchItem[] { new(0, "第一章", "匹配的原文") },
            new AiConnectionSettings(), new KindleEmailSettings(),
            Data(("settings", new TtsSettings()), ("voices", new TtsVoiceInfo[] { new("voice", "中文", "zh-CN") })),
            new DictionaryDefinition[] { new("dictionary", "测试词典", "dict.json", 1, now) },
            new DictionaryEntry[] { new("word", "释义", "测试词典") },
            new DictionaryHtmlPalette("#ffffff", "#1c1c1c", "#767676", "#f5f5f5", "#efefef"),
            new WikipediaArticle[] { new("量子纠缠", "量子力学中的一种现象。",
                new Uri("https://zh.wikipedia.org/wiki/%E9%87%8F%E5%AD%90%E7%BA%A0%E7%BC%A0"), "zh") },
            Array.Empty<WikipediaArticle>(),
            Data(("chapterCount", 1), ("detectedEncoding", "Utf8"),
                ("chapters", new TxtChapterPreviewItem[] { new(1, 1, "第一章", false, 20) }), ("hasNextPage", false))
        ];
        foreach (var reply in replies)
        {
            // The context has no reflection fallback. An unregistered runtime
            // type would throw here, as it would when a native control loads it.
            using var document = JsonDocument.Parse(Serialize(Data(("ok", true), ("result", reply))));
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, document.RootElement.GetProperty("result").ValueKind);
        }
    }

    [Fact]
    public void LibraryFilterOptionsAndIdsUseTheGeneratedNativeWireContract()
    {
        var id = Guid.NewGuid();
        var options = new LibraryFilterOptions(["作者甲", "作者乙"], ["文学"], ["EPUB", "PDF"], ["小说"]);
        using var document = JsonDocument.Parse(Serialize(Data(("options", options), ("ids", new[] { id }))));
        var encoded = document.RootElement.GetProperty("options");
        Assert.Equal("作者甲", encoded.GetProperty("authors")[0].GetString());
        Assert.Equal("文学", encoded.GetProperty("tags")[0].GetString());
        Assert.Equal("PDF", encoded.GetProperty("formats")[1].GetString());
        Assert.Equal("小说", encoded.GetProperty("categories")[0].GetString());
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("ids")[0].GetString());
    }

    [Fact]
    public void ReadingMedalWireContractIncludesDesktopArtworkAndProgress()
    {
        var medals = new[] { new ReadingMedal("测试勋章", "测试条件", 1, 4, "M3,5 L12,20") };
        using var document = JsonDocument.Parse(Serialize(medals));
        var medal = document.RootElement[0];
        Assert.Equal("M3,5 L12,20", medal.GetProperty("icon").GetString());
        Assert.Equal(0.25, medal.GetProperty("fraction").GetDouble());
        Assert.False(medal.GetProperty("isUnlocked").GetBoolean());
    }

    [Fact]
    public void MembershipAndDevicesUseTheGeneratedNativeWireContract()
    {
        var user = Guid.NewGuid(); var device = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var member = new AccountMembership(user, AccountRegion.International, "lifetime", true, null, 6, 1, 1,
            128, 1024, 64, 2048, 1024, 256, 4096, null);
        var status = new DeviceFeatureStatus(member, [new(device, "Fictional native device", "iOS", "active", now, now)], device);
        using var document = JsonDocument.Parse(Serialize(status));
        Assert.Equal(1, document.RootElement.GetProperty("membership").GetProperty("region").GetInt32());
        Assert.Equal(6, document.RootElement.GetProperty("membership").GetProperty("deviceLimit").GetInt32());
        Assert.True(document.RootElement.GetProperty("isCurrentDeviceActive").GetBoolean());
        Assert.True(document.RootElement.GetProperty("canUseFeatures").GetBoolean());
        var restored = document.RootElement.Deserialize(IOSNativeJsonContext.Default.DeviceFeatureStatus)!;
        Assert.Equal(device, restored.CurrentDeviceId); Assert.Equal(user, restored.Membership.AccountId);
        Assert.Null(typeof(AccountMembership).Assembly.GetType("Libzora.Core.ActivationCodeRedemption"));
        Assert.False(restored.CanUseAccountCloudSync);
    }

    [Fact]
    public void NativeBridgeDoesNotRegisterFeedbackOrPurchaseDtos()
    {
        Assert.Null(typeof(IOSNativeJsonContext).Assembly.GetType("Libzora.iOSHead.NativeFeedbackRequest"));
        Assert.Null(typeof(IOSNativeJsonContext).GetProperty("FeedbackReceipt"));
        Assert.Null(typeof(IOSNativeJsonContext).GetProperty("MembershipProductArray"));
        Assert.Null(typeof(IOSNativeJsonContext).GetProperty("PurchaseOrder"));
        Assert.Null(typeof(IOSNativeJsonContext).GetProperty("PurchaseOrderArray"));
        // Local log validation remains useful for explicit local exports, not uploads.
        Assert.Null(FeedbackValidation.ValidateSoftwareLog("Libzora software log\nERROR reader.open"));
        Assert.NotNull(FeedbackValidation.ValidateSoftwareLog("bad\0log"));
    }

    [Fact]
    public void ReaderFontCatalogUsesStableKeysAndSafeLocalPaths()
    {
        Assert.Equal(4, ReaderFontCatalog.Entries.Count);
        Assert.Equal(
            ReaderFontCatalog.Entries.Count,
            ReaderFontCatalog.Entries.Select(entry => entry.Id).Distinct().Count());
        var entry = ReaderFontCatalog.Find("lxgw-wenkai-lite");
        Assert.NotNull(entry);
        Assert.Equal(
            "local://Assets/Fonts/catalog/LXGWWenKaiLite-Regular.ttf",
            ReaderFontCatalog.ResolveUrl(entry!));
        Assert.Null(ReaderFontCatalog.Find("../../etc/passwd"));
        Assert.Null(ReaderFontCatalog.ResolveLocalPath("local://../secret.ttf"));
        Assert.NotNull(ReaderFontCatalog.ResolveLocalPath("local://Assets/Fonts/catalog/font.ttf"));
    }

    [Fact]
    public void ReaderFontRegistryResolvesOnlyExistingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "libzora-reader-font-registry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "font.ttf");
            File.WriteAllText(path, "not a real font");
            ReaderFontRegistry.Replace([("catalog:test", path)]);
            Assert.True(ReaderFontRegistry.TryResolve("catalog:test", out var resolved));
            Assert.Equal(path, resolved);

            File.Delete(path);
            Assert.False(ReaderFontRegistry.TryResolve("catalog:test", out _));
            ReaderFontRegistry.Replace([]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HighlightStyleAndColorPreferencesNormalizeInvalidValues()
    {
        Assert.Equal("solid", MobilePreferences.NormalizeReaderHighlightStyle(null));
        Assert.Equal("marker", MobilePreferences.NormalizeReaderHighlightStyle("  MARKER "));
        Assert.Equal("wavy", MobilePreferences.NormalizeReaderHighlightStyle("wavy"));
        Assert.Equal("solid", MobilePreferences.NormalizeReaderHighlightStyle("unknown"));
        Assert.Equal("#000000", MobilePreferences.NormalizeReaderHighlightColor(null));
        Assert.Equal("#FFD54F", MobilePreferences.NormalizeReaderHighlightColor(" #ffd54f "));
        Assert.Equal("#000000", MobilePreferences.NormalizeReaderHighlightColor("yellow"));

        var preferences = Read(
            JsonDocument.Parse("""{"ReaderHighlightStyle":"marker","ReaderHighlightColor":"#64B5F6"}""").RootElement,
            new MobilePreferences(),
            IOSNativeJsonContext.Default.MobilePreferences);
        Assert.True(new MobilePreferences().StackBookSeries);
        Assert.Equal("marker", preferences.ReaderHighlightStyle);
        Assert.Equal("#64B5F6", preferences.ReaderHighlightColor);
    }

    [Fact]
    public void UiFontPreferenceRoundTripsAndNormalizesInvalidValues()
    {
        Assert.Equal(AppUiFont.KingHwaOldSong, new MobilePreferences().UiFont);

        var serialized = Serialize(new MobilePreferences { UiFont = AppUiFont.SystemDefault });
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal((int)AppUiFont.SystemDefault, document.RootElement.GetProperty("uiFont").GetInt32());

        var restored = Read(
            document.RootElement,
            new MobilePreferences(),
            IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(AppUiFont.SystemDefault, restored.UiFont);

        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-uifont-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var store = new MobilePreferencesStore(paths);
            store.UpdateAndSave(p => p with { UiFont = AppUiFont.SystemDefault });

            var reloaded = new MobilePreferencesStore(paths);
            reloaded.Load();
            Assert.Equal(AppUiFont.SystemDefault, reloaded.Current.UiFont);

            File.WriteAllText(Path.Combine(root, "mobile-preferences.json"), """{"UiFont":999,"ReaderPageAnimation":2,"ReaderFlowMode":9}""");
            var normalized = new MobilePreferencesStore(paths);
            normalized.Load();
            Assert.Equal(AppUiFont.KingHwaOldSong, normalized.Current.UiFont);
            Assert.Equal(MobilePageAnimation.Push, normalized.Current.ReaderPageAnimation);
            Assert.Equal(1, normalized.Current.ReaderFlowMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AiContractsSerializeWithSourceGeneratedContext()
    {
        var sources = new[]
        {
            new Libzora.iOSHead.NativeAiSource("S1", "第一章", "原文内容", false, 2, 120, 0)
        };
        var resultJson = Serialize(new Libzora.iOSHead.NativeAiResult("回答 [S1]", "思考过程", "全书关键词检索 · 1 个原文片段", sources));
        using var resultDoc = JsonDocument.Parse(resultJson);
        Assert.Equal("回答 [S1]", resultDoc.RootElement.GetProperty("text").GetString());
        Assert.Equal("思考过程", resultDoc.RootElement.GetProperty("reasoning").GetString());
        Assert.Equal("S1", resultDoc.RootElement.GetProperty("sources")[0].GetProperty("id").GetString());

        var eventJson = Serialize(Data(
            ("type", "aiText"),
            ("sessionId", "s1"),
            ("text", "partial"),
            ("reasoning", "step"),
            ("status", "retrieving"),
            ("sources", sources)));
        using var eventDoc = JsonDocument.Parse(eventJson);
        Assert.Equal("aiText", eventDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, eventDoc.RootElement.GetProperty("sources")[0].GetProperty("chapterIndex").GetInt32());

        var infoJson = Serialize(new Libzora.iOSHead.NativeAiInfo(true, "deepseek", "deepseek-chat"));
        using var infoDoc = JsonDocument.Parse(infoJson);
        Assert.True(infoDoc.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("deepseek", infoDoc.RootElement.GetProperty("provider").GetString());
    }
}
