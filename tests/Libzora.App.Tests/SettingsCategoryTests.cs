using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task SettingsCategoriesGroupGlobalOptionsAndIntegrations() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        await Render();
        Assert.True(scope.Get<Control>("SettingsGeneralSection").IsEffectivelyVisible);
        foreach (var (control, category) in new[]
        {
            ("UiLanguageBox", "General"), ("MainThemeBox", "General"),
            ("NetworkEnabledCheck", "General"), ("TranslationGoogleProxyPane", "General"),
            ("SettingsCalibreExpander", "Library"),
            ("SettingsMcpExpander", "Advanced"), ("SettingsDiagnosticsExpander", "Advanced"),
            ("ExportSoftwareLogsButton", "Advanced"), ("SettingsSendToKindleExpander", "Kindle")
        })
            Assert.Contains(scope.Get<Control>(control).GetVisualAncestors(),
                ancestor => ReferenceEquals(ancestor, scope.Get<Control>($"Settings{category}Section")));

        var data = scope.Get<StackPanel>("SettingsDataSection");
        Assert.Same(scope.Get<Border>("SettingsReadingDataSection"), data.Children.Last());
        scope.Call("OpenSettingsExpander", "Advanced", scope.Get<Expander>("SettingsDiagnosticsExpander"));
        await Render();
        Capture(scope.Window, "settings-diagnostics-single-title");
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task NetworkSettingsTooltipsReflectActiveFeatures(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        try
        {
            ((Libzora.App)Application.Current!).ApplyLanguage(language);
            scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
            await Render();

            var networkTip = Assert.IsType<string>(ToolTip.GetTip(scope.Get<ToggleSwitch>("NetworkEnabledCheck")));
            var proxyPaneTip = Assert.IsType<string>(ToolTip.GetTip(scope.Get<Grid>("TranslationGoogleProxyPane")));
            var proxyBoxTip = Assert.IsType<string>(ToolTip.GetTip(scope.Get<TextBox>("TranslationGoogleProxyBox")));

            Assert.Equal(proxyPaneTip, proxyBoxTip);
            Assert.DoesNotContain("在线匹配", networkTip, StringComparison.Ordinal);
            Assert.DoesNotContain("online matching", networkTip, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Google", proxyPaneTip, StringComparison.OrdinalIgnoreCase);

            if (language == "zh-CN")
            {
                Assert.Contains("云同步", networkTip, StringComparison.Ordinal);
                Assert.Contains("在线 AI", networkTip, StringComparison.Ordinal);
                Assert.Contains("软件更新", networkTip, StringComparison.Ordinal);
                Assert.Contains("拼音模型", proxyPaneTip, StringComparison.Ordinal);
                Assert.Contains("语音模型", proxyPaneTip, StringComparison.Ordinal);
                Assert.Contains("Calibre", proxyPaneTip, StringComparison.Ordinal);
                Assert.Contains("软件更新", proxyPaneTip, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Cloud sync", networkTip, StringComparison.Ordinal);
                Assert.Contains("online AI", networkTip, StringComparison.Ordinal);
                Assert.Contains("software updates", networkTip, StringComparison.Ordinal);
                Assert.Contains("pinyin models", proxyPaneTip, StringComparison.Ordinal);
                Assert.Contains("TTS voice models", proxyPaneTip, StringComparison.Ordinal);
                Assert.Contains("Calibre", proxyPaneTip, StringComparison.Ordinal);
                Assert.Contains("software updates", proxyPaneTip, StringComparison.Ordinal);
            }
        }
        finally
        {
            ((Libzora.App)Application.Current!).ApplyLanguage("zh-CN");
        }
    });
}
