using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class LocalSystemTtsTests
{
    [Fact]
    public void ParsesInstalledMacVoicesWithSpacesAndUnicodeNames()
    {
        var voices = LocalSystemTtsEngine.ParseMacVoices("Bad News            en_US    # Hello\n婷婷                zh_CN    # 你好\ninvalid\n");
        Assert.Equal(2, voices.Count);
        Assert.Equal(new TtsVoiceInfo("Bad News", "Bad News", "en-US"), voices[0]);
        Assert.Equal("zh-CN", voices[1].Culture);
    }

    [Fact]
    public void LegacyOnlineVoiceFallsBackToInstalledVoiceOfSameLanguage()
    {
        TtsVoiceInfo[] voices = [new("Samantha", "Samantha", "en-US"), new("Tingting", "Tingting", "zh-CN")];
        Assert.Equal("Tingting", LocalSystemTtsEngine.SelectVoice(voices, "zh-CN-XiaoxiaoNeural")?.Id);
        Assert.Equal("Samantha", LocalSystemTtsEngine.SelectVoice(voices, "en-US-AriaNeural")?.Id);
        Assert.Equal("Samantha", LocalSystemTtsEngine.SelectVoice(voices, "Samantha")?.Id);
        Assert.Null(LocalSystemTtsEngine.SelectVoice([], "zh-CN-XiaoxiaoNeural"));
    }

    [Fact]
    public async Task InstalledSystemVoiceProducesPlayablePcmWave()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        using var engine = new LocalSystemTtsEngine();
        var voices = await engine.GetVoicesAsync();
        Assert.NotEmpty(voices);
        var result = await engine.SynthesizeAsync("Local speech. 你好。 <speak> plain text </speak>",
            new TtsOptions { Voice = voices.FirstOrDefault(v => v.Culture == "zh-CN")?.Id ?? voices[0].Id });
        try
        {
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal("wav", result.Format);
            var audio = await File.ReadAllBytesAsync(result.AudioPath!);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(audio, 8, 4));
            Assert.True(audio.Length > 44);
        }
        finally { if (result.AudioPath is not null) File.Delete(result.AudioPath); }
    }

    [Fact]
    public async Task CancelledRequestDoesNotSynthesize()
    {
        using var engine = new LocalSystemTtsEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.SynthesizeAsync("取消", new TtsOptions(), cancellation.Token));
    }
}
