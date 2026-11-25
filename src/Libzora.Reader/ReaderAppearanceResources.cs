using Avalonia.Controls;
using Avalonia.Media;
using Libzora.Core;

namespace Libzora;

internal static class ReaderAppearanceResources
{
    public static void Populate(IResourceDictionary resources, ReaderAppearanceSettings appearance, bool pureBlack = false)
    {
        var palette = ReaderPalette.For(appearance.Theme, pureBlack);
        Set("Page", palette.Page);
        Set("Chrome", palette.Chrome);
        Set("Sidebar", palette.Sidebar);
        Set("Ink", palette.Ink);
        Set("Muted", palette.Muted);
        Set("Border", palette.Border);
        Set("Hover", palette.Hover);
        Set("Selected", palette.Selected);
        Set("Pressed", palette.Selected);
        Set("Accent", palette.Accent);
        Set("OnAccent", palette.OnAccent);
        Set("Input", palette.Page);
        var pageBrush = ReaderPaperTexture.CreateBrush(palette.Page, appearance);
        resources["ReaderPageBrush"] = pageBrush;
        // Glyph and selection foregrounds need an opaque solid color: a tiled
        // paper texture cannot be painted through text and renders invisible.
        resources["ReaderPageSolidBrush"] = new SolidColorBrush(palette.Page);
        // Structural reader panes are transparent and reveal the single paper
        // surface painted by ReaderRoot. Keep these aliases for standalone
        // controls and popups that still need an opaque reader background.
        resources["ReaderChromeBrush"] = pageBrush;
        resources["ReaderSidebarBrush"] = pageBrush;

        void Set(string name, Color color)
        {
            resources[$"Reader{name}Color"] = color;
            resources[$"Reader{name}Brush"] = new SolidColorBrush(color);
        }
    }

    // Fluent resources live inside the reader scope, never in the library.
    public static void PopulateControls(IResourceDictionary resources, ReaderAppearanceSettings appearance, bool pureBlack = false)
    {
        var palette = ReaderPalette.For(appearance.Theme, pureBlack);
        AppAppearanceResources.Populate(resources, AppPalette.FromReader(palette));
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
                     "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            resources[key] = palette.Accent;
        foreach (var key in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
                     "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                     "LibzoraScrollBarThumbPointerOverBrush", "LibzoraScrollBarThumbPressedBrush" })
            resources[key] = new SolidColorBrush(palette.Accent);
        foreach (var key in new[] { "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed",
                     "SliderTrackValueFillDisabled", "SliderThumbBackgroundDisabled", "LibzoraScrollBarThumbBrush",
                     "LibzoraScrollBarThumbDisabledBrush" })
            resources[key] = new SolidColorBrush(palette.Border);
        foreach (var key in new[] { "LibzoraScrollBarBackgroundBrush", "LibzoraScrollBarExpandedBackgroundBrush",
                     "LibzoraScrollBarBorderBrush" })
            resources[key] = Brushes.Transparent;
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused",
                     "TextControlPlaceholderForegroundPointerOver" })
            resources[key] = new SolidColorBrush(palette.Muted);
    }
}
