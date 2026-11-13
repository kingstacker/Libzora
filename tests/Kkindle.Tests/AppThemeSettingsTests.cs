using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class AppThemeSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MainTheme\":-1}")]
    [InlineData("{\"MainTheme\":999}")]
    [InlineData("{\"MainTheme\":3}")]
    [InlineData("{\"MainTheme\":4}")]
    [InlineData("{\"MainTheme\":5}")]
    public void OlderOrUnsupportedPreferencesKeepBlackAndWhite(string json)
    {
        var settings = AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(json));
        Assert.Equal(AppTheme.Classic, settings.MainTheme);
        Assert.False(settings.ColorfulMode);
        Assert.Equal(ReaderTheme.Classic, settings.ReaderAppearance.Theme);
        Assert.False(settings.ReaderAppearance.PaperEnabled);
    }

    [Fact]
    public void ColorfulModeDefaultsOffAndRoundTripsWithoutChangingTheTheme()
    {
        var defaults = AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>("{}"));
        Assert.False(defaults.ColorfulMode);
        Assert.Equal(AppTheme.Classic, defaults.MainTheme);
        Assert.DoesNotContain("ColorfulMode", JsonSerializer.Serialize(defaults));

        var enabled = AppSettings.Normalize(new AppSettings
        {
            MainTheme = AppTheme.Green,
            ColorfulMode = true
        });
        Assert.True(enabled.ColorfulMode);
        Assert.Equal(AppTheme.Green, enabled.MainTheme);
        var stored = JsonSerializer.Serialize(enabled);
        Assert.Contains("ColorfulMode", stored);
        var restored = JsonSerializer.Deserialize<AppSettings>(stored)!;
        Assert.True(restored.ColorfulMode);
        Assert.Equal(AppTheme.Green, restored.MainTheme);
    }

    [Theory]
    [InlineData(AppTheme.Classic)]
    [InlineData(AppTheme.Beige)]
    [InlineData(AppTheme.Green)]
    public async Task StartupAndAsyncLoadsPreserveMainAndReaderThemesIndependently(AppTheme theme)
    {
        var directory = TestHelpers.CreateTempDirectory();
        try
        {
            var store = new AppSettingsStore(new AppPaths(directory));
            var settings = AppSettings.Normalize(new AppSettings
            {
                MainTheme = theme,
                ReaderAppearance = new() { Theme = ReaderTheme.Green, PaperEnabled = true, FibersEnabled = true },
                DefaultReaderLayout = new(FontScale: 1.3, LineHeight: 1.8)
            });
            await store.SaveAsync(settings);
            Assert.Equal(settings, await store.LoadAsync());
            Assert.Equal(settings, store.LoadSynchronously());
        }
        finally { TestHelpers.TryDelete(directory); }
    }
}
