using Avalonia.Controls;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;
using SkiaSharp;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ColorfulModeSamplesCoversAndPersistsIndependentlyFromTheTheme() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var toggle = scope.Get<ToggleSwitch>("ColorfulModeCheck");
        var library = scope.Get<AmbientBackground>("AmbientBackdrop");
        var reader = scope.Get<AmbientBackground>("ReaderAmbientBackdrop");
        Assert.False(toggle.IsChecked);
        Assert.Empty(library.TargetColors);
        Assert.Empty(reader.TargetColors);

        toggle.IsChecked = true;
        await Until(() => library.TargetColors.Count == AmbientPalette.FieldCount);
        await UntilSettled(() => !library.IsFading, "the wash fade should finish");
        Capture(scope.Window, "colorful-library-on");
        Assert.Equal(AmbientPalette.FieldCount, reader.TargetColors.Count);
        Assert.Equal(0.22, library.WashOpacity, 6);

        // The reader keeps a softer wash, and the dark Ink theme is slightly
        // stronger so the gradient remains visible on dark paper.
        Assert.Equal(0.07, reader.WashOpacity, 6);
        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = ReaderTheme.Ink });
        await Render();
        Assert.Equal(0.10, reader.WashOpacity, 6);

        // The main theme is independent from the colourful toggle.
        scope.Get<ComboBox>("MainThemeBox").SelectedIndex = (int)AppTheme.Beige;
        await Render();
        Assert.True(toggle.IsChecked);
        Assert.Equal(AmbientPalette.FieldCount, library.TargetColors.Count);
        Assert.Equal(Color.Parse("#F4F2EC"), ColorOf(scope.Get<Grid>("LibraryRoot").Background));

        await UntilSettled(() => !library.IsFading, "the wash fade should finish");
        Assert.Equal(AmbientPalette.FieldCount, library.DisplayedColors.Count);
        Assert.All(library.DisplayedColors, color => Assert.True(color.Strength > 0));
        Capture(scope.Window, "colorful-library-beige");

        scope.Get<MenuItem>("LibraryListViewMenuItem").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await Render();
        Capture(scope.Window, "colorful-library-list");

        Assert.True(await scope.Call<Task<bool>>("FlushAppSettingsAsync"));
        var saved = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.True(saved.ColorfulMode);
        Assert.Equal(AppTheme.Beige, saved.MainTheme);

        toggle.IsChecked = false;
        await UntilSettled(
            () => !library.IsFading && library.TargetColors.Count == 0,
            "turning the mode off should fade the wash out");
        Assert.Empty(library.DisplayedColors);
        Assert.Empty(reader.TargetColors);
        Capture(scope.Window, "colorful-library-off");

        Assert.True(await scope.Call<Task<bool>>("FlushAppSettingsAsync"));
        saved = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.False(saved.ColorfulMode);
        Assert.Equal(AppTheme.Beige, saved.MainTheme);
    });

    [Fact]
    public Task ColorfulModeUsesVividCoverColoursAndIgnoresNeutralNeighbours() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        Assert.False(string.IsNullOrWhiteSpace(card.Book.CoverPath));
        WriteCover(Path.Combine(scope.Paths.Data, card.Book.CoverPath!), new SKColor(210, 60, 45));
        await scope.Call<Task>("RefreshLibraryAsync");
        await Render();

        scope.Get<ToggleSwitch>("ColorfulModeCheck").IsChecked = true;
        var backdrop = scope.Get<AmbientBackground>("AmbientBackdrop");
        await Until(() => backdrop.TargetColors.Count == AmbientPalette.FieldCount);
        await UntilSettled(() => !backdrop.IsFading, "the wash fade should finish");
        Assert.All(backdrop.DisplayedColors, color =>
        {
            Assert.Equal(1d, color.Strength, 6);
            Assert.True(color.Red > color.Green + 0.3, $"{color}");
            Assert.True(color.Red > color.Blue + 0.3, $"{color}");
        });
        Capture(scope.Window, "colorful-vivid");
    });

    [Fact]
    public Task ColorfulModeWashesTheReaderPaper() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        Assert.False(string.IsNullOrWhiteSpace(card.Book.CoverPath));
        WriteCover(Path.Combine(scope.Paths.Data, card.Book.CoverPath!), new SKColor(210, 60, 45));
        await scope.Call<Task>("RefreshLibraryAsync");
        await Render();
        scope.Get<ToggleSwitch>("ColorfulModeCheck").IsChecked = true;
        var library = scope.Get<AmbientBackground>("AmbientBackdrop");
        await Until(() => library.TargetColors.Count == AmbientPalette.FieldCount);
        await UntilSettled(() => !library.IsFading, "the wash fade should finish");

        var reader = scope.Get<AmbientBackground>("ReaderAmbientBackdrop");
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        await Render();
        Assert.Equal(AmbientPalette.FieldCount, reader.TargetColors.Count);
        Assert.Equal(0.07, reader.WashOpacity, 6);
        Capture(scope.Window, "colorful-reader-light");

        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings { Theme = ReaderTheme.Ink });
        await Render();
        Assert.Equal(0.10, reader.WashOpacity, 6);
        Capture(scope.Window, "colorful-reader-ink");

        scope.Get<Control>("ReaderRoot").IsVisible = false;
        scope.Get<Control>("LibraryRoot").IsVisible = true;
        scope.Call("ChangeReaderAppearance", new ReaderAppearanceSettings());
        await Render();
    });

    [Fact]
    public Task ColorfulModeReachesPagesWithoutAShelf() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        var card = scope.Window.ViewModel.Books[0];
        WriteCover(Path.Combine(scope.Paths.Data, card.Book.CoverPath!), new SKColor(210, 60, 45));
        await scope.Call<Task>("RefreshLibraryAsync");
        await Render();
        scope.Get<ToggleSwitch>("ColorfulModeCheck").IsChecked = true;
        var backdrop = scope.Get<AmbientBackground>("AmbientBackdrop");
        await Until(() => backdrop.TargetColors.Count == AmbientPalette.FieldCount);
        await UntilSettled(() => !backdrop.IsFading, "the wash fade should finish");

        // Settings and the device page have no shelf of their own; the fallback
        // palette keeps the wash visible behind them.
        scope.Call("SettingsButton_Click", null, new Avalonia.Interactivity.RoutedEventArgs());
        await Render();
        Assert.True(scope.Get<Control>("SettingsPage").IsEffectivelyVisible);
        Assert.Null(scope.Get<Grid>("SettingsPage").Background);
        Capture(scope.Window, "colorful-settings-page");

        scope.Call("ShowStage3Page", scope.Get<Grid>("DevicePage"), null);
        await Render();
        Assert.True(scope.Get<Control>("DevicePage").IsEffectivelyVisible);
        Assert.Null(scope.Get<Grid>("DevicePage").Background);
        Capture(scope.Window, "colorful-device-page");
    });

    [Fact]
    public Task ColorfulModeRestoresAfterRestart() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Get<ToggleSwitch>("ColorfulModeCheck").IsChecked = true;
        await Until(() =>
            scope.Get<AmbientBackground>("AmbientBackdrop").TargetColors.Count == AmbientPalette.FieldCount);
        scope.RequestQuit();
        await Until(() => !scope.Window.IsVisible);
        var saved = await new AppSettingsStore(scope.Paths).LoadAsync();
        Assert.True(saved.ColorfulMode);

        await using var reopened = await TestWindow.Create(saved);
        await SeedLayoutLibrary(reopened);
        Assert.True(reopened.Get<ToggleSwitch>("ColorfulModeCheck").IsChecked);
        var backdrop = reopened.Get<AmbientBackground>("AmbientBackdrop");
        await Until(() => backdrop.TargetColors.Count == AmbientPalette.FieldCount);
    });

    private static void WriteCover(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(152, 212);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
