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
/// Formatting toolbar and image action buttons.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private Control CreateFormattingToolbar()
    {
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5V19M11 5V19M4 12H11M15.5 8L18 5.5V19"),
            "一级标题",
            "heading1");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5V19M11 5V19M4 12H11M14.5 8.5A3 3 0 0 1 20.5 8.5C20.5 11.5 14.5 13 14.5 19H20.5"),
            "二级标题",
            "heading2");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5V19M11 5V19M4 12H11M14.5 8.5C17 5.5 21 6.5 21 9.5C21 11.7 18.5 12 16.5 12C18.5 12 21 12.3 21 14.5C21 17.5 17 18.5 14.5 15.5"),
            "三级标题",
            "heading3");
        AddToolbarSeparator(tools);
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M7 4.5V19.5M7 4.5H13.5C16.5 4.5 18.5 6 18.5 8.2C18.5 10.3 17 11.5 14.5 11.8H7M7 11.8H14.5C17.5 11.8 19.5 13.2 19.5 15.6C19.5 18.1 17.3 19.5 14 19.5H7"),
            "粗体",
            "bold");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M3 11V8C3 6.3 3.8 5 5.5 4M3 8H6V11H3ZM8 11V8C8 6.3 8.8 5 10.5 4M8 8H11V11H8ZM13 12H16V15H13ZM16 15C16 16.7 15.2 18 13.5 19M18 12H21V15H18ZM21 15C21 16.7 20.2 18 18.5 19"),
            "引用",
            "quote");
        if (_citations.Count > 0)
        {
            AddToolbarAction(
                tools,
                CreateToolbarIcon("M5 5H19V15H12L8 19V15H5ZM9 9H15M9 12H17M19 16V22M16 19H22"),
                "引用批注",
                "citation");
        }
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5H7V8H4ZM4 11H7V14H4ZM4 17H7V20H4ZM10 6H20M10 12H20M10 18H20"),
            "无序列表",
            "unordered");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M5.5 5.5L7 4.5V7.5H5.5M5.5 11.5L7 10.5V13.5H5.5M5.5 17.5L7 16.5V19.5H5.5M10 6H20M10 12H20M10 18H20"),
            "有序列表",
            "ordered");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M10 13a5 5 0 0 0 7.54.54l1.09-1.09a5 5 0 0 0-7.07-7.07l-.63.63M14 11a5 5 0 0 0-7.54-.54l-1.09 1.09a5 5 0 0 0 7.07 7.07l.63-.63"),
            "链接",
            "link");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5h16v14H4zM4 15l4-4 3 3 2-2 5 5M15 9h.01"),
            "插入图片",
            "image");

        return new Border
        {
            Padding = new Thickness(5, 4),
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = tools
        };

    }

    private void AddToolbarAction(StackPanel tools, string content, string tooltip, string tag) =>
        AddToolbarAction(tools, (object)content, tooltip, tag);

    private void AddToolbarAction(StackPanel tools, Control content, string tooltip, string tag) =>
        AddToolbarAction(tools, (object)content, tooltip, tag);

    private void AddToolbarAction(
        StackPanel tools,
        object content,
        string tooltip,
        string tag)
    {
        // Background is deliberately left unset so the shared style owns the
        // normal, pointerover and pressed states.
        var button = new Button
        {
            Content = NormalizeToolbarContent(content, tag),
            Tag = tag,
            Width = ToolbarButtonWidth,
            MinWidth = ToolbarButtonWidth,
            MaxWidth = ToolbarButtonWidth,
            Height = ToolbarButtonHeight,
            Padding = new Thickness(0),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("bookReflectionFormattingAction");
        ToolTip.SetTip(button, UiText.Get(tooltip));
        AutomationProperties.SetName(button, UiText.Get(tooltip));
        button.Click += ToolbarButton_Click;
        tools.Children.Add(button);
    }

    private static void AddToolbarSeparator(StackPanel tools) =>
        tools.Children.Add(new AvaloniaRectangle
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = AppAppearanceResources.GetBrush("HairlineBrush")
        });

    // Icons are authored on a 24x24 grid with a deliberately light one-pixel
    // stroke. Rendering them through a fixed-size Viewbox keeps the whole
    // toolbar visually quiet and prevents larger paths from being clipped.
    private static Control CreateToolbarIcon(string geometry)
    {
        var canvas = new Canvas
        {
            Width = ToolbarIconCanvas,
            Height = ToolbarIconCanvas,
            IsHitTestVisible = false
        };
        canvas.Children.Add(new AvaloniaPath
        {
            Data = Geometry.Parse(geometry),
            Stroke = AppAppearanceResources.GetBrush("InkBrush"),
            StrokeThickness = ToolbarIconStroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        });

        return new Viewbox
        {
            Width = ToolbarIconSize,
            Height = ToolbarIconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = canvas
        };
    }

    private static object NormalizeToolbarContent(object content, string tag)
    {
        if (content is Control control)
        {
            control.HorizontalAlignment = HorizontalAlignment.Center;
            control.VerticalAlignment = VerticalAlignment.Center;
        }

        return content;
    }

    private StackPanel CreateImageActionBar(out List<Button> buttons)
    {
        buttons =
        [
            CreateImageActionButton("替换", "替换图片", 44),
            CreateImageActionButton("描述", "编辑图片描述", 44),
            CreateImageActionButton("−", "缩小图片", 30),
            CreateImageActionButton("＋", "放大图片", 30),
            CreateImageActionButton("删除", "删除图片", 44)
        ];
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Background = AppAppearanceResources.GetBrush("ReaderPageBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            IsVisible = false,
            ZIndex = 20,
            Children = { buttons[0], buttons[1], buttons[2], buttons[3], buttons[4] }
        };
    }

    private Button CreateImageActionButton(
        string content,
        string tooltip,
        double width)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            MinWidth = width,
            Height = 26,
            Padding = new Thickness(4, 2),
            FontSize = 11,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("readerSelectionAction");
        ToolTip.SetTip(button, UiText.Get(tooltip));
        AutomationProperties.SetName(button, UiText.Get(tooltip));
        button.Click += ImageActionButton_Click;
        return button;
    }
}
