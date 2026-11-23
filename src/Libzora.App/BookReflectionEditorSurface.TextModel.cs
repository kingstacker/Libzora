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
/// Inline styles, markdown parsing and preview helpers.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void RestoreSelection(SelectionSnapshot? selection)
    {
        if (selection?.Block.Editor is not { } editor)
            return;

        _activeBlock = selection.Block;
        editor.Focus();
        editor.SelectionStart = Math.Clamp(selection.Start, 0, editor.Text?.Length ?? 0);
        editor.SelectionEnd = Math.Clamp(selection.End, 0, editor.Text?.Length ?? 0);
        UpdateSelectionToolbar(selection.Block);
    }

    private void FocusBlock(BlockView? view, int? caret)
    {
        if (view?.Editor is not { } editor)
            return;

        _activeBlock = view;
        editor.Focus();
        var position = Math.Clamp(
            caret ?? editor.Text?.Length ?? 0,
            0,
            editor.Text?.Length ?? 0);
        editor.CaretIndex = position;
        editor.BringIntoView();
    }

    private void RefreshTextBlock(BlockView view)
    {
        if (view.Editor is not { } editor) return;
        SetEditorText(view, view.State.PlainText, editor.CaretIndex);
        UpdateBlockView(view);
    }

    private static List<InlineRun> ApplyInlineStyle(
        IReadOnlyList<InlineRun> runs,
        int start,
        int end,
        InlineStyle style,
        string? linkUrl = null)
    {
        var result = SliceRuns(runs, 0, start);
        result.AddRange(SliceRuns(runs, start, end)
            .Select(run => new InlineRun(run.Text, style, linkUrl)));
        result.AddRange(SliceRuns(runs, end, runs.Sum(run => run.Text.Length)));
        return MergeRuns(result);
    }

    private static InlineRun GetStyleAt(IReadOnlyList<InlineRun> runs, int index)
    {
        var position = 0;
        InlineRun? previous = null;
        foreach (var run in runs)
        {
            if (index < position + run.Text.Length)
                return run;
            position += run.Text.Length;
            previous = run;
        }
        return previous ?? new InlineRun(string.Empty);
    }

    private static List<InlineRun> SliceRuns(
        IReadOnlyList<InlineRun> runs,
        int start,
        int end)
    {
        var result = new List<InlineRun>();
        if (end <= start) return result;

        var position = 0;
        foreach (var run in runs)
        {
            var runEnd = position + run.Text.Length;
            var localStart = Math.Max(0, start - position);
            var localEnd = Math.Min(run.Text.Length, end - position);
            if (localEnd > localStart)
            {
                result.Add(new InlineRun(
                    run.Text[localStart..localEnd],
                    run.Style,
                    run.LinkUrl));
            }
            position = runEnd;
            if (position >= end) break;
        }
        return result;
    }

    private static List<InlineRun> MergeRuns(IEnumerable<InlineRun> runs)
    {
        var result = new List<InlineRun>();
        foreach (var run in runs.Where(run => run.Text.Length > 0))
        {
            if (result.LastOrDefault() is { } previous
                && previous.Style == run.Style
                && string.Equals(previous.LinkUrl, run.LinkUrl, StringComparison.Ordinal))
            {
                previous.Text += run.Text;
            }
            else
            {
                result.Add(new InlineRun(run.Text, run.Style, run.LinkUrl));
            }
        }
        return result;
    }

    private static BlockState ParseBlock(string source)
    {
        var image = ImagePattern.Match(source);
        if (image.Success && IsSupportedImageSource(image.Groups["source"].Value))
        {
            return new BlockState
            {
                Kind = BlockKind.Image,
                ImageAlt = image.Groups["alt"].Value,
                ImageSource = image.Groups["source"].Value,
                ImageWidth = ParsePositiveInt(image.Groups["width"].Value),
                ImageHeight = ParsePositiveInt(image.Groups["height"].Value)
            };
        }

        var heading = HeadingPattern.Match(source);
        if (heading.Success)
        {
            return new BlockState
            {
                Kind = (BlockKind)heading.Groups["marks"].Value.Length,
                Inlines = ParseInline(heading.Groups["text"].Value)
            };
        }

        var quote = Regex.Match(source, @"^\s*>\s?(?<text>.*)$");
        if (quote.Success)
            return ParseQuoteBlock([source]);

        var unordered = UnorderedPattern.Match(source);
        if (unordered.Success)
        {
            return new BlockState
            {
                Kind = BlockKind.UnorderedList,
                Inlines = ParseInline(unordered.Groups["text"].Value)
            };
        }

        var ordered = OrderedPattern.Match(source);
        if (ordered.Success)
        {
            _ = int.TryParse(ordered.Groups["number"].Value, out var number);
            return new BlockState
            {
                Kind = BlockKind.OrderedList,
                OrderedNumber = Math.Max(1, number),
                Inlines = ParseInline(ordered.Groups["text"].Value)
            };
        }

        return new BlockState { Inlines = ParseInline(source) };
    }

    private static BlockState ParseQuoteBlock(IReadOnlyList<string> sources)
    {
        var quoteLines = sources
            .Select(source => Regex.Match(source, @"^\s*>\s?(?<text>.*)$").Groups["text"].Value)
            .ToList();
        string? citationBookTitle = null;
        var isCitation = false;

        if (quoteLines.Count >= 2
            && string.IsNullOrWhiteSpace(quoteLines[^2])
            && TryParseCitationSource(quoteLines[^1], out var sourceTitle))
        {
            citationBookTitle = sourceTitle;
            isCitation = true;
            quoteLines.RemoveRange(quoteLines.Count - 2, 2);
        }

        if (quoteLines.Count == 1)
        {
            var legacy = LegacyCitationPattern.Match(quoteLines[0]);
            if (legacy.Success)
            {
                citationBookTitle = legacy.Groups["title"].Value;
                isCitation = true;
                quoteLines[0] = legacy.Groups["text"].Value;
            }
        }

        return new BlockState
        {
            Kind = BlockKind.Quote,
            Inlines = ParseInline(string.Join('\n', quoteLines)),
            IsCitation = isCitation,
            CitationBookTitle = citationBookTitle
        };
    }

    private static bool IsMarkdownQuoteLine(string source) =>
        source.TrimStart().StartsWith(">", StringComparison.Ordinal);

    private static bool TryParseCitationSource(string source, out string title)
    {
        var match = CitationSourcePattern.Match(source);
        title = match.Success
            ? match.Groups["title"].Value.Trim()
            : string.Empty;
        return title.Length > 0;
    }

    private static List<InlineRun> ParseInline(string text)
    {
        var result = new List<InlineRun>();
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
                result.Add(new InlineRun(text[position..match.Index]));

            if (match.Groups["bold"].Success)
            {
                result.Add(new InlineRun(
                    match.Groups["bold"].Value,
                    InlineStyle.Bold));
            }
            else
            {
                result.Add(new InlineRun(
                    match.Groups["label"].Value,
                    InlineStyle.Link,
                    match.Groups["url"].Value));
            }
            position = match.Index + match.Length;
        }

        if (position < text.Length)
            result.Add(new InlineRun(text[position..]));

        return MergeRuns(result);
    }

    private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsHeading(BlockKind kind) => kind is
        BlockKind.Heading1 or
        BlockKind.Heading2 or
        BlockKind.Heading3 or
        BlockKind.Heading4 or
        BlockKind.Heading5 or
        BlockKind.Heading6;

    private static int NextOrderedNumber(int number) => number >= int.MaxValue
        ? int.MaxValue
        : Math.Max(1, number + 1);

    private static int? ParsePositiveInt(string value) =>
        int.TryParse(value, out var number) && number > 0
            ? number
            : null;

    private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png"
    };

    private static bool IsSupportedImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private static bool IsSupportedImageSource(string source) =>
        source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.IsFile
            && File.Exists(uri.LocalPath));

    private static Bitmap? LoadBitmap(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        Bitmap? bitmap = null;
        try
        {
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var payload = source[(comma + 1)..];
                var bytes = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                using var stream = new MemoryStream(bytes, writable: false);
                bitmap = new Bitmap(stream);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && uri.IsFile
                && File.Exists(uri.LocalPath))
            {
                bitmap = new Bitmap(uri.LocalPath);
            }
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    internal static string NormalizeMarkdown(string markdown) =>
        BookReflectionMarkdown.NormalizeMarkdown(markdown);

    internal static string RemoveImagesForPreview(string markdown) =>
        BookReflectionMarkdown.RemoveImagesForPreview(markdown);
}
