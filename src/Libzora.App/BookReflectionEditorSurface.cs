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

internal sealed record BookReflectionCitation(
    Guid Id,
    string BookTitle,
    string ChapterLabel,
    string SelectedText,
    string Note,
    DateTimeOffset UpdatedAt)
{
    public string DisplayText => string.IsNullOrWhiteSpace(SelectedText)
        ? string.IsNullOrWhiteSpace(Note) ? UiText.Get("未命名批注") : Note
        : SelectedText;

    public string SearchText => string.Join('\n', BookTitle, ChapterLabel, SelectedText, Note);
}

/// <summary>
/// State, markdown projection and loading for the reflection editor.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private const double DefaultImageMaxWidth = 900;
    private const double DefaultImageMaxHeight = 620;
    private const double MinimumImageWidth = 48;
    private const double ImageZoomFactor = 1.2;
    private const double QuoteAdornmentMinWidth = 28;
    private const double QuoteTextHorizontalSpacing = 11;

    // Toolbar geometry. Buttons and icons share one square content box so the
    // glyphs line up on the same optical centre.
    private const double ToolbarButtonWidth = 34;
    private const double ToolbarButtonHeight = 30;
    private const double ToolbarIconSize = 16;
    private const double ToolbarIconCanvas = 24;
    private const double ToolbarIconStroke = 1;

    private static readonly Regex HeadingPattern = new(
        @"^\s*(?<marks>#{1,6})(?:\s+(?<text>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex UnorderedPattern = new(
        @"^\s*[-*+](?:\s+(?<text>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex OrderedPattern = new(
        @"^\s*(?<number>\d+)[.)](?:\s+(?<text>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(
        @"^\s*!\[(?<alt>[^\]]*)\]\((?<source>[^)\s\r\n]+)(?:\s*=\s*(?<width>\d+)(?:x(?<height>\d+))?)?\)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex MarkdownImageTokenPattern = new(
        @"!\[[^\]\r\n]*\]\([^\)\r\n]*\)",
        RegexOptions.Compiled);
    private static readonly Regex InlinePattern = new(
        @"(\*\*(?<bold>[^*\r\n]+)\*\*|\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\))",
        RegexOptions.Compiled);
    private static readonly Regex CitationSourcePattern = new(
        @"^\s*--(?<title>《.+》)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex LegacyCitationPattern = new(
        @"^\s*《(?<title>.+)》：(?<text>.*)$",
        RegexOptions.Compiled);

    private readonly StackPanel _blockStack;
    private readonly IReadOnlyList<BookReflectionCitation> _citations;
    private readonly string _currentBookTitle;
    public Control FormattingToolbar { get; }
    private readonly List<BlockView> _blocks = [];
    private BlockView? _activeBlock;
    private BlockView? _selectedImage;
    private ImageResizeSession? _imageResizeSession;
    private SelectionSnapshot? _selection;
    private bool _suppressTextChanged;
    private bool _disposed;

    public event EventHandler? ContentChanged;

    public string Markdown => string.Join("\n", _blocks.Select(block => block.State.ToMarkdown()));

    public int CharacterCount => _blocks
        .Where(block => block.State.Kind != BlockKind.Image)
        .Sum(block => block.State.PlainText.Length);

    public BookReflectionEditorSurface(
        string initialContent,
        IReadOnlyList<BookReflectionCitation>? citations = null,
        string? currentBookTitle = null)
    {
        _citations = citations ?? [];
        _currentBookTitle = currentBookTitle?.Trim() ?? string.Empty;
        Background = AppAppearanceResources.GetBrush("PaperBrush");
        BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush");
        BorderThickness = new Thickness(1);
        ClipToBounds = true;

        _blockStack = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var scrollViewer = new ScrollViewer
        {
            Background = Brushes.Transparent,
            Content = _blockStack,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        ScrollViewer.SetVerticalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Disabled);

        FormattingToolbar = CreateFormattingToolbar();
        Child = scrollViewer;

        SizeChanged += (_, _) => UpdateImageConstraints();

        Load(initialContent ?? string.Empty);
    }

    public void FocusEditor()
    {
        var block = _activeBlock ?? _blocks.LastOrDefault(view => view.Editor is not null);
        FocusBlock(block, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _imageResizeSession = null;
        _selectedImage = null;
        foreach (var block in _blocks)
            block.Dispose();
    }

    private void Load(string markdown)
    {
        var normalized = NormalizeMarkdown(markdown)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');

        for (var index = 0; index < lines.Length;)
        {
            if (IsMarkdownQuoteLine(lines[index]))
            {
                var quoteLines = new List<string>();
                while (index < lines.Length && IsMarkdownQuoteLine(lines[index]))
                {
                    quoteLines.Add(lines[index]);
                    index++;
                }

                AddBlock(ParseQuoteBlock(quoteLines), _blocks.Count);
                continue;
            }

            AddBlock(ParseBlock(lines[index]), _blocks.Count);
            index++;
        }

        if (_blocks.Count == 0)
            AddBlock(new BlockState(), 0);
    }
}
