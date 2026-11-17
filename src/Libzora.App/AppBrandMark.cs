using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Libzora;

/// <summary>
/// The app icon used as the small brand mark in window chrome and first-run
/// screens. It is the same artwork as the Dock, taskbar and window icon, so
/// the mark stays consistent after the Libzora rename.
/// </summary>
internal static class AppBrandMark
{
    private static readonly Uri IconUri = new("avares://Libzora.App/Assets/Icons/libzora.png");

    public static Image Create(double size)
    {
        var image = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = new Bitmap(AssetLoader.Open(IconUri))
        };
        AutomationProperties.SetName(image, "Libzora");
        return image;
    }
}
