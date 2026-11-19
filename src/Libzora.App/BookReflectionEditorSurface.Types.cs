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
/// Nested view types, block state and inline run model.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private sealed class ReflectionRichTextBlock : TextBlock
    {
        public void SetRuns(IReadOnlyList<InlineRun> runs, bool quote)
        {
            Inlines = new InlineCollection();

            foreach (var run in runs)
            {
                var textRun = new Run(run.Text);
                if (run.Style == InlineStyle.Bold)
                    textRun.FontWeight = FontWeight.Bold;
                else if (run.Style == InlineStyle.Link)
                {
                    textRun.Foreground = new SolidColorBrush(Color.Parse("#275F84"));
                    textRun.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                }
                Inlines?.Add(textRun);
            }
        }

        public void SetRuns(IReadOnlyList<InlineRun> runs)
        {
            SetRuns(runs, quote: false);
        }
    }

    private sealed class BlockView
    {
        public BlockView(
            BlockState state,
            Grid row,
            Border marker,
            Grid contentHost,
            ReflectionRichTextBlock? richText,
            TextBox? editor,
            TextBlock? quoteOpening,
            TextBlock? quoteClosing,
            TextBlock? citationSource,
            Border? imageHost,
            Bitmap? imageBitmap,
            Image? imageControl,
            Grid? imageFrame,
            TextBlock? imagePlaceholder,
            Border? imageSelectionBorder,
            StackPanel? imageActionBar,
            AvaloniaRectangle? resizeHandle)
        {
            State = state;
            Row = row;
            Marker = marker;
            ContentHost = contentHost;
            RichText = richText;
            Editor = editor;
            QuoteOpening = quoteOpening;
            QuoteClosing = quoteClosing;
            CitationSource = citationSource;
            ImageHost = imageHost;
            ImageBitmap = imageBitmap;
            ImageControl = imageControl;
            ImageFrame = imageFrame;
            ImagePlaceholder = imagePlaceholder;
            ImageSelectionBorder = imageSelectionBorder;
            ImageActionBar = imageActionBar;
            ResizeHandle = resizeHandle;
            LastPlainText = state.PlainText;
        }

        public BlockState State { get; }
        public Grid Row { get; }
        public Border Marker { get; }
        public Grid ContentHost { get; }
        public ReflectionRichTextBlock? RichText { get; }
        public TextBox? Editor { get; }
        public TextBlock? QuoteOpening { get; }
        public TextBlock? QuoteClosing { get; }
        public TextBlock? CitationSource { get; }
        public Border? ImageHost { get; }
        public Bitmap? ImageBitmap { get; set; }
        public Image? ImageControl { get; }
        public Grid? ImageFrame { get; }
        public TextBlock? ImagePlaceholder { get; }
        public Border? ImageSelectionBorder { get; }
        public StackPanel? ImageActionBar { get; }
        public AvaloniaRectangle? ResizeHandle { get; }
        public bool IsResizing { get; set; }
        public string LastPlainText { get; set; }

        public void Dispose()
        {
            ImageBitmap?.Dispose();
            ImageBitmap = null;
        }
    }

    private sealed class ImageResizeSession
    {
        public ImageResizeSession(
            BlockView view,
            Point startPoint,
            double startWidth,
            double startHeight)
        {
            View = view;
            StartPoint = startPoint;
            StartWidth = startWidth;
            StartHeight = startHeight;
        }

        public BlockView View { get; }
        public Point StartPoint { get; }
        public double StartWidth { get; }
        public double StartHeight { get; }
        public bool Changed { get; set; }
    }

    private sealed record ImageActionRequest(BlockView Block, ImageActionKind Action);

    private enum ImageActionKind
    {
        Replace,
        EditAlt,
        ZoomOut,
        ZoomIn,
        Delete
    }

    private sealed class SelectionSnapshot
    {
        public SelectionSnapshot(BlockView block, int start, int end)
        {
            Block = block;
            Start = start;
            End = end;
        }

        public BlockView Block { get; }
        public int Start { get; }
        public int End { get; }
    }

    private sealed class BlockState
    {
        public BlockKind Kind { get; set; }
        public List<InlineRun> Inlines { get; set; } = [];
        public int OrderedNumber { get; set; } = 1;
        public bool IsCitation { get; set; }
        public string? CitationBookTitle { get; set; }
        public string ImageAlt { get; set; } = string.Empty;
        public string ImageSource { get; set; } = string.Empty;
        public int? ImageWidth { get; set; }
        public int? ImageHeight { get; set; }

        public string PlainText => string.Concat(Inlines.Select(run => run.Text));

        public string ToMarkdown()
        {
            if (Kind == BlockKind.Image)
            {
                var dimensions = ImageWidth is > 0
                    ? $" ={ImageWidth}{(ImageHeight is > 0 ? $"x{ImageHeight}" : string.Empty)}"
                    : string.Empty;
                return $"![{ImageAlt}]({ImageSource}{dimensions})";
            }

            var inline = ToInlineMarkdown();
            var markdown = Kind switch
            {
                BlockKind.Heading1 => "# " + inline,
                BlockKind.Heading2 => "## " + inline,
                BlockKind.Heading3 => "### " + inline,
                BlockKind.Heading4 => "#### " + inline,
                BlockKind.Heading5 => "##### " + inline,
                BlockKind.Heading6 => "###### " + inline,
                BlockKind.UnorderedList => "- " + inline,
                BlockKind.OrderedList => $"{Math.Max(1, OrderedNumber)}. " + inline,
                BlockKind.Quote => "> " + inline.ReplaceLineEndings("\n> "),
                _ => inline
            };

            if (Kind == BlockKind.Quote
                && !string.IsNullOrWhiteSpace(CitationBookTitle))
            {
                markdown += "\n>\n> " + FormatCitationSource(CitationBookTitle);
            }

            return markdown;
        }

        private string ToInlineMarkdown()
        {
            var builder = new StringBuilder();
            foreach (var run in Inlines)
            {
                if (run.Style == InlineStyle.Bold)
                    builder.Append("**").Append(run.Text).Append("**");
                else if (run.Style == InlineStyle.Link)
                    builder.Append('[').Append(run.Text).Append("](").Append(run.LinkUrl).Append(')');
                else
                    builder.Append(run.Text);
            }
            return builder.ToString();
        }
    }

    private sealed class InlineRun
    {
        public InlineRun(string text, InlineStyle style = InlineStyle.Plain, string? linkUrl = null)
        {
            Text = text;
            Style = style;
            LinkUrl = linkUrl;
        }

        public string Text { get; set; }
        public InlineStyle Style { get; }
        public string? LinkUrl { get; }
    }

    private enum InlineStyle
    {
        Plain,
        Bold,
        Link
    }

    private enum BlockKind
    {
        Paragraph,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Heading5,
        Heading6,
        Quote,
        UnorderedList,
        OrderedList,
        Image
    }
}
