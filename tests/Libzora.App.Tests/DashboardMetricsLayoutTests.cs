using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    // The five headline metrics share one row, so their secondary lines are
    // the first thing to run out of room. They must wrap inside the card at
    // every supported window width and language instead of being clipped.
    [Theory]
    [InlineData(1024, "zh-CN")]
    [InlineData(1294, "zh-CN")]
    [InlineData(1024, "en-US")]
    [InlineData(1294, "en-US")]
    public Task ReadingDashboardMetricsStayReadableInsideTheirCards(int width, string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Window.Width = width;
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ReadingDashboardPage"), null);
        await scope.Call<Task>("RefreshReadingDashboardAsync");

        // Reproduce the values that used to be composed into one clipped line.
        var average = scope.Get<TextBlock>("DashboardDailyAverageText");
        var listening = scope.Get<TextBlock>("DashboardListeningHintText");
        average.Text = language == "en-US" ? "7-day avg 3.9 min" : "近 7 天日均 3.9 分钟";
        listening.Text = language == "en-US" ? "Total listening 31 s" : "累计听书 31 秒";
        listening.IsVisible = true;
        await Render();

        // The daily average and listening time each keep their own line; both
        // must fit without wrapping even at the minimum window width.
        foreach (var block in new[] { average, listening })
        {
            Assert.True(block.IsEffectivelyVisible, block.Name);
            var unwrapped = MeasureUnwrapped(block);
            Assert.True(unwrapped <= block.Bounds.Width + 0.5,
                $"{language} {width}: '{block.Text}' needs {unwrapped:0.#}px but the card gives {block.Bounds.Width:0.#}px");
            Assert.Single(block.TextLayout.TextLines);
        }

        var metrics = scope.Get<Grid>("DashboardMetricsGrid");
        foreach (var block in metrics.GetVisualDescendants().OfType<TextBlock>())
        {
            Assert.False(string.IsNullOrEmpty(block.Text), block.Name);
            var unwrapped = MeasureUnwrapped(block);
            if (unwrapped <= block.Bounds.Width + 0.5)
                continue;

            Assert.True(block.TextWrapping == TextWrapping.Wrap,
                $"{language} {width}: '{block.Text}' needs {unwrapped:0.#}px but the card gives {block.Bounds.Width:0.#}px and does not wrap");
            Assert.True(block.TextLayout.TextLines.Count > 1,
                $"{language} {width}: '{block.Text}' was clipped to a single line");
            Assert.True(block.Bounds.Height >= block.TextLayout.Height - 0.5,
                $"{language} {width}: '{block.Text}' is clipped vertically");
        }

        Capture(scope.Window, $"dashboard-metrics-{language}-{width}");
        ((Libzora.App)Application.Current!).ApplyLanguage("zh-CN");
    });

    private static double MeasureUnwrapped(TextBlock block) =>
        new TextLayout(block.Text!,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            block.Foreground).Width;
}
