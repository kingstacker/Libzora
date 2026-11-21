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
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Libzora.Core;

namespace Libzora;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

/// <summary>
/// Block view construction and quote adornments.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void AddBlock(BlockState state, int index)
    {
        var view = CreateBlockView(state);
        _blocks.Insert(index, view);
        _blockStack.Children.Insert(index, view.Row);
    }

    private BlockView CreateBlockView(BlockState state)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var marker = new Border
        {
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(2)
        };
        var contentHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(contentHost, 1);
        row.Children.Add(marker);
        row.Children.Add(contentHost);

        ReflectionRichTextBlock? richText = null;
        TextBox? editor = null;
        TextBlock? quoteOpening = null;
        TextBlock? quoteClosing = null;
        TextBlock? citationSource = null;
        Border? imageHost = null;
        Bitmap? imageBitmap = null;
        Image? imageControl = null;
        Grid? imageFrame = null;
        TextBlock? imagePlaceholder = null;
        Border? imageSelectionBorder = null;
        StackPanel? imageActionBar = null;
        List<Button>? imageActionButtons = null;
        AvaloniaRectangle? resizeHandle = null;

        if (state.Kind == BlockKind.Image)
        {
            imageHost = new Border
            {
                Padding = new Thickness(0, 10, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            imageFrame = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Focusable = true
            };
            AutomationProperties.SetName(imageFrame, UiText.Get("图片"));
            imageBitmap = LoadBitmap(state.ImageSource);
            imageControl = new Image
            {
                Source = imageBitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = DefaultImageMaxWidth,
                MaxHeight = DefaultImageMaxHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsVisible = imageBitmap is not null
            };
            ToolTip.SetTip(imageControl, state.ImageAlt);
            imageFrame.Children.Add(imageControl);

            imagePlaceholder = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(state.ImageAlt)
                    ? UiText.Get("图片无法显示")
                    : state.ImageAlt,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                TextWrapping = TextWrapping.Wrap,
                IsVisible = imageBitmap is null
            };
            imageFrame.Children.Add(imagePlaceholder);

            imageSelectionBorder = new Border
            {
                BorderBrush = AppAppearanceResources.GetBrush("InkBrush"),
                BorderThickness = new Thickness(1),
                IsVisible = false,
                IsHitTestVisible = false
            };
            imageFrame.Children.Add(imageSelectionBorder);

            if (imageBitmap is not null)
            {
                resizeHandle = new AvaloniaRectangle
                {
                    Width = 10,
                    Height = 10,
                    Fill = AppAppearanceResources.GetBrush("InkBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 4, 4),
                    Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                    IsVisible = false
                };
                imageFrame.Children.Add(resizeHandle);
            }

            imageActionBar = CreateImageActionBar(out imageActionButtons);
            imageFrame.Children.Add(imageActionBar);
            imageHost.Child = imageFrame;
            Grid.SetColumn(imageHost, 1);
            contentHost.Children.Add(imageHost);
        }
        else
        {
            quoteOpening = CreateQuoteAdornment("“", HorizontalAlignment.Left);
            quoteClosing = CreateQuoteAdornment("”", HorizontalAlignment.Right);
            Grid.SetColumn(quoteOpening, 0);
            Grid.SetColumn(quoteClosing, 2);
            contentHost.Children.Add(quoteOpening);

            richText = new ReflectionRichTextBlock
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 5)
            };
            BindDefaultUiFont(richText);
            Grid.SetColumn(richText, 1);

            editor = new TextBox
            {
                Tag = null,
                Text = state.PlainText,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 5, 0, 5),
                MinHeight = 30,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Foreground = Brushes.Transparent,
                CaretBrush = AppAppearanceResources.GetBrush("InkBrush"),
                SelectionBrush = new SolidColorBrush(Color.FromArgb(68, 17, 17, 17)),
                SelectionForegroundBrush = Brushes.Transparent,
                ClearSelectionOnLostFocus = true
            };
            BindDefaultUiFont(editor);
            Grid.SetColumn(editor, 1);
            editor.Classes.Add("bookReflectionEditor");

            contentHost.Children.Add(richText);
            contentHost.Children.Add(editor);
            contentHost.Children.Add(quoteClosing);

            citationSource = new TextBlock
            {
                FontSize = 11,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(QuoteTextHorizontalSpacing, 6, QuoteTextHorizontalSpacing, 10),
                IsVisible = false,
                IsHitTestVisible = false
            };
            BindDefaultUiFont(citationSource);
            Grid.SetColumn(citationSource, 1);
            Grid.SetRow(citationSource, 1);
            contentHost.Children.Add(citationSource);
        }

        var view = new BlockView(
            state,
            row,
            marker,
            contentHost,
            richText,
            editor,
            quoteOpening,
            quoteClosing,
            citationSource,
            imageHost,
            imageBitmap,
            imageControl,
            imageFrame,
            imagePlaceholder,
            imageSelectionBorder,
            imageActionBar,
            resizeHandle);
        if (editor is not null)
        {
            editor.Tag = view;
            editor.GotFocus += (_, _) =>
            {
                if (_activeBlock?.Editor is { } previousEditor
                    && previousEditor != editor)
                {
                    previousEditor.SelectionStart = previousEditor.CaretIndex;
                    previousEditor.SelectionEnd = previousEditor.CaretIndex;
                }
                _activeBlock = view;
                ClearImageSelection();
                UpdateSelectionToolbar(view);
            };
            editor.LostFocus += (_, _) =>
            {
                if (_selection?.Block == view)
                    _selection = null;
            };
            editor.TextChanged += BlockEditor_TextChanged;
            editor.PropertyChanged += BlockEditor_PropertyChanged;
            editor.AddHandler(
                InputElement.KeyDownEvent,
                BlockEditor_KeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            editor.ContextFlyout = CreateReflectionEditorContextFlyout(view);
        }
        if (imageFrame is not null)
        {
            imageFrame.Tag = view;
            imageFrame.PointerPressed += ImageFrame_PointerPressed;
            imageFrame.PointerEntered += (_, _) => SetImageControls(view, true);
            imageFrame.PointerExited += (_, _) =>
            {
                if (_selectedImage != view && !view.IsResizing)
                    SetImageControls(view, false);
            };
            imageFrame.GotFocus += (_, _) => SelectImage(view);
            imageFrame.AddHandler(InputElement.KeyDownEvent, ImageFrame_KeyDown, RoutingStrategies.Bubble);
        }
        if (resizeHandle is not null)
        {
            resizeHandle.Tag = view;
            resizeHandle.PointerPressed += ImageResizeHandle_PointerPressed;
            resizeHandle.PointerMoved += ImageResizeHandle_PointerMoved;
            resizeHandle.PointerReleased += ImageResizeHandle_PointerReleased;
        }
        if (imageActionButtons is not null)
        {
            foreach (var (button, action) in imageActionButtons.Select((button, index) =>
                         (button, (ImageActionKind)index)))
            {
                button.Tag = new ImageActionRequest(view, action);
            }
        }

        UpdateBlockView(view);
        return view;
    }

    private static TextBlock CreateQuoteAdornment(string text, HorizontalAlignment alignment)
    {
        var adornment = new TextBlock
        {
            Text = text,
            MinWidth = QuoteAdornmentMinWidth,
            FontSize = 31,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppAppearanceResources.GetBrush("AccentBrush"),
            TextAlignment = alignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
            HorizontalAlignment = alignment,
            VerticalAlignment = text == "“" ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = text == "“" ? new Thickness(0, 0, 0, 0) : new Thickness(0, 0, 3, 2),
            ClipToBounds = false,
            IsVisible = false,
            IsHitTestVisible = false,
            ZIndex = 1
        };
        BindDefaultUiFont(adornment);
        return adornment;
    }

    private static void BindDefaultUiFont(Control control) =>
        control.Bind(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, new DynamicResourceExtension("DefaultAppFontFamily"));
}
