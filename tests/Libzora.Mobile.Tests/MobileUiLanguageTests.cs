using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Localization;
using Libzora.Mobile.Views;
using Xunit;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class MobileUiLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libzora-language-tests", Guid.NewGuid().ToString("N"));

    public MobileUiLanguageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void NewAndLegacyPreferencesFollowTheSystemWithoutResettingOtherChoices()
    {
        Assert.Equal("auto", new MobilePreferences().UiLanguage);
        File.WriteAllText(Path.Combine(_root, "mobile-preferences.json"),
            """{"ReaderFontScale":1.45,"GalleryMode":true,"ReaderVerticalWriting":true}""");
        var store = Store();
        store.Load();
        Assert.Equal("auto", store.Current.UiLanguage);
        Assert.Equal(1.45, store.Current.ReaderFontScale);
        Assert.True(store.Current.GalleryMode);
        Assert.True(store.Current.ReaderVerticalWriting);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("unsupported", "auto")]
    [InlineData("fr-FR", "auto")]
    [InlineData(" EN-gb ", "en-US")]
    [InlineData("en_US", "en-US")]
    [InlineData("zh-Hant-TW", "zh-CN")]
    public void StoredChoiceIsNormalizedWithoutSavingAnEffectiveSystemLanguage(string? value, string expected) =>
        Assert.Equal(expected, MobileUiLanguageService.NormalizePreference(value));

    [Theory]
    [InlineData("auto", "zh-CN", "en-US", "zh-CN")]
    [InlineData("auto", "en-GB", "zh-CN", "en-US")]
    [InlineData("auto", "zh-Hant-TW", "en-US", "zh-CN")]
    [InlineData("auto", "fr-FR", "en-GB", "en-US")]
    [InlineData("auto", "fr-FR", "de-DE", "en-US")]
    [InlineData("en-US", "zh-CN", "zh-Hant", "en-US")]
    [InlineData("zh-CN", "en-US", "fr-FR", "zh-CN")]
    public void SystemPreferenceOrderAndManualOverridesAreRespected(string choice, string first, string second, string expected) =>
        Assert.Equal(expected, MobileUiLanguageService.ResolveLanguage(choice, [first, second]));

    [Theory]
    [InlineData("auto")]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void LanguageChoiceSurvivesRestartAndPreservesReaderPreferences(string language)
    {
        var store = Store();
        store.Update(value => value with
        {
            UiLanguage = language, ReaderFontScale = 1.45,
            ReaderVerticalWriting = true, ReaderTheme = ReaderTheme.Ink, GalleryMode = true
        });
        var restored = Store();
        restored.Load();
        Assert.Equal(language, restored.Current.UiLanguage);
        Assert.Equal(1.45, restored.Current.ReaderFontScale);
        Assert.True(restored.Current.ReaderVerticalWriting);
        Assert.True(restored.Current.GalleryMode);
        Assert.Equal(ReaderTheme.Ink, restored.Current.ReaderTheme);
    }

    [Fact]
    public void ResumeRefreshesAutomaticLanguageButNeverOverridesAManualChoice()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var uiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        var store = Store();
        IReadOnlyList<string> system = ["en-US"];
        var service = new MobileUiLanguageService(store, () => system);
        Assert.Equal("en-US", service.Language);
        Assert.Equal("auto", store.Current.UiLanguage);

        system = ["zh-Hant-TW", "en-US"];
        Assert.True(service.Refresh());
        Assert.Equal("zh-CN", service.Language);
        Assert.Equal("auto", store.Current.UiLanguage);

        store.Update(value => value with { UiLanguage = "en-US" });
        Assert.True(service.Refresh());
        system = ["zh-CN"];
        Assert.False(service.Refresh());
        Assert.Equal("en-US", service.Language);

        store.Update(value => value with { UiLanguage = "auto" });
        Assert.True(service.Refresh());
        Assert.Equal("zh-CN", service.Language);
        Assert.Same(culture, System.Globalization.CultureInfo.CurrentCulture);
        Assert.Same(uiCulture, System.Globalization.CultureInfo.CurrentUICulture);
    }

    [Fact]
    public async Task EnglishCatalogWorksOnBackgroundThreadsAndFormatsBeforeSubstitution()
    {
        _ = new MobileUiLanguageService(Store(), () => ["en-US"]);
        Assert.Equal("Profile", UiText.Get("我的"));
        Assert.Equal("Done", UiText.Get("完成"));
        Assert.Equal("Local Dictionary", UiText.Get("本地词典"));
        Assert.Equal("Book Details", UiText.Get("书籍详情"));
        Assert.Equal("Not Set", UiText.Get("未设置"));
        Assert.Equal("1 item", UiText.Get("1 条资料"));
        Assert.Equal("Imported 3 books", await Task.Run(() => UiText.Get("已导入 {0} 本", 3)));
        var title = "中文书名 {1} · English";
        Assert.Equal("Explain this passage: “" + title + "”", UiText.Get("解释这段话：「{0}」", title));
        Assert.Equal("并非界面文字的中文正文", UiText.Get("并非界面文字的中文正文"));
    }

    [Fact]
    public void NativeJsonRoundTripKeepsLanguageAndDoesNotTranslateBookContent()
    {
        var preferences = new MobilePreferences { UiLanguage = "en-US", ReaderFontFamily = "京华老宋体" };
        using var json = JsonDocument.Parse(Serialize(preferences));
        Assert.Equal("en-US", json.RootElement.GetProperty("uiLanguage").GetString());
        var restored = Read(json.RootElement, new MobilePreferences(), Libzora.iOSHead.IOSNativeJsonContext.Default.MobilePreferences);
        Assert.Equal(preferences, restored);

        var state = new NativeReaderState(true, true, false, "我的", "原文章节", "3 / 9", 25,
            false, null, TtsPlaybackState.Stopped, "", "原文摘录");
        using var reader = JsonDocument.Parse(Serialize(state));
        Assert.Equal("我的", reader.RootElement.GetProperty("title").GetString());
        Assert.Equal("原文章节", reader.RootElement.GetProperty("chapter").GetString());
        Assert.Equal("原文摘录", reader.RootElement.GetProperty("selectedText").GetString());
    }

    private MobilePreferencesStore Store() => new(new AppPaths(_root));

    public void Dispose()
    {
        UiText.Configure(_ => null, "zh-CN");
        Directory.Delete(_root, recursive: true);
    }
}
