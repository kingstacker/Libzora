using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class TtsVoicePickerTests
{
    private static readonly TtsVoiceInfo[] InstalledVoices =
    [
        new("Tingting", "Tingting", "zh-CN"),
        new("Samantha", "Samantha", "en-US"),
        new("Bells", "Bells", "en-US"),
        new("kokoro-int8:0", "Kokoro int8 · af_maple", "en-US"),
        new("kokoro-int8:3", "Kokoro int8 · zf_001", "zh-CN"),
        new("kokoro-int8:58", "Kokoro int8 · zm_009", "zh-CN"),
        new("kokoro-fp32:0", "Kokoro fp32 · af_maple", "en-US"),
        new("kokoro-fp32:3", "Kokoro fp32 · zf_001", "zh-CN"),
        new("kokoro-fp32:58", "Kokoro fp32 · zm_009", "zh-CN"),
        new("melo-zh-en:0", "MeloTTS · 中文女声", "zh-CN"),
    ];

    [Fact]
    public void RecommendedListIsShortAndPrefersInt8()
    {
        var voices = TtsVoicePicker.Select(InstalledVoices, showAll: false);

        Assert.Equal(
            [
                "kokoro-int8:3",
                "kokoro-int8:58",
                "kokoro-int8:0",
                "melo-zh-en:0",
                "Tingting",
                "Samantha",
            ],
            voices.Select(voice => voice.Id));
        Assert.DoesNotContain(voices, voice => voice.Id == "kokoro-fp32:3");
    }

    [Fact]
    public void RecommendedListFallsBackToSystemVoicesWithoutPackages()
    {
        var system = new[]
        {
            new TtsVoiceInfo("Bad News", "Bad News", "en-US"),
            new TtsVoiceInfo("Tingting", "Tingting", "zh-CN"),
            new TtsVoiceInfo("Alice", "Alice", "it-IT"),
            new TtsVoiceInfo("Samantha", "Samantha", "en-US"),
        };

        var voices = TtsVoicePicker.Select(system, showAll: false);

        Assert.Equal(["Tingting", "Samantha"], voices.Select(voice => voice.Id));
    }

    [Fact]
    public void SelectedVoiceSurvivesWhenItIsNotRecommended()
    {
        var voices = TtsVoicePicker.Select(
            InstalledVoices,
            showAll: false,
            selectedVoice: "kokoro-fp32:3");

        Assert.Equal("kokoro-fp32:3", voices[0].Id);
        Assert.Contains(voices, voice => voice.Id == "kokoro-int8:3");
    }

    [Fact]
    public void FullListHidesNoveltyVoicesAndKeepsEveryRealVoice()
    {
        var voices = TtsVoicePicker.Select(InstalledVoices, showAll: true);

        Assert.DoesNotContain(voices, voice => voice.Id == "Bells");
        Assert.Equal(InstalledVoices.Length - 1, voices.Count);
        Assert.Equal(voices.Count, voices.Select(voice => voice.Id).Distinct().Count());
        Assert.Equal("kokoro-int8:3", voices[0].Id);
        Assert.True(
            voices.ToList().FindIndex(voice => voice.Id == "Samantha")
            > voices.ToList().FindIndex(voice => voice.Id == "melo-zh-en:0"));
    }

    [Fact]
    public void SettingsRoundTripKeepsTheVoiceDisplayPreference()
    {
        var normalized = TtsSettings.Normalize(new TtsSettings
        {
            ShowAllVoices = true,
            VoiceCulture = " en-US ",
        });

        Assert.True(normalized.ShowAllVoices);
        Assert.Equal("en-US", normalized.VoiceCulture);
        Assert.True(normalized.Clone().ShowAllVoices);
        Assert.False(TtsSettings.Normalize(new TtsSettings()).ShowAllVoices);
    }
}
