using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Libzora.Core;

namespace Libzora;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

/// <summary>
/// Owner-window lookup and custom dialog chrome.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private async Task InsertImageAsync(SelectionSnapshot? selection)
    {
        var target = selection ?? GetToolbarSelection(allowCaret: true);
        var topLevel = GetOwnerWindow();
        if (target is null || topLevel is null || target.Block.State.IsCitation)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("插入图片"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(UiText.Get("图片"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length == 0) return;

        var source = $"data:{MimeTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";
        InsertImageBlock(target, source, Path.GetFileNameWithoutExtension(path));
    }

    private Window? GetOwnerWindow() =>
        TopLevel.GetTopLevel(this) as Window
        ?? TopLevel.GetTopLevel(FormattingToolbar) as Window;

    private static void ApplyCustomDialogChrome(
        Window dialog,
        string title,
        Action close,
        bool canResize = false)
    {
        if (dialog.Content is not Control body)
            return;

        // Detach the existing body before putting it into the custom frame.
        // Avalonia otherwise sees the control attached to two logical parents
        // during the Content replacement and raises AttachedToLogicalTreeCore.
        dialog.Content = null;

        dialog.WindowDecorations = MacTrafficLights.IsSupported
            ? WindowDecorations.Full
            : WindowDecorations.None;
        dialog.ExtendClientAreaToDecorationsHint = true;
        dialog.ExtendClientAreaTitleBarHeightHint = MacTrafficLights.IsSupported
            ? MacTrafficLights.TitleBarHeight
            : -1;
        dialog.UseLayoutRounding = true;

        var titleDragRegion = new Border
        {
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Children =
                {
                    AppBrandMark.Create(18),
                    new TextBlock
                    {
                        Text = "Libzora",
                        FontSize = 12,
                        Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "·",
                        FontSize = 12,
                        Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 12,
                        Foreground = AppAppearanceResources.GetBrush("InkBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        titleDragRegion.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed)
                return;

            if (canResize && e.ClickCount == 2)
            {
                dialog.WindowState = dialog.WindowState is WindowState.Maximized or WindowState.FullScreen
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                dialog.BeginMoveDrag(e);
            }
            e.Handled = true;
        };

        var captionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };
        captionButtons.Children.Add(CreateDialogCaptionButton(
            "M 0,5 H 10",
            UiText.Get("最小化"),
            (_, _) => dialog.WindowState = WindowState.Minimized));
        AvaloniaPath? maximizeGlyph = null;
        Button? maximizeButton = null;
        if (canResize)
        {
            maximizeGlyph = new AvaloniaPath
            {
                Data = Geometry.Parse("M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z"),
                Width = 10,
                Height = 10
            };
            maximizeButton = CreateDialogCaptionButton(
                maximizeGlyph,
                UiText.Get("最大化"),
                (_, _) =>
                {
                    dialog.WindowState = dialog.WindowState is WindowState.Maximized or WindowState.FullScreen
                        ? WindowState.Normal
                        : WindowState.Maximized;
                });
            captionButtons.Children.Add(maximizeButton);
        }
        captionButtons.Children.Add(CreateDialogCaptionButton(
            "M 0,0 L 10,10 M 10,0 L 0,10",
            UiText.Get("关闭"),
            (_, _) => close()));

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            Children = { titleDragRegion, captionButtons }
        };
        Grid.SetColumn(captionButtons, 1);

        if (MacTrafficLights.IsSupported)
        {
            captionButtons.IsVisible = false;
            if (titleDragRegion.Child is StackPanel brand)
                brand.Margin = new Thickness(MacTrafficLights.TitleContentInset, 0, 16, 0);
        }

        var titleBarBorder = new Border
        {
            BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
            BorderThickness = new Thickness(0),
            Child = titleBar
        };
        var frame = new Grid
        {
            RowDefinitions = new RowDefinitions("38,*"),
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            Children = { titleBarBorder, body }
        };
        Grid.SetRow(body, 1);
        var resizeLayer = canResize ? CreateDialogResizeLayer(dialog) : null;
        if (resizeLayer is not null)
        {
            Grid.SetRowSpan(resizeLayer, 2);
            resizeLayer.ZIndex = 10;
            frame.Children.Add(resizeLayer);
        }
        dialog.Content = new Border
        {
            BorderBrush = AppAppearanceResources.GetBrush("InkBrush"),
            BorderThickness = new Thickness(1),
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            Child = frame
        };

        if (canResize && maximizeGlyph is not null && maximizeButton is not null)
        {
            void UpdateChrome()
            {
                var maximized = dialog.WindowState is WindowState.Maximized or WindowState.FullScreen;
                maximizeGlyph.Data = Geometry.Parse(maximized
                    ? "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z"
                    : "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z");
                var label = UiText.Get(maximized ? "还原" : "最大化");
                AutomationProperties.SetName(maximizeButton, label);
                ToolTip.SetTip(maximizeButton, label);
                resizeLayer!.IsVisible = !maximized;
            }

            dialog.PropertyChanged += (_, change) =>
            {
                if (change.Property == Window.WindowStateProperty)
                    UpdateChrome();
            };
            UpdateChrome();
        }
    }

    private static Button CreateDialogCaptionButton(
        string geometry,
        string label,
        EventHandler<RoutedEventArgs> handler)
    {
        return CreateDialogCaptionButton(
            new AvaloniaPath
            {
                Data = Geometry.Parse(geometry),
                Width = 10,
                Height = 10
            },
            label,
            handler);
    }

    private static Button CreateDialogCaptionButton(
        Control content,
        string label,
        EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = content
        };
        button.Classes.Add("caption");
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        button.Click += handler;
        return button;
    }

    private static Grid CreateDialogResizeLayer(Window dialog)
    {
        var layer = new Grid();
        AddDialogResizeHandle(layer, dialog, "West", 6, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.SizeWestEast);
        AddDialogResizeHandle(layer, dialog, "East", 6, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.SizeWestEast);
        AddDialogResizeHandle(layer, dialog, "North", double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Top, StandardCursorType.SizeNorthSouth);
        AddDialogResizeHandle(layer, dialog, "South", double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.SizeNorthSouth);
        AddDialogResizeHandle(layer, dialog, "NorthWest", 12, 12, HorizontalAlignment.Left, VerticalAlignment.Top, StandardCursorType.TopLeftCorner);
        AddDialogResizeHandle(layer, dialog, "NorthEast", 12, 12, HorizontalAlignment.Right, VerticalAlignment.Top, StandardCursorType.TopRightCorner);
        AddDialogResizeHandle(layer, dialog, "SouthWest", 12, 12, HorizontalAlignment.Left, VerticalAlignment.Bottom, StandardCursorType.BottomLeftCorner);
        AddDialogResizeHandle(layer, dialog, "SouthEast", 12, 12, HorizontalAlignment.Right, VerticalAlignment.Bottom, StandardCursorType.BottomRightCorner);
        return layer;
    }

    private static void AddDialogResizeHandle(
        Grid layer,
        Window dialog,
        string edge,
        double width,
        double height,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        StandardCursorType cursor)
    {
        var handle = new AvaloniaRectangle
        {
            Width = width,
            Height = height,
            Fill = Brushes.Transparent,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Cursor = new Cursor(cursor),
            Tag = edge
        };
        handle.PointerPressed += (_, e) =>
        {
            if (dialog.WindowState != WindowState.Normal
                || !dialog.CanResize
                || !e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed
                || !Enum.TryParse<WindowEdge>(edge, out var windowEdge))
                return;

            dialog.BeginResizeDrag(windowEdge, e);
            e.Handled = true;
        };
        layer.Children.Add(handle);
    }
}
